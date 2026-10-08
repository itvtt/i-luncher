namespace TerminalNotes.TerminalTransport;

public sealed record TerminalProcessOptions
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public required string WorkingDirectory { get; init; }
    public int Columns { get; init; } = 80;
    public int Rows { get; init; } = 24;
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public string? UnixHelperPath { get; init; }
}
