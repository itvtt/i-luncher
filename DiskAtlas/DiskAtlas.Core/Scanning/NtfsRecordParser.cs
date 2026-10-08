using System.Buffers.Binary;
using System.Text;

namespace DiskAtlas.Core.Scanning;

/// <summary>Small, side-effect-free NTFS record parser exposed for synthetic parser tests.</summary>
public static class NtfsRecordParser
{
    public static ParsedNtfsRecord ParseRecord(ReadOnlySpan<byte> record, int bytesPerSector)
    {
        if (bytesPerSector < 256 || (bytesPerSector & 1) != 0)
            throw new InvalidDataException("The NTFS sector size is invalid.");
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8))
            throw new InvalidDataException("The buffer is not an NTFS FILE record.");

        var fixedRecord = record.ToArray();
        ApplyUpdateSequence(fixedRecord, bytesPerSector);

        var sequence = U16(fixedRecord, 0x10);
        var flags = U16(fixedRecord, 0x16);
        var used = checked((int)U32(fixedRecord, 0x18));
        var allocated = checked((int)U32(fixedRecord, 0x1c));
        var firstAttribute = U16(fixedRecord, 0x14);
        var baseReference = U64(fixedRecord, 0x20);
        var recordNumber = fixedRecord.Length >= 0x30 ? U32(fixedRecord, 0x2c) : 0;

        if (used < firstAttribute || used > fixedRecord.Length || allocated != fixedRecord.Length ||
            firstAttribute < 0x30 || (firstAttribute & 7) != 0 || ((flags & 1) != 0 && sequence == 0))
            throw new InvalidDataException("The NTFS record bounds are invalid.");

        var attributes = new List<ParsedNtfsAttribute>();
        for (var offset = (int)firstAttribute; ;)
        {
            if (offset < 0 || offset > used - 8)
                throw new InvalidDataException("The NTFS attribute terminator is outside the used record area.");
            var type = U32(fixedRecord, offset);
            if (type == 0xffffffff)
                break;

            var length = checked((int)U32(fixedRecord, offset + 4));
            if (length < 24 || (length & 7) != 0 || offset > used - length)
                throw new InvalidDataException("An NTFS attribute has invalid bounds.");

            var formCode = fixedRecord[offset + 8];
            if (formCode > 1)
                throw new InvalidDataException("An NTFS attribute has an invalid form code.");
            var nonResident = formCode == 1;
            var nameLength = fixedRecord[offset + 9];
            var nameOffset = U16(fixedRecord, offset + 10);
            var attributeFlags = U16(fixedRecord, offset + 12);
            var instance = U16(fixedRecord, offset + 14);
            string name = "";
            if (nameLength != 0)
            {
                var byteLength = checked(nameLength * 2);
                EnsureWithinAttribute(nameOffset, byteLength, length);
                if (nameOffset < (nonResident ? 64 : 24))
                    throw new InvalidDataException("An NTFS attribute name overlaps its header.");
                name = Encoding.Unicode.GetString(fixedRecord, offset + nameOffset, byteLength);
            }

            long lowestVcn = 0;
            long allocatedSize = 0;
            long dataSize;
            long compressedSize = 0;
            byte[]? residentValue = null;
            IReadOnlyList<NtfsDataRun>? runs = null;

            if (nonResident)
            {
                if (length < 64)
                    throw new InvalidDataException("A nonresident NTFS attribute is truncated.");
                lowestVcn = I64(fixedRecord, offset + 16);
                var highestVcn = I64(fixedRecord, offset + 24);
                var runOffset = U16(fixedRecord, offset + 32);
                allocatedSize = I64(fixedRecord, offset + 40);
                dataSize = I64(fixedRecord, offset + 48);
                if ((attributeFlags & 0x8001) != 0 && length >= 72 && runOffset >= 72)
                    compressedSize = I64(fixedRecord, offset + 64);
                if (lowestVcn < 0 || highestVcn < lowestVcn || allocatedSize < 0 || dataSize < 0 || compressedSize < 0)
                    throw new InvalidDataException("A nonresident NTFS attribute contains invalid sizes.");
                EnsureWithinAttribute(runOffset, 1, length);
                if (runOffset < 64 || ((attributeFlags & 0x8001) != 0 && runOffset < 72))
                    throw new InvalidDataException("An NTFS runlist overlaps its attribute header.");
                runs = ParseRuns(fixedRecord.AsSpan(offset + runOffset, length - runOffset), lowestVcn, highestVcn);
            }
            else
            {
                var valueLength = checked((int)U32(fixedRecord, offset + 16));
                var valueOffset = U16(fixedRecord, offset + 20);
                EnsureWithinAttribute(valueOffset, valueLength, length);
                if (valueOffset < 24)
                    throw new InvalidDataException("A resident NTFS value overlaps its attribute header.");
                residentValue = fixedRecord.AsSpan(offset + valueOffset, valueLength).ToArray();
                dataSize = valueLength;
            }

            attributes.Add(new ParsedNtfsAttribute(type, instance, name, nonResident, attributeFlags,
                lowestVcn, dataSize, allocatedSize, compressedSize, residentValue, runs));
            offset = checked(offset + length);
        }

        return new ParsedNtfsRecord(recordNumber, sequence, (flags & 1) != 0, (flags & 2) != 0,
            baseReference & 0x0000ffffffffffffUL, (ushort)(baseReference >> 48), attributes);
    }

    private static IReadOnlyList<NtfsDataRun> ParseRuns(ReadOnlySpan<byte> bytes, long lowestVcn, long highestVcn)
    {
        var result = new List<NtfsDataRun>();
        var cursor = 0;
        var vcn = lowestVcn;
        long lcn = 0;
        var terminated = false;
        while (cursor < bytes.Length)
        {
            var header = bytes[cursor++];
            if (header == 0)
            {
                terminated = true;
                break;
            }
            var lengthBytes = header & 0x0f;
            var offsetBytes = header >> 4;
            if (lengthBytes is 0 or > 8 || offsetBytes > 8 || cursor > bytes.Length - lengthBytes - offsetBytes)
                throw new InvalidDataException("An NTFS data run is malformed.");

            var clusterCount = ReadUnsigned(bytes.Slice(cursor, lengthBytes));
            cursor += lengthBytes;
            if (clusterCount <= 0)
                throw new InvalidDataException("An NTFS data run has an invalid length.");

            long runLcn = -1;
            if (offsetBytes != 0)
            {
                var delta = ReadSigned(bytes.Slice(cursor, offsetBytes));
                lcn = checked(lcn + delta);
                if (lcn < 0)
                    throw new InvalidDataException("An NTFS data run has a negative LCN.");
                runLcn = lcn;
            }
            cursor += offsetBytes;
            result.Add(new NtfsDataRun(vcn, clusterCount, runLcn));
            vcn = checked(vcn + clusterCount);
        }

        if (!terminated || result.Count == 0 || vcn != checked(highestVcn + 1))
            throw new InvalidDataException("The NTFS runlist does not cover the declared VCN range.");
        return result;
    }

    private static void ApplyUpdateSequence(Span<byte> record, int bytesPerSector)
    {
        var usaOffset = U16(record, 4);
        var usaCount = U16(record, 6);
        if (usaCount < 2 || usaOffset > record.Length - usaCount * 2 ||
            usaOffset < 8 || usaOffset + usaCount * 2 > U16(record, 0x14) ||
            checked((usaCount - 1) * bytesPerSector) != record.Length)
            throw new InvalidDataException("The NTFS update sequence array is invalid.");

        var marker = U16(record, usaOffset);
        for (var sector = 1; sector < usaCount; sector++)
        {
            var trailer = checked(sector * bytesPerSector - 2);
            if (U16(record, trailer) != marker)
                throw new InvalidDataException("The NTFS update sequence check failed.");
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(trailer, 2), U16(record, usaOffset + sector * 2));
        }
    }

    private static long ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (var i = 0; i < bytes.Length; i++)
            value |= (ulong)bytes[i] << (i * 8);
        if (value > long.MaxValue)
            throw new InvalidDataException("An NTFS integer is too large.");
        return (long)value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (var i = 0; i < bytes.Length; i++)
            value |= (ulong)bytes[i] << (i * 8);
        if ((bytes[^1] & 0x80) != 0 && bytes.Length < 8)
            value |= ulong.MaxValue << (bytes.Length * 8);
        return unchecked((long)value);
    }

    private static void Ensure(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
            throw new InvalidDataException("The NTFS record is truncated.");
    }

    private static void EnsureWithinAttribute(int offset, int size, int attributeLength)
    {
        if (offset < 0 || size < 0 || offset > attributeLength - size)
            throw new InvalidDataException("An NTFS attribute value is out of bounds.");
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
    private static long I64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
}

public sealed record ParsedNtfsRecord(uint RecordNumber, ushort SequenceNumber, bool InUse, bool IsDirectory,
    ulong BaseRecordNumber, ushort BaseRecordSequence, IReadOnlyList<ParsedNtfsAttribute> Attributes);

public sealed record ParsedNtfsAttribute(uint Type, ushort Instance, string Name, bool IsNonResident,
    ushort Flags, long LowestVcn, long DataSize, long AllocatedSize, long CompressedSize,
    byte[]? ResidentValue, IReadOnlyList<NtfsDataRun>? Runs);

public readonly record struct NtfsDataRun(long Vcn, long ClusterCount, long Lcn);
