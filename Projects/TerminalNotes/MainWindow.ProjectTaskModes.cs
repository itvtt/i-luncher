using System.IO;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private readonly Dictionary<string, bool> _projectTaskMultiModes = new(StringComparer.OrdinalIgnoreCase);

    // Legacy settings and run history must never enable the retired role scheduler.
    private bool IsMultiProjectTask(string project) => false;

    private static bool SingleTaskIsRunning(AutomationProjectRuntime runtime) => runtime.StartingSingle ||
        runtime.SingleTerminal?.Tag is TerminalTabState single && single.Pane.IsRunning &&
            (single.Pane.NativeOpenCodeStatus is OpenCodeStatusKind.Busy or OpenCodeStatusKind.Pending or OpenCodeStatusKind.NeedsInput ||
             single.Pane.HasUnfinishedAutoResumeWork);

    private RadioButton CreateTaskModeButton(string project, string label, bool multi)
    {
        var button = new RadioButton { GroupName = "TaskMode:" + project,
            IsChecked = IsMultiProjectTask(project) == multi, Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(2, 2, 2, 2), MinHeight = 26, Height = 26,
            ToolTip = multi ? "M · 멀티 · 지휘 / 검토 / 구현" : "S · 싱글 · 이 프로젝트의 단일 터미널" };
        SetTaskActionIcon(button, multi ? TaskGlyphKind.Multi : TaskGlyphKind.Single, label);
        button.SetResourceReference(StyleProperty, "TaskModeButton");
        button.Checked += (_, _) => SetProjectTaskMode(project, multi);
        return button;
    }

    private bool ProjectTaskIsRunning(string project)
    {
        var runtime = RuntimeFor(project);
        return _rerunningProjectTasks.Contains(CanonicalProjectPath(project)) || SingleTaskIsRunning(runtime);
    }

    private void SetProjectTaskMode(string project, bool multi)
    {
        multi = false;
        if (_updatingTaskOptions || IsMultiProjectTask(project) == multi) return;
        if (_closing || _closePending || _tabDocumentSaving || HasExternalTerminal(project) || _projectTaskControlWrites.Contains(project) || ProjectTaskIsRunning(project))
        {
            UpdateProjectTaskActivity();
            return;
        }
        _projectTaskMultiModes[CanonicalProjectPath(project)] = multi;
        SaveSettings();
        if (DirectoriesEqual(_selectedProjectPath, project))
        {
            SyncProjectPlanMode(project);
            TerminalTabs.SelectedItem = multi ? RuntimeFor(project).Team.GetValueOrDefault(AutomationRole.Coordinator) : RuntimeFor(project).SingleTerminal;
            UpdateWorkbenchTarget();
        }
        if (IsDisplayedTaskProject(project)) OpenProjectTask(project);
        UpdateProjectTaskActivity();
    }

    private void SyncProjectPlanMode(string project)
    {
        _planExecutionMode = PlanExecutionMode.CurrentTerminal;
    }

    private TabItem EnsureSingleProjectTerminal(string project)
        => EnsureSingleProjectTerminalCore(project, createPlanDocument: true);

    private TabItem EnsureSingleProjectTerminalWithoutPlanDocument(string project)
        => EnsureSingleProjectTerminalCore(project, createPlanDocument: false);

    private TabItem EnsureSingleProjectTerminalCore(string project, bool createPlanDocument)
    {
        var runtime = RuntimeFor(project);
        if (HasExternalTerminal(project))
            return runtime.SingleTerminal ?? throw new InvalidOperationException(ExternalTerminalBlockReason);
        if (runtime.SingleTerminal is { Tag: TerminalTabState existing } tab &&
            TerminalTabs.Items.Contains(tab) && existing.ExitCode is null && !existing.Pane.NativeAutomationDiagnostic.Failed)
            return tab;
        if (runtime.SingleTerminal is { Tag: TerminalTabState previous } old)
        {
            DetachTaskTerminal(old);
            TerminalTabs.Items.Remove(old);
            DisposeSafely(previous.Pane);
        }
        int slot = NextTerminalSlot();
        var launch = ResolveAutomationLaunch(project, ProjectDisplayName(project));
        var created = createPlanDocument
            ? CreateTerminalTab(slot, launch, ProjectDisplayName(project))
            : CreateTerminalTabWithoutPlanDocument(slot, launch, ProjectDisplayName(project));
        AttachSingleProjectTerminal(project, created);
        TerminalTabs.Items.Add(created);
        UpdateTerminalCount();
        return created;
    }

    private static bool IsNativeOpenCodeTaskPane(TerminalPane pane) => pane.StartupCommand is "opencode" or "opencode --auto";

    private void AttachSingleProjectTerminal(string project, TabItem tab)
    {
        var runtime = RuntimeFor(project);
        runtime.SingleTerminal = tab;
        ConfigureSingleContinuousImprovement(project);
        EnsureProjectTaskRow(project);
        if (!_projectTaskViews.TryGetValue(project, out var view))
        {
            view = new Grid { Visibility = Visibility.Hidden };
            view.ColumnDefinitions.Add(new ColumnDefinition());
            _projectTaskViews.Add(project, view);
            _taskDeck!.Children.Add(view);
        }
        if (_taskTerminalHosts.ContainsKey(tab)) return;
        var pane = ((TerminalTabState)tab.Tag).Pane;
        AttachAutoResumeDiagnostics(project, pane);
        pane.EnableTaskCompletionMonitoring();
        tab.Content = null;
        pane.AllowBackgroundAutomationStartup();
        var host = new ContentControl { Content = pane, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        var title = new TextBlock { Text = ProjectDisplayName(project), FontSize = 13,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis };
        var number = new TextBlock { FontSize = 12, Foreground = StateColorBrush("#A1A1AA"),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _terminalSlotNumbers[project] = number;
        var titleSurface = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        titleSurface.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleSurface.ColumnDefinitions.Add(new ColumnDefinition());
        var position = new StackPanel { Orientation = Orientation.Horizontal };
        position.Children.Add(new TextBlock { Text = "↕", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        position.Children.Add(number);
        position.Visibility = Visibility.Collapsed;
        titleSurface.Children.Add(position);
        Grid.SetColumn(title, 1); titleSurface.Children.Add(title);
        var heading = new ContentControl { Content = titleSurface, Margin = new Thickness(6, 0, 6, 0), MinHeight = 27,
            Background = System.Windows.Media.Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = System.Windows.Input.Cursors.SizeAll,
            Focusable = true, ToolTip = $"{ProjectDisplayName(project)} · 더블클릭 또는 F2를 눌러 작업 이름 편집\n{project}" };
        System.Windows.Automation.AutomationProperties.SetName(heading, $"{ProjectDisplayName(project)} 작업 이름. 더블클릭 또는 F2로 편집");
        heading.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (_titleEditor?.Parent == titleSurface) return;
            SelectTerminal(tab, false);
            heading.Focus();
        };
        heading.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
            e.Handled = true;
            BeginTerminalTaskNameEdit(tab);
        };
        heading.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.F2) { BeginTerminalTaskNameEdit(tab); e.Handled = true; } };
        EnableTaskSlotDrag(heading, project);
        var stop = new Button { Content = "■", Width = 28, Height = 26, Padding = new Thickness(0),
            Margin = new Thickness(4), ToolTip = "이 작업 중단" };
        stop.Click += async (_, _) => await StopProjectTaskAsync(project);
        var header = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var close = new Button { Content = "×", Width = 28, Height = 26, Padding = new Thickness(0), ToolTip = "칸 비우기 · 터미널과 작업은 계속 유지합니다" };
        close.Click += (_, _) => RemoveProjectSlot(project);
        var cmd = CreateCmdTerminalButton(tab);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(stop);
        actions.Children.Add(cmd);
        actions.Children.Add(close);
        actions.Visibility = Visibility.Collapsed;
        header.Children.Add(heading);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        bool actionsStacked = false;
        void ApplyHeaderLayout(double width)
        {
            bool stack = width < 280;
            if (actionsStacked == stack) return;
            actionsStacked = stack;
            if (stack)
            {
                Grid.SetColumn(heading, 0);
                Grid.SetColumnSpan(heading, 2);
                Grid.SetRow(heading, 0);
                Grid.SetColumn(actions, 0);
                Grid.SetColumnSpan(actions, 2);
                Grid.SetRow(actions, 1);
            }
            else
            {
                Grid.SetColumn(heading, 0);
                Grid.SetColumnSpan(heading, 1);
                Grid.SetRow(heading, 0);
                Grid.SetColumn(actions, 1);
                Grid.SetColumnSpan(actions, 1);
                Grid.SetRow(actions, 0);
            }
        }
        header.SizeChanged += (_, e) => ApplyHeaderLayout(e.NewSize.Width);
        ApplyHeaderLayout(header.ActualWidth);
        header.Visibility = Visibility.Visible;
        var body = new Grid(); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); body.RowDefinitions.Add(new RowDefinition());
        body.Children.Add(header); Grid.SetRow(host, 1); body.Children.Add(host);
        var frame = new Border { Child = body, Tag = tab, Margin = new Thickness(0), BorderThickness = new Thickness(0) };
        Grid.SetColumnSpan(frame, 3);
        view.Children.Add(frame);
        _taskTerminalHosts.Add(tab, host);
        _taskTerminalLabels.Add(tab, title);
        UpdateProjectTaskView(runtime);
    }

    private void UpdateProjectTaskView(AutomationProjectRuntime runtime)
    {
        if (!_projectTaskViews.TryGetValue(runtime.Project, out var view)) return;
        bool multi = IsMultiProjectTask(runtime.Project);
        foreach (var frame in view.Children.OfType<Border>())
            frame.Visibility = (ReferenceEquals(frame.Tag, runtime.SingleTerminal) != multi) ? Visibility.Visible : Visibility.Hidden;
    }

    private Task ExecuteSingleProjectPlanAsync(string project, TabDocumentSnapshot saved, Func<bool>? bindingStillValid = null, bool ownsRerunReservation = false) =>
        ExecuteSingleProjectPlanCoreAsync(project, saved, null, bindingStillValid, ownsRerunReservation);

    private Task RequestInitialProjectPlanInputAsync(string project) =>
        ExecuteSingleProjectPlanCoreAsync(project, null, null, requestPlanInput: true);

    private async Task ExecuteSingleProjectPlanCoreAsync(
        string project, TabDocumentSnapshot? saved, string? recoveryPrompt,
        Func<bool>? bindingStillValid = null, bool ownsRerunReservation = false, bool requestPlanInput = false)
    {
        if (requestPlanInput != (saved is null))
            throw new InvalidOperationException("초기 계획 입력 모드가 올바르지 않습니다.");
        if (HasExternalTerminal(project)) throw new InvalidOperationException(ExternalTerminalBlockReason);
        var runtime = RuntimeFor(project);
        if (ProjectTaskIsRunning(project) && !(ownsRerunReservation && _rerunningProjectTasks.Contains(CanonicalProjectPath(project)) && !SingleTaskIsRunning(runtime)))
            throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        runtime.StartingSingle = true;
        void ReportStartPhase(string phase, string eventName)
        {
            runtime.SingleStartPhase = phase;
            SetStatus(phase);
            UpdateProjectTaskActivity();
            RecordAutomationDiagnostic(eventName, phase, pane: runtime.SingleTerminal?.Tag is TerminalTabState state ? state.Pane : null);
        }

        runtime.SingleStopped = false;
        runtime.SingleError = null;
        using var startup = new CancellationTokenSource();
        using var startupNotice = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
        Task startupNoticeTask = Task.CompletedTask;
        runtime.SingleStartupCancellation = startup;
        BeginTaskNotificationAttempt(project, reusePendingAttempt: true);
        long intent = runtime.IntentVersion;
        TerminalPane? pane = null;
        try
        {

            startupNoticeTask = ReportSlowStartupProgressAsync(runtime, startupNotice.Token);
            ReportStartPhase("◌ OpenCode 실행 확인 중", "single_task_launch_check");
            if (bindingStillValid is not null && !bindingStillValid()) return;
            if (!await EnsureProjectOpenCodeReadyAsync(runtime, intent, startup.Token))
            {
                if (startup.IsCancellationRequested && !runtime.SingleStopped) startup.Token.ThrowIfCancellationRequested();
                return;
            }
            if (bindingStillValid is not null && !bindingStillValid()) return;
            if (!EnsureStartedProjectSlotVisible(project))
                throw new InvalidOperationException("터미널 8개 칸이 모두 사용 중입니다. 칸을 비운 뒤 시작하세요.");
            int startupAttempt = 0;
            while (!startup.IsCancellationRequested)
            {
                var tab = EnsureSingleProjectTerminal(project);
                SelectTaskProject(project);
                ShowProjectTask(project);
                if (!requestPlanInput) ConfigureSingleContinuousImprovement(project);
                pane = ((TerminalTabState)tab.Tag).Pane;
                pane.ShowProjectPlanInputNotice(requestPlanInput);
                if (!IsNativeOpenCodeTaskPane(pane))
                    throw new InvalidOperationException("이 프로젝트에 연결된 터미널이 OpenCode 세션이 아닙니다. 해당 터미널을 닫거나 칸에서 해제한 뒤 다시 시작하세요.");
                bool validateRecoveryPlan = false;
                bool StillOwned() => !_closing && !_closePending && runtime.IntentVersion == intent &&
                    !runtime.SingleStopped && !IsMultiProjectTask(project) && runtime.SingleTerminal == tab && TerminalTabs.Items.Contains(tab) &&
                    (bindingStillValid?.Invoke() ?? true) &&
                    (!validateRecoveryPlan || saved is not null && RecoveryPlanMatches(saved));
                using var readiness = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                readiness.CancelAfter(TerminalExecutionTiming.StartupReadyTimeout);
                try
                {
                    UpdateProjectTaskActivity();
                    ReportStartPhase("◌ 터미널 연결 확인 중", "single_task_terminal_check");
                    await pane.EnsureReadyAsync(readiness.Token);
                    ReportStartPhase("◌ OpenCode 입력 준비 확인 중", "single_task_input_ready_check");
                    while (!pane.CanExecuteSavedPlan && !pane.HasPendingStartupSubmission && StillOwned())
                    {
                        var state = pane.NativeAutomationDiagnostic;
                        if (state.Failed || state.Disposed || !pane.IsRunning)
                            throw new InvalidOperationException(pane.PlanExecutionBlockReason);
                        if (state.HasPendingInput || state.ManualOverride || pane.NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput) break;
                        await Task.Delay(100, readiness.Token);
                    }
                    if (!StillOwned()) return;
                    if (!pane.IsRunning) throw new InvalidOperationException(pane.PlanExecutionBlockReason);
                    if (!pane.CanExecuteSavedPlan && !pane.HasPendingStartupSubmission) throw new InvalidOperationException(pane.PlanExecutionBlockReason);
                    readiness.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (saved is not null &&
                        (!File.Exists(saved.Path) || !string.Equals(await File.ReadAllTextAsync(saved.Path), saved.Text, StringComparison.Ordinal)))
                        throw new InvalidOperationException("저장된 플랜이 바뀌어 실행하지 않았습니다.");
                    if (!StillOwned()) return;
                    validateRecoveryPlan = saved is not null;
                    ReportStartPhase("◌ 입력 전달·수신 확인 중", "single_task_submission_check");
                    bool DispatchOwned() => StillOwned() && !startup.IsCancellationRequested;
                    void ReportDispatch(string phase) => ReportStartPhase(phase, "single_task_startup_recovery");
                    bool accepted = requestPlanInput
                        ? await pane.SubmitStartupInputAsync("hi", true, DispatchOwned, ReportDispatch, startup.Token)
                        : recoveryPrompt is null
                            ? await pane.ExecuteStartupPlanAsync(saved!.Path, DispatchOwned, ReportDispatch, startup.Token)
                            : await pane.SubmitStartupInputAsync(recoveryPrompt, false, DispatchOwned, ReportDispatch, startup.Token);
                    if (!StillOwned()) return;
                    if (!accepted)
                        throw new InvalidOperationException(pane.LastPlanExecutionFailure ?? (pane.PlanExecutionBlockReason is { Length: > 0 } reason ? reason : "플랜 전송을 확인하지 못했습니다."));
                    pane.SetAutoResumeEnabled(_autoResumeEnabled);
                    RecordAutomationDiagnostic("single_task_submission_confirmed", requestPlanInput ? "hi 수신 확인" : "플랜 수신 확인", pane: pane);
                    runtime.SingleSubmitted = !requestPlanInput;
                    runtime.SinglePlanInputNeeded = requestPlanInput;
                    runtime.SingleError = null;
                    SetStatus(requestPlanInput
                        ? "OpenCode에 hi를 보냈습니다 · 플랜 입력 대기"
                        : "싱글 실행 시작 · " + ProjectDisplayName(project));
                    return;
                }
                catch (Exception ex) when (!startup.IsCancellationRequested && StillOwned() &&
                    pane.CanRecoverUnsubmittedStartup &&
                    (ex is TimeoutException || ex is OperationCanceledException && readiness.IsCancellationRequested ||
                     pane.NativeAutomationDiagnostic.Failed || !pane.IsRunning))
                {
                    if (!await pane.StopUnsubmittedStartupAsync() || !StillOwned()) throw;
                    startupAttempt++;
                    ReportStartPhase($"◌ 시작 실패 · 터미널 자동 재시작 {startupAttempt}", "single_task_startup_restart");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, startupAttempt * 3)), startup.Token);
                    if (!StillOwned()) return;
                }
            }
        }
        catch (Exception ex)
        {
            if (runtime.IntentVersion != intent) return;
            if (ex is OperationCanceledException && startup.IsCancellationRequested && !runtime.SingleStopped)
            {
                string reason = pane?.PlanExecutionBlockReason is { Length: > 0 } blockReason
                    ? blockReason
                    : "OpenCode 터미널 준비 시간이 초과되었습니다. 연결 상태를 확인한 뒤 다시 시작하세요.";
                var failure = new InvalidOperationException(reason, ex);
                runtime.SingleError = failure.Message;
                throw failure;
            }
            if (!runtime.SingleStopped) runtime.SingleError = ex.Message;
            throw;
        }
        finally
        {
            startupNotice.Cancel();
            try { await startupNoticeTask; }
            catch (OperationCanceledException) { }
            runtime.SingleStartupCancellation = null;
            runtime.StartingSingle = false;
            runtime.SingleStartPhase = null;
            UpdateProjectTaskActivity();
        }
    }

    private async Task ReportSlowStartupProgressAsync(AutomationProjectRuntime runtime, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                int elapsedSeconds = (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds;
                if (elapsedSeconds < TerminalExecutionTiming.SlowStartupNoticeAfter.TotalSeconds) continue;
                string? currentPhase = runtime.SingleStartPhase;
                if (string.IsNullOrWhiteSpace(currentPhase)) continue;
                string basePhase = currentPhase.Split(" · +", StringSplitOptions.None)[0];
                string notice = $"{basePhase} · + {elapsedSeconds}초 · 로딩 지연 확인 중";
                await Dispatcher.InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested || runtime.SingleStartPhase != currentPhase) return;
                    runtime.SingleStartPhase = notice;
                    SetStatus(notice);
                    UpdateProjectTaskActivity();
                });
            }
        }
        catch (OperationCanceledException) { }
    }
}
