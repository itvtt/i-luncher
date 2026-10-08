namespace TerminalNotes.Services;

/// <summary>Sanitized state for one native lifecycle operation in a ConPTY session.</summary>
public sealed record ConPtyLifecycleEvent(
    DateTimeOffset Timestamp,
    string Operation,
    string Phase,
    string? ExceptionType);
