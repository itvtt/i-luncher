using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private enum PlanExecutionMode { CurrentTerminal, NewTerminal, Conductor }
    private PlanExecutionMode _planExecutionMode;
    private readonly Dictionary<PlanExecutionMode, RadioButton> _planExecutionModes = [];

    private StackPanel CreatePlanExecutionControls()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };
        var row = new Grid();
        foreach (var mode in Enum.GetValues<PlanExecutionMode>())
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var button = new RadioButton
            {
                Content = ExecutionModeLabel(mode), GroupName = "PlanExecutionMode", IsChecked = mode == _planExecutionMode,
                Margin = new Thickness(mode == PlanExecutionMode.CurrentTerminal ? 0 : 4, 0, 0, 0),
                Padding = new Thickness(3, 3, 3, 3)
            };
            button.SetResourceReference(StyleProperty, "InputModeButton");
            button.Checked += (_, _) => SetPlanExecutionMode(mode);
            _planExecutionModes[mode] = button;
            Grid.SetColumn(button, row.ColumnDefinitions.Count - 1);
            row.Children.Add(button);
        }
        panel.Children.Add(row);
        return panel;
    }

    private static string ExecutionModeLabel(PlanExecutionMode mode) => mode switch
    {
        PlanExecutionMode.NewTerminal => "새 터미널",
        PlanExecutionMode.Conductor => "지휘자",
        _ => "현재 터미널"
    };

    private void SetPlanExecutionMode(PlanExecutionMode mode)
    {
        mode = PlanExecutionMode.CurrentTerminal;
        if (_tabDocumentSaving || _closing || _closePending || _planExecutionMode == mode) return;
        _planExecutionMode = mode;
        UpdatePlanExecutionControls();
        if (_activeTabDocumentPath is { } path && _tabDocumentStates.TryGetValue(path, out var edit))
            RefreshTabDocumentControls(edit);
    }

    private string? PlanExecutionBlockReason(TerminalTabState? source, PlanExecutionMode mode)
    {
        if (mode != PlanExecutionMode.CurrentTerminal) return "프로젝트는 하나의 터미널에서 실행합니다.";
        if (_closing || _closePending) return "실행 준비가 끝난 뒤 다시 시도하세요.";
        if (source is null && _selectedProjectPath is null) return "먼저 프로젝트를 선택하세요.";
        string project = CanonicalProjectPath(source?.Pane.WorkingDirectory ?? CurrentProjectPath);
        if (HasExternalTerminal(project)) return ExternalTerminalBlockReason;
        if (!string.Equals(project, CanonicalProjectPath(CurrentProjectPath), StringComparison.OrdinalIgnoreCase))
            return "프로젝트가 바뀌었습니다. 해당 프로젝트의 터미널을 선택하세요.";
        if (mode == PlanExecutionMode.CurrentTerminal)
        {
            if (ProjectTaskIsRunning(project)) return "이미 실행 중인 작업이 있습니다.";
            var single = RuntimeFor(project).SingleTerminal?.Tag as TerminalTabState;
            if (_taskWorkspaceVisible || source is null)
                return ProjectTaskPanePlanBlockReason(single?.Pane);
            if (source.Pane.CanExecuteSavedPlan) return null;
            if (IsNativeOpenCodeTaskPane(source.Pane))
            {
                var readiness = source.Pane.NativeAutomationDiagnostic;
                if (!readiness.Failed && !readiness.Disposed && !readiness.HasPendingInput && !readiness.ManualOverride &&
                    source.Pane.NativeOpenCodeStatus == OpenCodeStatusKind.Unknown)
                    return null;
            }
            return source.Pane.PlanExecutionBlockReason;
        }
        if (mode == PlanExecutionMode.NewTerminal)
        {
            if (ManualTerminalCount >= 8) return "새 터미널을 열려면 빈 자리가 필요합니다.";
            if (_autoStartCodex && !_openCodeReady) return "OpenCode 설치 확인이 필요합니다.";
            return null;
        }
        var runtime = RuntimeFor(project);
        if (runtime.StartingRun || runtime.Stage is not null || runtime.StartingNative is not null ||
            _knownAutomationRuns.Values.Any(run => string.Equals(CanonicalProjectPath(run.ProjectDirectory), project, StringComparison.OrdinalIgnoreCase) &&
                (run.State == AutomationState.Scheduled || run.State == AutomationState.Running && HasLiveAutomationClaim(run))))
            return "기존 지휘자 실행이 끝나거나 중단된 뒤 다시 시작하세요.";
        if (!_automationInitialized || _automationOwnership is null)
            return _automationInitializationError ?? "지휘자 실행 준비 중입니다.";
        return null;
    }

    private string? ProjectTaskPanePlanBlockReason(TerminalPane? pane)
    {
        if (pane is null) return null;
        if (!IsNativeOpenCodeTaskPane(pane))
            return "이 프로젝트에 연결된 터미널이 OpenCode 세션이 아닙니다. 해당 터미널을 닫거나 칸에서 해제한 뒤 다시 시작하세요.";
        if (pane.CanExecuteSavedPlan) return null;
        var readiness = pane.NativeAutomationDiagnostic;
        if (!readiness.Failed && !readiness.Disposed && !readiness.HasPendingInput && !readiness.ManualOverride &&
            pane.NativeOpenCodeStatus == OpenCodeStatusKind.Unknown)
            return null;
        return pane.PlanExecutionBlockReason;
    }
    private void UpdatePlanExecutionControls()
    {
        var source = _composerState;
        string projectName = ProjectDisplayName(source?.Pane.WorkingDirectory ?? _selectedProjectPath ?? CurrentProjectPath);
        foreach (var (mode, button) in _planExecutionModes)
        {
            button.IsEnabled = !_tabDocumentSaving && !_closing && !_closePending &&
                (mode != PlanExecutionMode.CurrentTerminal || source is not null);
            button.IsChecked = mode == _planExecutionMode;
            button.ToolTip = mode switch
            {
                PlanExecutionMode.NewTerminal => "새 터미널에서 실행 · " + projectName,
                PlanExecutionMode.Conductor => "지휘자 팀으로 실행 · " + projectName,
                _ when source is null => "실행할 터미널이 없습니다.",
                _ => $"터미널 {source.Sequence} · {source.Title}"
            };
        }
    }
    private async Task ExecuteSavedPlanAsync(TerminalTabState? source, TabDocumentSnapshot snapshot, PlanExecutionMode mode)
    {
        if (source is null && _selectedProjectPath is null)
            throw new InvalidOperationException("먼저 프로젝트를 선택하세요.");
        string project = CanonicalProjectPath(source?.Pane.WorkingDirectory ?? CurrentProjectPath);
        const int projectPlanSlot = 1;
        TerminalTabState? target = source;
        bool PlanPanelTerminalStillOwned()
        {
            if (ReferenceEquals(_composerState, target)) return true;
            return RuntimeFor(project).SingleTerminal is { Tag: TerminalTabState taskPane } taskTab &&
                TerminalTabs.Items.Contains(taskTab) && ReferenceEquals(_composerState, taskPane) &&
                DirectoriesEqual(taskPane.Pane.WorkingDirectory, project);
        }
        bool BindingAndPlanUnchanged() => !_closing && !_closePending && _planExecutionMode == mode &&
            string.Equals(CanonicalProjectPath(CurrentProjectPath), project, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(DefaultProjectPlanPath(project)), Path.GetFullPath(snapshot.Path), StringComparison.OrdinalIgnoreCase) &&
            PlanPanelTerminalStillOwned() &&
            (target is null || TerminalTabs.Items.Cast<TabItem>().Any(tab => ReferenceEquals(tab.Tag, target)) &&
                string.Equals(CanonicalProjectPath(target.Pane.WorkingDirectory), project, StringComparison.OrdinalIgnoreCase) &&
                IsTerminalBoundToPlanPath(target, snapshot.Path)) &&
            File.Exists(snapshot.Path) && string.Equals(File.ReadAllText(snapshot.Path), snapshot.Text, StringComparison.Ordinal);

        if (!BindingAndPlanUnchanged()) throw new InvalidOperationException("선택한 프로젝트나 MD가 바뀌어 실행하지 않았습니다.");
        if (PlanExecutionBlockReason(source, mode) is { } reason) throw new InvalidOperationException(reason);
        if (mode == PlanExecutionMode.Conductor)
        {
            await StartProjectAutomationAsync(project, snapshot.Text, source?.Sequence ?? projectPlanSlot, snapshot.Path, BindingAndPlanUnchanged);
            SetStatus("지휘자 실행 시작");
            return;
        }
        if (_taskWorkspaceVisible || mode == PlanExecutionMode.CurrentTerminal)
        {
            await ExecuteSingleProjectPlanAsync(project, snapshot, BindingAndPlanUnchanged);
            return;
        }
        if (mode == PlanExecutionMode.NewTerminal)
        {
            var before = TerminalTabs.Items.Cast<TabItem>().ToHashSet();
            AddTerminalCore();
            var created = TerminalTabs.Items.Cast<TabItem>().SingleOrDefault(tab => !before.Contains(tab));
            if (created?.Tag is not TerminalTabState newTarget) throw new InvalidOperationException("새 터미널을 만들지 못했습니다.");
            target = newTarget;
            UpdateWorkbenchTarget();
            if (!_autoStartCodex) throw new InvalidOperationException("저장된 플랜은 OpenCode 터미널에서만 실행할 수 있습니다.");
            using var startup = new CancellationTokenSource(TerminalExecutionTiming.StartupReadyTimeout);
            DateTimeOffset startupBegan = DateTimeOffset.UtcNow;
            int lastNoticeSecond = 0;
            SetStatus("◌ 새 터미널 연결 확인 중");
            await target.Pane.EnsureReadyAsync(startup.Token);
            if (!BindingAndPlanUnchanged()) throw new OperationCanceledException();
            SetStatus("◌ 새 터미널의 OpenCode 입력 준비 확인 중");
            while (!target.Pane.CanExecuteSavedPlan && !startup.IsCancellationRequested)
            {
                if (!BindingAndPlanUnchanged()) throw new OperationCanceledException();
                var state = target.Pane.NativeAutomationDiagnostic;
                if (target.ExitCode is not null || state.Failed || state.Disposed || !target.Pane.IsRunning) break;
                int elapsedSeconds = (int)(DateTimeOffset.UtcNow - startupBegan).TotalSeconds;
                if (elapsedSeconds >= TerminalExecutionTiming.SlowStartupNoticeAfter.TotalSeconds && elapsedSeconds > lastNoticeSecond)
                {
                    lastNoticeSecond = elapsedSeconds;
                    SetStatus($"◌ 새 터미널의 OpenCode 입력 준비 확인 중 · + {elapsedSeconds}초 · 로딩 지연 확인 중");
                }
                await Task.Delay(100, startup.Token);
            }
            if (startup.IsCancellationRequested) startup.Token.ThrowIfCancellationRequested();
            if (!target.Pane.CanExecuteSavedPlan)
                throw new InvalidOperationException(target.Pane.PlanExecutionBlockReason);
            startup.CancelAfter(Timeout.InfiniteTimeSpan);
            bool DispatchOwned() => !startup.IsCancellationRequested && BindingAndPlanUnchanged();
            if (!BindingAndPlanUnchanged()) throw new OperationCanceledException();
            if (!await target.Pane.ExecuteSavedPlanAsync(snapshot.Path, DispatchOwned))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(target.Pane.PlanExecutionBlockReason)
                    ? "플랜 전송을 확인하지 못했습니다. 터미널 상태를 확인하세요."
                    : target.Pane.PlanExecutionBlockReason);
            if (ReferenceEquals(_composerState, target)) target.Pane.FocusTerminal();
            SetStatus($"터미널 {target.Sequence} · 플랜 실행");
            return;
        }
        if (!BindingAndPlanUnchanged()) throw new OperationCanceledException();
        if (target is null || !await target.Pane.ExecuteSavedPlanAsync(snapshot.Path, BindingAndPlanUnchanged))
            throw new InvalidOperationException(target is null || string.IsNullOrWhiteSpace(target.Pane.PlanExecutionBlockReason)
                ? "플랜 전송을 확인하지 못했습니다. 터미널 상태를 확인하세요."
                : target.Pane.PlanExecutionBlockReason);
        if (ReferenceEquals(_composerState, target)) target.Pane.FocusTerminal();
        SetStatus($"터미널 {target.Sequence} · 플랜 실행");
    }
}
