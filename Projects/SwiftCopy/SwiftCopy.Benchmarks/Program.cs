using System.Diagnostics;
using System.Text.Json;
using SwiftCopy.Core;

// Each run creates a unique destination. Fixture creation and byte verification are
// outside the transfer timer. This measures this machine, not arbitrary USB/NAS hardware.
if (args.Length < 3) throw new ArgumentException("prepare <root> <count> | validate <root> <label> <count> | copy <root> <label> <workers> <repeats>");
string mode = args[0], root = Path.GetFullPath(args[1]);
string source = Path.Combine(root, "source"), marker = Path.Combine(root, "fixture.json");
byte[] Data(int index)
{
    var data = new byte[4096];
    new Random(index + 391).NextBytes(data);
    return data;
}
if (mode == "prepare")
{
    if (Directory.Exists(root)) throw new IOException("Use a new fixture directory.");
    int count = int.Parse(args[2]);
    Directory.CreateDirectory(source);
    for (int i = 0; i < count; i++)
    {
        string folder = Path.Combine(source, (i / 100).ToString("D3"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, $"{i:D6}.bin"), Data(i));
    }
    File.WriteAllText(marker, JsonSerializer.Serialize(new { count, bytesPerFile = 4096 }));
    Console.WriteLine($"Prepared {count} files in {root}");
    return;
}
using var fixture = JsonDocument.Parse(File.ReadAllText(marker));
int expected = fixture.RootElement.GetProperty("count").GetInt32();
string label = args[2];
if (label.Length == 0 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid run label.");
var results = new List<object>();
if (mode == "validate")
{
    string[] files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Take(int.Parse(args[3])).ToArray();
    var watch = Stopwatch.StartNew();
    var selected = TransferEngine.Validate(new(files, Path.Combine(root, "validation-target")));
    watch.Stop();
    if (selected.Count != files.Length) throw new Exception("Validation dropped files.");
    results.Add(new { label, mode, files = files.Length, elapsedMs = watch.Elapsed.TotalMilliseconds });
}
else if (mode == "copy")
{
    int workers = int.Parse(args[3]), repeats = int.Parse(args[4]);
    for (int trial = 1; trial <= repeats; trial++)
    {
        string destination = Path.Combine(root, $"{label}-{workers}-{trial}-{Guid.NewGuid():N}");
        using var session = new TransferSession(Path.Combine(root, "logs"));
        var watch = Stopwatch.StartNew();
        var result = await TransferEngine.RunAsync(new([source], destination, Workers: workers), session);
        watch.Stop();
        if (result.Cancelled || result.Snapshot.Errors != 0 || result.Snapshot.CopiedFiles != expected)
            throw new Exception("Transfer totals mismatch.");
        int verified = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(destination, "source"), "*", SearchOption.AllDirectories))
        {
            int index = int.Parse(Path.GetFileNameWithoutExtension(file));
            if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(Data(index))) throw new Exception("Output content mismatch.");
            verified++;
        }
        if (verified != expected) throw new Exception("Output file count mismatch.");
        results.Add(new { label, mode, trial, workers, files = expected, bytesPerFile = 4096, elapsedMs = watch.Elapsed.TotalMilliseconds,
            filesPerSecond = expected / watch.Elapsed.TotalSeconds, verified });
        Console.WriteLine(JsonSerializer.Serialize(results[^1]));
    }
}
else throw new ArgumentException("Unknown benchmark mode.");
string report = Path.Combine(root, $"{label}-{mode}-{Guid.NewGuid():N}.json");
File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(results));
Console.WriteLine("Report: " + report);
