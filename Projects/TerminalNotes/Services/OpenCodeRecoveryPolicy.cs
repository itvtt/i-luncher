using System.Security.Cryptography;
using System.Text;

namespace TerminalNotes.Services;

/// <summary>
/// A conservative, deterministic policy for deciding whether an OpenCode turn may be
/// continued or recovered. Callers provide native state and an already-filtered
/// substantive progress fingerprint; polling and heartbeat events must not change it.
/// </summary>
public sealed class OpenCodeRecoveryPolicy
{
    public static readonly TimeSpan IdleSettlePeriod = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StalledSuspectPeriod = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StalledAbortPeriod = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ContinuationAcknowledgementTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumObservationGap = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(3),
        TimeSpan.FromMinutes(10)
    ];

    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public RecoveryDecision Evaluate(RecoveryObservation observation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.SessionId))
            return RecoveryDecision.None("No exact OpenCode session is selected.");

        SessionState state = GetOrCreateState(observation, now);
        state.OfferedDecisionId = null;
        if (observation.ManualStopLatched)
            state.ManualStopLatched = true;
        if (state.ManualStopLatched)
            return RecoveryDecision.Pause("Manual stop is latched for this work request.", RecoveryPhase.Paused);

        if (state.LastEvaluationAt is DateTimeOffset previousEvaluationAt &&
            NonNegativeElapsed(now, previousEvaluationAt) > MaximumObservationGap)
        {
            ReconcileObservation(state, observation, now);
            state.OfferedDecisionId = null;
            state.SafetyBlockedSince ??= previousEvaluationAt;
            state.LastEvaluationAt = now;
            ResetActivityTimers(state, observation, now);
            return RecoveryDecision.None("A long polling gap suspended recovery; waiting for a fresh stable native interval.",
                observation.Status == OpenCodeRecoveryExecutionState.Busy ? RecoveryPhase.Busy : RecoveryPhase.IdleSettling);
        }

        state.LastEvaluationAt = now;
        ReconcileObservation(state, observation, now);
        state.OfferedDecisionId = null;

        if (IsSafetyGateActive(observation))
        {
            if (state.SafetyBlockedSince is null)
                state.SafetyBlockedSince = now;
            ResetActivityTimers(state, observation, now);
            return RecoveryDecision.None(GetSafetyGateReason(observation), GetSafetyGatePhase(observation));
        }

        if (state.SafetyBlockedSince is DateTimeOffset blockedSince)
        {
            if (state.ContinuationAcknowledgementDeadline is DateTimeOffset acknowledgementDeadline)
                state.ContinuationAcknowledgementDeadline = acknowledgementDeadline + NonNegativeElapsed(now, blockedSince);
            state.SafetyBlockedSince = null;
            ResetActivityTimers(state, observation, now);
            return RecoveryDecision.None("A recovery safety gate cleared; waiting for a fresh stable native interval.",
                observation.Status == OpenCodeRecoveryExecutionState.Busy ? RecoveryPhase.Busy : RecoveryPhase.IdleSettling,
                observation.Status == OpenCodeRecoveryExecutionState.Idle ? now + IdleSettlePeriod : null);
        }

        if (state.PendingDecisionId is Guid pendingId)
        {
            if (state.PendingResult == RecoveryActionResult.Ambiguous && ResolveAmbiguousAction(state, observation, now))
            {
                // Native evidence resolved the ambiguous send. Continue evaluating the
                // resulting state; never repeat the possibly accepted request.
            }
            else
            {
                return RecoveryDecision.None("A recovery action is still awaiting a result or native confirmation.", state.Phase);
            }
        }

        if (state.AwaitingContinuationStatusVersion is not null)
        {
            if (state.ContinuationAcknowledgementDeadline is DateTimeOffset deadline && now >= deadline)
            {
                state.Phase = RecoveryPhase.Paused;
                state.HardPaused = true;
                return RecoveryDecision.Pause(
                    "OpenCode did not acknowledge the continuation with native work or progress within two minutes.", state.Phase);
            }

            return RecoveryDecision.None("Waiting for native busy status or substantive progress after the continuation.",
                RecoveryPhase.AwaitingNativeResponse, state.ContinuationAcknowledgementDeadline);
        }

        if (state.HardPaused)
            return RecoveryDecision.Pause("Automatic recovery is paused for this user request.", RecoveryPhase.Paused);

        if (!HasCurrentRequest(observation))
            return RecoveryDecision.None("There is no confirmed current user request to recover.", RecoveryPhase.WaitingForEvidence);

        if (observation.RunningToolCount > 0 && observation.StalledLaunchToolFingerprint is null)
            return RecoveryDecision.None("An OpenCode tool is still running.", RecoveryPhase.Busy);

        if (state.AbortAcceptedStatusVersion is long waitingForIdleVersion)
        {
            if (observation.Status != OpenCodeRecoveryExecutionState.Idle || observation.NativeStatusVersion <= waitingForIdleVersion)
            {
                state.Phase = RecoveryPhase.AwaitingFreshIdleAfterAbort;
                return RecoveryDecision.None("Waiting for a fresh native idle event after the abort.", state.Phase);
            }

            state.AbortAcceptedStatusVersion = null;
            state.IdleSince = now;
            state.Phase = RecoveryPhase.IdleSettling;
        }

        if (observation.Status == OpenCodeRecoveryExecutionState.Busy)
        {
            if (observation.RunningToolCount > 0 && observation.StalledLaunchToolFingerprint is null)
                return RecoveryDecision.None("An OpenCode tool is still running.", RecoveryPhase.Busy);

            if (state.NoProgressSince is not DateTimeOffset noProgressSince)
                return RecoveryDecision.None("Waiting for a stable native busy interval.", RecoveryPhase.Busy);

            TimeSpan stalledFor = NonNegativeElapsed(now, noProgressSince);
            if (stalledFor < StalledSuspectPeriod)
                return RecoveryDecision.None("OpenCode is busy and has not yet reached the stall review period.", RecoveryPhase.Busy);

            state.Phase = RecoveryPhase.StalledSuspected;
            if (!HasKnownUnfinishedWork(observation))
                return RecoveryDecision.None("OpenCode appears stalled, but no known unfinished native todo permits recovery.", state.Phase);

            if (state.NextEligibleAt is DateTimeOffset nextEligibleAt && now < nextEligibleAt)
                return RecoveryDecision.None("Recovery is waiting for its escalating retry delay.", state.Phase, nextEligibleAt);

            if (stalledFor < StalledAbortPeriod)
                return RecoveryDecision.None("OpenCode appears stalled; waiting before any recovery action.", state.Phase);

            if (observation.RunningToolCount != 0 && observation.StalledLaunchToolFingerprint is null)
                return RecoveryDecision.None("A tool is still running, so a stalled turn will not be interrupted.", state.Phase);

            return CreateDecision(state, observation, RecoveryAction.Abort,
                "OpenCode has remained busy without substantive progress for 15 minutes.", state.Phase);
        }

        if (observation.Status == OpenCodeRecoveryExecutionState.Idle)
        {
            if (!HasKnownUnfinishedWork(observation))
                return RecoveryDecision.None("No known unfinished native todo task remains.", RecoveryPhase.Idle);

            if (state.NextEligibleAt is DateTimeOffset nextEligibleAt && now < nextEligibleAt)
                return RecoveryDecision.None("Recovery is waiting for its escalating retry delay.", state.Phase, nextEligibleAt);

            if (!state.HasWorkEvidence && !observation.HasCurrentUnfinishedRequest)
                return RecoveryDecision.None("Waiting for native busy or substantive progress evidence for this request.", RecoveryPhase.WaitingForEvidence);

            if (state.IdleSince is not DateTimeOffset idleSince)
                return RecoveryDecision.None("Waiting for native idle evidence.", RecoveryPhase.WaitingForEvidence);

            TimeSpan idleFor = NonNegativeElapsed(now, idleSince);
            if (idleFor < IdleSettlePeriod)
                return RecoveryDecision.None("OpenCode is idle; allowing the native state to settle.", RecoveryPhase.IdleSettling,
                    idleSince + IdleSettlePeriod);

            return CreateDecision(state, observation, RecoveryAction.Continue,
                "OpenCode is idle and a known unfinished native todo task remains.", RecoveryPhase.ReadyToContinue);
        }

        return RecoveryDecision.None("OpenCode state is not eligible for recovery.", state.Phase);
    }

    /// <summary>Reserves a decision before dispatch so polling cannot dispatch it twice.</summary>
    public bool TryRecordActionAttempted(RecoveryDecision decision, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Action is not (RecoveryAction.Continue or RecoveryAction.Abort) ||
            decision.DecisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(decision.SessionId) ||
            !_sessions.TryGetValue(decision.SessionId, out SessionState? state) ||
            state.OfferedDecisionId != decision.DecisionId ||
            !MatchesIdentity(state, decision))
        {
            return false;
        }

        if (state.PendingDecisionId is not null || state.NextEligibleAt is DateTimeOffset next && now < next)
            return false;

        state.PendingDecisionId = decision.DecisionId;
        state.OfferedDecisionId = null;
        state.PendingAction = decision.Action;
        state.PendingResult = null;
        state.PendingStatusVersion = decision.NativeStatusVersion;
        state.PendingAttemptAt = now;
        if (decision.Action == RecoveryAction.Abort)
            state.AbortAttemptsWithoutProgress++;
        else
            state.AttemptsWithoutProgress++;
        state.Phase = decision.Action == RecoveryAction.Abort
            ? RecoveryPhase.AbortRequested
            : RecoveryPhase.ContinuationRequested;
        return true;
    }

    /// <summary>
    /// Records the transport outcome for a reserved action. Ambiguous outcomes remain
    /// blocked until native status or substantive progress proves the action was handled.
    /// </summary>
    public bool RecordActionResult(Guid decisionId, RecoveryActionResult result, DateTimeOffset now)
    {
        SessionState? state = _sessions.Values.FirstOrDefault(candidate => candidate.PendingDecisionId == decisionId);
        if (state is null || state.PendingAction is null)
            return false;

        state.PendingResult = result;
        switch (result)
        {
            case RecoveryActionResult.Accepted:
                CompleteAcceptedAction(state, now);
                break;
            case RecoveryActionResult.Failed:
                RecoveryAction failedAction = state.PendingAction.Value;
                state.PendingDecisionId = null;
                state.PendingAction = null;
                state.PendingResult = null;
                state.NextEligibleAt = now + GetRetryDelay(failedAction == RecoveryAction.Abort
                    ? state.AbortAttemptsWithoutProgress
                    : state.AttemptsWithoutProgress);
                state.Phase = RecoveryPhase.RetryDelay;
                break;
            case RecoveryActionResult.Ambiguous:
                state.Phase = RecoveryPhase.AwaitingNativeResponse;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown recovery action result.");
        }

        return true;
    }

    private SessionState GetOrCreateState(RecoveryObservation observation, DateTimeOffset now)
    {
        if (!_sessions.TryGetValue(observation.SessionId, out SessionState? state))
        {
            state = new SessionState(observation, now);
            _sessions.Add(observation.SessionId, state);
            return state;
        }

        bool newSessionEpoch = state.SessionEpoch != observation.SessionEpoch;
        bool newUserWork = state.UserWorkGeneration != observation.UserWorkGeneration ||
                           !string.Equals(state.RequestId, observation.RequestId, StringComparison.Ordinal);
        bool newDocumentBinding = state.MarkdownBindingGeneration != observation.MarkdownBindingGeneration;

        if (newSessionEpoch || newUserWork)
        {
            state = new SessionState(observation, now);
            _sessions[observation.SessionId] = state;
        }
        else if (newDocumentBinding)
        {
            // A plan rebind resets timing and requires fresh activity evidence, while
            // preserving the work request's retry history and backoff.
            state.ResetEvidence(observation, now, preserveRetryHistory: true);
        }

        if (state.WasRecoveryEnabled == false && observation.RecoveryEnabled)
        {
            state.ResetEvidence(observation, now, preserveRetryHistory: false);
            state.ManualStopLatched = false;
            if (observation.Status == OpenCodeRecoveryExecutionState.Idle && HasKnownUnfinishedWork(observation))
                state.HasWorkEvidence = true;
        }

        state.WasRecoveryEnabled = observation.RecoveryEnabled;
        return state;
    }

    private static void ReconcileObservation(SessionState state, RecoveryObservation observation, DateTimeOffset now)
    {
        if (observation.IsConnected && observation.ActivityFingerprint is { } activity &&
            !string.Equals(state.LastActivityFingerprint, activity, StringComparison.Ordinal))
        {
            if (observation.Status == OpenCodeRecoveryExecutionState.Busy) state.NoProgressSince = now;
            state.LastActivityFingerprint = activity;
        }
        bool hasSubstantiveNativeEvidence = observation.IsConnected &&
            (observation.Status is OpenCodeRecoveryExecutionState.Idle or OpenCodeRecoveryExecutionState.Busy) &&
            observation.ProgressFingerprint is not null;
        if (hasSubstantiveNativeEvidence &&
            !string.Equals(state.LastProgressFingerprint, observation.ProgressFingerprint, StringComparison.Ordinal))
        {
            if (state.HasBaselineFingerprint)
            {
                state.LastProgressAt = now;
                state.AttemptsWithoutProgress = 0;
                state.AbortAttemptsWithoutProgress = 0;
                state.NextEligibleAt = null;
                state.HasWorkEvidence = true;
                if (observation.Status == OpenCodeRecoveryExecutionState.Busy)
                    state.NoProgressSince = now;
                else if (observation.Status == OpenCodeRecoveryExecutionState.Idle)
                    state.IdleSince = now;

                if (state.AwaitingContinuationStatusVersion is not null &&
                    state.ContinuationAcknowledgementStartedAt is DateTimeOffset acknowledgementStartedAt &&
                    now > acknowledgementStartedAt)
                {
                    state.AwaitingContinuationStatusVersion = null;
                    state.ContinuationAcknowledgementStartedAt = null;
                    state.ContinuationAcknowledgementDeadline = null;
                }
            }

            state.LastProgressFingerprint = observation.ProgressFingerprint;
            state.HasBaselineFingerprint = true;
        }

        if (observation.Status == OpenCodeRecoveryExecutionState.Busy)
        {
            if (state.LastObservedStatus != OpenCodeRecoveryExecutionState.Busy)
            {
                state.BusySince = now;
                state.NoProgressSince = now;
                state.HasWorkEvidence = true;
            }
            else if (state.NoProgressSince is null)
            {
                state.NoProgressSince = now;
            }

            state.IdleSince = null;
            state.Phase = RecoveryPhase.Busy;
        }
        else if (observation.Status == OpenCodeRecoveryExecutionState.Idle)
        {
            bool freshIdleAfterAbort = state.Phase == RecoveryPhase.AwaitingFreshIdleAfterAbort &&
                                       state.AbortAcceptedStatusVersion is long abortVersion &&
                                       observation.NativeStatusVersion > abortVersion;
            if (state.LastObservedStatus == OpenCodeRecoveryExecutionState.Busy || freshIdleAfterAbort)
                state.IdleSince = now;
            else
                state.IdleSince ??= now;

            if (state.LastObservedStatus == OpenCodeRecoveryExecutionState.Busy)
                state.HasWorkEvidence = true;
            state.BusySince = null;
            state.NoProgressSince = null;
            if (state.Phase is not (RecoveryPhase.AbortRequested or RecoveryPhase.AwaitingFreshIdleAfterAbort or
                RecoveryPhase.ContinuationRequested or RecoveryPhase.AwaitingNativeResponse or RecoveryPhase.RetryDelay))
                state.Phase = RecoveryPhase.Idle;
        }
        else if (observation.Status == OpenCodeRecoveryExecutionState.Retry)
        {
            state.Phase = RecoveryPhase.WaitingForNativeRetry;
        }

        if (observation.RunningToolCount > 0)
        {
            state.ToolWasRunning = true;
            if (observation.StalledLaunchToolFingerprint is null)
                state.NoProgressSince = null;
            state.IdleSince = null;
        }
        else if (state.ToolWasRunning)
        {
            state.ToolWasRunning = false;
            if (observation.Status == OpenCodeRecoveryExecutionState.Busy)
                state.NoProgressSince = now;
            else if (observation.Status == OpenCodeRecoveryExecutionState.Idle)
                state.IdleSince = now;
        }

        if (observation.Status == OpenCodeRecoveryExecutionState.Busy &&
            state.AwaitingContinuationStatusVersion is long continuationVersion &&
            observation.NativeStatusVersion > continuationVersion)
        {
            state.AwaitingContinuationStatusVersion = null;
            state.ContinuationAcknowledgementStartedAt = null;
            state.ContinuationAcknowledgementDeadline = null;
        }

        state.LastObservedStatus = observation.Status;
        state.LastObservedStatusVersion = observation.NativeStatusVersion;
    }

    private static bool ResolveAmbiguousAction(SessionState state, RecoveryObservation observation, DateTimeOffset now)
    {
        if (state.PendingResult != RecoveryActionResult.Ambiguous || state.PendingAction is null)
            return false;

        bool newerNativeStatus = observation.NativeStatusVersion > state.PendingStatusVersion;
        bool progressAfterAttempt = state.LastProgressAt is DateTimeOffset progressAt && progressAt > state.PendingAttemptAt;
        bool resolved = state.PendingAction switch
        {
            RecoveryAction.Abort => newerNativeStatus && observation.Status == OpenCodeRecoveryExecutionState.Idle,
            RecoveryAction.Continue => (newerNativeStatus && observation.Status == OpenCodeRecoveryExecutionState.Busy) || progressAfterAttempt,
            _ => false
        };

        if (!resolved)
            return false;

        CompleteAcceptedAction(state, now);
        return true;
    }

    private static void CompleteAcceptedAction(SessionState state, DateTimeOffset now)
    {
        RecoveryAction action = state.PendingAction!.Value;
        long pendingVersion = state.PendingStatusVersion;
        state.PendingDecisionId = null;
        state.PendingAction = null;
        state.PendingResult = null;
        state.NextEligibleAt = null;
        if (action == RecoveryAction.Abort)
        {
            state.AbortAcceptedStatusVersion = pendingVersion;
            state.AwaitingContinuationStatusVersion = null;
            state.Phase = RecoveryPhase.AwaitingFreshIdleAfterAbort;
        }
        else
        {
            state.TotalCycles++;
            state.AwaitingContinuationStatusVersion = pendingVersion;
            state.ContinuationAcknowledgementStartedAt = now;
            state.ContinuationAcknowledgementDeadline = now + ContinuationAcknowledgementTimeout;
            state.AbortAcceptedStatusVersion = null;
            state.Phase = RecoveryPhase.AwaitingNativeResponse;
            state.NextEligibleAt = now + GetRetryDelay(state.AttemptsWithoutProgress);
        }
    }

    private static RecoveryDecision CreateDecision(
        SessionState state,
        RecoveryObservation observation,
        RecoveryAction action,
        string reason,
        RecoveryPhase phase)
    {
        state.Phase = phase;
        var decision = new RecoveryDecision(
            CreateDeterministicDecisionId(observation, action, state), action, phase, reason, null,
            observation.SessionId, observation.SessionEpoch, observation.UserWorkGeneration,
            observation.MarkdownBindingGeneration, observation.RequestId,
            observation.NativeStatusVersion);
        state.OfferedDecisionId = decision.DecisionId;
        return decision;
    }

    private static Guid CreateDeterministicDecisionId(
        RecoveryObservation observation,
        RecoveryAction action,
        SessionState state)
    {
        string identity = string.Join('\u001f',
            observation.SessionId,
            observation.SessionEpoch,
            observation.UserWorkGeneration,
            observation.MarkdownBindingGeneration,
            observation.RequestId ?? string.Empty,
            observation.ProgressFingerprint ?? string.Empty,
            (int)action,
            observation.NativeStatusVersion,
            state.AttemptsWithoutProgress,
            state.TotalCycles);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static bool MatchesIdentity(SessionState state, RecoveryDecision decision) =>
        state.SessionEpoch == decision.SessionEpoch &&
        state.UserWorkGeneration == decision.UserWorkGeneration &&
        state.MarkdownBindingGeneration == decision.MarkdownBindingGeneration &&
        string.Equals(state.RequestId, decision.RequestId, StringComparison.Ordinal);

    private static bool HasCurrentRequest(RecoveryObservation observation) =>
        !string.IsNullOrWhiteSpace(observation.RequestId);

    private static bool HasKnownUnfinishedWork(RecoveryObservation observation) =>
        observation.HasKnownTodoList && observation.UnfinishedTodoCount > 0 || observation.HasCurrentUnfinishedRequest;

    private static TimeSpan GetRetryDelay(int attemptCount) =>
        RetryDelays[Math.Clamp(attemptCount - 1, 0, RetryDelays.Length - 1)];

    private static TimeSpan NonNegativeElapsed(DateTimeOffset now, DateTimeOffset since) =>
        now <= since ? TimeSpan.Zero : now - since;

    private static bool IsSafetyGateActive(RecoveryObservation observation) =>
        !observation.RecoveryEnabled ||
        !observation.IsConnected ||
        observation.Status is OpenCodeRecoveryExecutionState.Unknown or OpenCodeRecoveryExecutionState.Retry ||
        observation.HasPendingApprovalOrQuestion ||
        observation.HasError ||
        !observation.IsMarkdownValid;

    private static string GetSafetyGateReason(RecoveryObservation observation)
    {
        if (!observation.RecoveryEnabled)
            return "Automatic recovery is disabled.";
        if (!observation.IsConnected || observation.Status == OpenCodeRecoveryExecutionState.Unknown)
            return "OpenCode status is disconnected or unavailable.";
        if (observation.Status == OpenCodeRecoveryExecutionState.Retry)
            return "OpenCode is in its native retry wait; recovery will not interrupt it.";
        if (observation.HasPendingApprovalOrQuestion)
            return "OpenCode is waiting for permission or a question response.";
        if (observation.HasError)
            return "OpenCode reports an error; automatic recovery is paused.";
        return "The saved plan document is not valid for this request.";
    }

    private static RecoveryPhase GetSafetyGatePhase(RecoveryObservation observation)
    {
        if (observation.Status == OpenCodeRecoveryExecutionState.Retry)
            return RecoveryPhase.WaitingForNativeRetry;
        if (observation.HasPendingApprovalOrQuestion)
            return RecoveryPhase.WaitingForInput;
        if (observation.HasError || !observation.IsMarkdownValid)
            return RecoveryPhase.Paused;
        return RecoveryPhase.WaitingForEvidence;
    }

    private static void ResetActivityTimers(SessionState state, RecoveryObservation observation, DateTimeOffset now)
    {
        state.BusySince = observation.Status == OpenCodeRecoveryExecutionState.Busy && observation.RunningToolCount == 0 ? now : null;
        state.NoProgressSince = observation.Status == OpenCodeRecoveryExecutionState.Busy && observation.RunningToolCount == 0 ? now : null;
        state.IdleSince = observation.Status == OpenCodeRecoveryExecutionState.Idle && observation.RunningToolCount == 0 ? now : null;
        state.ToolWasRunning = observation.RunningToolCount > 0;
    }

    private sealed class SessionState
    {
        public SessionState(RecoveryObservation observation, DateTimeOffset now)
        {
            SessionEpoch = observation.SessionEpoch;
            LastEvaluationAt = now;
            UserWorkGeneration = observation.UserWorkGeneration;
            MarkdownBindingGeneration = observation.MarkdownBindingGeneration;
            RequestId = observation.RequestId;
            LastProgressFingerprint = observation.ProgressFingerprint;
            LastActivityFingerprint = observation.ActivityFingerprint;
            HasBaselineFingerprint = true;
            LastProgressAt = now;
            LastObservedStatus = observation.Status;
            LastObservedStatusVersion = observation.NativeStatusVersion;
            WasRecoveryEnabled = observation.RecoveryEnabled;

            if (observation.Status == OpenCodeRecoveryExecutionState.Idle)
                IdleSince = now;
            else if (observation.Status == OpenCodeRecoveryExecutionState.Busy)
            {
                BusySince = now;
                NoProgressSince = now;
                HasWorkEvidence = true;
                Phase = RecoveryPhase.Busy;
            }
            else if (observation.Status == OpenCodeRecoveryExecutionState.Retry)
                Phase = RecoveryPhase.WaitingForNativeRetry;
        }

        public long SessionEpoch { get; }
        public DateTimeOffset? LastEvaluationAt { get; set; }
        public Guid? OfferedDecisionId { get; set; }
        public long UserWorkGeneration { get; }
        public long MarkdownBindingGeneration { get; private set; }
        public string? RequestId { get; }
        public string? LastProgressFingerprint { get; set; }
        public string? LastActivityFingerprint { get; set; }
        public bool HasBaselineFingerprint { get; set; }
        public DateTimeOffset? LastProgressAt { get; set; }
        public DateTimeOffset? BusySince { get; set; }
        public DateTimeOffset? NoProgressSince { get; set; }
        public DateTimeOffset? IdleSince { get; set; }
        public DateTimeOffset? NextEligibleAt { get; set; }
        public OpenCodeRecoveryExecutionState LastObservedStatus { get; set; }
        public long LastObservedStatusVersion { get; set; }
        public bool HasWorkEvidence { get; set; }
        public int AttemptsWithoutProgress { get; set; }
        public int AbortAttemptsWithoutProgress { get; set; }
        public int TotalCycles { get; set; }
        public RecoveryPhase Phase { get; set; } = RecoveryPhase.WaitingForEvidence;
        public bool ManualStopLatched { get; set; }
        public bool WasRecoveryEnabled { get; set; }
        public Guid? PendingDecisionId { get; set; }
        public RecoveryAction? PendingAction { get; set; }
        public RecoveryActionResult? PendingResult { get; set; }
        public long PendingStatusVersion { get; set; }
        public DateTimeOffset PendingAttemptAt { get; set; }
        public long? AbortAcceptedStatusVersion { get; set; }
        public long? AwaitingContinuationStatusVersion { get; set; }
        public DateTimeOffset? ContinuationAcknowledgementStartedAt { get; set; }
        public DateTimeOffset? ContinuationAcknowledgementDeadline { get; set; }
        public bool HardPaused { get; set; }
        public bool ToolWasRunning { get; set; }
        public DateTimeOffset? SafetyBlockedSince { get; set; }

        public void ResetEvidence(RecoveryObservation observation, DateTimeOffset now, bool preserveRetryHistory)
        {
            DateTimeOffset? previousNextEligibleAt = NextEligibleAt;
            MarkdownBindingGeneration = observation.MarkdownBindingGeneration;
            LastProgressFingerprint = observation.ProgressFingerprint;
            LastActivityFingerprint = observation.ActivityFingerprint;
            HasBaselineFingerprint = true;
            LastProgressAt = now;
            LastEvaluationAt = now;
            BusySince = null;
            NoProgressSince = null;
            IdleSince = observation.Status == OpenCodeRecoveryExecutionState.Idle ? now : null;
            LastObservedStatus = observation.Status;
            LastObservedStatusVersion = observation.NativeStatusVersion;
            HasWorkEvidence = false;
            NextEligibleAt = preserveRetryHistory ? previousNextEligibleAt : null;
            Phase = RecoveryPhase.WaitingForEvidence;
            if (!preserveRetryHistory)
            {
                PendingDecisionId = null;
                PendingAction = null;
                PendingResult = null;
                AbortAcceptedStatusVersion = null;
                AwaitingContinuationStatusVersion = null;
                ContinuationAcknowledgementStartedAt = null;
                ContinuationAcknowledgementDeadline = null;
                AttemptsWithoutProgress = 0;
                AbortAttemptsWithoutProgress = 0;
                TotalCycles = 0;
                HardPaused = false;
            }
            if (observation.Status == OpenCodeRecoveryExecutionState.Busy)
            {
                BusySince = now;
                NoProgressSince = now;
                HasWorkEvidence = true;
                Phase = RecoveryPhase.Busy;
            }
        }
    }
}

