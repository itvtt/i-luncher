using System.Windows.Controls;
using Microsoft.Win32;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private SleepPreventionService _sleepPrevention = new();
    private string? _sleepPreventionError;

    private void InitializeSleepPrevention()
    {
        StatusLabel.ToolTip = "Loop 중 화면 보호기/자동 절전/화면 꺼짐 보류. 설정 변경 없음.";
        SystemEvents.PowerModeChanged += PowerModeChanged;
    }

    private bool HasWorkRequiringSystemAwake()
    {
        if (_closing || _resourcesDisposed) return false;
        foreach (var runtime in _automationRuntimes.Values)
        {
            if (runtime.SingleStopped || runtime.SingleError is not null) continue;
            bool preferenceEnabled = _projectTaskContinuousModes.GetValueOrDefault(runtime.Project) &&
                (!_projectTaskImprovementUntil.TryGetValue(runtime.Project, out var until) || until is null || until > DateTimeOffset.Now);
            if (runtime.StartingSingle && preferenceEnabled) return true;
            if (runtime.SingleTerminal?.Tag is not TerminalTabState single || single.ExitCode is not null ||
                !single.Pane.IsRunning || single.Pane.NativeAutomationDiagnostic.Failed) continue;
            var pane = single.Pane;
            if (!pane.IsContinuousImprovementEnabled) continue;
            var status = pane.NativeOpenCodeStatus;
            // Carry an enabled, unfinished improvement through idle/tray/approval waits,
            // and protect dispatch/startup even before the first TODO snapshot arrives.
            if (runtime.StartingSingle || pane.HasContinuousImprovementWork || pane.IsNativeAutomationActive ||
                status is OpenCodeStatusKind.Busy or OpenCodeStatusKind.Pending or OpenCodeStatusKind.NeedsInput) return true;
        }
        // Include explicitly opened non-task terminals only while they are doing work.
        return TerminalTabs.Items.Cast<TabItem>().Where(tab => !IsTaskTerminal(tab)).Any(tab =>
            tab.Tag is TerminalTabState state && state.ExitCode is null && state.Pane.IsRunning &&
            (state.Pane.NativeOpenCodeStatus is OpenCodeStatusKind.Busy or OpenCodeStatusKind.Pending or OpenCodeStatusKind.NeedsInput ||
             state.Pane.HasUnfinishedAutoResumeWork || state.Pane.Activity == TerminalActivity.Active));
    }

    private void UpdateSleepPrevention()
    {
        bool required = HasWorkRequiringSystemAwake();
        _sleepPrevention.SetRequired(required);
        if (_sleepPrevention.LastError is { } error && error != _sleepPreventionError)
        {
            SetStatus(error);
            RecordAutomationDiagnostic("sleep_prevention_failed", error, severity: "warning");
        }
        _sleepPreventionError = _sleepPrevention.LastError;
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _resourcesDisposed) return;
            _sleepPrevention.ResetAfterResume();
            UpdateSleepPrevention();
        });
    }

    private void DisposeSleepPrevention()
    {
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        _sleepPrevention.Dispose();
    }
}
