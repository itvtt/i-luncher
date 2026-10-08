using System.IO;
using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private OpenCodeAutoResumeController? _autoResume;
    private string? _autoResumeStatus;
    internal Func<OpenCodeStalledRestart, Task<bool>>? RestartStalledProcessAsync { get; set; }
    internal bool HasUnfinishedAutoResumeWork => _autoResume?.HasUnfinishedWork == true;
    internal bool HasContinuousImprovementWork => _autoResume?.HasContinuousImprovementWork == true;
    internal bool IsContinuousImprovementEnabled => _autoResume?.IsContinuousImprovementEnabled == true;
    internal bool IsAutoResumePaused => _autoResume?.IsRecoveryPaused == true;
    internal bool HasVerifiedTaskCompletion => _autoResume?.HasVerifiedCompletion == true;
    internal bool HasVerifiedNotificationStall => _autoResume?.HasVerifiedNotificationStall == true;
    internal string? AutoResumeStatus => _autoResume?.Status;
    internal void EnableTaskCompletionMonitoring() => _autoResume?.EnableTaskCompletionMonitoring();

    private void InitializeAutoResume(Uri endpoint, string? planPath)
    {
        BindSavedPlanExecutionPath(planPath);
        var client = new OpenCodeRecoveryClient(endpoint, WorkingDirectory);
        _autoResume = new OpenCodeAutoResumeController(WorkingDirectory, planPath, client,
            () => !_disposed && !_failed && !IsUserInputBlocked && !_manualInputOverride && !_automationBusy &&
                  IsRunning && _openCodeStatus?.Snapshot.Kind is not (null or OpenCodeStatusKind.Unknown));
        _autoResume.RestartStalledProcessAsync = request => Dispatcher.InvokeAsync(async () =>
            RestartStalledProcessAsync is { } restart && await restart(request)).Task.Unwrap();
        _openCodeStatus!.RecoverySignalReceived += _autoResume.ObserveNativeSignal;
        _autoResume.StatusChanged += status =>
        {
            if (_disposed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                _autoResumeStatus = _autoResume?.Status;
                ApplyActivityChange();
            });
        };
        _autoResume.DiagnosticEmitted += diagnostic =>
        {
            foreach (Action<OpenCodeAutoResumeDiagnostic> handler in AutoResumeDiagnosticEmitted?.GetInvocationList() ?? [])
            {
                try { handler(diagnostic); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Auto-resume diagnostic observer failed: {ex.GetType().Name}"); }
            }
        };
        if (_usesNativeOpenCodeStatus)
        {
            if (_headlessOpenCodeHost is { } headlessHost) headlessHost.Exited += _ => _autoResume?.Dispose();
        }
        else _session.Exited += _ => _autoResume?.Dispose();
        _autoResume.Start();
    }

    internal async Task<bool> StopStalledProcessAsync(OpenCodeStalledRestart request)
    {
        if (!Dispatcher.CheckAccess()) throw new InvalidOperationException("Recovery requires the pane dispatcher.");
        if (_disposed || _failed || !IsReady || WasContinuedExternally || IsUserInputBlocked ||
            HasLocalPendingInput() || _manualInputOverride || !request.CanRestart()) return false;
        if (!await request.VerifyAsync() || !request.CanRestart() || HasLocalPendingInput() ||
            NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput) return false;
        IsUserInputBlocked = true;
        CancelNativeAutomation();
        // Wait for this pane's owned job to exit before another OpenCode can write the same project.
        await StopOwnedOpenCodeProcessAsync();
        _failed = true;
        _openCodeStatus?.Dispose();
        ApplyActivityChange();
        return !IsRunning;
    }
    internal void PreserveRestartInstruction(OpenCodeStalledRestart request) =>
        _autoResume?.PreserveRestartInstruction(request.Prompt, request.OriginalInstruction);

    public void SetAutoResumeEnabled(bool enabled) => _autoResume?.SetEnabled(enabled);

    public void ConfigureSingleContinuousImprovement(string project, bool enabled, DateTimeOffset? until = null)
    {
        string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkingDirectory));
        string supplied = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        if (!string.Equals(expected, supplied, StringComparison.OrdinalIgnoreCase)) return;
        _autoResume?.SetContinuousImprovement(enabled, until);
    }
}
