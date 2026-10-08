using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerminalNotes.Services;

/// <summary>Operational metadata only: never terminal input, prompts, credentials or HTTP bodies.</summary>
public sealed record AutomationDiagnosticEvent(
    DateTimeOffset Timestamp, string EventName, string Severity, string Detail,
    string? Role = null, string? Phase = null, Guid? RunId = null, Guid? DispatchToken = null)
{
    public int SchemaVersion { get; init; } = 1;
    public long Sequence { get; init; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public string BuildId { get; init; } = typeof(AutomationDiagnostics).Module.ModuleVersionId.ToString("N");
    public long? Revision { get; init; }
    public int? Iteration { get; init; }
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public string? ExceptionType { get; init; }
    public string? ErrorCode { get; init; }
    public string? ProjectDirectory { get; init; }
    public DateTimeOffset? LastSubstantiveProgressAt { get; init; }
    public bool? RequiresUserAction { get; init; }
    public AutomationPaneDiagnostic? Pane { get; init; }
}

public sealed record AutomationPaneDiagnostic(
    string NativeStatus, bool IsReady, bool IsRunning, bool CanStart,
    bool AutomationActive, bool HasEndpoint, bool HasPendingInput,
    bool InputBlocked, bool ManualOverride, bool Failed, bool Disposed, bool Cancelled)
{
    public string? ConnectionFailure { get; init; }
}

public static class AutomationDiagnostics
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex Credentials = new(
        "(?i)([\\\"']?(?:authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|password|secret)[\\\"']?\\s*[:=]\\s*)(?:[\\\"'][^\\\"']*[\\\"']|[^\\s,;]+)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Bearer = new("(?i)\\b(Bearer|Basic)\\s+[A-Za-z0-9+/=_\\-.]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex UrlCredentials = new("(?i)(https?://)[^\\s/@]+:[^\\s/@]+@", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex UrlQuery = new("(?i)(https?://[^\\s?#]+)[?#][^\\s]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string LogPath(string project, Guid runId) =>
        Path.Combine(Path.GetFullPath(project), "terminal-notes", "runs", runId.ToString("N"), "diagnostics.jsonl");

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string text = value.Length > 4096 ? value[..4096] + "…" : value;
        try
        {
            text = Bearer.Replace(text, "$1 [redacted]");
            text = Credentials.Replace(text, "$1[redacted]");
            text = UrlCredentials.Replace(text, "$1[redacted]@");
            return UrlQuery.Replace(text, "$1?[redacted]");
        }
        catch (RegexMatchTimeoutException) { return "[diagnostic detail omitted: redaction timeout]"; }
    }

    public static async Task AppendAsync(string path, AutomationDiagnosticEvent entry)
    {
        // Serialize writes and flush every line; a failed diagnostic must be surfaced
        // by its host, but must never cause a native prompt to be submitted again.
        await WriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string line = JsonSerializer.Serialize(entry with
            {
                Detail = Sanitize(entry.Detail),
                Pane = entry.Pane is { } pane ? pane with { ConnectionFailure = Sanitize(pane.ConnectionFailure) } : null
            }, JsonOptions) + "\n";
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line)).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        finally { WriteGate.Release(); }
    }
}
