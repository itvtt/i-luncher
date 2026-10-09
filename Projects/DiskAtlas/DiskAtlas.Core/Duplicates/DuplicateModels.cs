namespace DiskAtlas.Core;

public sealed record DuplicateFile(
    ScanNode Node,
    string FullPath,
    long Size,
    DateTime LastWriteTimeUtc,
    string ContentHash,
    string Identity);

public sealed record DuplicateGroup(string Id, IReadOnlyList<DuplicateFile> Files)
{
    public long ReclaimableBytes => Files.Count <= 1 ? 0 : checked(Files[0].Size * (Files.Count - 1L));
}

public sealed record DuplicateProgress(
    long ProcessedFiles,
    long TotalFiles,
    long ProcessedBytes,
    string CurrentPath,
    string Stage);

public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateGroup> Groups,
    long SkippedFiles,
    TimeSpan Elapsed);

public sealed record DuplicateDeleteResult(
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyDictionary<string, string> Failures);

public interface IFileRecycler
{
    Task RecycleAsync(
        string path,
        Action validateImmediatelyBeforeRecycle,
        CancellationToken cancellationToken);
}
