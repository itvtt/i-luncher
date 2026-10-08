using System.Windows.Threading;
using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private CancellationTokenSource _nativeAutomationCancellation = new();
    private bool _nativeAutomationActive;
    private string? _nativeAutomationSupervisionNotice;
    private bool _allowBackgroundAutomationStartup;
    private DateTimeOffset? _startupUnresponsiveSince;

    internal void UseDedicatedAutomationSession() { _boundAutomationSession = null; _externalContinuationSession = null; _openCodeStatus?.SetSessionScope(null); }
    internal void BindNativeAutomationSession(string sessionId)
    {
        _boundAutomationSession = sessionId;
        _externalContinuationSession = null;
        _openCodeStatus?.SetSessionScope(sessionId);
        if (_usesNativeOpenCodeStatus && _headlessSessionId != sessionId)
        {
            _headlessSessionId = sessionId;
            _nativeViewGeneration++;
            PersistNativeSession(sessionId);
            ScheduleNativeStateRefresh();
        }
    }
    internal void ObserveOwnedAutomationSessions(IEnumerable<string> sessionIds) => _openCodeStatus?.ObserveOwnedSessionTree(sessionIds);

    internal void ObserveStartupReadiness()
    {
        if (IsReady && NativeOpenCodeStatus == OpenCodeStatusKind.Unknown &&
            !HasLocalPendingInput() && !_manualInputOverride && !IsUserInputBlocked)
            _startupUnresponsiveSince ??= DateTimeOffset.UtcNow;
        else _startupUnresponsiveSince = null;
    }

    internal void AllowBackgroundAutomationStartup()
    {
        _allowBackgroundAutomationStartup = true;
        TryStartTerminalWhenLaidOut();
    }

    internal void SetNativeAutomationSupervisionNotice(string? notice)
    {
        if (_nativeAutomationSupervisionNotice == notice) return;
        _nativeAutomationSupervisionNotice = notice;
        ApplyActivityChange();
    }

    internal Uri? NativeOpenCodeEndpoint { get; private set; }

    /// <summary>
    /// The cancellation token for the current native automation dispatch. A new
    /// token is created for each dispatch and canceled by Interrupt or Dispose.
    /// </summary>
    internal CancellationToken NativeAutomationCancellation => _nativeAutomationCancellation.Token;

    internal bool CanStartNativeAutomation => OnDispatcher(CanStartNativeAutomationCore, false);

    /// <summary>
    /// True while a dispatch still owns this pane. Native busy and approval
    /// states are valid during an active dispatch; local pending input is not.
    /// </summary>
    internal bool IsNativeAutomationActive => OnDispatcher(IsNativeAutomationActiveCore, false);

    internal OpenCodeStatusKind NativeOpenCodeStatus => OnDispatcher(
        () => WasContinuedExternally || _disposed || _failed || (_ready && !IsRunning) || _activityTracker.Activity == TerminalActivity.Exited
            ? OpenCodeStatusKind.Unknown : _openCodeStatus?.Snapshot.Kind ?? OpenCodeStatusKind.Unknown,
        OpenCodeStatusKind.Unknown);

    internal OpenCodeStatusMonitorPhase NativeStatusMonitorPhase => OnDispatcher(
        () => !_usesNativeOpenCodeStatus || WasContinuedExternally || _disposed || _failed || !IsRunning
            ? OpenCodeStatusMonitorPhase.NotMonitoring
            : _openCodeStatus?.MonitorPhase ?? OpenCodeStatusMonitorPhase.NotMonitoring,
        OpenCodeStatusMonitorPhase.NotMonitoring);

    // Readiness diagnostics contain flags only, never the user's pending input.
    internal AutomationPaneDiagnostic NativeAutomationDiagnostic => OnDispatcher(() => new AutomationPaneDiagnostic(
        NativeOpenCodeStatus.ToString(), IsReady, IsRunning, CanStartNativeAutomationCore(),
        _nativeAutomationActive, NativeOpenCodeEndpoint is not null, HasLocalPendingInput(),
        IsUserInputBlocked, _manualInputOverride, _failed, _disposed, _nativeAutomationCancellation.IsCancellationRequested)
        { ConnectionFailure = _openCodeStatus?.LastConnectionFailure },
        new AutomationPaneDiagnostic("Unknown", false, false, false, false, false, false, false, false, false, true, true));

    internal void BeginNativeAutomation()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(BeginNativeAutomation);
            return;
        }

        if (!CanStartNativeAutomationCore())
            throw new InvalidOperationException("The native OpenCode pane is not ready for automation.");

        _nativeAutomationCancellation.Cancel();
        _nativeAutomationCancellation.Dispose();
        _nativeAutomationCancellation = new CancellationTokenSource();
        _nativeAutomationActive = true;
        _startupUnresponsiveSince = null;
        _autoResume?.Suspend("자동화 진행 · 자동 진행 중복 방지");
        SetAutomationBusy(true);
        SetNativeAutomationSupervisionNotice("응답 변화 대기");
    }

    /// <summary>
    /// Records the native submission immediately before an automation prompt is
    /// posted. Invalid state throws so a caller cannot silently continue to POST.
    /// </summary>
    internal void ObserveNativeAutomationSubmission()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ObserveNativeAutomationSubmissionCore);
            return;
        }

        ObserveNativeAutomationSubmissionCore();
    }

    private void ObserveNativeAutomationSubmissionCore()
    {
        if (!IsNativeAutomationActiveCore() || _openCodeStatus?.Snapshot.Kind != OpenCodeStatusKind.Idle)
            throw new InvalidOperationException("Native automation state changed before the prompt was submitted.");

        _openCodeStatus.ObserveSubmission();
    }

    private bool CanStartNativeAutomationCore()
    {
        if (!_usesNativeOpenCodeStatus || _openCodeStatus is null || !IsReady || !IsRunning ||
            _disposed || _failed || _automationBusy || _nativeAutomationActive ||
            IsUserInputBlocked || _manualInputOverride || HasLocalPendingInput())
        {
            return false;
        }

        return _openCodeStatus.Snapshot.Kind == OpenCodeStatusKind.Idle;
    }

    private bool IsNativeAutomationActiveCore() =>
        _usesNativeOpenCodeStatus && _nativeAutomationActive && !_nativeAutomationCancellation.IsCancellationRequested &&
        _automationBusy && IsReady && IsRunning && !_disposed && !_failed &&
        !IsUserInputBlocked && !_manualInputOverride && !HasLocalPendingInput();

    private bool HasLocalPendingInput() =>
        _inputCapture.HasPendingInput || _savedPlanComposerMayContainPendingInput ||
        _pendingTextSends.Count != 0 || _savedPlanDispatchForNextSend is not null ||
        _savedPlanDispatches.Count != 0 || (_usesNativeOpenCodeStatus && _nativeComposerDraft.Length > 0);

    private T OnDispatcher<T>(Func<T> callback, T fallback)
    {
        if (Dispatcher.CheckAccess()) return callback();
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return fallback;

        try { return Dispatcher.Invoke(callback); }
        catch (InvalidOperationException) { return fallback; }
        catch (TaskCanceledException) { return fallback; }
    }

    private void CancelNativeAutomation()
    {
        _nativeAutomationActive = false;
        try { _nativeAutomationCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Closes only this pane's existing ConPTY job; retains its view and
    /// dispatch token so automatic recovery cannot be mistaken for a manual stop.</summary>
    internal async Task<bool> StopNativeAutomationProcessAsync()
    {
        if (!Dispatcher.CheckAccess()) throw new InvalidOperationException("Recovery must start on the pane dispatcher.");
        if (!_nativeAutomationActive || _nativeAutomationCancellation.IsCancellationRequested ||
            _disposed || IsUserInputBlocked || HasLocalPendingInput() || _manualInputOverride ||
            NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput) return false;
        await StopOwnedOpenCodeProcessAsync();
        _failed = true;
        _flushTimer.Stop();
        MarkWorkFailed();
        return true;
    }

    internal async Task<bool> StopUnresponsiveStartupProcessAsync()
    {
        if (!Dispatcher.CheckAccess()) throw new InvalidOperationException("Startup recovery must start on the pane dispatcher.");
        if (_nativeAutomationActive || _nativeAutomationCancellation.IsCancellationRequested ||
            _disposed || IsUserInputBlocked || HasLocalPendingInput() || _manualInputOverride || !IsReady ||
            NativeOpenCodeStatus != OpenCodeStatusKind.Unknown || _startupUnresponsiveSince is not { } since ||
            DateTimeOffset.UtcNow - since < TimeSpan.FromMinutes(3)) return false;
        await StopOwnedOpenCodeProcessAsync();
        _failed = true;
        _flushTimer.Stop();
        MarkWorkFailed();
        return true;
    }
}
