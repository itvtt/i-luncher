namespace TerminalNotes.Services;

/// <summary>Safe, structured status for the local OpenCode recovery monitor.</summary>
public sealed record OpenCodeAutoResumeDiagnostic(
    DateTimeOffset Timestamp,
    string Code,
    string Phase,
    string Reason,
    string? SessionId,
    string? MessageId,
    DateTimeOffset? LastSubstantiveProgressAt,
    string? ErrorCode = null,
    string? ExceptionType = null,
    bool RequiresUserAction = false);