public sealed record RecoveryObservation(
    string SessionId,
    long SessionEpoch,
    long UserWorkGeneration,
    long MarkdownBindingGeneration,
    string? RequestId,
    OpenCodeRecoveryExecutionState Status,
    long NativeStatusVersion,
    bool IsConnected,
    bool HasPendingApprovalOrQuestion,
    bool HasError,
    int RunningToolCount,
    bool HasKnownTodoList,
    int UnfinishedTodoCount,
    bool IsMarkdownValid,
    bool ManualStopLatched,
    bool RecoveryEnabled,
    string? ProgressFingerprint)
{
    // Liveness prevents interrupting a streaming response, but never resets the no-progress retry budget.
    public string? ActivityFingerprint { get; init; }
    public bool HasCurrentUnfinishedRequest { get; init; }
    public string? StalledLaunchToolFingerprint { get; init; }
}

public sealed record RecoveryDecision(
    Guid DecisionId,
    RecoveryAction Action,
    RecoveryPhase Phase,
    string Reason,
    DateTimeOffset? RetryAfterUtc,
    string? SessionId,
    long SessionEpoch,
    long UserWorkGeneration,
    long MarkdownBindingGeneration,
    string? RequestId,
    long NativeStatusVersion)
{
    public static RecoveryDecision None(string reason, RecoveryPhase phase = RecoveryPhase.WaitingForEvidence, DateTimeOffset? retryAfterUtc = null) =>
        new(Guid.Empty, RecoveryAction.None, phase, reason, retryAfterUtc, null, 0, 0, 0, null, 0);

    public static RecoveryDecision Pause(string reason, RecoveryPhase phase = RecoveryPhase.Paused) =>
        new(Guid.Empty, RecoveryAction.Pause, phase, reason, null, null, 0, 0, 0, null, 0);
}

public enum OpenCodeRecoveryExecutionState
{
    Unknown,
    Idle,
    Busy,
    Retry
}

public enum RecoveryAction
{
    None,
    Continue,
    Abort,
    Pause
}

public enum RecoveryActionResult
{
    Accepted,
    Failed,
    Ambiguous
}

public enum RecoveryPhase
{
    WaitingForEvidence,
    Idle,
    IdleSettling,
    ReadyToContinue,
    Busy,
    StalledSuspected,
    AbortRequested,
    AwaitingFreshIdleAfterAbort,
    ContinuationRequested,
    AwaitingNativeResponse,
    WaitingForNativeRetry,
    WaitingForInput,
    RetryDelay,
    Paused
}
