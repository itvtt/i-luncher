using System.Diagnostics;

namespace SwiftCopy.Core;

public enum ConflictPolicy { Skip, Overwrite, KeepBoth }
public enum TransferWorkerState { Waiting, Copying, Verifying, Moving, Completed, Skipped, Failed, Cancelled }

public sealed record TransferWorkerSnapshot(int Id, string Source, string Destination,
    long TotalBytes, long TransferredBytes, long CompletedFiles, TransferWorkerState State);

public sealed record TransferOptions(IReadOnlyList<string> Sources, string Destination,
    int Workers = 4, ConflictPolicy Conflict = ConflictPolicy.Skip, bool Move = false,
    bool Verify = false, bool UnbufferedLargeFiles = true);

public sealed record TransferSnapshot(long FoundFiles, long FoundBytes, long CopiedFiles,
    long CopiedBytes, long Skipped, long Errors, long ResolvedBytes, long InFlightBytes,
    bool ScanComplete, bool Paused, string CurrentFile, TimeSpan Elapsed, string LogPath, long MovedDirectories)
{
    public IReadOnlyList<TransferWorkerSnapshot> Workers { get; init; } = [];
}

public sealed record TransferResult(TransferSnapshot Snapshot, bool Cancelled);

public sealed class TransferSession : IDisposable
{
    internal readonly ManualResetEventSlim Gate = new(true);
    internal readonly Stopwatch Watch = Stopwatch.StartNew();
    internal long FoundFiles, FoundBytes, CopiedFiles, CopiedBytes, Skipped, Errors, ResolvedBytes, InFlightBytes;
    internal long MovedDirectories;
    internal volatile bool ScanComplete;
    internal volatile string CurrentFile = "";
    private readonly object logLock = new();
    private readonly StreamWriter log;
    private TransferWorkerSnapshot[] workerSlots = [];
    public string LogPath { get; }
    public TransferSession(string? logDirectory = null)
    {
        logDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftCopy", "Logs");
        Directory.CreateDirectory(logDirectory);
        LogPath = Path.Combine(logDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        log = new StreamWriter(LogPath) { AutoFlush = true };
    }
    public void Pause() { Gate.Reset(); Watch.Stop(); }
    public void Resume() { Gate.Set(); Watch.Start(); }
    internal void Wait(CancellationToken token) => Gate.Wait(token);
    internal void Log(string status, string path, string detail = "")
    {
        lock (logLock) log.WriteLine($"{DateTime.Now:O}\t{status}\t{path}\t{detail.Replace('\r', ' ').Replace('\n', ' ')}");
    }
    internal void Error(string path, Exception ex) { Interlocked.Increment(ref Errors); Log("ERROR", path, ex.Message); }
    internal void FinishLog() { lock (logLock) log.Dispose(); }

    internal void InitializeWorkers(int count)
    {
        var initialized = new TransferWorkerSnapshot[count];
        for (int id = 0; id < initialized.Length; id++)
            initialized[id] = new(id, "", "", 0, 0, 0, TransferWorkerState.Waiting);
        Volatile.Write(ref workerSlots, initialized);
    }

    internal void BeginWorker(int id, string source, string destination, long totalBytes)
    {
        var slots = Volatile.Read(ref workerSlots);
        var previous = Volatile.Read(ref slots[id]);
        Volatile.Write(ref slots[id], new(id, source, destination, totalBytes, 0,
            previous.CompletedFiles, TransferWorkerState.Waiting));
        CurrentFile = source;
    }

    internal void SetWorkerState(int id, TransferWorkerState state)
    {
        var slots = Volatile.Read(ref workerSlots);
        var previous = Volatile.Read(ref slots[id]);
        if (previous.State == state) return;
        Volatile.Write(ref slots[id], previous with { State = state });
    }

    internal void SetWorkerTotal(int id, long totalBytes)
    {
        var slots = Volatile.Read(ref workerSlots);
        var previous = Volatile.Read(ref slots[id]);
        if (previous.TotalBytes == totalBytes) return;
        Volatile.Write(ref slots[id], previous with
        {
            TotalBytes = totalBytes,
            TransferredBytes = Math.Min(previous.TransferredBytes, totalBytes)
        });
    }

    internal void SetWorkerDestination(int id, string destination)
    {
        var slots = Volatile.Read(ref workerSlots);
        var previous = Volatile.Read(ref slots[id]);
        if (previous.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)) return;
        Volatile.Write(ref slots[id], previous with { Destination = destination });
    }

    internal void SetWorkerProgress(int id, long transferredBytes)
    {
        var slots = Volatile.Read(ref workerSlots);
        var previous = Volatile.Read(ref slots[id]);
        long progress = Math.Max(0, Math.Min(previous.TotalBytes, transferredBytes));
        if (previous.TransferredBytes == progress) return;
        Volatile.Write(ref slots[id], previous with { TransferredBytes = progress });
    }

    internal void FinishWorkerFile(int id, TransferWorkerState state)
    {
        var slots = Volatile.Read(ref workerSlots);
        var previous = Volatile.Read(ref slots[id]);
        bool completed = state == TransferWorkerState.Completed;
        Volatile.Write(ref slots[id], previous with
        {
            TransferredBytes = completed ? previous.TotalBytes : previous.TransferredBytes,
            CompletedFiles = previous.CompletedFiles + (completed ? 1 : 0),
            State = state
        });
    }

    private IReadOnlyList<TransferWorkerSnapshot> SnapshotWorkers()
    {
        var slots = Volatile.Read(ref workerSlots);
        var copy = new TransferWorkerSnapshot[slots.Length];
        for (int id = 0; id < slots.Length; id++) copy[id] = Volatile.Read(ref slots[id]);
        return Array.AsReadOnly(copy);
    }

    internal void CancelUnfinishedWorkers()
    {
        var slots = Volatile.Read(ref workerSlots);
        for (int id = 0; id < slots.Length; id++)
        {
            var previous = Volatile.Read(ref slots[id]);
            if (previous.State is TransferWorkerState.Waiting or TransferWorkerState.Copying
                or TransferWorkerState.Verifying or TransferWorkerState.Moving)
                Volatile.Write(ref slots[id], previous with { State = TransferWorkerState.Cancelled });
        }
    }

    public TransferSnapshot Snapshot() => new(
        Interlocked.Read(ref FoundFiles), Interlocked.Read(ref FoundBytes),
        Interlocked.Read(ref CopiedFiles), Interlocked.Read(ref CopiedBytes),
        Interlocked.Read(ref Skipped), Interlocked.Read(ref Errors),
        Interlocked.Read(ref ResolvedBytes), Interlocked.Read(ref InFlightBytes),
        ScanComplete, !Gate.IsSet, CurrentFile, Watch.Elapsed, LogPath, Interlocked.Read(ref MovedDirectories))
        { Workers = SnapshotWorkers() };
    public void Dispose() { log.Dispose(); Gate.Dispose(); }
}
