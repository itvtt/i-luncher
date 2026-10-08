using System.IO;
using System.Net.Http;
using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private bool _startupApiActive;
    private OpenCodeAutomationDispatch? _startupApiUnconfirmed;
    private long _startupApiGeneration;

    internal bool HasPendingStartupSubmission => _startupApiUnconfirmed is not null;

    internal Task<bool> ExecuteStartupPlanAsync(string path, Func<bool> canDispatch,
        Action<string> report, CancellationToken cancellationToken) =>
        ExecuteSavedPlanOnDispatcherAsync(path, canDispatch,
            prompt => SubmitStartupInputAsync(prompt, false, canDispatch, report, cancellationToken));

    // Startup has no dependency on whether the TUI composer has finished mounting.
    // A POST is made once; a lost response changes this loop to exact-ID reads only.
    internal async Task<bool> SubmitStartupInputAsync(string prompt, bool isQuestion, Func<bool> canDispatch,
        Action<string> report, CancellationToken cancellationToken, bool userInitiated = false)
    {
        if (!Dispatcher.CheckAccess()) throw new InvalidOperationException("Startup must run on the pane dispatcher.");
        if (_startupApiActive || NativeOpenCodeEndpoint is null || _openCodeStatus is null)
            return FailPlanExecution("OpenCode 시작 요청을 준비할 수 없습니다.");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32 * 1024)
            return FailPlanExecution("입력 내용이 비어 있거나 너무 깁니다.");
        if (_startupApiUnconfirmed is { } pending && pending.Prompt != prompt)
            return FailPlanExecution("이전 시작 요청의 접수를 확인 중입니다.");
        if (_startupApiUnconfirmed is null && GetPlanExecutionBlockReason(canDispatch) is { Length: > 0 } block)
            return FailPlanExecution(block);

        LastPlanExecutionFailure = null;
        _startupApiActive = true;
        long inputVersion = _savedPlanComposerInterruptVersion;
        using var client = new OpenCodeAutomationClient(NativeOpenCodeEndpoint, WorkingDirectory, useTuiSelection: _usesNativeOpenCodeTui);
        using var lifetime = userInitiated
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HeadlessLifetimeCancellation)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, NativeAutomationCancellation, HeadlessLifetimeCancellation);
        string? ownedHeadlessSession = _usesNativeOpenCodeStatus ? _headlessSessionId : null;
        long ownedNativeViewGeneration = _nativeViewGeneration;
        bool Owned() => !lifetime.IsCancellationRequested && !_disposed && !_failed && IsRunning &&
            !WasContinuedExternally && !IsUserInputBlocked && !_manualInputOverride &&
            !_inputCapture.HasPendingInput && !_savedPlanComposerMayContainPendingInput &&
            (!_usesNativeOpenCodeStatus || ((_usesNativeOpenCodeTui || _nativeComposerDraft.Length == 0) &&
                _headlessSessionId == ownedHeadlessSession && _nativeViewGeneration == ownedNativeViewGeneration)) &&
            inputVersion == _savedPlanComposerInterruptVersion && CanDispatchCallback(canDispatch);
        DateTimeOffset preparationStarted = DateTimeOffset.UtcNow;
        int failures = 0;
        try
        {
            while (Owned())
            {
                try
                {
                    if (_startupApiUnconfirmed is null)
                    {
                        if (NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput)
                            return FailPlanExecution("OpenCode의 질문이나 승인을 먼저 처리해야 합니다.");
                        string? session = _boundAutomationSession ?? _autoResume?.ContinuationSessionId;
                        if (session is null)
                        {
                            report("◌ OpenCode 시작 세션 준비 중");
                            session = await client.CreateStartupSessionAsync("Terminal Notes", lifetime.Token);
                            if (!Owned()) return false;
                            if (_usesNativeOpenCodeStatus)
                            {
                                SetHeadlessSession(session);
                                ownedHeadlessSession = _headlessSessionId;
                                ownedNativeViewGeneration = _nativeViewGeneration;
                            }
                            else BindNativeAutomationSession(session);
                        }
                        report("◌ OpenCode 시작 세션 연결 중");
                        await client.SelectStartupSessionAsync(session, lifetime.Token);
                        if (!Owned()) return false;
                        var dispatch = new OpenCodeAutomationDispatch(session,
                            OpenCodeRecoveryClient.CreateMessageId(), prompt, DateTimeOffset.UtcNow);
                        await client.SubmitStartupPromptAsync(dispatch, () =>
                        {
                            // Client callbacks may run off the WPF thread.
                            Dispatcher.Invoke(() =>
                            {
                                if (!Owned() || NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput)
                                    throw new OperationCanceledException(lifetime.Token);
                                _startupApiUnconfirmed = dispatch;
                                _startupApiGeneration = _openCodeStatus.ObserveSubmission().Generation;
                                if (isQuestion) _autoResume?.Suspend("질문 입력 · 자동 진행 대기");
                                else _autoResume?.ObserveSubmittedPrompt(prompt, dispatch.SessionId, dispatch.MessageId);
                                report("◌ 입력 전달·수신 확인 중");
                            });
                        }, lifetime.Token, () => Dispatcher.Invoke(() =>
                        {
                            if (ReferenceEquals(_startupApiUnconfirmed, dispatch))
                            {
                                _startupApiUnconfirmed = null;
                                _openCodeStatus.SetSessionScope(_boundAutomationSession);
                                _autoResume?.Suspend("전송 전 중단 · 시작 요청 취소");
                            }
                        }));
                    }

                    if (!Owned()) return false;
                    var receipt = _startupApiUnconfirmed!;
                    if (await client.HasStartupMessageAsync(receipt, lifetime.Token))
                    {
                        if (!Owned()) return false;
                        _openCodeStatus.ConfirmApiSubmission(receipt.SessionId, receipt.MessageId, _startupApiGeneration);
                        if (!isQuestion) _autoResume?.ConfirmApiSubmission(receipt.SessionId, receipt.MessageId);
                        _startupApiUnconfirmed = null;
                        // TUI session selection can race its startup subscriber. Reattach the
                        // display independently; never repeat the accepted model request.
                        _ = RestoreStartupSessionDisplayAsync(receipt, _startupApiGeneration, inputVersion, canDispatch);
                        return true;
                    }
                    report("◌ 요청 접수 자동 재확인 중");
                }
                catch (OpenCodeAutomationException ex) when (ex.Code == "prompt_rejected")
                {
                    _startupApiUnconfirmed = null;
                    _openCodeStatus.SetSessionScope(_boundAutomationSession);
                    _autoResume?.Suspend("시작 요청 거절 · 자동 재시도 준비");
                    report("◌ 요청 거절 · 연결 자동 재확인 중");
                    // The server explicitly rejected the request. A fresh readiness
                    // check is required before another attempt, with bounded backoff.
                }
                catch (Exception ex) when (ex is OpenCodeAutomationException or HttpRequestException or IOException ||
                                           ex is OperationCanceledException && !lifetime.IsCancellationRequested)
                {
                    report(_startupApiUnconfirmed is null
                        ? "◌ 시작 연결 실패 · 자동 재시도 중"
                        : "◌ 연결 복구·요청 접수 자동 재확인 중");
                }
                if (!Owned()) return false;
                if (_startupApiUnconfirmed is null && DateTimeOffset.UtcNow - preparationStarted >= TerminalExecutionTiming.StartupReadyTimeout)
                    throw new TimeoutException("OpenCode 시작 연결이 응답하지 않아 해당 터미널을 자동 복구합니다.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + failures++ * 2)), lifetime.Token);
            }
            return FailPlanExecution("시작 요청의 소유권 또는 입력 상태가 변경되었습니다.");
        }
        finally { _startupApiActive = false; }
    }

    private async Task RestoreStartupSessionDisplayAsync(OpenCodeAutomationDispatch receipt, long generation,
        long inputVersion, Func<bool> canDispatch)
    {
        using var client = new OpenCodeAutomationClient(NativeOpenCodeEndpoint!, WorkingDirectory, useTuiSelection: _usesNativeOpenCodeTui);
        // These are display-only retries, covering delayed TUI subscription.
        foreach (int seconds in new[] { 1, 3, 10 })
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), NativeAutomationCancellation);
                if (_disposed || !IsRunning || _boundAutomationSession != receipt.SessionId ||
                    _inputCapture.HasPendingInput || _manualInputOverride || IsUserInputBlocked ||
                    inputVersion != _savedPlanComposerInterruptVersion || !CanDispatchCallback(canDispatch)) return;
                if (!_usesNativeOpenCodeStatus || _usesNativeOpenCodeTui)
                    await client.SelectStartupSessionAsync(receipt.SessionId, NativeAutomationCancellation);
                if (_openCodeStatus is not null)
                    await _openCodeStatus.RefreshConfirmedApiStatusAsync(receipt.SessionId, receipt.MessageId, generation,
                        NativeAutomationCancellation);
            }
            catch (Exception ex) when (ex is OpenCodeAutomationException or HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    internal bool CanRecoverUnsubmittedStartup => !_disposed && !WasContinuedExternally &&
        !_nativeAutomationActive && _startupApiUnconfirmed is null && !IsUserInputBlocked &&
        !_manualInputOverride && !HasLocalPendingInput() &&
        _autoResume?.HasUnfinishedWork != true &&
        NativeOpenCodeStatus is not (OpenCodeStatusKind.Busy or OpenCodeStatusKind.Pending or OpenCodeStatusKind.NeedsInput);

    internal async Task<bool> StopUnsubmittedStartupAsync()
    {
        if (!Dispatcher.CheckAccess() || !CanRecoverUnsubmittedStartup) return false;
        await StopOwnedOpenCodeProcessAsync();
        _failed = true;
        _flushTimer.Stop();
        return true;
    }
}
