using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private string? _boundAutomationSession;
    private string? _externalContinuationSession;
    private bool _retiredForExternalContinuation;
    private string? _externalContinuationNotice;
    internal bool WasContinuedExternally { get; private set; }
    internal bool HasContinuationSession => ContinuationCandidate is not null;
    private string? ContinuationCandidate => (_retiredForExternalContinuation ? _externalContinuationSession : null) ?? _boundAutomationSession ??
        _autoResume?.ContinuationSessionId ?? _openCodeStatus?.Snapshot.SessionId;

    internal async Task<string> CaptureContinuationSessionAsync()
    {
        // Only reuse an ID already verified while this pane's server was alive.
        // A failed external launch can retry even though that server has retired.
        if (_retiredForExternalContinuation && _externalContinuationSession is { } captured) return captured;
        if (ContinuationCandidate is not { } candidate)
            throw new InvalidOperationException("이어 열 OpenCode 대화가 없습니다. 먼저 이 터미널에서 작업을 시작하세요.");
        // The controller retained this exact root only after a successful directory-scoped
        // native read. A crashed server cannot answer again; the new CMD verifies it too.
        if (!IsRunning && (_ready || _failed) &&
            (candidate == _autoResume?.VerifiedContinuationSessionId || candidate == _externalContinuationSession))
            return _externalContinuationSession = candidate;
        if (NativeOpenCodeEndpoint is null)
            throw new InvalidOperationException("원래 OpenCode 세션의 연결을 확인하지 못했습니다.");
        return _externalContinuationSession = await ExternalTerminalLauncher.ResolveRootSessionAsync(NativeOpenCodeEndpoint, WorkingDirectory, candidate);
    }

    internal async Task StopForExternalContinuationAsync()
    {
        if (_retiredForExternalContinuation && !IsRunning) return;
        IsUserInputBlocked = true;
        CancelNativeAutomation();
        _autoResume?.Dispose();
        // Retire this pane's attach child and server together; the standalone CMD
        // opens the verified same session after the embedded process tree is gone.
        await StopOwnedOpenCodeProcessAsync();
        _openCodeStatus?.Dispose();
        _failed = true;
        _retiredForExternalContinuation = true;
        _externalContinuationNotice = "CMD 연결 확인 중";
        ApplyActivityChange();
    }

    internal void CompleteExternalContinuation(string session)
    {
        if (!_retiredForExternalContinuation || session != _externalContinuationSession)
            throw new InvalidOperationException("CMD에서 확인된 대화가 원래 세션과 일치하지 않습니다.");
        WasContinuedExternally = true;
        _externalContinuationNotice = null;
        ApplyActivityChange();
    }

    internal void MarkExternalContinuationFailed()
    {
        if (!_retiredForExternalContinuation || WasContinuedExternally) return;
        _externalContinuationNotice = "CMD 연결 실패 · CMD 버튼으로 재시도";
        ApplyActivityChange();
    }
}
