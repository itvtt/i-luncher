using System.IO;
using System.Text.Json;
using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private readonly Dictionary<long, SavedPlanDispatch> _savedPlanDispatches = [];
    private readonly HashSet<long> _blockedSavedPlanSendIds = [];
    private SavedPlanDispatch? _savedPlanDispatchForNextSend;
    private string? _planExecutionBoundPath;
    private bool _savedPlanComposerMayContainPendingInput;
    private long _savedPlanComposerInterruptVersion;

    public bool CanExecuteSavedPlan => string.IsNullOrEmpty(PlanExecutionBlockReason);

    public string PlanExecutionBlockReason => GetPlanExecutionBlockReason() ?? string.Empty;
    internal string? LastPlanExecutionFailure { get; private set; }

    public Task<bool> ExecuteSavedPlanAsync(string planPath, Func<bool>? canDispatch = null)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(() => ExecuteSavedPlanAsync(planPath, canDispatch)).Task.Unwrap();
        return ExecuteSavedPlanOnDispatcherAsync(planPath, canDispatch);
    }

    /// <summary>
    /// Submits one user-authored plan instruction or question to the currently bound
    /// native OpenCode session. This deliberately shares the saved-plan idle gate and
    /// PTY write revalidation, so natural language is never sent to a shell.
    /// </summary>
    public Task<bool> SubmitProjectInputAsync(string prompt, bool isQuestion, Func<bool>? canSubmit = null)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(() => SubmitProjectInputAsync(prompt, isQuestion, canSubmit)).Task.Unwrap();
        return SubmitProjectInputOnDispatcherAsync(prompt, isQuestion, canSubmit);
    }

    private async Task<bool> SubmitProjectInputOnDispatcherAsync(string prompt, bool isQuestion, Func<bool>? canSubmit)
    {
        LastPlanExecutionFailure = null;
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32 * 1024)
            return FailPlanExecution("입력 내용이 비어 있거나 너무 깁니다.");

        string? block = GetPlanExecutionBlockReason(canSubmit);
        if (!string.IsNullOrEmpty(block)) return FailPlanExecution(block);

        // Questions are explicit user work. They invalidate queued recovery and never
        // arm its native auto-resume correlation window.
        if (isQuestion) _autoResume?.Suspend("질문 입력 · 자동 진행 대기");
        if (_usesNativeOpenCodeStatus)
        {
            using var manualDispatch = BeginHeadlessManualDispatch();
            bool accepted = await SubmitStartupInputAsync(prompt, isQuestion, canSubmit ?? (() => true), _ => { }, manualDispatch.Token, userInitiated: true);
            _savedPlanComposerMayContainPendingInput = !accepted;
            return accepted;
        }
        var dispatch = new SavedPlanDispatch(prompt, canSubmit, observeForAutoResume: !isQuestion);
        _savedPlanDispatchForNextSend = dispatch;
        try
        {
            Task sendTask;
            try { sendTask = SendTextAsync(prompt); }
            finally
            {
                if (ReferenceEquals(_savedPlanDispatchForNextSend, dispatch))
                    _savedPlanDispatchForNextSend = null;
            }
            await sendTask;
            if (!dispatch.Submitted)
            {
                if (!isQuestion) _autoResume?.Suspend("입력 제출 확인 필요 · 자동 진행 멈춤");
                return FailPlanExecution("OpenCode가 입력 제출 확인을 받지 못했습니다.");
            }
            _savedPlanComposerMayContainPendingInput = true;
            if (!await WaitForNativeSubmissionConfirmationAsync(dispatch))
            {
                PreserveComposerAfterUnconfirmedSubmission(dispatch);
                if (!isQuestion) _autoResume?.Suspend("입력 전달 미확인 · 자동 진행 멈춤");
                return FailPlanExecution("OpenCode가 입력 전달을 확인하지 못했습니다. 입력이 남아 있을 수 있으니 확인한 뒤 중복 제출을 피해주세요.");
            }
            _savedPlanComposerMayContainPendingInput = false;
            return true;
        }
        catch (Exception ex)
        {
            CancelSavedPlanDispatch(dispatch, ex);
            if (!isQuestion) _autoResume?.Suspend("입력 전달 실패 · 자동 진행 멈춤");
            return FailPlanExecution("입력을 OpenCode에 전달하지 못했습니다.");
        }
        finally
        {
            if (ReferenceEquals(_savedPlanDispatchForNextSend, dispatch))
                _savedPlanDispatchForNextSend = null;
        }
    }

    private async Task<bool> ExecuteSavedPlanOnDispatcherAsync(string planPath, Func<bool>? canDispatch,
        Func<string, Task<bool>>? startupDispatch = null)
    {
        LastPlanExecutionFailure = null;
        SavedPlanDispatch? dispatch = null;
        try
        {
            string? initialBlock = startupDispatch is not null && _startupApiUnconfirmed is not null
                ? null : GetPlanExecutionBlockReason(canDispatch);
            if (!string.IsNullOrEmpty(initialBlock)) return FailPlanExecution(initialBlock);

            string fullPath;
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(planPath);
                fullPath = Path.GetFullPath(planPath, WorkingDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return FailPlanExecution("저장된 플랜 경로를 확인할 수 없습니다.");
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase))
                return FailPlanExecution("저장된 플랜은 Markdown(.md) 파일이어야 합니다.");
            if (_planExecutionBoundPath is null || !string.Equals(
                    Path.GetFullPath(fullPath), Path.GetFullPath(_planExecutionBoundPath), StringComparison.OrdinalIgnoreCase))
                return FailPlanExecution("현재 터미널에 연결된 플랜이 변경되었습니다.");

            string planContents;
            try
            {
                var file = new FileInfo(fullPath);
                if (!file.Exists || file.Length == 0 || file.Length > MaximumSavedPlanBytes)
                    return FailPlanExecution("저장된 플랜이 없거나 비어 있거나 너무 큽니다.");
                planContents = await File.ReadAllTextAsync(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return FailPlanExecution("저장된 플랜을 읽을 수 없습니다.");
            }

            if (string.IsNullOrWhiteSpace(planContents) || planContents.Length > MaximumSavedPlanBytes)
                return FailPlanExecution("저장된 플랜이 비어 있거나 너무 큽니다.");

            // The selected pane, its project, and its bound plan may have changed
            // while the Markdown file was being read. Recheck after that await.
            string? preSendBlock = startupDispatch is not null && _startupApiUnconfirmed is not null
                ? null : GetPlanExecutionBlockReason(canDispatch);
            if (!string.IsNullOrEmpty(preSendBlock)) return FailPlanExecution(preSendBlock);
            if (!string.Equals(Path.GetFullPath(fullPath), Path.GetFullPath(_planExecutionBoundPath!), StringComparison.OrdinalIgnoreCase))
                return FailPlanExecution("현재 터미널에 연결된 플랜이 변경되었습니다.");

            string prompt = BuildSavedPlanPrompt(fullPath);
            if (startupDispatch is not null) return await startupDispatch(prompt);
            if (_usesNativeOpenCodeStatus)
            {
                using var manualDispatch = BeginHeadlessManualDispatch();
                return await SubmitStartupInputAsync(prompt, false, canDispatch ?? (() => true), _ => { }, manualDispatch.Token, userInitiated: true);
            }
            dispatch = new SavedPlanDispatch(prompt, canDispatch);
            _savedPlanDispatchForNextSend = dispatch;

            Task sendTask;
            try
            {
                // SendTextAsync posts one browser-side paste + Enter into this
                // same OpenCode TUI. The correlated host handler rechecks every
                // actual PTY write, including the gap between paste and Enter.
                sendTask = SendTextAsync(prompt);
            }
            finally
            {
                if (ReferenceEquals(_savedPlanDispatchForNextSend, dispatch))
                    _savedPlanDispatchForNextSend = null;
            }

            await sendTask;
            if (!dispatch.Submitted)
            {
                _autoResume?.Suspend("플랜 전달 결과 확인 필요 · 자동 진행 멈춤");
                return FailPlanExecution("터미널이 플랜 제출을 확인하지 못했습니다.");
            }
            _savedPlanComposerMayContainPendingInput = true;
            if (!await WaitForNativeSubmissionConfirmationAsync(dispatch))
            {
                PreserveComposerAfterUnconfirmedSubmission(dispatch);
                _autoResume?.Suspend("플랜 입력 전달 미확인 · 자동 진행 멈춤");
                return FailPlanExecution("OpenCode가 플랜 입력 전달을 확인하지 못했습니다. 입력이 남아 있을 수 있으니 확인한 뒤 중복 제출을 피해주세요.");
            }
            _savedPlanComposerMayContainPendingInput = false;
            return true;
        }
        catch (Exception ex)
        {
            if (startupDispatch is not null) throw;
            if (dispatch is not null) CancelSavedPlanDispatch(dispatch, ex);
            _autoResume?.Suspend("플랜 전달 실패 · 자동 진행 멈춤");
            return FailPlanExecution(ex is TimeoutException
                ? "플랜 전달 확인 시간이 초과되었습니다. 중복 제출은 하지 않았습니다."
                : "플랜을 OpenCode에 전달하지 못했습니다.");
        }
        finally
        {
            if (dispatch is not null && ReferenceEquals(_savedPlanDispatchForNextSend, dispatch))
                _savedPlanDispatchForNextSend = null;
        }
    }


    private async Task<bool> WaitForNativeSubmissionConfirmationAsync(SavedPlanDispatch dispatch)
    {
        OpenCodeStatusMonitor? monitor = _openCodeStatus;
        if (monitor is null || _disposed || !IsRunning || !CanDispatchCallback(dispatch.CanDispatch))
            return false;

        long baseline = dispatch.SubmissionConfirmationBaseline;
        if (monitor.SubmissionGeneration != dispatch.SubmissionGeneration)
            return false;
        if (monitor.SubmissionConfirmationVersion > baseline)
            return dispatch.ComposerInterruptVersion == _savedPlanComposerInterruptVersion &&
                   !_disposed && IsRunning && CanDispatchCallback(dispatch.CanDispatch);

        var confirmation = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSubmissionConfirmed(OpenCodeSubmissionObservation observation)
        {
            if (observation.ConfirmationVersion > baseline && observation.Generation == dispatch.SubmissionGeneration)
                confirmation.TrySetResult(observation.ConfirmationVersion);
        }

        monitor.SubmissionConfirmed += OnSubmissionConfirmed;
        try
        {
            if (monitor.SubmissionGeneration != dispatch.SubmissionGeneration)
                return false;
            if (monitor.SubmissionConfirmationVersion > baseline)
                return dispatch.ComposerInterruptVersion == _savedPlanComposerInterruptVersion &&
                       !_disposed && IsRunning && CanDispatchCallback(dispatch.CanDispatch);

            return await TerminalExecutionTiming.WaitForNativeSubmissionConfirmationAsync(
                confirmation.Task,
                () => monitor.SubmissionGeneration == dispatch.SubmissionGeneration &&
                      dispatch.ComposerInterruptVersion == _savedPlanComposerInterruptVersion &&
                      !_disposed && IsRunning && CanDispatchCallback(dispatch.CanDispatch));
        }
        finally
        {
            monitor.SubmissionConfirmed -= OnSubmissionConfirmed;
        }
    }
    private const int MaximumSavedPlanBytes = 2 * 1024 * 1024;

    private static string BuildSavedPlanPrompt(string planPath) => BuildSavedPlanPromptCore(planPath) +
        "\n\nWhen verifying an app you created, run it only as a bounded 60–120 second smoke check. Keep the exact child process handle or PID you started and terminate only that owned process tree in a finally/cleanup step, then verify it exited. Do not leave a foreground server running, use an unbounded wait, or kill by process name. If cleanup or exit cannot be verified, report that clearly and stop.";

    private static string BuildSavedPlanPromptCore(string planPath) =>
        "Read and follow the saved implementation plan at " + JsonSerializer.Serialize(planPath) +
        ". Treat it as the requested task. First read the Markdown file, then create or update OpenCode native todos for the implementation and its tests. " +
        "Implement the plan, run an appropriate build and tests, and keep todo status accurate. Stop if the plan is missing, unreadable, or conflicts with these instructions.";

    private string? GetPlanExecutionBlockReason(Func<bool>? canDispatch = null, bool allowTrackedDispatch = false)
    {
        if (_startupApiActive || _startupApiUnconfirmed is not null)
            return "OpenCode 시작 요청의 접수를 확인 중입니다.";
        if (!allowTrackedDispatch && (_savedPlanDispatchForNextSend is not null || _savedPlanDispatches.Count != 0))
            return "저장된 플랜을 이미 전달하고 있습니다.";

        string? reason = GetSavedPlanExecutionGateBlockReason(new SavedPlanExecutionState(
            _usesNativeOpenCodeStatus,
            IsReady,
            _disposed,
            _failed,
            IsRunning,
            IsUserInputBlocked,
            _manualInputOverride,
            _automationBusy,
            _inputCapture.HasPendingInput || _savedPlanComposerMayContainPendingInput ||
                (_usesNativeOpenCodeStatus && _nativeComposerDraft.Length > 0),
            _openCodeStatus?.Snapshot.Kind ?? OpenCodeStatusKind.Unknown));
        if (!string.IsNullOrEmpty(reason)) return reason;

        if (string.IsNullOrWhiteSpace(_planExecutionBoundPath))
            return "현재 터미널에 Markdown 플랜이 연결되어 있지 않습니다.";
        if (!string.Equals(Path.GetExtension(_planExecutionBoundPath), ".md", StringComparison.OrdinalIgnoreCase))
            return "현재 연결된 플랜은 Markdown(.md) 파일이 아닙니다.";

        if (!CanDispatchCallback(canDispatch))
            return "선택된 터미널 또는 플랜 연결이 변경되었습니다.";
        return null;
    }

    internal static string? GetSavedPlanExecutionGateBlockReason(SavedPlanExecutionState state)
    {
        if (!state.UsesNativeOpenCodeStatus) return "네이티브 OpenCode 대화형 세션에서만 실행할 수 있습니다.";
        if (state.IsDisposed || state.IsFailed || !state.ProcessRunning) return "터미널 세션을 사용할 수 없습니다.";
        if (!state.IsReady) return "터미널이 준비되지 않았습니다.";
        if (state.IsUserInputBlocked) return "터미널 입력이 차단되어 있습니다.";
        if (state.ManualOverride) return "수동 확인 상태를 먼저 해제해야 합니다.";
        if (state.AutomationBusy) return "자동화가 진행 중입니다.";
        if (state.HasPendingInput) return "전송하지 않은 입력이 있어 플랜을 실행할 수 없습니다. 입력창을 비우거나 먼저 보낸 뒤 다시 실행하세요.";

        return state.Status switch
        {
            OpenCodeStatusKind.Idle => null,
            OpenCodeStatusKind.Busy => "OpenCode가 작업 중입니다.",
            OpenCodeStatusKind.Pending => "OpenCode가 이전 요청을 확인 중입니다.",
            OpenCodeStatusKind.NeedsInput => "OpenCode의 질문이나 승인을 먼저 처리해야 합니다.",
            _ => "OpenCode 상태를 확인할 수 없습니다."
        };
    }

    internal readonly record struct SavedPlanExecutionState(
        bool UsesNativeOpenCodeStatus,
        bool IsReady,
        bool IsDisposed,
        bool IsFailed,
        bool ProcessRunning,
        bool IsUserInputBlocked,
        bool ManualOverride,
        bool AutomationBusy,
        bool HasPendingInput,
        OpenCodeStatusKind Status);

    private static bool CanDispatchCallback(Func<bool>? callback)
    {
        if (callback is null) return true;
        try { return callback(); }
        catch { return false; }
    }

    private bool FailPlanExecution(string reason)
    {
        LastPlanExecutionFailure = reason;
        StatusChanged?.Invoke(reason);
        return false;
    }

    private void BindSavedPlanExecutionPath(string? path)
    {
        string? nextPath = string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path, WorkingDirectory);
        if (!string.Equals(_planExecutionBoundPath, nextPath, StringComparison.OrdinalIgnoreCase))
            CancelIncompleteSavedPlanDispatches();
        _planExecutionBoundPath = nextPath;
    }

    private SavedPlanDispatch? AttachSavedPlanDispatch(long sendId, string text, bool submit)
    {
        SavedPlanDispatch? dispatch = _savedPlanDispatchForNextSend;
        if (dispatch is null) return null;
        _savedPlanDispatchForNextSend = null;
        if (!submit || !string.Equals(text, dispatch.Prompt, StringComparison.Ordinal))
        {
            dispatch.Cancelled = true;
            return null;
        }
        dispatch.SendId = sendId;
        dispatch.ComposerInterruptVersion = _savedPlanComposerInterruptVersion;
        _savedPlanDispatches[sendId] = dispatch;
        return dispatch;
    }

    private bool CanWriteSavedPlanDispatch(long sendId, string input)
    {
        if (_blockedSavedPlanSendIds.Contains(sendId))
        {
            return false;
        }

        if (!_savedPlanDispatches.TryGetValue(sendId, out SavedPlanDispatch? dispatch)) return true;
        string? blockReason = GetPlanExecutionBlockReason(dispatch.CanDispatch, allowTrackedDispatch: true);
        if (string.IsNullOrEmpty(blockReason)) return true;

        CancelSavedPlanDispatch(dispatch, new InvalidOperationException(blockReason));
        StatusChanged?.Invoke(blockReason);
        return false;
    }

    private void PrepareSavedPlanDispatchSubmit(long sendId, string input)
    {
        if (input != "\r") return;
        if (!_savedPlanDispatches.TryGetValue(sendId, out SavedPlanDispatch? dispatch)) return;

        // The native server can publish busy and even idle while the PTY write
        // is returning. Mark the local submission before Enter reaches it so a
        // fast native completion cannot be overwritten by a late optimistic
        // Pending state.
        if (_openCodeStatus is { } monitor)
        {
            OpenCodeSubmissionObservation observation = monitor.ObserveSubmission();
            dispatch.SubmissionConfirmationBaseline = observation.ConfirmationVersion;
            dispatch.SubmissionGeneration = observation.Generation;
        }

        // Arm the controller immediately before Enter reaches the PTY. The
        // native message event can arrive as soon as OpenCode reads that byte.
        // If the write fails, the caller cancels and suspends this observation.
        if (dispatch.ObserveForAutoResume)
        {
            _autoResume?.ObserveSubmittedPrompt(dispatch.Prompt);
            dispatch.LocalInputObserved = true;
        }
    }

    private void ObserveSavedPlanDispatchWrite(long sendId, string input)
    {
        if (!_savedPlanDispatches.TryGetValue(sendId, out SavedPlanDispatch? dispatch)) return;
        if (input != "\r")
        {
            if (input.Length != 0) dispatch.PromptWasWritten = true;
            return;
        }

        // Record the successful Enter write without resetting any native
        // evidence that may already have arrived. Submission observation was
        // armed before the write in PrepareSavedPlanDispatchSubmit.
        if (dispatch.ObserveForAutoResume && !dispatch.LocalInputObserved)
            _autoResume?.ObserveSubmittedPrompt(dispatch.Prompt);
        _savedPlanComposerMayContainPendingInput = false;
        dispatch.Submitted = true;
        _savedPlanDispatches.Remove(sendId);
    }

    private void CancelIncompleteSavedPlanDispatches()
    {
        foreach (SavedPlanDispatch dispatch in _savedPlanDispatches.Values.ToArray())
            CancelSavedPlanDispatch(dispatch, new InvalidOperationException("User input cancelled the pending saved plan dispatch."));
    }

    private void CancelSavedPlanDispatch(SavedPlanDispatch dispatch, Exception error)
    {
        if (dispatch.Cancelled) return;
        if (dispatch.PromptWasWritten && !dispatch.Submitted)
            _savedPlanComposerMayContainPendingInput = true;
        if (dispatch.SendId is long sendId)
        {
            if (_savedPlanDispatches.TryGetValue(sendId, out SavedPlanDispatch? tracked) && ReferenceEquals(tracked, dispatch))
                _savedPlanDispatches.Remove(sendId);
            _blockedSavedPlanSendIds.Add(sendId);
            if (_pendingTextSends.Remove(sendId, out TaskCompletionSource? completion))
                completion.TrySetException(error);
        }
        if (ReferenceEquals(_savedPlanDispatchForNextSend, dispatch))
            _savedPlanDispatchForNextSend = null;
        dispatch.Cancelled = true;
    }

    private void CompleteSavedPlanDispatch(long sendId)
    {
        _blockedSavedPlanSendIds.Remove(sendId);
        if (_savedPlanDispatches.TryGetValue(sendId, out SavedPlanDispatch? dispatch) && dispatch.Cancelled)
            _savedPlanDispatches.Remove(sendId);
    }

    private void PreserveComposerAfterUnconfirmedSubmission(SavedPlanDispatch dispatch)
    {
        if (dispatch.ComposerInterruptVersion == _savedPlanComposerInterruptVersion &&
            (_openCodeStatus is null || _openCodeStatus.SubmissionGeneration == dispatch.SubmissionGeneration))
            _savedPlanComposerMayContainPendingInput = true;
    }

    private void ClearSavedPlanComposerDraftAfterInterrupt()
    {
        _savedPlanComposerInterruptVersion++;
        _savedPlanComposerMayContainPendingInput = false;
    }

    private void ObserveUserWriteForSavedPlanDraft(string input)
    {
        if (input.Contains('\x03') || input.Contains('\r') || input.Contains('\n'))
            ClearSavedPlanComposerDraftAfterInterrupt();
    }

    private sealed class SavedPlanDispatch(string prompt, Func<bool>? canDispatch, bool observeForAutoResume = true)
    {
        public string Prompt { get; } = prompt;
        public Func<bool>? CanDispatch { get; } = canDispatch;
        public bool ObserveForAutoResume { get; } = observeForAutoResume;
        public long? SendId { get; set; }
        public bool PromptWasWritten { get; set; }
        public bool LocalInputObserved { get; set; }
        public long SubmissionConfirmationBaseline { get; set; }
        public long SubmissionGeneration { get; set; }
        public long ComposerInterruptVersion { get; set; }
        public bool Submitted { get; set; }
        public bool Cancelled { get; set; }
    }
}


