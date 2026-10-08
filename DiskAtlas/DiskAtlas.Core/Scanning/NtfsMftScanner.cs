using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskAtlas.Core.Scanning;

internal static class NtfsMftScanner
{
    private const uint AttributeStandardInformation = 0x10;
    private const uint AttributeAttributeList = 0x20;
    private const uint AttributeFileName = 0x30;
    private const uint AttributeData = 0x80;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint ShareDelete = 4;
    private const uint OpenExisting = 3;

    internal static ScanResult Scan(string rootPath, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var drive = Path.GetPathRoot(rootPath) ?? throw new RawScanUnavailableException("The drive root could not be determined.");
        var device = $@"\\.\{drive.TrimEnd('\\')}";
        using var handle = CreateFileW(device, GenericRead, ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new RawScanUnavailableException($"Raw volume access was denied ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");

        if (!DeviceIoControl(handle, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, out var volumeData,
                Marshal.SizeOf<NtfsVolumeDataWithExtended>(), out var volumeBytes, IntPtr.Zero))
            throw new RawScanUnavailableException($"NTFS volume metadata could not be read ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");

        var volume = volumeData.Basic;
        if (volumeBytes < Marshal.SizeOf<NtfsVolumeData>() + 8 || volumeData.Extended.ByteCount < 8 ||
            volumeData.Extended.MajorVersion != 3 || volumeData.Extended.MinorVersion > 1)
            throw new RawScanUnavailableException("Only NTFS volume format versions 3.0 and 3.1 are supported by the raw parser.");

        ValidateVolume(volume);
        var firstRecord = new byte[volume.BytesPerFileRecordSegment];
        ReadExactly(handle, firstRecord, checked(volume.MftStartLcn * volume.BytesPerCluster));
        var mftHeader = NtfsRecordParser.ParseRecord(firstRecord, checked((int)volume.BytesPerSector));
        if (!mftHeader.InUse || mftHeader.BaseRecordNumber != 0)
            throw new RawScanUnavailableException("The primary MFT record is not a valid base record.");
        if (mftHeader.Attributes.Any(a => a.Type == AttributeAttributeList))
            throw new RawScanUnavailableException("The MFT itself uses ATTRIBUTE_LIST extents that cannot be proven complete.");

        var mftDataAttributes = mftHeader.Attributes.Where(a =>
            a.Type == AttributeData && a.Name.Length == 0 && a.IsNonResident && a.LowestVcn == 0).ToArray();
        if (mftDataAttributes.Length != 1 || mftDataAttributes[0].Runs is null)
            throw new RawScanUnavailableException("The MFT data runlist is missing or ambiguous.");
        var mftData = mftDataAttributes[0];
        var mftRuns = mftData.Runs!;
        ValidateMftRuns(mftRuns, volume.MftValidDataLength, volume.BytesPerCluster);

        if (volume.MftValidDataLength % volume.BytesPerFileRecordSegment != 0)
            throw new RawScanUnavailableException("The MFT valid length ends in a partial record.");
        var recordCount64 = volume.MftValidDataLength / volume.BytesPerFileRecordSegment;
        if (recordCount64 <= 5 || recordCount64 > int.MaxValue)
            throw new RawScanUnavailableException("The MFT record count is outside supported bounds.");
        var entries = new MftEntry?[checked((int)recordCount64)];
        var lastProgress = Stopwatch.StartNew();
        using var reader = new RunReader(handle, mftRuns, volume.BytesPerCluster,
            checked(recordCount64 * volume.BytesPerFileRecordSegment));
        var recordBuffer = new byte[volume.BytesPerFileRecordSegment];
        long discoveredFiles = 0, discoveredDirectories = 0;

        for (var index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reader.ReadExactly(recordBuffer);
            if (!recordBuffer.AsSpan(0, 4).SequenceEqual("FILE"u8))
            {
                if (recordBuffer.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                    throw new RawScanUnavailableException(
                        $"MFT record {index} has an invalid signature; raw results would be incomplete.");
                continue;
            }
            ParsedNtfsRecord parsed;
            try { parsed = NtfsRecordParser.ParseRecord(recordBuffer, checked((int)volume.BytesPerSector)); }
            catch (InvalidDataException ex)
            {
                throw new RawScanUnavailableException(
                    $"MFT record {index} is invalid or changed during the scan; raw results were discarded.", ex);
            }
            if (!parsed.InUse)
                continue;
            if (parsed.RecordNumber != (uint)index)
                throw new RawScanUnavailableException("An MFT record number does not match its logical position.");
            entries[index] = ConvertRecord(index, parsed);
            if (parsed.BaseRecordNumber == 0)
            {
                if (parsed.IsDirectory) discoveredDirectories++;
                else discoveredFiles++;
            }

            if (progress is not null && lastProgress.ElapsedMilliseconds >= 125)
            {
                progress.Report(new ScanProgress(discoveredFiles, discoveredDirectories, 0, rootPath,
                    "NTFS MFT", stopwatch.Elapsed));
                lastProgress.Restart();
            }
        }

        ValidateAttributeLists(entries);
        var warnings = new List<string>();
        var result = BuildTree(rootPath, entries, warnings, stopwatch, cancellationToken);
        progress?.Report(new ScanProgress(result.Summary.Files, result.Summary.Directories,
            result.Root.Size, rootPath, "NTFS MFT", stopwatch.Elapsed));
        return result;
    }

    private static MftEntry ConvertRecord(int index, ParsedNtfsRecord record)
    {
        var entry = new MftEntry(index, record.SequenceNumber, record.IsDirectory,
            record.BaseRecordNumber, record.BaseRecordSequence);
        foreach (var attribute in record.Attributes)
        {
            entry.Signatures.Add(new AttributeSignature(attribute.Type, attribute.Instance,
                attribute.Name, attribute.LowestVcn));
            switch (attribute.Type)
            {
                case AttributeFileName when attribute.ResidentValue is { } fileName:
                    entry.Names.Add(new NamedFileName(attribute.Instance, ParseFileName(fileName)));
                    break;
                case AttributeStandardInformation when attribute.ResidentValue is { Length: >= 36 } standard:
                    entry.LastWriteTimeUtc = FileTime(BinaryPrimitives.ReadInt64LittleEndian(standard.AsSpan(8, 8)));
                    entry.Attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(standard.AsSpan(32, 4));
                    break;
                case AttributeData:
                    entry.Data.Add(new DataAttribute(attribute.Instance, attribute.Name, attribute.LowestVcn, attribute.IsNonResident,
                        attribute.Flags, attribute.DataSize, attribute.AllocatedSize, attribute.CompressedSize));
                    break;
                case AttributeAttributeList:
                    if (attribute.IsNonResident)
                        throw new RawScanUnavailableException("A nonresident ATTRIBUTE_LIST prevents a complete MFT result.");
                    if (entry.AttributeList is not null)
                        throw new RawScanUnavailableException("Multiple resident ATTRIBUTE_LIST attributes cannot be proven complete.");
                    entry.AttributeList = ParseAttributeList(attribute.ResidentValue ?? []);
                    break;
            }
        }
        return entry;
    }

    private static FileNameAttribute ParseFileName(byte[] value)
    {
        if (value.Length < 66)
            throw new InvalidDataException("A FILE_NAME attribute is truncated.");
        var nameLength = value[64];
        var byteLength = checked(nameLength * 2);
        if (66 > value.Length - byteLength)
            throw new InvalidDataException("A FILE_NAME attribute has invalid bounds.");
        var parent = BinaryPrimitives.ReadUInt64LittleEndian(value);
        var name = System.Text.Encoding.Unicode.GetString(value, 66, byteLength);
        var lastWrite = FileTime(BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(16, 8)));
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(56, 4));
        return new FileNameAttribute(name, value[65], parent & 0x0000ffffffffffffUL,
            (ushort)(parent >> 48), lastWrite, attributes);
    }

