using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private readonly HashSet<TabItem> _terminalLifecycleOperations = [];
    private readonly HashSet<string> _terminalLifecycleProjects = new(StringComparer.OrdinalIgnoreCase);

    private async Task StopSelectedTerminalProcessAsync() => await RunSelectedTerminalLifecycleAsync(restart: false);

    private async Task ToggleSelectedTerminalProcessAsync()
    {
        if (TerminalTabs.SelectedItem is not TabItem tab || !TerminalTabs.Items.Contains(tab) || tab.Tag is not TerminalTabState state)
            return;
        if (state.Pane.IsRunning)
            await StopSelectedTerminalProcessAsync();
        else if (!IsStartingNativePane(state.Pane))
            await RestartSelectedTerminalProcessAsync();
    }

    private async Task RestartSelectedTerminalProcessAsync() => await RunSelectedTerminalLifecycleAsync(restart: true);

    private async Task RunSelectedTerminalLifecycleAsync(bool restart)
    {
        if (_closing || _closePending || TerminalTabs.SelectedItem is not TabItem tab ||
            !TerminalTabs.Items.Contains(tab) || tab.Tag is not TerminalTabState state)
            return;
        if (state.IsIndependent)
        {
            await RunIndependentTerminalLifecycleAsync(tab, state, restart);
            return;
        }
        string project = CanonicalProjectPath(state.Pane.WorkingDirectory);
        if (_terminalLifecycleOperations.Contains(tab) || !_terminalLifecycleProjects.Add(project)) return;
        _terminalLifecycleOperations.Add(tab);
        bool taskControlReserved = false;
        try
        {
            if (!CanControlTerminalProcess(project, state.Pane))
            {
                SetStatus("현재 터미널의 프로세스 소유권을 확인할 수 없어 변경하지 않았습니다.");
                return;
            }

            var owner = FindProjectTaskOwner(tab);
            var runtime = RuntimeFor(project);
            bool stopAutomation = owner is not null || IsStartingNativePane(state.Pane);
            if (stopAutomation)
            {
                if (_stoppingProjectTasks.Contains(project) || _rerunningProjectTasks.Contains(project) ||
                    _projectTaskControlWrites.Contains(project) || _projectTaskOptionWrites.Contains(project))
                {
                    SetStatus("Project task is already changing; retry when it finishes.");
                    return;
                }

                if (!_projectTaskControlWrites.Add(project)) return;
                taskControlReserved = true;
                long intentBeforeStop = runtime.IntentVersion;
                // Persist the task stop first so recovery/watchdog paths cannot launch it again.
                await StopProjectTaskAsync(project);
                var stopDeadline = DateTime.UtcNow.AddSeconds(10);
                while (runtime.IntentVersion == intentBeforeStop + 1 &&
                    (runtime.StartingSingle || runtime.SingleStartupCancellation is not null || _startingProjectTasks.Contains(project)))
                {
                    if (_closing || _closePending || !TerminalTabs.Items.Contains(tab)) return;
                    if (DateTime.UtcNow >= stopDeadline)
                        throw new TimeoutException("터미널 시작 취소가 아직 완료되지 않았습니다. 잠시 후 다시 시도하세요.");
                    await Task.Delay(50);
                }
                if (_closing || _closePending || !TerminalTabs.Items.Contains(tab)) return;
                if (!runtime.SingleStopped || runtime.IntentVersion != intentBeforeStop + 1 ||
                    runtime.StartingSingle || runtime.SingleStartupCancellation is not null ||
                    _startingProjectTasks.Contains(project) || _rerunningProjectTasks.Contains(project) ||
                    !ReferenceEquals(owner, FindProjectTaskOwner(tab)) || HasExternalTerminal(project))
                    throw new InvalidOperationException("Project automation did not stop safely; the terminal process was kept running.");

                var activeRuns = await new AutomationStore(project).LoadAsync();
                if (activeRuns.Any(run => run.State is AutomationState.Running or AutomationState.Scheduled or AutomationState.Paused))
                    throw new InvalidOperationException("A persisted automation run is still active; the terminal process was kept running.");
                if (_closing || _closePending || !TerminalTabs.Items.Contains(tab) || runtime.IntentVersion != intentBeforeStop + 1)
                    return;
            }

            await state.Pane.StopForTerminalLifecycleAsync();
            if (_closing || _closePending || !TerminalTabs.Items.Contains(tab)) return;
            if (!restart)
            {
                SetStatus("선택한 터미널 프로세스를 중단했습니다. 탭과 계획 초안은 유지됩니다.");
                return;
            }

            TabItem replacement = CreateTerminalLifecycleReplacement(state);
            var replacementState = (TerminalTabState)replacement.Tag;
            int index = TerminalTabs.Items.IndexOf(tab);
            if (index < 0) { DisposeSafely(replacementState.Pane); return; }
            bool wasSelected = ReferenceEquals(TerminalTabs.SelectedItem, tab);

            if (_taskTerminalHosts.Remove(tab, out var host)) host.Content = null;
            _taskTerminalLabels.Remove(tab);
            _cmdTerminalButtons.Remove(tab);
            foreach (var view in _projectTaskViews.Values)
                foreach (var frame in view.Children.OfType<System.Windows.Controls.Border>()
                    .Where(frame => ReferenceEquals(frame.Tag, tab)).ToArray())
                    view.Children.Remove(frame);

            tab.Content = null;
            TerminalTabs.Items.RemoveAt(index);
            TerminalTabs.Items.Insert(index, replacement);
            if (owner is not null)
            {
                if (ReferenceEquals(owner.SingleTerminal, tab))
                {
                    AttachSingleProjectTerminal(project, replacement);
                    if (owner.SinglePlanInputNeeded && !owner.SingleSubmitted)
                        replacementState.Pane.ShowProjectPlanInputNotice(true);
                    if (_projectPlanWaitSubmissionBaselines.TryGetValue(project, out var waitingBaseline) &&
                        waitingBaseline.Pane.TryGetTarget(out var waitingPane) && ReferenceEquals(waitingPane, state.Pane) &&
                        waitingBaseline.SubmissionGeneration == state.Pane.NativeSubmissionGeneration)
                    {
                        _projectPlanWaitSubmissionBaselines[project] = new(
                            new WeakReference<TerminalPane>(replacementState.Pane),
                            replacementState.Pane.NativeSubmissionGeneration);
                        UpdateProjectTaskActivity();
                        RefreshActiveTabDocumentControls();
                    }
                }
                else
                {
                    foreach (var role in ProjectTaskRoles)
                        if (owner.Team.TryGetValue(role, out var roleTab) && ReferenceEquals(roleTab, tab))
                            owner.Team[role] = replacement;
                    AttachProjectTaskTerminals(owner);
                }
            }
            if (wasSelected) TerminalTabs.SelectedItem = replacement;
            DisposeSafely(state.Pane);
            UpdateTerminalTitle(replacement);
            UpdateTerminalCount();
            SetStatus("선택한 터미널을 다시 시작했습니다. 계획은 자동 전송하지 않았습니다.");
        }
        catch (Exception exception)
        {
            SetStatus((restart ? "터미널 재시작" : "터미널 중단") + " 실패 · " + exception.Message);
        }
        finally
        {
            if (taskControlReserved) _projectTaskControlWrites.Remove(project);
            _terminalLifecycleOperations.Remove(tab);
            _terminalLifecycleProjects.Remove(project);
            UpdateTerminalLifecycleButtons();
        }
    }

    private bool CanControlTerminalProcess(string project, TerminalPane pane) =>
        !_closing && !_closePending && !_tabDocumentSaving && !_projectArchiveWrites.Contains(project) &&
        !_externalTerminalWrites.Contains(project) && !_projectTaskControlWrites.Contains(project) &&
        !_projectTaskOptionWrites.Contains(project) && !pane.WasContinuedExternally && !HasExternalTerminal(project);

    private AutomationProjectRuntime? FindProjectTaskOwner(TabItem tab)
    {
        foreach (var runtime in _automationRuntimes.Values)
            if (ReferenceEquals(runtime.SingleTerminal, tab) || runtime.Team.Values.Any(value => ReferenceEquals(value, tab)))
                return runtime;
        return null;
    }

    private TabItem CreateTerminalLifecycleReplacement(TerminalTabState priorState)
    {
        var pane = priorState.Pane;
        var launch = new ResolvedTerminalLaunch(pane.LaunchesCodex, pane.StartupCommand, priorState.Title,
            pane.WorkingDirectory, priorState.Tool);
        var replacement = CreateTerminalTabCore(priorState.Sequence, launch, priorState.Title,
            createPlanDocument: false, existingPlanDocumentPath: priorState.PlanDocumentPath, suppressAutoResume: true,
            independent: priorState.IsIndependent, independentSessionKey: priorState.IndependentSessionKey);
        var nextState = (TerminalTabState)replacement.Tag;
        nextState.InputMode = priorState.InputMode;
        nextState.InputDraft = priorState.InputDraft;
        return replacement;
    }

    private void UpdateTerminalLifecycleButtons()
    {
        if (StopTerminalProcessButton is null) return;
        if (TerminalTabs.SelectedItem is not TabItem tab || tab.Tag is not TerminalTabState state)
        {
            StopTerminalProcessButton.IsEnabled = false;
            return;
        }

        string project = CanonicalProjectPath(state.Pane.WorkingDirectory);
        bool running = state.Pane.IsRunning;
        bool available = CanControlTerminalProcess(project, state.Pane) && (running || !IsStartingNativePane(state.Pane)) &&
            !_terminalLifecycleOperations.Contains(tab) && !_terminalLifecycleProjects.Contains(project) && !_stoppingProjectTasks.Contains(project) &&
            !_rerunningProjectTasks.Contains(project);
        if (state.IsIndependent) available = !_closing && !_closePending && !state.Pane.WasContinuedExternally &&
            !_terminalLifecycleOperations.Contains(tab) && (running || !IsStartingNativePane(state.Pane));
        StopTerminalProcessButton.Content = running ? "\u25A0" : "\u25B6";
        StopTerminalProcessButton.ToolTip = running ? "\ud130\ubbf8\ub110\u0020\ud504\ub85c\uc138\uc2a4\u0020\uc911\ub2e8" : "\uc911\ub2e8\ud55c\u0020\ud130\ubbf8\ub110\u0020\uc2dc\uc791";
        System.Windows.Automation.AutomationProperties.SetName(StopTerminalProcessButton, running ? "\ud130\ubbf8\ub110\u0020\ud504\ub85c\uc138\uc2a4\u0020\uc911\ub2e8" : "\uc911\ub2e8\ud55c\u0020\ud130\ubbf8\ub110\u0020\uc2dc\uc791");
        StopTerminalProcessButton.IsEnabled = available;
    }
}
