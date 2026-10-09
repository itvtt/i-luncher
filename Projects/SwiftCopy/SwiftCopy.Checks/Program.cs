using System.Diagnostics;
using System.Security.Cryptography;
using SwiftCopy.Core;

if (args.Length == 2 && args[0] == "--move-existing-fixture")
{
    string fixture = Path.GetFullPath(args[1]);
    if (!fixture.StartsWith(Path.Combine(Path.GetTempPath(), "SwiftCopy-Checks-"), StringComparison.OrdinalIgnoreCase)
        || !fixture.EndsWith(Path.Combine("scale", "source"), StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Only a generated scale fixture may be used for this check.");
    int before = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).Count();
    string dest = Path.Combine(Path.GetDirectoryName(fixture)!, "fast-moved-" + Guid.NewGuid().ToString("N"));
    using var session = new TransferSession();
    var watch = Stopwatch.StartNew();
    var result = await TransferEngine.RunAsync(new([fixture], dest, Move: true), session);
    watch.Stop();
    int after = Directory.EnumerateFiles(Path.Combine(dest, "source"), "*", SearchOption.AllDirectories).Count();
    if (before != after || Directory.Exists(fixture) || result.Snapshot.Errors != 0 || result.Snapshot.MovedDirectories != 1)
        throw new Exception("Fast folder move verification failed.");
    string message = $"PASS same-volume whole-folder move: {before:N0} files, {watch.Elapsed.TotalMilliseconds:F2} ms; output count verified; source relocated.";
    Console.WriteLine(message);
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fast-move-results.txt"), message);
    return;
}

string? filter = null;
for (int i = 0; i < args.Length; i++)
{
    if (!args[i].Equals("--filter", StringComparison.OrdinalIgnoreCase)) continue;
    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
        throw new ArgumentException("--filter requires a non-empty check name substring.");
    filter = args[i];
}

string root = Path.Combine(Path.GetTempPath(), "SwiftCopy-Checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<string>();
int failures = 0;
int executed = 0;
Console.WriteLine($"Test workspace: {root}");

string Dir(string relative) { string path = Path.Combine(root, relative); Directory.CreateDirectory(path); return path; }
string Write(string relative, string contents)
{
    string path = Path.Combine(root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, contents);
    return path;
}
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task<TransferResult> Run(string[] paths, string destination, ConflictPolicy conflict = ConflictPolicy.Skip, bool move = false, bool verify = true, int workers = 4)
{
    using var session = new TransferSession(Dir("logs"));
    var result = await TransferEngine.RunAsync(new(paths, destination, workers, conflict, move, verify), session);
    if (result.Snapshot.Errors > 0) Console.WriteLine(File.ReadAllText(session.LogPath));
    return result;
}
async Task Test(string name, Func<Task> action)
{
    if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
    executed++;
    var sw = Stopwatch.StartNew();
    try { await action(); string line = $"PASS {name} ({sw.Elapsed.TotalSeconds:F2}s)"; results.Add(line); Console.WriteLine(line); }
    catch (Exception ex) { failures++; string line = $"FAIL {name}: {ex}"; results.Add(line); Console.WriteLine(line); }
}

await Test("Mixed files, nested directories, empty directories, Unicode, zero bytes and content hashes", async () =>
{
    string source = Dir("mixed/source/사진 폴더");
    Dir("mixed/source/사진 폴더/빈 폴더");
    Write("mixed/source/사진 폴더/깊은 곳/한글 😊.dat", new string('한', 10000));
    Write("mixed/source/사진 폴더/empty.bin", "");
    string loose = Write("mixed/별도.xyz", "no extension restrictions");
    string dest = Dir("mixed/dest");
    var r = await Run([source, loose], dest);
    Assert(r.Snapshot.CopiedFiles == 3 && r.Snapshot.Errors == 0, "Mixed transfer counts");
    Assert(Directory.Exists(Path.Combine(dest, "사진 폴더", "빈 폴더")), "Empty directory missing");
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        Assert(SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(dest, "사진 폴더", Path.GetRelativePath(source, file))))), "Hash mismatch");
});

