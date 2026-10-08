using System.Buffers.Binary;
using DiskAtlas.Core.Scanning;
using DiskAtlas.Core;
using System.Diagnostics;
using System.Reflection;
using System.Text;

internal static class ParserChecks
{
    public static void Run()
    {
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        var resident = Record(848, false);
        Put32(resident, 56 + 16, 820);
        Put16(resident, 56 + 20, 24);
        resident.AsSpan(80, 820).Fill(0x5a);
        Seal(resident);
        var parsed = NtfsRecordParser.ParseRecord(resident, 512);
        Assert(parsed.InUse && parsed.RecordNumber == 37 && parsed.SequenceNumber == 9, "Record header parse failed");
        Assert(parsed.Attributes.Single().ResidentValue!.All(value => value == 0x5a), "USA fixup did not restore resident content");
        Assert(parsed.Attributes[0].DataSize == 820 && parsed.Attributes[0].AllocatedSize == 0, "Resident size incorrect");

        var nonresident = NonResident([0x11, 2, 20, 0x11, 1, 0xfe, 0], 2);
        parsed = NtfsRecordParser.ParseRecord(nonresident, 512);
        var data = parsed.Attributes.Single();
        Assert(data.DataSize == 9000 && data.AllocatedSize == 12288 && data.CompressedSize == 0, "Nonresident size/header interpretation failed");
        Assert(data.Runs!.Count == 2 && data.Runs[0].Lcn == 20 && data.Runs[1].Lcn == 18, "Signed delta decoding failed");

        var sparse = Record(88, true);
        Put16(sparse, 56 + 12, 0x8000);
        Put64(sparse, 56 + 24, 2);
        Put16(sparse, 56 + 32, 72);
        Put64(sparse, 56 + 40, 12288);
        Put64(sparse, 56 + 48, 9000);
        Put64(sparse, 56 + 56, 9000);
        Put64(sparse, 56 + 64, 8192);
        new byte[] { 0x11, 2, 20, 0x01, 1, 0 }.CopyTo(sparse, 56 + 72);
        Seal(sparse);
        parsed = NtfsRecordParser.ParseRecord(sparse, 512);
        Assert(parsed.Attributes[0].CompressedSize == 8192 && parsed.Attributes[0].Runs![1].Lcn == -1, "Sparse run/header parse failed");

        var brokenUsa = (byte[])resident.Clone(); brokenUsa[510] ^= 1;
        Reject(brokenUsa, "Torn sector accepted");
        var brokenBounds = (byte[])nonresident.Clone(); Put32(brokenBounds, 60, uint.MaxValue);
        Reject(brokenBounds, "Oversized attribute accepted");
        var shortCoverage = NonResident([0x11, 1, 20, 0], 2);
        Reject(shortCoverage, "Incomplete run coverage accepted");
        var extraCoverage = NonResident([0x11, 4, 20, 0], 2);
        Reject(extraCoverage, "Run outside declared coverage accepted");
        var negativeLcn = NonResident([0x11, 3, 0xff, 0], 2);
        Reject(negativeLcn, "Negative physical cluster accepted");
        var overlap = (byte[])resident.Clone(); Put16(overlap, 56 + 20, 8);
        Reject(overlap, "Resident value overlapping header accepted");
        CheckReconstructedTree();
    }

    private static void CheckReconstructedTree()
    {
        var scannerType = typeof(DiskScanner).Assembly.GetType("DiskAtlas.Core.Scanning.NtfsMftScanner")!;
        var convert = scannerType.GetMethod("ConvertRecord", BindingFlags.Static | BindingFlags.NonPublic)!;
        var build = scannerType.GetMethod("BuildTree", BindingFlags.Static | BindingFlags.NonPublic)!;
        var entries = Array.CreateInstance(convert.ReturnType, 40);
        object Entry(int index, bool directory, string name, int parent, int bytes = 0)
        {
            var nameBytes = Encoding.Unicode.GetBytes(name);
            var value = new byte[66 + nameBytes.Length];
            Put64(value, 0, (long)(uint)parent | (9L << 48));
            Put32(value, 56, directory ? 0x10u : 0x20u);
            value[64] = (byte)name.Length; value[65] = 1;
            nameBytes.CopyTo(value, 66);
            var attributes = new List<ParsedNtfsAttribute>
            { new(0x30, 0, "", false, 0, 0, value.Length, 0, 0, value, null) };
            if (!directory) attributes.Add(new(0x80, 1, "", false, 0, 0, bytes, 0, 0, new byte[bytes], null));
            var parsed = new ParsedNtfsRecord((uint)index, 9, true, directory, 0, 0, attributes);
            return convert.Invoke(null, [index, parsed])!;
        }
        entries.SetValue(Entry(5, true, ".", 5), 5);
        entries.SetValue(Entry(20, true, "Folder", 5), 20);
        entries.SetValue(Entry(21, false, "note.txt", 20, 123), 21);
        var scan = (ScanResult)build.Invoke(null, [@"C:\ntfs-test\", entries, new List<string>(), Stopwatch.StartNew(), CancellationToken.None])!;
        if (scan.Files.Count != 1 || scan.Root.Size != 123 || scan.Root.FileCount != 1 || scan.Files[0].FullPath != @"C:\ntfs-test\Folder\note.txt")
            throw new Exception("Raw MFT tree failed to reconstruct file paths/totals");

        // An unresolved or cyclic parent graph must trigger fallback, never invent paths.
        entries.SetValue(Entry(20, true, "Folder", 20), 20);
        try { build.Invoke(null, [@"C:\ntfs-test\", entries, new List<string>(), Stopwatch.StartNew(), CancellationToken.None]); }
        catch (TargetInvocationException error) when (error.InnerException is IOException) { return; }
        throw new Exception("Cyclic MFT parent graph was accepted");
    }

    private static byte[] Record(int attributeLength, bool nonResident)
    {
        var record = new byte[1024];
        "FILE"u8.CopyTo(record);
        Put16(record, 4, 48); Put16(record, 6, 3);
        Put16(record, 16, 9); Put16(record, 20, 56); Put16(record, 22, 1);
        Put32(record, 24, (uint)(56 + attributeLength + 8)); Put32(record, 28, 1024); Put32(record, 44, 37);
        Put32(record, 56, 0x80); Put32(record, 60, (uint)attributeLength);
        record[64] = nonResident ? (byte)1 : (byte)0;
        Put32(record, 56 + attributeLength, uint.MaxValue);
        return record;
    }

    private static byte[] NonResident(byte[] runs, long highestVcn)
    {
        var record = Record(80, true);
        Put64(record, 56 + 24, highestVcn); Put16(record, 56 + 32, 64);
        Put64(record, 56 + 40, 12288); Put64(record, 56 + 48, 9000); Put64(record, 56 + 56, 9000);
        runs.CopyTo(record, 56 + 64);
        Seal(record);
        return record;
    }

    private static void Seal(byte[] record)
    {
        Put16(record, 48, 0xabcd);
        Put16(record, 50, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510, 2)));
        Put16(record, 52, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022, 2)));
        Put16(record, 510, 0xabcd); Put16(record, 1022, 0xabcd);
    }

    private static void Reject(byte[] record, string message)
    {
        try { NtfsRecordParser.ParseRecord(record, 512); }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException) { return; }
        throw new Exception(message);
    }

    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static void Put64(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset, 8), value);
}
