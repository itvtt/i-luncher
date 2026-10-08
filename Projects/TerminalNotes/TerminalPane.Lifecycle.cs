namespace TerminalNotes;

public sealed partial class TerminalPane
{
    /// <summary>Stops only this pane's owned terminal process tree and leaves its rendered buffer in place.</summary>
    internal async Task StopForTerminalLifecycleAsync()
    {
        if (_disposed) return;

        ReportLifecycleStage("user_terminal_stop_requested");
        Interlocked.Exchange(ref _sessionCloseStarted, 1);

        _ready = false;
        _failed = true;
        IsUserInputBlocked = true;
        _automationBusy = false;
        _workFailed = false;
        CancelNativeAutomation();
        try { _headlessManualDispatchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _autoResume?.Suspend("터미널 프로세스 중단 · 자동 진행 중지");
        CancelIncompleteSavedPlanDispatches();
        _flushTimer.Stop();
        _activityTimer.Stop();
        _nativeRefreshTimer?.Stop();

        try
        {
            await StopOwnedOpenCodeProcessAsync();
            _openCodeStatus?.Dispose();
            _activityTracker.MarkExited();
            ReportLifecycleStage("user_terminal_stopped");
            StatusChanged?.Invoke("터미널 프로세스를 중단했습니다.");
            ApplyActivityChange();
        }
        catch
        {
            ReportLifecycleStage("user_terminal_stop_failed");
            throw;
        }
    }
}
