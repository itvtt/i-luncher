using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace DiskAtlas.Core;

public sealed class DuplicateFinder
{
    private const int MaximumConcurrency = 4;

    public Task<DuplicateScanResult> FindAsync(
        IEnumerable<ScanNode> files,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Task.Run(() => FindCoreAsync(files, progress, cancellationToken), cancellationToken);
    }

    private static async Task<DuplicateScanResult> FindCoreAsync(
        IEnumerable<ScanNode> files,
        IProgress<DuplicateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var candidateList = new List<FrozenCandidate>();
        foreach (ScanNode node in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateList.Add(Freeze(node));
        }
        FrozenCandidate[] candidates = candidateList.ToArray();

        long processed = 0;
        long processedBytes = 0;
        long skipped = 0;
        var metadataReporter = new ProgressReporter(progress, candidates.LongLength);
        var measured = new ConcurrentBag<MeasuredCandidate>();

        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaximumConcurrency,
            CancellationToken = cancellationToken
        }, (candidate, token) =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (candidate.IsDirectory || candidate.Excluded)
                    throw new UnauthorizedAccessException();

                using var file = WindowsFileInspector.OpenEligible(candidate.FullPath, FileShare.Read);
                measured.Add(new MeasuredCandidate(candidate.Node, candidate.FullPath, file.Snapshot));
            }
            catch (Exception error) when (IsFileFailure(error))
            {
                Interlocked.Increment(ref skipped);
            }

            long done = Interlocked.Increment(ref processed);
            metadataReporter.Report(done, Volatile.Read(ref processedBytes), candidate.FullPath, "파일 정보 확인");
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        MeasuredCandidate[] hashCandidates = measured
            .GroupBy(item => item.Snapshot.Identity, StringComparer.Ordinal)
            .Select(group => group.First())
            .GroupBy(item => item.Snapshot.Size)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        var hashed = new ConcurrentBag<DuplicateFile>();
        var hashReporter = new ProgressReporter(progress, hashCandidates.LongLength);
        processed = 0;
        processedBytes = 0;

        await Parallel.ForEachAsync(hashCandidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaximumConcurrency,
            CancellationToken = cancellationToken
        }, async (candidate, token) =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var file = WindowsFileInspector.OpenEligible(candidate.FullPath, FileShare.Read);
                string hash = await WindowsFileInspector.ComputeSha256Async(file.Stream, token).ConfigureAwait(false);
                WindowsFileInspector.Snapshot current = WindowsFileInspector.RefreshSnapshot(file);
                hashed.Add(new DuplicateFile(
                    candidate.Node,
                    candidate.FullPath,
                    current.Size,
                    current.LastWriteTimeUtc,
                    hash,
                    current.Identity));
                Interlocked.Add(ref processedBytes, current.Size);
            }
            catch (Exception error) when (IsFileFailure(error))
            {
                Interlocked.Increment(ref skipped);
            }

            long done = Interlocked.Increment(ref processed);
            hashReporter.Report(done, Volatile.Read(ref processedBytes), candidate.FullPath, "내용 비교");
        }).ConfigureAwait(false);

        DuplicateGroup[] groups = hashed
            .GroupBy(file => file.Identity, StringComparer.Ordinal)
            .Select(group => group.First())
            .GroupBy(file => (file.Size, file.ContentHash))
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                DuplicateFile[] ordered = group.OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
                return new DuplicateGroup($"{group.Key.Size}:{group.Key.ContentHash}", ordered);
            })
            .OrderByDescending(group => group.ReclaimableBytes)
            .ThenBy(group => group.Id, StringComparer.Ordinal)
            .ToArray();

        if (hashCandidates.Length > 0)
            hashReporter.Force(processed, processedBytes, string.Empty, "완료");
        else
            metadataReporter.Force(candidates.LongLength, 0, string.Empty, "완료");
        return new DuplicateScanResult(groups, skipped, timer.Elapsed);
    }

    private static FrozenCandidate Freeze(ScanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string path = node.FullPath;
        bool isDirectory = node.IsDirectory;
        bool excluded;
        try
        {
            excluded = IsExcludedByFrozenScanPolicy(node, path);
        }
        catch
        {
            excluded = true;
        }
        return new FrozenCandidate(node, path, isDirectory, excluded);
    }

    private static bool IsExcludedByFrozenScanPolicy(ScanNode node, string frozenPath)
    {
        var probe = new ScanNode
        {
            FullPath = frozenPath,
            Name = node.Name,
            IsDirectory = node.IsDirectory,
            Attributes = node.Attributes
        };
        if (SearchPolicy.IsExcluded(probe))
            return true;

        ScanNode? parent = node.Parent;
        const int maximumParentDepth = 4096;
        for (int depth = 0; parent is not null && depth < maximumParentDepth; depth++, parent = parent.Parent)
        {
            string parentPath = parent.FullPath;
            FileAttributes attributes = parent.Attributes;
            if (IsVolumeRoot(parentPath))
                attributes &= ~FileAttributes.System;

            probe.FullPath = parentPath;
            probe.Name = parent.Name;
            probe.IsDirectory = true;
            probe.Attributes = attributes;
            if (SearchPolicy.IsExcluded(probe))
                return true;
        }
        return parent is not null;
    }

    private static bool IsVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) &&
                StringComparer.OrdinalIgnoreCase.Equals(
                    path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            Win32Exception or ArgumentException or NotSupportedException;

    private sealed record FrozenCandidate(ScanNode Node, string FullPath, bool IsDirectory, bool Excluded);
    private sealed record MeasuredCandidate(ScanNode Node, string FullPath, WindowsFileInspector.Snapshot Snapshot);

    private sealed class ProgressReporter(IProgress<DuplicateProgress>? progress, long totalFiles)
    {
        private readonly object _gate = new();
        private long _lastTimestamp;

        internal void Report(long processedFiles, long processedBytes, string path, string stage)
        {
            if (progress is null)
                return;
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (_lastTimestamp != 0 && Stopwatch.GetElapsedTime(_lastTimestamp, now) < TimeSpan.FromMilliseconds(100))
                    return;
                _lastTimestamp = now;
            }
            progress.Report(new DuplicateProgress(processedFiles, totalFiles, processedBytes, path, stage));
        }

        internal void Force(long processedFiles, long processedBytes, string path, string stage) =>
            progress?.Report(new DuplicateProgress(processedFiles, totalFiles, processedBytes, path, stage));
    }
}
