namespace DiskAtlas.Core.Optimization;

public enum OptimizationCategory
{
    UserTemporary,
    WindowsTemporary,
    CrashDumps,
    ErrorReports
}

public enum OptimizationItemStatus
{
    Candidate,
    Deleted,
    Skipped,
    Failed
}

public sealed record OptimizationOptions
{
    public bool IncludeTemporarySubfolders { get; init; } = true;
    public bool IncludeCrashDumps { get; init; }
    public bool IncludeErrorReports { get; init; }
}

public sealed record OptimizationItemResult
{
    public string Path { get; init; } = string.Empty;
    public OptimizationCategory Category { get; init; }
    public long SizeBytes { get; init; }
    public DateTime? LastWriteTimeUtc { get; init; }
    public OptimizationItemStatus Status { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed record OptimizationProgress
{
    public string Stage { get; init; } = string.Empty;
    public string CurrentPath { get; init; } = string.Empty;
    public int ExaminedFiles { get; init; }
    public int DeletedFiles { get; init; }
    public long DeletedBytes { get; init; }
}

public sealed record OptimizationReport
{
    public IReadOnlyList<OptimizationItemResult> Items { get; init; } = Array.Empty<OptimizationItemResult>();
    public bool IsPreview { get; init; }
    public bool IsCanceled { get; init; }
    public int CandidateCount { get; init; }
    public long CandidateBytes { get; init; }
    public int DeletedCount { get; init; }
    public long DeletedBytes { get; init; }
    public int SkippedCount { get; init; }
    public int FailedCount { get; init; }
}
