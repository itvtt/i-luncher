using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TerminalNotes.Services;

public sealed partial class OpenCodeAutomationClient
{
    // These are inactivity windows, not limits on stage duration or retry count.
    // Tool/child work gets a longer window because a build may produce no output.
    private static readonly TimeSpan ResponseInactivity = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ToolInactivity = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _timeProvider;
    private string? _observedDispatch;
    private string? _progressFingerprint;
    private DateTimeOffset _lastObservation;
    private AutomationSnapshot? _lastSnapshot;
    private string? _recoveryReason;
    private readonly HashSet<string> _abortSent = new(StringComparer.Ordinal);
    private DateTimeOffset? _idleSince;
    private DateTimeOffset? _firstAbortAt;
    private string? _requestedIntervention;
    private DateTimeOffset? _retryObservedAt;
    private DateTimeOffset? _transportFailureSince;
    private DateTimeOffset _lastTransportFailure;
    private int _transportFailures;
    private AutomationSnapshot? _lastVerifiedSnapshot;
    private DateTimeOffset _lastTuiSelection;
    private string? _selectedDispatch;
    public string? TuiConnectionWarning { get; private set; }

    public DateTimeOffset? LastProgressAt { get; private set; }
    // UI activity only advances on assistant/tool evidence. Watchdog grace
    // resets, repeated busy polls and approval waits must not animate progress.
    public DateTimeOffset? LastActivityAt { get; private set; }
    public string ProgressExcerpt => _lastSnapshot?.ProgressExcerpt ?? "아직 확인된 응답이 없습니다.";
    public long ObservedTokens { get; private set; }
    public bool IsRecovering => _recoveryReason is not null;
    public bool IsCoordinatorIntervention => IsRecovering && _requestedIntervention is not null;
    public bool IsProviderRetrying => _lastSnapshot?.IsRetrying == true;
    public bool HasPendingNativeInput => _lastSnapshot?.HasPendingInput == true;
    public bool HasNativeSnapshot => _lastSnapshot is not null && LastReadWarning is null;
    public bool IsNativeIdle => _lastSnapshot is { Status: NativeRunStatus.Idle, HasBusyDescendants: false, HasRunningTools: false };
    public bool HasRecentActivity => LastActivityAt is { } at && _timeProvider.GetUtcNow() - at <= TimeSpan.FromSeconds(8);
    public string? SupervisionNotice => IsRecovering ? "정체 작업 중단 확인 중" :
        LastReadWarning is not null ? "연결 복구 대기" : TuiConnectionWarning is not null ? "터미널 화면 연결 재시도" : IsProviderRetrying ? "서비스 재시도 대기" :
        IsStalled ? "진행 정체 확인 중" : !HasRecentActivity ? "응답 변화 대기" : null;
    // Only repeated transport failures authorize replacing the already owned
    // local process. Malformed data/identity conflicts never grant this right.
    // The caller must still verify pane ownership, manual input and cancellation,
    // and confirm process exit before handing partial work to another role.
    public bool CanRestartUnresponsiveProcess => _transportFailureSince is { } since &&
        _transportFailures >= 3 && _timeProvider.GetUtcNow() - since >= ResponseInactivity &&
        _timeProvider.GetUtcNow() - _lastTransportFailure < TimeSpan.FromSeconds(30) &&
        (_lastVerifiedSnapshot is null ||
         (!_lastVerifiedSnapshot.HasPendingInput && !_lastVerifiedSnapshot.HasUnexpectedUserMessage &&
          (!_lastVerifiedSnapshot.HasTargetUserMessage ||
           (_lastVerifiedSnapshot.LatestUserMessageIsTarget && _lastVerifiedSnapshot.TargetUserTextMatches))));
    private bool RetryGraceElapsed => _retryObservedAt is { } at &&
        _timeProvider.GetUtcNow() - at >= TimeSpan.FromSeconds(45);

