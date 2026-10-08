using System.Text.Json;
using TerminalNotes.Services;

string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes diagnostics " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
Guid run = Guid.NewGuid(), dispatch = Guid.NewGuid();
string path = AutomationDiagnostics.LogPath(fixture, run);
var entry = new AutomationDiagnosticEvent(DateTimeOffset.UtcNow, "stage_start_failed", "error", "준비 실패\n상세 원인",
    "Coordinator", "Planning", run, dispatch)
{
    Revision = 4,
    Iteration = 1,
    ProjectDirectory = fixture,
    LastSubstantiveProgressAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    RequiresUserAction = true
};
await AutomationDiagnostics.AppendAsync(path, entry);
await Task.WhenAll(Enumerable.Range(0, 50).Select(index => AutomationDiagnostics.AppendAsync(path,
    entry with { EventName = "concurrent", Detail = "상태 " + index, Sequence = index })));
string[] lines = await File.ReadAllLinesAsync(path);
Check(lines.Length == 51, "append preserves prior failure and concurrent complete JSONL records");
foreach (string line in lines)
{
    using var json = JsonDocument.Parse(line);
    Check(json.RootElement.GetProperty("runId").GetGuid() == run &&
        json.RootElement.GetProperty("dispatchToken").GetGuid() == dispatch, "exact run/dispatch identity");
}
using (var json = JsonDocument.Parse(lines[0]))
{
    Check(json.RootElement.GetProperty("detail").GetString() == entry.Detail, "multiline Korean reason retained in one escaped line");
    Check(json.RootElement.GetProperty("buildId").GetString()!.Length == 32, "build identity recorded");
    Check(json.RootElement.GetProperty("lastSubstantiveProgressAt").GetDateTimeOffset() == entry.LastSubstantiveProgressAt,
        "last substantive progress timestamp is persisted structurally");
    Check(json.RootElement.GetProperty("requiresUserAction").GetBoolean() &&
        json.RootElement.GetProperty("projectDirectory").GetString() == fixture,
        "actionable loop status is tied to its project");
}
string privateDetail = "Authorization: Bearer bearerValue api_key='keyValue' password=passValue " +
    "https://user:credValue@example.com/path?token=queryValue \"secret\":\"secretValue\" OPENCODE_SERVER_PASSWORD=serverValue";
string safe = AutomationDiagnostics.Sanitize(privateDetail);
Check(new[] { "bearerValue", "keyValue", "passValue", "credValue", "queryValue", "secretValue", "serverValue" }
    .All(secret => !safe.Contains(secret, StringComparison.Ordinal)), "credential/header/URL secrets redacted");
await AutomationDiagnostics.AppendAsync(path, entry with { Detail = privateDetail });
Check(!(await File.ReadAllTextAsync(path)).Contains("bearerValue"), "redaction enforced at persistent boundary");
await AutomationDiagnostics.AppendAsync(path, entry with
{
    Pane = new("Unknown", false, true, false, false, true, false, false, false, false, false, false)
        { ConnectionFailure = "password=nativeSecret" }
});
Check(!(await File.ReadAllTextAsync(path)).Contains("nativeSecret"), "nested connection failure metadata is redacted");
string invalidParent = Path.Combine(fixture, "not-a-directory");
await File.WriteAllTextAsync(invalidParent, "preserve");
bool failure = false;
try { await AutomationDiagnostics.AppendAsync(Path.Combine(invalidParent, "diagnostics.jsonl"), entry); }
catch (IOException) { failure = true; }
Check(failure && await File.ReadAllTextAsync(invalidParent) == "preserve", "write failure reported and existing data untouched");
Console.WriteLine("PASS diagnostics append, identity, redaction, concurrent writes and I/O failure; fixture: " + fixture);

static void Check(bool value, string label)
{
    if (!value) throw new Exception(label);
}