    private static List<AttributeListReference> ParseAttributeList(byte[] value)
    {
        var references = new List<AttributeListReference>();
        for (var offset = 0; offset < value.Length;)
        {
            if (offset > value.Length - 26)
                throw new RawScanUnavailableException("An ATTRIBUTE_LIST entry is truncated.");
            var type = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(offset, 4));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(offset + 4, 2));
            var nameLength = value[offset + 6];
            var nameOffset = value[offset + 7];
            if (length < 26 || offset > value.Length - length || nameOffset > length - nameLength * 2 ||
                (nameLength != 0 && nameOffset < 26))
                throw new RawScanUnavailableException("An ATTRIBUTE_LIST entry has invalid bounds.");
            var lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(offset + 8, 8));
            var reference = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(offset + 16, 8));
            var instance = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(offset + 24, 2));
            var name = nameLength == 0 ? "" : System.Text.Encoding.Unicode.GetString(value, offset + nameOffset, nameLength * 2);
            references.Add(new AttributeListReference(type, instance, name, lowestVcn,
                reference & 0x0000ffffffffffffUL, (ushort)(reference >> 48)));
            offset += length;
        }
        if (references.Count == 0)
            throw new RawScanUnavailableException("An ATTRIBUTE_LIST contains no entries.");
        return references;
    }

    private static void ValidateAttributeLists(MftEntry?[] entries)
    {
        var extensions = new Dictionary<(ulong Base, ushort Sequence), List<MftEntry>>();
        foreach (var candidate in entries)
        {
            if (candidate is null || candidate.BaseIndex == 0)
                continue;
            var key = (candidate.BaseIndex, candidate.BaseSequence);
            if (!extensions.TryGetValue(key, out var list))
                extensions[key] = list = [];
            list.Add(candidate);
        }

        foreach (var entry in entries)
        {
            if (entry?.AttributeList is not { } list)
                continue;
            var unique = new HashSet<AttributeListReference>();
            foreach (var reference in list)
            {
                if (!unique.Add(reference))
                    throw new RawScanUnavailableException("An ATTRIBUTE_LIST contains duplicate extent references.");
                var signature = new AttributeSignature(reference.Type, reference.Instance,
                    reference.Name, reference.LowestVcn);
                if (reference.RecordNumber >= (ulong)entries.Length ||
                    entries[(int)reference.RecordNumber] is not { } target ||
                    target.Sequence != reference.RecordSequence ||
                    (target.Index != entry.Index &&
                     (target.BaseIndex != (ulong)entry.Index || target.BaseSequence != entry.Sequence)) ||
                    target.Signatures.Count(item => item == signature) != 1)
                    throw new RawScanUnavailableException("An ATTRIBUTE_LIST extent is missing or stale; raw results would be incomplete.");
            }

            var declared = list.Select(reference => new AttributeLocation(reference.Type, reference.Instance,
                reference.Name, reference.LowestVcn, reference.RecordNumber, reference.RecordSequence)).ToHashSet();
            var actual = new HashSet<AttributeLocation>();
            AddLocations(entry);
            if (extensions.TryGetValue(((ulong)entry.Index, entry.Sequence), out var associated))
                foreach (var extension in associated)
                    AddLocations(extension);
            if (!declared.SetEquals(actual))
                throw new RawScanUnavailableException("ATTRIBUTE_LIST does not account for every record extent; raw results would be incomplete.");

            void AddLocations(MftEntry owner)
            {
                foreach (var signature in owner.Signatures.Where(item => item.Type != AttributeAttributeList))
                    actual.Add(new AttributeLocation(signature.Type, signature.Instance, signature.Name,
                        signature.LowestVcn, (ulong)owner.Index, owner.Sequence));
            }
        }

        foreach (var pair in extensions)
        {
            if (pair.Key.Base >= (ulong)entries.Length || entries[(int)pair.Key.Base] is not { } baseEntry ||
                baseEntry.Sequence != pair.Key.Sequence || baseEntry.AttributeList is null)
                throw new RawScanUnavailableException("An extension record has no valid base ATTRIBUTE_LIST.");
        }
    }

    private static ScanResult BuildTree(string rootPath, MftEntry?[] entries, List<string> warnings,
        Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var rootEntry = entries[5] ?? throw new RawScanUnavailableException("The NTFS root directory record is missing.");
        if (!rootEntry.IsDirectory)
            throw new RawScanUnavailableException("The NTFS root record is not a directory.");

        var root = new ScanNode
        {
            Name = rootPath,
            FullPath = rootPath,
            IsDirectory = true,
            Attributes = FileAttributes.Directory,
            LastWriteTimeUtc = rootEntry.LastWriteTimeUtc
        };
        var nodes = new Dictionary<int, ScanNode>(entries.Length / 2) { [5] = root };
        var chosenNames = new Dictionary<int, FileNameAttribute>();
        var files = new List<ScanNode>();
        var hardLinksCollapsed = 0L;

        for (var i = 0; i < entries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (entry is null || entry.BaseIndex != 0 || i == 5)
                continue;
            var names = GatherNames(entry, entries);
            if (names.Count == 0)
                throw new RawScanUnavailableException(
                    $"In-use base MFT record {i} has no valid FILE_NAME attribute; raw results would be incomplete.");
            var selected = names.OrderByDescending(n => NamespaceRank(n.Namespace))
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).First();
            hardLinksCollapsed += names.Where(n => n.Namespace != 2).Select(n => (n.ParentIndex, n.Name))
                .Distinct().Skip(1).LongCount();
            chosenNames[i] = selected;
            var (size, allocated) = DataSizes(entry, entries);
            var node = new ScanNode
            {
                Name = selected.Name,
                IsDirectory = entry.IsDirectory,
                Size = entry.IsDirectory ? 0 : size,
                AllocatedSize = entry.IsDirectory ? 0 : allocated,
                LastWriteTimeUtc = entry.LastWriteTimeUtc == default ? selected.LastWriteTimeUtc : entry.LastWriteTimeUtc,
                Attributes = entry.Attributes == 0 ? selected.Attributes : entry.Attributes,
                FileCount = entry.IsDirectory ? 0 : 1
            };
            nodes[i] = node;
            if (!entry.IsDirectory)
                files.Add(node);
        }

        foreach (var (index, node) in nodes)
        {
            if (index == 5)
                continue;
            var entry = entries[index]!;
            var name = chosenNames[index];
            ScanNode parent;
            if (name.ParentIndex < (ulong)entries.Length && nodes.TryGetValue((int)name.ParentIndex, out var candidate) &&
                candidate.IsDirectory && entries[(int)name.ParentIndex]?.Sequence == name.ParentSequence)
            {
                parent = candidate;
            }
            else
            {
                throw new RawScanUnavailableException(
                    $"MFT record {index} has a stale or missing parent; a complete path tree cannot be proven.");
            }
            node.Parent = parent;
            parent.Children.Add(node);
        }

        AssignPathsAndTotals(root, nodes.Values.LongCount(n => n.IsDirectory), cancellationToken);
        if (hardLinksCollapsed != 0)
            warnings.Add($"Collapsed {hardLinksCollapsed:N0} additional hard-link names to avoid counting the same MFT record twice.");

        stopwatch.Stop();
        return new ScanResult(root, files, new ScanSummary(files.Count,
            nodes.Values.LongCount(n => n.IsDirectory), 0, stopwatch.Elapsed, "NTFS MFT", false, warnings));
    }

    private static List<FileNameAttribute> GatherNames(MftEntry entry, MftEntry?[] entries)
    {
        var result = entry.Names.Select(item => item.Value).ToList();
        if (entry.AttributeList is null)
            return result;
        foreach (var reference in entry.AttributeList.Where(r => r.Type == AttributeFileName && r.RecordNumber != (ulong)entry.Index))
        {
            var target = entries[(int)reference.RecordNumber]!;
            result.AddRange(target.Names.Where(item => item.Instance == reference.Instance).Select(item => item.Value));
        }
        return result;
    }

    private static (long Size, long Allocated) DataSizes(MftEntry entry, MftEntry?[] entries)
    {
        var data = new List<DataAttribute>(entry.Data);
        if (entry.AttributeList is not null)
        {
            foreach (var reference in entry.AttributeList.Where(r => r.Type == AttributeData && r.RecordNumber != (ulong)entry.Index))
                data.AddRange(entries[(int)reference.RecordNumber]!.Data.Where(d => d.Instance == reference.Instance &&
                    d.Name == reference.Name && d.LowestVcn == reference.LowestVcn));
        }

        long size = 0, allocated = 0;
        foreach (var stream in data.GroupBy(d => d.Name, StringComparer.Ordinal))
        {
            var first = stream.OrderBy(d => d.LowestVcn).First();
            if (first.LowestVcn != 0)
                throw new RawScanUnavailableException("A data stream is missing its first extent.");
            size = checked(size + first.DataSize);
            if (!first.NonResident)
                continue;
            var stored = (first.Flags & 0x8001) != 0
                ? first.CompressedSize
                : first.AllocatedSize;
            allocated = checked(allocated + stored);
        }
        return (size, allocated);
    }

    private static void AssignPathsAndTotals(ScanNode root, long expectedDirectories, CancellationToken token)
    {
        var stack = new Stack<(ScanNode Node, bool Visited)>();
        var active = new HashSet<ScanNode>(ReferenceEqualityComparer.Instance);
        var completed = new HashSet<ScanNode>(ReferenceEqualityComparer.Instance);
        stack.Push((root, false));
        while (stack.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            var (node, visited) = stack.Pop();
            if (!visited)
            {
                if (!active.Add(node))
                    throw new RawScanUnavailableException("The MFT directory parent graph contains a cycle.");
                stack.Push((node, true));
                foreach (var child in node.Children)
                {
                    child.FullPath = Path.Combine(node.FullPath, child.Name);
                    if (child.IsDirectory)
                        stack.Push((child, false));
                }
            }
            else
            {
                active.Remove(node);
                completed.Add(node);
                foreach (var child in node.Children)
                {
                    node.Size = checked(node.Size + child.Size);
                    node.AllocatedSize = checked(node.AllocatedSize + child.AllocatedSize);
                    node.FileCount = checked(node.FileCount + child.FileCount);
                }
            }
        }
        if (completed.LongCount() != expectedDirectories)
            throw new RawScanUnavailableException("The MFT directory graph is not fully reachable from the root.");
    }

    private static int NamespaceRank(byte value) => value switch { 3 => 4, 1 => 3, 0 => 2, 2 => 1, _ => 0 };

    private static DateTime FileTime(long value)
    {
        try { return value <= 0 ? default : DateTime.FromFileTimeUtc(value); }
        catch (ArgumentOutOfRangeException) { return default; }
    }

    private static void ValidateVolume(NtfsVolumeData volume)
    {
        if (volume.BytesPerSector is < 256 or > 65536 || volume.BytesPerCluster < volume.BytesPerSector ||
            volume.BytesPerFileRecordSegment is < 512 or > 65536 ||
            volume.BytesPerFileRecordSegment % volume.BytesPerSector != 0 ||
            volume.MftStartLcn < 0 || volume.MftValidDataLength <= 0)
            throw new RawScanUnavailableException("The NTFS volume geometry is invalid or unsupported.");
    }

    private static void ValidateMftRuns(IReadOnlyList<NtfsDataRun> runs, long length, uint clusterSize)
    {
        long expectedVcn = 0;
        foreach (var run in runs)
        {
            if (run.Vcn != expectedVcn || run.Lcn < 0)
                throw new RawScanUnavailableException("The MFT runlist is sparse or discontinuous.");
            expectedVcn = checked(expectedVcn + run.ClusterCount);
        }
        if (checked(expectedVcn * clusterSize) < length)
            throw new RawScanUnavailableException("The MFT runlist does not cover its valid data length.");
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = RandomAccess.Read(handle, buffer[read..], checked(offset + read));
            if (count == 0)
                throw new EndOfStreamException("Unexpected end of raw NTFS volume data.");
            read += count;
        }
    }

    private sealed class RunReader : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly IReadOnlyList<NtfsDataRun> _runs;
        private readonly long _clusterSize;
        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(8 * 1024 * 1024);
        private long _remaining;
        private int _runIndex;
        private long _runByteOffset;
        private int _bufferOffset;
        private int _bufferCount;

        internal RunReader(SafeFileHandle handle, IReadOnlyList<NtfsDataRun> runs, uint clusterSize, long length)
        {
            _handle = handle;
            _runs = runs;
            _clusterSize = clusterSize;
            _remaining = length;
        }

        internal void ReadExactly(Span<byte> destination)
        {
            if (destination.Length > _remaining)
                throw new EndOfStreamException();
            while (!destination.IsEmpty)
            {
                if (_bufferOffset == _bufferCount)
                    Fill();
                var count = Math.Min(destination.Length, _bufferCount - _bufferOffset);
                _buffer.AsSpan(_bufferOffset, count).CopyTo(destination);
                _bufferOffset += count;
                destination = destination[count..];
                _remaining -= count;
            }
        }

        private void Fill()
        {
            while (_runIndex < _runs.Count)
            {
                var run = _runs[_runIndex];
                var runBytes = checked(run.ClusterCount * _clusterSize);
                if (_runByteOffset >= runBytes)
                {
                    _runIndex++;
                    _runByteOffset = 0;
                    continue;
                }
                var requested = checked((int)Math.Min(Math.Min(_buffer.Length, runBytes - _runByteOffset), _remaining));
                NtfsMftScanner.ReadExactly(_handle, _buffer.AsSpan(0, requested), checked(run.Lcn * _clusterSize + _runByteOffset));
                _runByteOffset += requested;
                _bufferOffset = 0;
                _bufferCount = requested;
                return;
            }
            throw new EndOfStreamException("The MFT runlist ended early.");
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
    }

    private sealed class MftEntry(int index, ushort sequence, bool isDirectory, ulong baseIndex, ushort baseSequence)
    {
        internal int Index { get; } = index;
        internal ushort Sequence { get; } = sequence;
        internal bool IsDirectory { get; } = isDirectory;
        internal ulong BaseIndex { get; } = baseIndex;
        internal ushort BaseSequence { get; } = baseSequence;
        internal DateTime LastWriteTimeUtc { get; set; }
        internal FileAttributes Attributes { get; set; }
        internal List<NamedFileName> Names { get; } = [];
        internal List<DataAttribute> Data { get; } = [];
        internal List<AttributeSignature> Signatures { get; } = [];
        internal List<AttributeListReference>? AttributeList { get; set; }
    }

    private sealed record FileNameAttribute(string Name, byte Namespace, ulong ParentIndex,
        ushort ParentSequence, DateTime LastWriteTimeUtc, FileAttributes Attributes);
    private sealed record NamedFileName(ushort Instance, FileNameAttribute Value);
    private sealed record DataAttribute(ushort Instance, string Name, long LowestVcn, bool NonResident, ushort Flags,
        long DataSize, long AllocatedSize, long CompressedSize);
    private readonly record struct AttributeSignature(uint Type, ushort Instance, string Name, long LowestVcn);
    private readonly record struct AttributeLocation(uint Type, ushort Instance, string Name, long LowestVcn,
        ulong RecordNumber, ushort RecordSequence);
    private sealed record AttributeListReference(uint Type, ushort Instance, string Name, long LowestVcn,
        ulong RecordNumber, ushort RecordSequence);

    [StructLayout(LayoutKind.Sequential)]
    private struct NtfsVolumeData
    {
        internal long VolumeSerialNumber, NumberSectors, TotalClusters, FreeClusters, TotalReserved;
        internal uint BytesPerSector, BytesPerCluster, BytesPerFileRecordSegment, ClustersPerFileRecordSegment;
        internal long MftValidDataLength, MftStartLcn, Mft2StartLcn, MftZoneStart, MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NtfsExtendedVolumeData
    {
        internal uint ByteCount;
        internal ushort MajorVersion, MinorVersion;
        internal uint BytesPerPhysicalSector;
        internal ushort LfsMajorVersion, LfsMinorVersion;
        internal uint MaxDeviceTrimExtentCount, MaxDeviceTrimByteCount, MaxVolumeTrimExtentCount, MaxVolumeTrimByteCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NtfsVolumeDataWithExtended
    {
        internal NtfsVolumeData Basic;
        internal NtfsExtendedVolumeData Extended;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr input, int inputSize, out NtfsVolumeDataWithExtended output, int outputSize, out int bytesReturned,
        IntPtr overlapped);
}

internal sealed class RawScanUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