    private void ObserveTransportFailure()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _transportFailureSince ??= now;
        _lastTransportFailure = now;
        _transportFailures++;
    }

    private void ResetTransportFailures()
    {
        _transportFailureSince = null;
        _transportFailures = 0;
    }

    public async Task RefreshTuiSessionAsync(OpenCodeAutomationDispatch dispatch, Func<bool> stillOwned,
        CancellationToken cancellationToken = default)
    {
        if (!_useTuiSelection) return;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string identity = dispatch.SessionId + "/" + dispatch.MessageId;
        if (!stillOwned() || LastReadWarning is not null || _lastSnapshot is not { } snapshot ||
            snapshot.HasPendingInput || snapshot.HasUnexpectedUserMessage ||
            !snapshot.HasTargetUserMessage || !snapshot.LatestUserMessageIsTarget || !snapshot.TargetUserTextMatches ||
            (_selectedDispatch == identity && now - _lastTuiSelection < TimeSpan.FromSeconds(15))) return;
        _selectedDispatch = identity;
        _lastTuiSelection = now;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            // select-session acknowledges a broadcast, not a rendered view. The
            // initial event can precede the TUI subscriber. Rebind the exact owned
            // session after native acknowledgement and periodically while owned.
            // Never submit a prompt or type "continue" to repair only the display.
            using JsonDocument detail = await GetJsonAsync(BuildSessionEndpoint(dispatch.SessionId), timeout.Token).ConfigureAwait(false);
            VerifySessionIdentity(detail.RootElement, dispatch.SessionId, null, false);
            if (!stillOwned()) return;
            timeout.Token.ThrowIfCancellationRequested();
            using JsonDocument response = await PostJsonAsync(BuildEndpoint("/tui/select-session"),
                new { sessionID = dispatch.SessionId }, timeout.Token, "tui_select").ConfigureAwait(false);
            TuiConnectionWarning = response.RootElement.ValueKind == JsonValueKind.True ? null : "화면 연결 응답 미확인";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OpenCodeAutomationException or HttpRequestException or System.IO.IOException or JsonException or OperationCanceledException)
        {
            TuiConnectionWarning = "터미널 화면 연결 재시도";
        }
    }
    // Escalation is allowed only with fresh, exact native evidence. An unreadable
    // endpoint or approval/question can never authorize killing its process tree.
    public bool CanRestartStalledProcess => IsRecovering && LastReadWarning is null &&
        _lastSnapshot is { HasPendingInput: false, HasUnexpectedUserMessage: false } &&
        (!_lastSnapshot.IsRetrying || RetryGraceElapsed) &&
        (_lastSnapshot.ActiveSessionIds.Count > 0 || _lastSnapshot.HasRunningTools || _lastSnapshot.HasBusyDescendants) &&
        (!_lastSnapshot.HasTargetUserMessage || (_lastSnapshot.LatestUserMessageIsTarget && _lastSnapshot.TargetUserTextMatches)) &&
        _firstAbortAt is { } abortAt && LastProgressAt is { } progressAt &&
        _timeProvider.GetUtcNow() - _lastObservation < TimeSpan.FromSeconds(5) &&
        _timeProvider.GetUtcNow() - abortAt >= TimeSpan.FromMinutes(1) &&
        _timeProvider.GetUtcNow() - progressAt >= TimeSpan.FromMinutes(1);
    public bool IsStalled => _lastSnapshot is { } snapshot && LastReadWarning is null &&
        !snapshot.HasPendingInput && (!snapshot.IsRetrying || RetryGraceElapsed) &&
        LastProgressAt is { } progressAt &&
        _timeProvider.GetUtcNow() - _lastObservation < TimeSpan.FromSeconds(30) &&
        _timeProvider.GetUtcNow() - progressAt >=
            (snapshot.HasRunningTools || snapshot.HasBusyDescendants ? ToolInactivity : ResponseInactivity);

    private void ResetProgressObservation()
    {
        _lastSnapshot = null;
        LastProgressAt = null;
        LastActivityAt = null;
        _idleSince = null;
        _retryObservedAt = null;
    }

    private void ObserveProgress(OpenCodeAutomationDispatch dispatch, AutomationSnapshot snapshot)
    {
        string identity = dispatch.SessionId + "/" + dispatch.MessageId;
        if (_observedDispatch != identity)
        {
            _observedDispatch = identity;
            _progressFingerprint = null;
            _recoveryReason = null;
            _abortSent.Clear();
            _firstAbortAt = null;
            _requestedIntervention = null;
            ObservedTokens = 0;
            ResetProgressObservation();
        }
        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool changed = snapshot.ProgressFingerprint != _progressFingerprint;
        if (changed || snapshot.HasPendingInput) _retryObservedAt = null;
        if (snapshot.IsRetrying) _retryObservedAt ??= now;
        if (snapshot.HasProgressEvidence && snapshot.ProgressFingerprint != _progressFingerprint)
            LastActivityAt = now;
        if (LastProgressAt is null || snapshot.ProgressFingerprint != _progressFingerprint ||
            now - _lastObservation >= TimeSpan.FromSeconds(30) || snapshot.HasPendingInput)
            LastProgressAt = now;
        _progressFingerprint = snapshot.ProgressFingerprint;
        _lastObservation = now;
        _lastSnapshot = snapshot;
        _lastVerifiedSnapshot = snapshot;
        ResetTransportFailures();
        ObservedTokens = Math.Max(ObservedTokens, snapshot.Tokens);
    }

    /// <summary>
    /// Rechecks native progress and ownership before aborting only this dispatch's
    /// root/descendants. A new prompt is never sent here. Uncertain abort responses
    /// keep their ownership until subsequent reads prove the whole tree idle.
    /// </summary>
    public async Task<AutomationStageOutput?> RecoverStalledAsync(
        OpenCodeAutomationDispatch dispatch, Func<bool> stillOwned,
        CancellationToken cancellationToken = default)
    {
        if ((!IsRecovering && !IsStalled && _requestedIntervention is null) || !stillOwned()) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        CancellationToken token = timeout.Token;
        try
        {
            // One side effect per pass: descendants first, then the root. Each
            // subsequent abort gets another complete identity/input/progress read.
            AutomationSnapshot snapshot = await ReadSnapshotAsync(dispatch, token).ConfigureAwait(false);
            ObserveProgress(dispatch, snapshot);
            if (snapshot.HasPendingInput || (snapshot.IsRetrying && !RetryGraceElapsed) || !stillOwned()) return null;
            if (snapshot.HasUnexpectedUserMessage) return null;
            if (snapshot.HasTargetUserMessage &&
                (!snapshot.LatestUserMessageIsTarget || !snapshot.TargetUserTextMatches)) return null;
            if (!IsRecovering && !IsStalled && _requestedIntervention is null) return null;
            if (_requestedIntervention is not null && !snapshot.HasTargetUserMessage) return null;
            _recoveryReason ??= _requestedIntervention ?? "실제 응답·도구 진행이 정체되어 해당 역할 작업을 중단했습니다. 현재 변경 상태를 확인하고 남은 목표를 계속 진행하세요.";
            AutomationStageOutput? recovered = RecoveredOutputIfIdle(snapshot);
            if (recovered is not null) return recovered;
            string? target = snapshot.ActiveSessionIds
                .OrderBy(id => id == dispatch.SessionId ? 1 : 0)
                .FirstOrDefault(id => !_abortSent.Contains(id))
                // OpenCode may report idle while an owned tool is still running.
                // Try its exact root once, then require the normal exit/grace checks.
                ?? (snapshot.HasRunningTools && !_abortSent.Contains(dispatch.SessionId) ? dispatch.SessionId : null);
            if (target is null) return null;
            token.ThrowIfCancellationRequested();
            if (!stillOwned()) return null;
            _abortSent.Add(target); // A lost response must not cause a blind POST replay.
            _firstAbortAt ??= _timeProvider.GetUtcNow();
            using JsonDocument response = await PostJsonAsync(BuildSessionEndpoint(target, "/abort"),
                new { }, token, "session_abort").ConfigureAwait(false);
            if (response.RootElement.ValueKind != JsonValueKind.True)
                LastReadWarning = "정체 작업 중단 응답 미확인 · 동일 세션 종료 확인 중";
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OpenCodeAutomationException or HttpRequestException or
                                          System.IO.IOException or JsonException or OperationCanceledException)
        {
            LastReadWarning = "정체 작업 중단 확인 재시도 · " + exception.Message;
            _idleSince = null;
            return null;
        }
    }

    public Task<AutomationStageOutput?> RequestCoordinatorInterventionAsync(
        OpenCodeAutomationDispatch dispatch, string instruction, Func<bool> stillOwned,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instruction) || !stillOwned()) return Task.FromResult<AutomationStageOutput?>(null);
        _requestedIntervention = "지휘자의 중간 개입 · " + instruction;
        return RecoverStalledAsync(dispatch, stillOwned, cancellationToken);
    }

    private AutomationStageOutput? RecoveredOutputIfIdle(AutomationSnapshot snapshot)
    {
        if (snapshot.Status != NativeRunStatus.Idle || snapshot.HasBusyDescendants ||
            snapshot.HasRunningTools || snapshot.HasPendingInput || snapshot.HasUnexpectedUserMessage ||
            (snapshot.HasTargetUserMessage && (!snapshot.LatestUserMessageIsTarget || !snapshot.TargetUserTextMatches)))
        {
            _idleSince = null;
            return null;
        }
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _idleSince ??= now;
        // A second stable observation avoids handing off on a transient idle edge.
        if (now - _idleSince < TimeSpan.FromSeconds(1)) return null;
        return FailureOutput(_recoveryReason!, snapshot.Tokens) with { Interrupted = true };
    }

    private static bool AppendMessageProgress(StringBuilder progress, JsonElement messages,
        string sessionId, string? targetMessageId = null)
    {
        JsonElement rows = RequireArray(messages, "progress messages");
        if (rows.GetArrayLength() > MaximumMessageCount)
            throw Failure("too_many_messages", "OpenCode progress exceeded the bounded message count.");
        bool runningTool = false;
        foreach (JsonElement row in rows.EnumerateArray().OrderBy(row => GetString(GetProperty(row, "info"), "id"), StringComparer.Ordinal))
        {
            JsonElement info = RequireObject(GetProperty(row, "info"), "progress message info");
            if (GetString(info, "sessionID") != sessionId)
                throw Failure("message_session_mismatch", "Progress messages came from a different session.");
            if (GetString(info, "role") != "assistant" ||
                (targetMessageId is not null && GetString(info, "parentID") != targetMessageId)) continue;
            JsonElement parts = RequireArray(GetProperty(row, "parts"), "progress parts");
            // Allocating an empty assistant/message ID is not model activity.
            if (!parts.EnumerateArray().Any(part => GetString(part, "type") == "tool" ||
                (GetString(part, "type") is "text" or "reasoning" && !string.IsNullOrWhiteSpace(GetString(part, "text"))))) continue;
            Append(sessionId);
            Append(GetString(info, "id"));
            Append(GetString(info, "finish"));
            Append(ReadTokenCount(GetProperty(info, "tokens")).ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (JsonElement part in parts.EnumerateArray())
            {
                string? type = GetString(part, "type");
                if (type is "text" or "reasoning") { Append(type); Append(GetString(part, "text")); }
                else if (type == "tool")
                {
                    JsonElement state = RequireObject(GetProperty(part, "state"), "tool state");
                    string? status = GetString(state, "status");
                    if (status is not ("pending" or "running" or "completed" or "error"))
                        throw Failure("invalid_tool_status", "OpenCode returned an unknown progress tool state.");
                    runningTool |= status is "pending" or "running";
                    Append(GetString(part, "callID")); Append(GetString(part, "tool")); Append(status);
                    foreach (string property in new[] { "title", "input", "output", "error" })
                    {
                        JsonElement value = GetProperty(state, property);
                        Append(value.ValueKind == JsonValueKind.Undefined ? null : value.GetRawText());
                    }
                    // Streamed shell output may be exposed here while still running.
                    Append(GetString(GetProperty(state, "metadata"), "output"));
                }
            }
        }
        return runningTool;

        // Length framing prevents different sequences of text from colliding.
        void Append(string? value) => progress.Append(value?.Length ?? 0).Append(':').Append(value).Append(';');
    }
}
