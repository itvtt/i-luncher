namespace TerminalNotes.TerminalTransport;

public sealed record TerminalProcessExit(int? ExitCode, string? Error = null);