await Test("Skip preserves both originals and existing targets, including in move mode", async () =>
{
    string file = Write("skip/source/a.txt", "source");
    string dest = Dir("skip/dest"); Write("skip/dest/a.txt", "existing");
    var r = await Run([file], dest, move: true);
    Assert(r.Snapshot.Skipped == 1 && File.Exists(file) && File.ReadAllText(Path.Combine(dest, "a.txt")) == "existing", "Skip changed files");
});

await Test("Overwrite replaces only completed output", async () =>
{
    string file = Write("overwrite/source/a.txt", "new data");
    string dest = Dir("overwrite/dest"); Write("overwrite/dest/a.txt", "old data");
    var r = await Run([file], dest, ConflictPolicy.Overwrite);
    Assert(r.Snapshot.CopiedFiles == 1 && File.ReadAllText(Path.Combine(dest, "a.txt")) == "new data" && File.Exists(file), "Overwrite failed");
});

await Test("Keep both chooses a free numbered name", async () =>
{
    string file = Write("keep/source/a.txt", "new data"); string dest = Dir("keep/dest");
    Write("keep/dest/a.txt", "old"); Write("keep/dest/a (1).txt", "old one");
    var r = await Run([file], dest, ConflictPolicy.KeepBoth);
    Assert(r.Snapshot.CopiedFiles == 1 && File.ReadAllText(Path.Combine(dest, "a (2).txt")) == "new data", "Keep both failed");
});

await Test("Same-volume directory moves immediately without enumerating contents", async () =>
{
    string source = Dir("move/source/tree"); Write("move/source/tree/sub/a.txt", "safe move"); Dir("move/source/tree/empty");
    string dest = Dir("move/dest");
    var r = await Run([source], dest, move: true, verify: false);
    Assert(r.Snapshot.MovedDirectories == 1 && r.Snapshot.FoundFiles == 0 && r.Snapshot.Errors == 0 && !Directory.Exists(source), "Directory should move without scanning");
    Assert(File.ReadAllText(Path.Combine(dest, "tree/sub/a.txt")) == "safe move", "Moved target missing");
});

await Test("Same-volume file rename and merging into existing directories", async () =>
{
    string source = Dir("fast/source/tree"); Write("fast/source/tree/sub/a.txt", "fast move");
    string dest = Dir("fast/dest"); Dir("fast/dest/tree");
    var r = await Run([source], dest, move: true);
    Assert(r.Snapshot.CopiedFiles == 1 && r.Snapshot.MovedDirectories == 0 && r.Snapshot.Errors == 0 && !Directory.Exists(source), "Per-file rename failed");
    Assert(File.ReadAllText(Path.Combine(dest, "tree/sub/a.txt")) == "fast move", "Rename content mismatch");
    string another = Write("fast/another/a.txt", "second");
    r = await Run([another], Path.Combine(dest, "tree/sub"), ConflictPolicy.KeepBoth, move: true);
    Assert(r.Snapshot.CopiedFiles == 1 && !File.Exists(another) && File.ReadAllText(Path.Combine(dest, "tree/sub/a (1).txt")) == "second", "Rename keep-both failed");
});

await Test("Cross-volume move copies, verifies, commits and deletes source", async () =>
{
    string source = Dir("cross/source/tree"); Write("cross/source/tree/sub/a.txt", "cross-volume safe move");
    string crossVolumeRoot = Environment.GetEnvironmentVariable("SWIFTCOPY_CROSS_VOLUME_ROOT") ?? AppContext.BaseDirectory;
    Directory.CreateDirectory(crossVolumeRoot);
    string dest = Path.Combine(crossVolumeRoot, "cross-volume-fixture-" + Guid.NewGuid().ToString("N"));
    Assert(Path.GetPathRoot(source) != Path.GetPathRoot(dest), "This check requires test binaries and TEMP on different volumes");
    var r = await Run([source], dest, move: true, verify: false);
    Assert(r.Snapshot.CopiedFiles == 1 && r.Snapshot.Errors == 0 && !Directory.Exists(source), "Cross-volume move failed");
    Assert(File.ReadAllText(Path.Combine(dest, "tree/sub/a.txt")) == "cross-volume safe move", "Cross-volume target mismatch");
});

