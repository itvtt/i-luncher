using System.Threading;
using System.Threading.Tasks;

namespace FolderCompareWPF;

internal static class ComparisonWorkers
{
    public static Task RunAsync(IAsyncEnumerable<string> paths,
        Func<string, CancellationToken, ValueTask> compare,
        CancellationToken cancellationToken, int concurrency = FilePairComparer.FileConcurrency) =>
        Parallel.ForEachAsync(paths, new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = cancellationToken
        }, compare);
}
