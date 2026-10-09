namespace DiskAtlas.Core;

public sealed class ScanNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public long AllocatedSize { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public FileAttributes Attributes { get; set; }
    public ScanNode? Parent { get; set; }
    public List<ScanNode> Children { get; } = [];
    public long FileCount { get; set; }
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();
}

public sealed record ScanProgress(long Files, long Directories, long Bytes, string CurrentPath, string Mode, TimeSpan Elapsed);
public sealed record ScanSummary(long Files, long Directories, long Skipped, TimeSpan Elapsed,
    string Mode, bool AllocationIsEstimated, IReadOnlyList<string> Warnings);
public sealed record ScanResult(ScanNode Root, IReadOnlyList<ScanNode> Files, ScanSummary Summary);

public enum SearchMode { FileName, Contents, NameOrContents }
public sealed record SearchOptions(string Query, SearchMode Mode = SearchMode.NameOrContents,
    bool MatchCase = false, int MaxResults = 10000);
public sealed record SearchHit(ScanNode Node, string MatchKind, string Preview, long LineNumber = 0);
public sealed record SearchProgress(long Examined, long Total, long Matches, long Skipped);
public sealed record SearchReport(IReadOnlyList<SearchHit> Hits, long Examined, long Excluded,
    long Skipped, bool LimitReached, TimeSpan Elapsed);
