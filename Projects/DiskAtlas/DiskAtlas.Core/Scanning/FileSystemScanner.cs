using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace DiskAtlas.Core.Scanning;

internal static class FileSystemScanner
{
    internal static async Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken, IEnumerable<string>? initialWarnings = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new WarningCollector(initialWarnings);
        var clusterSize = GetClusterSize(rootPath, warnings);
        var rootInfo = new DirectoryInfo(rootPath);
        var root = new ScanNode
        {
            Name = rootInfo.Name.Length == 0 ? rootPath : rootInfo.Name,
            FullPath = rootPath,
            IsDirectory = true,
            Attributes = SafeAttributes(rootInfo),
            LastWriteTimeUtc = SafeLastWrite(rootInfo)
        };

        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            warnings.Add($"Did not traverse reparse-point scan root '{rootPath}'.");
            stopwatch.Stop();
            return new ScanResult(root, [], new ScanSummary(0, 1, 1, stopwatch.Elapsed,
                "File system", true, warnings.Snapshot()));
        }

        var files = new ConcurrentBag<ScanNode>();
        long fileCount = 0, directoryCount = 1, byteCount = 0, skipped = 0;
        var pending = 1;
        long lastProgressTimestamp = 0;
        var channel = Channel.CreateUnbounded<DirectoryWork>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });
        channel.Writer.TryWrite(new DirectoryWork(rootPath, root));

        var workerCount = Math.Clamp(Environment.ProcessorCount, 2, 16);
        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            await foreach (var work in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    EnumerateDirectory(work, channel.Writer);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    Interlocked.Increment(ref skipped);
                    warnings.AddSkipped(work.Path, ex);
                }
                finally
                {
                    if (Interlocked.Decrement(ref pending) == 0)
                        channel.Writer.TryComplete();
                }
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        AssignTotals(root, cancellationToken);
        stopwatch.Stop();
        progress?.Report(new ScanProgress(fileCount, directoryCount, root.Size, rootPath,
            "File system", stopwatch.Elapsed));
        return new ScanResult(root, files.ToArray(), new ScanSummary(fileCount, directoryCount, skipped,
            stopwatch.Elapsed, "File system", true, warnings.Snapshot()));

        void EnumerateDirectory(DirectoryWork work, ChannelWriter<DirectoryWork> writer)
        {
            var enumerable = new FileSystemEnumerable<EntrySnapshot>(work.Path,
                static (ref FileSystemEntry entry) => new EntrySnapshot(entry.FileName.ToString(),
                    entry.ToFullPath(), entry.Attributes, entry.IsDirectory,
                    entry.IsDirectory ? 0 : entry.Length, entry.LastWriteTimeUtc.UtcDateTime),
                new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = 0,
                    MatchType = MatchType.Simple
                });

            foreach (var item in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isReparse = (item.Attributes & FileAttributes.ReparsePoint) != 0;
                var allocated = item.IsDirectory ? 0 : EstimateAllocation(item.Length, clusterSize);
                var node = new ScanNode
                {
                    Name = item.Name,
                    FullPath = item.FullPath,
                    IsDirectory = item.IsDirectory,
                    Size = item.Length,
                    AllocatedSize = allocated,
                    LastWriteTimeUtc = item.LastWriteTimeUtc,
                    Attributes = item.Attributes,
                    Parent = work.Node,
                    FileCount = item.IsDirectory ? 0 : 1
                };
                lock (work.Node.Children)
                    work.Node.Children.Add(node);

                if (item.IsDirectory)
                {
                    Interlocked.Increment(ref directoryCount);
                    if (!isReparse)
                    {
                        Interlocked.Increment(ref pending);
                        if (!writer.TryWrite(new DirectoryWork(item.FullPath, node)))
                        {
                            Interlocked.Decrement(ref pending);
                            Interlocked.Increment(ref skipped);
                            warnings.Add($"Skipped '{item.FullPath}': the directory work queue was closed.");
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref skipped);
                        warnings.AddReparsePoint(item.FullPath);
                    }
                }
                else
                {
                    files.Add(node);
                    Interlocked.Increment(ref fileCount);
                    Interlocked.Add(ref byteCount, item.Length);
                }

                var now = Stopwatch.GetTimestamp();
                var previous = Volatile.Read(ref lastProgressTimestamp);
                if (progress is not null && now - previous >= Stopwatch.Frequency / 8 &&
                    Interlocked.CompareExchange(ref lastProgressTimestamp, now, previous) == previous)
                    progress.Report(new ScanProgress(Volatile.Read(ref fileCount), Volatile.Read(ref directoryCount),
                        Volatile.Read(ref byteCount), item.FullPath, "File system", stopwatch.Elapsed));
            }
        }
    }

    private static long EstimateAllocation(long length, long clusterSize)
    {
        if (length <= 0)
            return 0;
        try { return checked(((length + clusterSize - 1) / clusterSize) * clusterSize); }
        catch (OverflowException) { return length; }
    }

    private static long GetClusterSize(string path, WarningCollector warnings)
    {
        var root = Path.GetPathRoot(path);
        if (root is not null && GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _) &&
            sectorsPerCluster != 0 && bytesPerSector != 0)
            return checked((long)sectorsPerCluster * bytesPerSector);
        warnings.Add("Allocated sizes are estimates using a 4 KiB cluster because volume geometry was unavailable.");
        return 4096;
    }

    private static void AssignTotals(ScanNode root, CancellationToken token)
    {
        var stack = new Stack<(ScanNode Node, bool Visited)>();
        stack.Push((root, false));
        while (stack.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            var (node, visited) = stack.Pop();
            if (!visited)
            {
                stack.Push((node, true));
                foreach (var child in node.Children)
                    if (child.IsDirectory)
                        stack.Push((child, false));
            }
            else
            {
                foreach (var child in node.Children)
                {
                    node.Size = checked(node.Size + child.Size);
                    node.AllocatedSize = checked(node.AllocatedSize + child.AllocatedSize);
                    node.FileCount = checked(node.FileCount + child.FileCount);
                }
            }
        }
    }

    private static FileAttributes SafeAttributes(FileSystemInfo info)
    {
        try { return info.Attributes; } catch { return FileAttributes.Directory; }
    }

    private static DateTime SafeLastWrite(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; } catch { return default; }
    }

    private readonly record struct DirectoryWork(string Path, ScanNode Node);
    private readonly record struct EntrySnapshot(string Name, string FullPath, FileAttributes Attributes,
        bool IsDirectory, long Length, DateTime LastWriteTimeUtc);

    internal sealed class WarningCollector
    {
        private const int Limit = 100;
        private const int ExampleLimit = 3;
        private readonly ConcurrentQueue<string> _items = new();
        private readonly WarningGroup _accessDenied = new();
        private readonly WarningGroup _ioFailures = new();
        private readonly WarningGroup _reparsePoints = new();
        private long _attempted;
        private long _omitted;

        internal WarningCollector(IEnumerable<string>? initial)
        {
            if (initial is not null)
                foreach (var item in initial)
                    Add(item);
        }

        internal void Add(string warning)
        {
            if (Interlocked.Increment(ref _attempted) <= Limit)
                _items.Enqueue(warning);
            else
                Interlocked.Increment(ref _omitted);
        }

        internal void AddSkipped(string path, Exception error)
        {
            if (error is UnauthorizedAccessException or System.Security.SecurityException)
                _accessDenied.Add($"'{path}'");
            else
                _ioFailures.Add($"'{path}' ({error.Message})");
        }

        internal void AddReparsePoint(string path) => _reparsePoints.Add($"'{path}'");

        internal IReadOnlyList<string> Snapshot()
        {
            var result = _items.ToList();
            AddGroup(result, _accessDenied,
                count => $"Skipped {count:N0} directories because access was denied.");
            AddGroup(result, _ioFailures,
                count => $"Skipped {count:N0} directories because of file-system I/O errors.");
            AddGroup(result, _reparsePoints,
                count => $"Did not traverse {count:N0} reparse-point directories (links or junctions) to avoid duplicate or cyclic traversal.");
            var omitted = Volatile.Read(ref _omitted);
            if (omitted != 0)
                result.Add($"{omitted:N0} additional warnings were omitted.");
            result.Add("Allocated sizes are cluster-rounded estimates in file-system mode; sparse and compressed files may differ.");
            result.Add("File-system mode may count hard-linked files more than once and does not include alternate data streams.");
            return result;
        }

        private static void AddGroup(List<string> result, WarningGroup group, Func<long, string> summary)
        {
            var count = group.Count;
            if (count == 0)
                return;
            var examples = group.Examples;
            var remainder = count - examples.Count;
            var suffix = examples.Count == 0 ? "" : $" Examples: {string.Join("; ", examples)}" +
                (remainder == 0 ? "." : $" (+{remainder:N0} more).");
            result.Add(summary(count) + suffix);
        }

        private sealed class WarningGroup
        {
            private readonly ConcurrentQueue<string> _examples = new();
            private long _count;
            private int _exampleSlots;

            internal long Count => Volatile.Read(ref _count);
            internal IReadOnlyList<string> Examples => _examples.ToList();

            internal void Add(string example)
            {
                Interlocked.Increment(ref _count);
                if (Interlocked.Increment(ref _exampleSlots) <= ExampleLimit)
                    _examples.Enqueue(example);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string rootPathName, out uint sectorsPerCluster,
        out uint bytesPerSector, out uint numberOfFreeClusters, out uint totalNumberOfClusters);
}
