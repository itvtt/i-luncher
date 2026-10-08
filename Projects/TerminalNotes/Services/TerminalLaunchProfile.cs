namespace TerminalNotes.Services;

public enum TerminalTool
{
    // Keep these numeric values stable so settings written by older releases
    // can still be deserialized before their profiles are normalized.
    Default = 0,
    Codex = 1,
    ClaudeCode = 2,
    PowerShell = 3,
    OpenCode = 4,
    Custom = 5
}

public sealed record TerminalLaunchProfile(
    int Slot,
    TerminalTool Tool = TerminalTool.Default,
    string? Title = null,
    string? Command = null,
    string? WorkingDirectory = null,
    TerminalPermissionMode Mode = TerminalPermissionMode.Standard);
