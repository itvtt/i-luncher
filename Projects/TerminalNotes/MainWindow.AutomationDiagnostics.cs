using System.IO;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private readonly Dictionary<Guid, List<AutomationDiagnosticEvent>> _automationProgressEvents = [];
    private readonly Dictionary<Guid, string> _automationDiagnosticErrors = [];
    private readonly Dictionary<string, DateTimeOffset> _automationDiagnosticLastWritten = [];
    private Task _automationDiagnosticWrites = Task.CompletedTask;
    private long _automationDiagnosticSequence;

    private static string NativeAutomationStatusDetail(OpenCodeStatusKind status) => status switch
    {
        OpenCodeStatusKind.Busy => "OpenCode busy · 실제 응답 변화 별도 확인",
        OpenCodeStatusKind.NeedsInput => "OpenCode 승인·질문 응답 대기",
        OpenCodeStatusKind.Pending => "OpenCode 요청 전달·응답 대기",
        OpenCodeStatusKind.Idle => "OpenCode 대기 · 단계 결과 확인 중",
        _ => "OpenCode 상태 확인 불가 · 연결 재확인 중"
    };

    private void RecordAutomationDiagnostic(string eventName, string detail,
        AutomationStepClaim? claim = null, AutomationRun? run = null, TerminalPane? pane = null,
        string severity = "info", Exception? exception = null, OpenCodeAutomationDispatch? dispatch = null,
        bool deduplicate = false, string? projectDirectory = null,
        DateTimeOffset? lastSubstantiveProgressAt = null, bool? requiresUserAction = null,
        string? sessionId = null, string? messageId = null, string? exceptionType = null, string? errorCode = null,
        string? phase = null, DateTimeOffset? timestamp = null)
    {
        Guid? runId = claim?.RunId ?? run?.Id;
        string? project = claim?.ProjectDirectory ?? run?.ProjectDirectory;
        DateTimeOffset now = timestamp ?? DateTimeOffset.UtcNow;
        string safeDetail = AutomationDiagnostics.Sanitize(detail);
        if (deduplicate)
        {
            string key = $"{projectDirectory ?? project}/{sessionId}/{messageId}/{runId}/{claim?.DispatchToken}/{eventName}/{phase ?? (claim?.WorkflowPhase ?? run?.WorkflowPhase)?.ToString()}/{safeDetail}";
            if (_automationDiagnosticLastWritten.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromSeconds(30)) return;
            _automationDiagnosticLastWritten[key] = now;
            foreach (string expired in _automationDiagnosticLastWritten.Where(item => now - item.Value > TimeSpan.FromMinutes(2)).Select(item => item.Key).ToArray())
                _automationDiagnosticLastWritten.Remove(expired);
        }
        var entry = new AutomationDiagnosticEvent(now, eventName, severity, safeDetail,
            (claim?.Role ?? run?.NextRole)?.ToString(), phase ?? (claim?.WorkflowPhase ?? run?.WorkflowPhase)?.ToString(),
            runId, claim?.DispatchToken ?? run?.ActiveDispatchToken)
        {
            Sequence = ++_automationDiagnosticSequence,
            Revision = claim?.Revision ?? run?.Revision,
            Iteration = claim?.Iteration ?? run?.Iteration,
            SessionId = dispatch?.SessionId ?? sessionId, MessageId = dispatch?.MessageId ?? messageId,
            ExceptionType = exception?.GetType().FullName ?? exceptionType,
            ErrorCode = (exception as OpenCodeAutomationException)?.Code ?? errorCode,
            ProjectDirectory = projectDirectory is null ? null : Path.GetFullPath(projectDirectory),
            LastSubstantiveProgressAt = lastSubstantiveProgressAt,
            RequiresUserAction = requiresUserAction,
            Pane = pane?.NativeAutomationDiagnostic
        };
        if (runId is Guid id)
        {
            if (!_automationProgressEvents.TryGetValue(id, out var events)) _automationProgressEvents[id] = events = [];
            events.Add(entry);
            // Only the on-screen cache is bounded. The full history stays in the log.
            if (events.Count > 80) events.RemoveRange(0, events.Count - 80);
        }
        string path = project is not null && runId is Guid logId
            ? AutomationDiagnostics.LogPath(project, logId)
            : _settingsPath + ".automation-diagnostics.jsonl";
        Task previousWrite = _automationDiagnosticWrites;
        _automationDiagnosticWrites = WriteDiagnosticAsync(previousWrite, path, entry);
        // Normal explicit exit already drains this collection before releasing panes.
        TrackJournalWork(_automationDiagnosticWrites);
    }

    private async Task WriteDiagnosticAsync(Task previous, string path, AutomationDiagnosticEvent entry)
    {
        await previous;
        try
        {
            await AutomationDiagnostics.AppendAsync(path, entry);
            if (entry.RunId is Guid id) _automationDiagnosticErrors.Remove(id);
        }
        catch (Exception ex)
        {
            string reason = "진단 로그 저장 실패 · " + AutomationDiagnostics.Sanitize(ex.Message);
            if (entry.RunId is Guid id) _automationDiagnosticErrors[id] = reason;
            System.Diagnostics.Trace.WriteLine(reason);
            if (!_closing) SetStatus(reason);
        }
    }
}
