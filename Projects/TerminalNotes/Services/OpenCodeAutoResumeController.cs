using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TerminalNotes.Services;

/// <summary>
/// Pins recovery to native evidence for a local user submission. Never reads terminal
/// output, types into a shell, resurrects historical work, or repeats an uncertain POST.
/// </summary>
public sealed class OpenCodeAutoResumeController : IDisposable
{
    private static readonly SemaphoreSlim ActionGate = new(1, 1);
    private static readonly Dictionary<string, OpenCodeAutoResumeController> Owners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object OwnersGate = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _work = new();
    private readonly OpenCodeRecoveryClient _client;
    private readonly string _project;
    private readonly Func<bool> _canAct;
    private readonly TimeProvider _time;
    private OpenCodeRecoveryPolicy _policy = new();
    private readonly TerminalInputCapture _localInput = new();
    private string? _planPath, _planHash, _sessionId, _requestId, _expectedUserMessageId, _pendingMessageId;
    private string? _previousUserMessageId;
    private DateTimeOffset? _awaitingMessageUntil;
    private DateTimeOffset? _continuousImprovementUntil;
    private bool _abortAccepted;
    private string? _abortedLaunchToolFingerprint;
    private DateTimeOffset? _stalledAbortAttemptedAt;
    private string? _stalledAbortActivity, _stalledAbortProgress, _stalledAbortRequest;
    private string _lastUserInstruction = "";
    private bool _isStalled, _restartRequested, _abortActionInFlight;
    private bool _hasVerifiedNotificationStall;
    private string? _notificationStallSession, _notificationStallRequest, _notificationStallProgress, _notificationStallActivity;
    private bool _notificationStallHasTools;
    private long _notificationStallWork, _notificationStallBinding;
    private DateTimeOffset? _notificationStallSince, _notificationStallLastVerifiedAt;
    public bool IsStalled { get { lock (_gate) return _isStalled; } }
    public bool HasVerifiedNotificationStall
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset now = _time.GetUtcNow();
                return _hasVerifiedNotificationStall && _notificationStallLastVerifiedAt is { } verifiedAt &&
                    now >= verifiedAt && now - verifiedAt <= TimeSpan.FromSeconds(30);
            }
        }
    }
    public Func<OpenCodeStalledRestart, Task<bool>>? RestartStalledProcessAsync { get; set; }
    private OpenCodeRecoverySignal? _candidate;
    private readonly Dictionary<string, long> _todoSignals = new(StringComparer.Ordinal);
    private DateTimeOffset? _localSubmissionAt;
    private string? _apiSubmissionSession, _apiSubmissionMessage;
    private long _apiSubmissionTodoVersion;
    private long _epoch, _userWork, _binding, _statusVersion;
    private long? _lastPollTimestamp;
    private bool _enabled, _continuousImprovementEnabled, _disposed, _workConfirmed, _started, _observeCompletion;
    private bool _continuousTaskInProgress, _isRecoveryPaused;
    private string _status = "";
    private readonly Dictionary<string, DateTimeOffset> _diagnosticLastEmitted = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastSubstantiveProgressAt, _lastHealthDiagnosticAt;
    private string? _lastProgressFingerprint;
    private DateTimeOffset? _nextReadRetryAt;
    private int _readFailureCount;

    public OpenCodeAutoResumeController(string project, string? planPath, OpenCodeRecoveryClient client,
        Func<bool> canAct, TimeProvider? timeProvider = null)
    {
        _project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        _planPath = planPath;
        _client = client;
        _canAct = canAct;
        _time = timeProvider ?? TimeProvider.System;
        _localInput.Submitted += text =>
        {
            if (text.TrimStart().StartsWith('/')) return;
            lock (_gate)
            {
                _lastUserInstruction = text;
                _localSubmissionAt = _time.GetUtcNow();
                _continuousTaskInProgress = ContinuousOptionEnabledLocked;
            }
        };
    }

    public event Action<string>? StatusChanged;
    public event Action<OpenCodeAutoResumeDiagnostic>? DiagnosticEmitted;
    public string Status { get { lock (_gate) return _status; } }
    private bool _hasUnfinishedWork, _hasVerifiedCompletion;
    public bool HasUnfinishedWork { get { lock (_gate) return RecoveryActiveLocked && _hasUnfinishedWork; } }
    public bool IsContinuousImprovementEnabled
    {
        get
        {
            lock (_gate)
                return _continuousImprovementEnabled &&
                    (_continuousImprovementUntil is not { } until || until > _time.GetUtcNow());
        }
    }
    public bool HasContinuousImprovementWork { get { lock (_gate) return IsContinuousImprovementEnabled && _hasUnfinishedWork; } }
    public bool IsRecoveryPaused { get { lock (_gate) return _isRecoveryPaused; } }
    public bool HasVerifiedCompletion { get { lock (_gate) return _hasVerifiedCompletion; } }
    private string? _continuationSessionId;
    public string? ContinuationSessionId { get { lock (_gate) return _sessionId ?? _candidate?.SessionId ?? _continuationSessionId; } }
    public string? VerifiedContinuationSessionId { get { lock (_gate) return _continuationSessionId; } }
    public void EnableTaskCompletionMonitoring() { lock (_gate) _observeCompletion = true; }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            _ = Task.Run(RunAsync);
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || _enabled == enabled) return;
            _enabled = enabled;
            if (!RecoveryActiveLocked) InvalidateLocked();
            SetStatusLocked(enabled ? "" : "자동 진행 꺼짐");
        }
    }

    public void SetContinuousImprovement(bool enabled, DateTimeOffset? until)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (enabled && until is { } end && end <= _time.GetUtcNow()) enabled = false;
            if (_continuousImprovementEnabled == enabled && _continuousImprovementUntil == until) return;
            _continuousImprovementEnabled = enabled;
            _continuousImprovementUntil = until;
            if (enabled && (_sessionId is not null || _candidate is not null || _localSubmissionAt is not null))
                _continuousTaskInProgress = true;
            _epoch++;
            _work.Cancel();
            _work.Dispose();
            _work = new CancellationTokenSource();
            _policy = new();
            SetStatusLocked(enabled ? "Loop · 근거 확인 대기" : "");
        }
    }

    public void Suspend(string reason)
    {
        lock (_gate)
        {
            if (_disposed) return;
            InvalidateLocked();
            SetStatusLocked(_enabled ? reason : "자동 진행 꺼짐");
        }
    }

    public void UpdatePlan(string? path)
    {
        lock (_gate)
        {
            if (_disposed || string.Equals(_planPath, path, StringComparison.OrdinalIgnoreCase)) return;
            _planPath = path;
            _binding++;
            InvalidateLocked();
            SetStatusLocked(_enabled ? "MD 변경 · 새 요청 대기" : "자동 진행 꺼짐");
        }
    }

    public void ObserveLocalInput(string input)
    {
        if (input.Length == 0) return;
        lock (_gate)
        {
            if ((!RecoveryActiveLocked && !_observeCompletion) || _disposed) return;
            if (TerminalInputCapture.IsTerminalResponse(input))
            {
                return;
            }
            InvalidateLocked(preserveInput: true); // Cancel queued work before forwarding a user's key to the TUI.
            _localInput.Observe(input);
            if (input.Contains('\x03') || input == "\x1b")
            {
                _localInput.Clear();
                _localSubmissionAt = null;
                EmitDiagnosticLocked("manual_stop", "user", "Automatic recovery stopped by user input.");
                SetStatusLocked("사용자 중단 · 자동 진행 멈춤");
                return;
            }
            SetStatusLocked("");
        }
    }

    public void ObserveSubmittedPrompt(string prompt, string? sessionId = null, string? messageId = null)
    {
        lock (_gate)
        {
            if ((!RecoveryActiveLocked && !_observeCompletion) || _disposed) return;
            InvalidateLocked();
            if (!string.IsNullOrWhiteSpace(prompt) && !prompt.TrimStart().StartsWith('/'))
            {
                _lastUserInstruction = prompt;
                _localSubmissionAt = _time.GetUtcNow();
                _continuousTaskInProgress = ContinuousOptionEnabledLocked;
                _apiSubmissionSession = sessionId;
                _apiSubmissionMessage = messageId;
                _apiSubmissionTodoVersion = sessionId is null ? 0 : _todoSignals.GetValueOrDefault(sessionId);
            }
            SetStatusLocked("");
        }
    }

    // An exact native message read can restore correlation after a missed SSE event.
    // A user's intervening input invalidates these IDs before this method can act.
    public void ConfirmApiSubmission(string sessionId, string messageId)
    {
        lock (_gate)
        {
            if (_disposed || _apiSubmissionSession != sessionId || _apiSubmissionMessage != messageId ||
                _sessionId is not null) return;
            _localSubmissionAt = _time.GetUtcNow();
            _candidate = new("message.updated", sessionId, messageId, _localSubmissionAt, _apiSubmissionTodoVersion);
        }
        _ = TickSafelyAsync();
    }

    public void PreserveRestartInstruction(string submittedPrompt, string? originalInstruction)
    {
        lock (_gate)
        {
            if (!_disposed && originalInstruction is not null && _lastUserInstruction == submittedPrompt)
                _lastUserInstruction = originalInstruction;
        }
    }
    public void ObserveNativeSignal(OpenCodeRecoverySignal signal)
    {
        bool poll = false;
        lock (_gate)
        {
            if ((!RecoveryActiveLocked && !_observeCompletion) || _disposed) return;
            if (signal.Type == "todo.updated")
            {
                if (_todoSignals.Count > 64) _todoSignals.Clear();
                _todoSignals[signal.SessionId] = signal.Version;
                if (signal.SessionId == _sessionId) _workConfirmed = true;
                return;
            }
            if (signal.Type == "session.error" && signal.SessionId == _sessionId)
            {
                // The fresh HTTP evidence distinguishes our abort acknowledgement from other errors.
                poll = true;
            }
            else if (signal.Type == "message.updated" && signal.MessageId is { } messageId)
            {
                if (_apiSubmissionMessage is not null && _sessionId is null &&
                    (signal.SessionId != _apiSubmissionSession || messageId != _apiSubmissionMessage)) return;
                if (signal.SessionId == _sessionId && (messageId == _pendingMessageId || messageId == _expectedUserMessageId))
                    return;
                if (_localSubmissionAt is not { } submitted)
                {
                    if (signal.SessionId == _sessionId)
                    {
                        InvalidateLocked();
                        SetStatusLocked("새 요청 감지 · 자동 진행 대기");
                    }
                    return; // Other sessions can be child-agent work; never retarget from their events.
                }
                var now = _time.GetUtcNow();
                if (now - submitted > TimeSpan.FromMinutes(2) || signal.MessageCreatedAt is not { } created ||
                    created < submitted.AddSeconds(-2) || created > now.AddSeconds(5)) return;
                if (_candidate is { } candidate && (candidate.SessionId != signal.SessionId || candidate.MessageId != messageId))
                {
                    InvalidateLocked();
                    SetStatusLocked("세션 확인 필요 · 자동 진행 멈춤");
                    return;
                }
                _candidate ??= signal; // Repeated metadata updates must not move the causal todo baseline.
                poll = true;
            }
        }
        if (poll) _ = TickSafelyAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _time, _lifetime.Token).ConfigureAwait(false);
                await TickSafelyAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickSafelyAsync()
    {
        try { await TickAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (_disposed || (!RecoveryActiveLocked && !_observeCompletion)) return;
                // Keep the exact owner, request pin, policy reservation and epoch. The next
                // periodic tick retries safe reads; it cannot retarget or repeat a POST.
                bool actionUncertain = _pendingMessageId is not null || _restartRequested || _abortActionInFlight;
                if (_restartRequested) _isRecoveryPaused = true;
                if (!actionUncertain) RecordReadFailureLocked();
                EmitDiagnosticLocked(actionUncertain ? "action_ambiguous" : "tick_fault",
                    actionUncertain ? "action" : "read",
                    actionUncertain
                        ? "An action may have started; native evidence or user review is required before further action."
                        : "Recovery check failed; the exact request remains pinned for a safe retry.",
                    errorCode: "unexpected_exception", exceptionType: exception.GetType().Name, requiresUserAction: actionUncertain);
            }
        }
    }

    public async Task TickAsync()
    {
        if (!await _tickGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            long epoch;
            string? session, path;
            CancellationToken cancellation;
            OpenCodeRecoverySignal? candidate;
            lock (_gate)
            {
                if (_continuousImprovementEnabled && _continuousImprovementUntil is { } end && end <= _time.GetUtcNow())
                {
                    _continuousImprovementEnabled = false;
                    _continuousImprovementUntil = null;
                    SetStatusLocked("Loop 종료 시간 도달");
                }
                if (_disposed || _restartRequested && _isRecoveryPaused || (!RecoveryActiveLocked && !_observeCompletion) || (_sessionId is null && _candidate is null)) return;
                if (_nextReadRetryAt is { } retryAt && _time.GetUtcNow() < retryAt) return;
                long timestamp = _time.GetTimestamp();
                if (_lastPollTimestamp is long previous && _time.GetElapsedTime(previous, timestamp) > TimeSpan.FromMinutes(2))
                {
                    InvalidateLocked();
                    SetStatusLocked("장시간 대기 복귀 · 새 요청 대기");
                    return;
                }
                _lastPollTimestamp = timestamp;
                if (!_canAct())
                {
                    _hasVerifiedCompletion = false;
                    ResetNotificationStallLocked();
                    if (_sessionId is { } pinned) _policy.Evaluate(UnavailableObservation(pinned), _time.GetUtcNow());
                    SetStatusLocked("상태 연결 대기");
                    EmitDiagnosticLocked("native_unavailable", "read", "OpenCode native state is temporarily unavailable.", errorCode: "unavailable");
                    return;
                }
                epoch = _epoch;
                candidate = _candidate;
                session = _sessionId ?? candidate?.SessionId;
                path = _planPath;
                cancellation = _work.Token;
            }
            if (session is null || path is null) return;
            var plan = await ReadPlanAsync(path, cancellation).ConfigureAwait(false);
            var read = await _client.ReadAsync(session, cancellation).ConfigureAwait(false);
            RecoveryDecision decision;
            bool restart = false;
            lock (_gate)
            {
                if (!IsCurrentLocked(epoch)) return;
                if (!read.IsAvailable || read.Evidence is not { } evidence)
                {
                    RecordReadFailureLocked();
                    _hasVerifiedCompletion = false;
                    ResetNotificationStallLocked();
                    SetStatusLocked("상태 연결 대기");
                    EmitDiagnosticLocked("native_unavailable", "read", "OpenCode native state is temporarily unavailable.",
                        read.ErrorCode ?? "unavailable");
                    if (_sessionId is not null)
                        _policy.Evaluate(UnavailableObservation(session), _time.GetUtcNow());
                    return;
                }
                _readFailureCount = 0;
                _nextReadRetryAt = null;
                if (candidate is not null && _sessionId is null)
                {
                    if (_localSubmissionAt is not { } submitted || _time.GetUtcNow() - submitted > TimeSpan.FromMinutes(2))
                    {
                        InvalidateLocked();
                        SetStatusLocked("요청 확인 시간 초과 · 새 요청 대기");
                        return;
                    }
                    if (!string.IsNullOrEmpty(evidence.ParentSessionId) || evidence.LatestUserMessageId != candidate.MessageId)
                    {
                        InvalidateLocked();
                        SetStatusLocked("세션 확인 필요 · 자동 진행 멈춤");
                        return;
                    }
                    _sessionId = session;
                    _continuationSessionId = session;
                    _requestId = _expectedUserMessageId = candidate.MessageId;
                    _localSubmissionAt = null;
                    _candidate = null;
                    _userWork++;
                    _workConfirmed = _todoSignals.GetValueOrDefault(session) > candidate.Version;
                    _planHash = plan.Hash;
                    lock (OwnersGate) Owners[OwnerKey(session)] = this;
                }
                if (!OwnsSessionLocked(session)) { InvalidateLocked(); SetStatusLocked("다른 터미널에서 관리 중"); return; }
                if (!ValidateEvidenceLocked(evidence, plan.Hash, plan.Valid)) { ResetNotificationStallLocked(); return; }
                if (_lastProgressFingerprint != evidence.ProgressFingerprint)
                {
                    if (_lastProgressFingerprint is not null)
                        _lastSubstantiveProgressAt = _time.GetUtcNow();
                    _lastProgressFingerprint = evidence.ProgressFingerprint;
                }
                _hasUnfinishedWork = _workConfirmed && evidence.IncompleteTodoCount > 0 || HasUntrackedRequestLocked(evidence);
                restart = CanRestartStalledLocked(evidence);
                _hasVerifiedCompletion = _workConfirmed && evidence.AllTodosCompleted && evidence.HasSuccessfulFinalResponse &&
                    evidence.Status == OpenCodeRecoveryStatus.Idle && !evidence.HasRunningTools && !evidence.HasBusyDescendants &&
                    !evidence.HasErrorMarker && !evidence.HasPendingPermission && !evidence.HasPendingQuestion &&
                    !evidence.HasPendingPermissionInDescendants && !evidence.HasPendingQuestionInDescendants;
                if (_hasVerifiedCompletion) _continuousTaskInProgress = false;
                decision = _policy.Evaluate(ToObservation(evidence, plan.Valid), _time.GetUtcNow());
                if (_pendingMessageId is { } pendingId && decision.Phase != RecoveryPhase.AwaitingNativeResponse)
                {
                    if (evidence.LatestUserMessageId == pendingId)
                    {
                        _previousUserMessageId = _expectedUserMessageId;
                        _expectedUserMessageId = pendingId;
                    }
                    _pendingMessageId = null;
                    _awaitingMessageUntil = null;
                }
                UpdateVerifiedNotificationStallLocked(evidence, _time.GetUtcNow());
                _isRecoveryPaused = decision.Action == RecoveryAction.Pause;
                if (_isRecoveryPaused) _hasUnfinishedWork = false;
                ShowDecisionLocked(decision);
                if (_lastHealthDiagnosticAt is not { } lastHealth || _time.GetUtcNow() - lastHealth >= TimeSpan.FromMinutes(5))
                {
                    _lastHealthDiagnosticAt = _time.GetUtcNow();
                    EmitDiagnosticLocked("health_snapshot", decision.Phase.ToString(), "Recovery monitor is active and native state was checked.");
                }
            }
            if (restart)
            {
                await RestartStalledAsync(epoch, session, path, cancellation).ConfigureAwait(false);
                return;
            }
            if (decision.Action is not (RecoveryAction.Continue or RecoveryAction.Abort)) return;

            await ActionGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                // Repeat all native checks immediately before a side effect, after the app-wide queue.
                var freshPlan = await ReadPlanAsync(path, cancellation).ConfigureAwait(false);
                var fresh = await _client.ReadAsync(session, cancellation).ConfigureAwait(false);
                string? messageId = null;
                Task? actionTask = null;
                string? stalledLaunchToolFingerprint = null;
                lock (_gate)
                {
                    if (!IsCurrentLocked(epoch) || !OwnsSessionLocked(session) || !fresh.IsAvailable || fresh.Evidence is not { } evidence ||
                        !ValidateEvidenceLocked(evidence, freshPlan.Hash, freshPlan.Valid)) return;
                    var checkedDecision = _policy.Evaluate(ToObservation(evidence, freshPlan.Valid), _time.GetUtcNow());
                    if (checkedDecision.Action != decision.Action || !_policy.TryRecordActionAttempted(checkedDecision, _time.GetUtcNow())) return;
                    decision = checkedDecision;
                    if (decision.Action == RecoveryAction.Continue)
                    {
                        messageId = OpenCodeRecoveryClient.CreateMessageId();
                        _pendingMessageId = messageId;
                        actionTask = _client.ContinueAsync(session, BuildContinuationPrompt(path), messageId, cancellation,
                            expectedUserMessageId: _expectedUserMessageId, allowAbortedTurn: _abortAccepted,
                            canSend: () => CanSend(epoch, session, path, freshPlan.Hash),
                            expectedProgressFingerprint: evidence.ProgressFingerprint,
                            expectedTodoFingerprint: evidence.TodoFingerprint,
                            allowUntrackedWork: HasUntrackedRequestLocked(evidence), expectedActivityFingerprint: evidence.ActivityFingerprint,
                            expectedStalledLaunchToolFingerprint: _abortedLaunchToolFingerprint);
                    }
                    else
                    {
                        _abortActionInFlight = true;
                        _stalledAbortAttemptedAt = _time.GetUtcNow();
                        _stalledAbortActivity = evidence.ActivityFingerprint;
                        _stalledAbortProgress = evidence.ProgressFingerprint;
                        _stalledAbortRequest = evidence.LatestUserMessageId;
                        stalledLaunchToolFingerprint = evidence.StalledLaunchToolFingerprint;
                        actionTask = _client.AbortAsync(session, cancellation,
                        expectedUserMessageId: _expectedUserMessageId, canSend: () => CanSend(epoch, session, path, freshPlan.Hash),
                        expectedProgressFingerprint: evidence.ProgressFingerprint,
                        expectedTodoFingerprint: evidence.TodoFingerprint,
                            allowUntrackedWork: HasUntrackedRequestLocked(evidence), expectedActivityFingerprint: evidence.ActivityFingerprint,
                            expectedStalledLaunchToolFingerprint: stalledLaunchToolFingerprint);
                    }
                    SetStatusLocked(decision.Action == RecoveryAction.Continue ? "미완료 작업 자동 재개" : "정체 복구 · 중단 확인 중");
                }
                RecoveryActionResult outcome;
                string? actionExceptionType = null;
                try
                {
                    await actionTask.ConfigureAwait(false);
                    // Transport-specific result adaptation is isolated here.
                    outcome = ReadActionOutcome(actionTask);
                }
                catch (OperationCanceledException exception) { outcome = RecoveryActionResult.Ambiguous; actionExceptionType = exception.GetType().Name; }
                catch (Exception exception) { outcome = RecoveryActionResult.Ambiguous; actionExceptionType = exception.GetType().Name; }
                lock (_gate)
                {
                    if (!IsCurrentLocked(epoch)) return;
                    _policy.RecordActionResult(decision.DecisionId, outcome, _time.GetUtcNow());
                    if (outcome == RecoveryActionResult.Accepted)
                    {
                        if (messageId is not null)
                        {
                            _previousUserMessageId = _expectedUserMessageId;
                            _expectedUserMessageId = messageId;
                            _awaitingMessageUntil = _time.GetUtcNow().AddMinutes(2);
                            _abortAccepted = false;
                            _abortedLaunchToolFingerprint = null;
                        }
                        else
                        {
                            _abortAccepted = true;
                            _abortedLaunchToolFingerprint = stalledLaunchToolFingerprint;
                        }
                    }
                    EmitDiagnosticLocked(outcome switch
                    {
                        RecoveryActionResult.Accepted => decision.Action == RecoveryAction.Abort ? "abort_accepted" : "continuation_accepted",
                        RecoveryActionResult.Ambiguous => "action_ambiguous",
                        _ => "action_rejected"
                    }, decision.Phase.ToString(), outcome switch
                    {
                        RecoveryActionResult.Accepted => "Native recovery action was accepted.",
                        RecoveryActionResult.Ambiguous => "Recovery action delivery is uncertain; native evidence is required before any further action.",
                        _ => "Native recovery action was not accepted."
                    }, messageId, errorCode: rejectionCode(actionTask), exceptionType: actionExceptionType,
                    requiresUserAction: outcome != RecoveryActionResult.Accepted);
                    string? rejection = actionTask switch
                    {
                        Task<OpenCodeRecoveryContinueResult> task when task.IsCompletedSuccessfully => task.Result.ErrorCode,
                        Task<OpenCodeRecoveryActionResult> task when task.IsCompletedSuccessfully => task.Result.ErrorCode,
                        _ => null
                    };
                    bool nativeStateAdvanced = outcome == RecoveryActionResult.Failed &&
                        rejection is "progress_changed" or "session_not_ready";
                    if (nativeStateAdvanced)
                    {
                        _stalledAbortAttemptedAt = null;
                        SetStatusLocked("진행 상태 변경 · 다시 확인 중");
                    }
                    else if (decision.Action == RecoveryAction.Abort && outcome != RecoveryActionResult.Accepted)
                        SetStatusLocked("정체 복구 · 중단 응답 확인 중");
                    else if (outcome == RecoveryActionResult.Failed)
                    {
                        InvalidateLocked();
                        SetStatusLocked("재개 결과 확인 필요 · 자동 재시도 멈춤");
                    }
                    if (outcome != RecoveryActionResult.Ambiguous) _pendingMessageId = null;
                    _abortActionInFlight = false;
                }
            }
            finally { ActionGate.Release(); }
        }
        finally { _tickGate.Release(); }
    }

    private static string? rejectionCode(Task? action) => action switch
    {
        Task<OpenCodeRecoveryContinueResult> task when task.IsCompletedSuccessfully => SafeCode(task.Result.ErrorCode),
        Task<OpenCodeRecoveryActionResult> task when task.IsCompletedSuccessfully => SafeCode(task.Result.ErrorCode),
        _ => null
    };

    private static string? SafeCode(string? value) => value is not null && value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ? value : null;

    private void RecordReadFailureLocked()
    {
        _readFailureCount = Math.Min(_readFailureCount + 1, 6);
        int seconds = Math.Min(10 * (1 << Math.Min(_readFailureCount - 1, 3)), 60);
        _nextReadRetryAt = _time.GetUtcNow().AddSeconds(seconds);
    }

    private static RecoveryActionResult ReadActionOutcome(Task action)
    {
        OpenCodeRecoveryOutcome outcome = action switch
        {
            Task<OpenCodeRecoveryContinueResult> continuation => continuation.Result.Outcome,
            Task<OpenCodeRecoveryActionResult> abort => abort.Result.Outcome,
            _ => OpenCodeRecoveryOutcome.Ambiguous
        };
        return outcome switch
        {
            OpenCodeRecoveryOutcome.Accepted => RecoveryActionResult.Accepted,
            OpenCodeRecoveryOutcome.Rejected => RecoveryActionResult.Failed,
            _ => RecoveryActionResult.Ambiguous
        };
    }

    private RecoveryObservation ToObservation(RecoverySessionEvidence evidence, bool validPlan) =>
        new(evidence.SessionId, _epoch, _userWork, _binding, _requestId,
            evidence.Status switch
            {
                OpenCodeRecoveryStatus.Idle => OpenCodeRecoveryExecutionState.Idle,
                OpenCodeRecoveryStatus.Busy => OpenCodeRecoveryExecutionState.Busy,
                OpenCodeRecoveryStatus.Retry => OpenCodeRecoveryExecutionState.Retry,
                _ => OpenCodeRecoveryExecutionState.Unknown
            }, ++_statusVersion, true,
            evidence.HasPendingPermission || evidence.HasPendingQuestion || evidence.HasPendingPermissionInDescendants || evidence.HasPendingQuestionInDescendants,
            evidence.HasErrorMarker && !(_abortAccepted && OpenCodeRecoveryClient.IsExpectedAbortError(evidence, _abortedLaunchToolFingerprint)),
            evidence.HasRunningTools || evidence.HasBusyDescendants ? 1 : 0,
            _workConfirmed && evidence.TotalTodoCount > 0, evidence.IncompleteTodoCount,
            validPlan, false, RecoveryActiveLocked, evidence.ProgressFingerprint)
        { ActivityFingerprint = evidence.ActivityFingerprint, HasCurrentUnfinishedRequest = HasUntrackedRequestLocked(evidence),
            StalledLaunchToolFingerprint = evidence.StalledLaunchToolFingerprint };

    private bool HasUntrackedRequestLocked(RecoverySessionEvidence evidence) =>
        _sessionId == evidence.SessionId && !string.IsNullOrEmpty(_expectedUserMessageId) &&
        evidence.LatestUserMessageId == _expectedUserMessageId && !evidence.HasSuccessfulFinalResponse;

    private void UpdateVerifiedNotificationStallLocked(RecoverySessionEvidence evidence, DateTimeOffset now)
    {
        bool hasTools = evidence.HasRunningTools || evidence.HasBusyDescendants;
        string activity = evidence.ActivityFingerprint;
        bool hasPendingInput = evidence.HasPendingPermission || evidence.HasPendingQuestion ||
            evidence.HasPendingPermissionInDescendants || evidence.HasPendingQuestionInDescendants;
        bool exactCurrentRequest = _workConfirmed && _sessionId == evidence.SessionId &&
            !string.IsNullOrEmpty(_expectedUserMessageId) && evidence.LatestUserMessageId == _expectedUserMessageId;
        if (!exactCurrentRequest || evidence.Status != OpenCodeRecoveryStatus.Busy || hasPendingInput ||
            evidence.HasErrorMarker || evidence.HasSuccessfulFinalResponse || evidence.ProgressFingerprint is not { Length: > 0 } progress)
        {
            ResetNotificationStallLocked();
            return;
        }
        if (_notificationStallSince is null || _notificationStallSession != evidence.SessionId ||
            _notificationStallRequest != _expectedUserMessageId || _notificationStallProgress != progress ||
            _notificationStallActivity != activity || _notificationStallHasTools != hasTools || _notificationStallWork != _userWork || _notificationStallBinding != _binding)
        {
            _notificationStallSince = now;
            _notificationStallSession = evidence.SessionId;
            _notificationStallRequest = _expectedUserMessageId;
            _notificationStallProgress = progress;
            _notificationStallActivity = activity;
            _notificationStallHasTools = hasTools;
            _notificationStallWork = _userWork;
            _notificationStallBinding = _binding;
        }
        TimeSpan required = hasTools ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(5);
        _hasVerifiedNotificationStall = now >= _notificationStallSince.Value &&
            now - _notificationStallSince.Value >= required;
        _notificationStallLastVerifiedAt = _hasVerifiedNotificationStall ? now : null;
    }

    private void ResetNotificationStallLocked()
    {
        _hasVerifiedNotificationStall = false;
        _notificationStallLastVerifiedAt = null;
        _notificationStallSince = null;
        _notificationStallSession = _notificationStallRequest = _notificationStallProgress = _notificationStallActivity = null;
        _notificationStallWork = _notificationStallBinding = 0;
    }

    private bool CanRestartStalledLocked(RecoverySessionEvidence evidence) => !_restartRequested &&
        RestartStalledProcessAsync is not null && _stalledAbortAttemptedAt is { } attempted &&
        _time.GetUtcNow() - attempted >= TimeSpan.FromMinutes(2) &&
        evidence.Status == OpenCodeRecoveryStatus.Busy && evidence.LatestUserMessageId == _stalledAbortRequest &&
        evidence.ActivityFingerprint == _stalledAbortActivity && evidence.ProgressFingerprint == _stalledAbortProgress &&
        !evidence.HasRunningTools && !evidence.HasBusyDescendants && !evidence.HasErrorMarker &&
        !evidence.HasPendingPermission && !evidence.HasPendingQuestion &&
        !evidence.HasPendingPermissionInDescendants && !evidence.HasPendingQuestionInDescendants;

    private async Task RestartStalledAsync(long epoch, string session, string path, CancellationToken cancellation)
    {
        await ActionGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var plan = await ReadPlanAsync(path, cancellation).ConfigureAwait(false);
            var fresh = await _client.ReadAsync(session, cancellation).ConfigureAwait(false);
            OpenCodeStalledRestart request;
            Func<OpenCodeStalledRestart, Task<bool>> restart;
            lock (_gate)
            {
                if (!IsCurrentLocked(epoch) || !OwnsSessionLocked(session) || !fresh.IsAvailable || fresh.Evidence is not { } evidence ||
                    !ValidateEvidenceLocked(evidence, plan.Hash, plan.Valid) || !CanRestartStalledLocked(evidence)) return;
                _restartRequested = true;
                _isStalled = true;
                restart = RestartStalledProcessAsync!;
                string prompt = BuildContinuationPrompt(path) + "\n\n이전 OpenCode 요청이 정체되어 프로세스를 재시작했습니다. " +
                    "현재 파일과 실행 결과부터 확인해 이미 적용한 변경을 반복하지 마세요.\n원래 사용자 요청:\n" + _lastUserInstruction;
                request = new(session, _expectedUserMessageId!, path, plan.Hash!, prompt,
                    () => CanSend(epoch, session, path, plan.Hash),
                    async () =>
                    {
                        var final = await _client.ReadAsync(session, cancellation).ConfigureAwait(false);
                        lock (_gate)
                        {
                            return CanSend(epoch, session, path, plan.Hash) && final.IsAvailable && final.Evidence is { } current &&
                                current.LatestUserMessageId == evidence.LatestUserMessageId && current.Status == OpenCodeRecoveryStatus.Busy &&
                                current.ActivityFingerprint == evidence.ActivityFingerprint && current.ProgressFingerprint == evidence.ProgressFingerprint &&
                                current.TodoFingerprint == evidence.TodoFingerprint && !current.HasRunningTools && !current.HasBusyDescendants &&
                                !current.HasErrorMarker && !current.HasPendingPermission && !current.HasPendingQuestion &&
                                !current.HasPendingPermissionInDescendants && !current.HasPendingQuestionInDescendants;
                        }
                    }) { OriginalInstruction = _lastUserInstruction };
                SetStatusLocked("정체 복구 · 터미널 재시작 중");
            }
            bool resumed = await restart(request).ConfigureAwait(false);
            lock (_gate)
            {
                if (IsCurrentLocked(epoch) && !resumed)
                {
                    _isRecoveryPaused = true;
                    _hasUnfinishedWork = false;
                    SetStatusLocked("재시작 확인 필요 · 자동 복구 멈춤");
                }
            }
        }
        finally { ActionGate.Release(); }
    }

    private RecoveryObservation UnavailableObservation(string session) => new(session, _epoch, _userWork, _binding, _requestId,
        OpenCodeRecoveryExecutionState.Unknown, ++_statusVersion, false, false, false, 0, false, 0, false, false, _enabled, null);

    private bool ValidateEvidenceLocked(RecoverySessionEvidence evidence, string? planHash, bool valid)
    {
        bool waitingForOurMessage = _awaitingMessageUntil is { } until && _time.GetUtcNow() <= until &&
            evidence.LatestUserMessageId == _previousUserMessageId;
        bool exactAmbiguousMessage = _pendingMessageId is { } pending && evidence.LatestUserMessageId == pending;
        if (evidence.SessionId != _sessionId || (evidence.LatestUserMessageId != _expectedUserMessageId && !waitingForOurMessage && !exactAmbiguousMessage))
        {
            EmitDiagnosticLocked("request_changed", "evidence", "The latest native request no longer matches the pinned request.", requiresUserAction: true);
            InvalidateLocked(); SetStatusLocked("요청 변경 · 자동 진행 대기"); return false;
        }
        if (_planHash is not null && planHash != _planHash)
        {
            EmitDiagnosticLocked("plan_changed", "plan", "The bound plan changed while this request was active.", requiresUserAction: true);
            InvalidateLocked(); SetStatusLocked("MD 내용 변경 · 새 요청 대기"); return false;
        }
        if (!valid)
        {
            EmitDiagnosticLocked("plan_unavailable", "plan", "The bound plan file is missing or invalid.", requiresUserAction: true);
            SetStatusLocked("저장된 MD 확인 필요"); return false;
        }
        _planHash ??= planHash;
        return true;
    }

    private bool CanSend(long epoch, string session, string path, string? hash)
    {
        lock (_gate)
        {
            if (!RecoveryActiveLocked || !IsCurrentLocked(epoch) || !OwnsSessionLocked(session)) return false;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 2 * 1024 * 1024) return false;
                return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path)))) == hash;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
    }

    private bool RecoveryActiveLocked => _continuousTaskInProgress
        ? ContinuousOptionEnabledLocked
        : _enabled || ContinuousOptionEnabledLocked;
    private bool ContinuousOptionEnabledLocked => _continuousImprovementEnabled &&
        (_continuousImprovementUntil is not { } until || until > _time.GetUtcNow());
    private bool IsCurrentLocked(long epoch) => !_disposed && (RecoveryActiveLocked || _observeCompletion) && _epoch == epoch && !_work.IsCancellationRequested && _canAct();
    private string OwnerKey(string session) => _project + "\n" + session;
    private bool OwnsSessionLocked(string session) { lock (OwnersGate) return Owners.TryGetValue(OwnerKey(session), out var owner) && ReferenceEquals(owner, this); }

    private void InvalidateLocked(bool preserveInput = false)
    {
        if (!preserveInput) _localInput.Clear();
        if (_sessionId is { } session)
            lock (OwnersGate)
                if (Owners.TryGetValue(OwnerKey(session), out var owner) && ReferenceEquals(owner, this)) Owners.Remove(OwnerKey(session));
        _epoch++;
        _work.Cancel();
        _work.Dispose();
        _work = new CancellationTokenSource();
        _sessionId = _requestId = _expectedUserMessageId = _pendingMessageId = null;
        _previousUserMessageId = null;
        _awaitingMessageUntil = null;
        _abortAccepted = false;
        _abortedLaunchToolFingerprint = null;
        _stalledAbortAttemptedAt = null;
        _stalledAbortActivity = _stalledAbortProgress = _stalledAbortRequest = null;
        _isStalled = _restartRequested = false;
        _abortActionInFlight = false;
        ResetNotificationStallLocked();
        _candidate = null;
        _localSubmissionAt = null;
        _apiSubmissionSession = _apiSubmissionMessage = null;
        _lastPollTimestamp = null;
        _planHash = null;
        _lastProgressFingerprint = null;
        _lastSubstantiveProgressAt = null;
        _workConfirmed = false;
        _continuousTaskInProgress = false;
        _isRecoveryPaused = false;
        _hasUnfinishedWork = _hasVerifiedCompletion = false;
        _policy = new();
    }

    private void ShowDecisionLocked(RecoveryDecision decision)
    {
        _isStalled = decision.Phase is RecoveryPhase.StalledSuspected or RecoveryPhase.AbortRequested or
            RecoveryPhase.AwaitingFreshIdleAfterAbort or RecoveryPhase.Paused || _restartRequested;
        if (decision.Action == RecoveryAction.Pause)
        {
            EmitDiagnosticLocked("recovery_paused", decision.Phase.ToString(), "Recovery is paused and needs user attention.", requiresUserAction: true);
            SetStatusLocked("자동 진행 확인 필요");
            return;
        }
        string diagnosticCode = decision.Phase switch
        {
            RecoveryPhase.StalledSuspected or RecoveryPhase.AbortRequested or RecoveryPhase.AwaitingFreshIdleAfterAbort => "stall_recovery_wait",
            RecoveryPhase.WaitingForNativeRetry => "native_retry_wait",
            _ => "recovery_wait"
        };
        EmitDiagnosticLocked(diagnosticCode, decision.Phase.ToString(), SafeReason(decision.Reason),
            requiresUserAction: decision.Phase is RecoveryPhase.Paused or RecoveryPhase.WaitingForInput);
        SetStatusLocked(decision.Phase switch
        {
            RecoveryPhase.StalledSuspected => "정체 의심 · 복구 대기",
            RecoveryPhase.IdleSettling or RecoveryPhase.ReadyToContinue => "미완료 · 자동 재개 대기",
            RecoveryPhase.AwaitingFreshIdleAfterAbort => "중단 확인 대기",
            RecoveryPhase.AwaitingNativeResponse => "자동 재개 응답 대기",
            RecoveryPhase.WaitingForNativeRetry => "OpenCode 재시도 대기",
            RecoveryPhase.RetryDelay => "재개 간격 대기",
            RecoveryPhase.Paused => "자동 진행 확인 필요",
            _ => ""
        });
    }

    private void SetStatusLocked(string text)
    {
        if (_status == text) return;
        _status = text;
        var handlers = StatusChanged;
        if (handlers is null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(text); }
            catch { /* An observer must not interrupt a recovery decision or side effect. */ }
        }
    }

    private void EmitDiagnosticLocked(string code, string phase, string reason, string? messageId = null,
        string? errorCode = null, string? exceptionType = null, bool requiresUserAction = false)
    {
        if (_disposed || (!_enabled && !ContinuousOptionEnabledLocked && !_observeCompletion)) return;
        DateTimeOffset now = _time.GetUtcNow();
        string? sessionId = _sessionId ?? _candidate?.SessionId;
        string safeCode = SafeCode(code) ?? "state";
        string safePhase = SafeCode(phase) ?? "unknown";
        string key = string.Join("|", safeCode, safePhase, sessionId, messageId ?? _expectedUserMessageId, errorCode, exceptionType);
        if (code != "health_snapshot" && _diagnosticLastEmitted.TryGetValue(key, out DateTimeOffset previous) &&
            now >= previous && now - previous < TimeSpan.FromMinutes(10)) return;
        _diagnosticLastEmitted[key] = now;
        if (_diagnosticLastEmitted.Count > 128)
        {
            foreach (string oldKey in _diagnosticLastEmitted.Where(pair => now - pair.Value >= TimeSpan.FromMinutes(10))
                         .Select(pair => pair.Key).ToArray()) _diagnosticLastEmitted.Remove(oldKey);
        }
        var diagnostic = new OpenCodeAutoResumeDiagnostic(now, safeCode, safePhase, reason,
            sessionId, messageId ?? _pendingMessageId ?? _expectedUserMessageId, _lastSubstantiveProgressAt,
            SafeCode(errorCode), SafeCode(exceptionType), requiresUserAction);
        var handlers = DiagnosticEmitted;
        if (handlers is null) return;
        foreach (Action<OpenCodeAutoResumeDiagnostic> handler in handlers.GetInvocationList())
        {
            try { handler(diagnostic); }
            catch { /* Diagnostics are observational and cannot alter recovery safety. */ }
        }
    }

    private static string SafeReason(string? reason) => reason switch
    {
        "Permission or question input is pending." => "Permission or question input is pending.",
        "OpenCode is waiting for permission or a question response." => "OpenCode is waiting for permission or a question response.",
        "OpenCode reported an error for the current request." => "OpenCode reported an error for the current request.",
        "OpenCode reports an error; automatic recovery is paused." => "OpenCode reports an error; automatic recovery is paused.",
        "OpenCode is in its native retry wait; recovery will not interrupt it." => "OpenCode is in its native retry wait; recovery will not interrupt it.",
        "The plan file is invalid." => "The bound plan is invalid.",
        "A known unfinished native todo task remains." => "A known unfinished native todo task remains.",
        _ => "Recovery is waiting for fresh native evidence."
    };

    private const string BoundedAppVerificationInstruction = "\n\nWhen verifying an app you created, run it only as a bounded 60–120 second smoke check. Keep the exact child process handle or PID you started and terminate only that owned process tree in a finally/cleanup step, then verify it exited. Do not leave a foreground server running, use an unbounded wait, or kill by process name. If cleanup or exit cannot be verified, report that clearly and stop.";

    private static string BuildContinuationPrompt(string planPath) => BuildContinuationPromptCore(planPath) + BoundedAppVerificationInstruction;

    private static string BuildContinuationPromptCore(string planPath) =>
        "연결된 plan.md " + JsonSerializer.Serialize(planPath) +
        "를 다시 읽고 현재 코드·변경분·검증 근거와 미완료 todo를 요구사항별로 대조하세요. 끝난 작업은 반복하지 말고 이미 충족된 항목은 근거를 남겨 todo만 완료로 정리하세요. 플랜이나 todo의 미완료 표시만으로 재구현하지 마세요. 실제 누락·결함이 남아 있을 때만 다음 구체 todo 하나를 최소한으로 수정·검증하고 나머지 미완료 작업을 pending으로 두세요. 기존 검증이 여전히 유효하면 재사용하고, 모두 충족되었으면 변경 없이 완료 근거를 보고하고 멈추세요. todo는 실제 결과로만 갱신하세요. 승인이나 질문이 필요하거나 안전하게 진전할 수 없으면 사유를 남기고 멈추세요.";

    private static async Task<(bool Valid, string? Hash)> ReadPlanAsync(string path, CancellationToken token)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > 2 * 1024 * 1024) return (false, null);
            string text = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            return (!string.IsNullOrWhiteSpace(text), Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return (false, null); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            InvalidateLocked();
            _disposed = true;
            _lifetime.Cancel();
            _work.Cancel();
            _client.Dispose();
        }
    }
}