await Test("Overlap, self-copy, duplicate root names and junction destinations are rejected", () =>
{
    string source = Dir("validate/source"); Write("validate/source/a.txt", "data");
    void Reject(TransferOptions options)
    {
        bool rejected = false;
        try { TransferEngine.Validate(options); } catch (Exception ex) when (ex is ArgumentException or IOException) { rejected = true; }
        Assert(rejected, "Unsafe path accepted");
    }
    Reject(new([source], Path.Combine(source, "child")));
    Reject(new([Path.Combine(source, "a.txt")], source));
    Reject(new([source, Dir("validate/other/source")], Dir("validate/dest")));
    var normalized = TransferEngine.Validate(new([source, Path.Combine(source, "a.txt"), source], Dir("validate/dest")));
    Assert(normalized.Count == 1, "Nested selection not deduplicated");
    string junction = Path.Combine(root, "validate/link");
    var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{source}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
    p!.WaitForExit(); Assert(p.ExitCode == 0, "Unable to create junction fixture");
    Reject(new([source], Path.Combine(junction, "out")));
    Directory.Delete(junction);
    return Task.CompletedTask;
});

await Test("Many individually selected files validate with the same dedupe and nesting semantics", () =>
{
    string source = Dir("validate-many/source");
    string destination = Dir("validate-many/dest");
    var files = new List<string>();
    for (int i = 0; i < 2_000; i++) files.Add(Write($"validate-many/source/item-{i:D4}.bin", i.ToString()));

    var selected = TransferEngine.Validate(new(files, destination));
    Assert(selected.Count == files.Count, "Individual selections were lost or duplicated");
    Assert(selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() == files.Count, "Validated roots are not unique");

    var withDuplicates = TransferEngine.Validate(new(files.Concat(files.Take(100)).ToArray(), destination));
    Assert(withDuplicates.Count == files.Count, "Exact duplicate selections were not removed");

    var withParent = TransferEngine.Validate(new(files.Append(source).ToArray(), destination));
    Assert(withParent.Count == 1 && withParent[0].Equals(TransferEngine.Normalize(source), StringComparison.OrdinalIgnoreCase),
        "A selected directory did not subsume its individually selected children");
    return Task.CompletedTask;
});

await Test("Locked source reports failure and preserves destination and source", async () =>
{
    string file = Write("locked/source/a.txt", "source"); string dest = Dir("locked/dest"); Write("locked/dest/a.txt", "original target");
    using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var r = await Run([file], dest, ConflictPolicy.Overwrite, move: true);
    Assert(r.Snapshot.Errors == 1 && r.Snapshot.CopiedFiles == 0 && File.ReadAllText(Path.Combine(dest, "a.txt")) == "original target", "Locked file safety failed");
});

await Test("Read-only file and long path transfer", async () =>
{
    string longFolder = "long/source/" + string.Join('/', Enumerable.Repeat(new string('d', 50), 6));
    string file = Write(longFolder + "/readonly.txt", "long path data"); File.SetAttributes(file, FileAttributes.ReadOnly);
    string dest = Dir("long/dest");
    var r = await Run([Path.Combine(root, "long/source")], dest);
    string target = Path.Combine(dest, "source", Path.GetRelativePath(Path.Combine(root, "long/source"), file));
    Assert(r.Snapshot.CopiedFiles == 1 && File.ReadAllText(target) == "long path data", "Long path copy failed");
    File.SetAttributes(file, FileAttributes.Normal); File.SetAttributes(target, FileAttributes.Normal);
});

