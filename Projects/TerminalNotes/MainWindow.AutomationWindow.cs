using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{


    private void ShowAutomationWindow()
    {
        if (_closing || _closePending) return;
        if (_automationWindow is not null) { _automationWindow.Activate(); return; }
        var target = SelectedPlanTerminal();
        // Keep this captured Plan file as the window's target even if terminal selection changes.
        string project = CanonicalProjectPath(target?.Pane.WorkingDirectory ?? CurrentProjectPath);
        int slot = target?.Sequence ?? 1;
        string planDocumentPath = Path.GetFullPath(target?.PlanDocumentPath ?? InitialPlanDocumentPath(slot, project), project);
        var window = new Window
        {
            Owner = this, Title = "프로젝트 자동화", Width = 620, Height = 640,
            MinWidth = 440, MinHeight = 420,
            FontFamily = FontFamily, FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(16, 24, 36)), Foreground = Brushes.White,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.Resources.MergedDictionaries.Add(Resources);
        NameScope.SetNameScope(window, new NameScope());
        _automationWindow = window;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "프로젝트 이름", Margin = new Thickness(0, 12, 0, 4), Foreground = Brushes.LightSteelBlue });
        var projectName = new TextBox
        {
            Name = "ProjectAutomationNameInput",
            Text = ProjectDisplayName(project),
            MaxLength = 60,
            Height = 32,
            Padding = new Thickness(9, 5, 9, 5),
            ToolTip = project
        };
        window.RegisterName(projectName.Name, projectName);
        panel.Children.Add(projectName);
        var content = new TextBox
        {
            Name = "AutomationProjectContent", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 10), Padding = new Thickness(10),
            IsEnabled = false, ToolTip = planDocumentPath
        };
        panel.Children.Add(content);
        var start = new Button { Name = "AutomationStartButton", Content = "실행", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 100, Height = 34 };
        panel.Children.Add(start);
        var feedback = new TextBlock { Name = "AutomationStartStatus", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, FontSize = 13, Margin = new Thickness(0, 8, 0, 12) };
        panel.Children.Add(feedback);
        bool starting = false;
        AutomationPlanEditSession? planSession = null;
        void UpdateStart() => start.IsEnabled = !starting && planSession is not null &&
            IsValidAutomationProjectName(projectName.Text) && !string.IsNullOrWhiteSpace(content.Text);
        content.TextChanged += (_, _) => UpdateStart();
        projectName.TextChanged += (_, _) => UpdateStart();
        UpdateStart();
        start.Click += (_, _) => TrackJournalWork(StartAsync());
        async Task StartAsync()
        {
            if (starting || string.IsNullOrWhiteSpace(content.Text)) return;
            if (!IsValidAutomationProjectName(projectName.Text))
            {
                feedback.Text = "프로젝트 이름을 1~60자로 입력하세요.";
                projectName.Focus();
                return;
            }
            if (planSession is not null && RuntimeFor(planSession.Project).StartingRun) { feedback.Text = "이 프로젝트의 실행을 준비 중입니다."; return; }
            starting = true;
            bool planSaved = false;
            string acceptedProjectName = projectName.Text.Trim();
            content.IsReadOnly = true;
            projectName.IsReadOnly = true;
            UpdateStart();
            feedback.Text = "플랜을 저장하고 자동화를 준비하고 있습니다…";
            try
            {
                if (planSession is null) throw new InvalidOperationException("플랜 문서를 아직 불러오지 못했습니다.");
                string draft = content.Text;
                var saved = await SaveAutomationPlanAsync(planSession, draft);
                planSaved = true;
                if (planSession.Terminal is { } captured && !TerminalTabs.Items.Cast<TabItem>().Any(tab =>
                        tab.Tag is TerminalTabState open && ReferenceEquals(open, captured) &&
                        ReferenceEquals(open.Pane, captured.Pane)))
                    throw new IOException("플랜 문서는 저장했지만 대상 터미널이 닫혀 자동화를 시작하지 않았습니다.");
                if (planSession.Terminal is { } boundTerminal && !IsTerminalBoundToPlanPath(boundTerminal, planSession.Path))
                    throw new IOException("자동화 창을 연 뒤 대상 터미널의 플랜 MD 경로가 바뀌었습니다. 최신 경로에서 다시 실행하세요.");
                RememberProjectName(planSession.Project, acceptedProjectName);
                await StartProjectAutomationAsync(planSession.Project, saved.Text, planSession.Slot, saved.Path);
                feedback.Text = "자동화를 시작했습니다. 결과는 우측 프로젝트 MD 상단과 실행 기록에서 확인하세요.";
            }
            catch (Exception ex)
            {
                feedback.Text = (planSaved ? "플랜은 저장했지만 자동화를 시작하지 못했습니다 · " : "실행하지 못했습니다 · ") + ex.Message;
            }
            finally
            {
                starting = false;
                content.IsReadOnly = false;
                projectName.IsReadOnly = false;
                UpdateStart();
            }
        }
        _automationRunsList = new ListBox { Height = 90, Background = Brushes.Transparent, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 7) };
        _automationRunsList.SelectionChanged += (_, _) =>
        {
            if (_automationRunRefreshDepth == 0) _preferredAutomationRunId = null;
            UpdateAutomationSelection();
        };
        panel.Children.Add(_automationRunsList);
        var control = new WrapPanel();
        foreach (var (label, action) in new[] { ("일시정지", "pause"), ("재개", "resume"), ("중단", "stop"), ("기록 열기", "record") })
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 7, 4) };
            button.Click += (_, _) => TrackJournalWork(ControlAutomationAsync(action, window));
            control.Children.Add(button);
        }
        panel.Children.Add(control);
        _automationRunStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, FontSize = 13, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(_automationRunStatus);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_automationWindow, window)) return;
            _automationWindow = null; _automationRunsList = null; _automationRunStatus = null;
        };
        window.Show();
        _ = LoadPlanForWindowAsync();
        _ = RefreshAutomationRunsAsync();

        async Task LoadPlanForWindowAsync()
        {
            try
            {
                var loaded = await LoadAutomationPlanAsync(target, project, slot, planDocumentPath);
                if (target is not null && !IsTerminalBoundToPlanPath(target, planDocumentPath))
                    throw new IOException("자동화 창을 연 뒤 대상 터미널의 플랜 MD 경로가 바뀌었습니다. 자동화 창을 다시 여세요.");
                planSession = loaded;
                content.Text = loaded.InitialDraft;
                content.IsEnabled = true;
                feedback.Text = "플랜 준비됨";
                feedback.ToolTip = loaded.Path;
                UpdateStart();
            }
            catch (Exception ex)
            {
                feedback.Text = "Plan 문서를 불러오지 못했습니다 · " + ex.Message;
            }
        }
    }

    private async Task<AutomationRun> StartProjectAutomationAsync(string project, string content, int slot, string? planDocumentPath = null, Func<bool>? canStart = null)
    {
        if (!IsMultiProjectTask(project)) throw new InvalidOperationException("역할별 멀티 실행은 지원하지 않습니다. 프로젝트의 싱글 터미널을 사용하세요.");
        if (HasExternalTerminal(project)) throw new InvalidOperationException(ExternalTerminalBlockReason);
        project = CanonicalProjectPath(project);
        bool continuousImprovement = _projectTaskContinuousModes.GetValueOrDefault(project);
        DateTimeOffset? improvementUntil = _projectTaskImprovementUntil.GetValueOrDefault(project);
        var runtime = RuntimeFor(project);
        if (SingleTaskIsRunning(runtime)) throw new InvalidOperationException("싱글 작업을 완료하거나 중단한 뒤 시작하세요.");
        if (_closing || _closePending) throw new InvalidOperationException("앱 종료 처리 중입니다.");
        if (runtime.StartingRun) throw new InvalidOperationException("자동화를 준비 중입니다.");
        if (_projectTaskOptionWrites.Contains(project)) throw new InvalidOperationException("Loop 설정을 저장 중입니다.");
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("프로젝트 내용을 입력하세요.", nameof(content));
        if (!await TryInitializeAutomationAsync())
            throw new InvalidOperationException(_automationInitializationError ?? "지휘자 실행 준비 중입니다.");
        if (_closing || _closePending || runtime.StartingRun) throw new OperationCanceledException();
        runtime.StartingRun = true;
        UpdateSleepPrevention();
        long startIntent = ++runtime.IntentVersion;
        try
        {
            var store = new AutomationStore(project);
            var existingRuns = await store.LoadAsync();
            // The scheduler lock is held and new polls are suppressed while this
            // explicit approval is prepared. Never recover a claim a live poll owns.
            if (!runtime.Polling && _automationOwnership is not null)
            {
                foreach (var orphan in existingRuns.Where(run => run.State == AutomationState.Running && !HasLiveAutomationClaim(run)))
                {
                    if (orphan.ActiveDispatchToken is Guid token)
                        await store.TryRecoverOrphanedRunningAsync(orphan.Id, token, orphan.Revision);
                }
                existingRuns = await store.LoadAsync();
            }
            if (existingRuns.Any(run => run.State is AutomationState.Running or AutomationState.Scheduled))
                throw new InvalidOperationException("기존 지휘자 실행을 완료하거나 중단한 뒤 시작하세요.");
            if (!HasProjectName(project))
            {
                string acceptedName = (await RequestAutomationProjectNameAsync(project)).Trim();
                RememberProjectName(project, acceptedName);
            }

            string approvedPlan = content;
            if (planDocumentPath is not null)
            {
                string savedPlanPath = Path.GetFullPath(planDocumentPath, Path.GetFullPath(project));
                var planStore = new TerminalTabDocumentStore(project, savedPlanPath);
                string expectedPlanPath = Path.GetFullPath(planStore
                    .GetDocumentPath(slot, TerminalInputMode.Plan));
                if (!string.Equals(expectedPlanPath, savedPlanPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("저장된 Plan 경로를 확인할 수 없습니다.");
                approvedPlan += "\n\n## Persisted plan Markdown\nAbsolute file path: " + savedPlanPath +
                    "\nThis is the live Plan source for this run. Re-read it before each role and follow its latest saved contents. If the file is missing, deleted, empty, or unreadable, stop and report instead of falling back to the historical snapshot.";
            }

            // Run authorizes the selected plan using the configured default models.
            if (canStart is not null && !canStart()) throw new OperationCanceledException();
            if (runtime.IntentVersion != startIntent) throw new OperationCanceledException("프로젝트 실행 준비가 중단되었습니다.");
            // Let a harmless refresh/claim scan finish before creating the new run.
            // Live startup/stage work was rejected by the active-run check above,
            // so this cannot wait behind the run the user is trying to guard.
            while (runtime.Polling && !_closing && !_closePending)
                await Task.Delay(25);
            if (_closing || _closePending) throw new OperationCanceledException();
            if (runtime.IntentVersion != startIntent) throw new OperationCanceledException("프로젝트 실행 준비가 중단되었습니다.");
            existingRuns = await store.LoadAsync();
            if (existingRuns.Any(run => run.State is AutomationState.Running or AutomationState.Scheduled))
                throw new InvalidOperationException("기존 지휘자 실행을 완료하거나 중단한 뒤 시작하세요.");
            _projectTaskMultiModes[project] = true;
            SyncProjectPlanMode(project);
            SaveSettings();
            EnsureAutomationTeam(project);
            ShowProjectTask(project);
            await new ProjectWorkJournal(project).SubmitAsync(TerminalInputMode.Plan, approvedPlan, slot);
            _automationProjects.Add(project);
            await _automationProjectWriteGate.WaitAsync();
            try
            {
                string temporary = AutomationProjectsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_automationProjects));
                File.Move(temporary, AutomationProjectsPath, true);
            }
            finally { _automationProjectWriteGate.Release(); }
            if (_closing || _closePending) throw new OperationCanceledException();
            if (runtime.IntentVersion != startIntent) throw new OperationCanceledException("프로젝트 실행 준비가 중단되었습니다.");
            // Clicking Run approves a fresh plan. Retire paused history and schedule its
            // replacement together; the old run's metadata and results remain unchanged.
            var scheduled = await store.StartApprovedPlanAsync(approvedPlan,
                options: new AutomationExecutionOptions { ContinuousImprovement = continuousImprovement, ImprovementUntil = improvementUntil });
            if (runtime.IntentVersion != startIntent || _closing || _closePending)
            {
                await store.StopAsync(scheduled.Id);
                throw new OperationCanceledException("프로젝트 실행 준비가 중단되었습니다.");
            }
            RecordAutomationDiagnostic("run_started", "MD 실행 승인 · 첫 단계는 지휘자의 계획 구체화입니다.", run: scheduled);
            var approvedClaims = await store.ClaimDueAsync(DateTimeOffset.UtcNow, maxClaims: 1);
            var approvedClaim = approvedClaims.SingleOrDefault(claim => claim.RunId == scheduled.Id)
                ?? throw new InvalidOperationException("새 지휘자 실행의 첫 지휘 단계를 확보하지 못했습니다.");
            EnqueueAutomationClaim(store, approvedClaim, prioritizeRun: true);
            _preferredAutomationRunId = scheduled.Id;
            _automationTimer.Start();
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => _ = PollAutomationAsync()));
            await RefreshAutomationRunsAsync();
            UpdateAutomationProgressPanel();
            SetStatus("지휘자 실행을 예약했습니다 · OpenCode 준비 중");
            _ = ScanDocumentsAsync();
            return await store.GetAsync(scheduled.Id) ?? scheduled;
        }
        catch (Exception ex)
        {
            RecordAutomationDiagnostic("run_start_failed", "지휘자 실행 준비 실패 · " + ex.Message,
                severity: "error", exception: ex);
            throw;
        }
        finally { runtime.StartingRun = false; UpdateSleepPrevention(); }
    }

    private async Task ControlAutomationAsync(string action, Window owner)
    {
        if (_closing || _closePending) return;
        if ((_automationRunsList?.SelectedItem as AutomationListItem)?.Run is not { } run) return;
        var runtime = RuntimeFor(run.ProjectDirectory);
        try
        {
            var store = new AutomationStore(run.ProjectDirectory);
            switch (action)
            {
                case "pause":
                    // Suppress an in-flight watchdog before the ledger await.
                    _knownAutomationRuns[run.Id] = run with { PauseAfterCurrentStage = true };
                    _knownAutomationRuns[run.Id] = await store.PauseAsync(run.Id);
                    break;
                case "resume": await store.ResumeAsync(run.Id, DateTimeOffset.UtcNow); _automationTimer.Start(); break;
                case "stop":
                    runtime.IntentVersion++;
                    StopMicroManagement(runtime);
                    // Cancel local submission immediately; persistence must not leave a window
                    // in which a previously prepared native prompt can still be sent.
                    if (runtime.StartingNative is { } pendingStart && pendingStart.RunId == run.Id)
                        pendingStart.Pane.Interrupt();
                    if (runtime.Stage is { } pendingStage && pendingStage.Claim.RunId == run.Id)
                        pendingStage.Pane.Interrupt();
                    await store.StopAsync(run.Id);
                    if (runtime.StartingNative is { } starting && starting.RunId == run.Id)
                        ReleaseAutomationPane(starting.Pane, failed: true);
                    if (runtime.Stage is { } stage && stage.Claim.RunId == run.Id) await RetireStoppedStageAsync(stage);
                    break;
                case "record":
                    string path = Path.Combine(run.ProjectDirectory, "terminal-notes", "runs", run.Id.ToString("N"));
                    if (!Directory.Exists(path)) path = Path.Combine(run.ProjectDirectory, "terminal-notes");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
                    break;
            }
            if (action is "pause" or "resume" or "stop")
                RecordAutomationDiagnostic("run_" + action, action switch
                {
                    "pause" => "사용자가 일시정지를 요청했습니다.",
                    "resume" => "사용자가 실행을 재개했습니다.",
                    _ => "사용자가 실행을 중단했습니다."
                }, run: await store.GetAsync(run.Id) ?? run);
            await RefreshAutomationRunsAsync();
            UpdateAutomationProgressPanel();
        }
        catch (Exception ex)
        {
            RecordAutomationDiagnostic("run_control_failed", "자동화 제어 실패 · " + ex.Message, run: run, severity: "error", exception: ex);
            MessageBox.Show(owner, ex.Message, "자동화 제어");
        }
    }
}

