using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FolderCompareWPF;

public partial class MainWindow
{
    private async Task RunComparisonPipelineAsync(string dirA, string dirB, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = stop.Token;
        var ready = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
        var presence = new ConcurrentDictionary<string, (int Sides, bool Small)>(RelativePathComparer);
        var completed = new ConcurrentQueue<CommonFileComparisonResult>();
        var scanClock = Stopwatch.StartNew();
        TimeSpan actualScanTime = TimeSpan.Zero;

        void Found(string relativePath, int side, long length)
        {
            // Exactly one callback observes the transition from one side to both sides.
            // Queue the file immediately, before the rest of either tree has been scanned.
            var both = presence.AddOrUpdate(relativePath, static (_, value) => value,
                static (_, previous, value) => (previous.Sides | value.Sides, previous.Small && value.Small),
                (Sides: side, Small: length <= FilePairComparer.SmallFileBytes));
            if (both.Sides == 3) ready.Writer.TryWrite(relativePath);
        }

        Task<PreparedComparison> scanTask = Task.Run(async () =>
        {
            try
            {
                var scanA = Task.Run(() => GetAllFiles(dirA, token,
                    () => Interlocked.Increment(ref _discoveredFileCountA), (rel, size) => Found(rel, 1, size)), token);
                var scanB = Task.Run(() => GetAllFiles(dirB, token,
                    () => Interlocked.Increment(ref _discoveredFileCountB), (rel, size) => Found(rel, 2, size)), token);
                await Task.WhenAll(scanA, scanB).ConfigureAwait(false);
                var prepared = new PreparedComparison
                {
                    OnlyInA = presence.Where(p => p.Value.Sides == 1).Select(p => p.Key).OrderBy(p => p, RelativePathComparer).ToList(),
                    OnlyInB = presence.Where(p => p.Value.Sides == 2).Select(p => p.Key).OrderBy(p => p, RelativePathComparer).ToList(),
                    Common = presence.Where(p => p.Value.Sides == 3).Select(p => p.Key).ToList(),
                    SkippedPathsA = scanA.Result.SkippedPathCount,
                    SkippedPathsB = scanB.Result.SkippedPathCount
                };
                actualScanTime = scanClock.Elapsed;
                ready.Writer.TryComplete();
                return prepared;
            }
            catch (Exception ex) { ready.Writer.TryComplete(ex); throw; }
        }, token);

        Task readTask = Task.Run(() => ComparisonWorkers.RunAsync(ready.Reader.ReadAllAsync(token), async (rel, ct) =>
        {
            try
            {
                var comparison = await FilePairComparer.CompareDiscoveredAsync(Path.Combine(dirA, rel), Path.Combine(dirB, rel),
                    presence[rel].Small, _ignoreWhitespace, ct).ConfigureAwait(false);
                completed.Enqueue(new CommonFileComparisonResult
                {
                    RelPath = rel,
                    IsIdentical = comparison.Kind == FileContentComparisonKind.Identical,
                    IsBinary = comparison.Kind == FileContentComparisonKind.BinaryDifferent
                });
            }
            catch (Exception ex) when (IsSkippableFileSystemException(ex))
            { completed.Enqueue(new CommonFileComparisonResult { RelPath = rel, ErrorMessage = ex.Message }); }
        }, token), token);

        try
        {
            bool scanApplied = false;
            while (!scanApplied || !readTask.IsCompleted || !completed.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                if (!scanApplied && scanTask.IsCompleted)
                {
                    var prepared = await scanTask;
                    _scanElapsed = actualScanTime;
                    _lastResult!.OnlyInA.AddRange(prepared.OnlyInA);
                    _lastResult.OnlyInB.AddRange(prepared.OnlyInB);
                    _totalFileCount = prepared.OnlyInA.Count + prepared.OnlyInB.Count + prepared.Common.Count;
                    _processedFileCount += prepared.OnlyInA.Count + prepared.OnlyInB.Count;
                    _skippedPathCount = prepared.SkippedPathsA + prepared.SkippedPathsB;
                    _deferTreeMaterialization = _totalFileCount >= 10000;
                    await AppendInitialResultsAsync(_lastResult, token);
                    scanApplied = true;
                }
                else if (!scanApplied && Math.Max(Volatile.Read(ref _discoveredFileCountA), Volatile.Read(ref _discoveredFileCountB)) >= 10000)
                    _deferTreeMaterialization = true;

                DrainCompletedComparisons(_lastResult!, completed, maxItems: 4096);
                if (scanApplied) UpdateComparisonProgress(isComplete: false);
                else
                    TxtCompareProgress.Text = $"검색·비교 동시 진행 · {FilePairComparer.FileConcurrency}개 병렬\nA {Volatile.Read(ref _discoveredFileCountA):N0}개 · B {Volatile.Read(ref _discoveredFileCountB):N0}개\n비교 {_processedFileCount:N0}개 완료 · {_comparisonElapsed.Elapsed.TotalSeconds:F1}초";
                if (!completed.IsEmpty) await Dispatcher.Yield(DispatcherPriority.Background);
                else if (!scanApplied || !readTask.IsCompleted) await Task.Delay(15, token);
            }
            await readTask;
            UpdateComparisonProgress(isComplete: true);
        }
        catch
        {
            stop.Cancel();
            ready.Writer.TryComplete();
            // Observe both tasks before the next comparison can reuse the window's state.
            try { await Task.WhenAll(scanTask, readTask); } catch { }
            throw;
        }
    }
}