await Test("Pause blocks work and cancellation leaves originals and no partial files", async () =>
{
    string source = Write("pause/source/a.txt", "retain me"); string dest = Dir("pause/dest");
    using var session = new TransferSession(Dir("logs")); using var cts = new CancellationTokenSource();
    session.Pause();
    var task = TransferEngine.RunAsync(new([source], dest, Move: true), session, cts.Token);
    await Task.Delay(200);
    var paused = session.Snapshot();
    Assert(paused.Paused && paused.Workers.Count == 4, "Paused snapshot or worker slot count is incorrect");
    Assert(paused.Workers.Select(x => x.Id).SequenceEqual(Enumerable.Range(0, 4)), "Worker identities are not stable");
    Assert(paused.Workers.All(x => x.State == TransferWorkerState.Waiting && x.Source.Length == 0), "Paused workers started transfer work");
    Assert(!task.IsCompleted && !File.Exists(Path.Combine(dest, "a.txt")), "Pause did not block transfer");
    cts.Cancel(); var r = await task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert(r.Cancelled && File.Exists(source) && !Directory.EnumerateFiles(dest, "*.partial").Any(), "Cancellation safety failed");
    Assert(r.Snapshot.Workers.All(x => x.State == TransferWorkerState.Cancelled), "Cancelled worker slots did not reach a terminal state");
});

await Test("Resume completes a paused transfer", async () =>
{
    string source = Write("resume/source/a.txt", "resume me"); string dest = Dir("resume/dest");
    using var session = new TransferSession(Dir("logs")); session.Pause();
    var task = TransferEngine.RunAsync(new([source], dest), session);
    var capturedWaitingWorkers = session.Snapshot().Workers;
    await Task.Delay(100); session.Resume(); var r = await task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert(r.Snapshot.CopiedFiles == 1, "Resume failed");
    Assert(capturedWaitingWorkers.Count == 4
        && capturedWaitingWorkers.All(x => x.State == TransferWorkerState.Waiting && x.Source.Length == 0 && x.CompletedFiles == 0),
        "A previously captured worker snapshot changed with live progress");
});

await Test("300 MiB unbuffered file copy with SHA-256 verification", async () =>
{
    string source = Path.Combine(Dir("large/source"), "large.bin");
    byte[] block = new byte[1024 * 1024]; new Random(42).NextBytes(block);
    using (var stream = File.Create(source)) for (int i = 0; i < 300; i++) stream.Write(block);
    string dest = Dir("large/dest");
    var r = await Run([source], dest);
    Assert(r.Snapshot.Errors == 0 && r.Snapshot.CopiedBytes == 300L * 1024 * 1024, "Large file failed");
});

await Test("Cancel in-flight overwrite retains previous target and source", async () =>
{
    string source = Path.Combine(root, "large/source/large.bin"); string dest = Dir("cancel/dest"); Write("cancel/dest/large.bin", "previous target");
    string small = Write("cancel/source/small.txt", "complete before the large transfer is cancelled");
    using var session = new TransferSession(Dir("logs")); using var cts = new CancellationTokenSource();
    var task = TransferEngine.RunAsync(new([source, small], dest, Workers: 2, Conflict: ConflictPolicy.Overwrite, Verify: true), session, cts.Token);
    var wait = Stopwatch.StartNew();
    TransferSnapshot beforeCancel;
    do
    {
        beforeCancel = session.Snapshot();
        if (beforeCancel.InFlightBytes > 0 && beforeCancel.Workers.Any(x => x.State == TransferWorkerState.Completed)) break;
        await Task.Delay(1);
    }
    while (!task.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(5));
    Assert(!task.IsCompleted && beforeCancel.InFlightBytes > 0
        && beforeCancel.Workers.Any(x => x.State == TransferWorkerState.Completed),
        "Transfer did not expose completed and active lanes before cancellation");
    cts.Cancel(); var r = await task.WaitAsync(TimeSpan.FromSeconds(15));
    Assert(r.Cancelled && File.ReadAllText(Path.Combine(dest, "large.bin")) == "previous target" && File.Exists(source), "Cancelled overwrite lost data");
    Assert(File.ReadAllText(Path.Combine(dest, "small.txt")) == "complete before the large transfer is cancelled",
        "Cancellation lost the file completed by another lane");
    Assert(!Directory.EnumerateFiles(dest, "*.partial").Any(), "Partial file leaked");
    Assert(r.Snapshot.Workers.Any(x => x.State == TransferWorkerState.Completed && x.CompletedFiles == 1),
        "Cancellation relabeled an already completed worker lane");
    Assert(r.Snapshot.Workers.All(x => x.State is TransferWorkerState.Completed or TransferWorkerState.Skipped
        or TransferWorkerState.Failed or TransferWorkerState.Cancelled), "Active cancellation left a non-terminal worker lane");
});

int count = args.Contains("--scale") ? 300_000 : 5_000;
await Test($"Bounded-queue scale transfer of {count:N0} small files", async () =>
{
    string source = Dir("scale/source"); string dest = Dir("scale/dest");
    byte[] data = System.Text.Encoding.UTF8.GetBytes("SwiftCopy fixture · small-file content verification");
    for (int i = 0; i < count; i++)
    {
        string folder = Path.Combine(source, (i / 1000).ToString("D4"));
        if (i % 1000 == 0) Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, $"{i:D6}.bin"), data);
    }
    using var session = new TransferSession(Dir("logs"));
    var sw = Stopwatch.StartNew();
    var task = TransferEngine.RunAsync(new([source], dest, Workers: 8), session);
    bool sawMultipleWorkerSources = false;
    DateTime nextReport = DateTime.UtcNow;
    while (!task.IsCompleted)
    {
        var s = session.Snapshot();
        Assert(s.Workers.Count == 8 && s.Workers.Select(x => x.Id).SequenceEqual(Enumerable.Range(0, 8)),
            "Live worker snapshot changed slot identity");
        Assert(s.Workers.All(x => x.TotalBytes >= 0 && x.TransferredBytes >= 0 && x.TransferredBytes <= x.TotalBytes),
            "Live worker byte progress is outside its file bounds");
        if (s.Workers.Where(x => x.Source.Length > 0).Select(x => x.Source).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() == 2)
            sawMultipleWorkerSources = true;
        if (DateTime.UtcNow >= nextReport)
        {
            Console.WriteLine($"  scale: {s.CopiedFiles:N0}/{s.FoundFiles:N0}; managed memory {GC.GetTotalMemory(false) / 1024 / 1024} MiB; {sw.Elapsed.TotalSeconds:F1}s");
            nextReport = DateTime.UtcNow.AddSeconds(5);
        }
        await Task.WhenAny(task, Task.Delay(20));
    }
    var result = await task;
    Assert(result.Snapshot.CopiedFiles == count && result.Snapshot.Errors == 0, "Scale counts differ");
    Assert(result.Snapshot.Workers.Count == 8
        && result.Snapshot.Workers.Select(x => x.Id).SequenceEqual(Enumerable.Range(0, 8)), "Scale worker slots changed identity");
    Assert(result.Snapshot.Workers.Sum(x => x.CompletedFiles) == count, "Per-worker completed counts differ from the overall count");
    Assert(sawMultipleWorkerSources, "Live worker snapshots never exposed distinct file identities");
    Assert(result.Snapshot.Workers.Count(x => x.CompletedFiles > 0) > 1, "Fewer than two worker lanes completed files");
    int verified = 0;
    foreach (string path in Directory.EnumerateFiles(Path.Combine(dest, "source"), "*", SearchOption.AllDirectories))
    { Assert(File.ReadAllBytes(path).SequenceEqual(data), "Scale output content differs"); verified++; }
    Assert(verified == count, "Scale output count differs");
    results.Add($"METRIC {count:N0} files, transfer+validation {sw.Elapsed.TotalSeconds:F2}s, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MiB");
});

string report = Path.Combine(AppContext.BaseDirectory, "verification-results.txt");
if (executed == 0)
{
    failures++;
    string line = $"FAIL filter matched no checks: {filter}";
    results.Add(line);
    Console.WriteLine(line);
}
File.WriteAllLines(report, new[] { $"SwiftCopy verification · {DateTime.Now:O}", $"Workspace: {root}",
    $"Filter: {filter ?? "(none)"}", $"Checks executed: {executed}", $"Failures: {failures}" }.Concat(results));
Console.WriteLine($"\n{results.Count(x => x.StartsWith("PASS"))} passed, {failures} failed, {executed} executed. Report: {report}");
// Fixtures remain available for inspection. Only the uniquely named directory above is test-owned.
Environment.ExitCode = failures == 0 ? 0 : 1;
