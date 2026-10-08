using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerminalNotes;
using TerminalNotes.Services;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application();
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { _failures++; Console.WriteLine("FAIL " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return _failures;
    }

    private static async Task RunAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes automation progress " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "terminal-notes"));
        await File.WriteAllTextAsync(Path.Combine(fixture, "terminal-notes", "plan.md"), "# Isolated progress fixture\n");
        await File.WriteAllTextAsync(Path.Combine(fixture, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new
            { Workspace = fixture, CodexWorkspace = fixture, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        var window = new MainWindow(Path.Combine(fixture, "settings.json"), autoStartCodex: false);
        try
        {
            window.Show();
            await DispatcherIdle(window);
            var panel = Field<FrameworkElement>(window, "_automationProgressPanel");
            Assert(panel.Visibility == Visibility.Collapsed, "blank startup hides automation activity");
            SetField(window, "_selectedProjectPath", fixture);
            Call(window, "UpdateWorkbenchTarget");
            await DispatcherIdle(window);
            Update(window);
            Assert(panel.Visibility == Visibility.Collapsed, "a project without automation has no empty status or history panel");

            Guid runId = Guid.NewGuid();
            var run = new AutomationRun(runId, fixture, "# approved", new AutomationExecutionOptions(), null, 1,
                AutomationState.Running, AutomationRole.Reviewer, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow,
                null, null, 1, false, 0, "SENSITIVE SUMMARY", "SENSITIVE INSTRUCTION", null)
            {
                WorkflowPhase = AutomationWorkflowPhase.PlanReview,
                CoordinatorPlan = "SENSITIVE PLAN", LastReviewSummary = "SENSITIVE REVIEW"
            };
            var runs = Field<Dictionary<Guid, AutomationRun>>(window, "_knownAutomationRuns");
            runs[runId] = run;
            Update(window);
            await DispatcherIdle(window);
            Assert(panel.Visibility == Visibility.Visible, "current execution shows a compact activity visual");
            Assert(!Descendants(panel).OfType<Expander>().Any() &&
                !Descendants(panel).OfType<TextBlock>().Any(text => text.Text.Contains("SENSITIVE")),
                "plans, reviews, next instructions and history are absent from the right panel");
            var indicator = Field<FrameworkElement>(window, "_automationProgressIndicator");
            Assert(indicator.ActualWidth >= 40 && indicator.ActualHeight >= 40 && !IsAnimating(indicator),
                "waiting displays a real graphic without a pretend running animation");

            var claim = new AutomationStepClaim(runId, Guid.NewGuid(), run.Revision, AutomationRole.Coordinator, 1,
                fixture, run.ApprovedPlanSnapshot, run.Options, 0, null, "Prepare plan", DateTimeOffset.UtcNow)
                { WorkflowPhase = AutomationWorkflowPhase.Planning };
            Call(window, "RuntimeFor", fixture).GetType().GetField("Dispatching")!.SetValue(Call(window, "RuntimeFor", fixture), claim);
            Update(window);
            Assert(Field<TextBlock>(window, "_automationProgressRole").Text.Contains("지휘") &&
                Field<TextBlock>(window, "_automationProgressState").Text == "요청 준비" && !IsAnimating(indicator),
                "the actual Coordinator claim overrides run NextRole without claiming work has begun");

            DateTimeOffset now = DateTimeOffset.UtcNow;
            AssertTone(OpenCodeStatusKind.Busy, null, "Green", "진행 중", "busy stays green without pretending new output", now);
            AssertTone(OpenCodeStatusKind.Busy, now, "Green", "진행 중", "fresh response activity is green and animated", now, animating: true);
            AssertTone(OpenCodeStatusKind.Busy, now.AddSeconds(-9), "Green", "진행 중", "quiet busy stays green with its animation stopped", now);
            AssertTone(OpenCodeStatusKind.NeedsInput, now, "Orange", "승인·질문 대기", "approval overrides fresh activity", now);
            AssertTone(OpenCodeStatusKind.Unknown, now, "Gray", "상태 확인 불가", "connection loss overrides fresh activity", now);
            AssertTone(OpenCodeStatusKind.Idle, now, "Gray", "결과 확인 중", "idle cannot masquerade as active work", now);
            await VerifyQuietCoordinatorActivity(window, fixture, claim, indicator);
            VerifyCheckpointActivity(window, fixture, claim, indicator);
            await VerifyProjectTaskControls(window, fixture);
            await VerifyProjectTaskTree(window, fixture, run);

            var timer = Field<System.Windows.Threading.DispatcherTimer>(window, "_automationProgressTimer");
            Assert(timer.IsEnabled, "activity updates while the window is open");
            timer.Stop();
            Field<TextBlock>(window, "_automationProgressState").Text = "진행 중";
            Field<TextBlock>(window, "_automationProgressState").Foreground = Brushes.MediumSeaGreen;
            indicator.GetType().GetMethod("SetStatus")!.Invoke(indicator, new object[] { true, false, false, Brushes.MediumSeaGreen, "진행 중" });
            Assert(IsAnimating(indicator), "observed work animates the graphic");
            await DispatcherIdle(window);
            SaveVisualScreenshot(panel, Path.Combine(AppContext.BaseDirectory, "automation-activity-running.png"));
            panel.Visibility = Visibility.Collapsed;
            Assert(!IsAnimating(indicator), "hidden activity stops its animation");
            panel.Visibility = Visibility.Visible;
            indicator.GetType().GetMethod("SetStatus")!.Invoke(indicator, new object[] { false, true, false, Brushes.Orange, "승인·질문 대기" });
            Assert(!IsAnimating(indicator), "approval icon remains still");

            window.Width = 820;
            window.Height = 480;
            Call(window, "RuntimeFor", fixture).GetType().GetField("Dispatching")!.SetValue(Call(window, "RuntimeFor", fixture), null);
            runs[runId] = run with { State = AutomationState.Completed, LastSummary = new string('완', 900), DeliveryEvidence = "Service rendered", FinalVerificationEvidence = new string('증', 900) };
            Update(window);
            await DispatcherIdle(window);
            window.UpdateLayout();
            Assert(panel.ActualHeight <= 70 && Field<TextBox>(window, "_tabDocumentEditor").ActualHeight > 80,
                "the minimum window preserves editor space even after completion");
            Assert(Field<TextBlock>(window, "_automationProgressState").Text == "완료" && !IsAnimating(indicator),
                "completion is a static check graphic and concise status");
            Assert(!Descendants((FrameworkElement)window.FindName("WorkbenchHost")).OfType<TextBlock>()
                .Any(text => text.Text.Contains(new string('완', 20))), "completion does not reintroduce a summary banner");
            SaveVisualScreenshot((FrameworkElement)window.FindName("WorkbenchHost"), Path.Combine(AppContext.BaseDirectory, "automation-activity-completed.png"));
            Assert(runs[runId].CoordinatorPlan == "SENSITIVE PLAN" && runs[runId].LastReviewSummary == "SENSITIVE REVIEW",
                "removing panel detail preserves workflow evidence");
            timer.Start();
            Call(window, "DisposeResources");
            Assert(!timer.IsEnabled && !IsAnimating(indicator), "disposal stops polling and animation");
            Console.WriteLine("AUTOMATION PROGRESS TESTS PASSED");
        }
        finally
        {
            Call(window, "DisposeResources");
            window.Close();
            Console.WriteLine("Fixture: " + fixture);
        }
    }

    private static async Task VerifyProjectTaskControls(MainWindow window, string fixture)
    {
        string project = Path.Combine(fixture, "readable-task");
        Directory.CreateDirectory(project);
        var store = new AutomationStore(project);
        var run = await store.StartApprovedPlanAsync("Verify task controls", options: new AutomationExecutionOptions { ContinuousImprovement = true });
        var claim = (await store.ClaimDueAsync(DateTimeOffset.UtcNow)).Single();
        var runs = Field<Dictionary<Guid, AutomationRun>>(window, "_knownAutomationRuns");
        runs[run.Id] = (await store.GetAsync(run.Id))!;
        Call(window, "EnsureProjectTaskRow", project);
        Call(window, "ShowProjectTaskList");
        Call(window, "UpdateProjectTaskActivity");
        var rows = Field<System.Collections.IDictionary>(window, "_projectTaskRows");
        object row = rows[project]!;
        T Get<T>(string name) => (T)row.GetType().GetProperty(name)!.GetValue(row)!;
        var surface = Get<Border>("Surface");
        var loop = Get<CheckBox>("Loop");
        Assert(((SolidColorBrush)surface.Background).Color == Color.FromRgb(26, 26, 26) &&
            ((SolidColorBrush)surface.BorderBrush).Color == Color.FromRgb(64, 211, 146) &&
            !Descendants(surface).Where(child => child.GetType().Name == "AutomationActivityIndicator").Any(IsAnimating),
            "quiet ongoing TASK uses a simple neutral row with a green marker and no pretend activity");
        Assert(Get<TextBlock>("Deadline").Text == "∞" && Get<TextBlock>("Deadline").Visibility == Visibility.Visible,
            "continuous improvement without a schedule shows no fixed end on the task");
        Assert(loop.IsEnabled && loop.IsChecked == true, "continuous improvement remains editable during an owned running stage");
        loop.IsChecked = false;
        for (int index = 0; index < 100 && (await store.GetAsync(run.Id))!.Options.ContinuousImprovement; index++) await Task.Delay(20);
        var changed = (await store.GetAsync(run.Id))!;
        Assert(!changed.Options.ContinuousImprovement && changed.ActiveDispatchToken == claim.DispatchToken && changed.Revision == claim.Revision,
            "live checkbox disable persists without interrupting or invalidating the current result");
        DateTimeOffset until = DateTimeOffset.Now.AddHours(2);
        Assert(await (Task<bool>)Call(window, "ApplyProjectImprovementOptionsAsync", project, true, until), "deadline can be edited during execution");
        changed = (await new AutomationStore(project).GetAsync(run.Id))!;
        Assert(changed.Options.ImprovementUntil == until && changed.Options.ContinuousImprovement && Get<Button>("Until").Content.ToString()!.Contains("까지") &&
            Get<TextBlock>("Deadline").Text.Contains(until.LocalDateTime.ToString("MM/dd HH:mm")),
            "deadline survives disk reload and appears in the TASK row");
        window.Width = 820;
        await DispatcherIdle(window);
        window.UpdateLayout();
        Assert(Get<TextBlock>("Title").FontSize >= 17 && Get<TextBlock>("State").FontSize >= 13 &&
            Get<RadioButton>("Multi").FontSize >= 13 && ((SolidColorBrush)Get<TextBlock>("Title").Foreground).Color.G > 180,
            "TASK title and controls have readable type and bright text on dark backgrounds");
        Assert(!loop.IsVisible && !Get<Button>("Until").IsVisible && !Get<Button>("Record").IsVisible &&
            Get<FrameworkElement>("RolePanel").IsVisible && surface.ActualHeight <= 44,
            "one-line multi task keeps its three roles and moves secondary actions into details");
        Assert(Descendants(Get<FrameworkElement>("RolePanel")).OfType<TextBlock>()
            .Where(text => text.Text is "지휘" or "검토" or "구현").Select(text => text.Text).SequenceEqual(new[] { "지휘", "검토", "구현" }),
            "multi task divides roles into commander, reviewer and implementer in order");
        var roleCells = ((Panel)Get<FrameworkElement>("RolePanel")).Children.OfType<Border>().ToArray();
        Assert(roleCells.Length == 3 && roleCells.Max(cell => cell.ActualWidth) - roleCells.Min(cell => cell.ActualWidth) <= 1 &&
            roleCells.All(cell => cell.ActualHeight >= 28) && !Get<TextBlock>("State").IsVisible,
            "three equally sized inline role sections remain readable without duplicating status");
        var fixedControls = new FrameworkElement[] { Get<RadioButton>("Multi"), Get<RadioButton>("Single"), Get<Button>("Start"), Get<Button>("Stop"), Get<Button>("Open") };
        Assert(fixedControls.Cast<ContentControl>().All(control => control.Content is TextBlock &&
            !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)) && control.ToolTip is string && ToolTipService.GetShowOnDisabled(control)) &&
            Descendants(Get<FrameworkElement>("RolePanel")).OfType<TextBlock>().Where(text => text.IsVisible).All(text => text.Text is "지휘" or "검토" or "구현"),
            "task controls use readable labels with disabled tooltips; role cells contain only their names");
        Assert(fixedControls.Cast<ContentControl>().Select(control => ((TextBlock)control.Content).Text)
            .SequenceEqual(new[] { "M", "S", "▶", "■", "↗" }), "task actions have distinct mode letters and familiar operation symbols");
        var runningPositions = fixedControls.Select(control => control.TranslatePoint(new Point(), surface)).ToArray();
        double CenterY(FrameworkElement element) => element.TranslatePoint(new Point(0, element.ActualHeight / 2), surface).Y;
        Assert(fixedControls.Concat(roleCells).Append(Get<Button>("Select")).Append(Get<TextBlock>("Deadline"))
            .All(control => Math.Abs(CenterY(control) - CenterY(Get<TextBlock>("Title"))) <= 1),
            "project, role states, deadline and operation buttons share one horizontal line");
        var listScroll = Field<ScrollViewer>(window, "_taskListScroll");
        listScroll.ScrollToRightEnd();
        await DispatcherIdle(window);
        Assert(listScroll.ScrollableWidth > 0 && Get<Button>("Open").TranslatePoint(new Point(Get<Button>("Open").ActualWidth, 0), listScroll).X <= listScroll.ViewportWidth,
            "narrow panels scroll horizontally to the action buttons without wrapping or hiding an operation");
        double horizontalOffset = listScroll.HorizontalOffset;
        Call(window, "UpdateProjectTaskActivity");
        await DispatcherIdle(window);
        Assert(listScroll.HorizontalOffset == horizontalOffset, "status refresh preserves horizontal scroll position");
        double runningHeight = surface.ActualHeight;
        var deadline = Get<TextBlock>("Deadline");
        Assert(deadline.IsVisible && deadline.TranslatePoint(new Point(), surface).X >= Get<TextBlock>("State").TranslatePoint(new Point(), surface).X + Get<TextBlock>("State").ActualWidth &&
            deadline.TranslatePoint(new Point(deadline.ActualWidth, 0), surface).X <= surface.ActualWidth,
            "the live deadline fits on the right without colliding with state or fixed action controls");
        var scheduled = runs[run.Id];
        runs[run.Id] = scheduled with { Options = scheduled.Options with { ImprovementUntil = DateTimeOffset.Now.AddMinutes(-1) } };
        Call(window, "UpdateProjectTaskActivity");
        Assert(deadline.Text == "최종 검토", "expired improvement waits for final review instead of claiming an exact stop");
        runs[run.Id] = scheduled;
        Call(window, "UpdateProjectTaskActivity");
        Assert(fixedControls.All(control => control.IsVisible) && !Get<Button>("Start").IsEnabled && Get<Button>("Stop").IsEnabled,
            "run and stop stay visible in their own slots while running");
        SaveVisualScreenshot(surface, Path.Combine(AppContext.BaseDirectory, "task-running-readable.png"));
        var dialogCheck = new TaskCompletionSource();
        _ = window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            Window? dialog = null;
            try
            {
                dialog = window.OwnedWindows.Cast<Window>().Single(child => child.Title == "Loop 종료 시간");
                await DispatcherIdle(dialog);
                var date = Descendants(dialog).OfType<TextBox>().Single(input => input.Name == "ImprovementEndDate");
                var time = Descendants(dialog).OfType<TextBox>().Single(input => input.Name == "ImprovementEndTime");
                var apply = Descendants(dialog).OfType<Button>().Single(button => button.Content as string == "적용");
                time.Text = "25:99";
                apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert(dialog.IsVisible && Descendants(dialog).OfType<TextBlock>().Any(text => text.Text.Contains("날짜와 시간을 확인")),
                    "deadline dialog rejects invalid time without changing execution");
                DateTime end = DateTime.Now.AddHours(3);
                date.Text = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                time.Text = end.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                await DispatcherIdle(dialog);
                SaveVisualScreenshot((FrameworkElement)dialog.Content, Path.Combine(AppContext.BaseDirectory, "improvement-deadline-dialog.png"));
                apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                for (int index = 0; index < 100 && dialog.IsVisible; index++) await Task.Delay(20);
                var updated = (await store.GetAsync(run.Id))!;
                Assert(!dialog.IsVisible && updated.Options.ImprovementUntil?.LocalDateTime.ToString("HH:mm") == time.Text,
                    "real WPF deadline form applies to the running record and closes after persistence");
                dialogCheck.SetResult();
            }
            catch (Exception ex) { dialogCheck.SetException(ex); }
            finally { dialog?.Close(); }
        }));
        Assert(Get<Button>("DeadlineEdit").IsVisible && Get<Button>("DeadlineEdit").IsEnabled,
            "the task list exposes its deadline as an enabled button during execution");
        Get<Button>("DeadlineEdit").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await dialogCheck.Task;
        Assert(Field<string?>(window, "_displayedTaskProject") is null &&
            (await store.GetAsync(run.Id))!.ActiveDispatchToken == claim.DispatchToken &&
            (await store.GetAsync(run.Id))!.Revision == claim.Revision,
            "editing the deadline from the list preserves the list and active dispatch ownership");
        runs[run.Id] = await store.PauseAsync(run.Id);
        // Pause-after-stage is still in progress; a settled pause releases emphasis.
        Call(window, "UpdateProjectTaskActivity");
        Assert(roleCells.Any(cell => cell.ToolTip?.ToString()?.Contains("일시정지 예정") == true &&
            Descendants(cell).Any(icon => icon.GetType().Name == "TaskGlyph" && icon.GetType().GetProperty("Kind")!.GetValue(icon)!.ToString() == "Pause")) &&
            ((SolidColorBrush)surface.BorderBrush).Color == Color.FromRgb(64, 211, 146), "pause request stays visible in the current role until the stage finishes");
        runs[run.Id] = await store.StopAsync(run.Id);
        Call(window, "UpdateProjectTaskActivity");
        Assert(AutomationProperties.GetName(Get<Button>("Start")) == "이어서 실행" && Get<Button>("Start").Content is TextBlock { Text: "↻" } &&
            roleCells.Any(cell => cell.ToolTip?.ToString()?.Contains("중단") == true),
            "stopped task keeps a recognizable resume icon and full status tooltip");
        Assert(((SolidColorBrush)surface.BorderBrush).Color != Color.FromRgb(64, 211, 146) && ((SolidColorBrush)surface.Background).Color == Color.FromRgb(26, 26, 26),
            "explicit stop releases persistent TASK emphasis");
        Assert(deadline.Visibility == Visibility.Visible && deadline.Text == "—",
            "a stopped task keeps the deadline column visible without advertising ongoing improvement");
        await DispatcherIdle(window);
        Assert(fixedControls.Select(control => control.TranslatePoint(new Point(), surface)).SequenceEqual(runningPositions) &&
            surface.ActualHeight == runningHeight && Get<Button>("Start").IsEnabled && !Get<Button>("Stop").IsEnabled,
            "stopped and resumable states keep button positions and card height fixed");
        var stopped = runs[run.Id];
        runs[run.Id] = stopped with { State = AutomationState.Completed, DeliveryEvidence = null, FinalVerificationEvidence = null };
        Call(window, "UpdateProjectTaskActivity");
        Assert(((SolidColorBrush)surface.Background).Color == Color.FromRgb(26, 26, 26), "unverified completion never turns the task purple");
        runs[run.Id] = stopped with { State = AutomationState.Completed, DeliveryEvidence = "Service running", FinalVerificationEvidence = "Independent verification" };
        Call(window, "UpdateProjectTaskActivity");
        await DispatcherIdle(window);
        Assert(((SolidColorBrush)surface.Background).Color == Color.FromRgb(85, 38, 138) &&
            fixedControls.Select(control => control.TranslatePoint(new Point(), surface)).SequenceEqual(runningPositions),
            "verified completion turns only the task purple without moving controls");
        SaveVisualScreenshot(surface, Path.Combine(AppContext.BaseDirectory, "task-completed.png"));
        runs[run.Id] = stopped;
        Call(window, "UpdateProjectTaskActivity");
        Call(window, "ShowProjectTaskHistory", project);
        Window? history = null;
        for (int index = 0; index < 100 && history is null; index++)
        {
            history = Application.Current.Windows.Cast<Window>().FirstOrDefault(candidate => candidate.Owner == window && candidate.Title.StartsWith("실행 기록 ·"));
            if (history is null) await Task.Delay(20);
        }
        Assert(history is not null, "task history is reachable without the removed automation button");
        var historyPanel = (Grid)history!.Content;
        var historyDetail = historyPanel.Children.OfType<TextBox>().Single();
        Assert(historyDetail.IsReadOnly && historyDetail.Text.Contains("중단"), "history shows persisted results in a read-only view");
        history.Width = history.MinWidth; history.Height = history.MinHeight; history.UpdateLayout();
        Assert(historyDetail.ActualHeight > 80 && historyDetail.ActualWidth > 300, "history remains readable at minimum size");
        history.Close();
        window.Width = 1480;
    }

    private static async Task VerifyProjectTaskTree(MainWindow window, string fixture, AutomationRun template)
    {
        var runs = Field<Dictionary<Guid, AutomationRun>>(window, "_knownAutomationRuns");
        var rows = Field<System.Collections.IDictionary>(window, "_projectTaskRows");
        var taskOrder = Field<TreeViewItem>(window, "_taskOrderGroup");
        var tree = Field<TreeView>(window, "_taskTree");
        var projects = new[] { "zulu", "alpha", "mike" }.Select(name => Path.Combine(fixture, name)).ToArray();
        var sampleRuns = projects.Select(project => template with { Id = Guid.NewGuid(), ProjectDirectory = project }).ToArray();
        TreeViewItem Node(string project) => (TreeViewItem)rows[project]!.GetType().GetProperty("Node")!.GetValue(rows[project])!;
        string[] Order() => taskOrder.Items.Cast<TreeViewItem>().Select(node => (string)node.Tag).Where(projects.Contains).ToArray();
        foreach (var run in sampleRuns)
        {
            Directory.CreateDirectory(run.ProjectDirectory);
            runs[run.Id] = run;
            Call(window, "EnsureProjectTaskRow", run.ProjectDirectory);
        }
        Call(window, "UpdateProjectTaskActivity");
        Assert(tree.Items.Count == 1 && ReferenceEquals(tree.Items[0], taskOrder) &&
            Order().SequenceEqual(new[] { projects[1], projects[2], projects[0] }),
            "the task tree sorts project names independently of creation order");
        var originalNodes = projects.Select(Node).ToArray();
        var originalOrder = taskOrder.Items.Cast<TreeViewItem>().ToArray();
        for (int index = 0; index < 6; index++)
        {
            foreach (var run in sampleRuns.Reverse())
                runs[run.Id] = run with { UpdatedAt = DateTimeOffset.Now.AddSeconds(index), LastSummary = "new response " + index,
                    NextRole = (AutomationRole)(index % 3), WorkflowPhase = (AutomationWorkflowPhase)(index % 6) };
            Call(window, "UpdateProjectTaskActivity");
        }
        Assert(taskOrder.Items.Cast<TreeViewItem>().SequenceEqual(originalOrder) && projects.Select(Node).SequenceEqual(originalNodes),
            "responses and role handoffs preserve order and row identity");
        var alpha = sampleRuns[1];
        runs[alpha.Id] = alpha with { State = AutomationState.Paused };
        Call(window, "UpdateProjectTaskActivity");
        Assert(taskOrder.Items.Contains(Node(alpha.ProjectDirectory)) && Order().SequenceEqual(new[] { projects[1], projects[2], projects[0] }),
            "a settled pause preserves the task's position in the global list");
        foreach (var state in new[] { AutomationState.Stopped, AutomationState.Failed, AutomationState.Completed })
        {
            runs[alpha.Id] = alpha with { State = state, DeliveryEvidence = "partial result", FinalVerificationEvidence = null };
            Call(window, "UpdateProjectTaskActivity");
            Assert(taskOrder.Items.Contains(Node(alpha.ProjectDirectory)) && Order().SequenceEqual(new[] { projects[1], projects[2], projects[0] }),
                "unverified " + state + " preserves the task's global list position");
        }
        runs[alpha.Id] = alpha with { State = AutomationState.Scheduled };
        Call(window, "UpdateProjectTaskActivity");
        Assert(taskOrder.Items.Cast<TreeViewItem>().SequenceEqual(originalOrder), "resuming returns the task to its stable project-name position");
        runs[alpha.Id] = alpha with { State = AutomationState.Completed, DeliveryEvidence = "delivered", FinalVerificationEvidence = "verified" };
        Call(window, "UpdateProjectTaskActivity");
        Assert(taskOrder.Items.Contains(Node(alpha.ProjectDirectory)) && Order().SequenceEqual(new[] { projects[1], projects[2], projects[0] }),
            "verified completion preserves the task's global list position");
        await DispatcherIdle(window);
        var alphaSurface = (Border)rows[alpha.ProjectDirectory]!.GetType().GetProperty("Surface")!.GetValue(rows[alpha.ProjectDirectory])!;
        Assert(alphaSurface.IsVisible && ((SolidColorBrush)alphaSurface.Background).Color == Color.FromRgb(85, 38, 138),
            "the completed task stays visible with the same purple card in the global list");
        runs[alpha.Id] = runs[alpha.Id] with { State = AutomationState.Running };
        Call(window, "UpdateProjectTaskActivity");
        Assert(taskOrder.Items.Contains(Node(alpha.ProjectDirectory)) && Order().SequenceEqual(new[] { projects[1], projects[2], projects[0] }),
            "a new improvement cycle remains ongoing even when the previous cycle has completion evidence");
        runs[alpha.Id] = runs[alpha.Id] with { State = AutomationState.Completed };
        Call(window, "UpdateProjectTaskActivity");

        // Exercise native busy/idle/approval transitions without starting an external process.
        string singleProject = Path.Combine(fixture, "single-tree");
        Call(window, "EnsureProjectTaskRow", singleProject);
        Field<Dictionary<string, bool>>(window, "_projectTaskMultiModes")[singleProject] = false;
        using var pane = new TerminalPane(singleProject);
        using var monitor = new OpenCodeStatusMonitor(new Uri("http://127.0.0.1:43127/"), singleProject);
        using var recovery = new OpenCodeAutoResumeController(singleProject, null,
            new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43127/"), singleProject), () => false);
        SetField(pane, "_openCodeStatus", monitor);
        SetField(pane, "_autoResume", recovery);
        var stateType = typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!;
        var terminalState = Activator.CreateInstance(stateType)!;
        stateType.GetProperty("Pane")!.SetValue(terminalState, pane);
        var runtime = Call(window, "RuntimeFor", singleProject);
        runtime.GetType().GetField("SingleTerminal")!.SetValue(runtime, new TabItem { Tag = terminalState });
        foreach (var status in new[] { OpenCodeStatusKind.Busy, OpenCodeStatusKind.Idle, OpenCodeStatusKind.NeedsInput, OpenCodeStatusKind.Unknown, OpenCodeStatusKind.Idle })
        {
            SetField(monitor, "_snapshot", new OpenCodeStatusSnapshot(status, status == OpenCodeStatusKind.Idle, "tree task"));
            Call(window, "UpdateProjectTaskActivity");
            Assert(taskOrder.Items.Contains(Node(singleProject)), "single task stays in the global list through native " + status);
        }
        SetField(recovery, "_hasVerifiedCompletion", true);
        Call(window, "UpdateProjectTaskActivity");
        Assert(taskOrder.Items.Contains(Node(singleProject)), "verified single completion preserves its global list membership");
        runtime.GetType().GetField("SingleStopped")!.SetValue(runtime, true);
        Call(window, "UpdateProjectTaskActivity");
        Assert(taskOrder.Items.Contains(Node(singleProject)), "explicit single stop preserves its global list membership");
        runtime.GetType().GetField("SingleStopped")!.SetValue(runtime, false);
        SetField(monitor, "_snapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Busy, false, "new request"));
        Call(window, "UpdateProjectTaskActivity");
        stateType.GetProperty("ExitCode")!.SetValue(terminalState, 1);
        Call(window, "UpdateProjectTaskActivity");
        Assert(taskOrder.Items.Contains(Node(singleProject)), "a terminated single terminal preserves global list membership despite a stale busy snapshot");
        runtime.GetType().GetField("SingleTerminal")!.SetValue(runtime, null);

        for (int index = 0; index < 18; index++) Call(window, "EnsureProjectTaskRow", Path.Combine(fixture, "task-" + index.ToString("D2")));
        Call(window, "UpdateProjectTaskActivity");
        Assert(taskOrder.Items.Contains(Node(Path.Combine(fixture, "task-00"))) && taskOrder.Items.Count == rows.Count,
            "unstarted tasks enter the global list and every task appears exactly once");
        await DispatcherIdle(window);
        var scroll = Field<ScrollViewer>(window, "_taskListScroll");
        scroll.ScrollToVerticalOffset(180);
        await DispatcherIdle(window);
        double offset = scroll.VerticalOffset;
        Call(window, "UpdateProjectTaskActivity");
        await DispatcherIdle(window);
        Assert(scroll.ScrollableHeight > 0 && offset > 0 && scroll.VerticalOffset == offset, "ordinary status refresh preserves tree scroll position");
        scroll.ScrollToTop();
        await DispatcherIdle(window);
        SaveVisualScreenshot(window, Path.Combine(AppContext.BaseDirectory, "task-tree-design.png"));
    }

    private static async Task VerifyQuietCoordinatorActivity(MainWindow window, string fixture, AutomationStepClaim claim, object indicator)
    {
        using var pane = new TerminalPane(fixture);
        using var client = new OpenCodeAutomationClient(new Uri("http://127.0.0.1:43127/"), fixture);
        using var monitor = new OpenCodeStatusMonitor(new Uri("http://127.0.0.1:43127/"), fixture);
        SetField(pane, "_openCodeStatus", monitor);
        SetField(Field<ConPtySession>(pane, "_session"), "_running", true);
        var runtime = Call(window, "RuntimeFor", fixture);
        var stageType = typeof(MainWindow).GetNestedType("ActiveAutomationStage", BindingFlags.NonPublic)!;
        var stage = Activator.CreateInstance(stageType, new AutomationStore(fixture), claim,
            new AutomationCommand(fixture, "", "", "", "", AutomationRole.Coordinator), pane, null, client,
            new OpenCodeAutomationDispatch("fixture-session", "fixture-message"))!;
        var stageField = runtime.GetType().GetField("Stage")!;
        var runs = Field<Dictionary<Guid, AutomationRun>>(window, "_knownAutomationRuns");
        var originalRun = runs[claim.RunId];
        Call(window, "EnsureProjectTaskRow", fixture);
        var rows = Field<System.Collections.IDictionary>(window, "_projectTaskRows");
        var roles = (Panel)rows[fixture]!.GetType().GetProperty("RolePanel")!.GetValue(rows[fixture])!;
        var coordinator = (Border)roles.Children[0];
        void Refresh(OpenCodeStatusKind status)
        {
            SetField(monitor, "_snapshot", new OpenCodeStatusSnapshot(status, status == OpenCodeStatusKind.Idle, "fixture"));
            Update(window);
            Call(window, "UpdateProjectTaskActivity");
        }
        bool RowAnimating() => Descendants(coordinator).Where(element => element.GetType().Name == "AutomationActivityIndicator").Any(IsAnimating);
        bool Green() => ((SolidColorBrush)coordinator.Background).Color == Color.FromRgb(18, 100, 58);
        try
        {
            stageField.SetValue(runtime, stage);
            typeof(OpenCodeAutomationClient).GetProperty("LastActivityAt")!.SetValue(client, DateTimeOffset.UtcNow.AddSeconds(-30));
            Refresh(OpenCodeStatusKind.Busy);
            await DispatcherIdle(window);
            Assert(Green() && !RowAnimating() && !IsAnimating(indicator) &&
                Field<TextBlock>(window, "_automationProgressState").Text == "진행 중",
                "quiet Coordinator remains green in the row and running in the panel without animation");
            typeof(OpenCodeAutomationClient).GetProperty("LastActivityAt")!.SetValue(client, DateTimeOffset.UtcNow);
            Refresh(OpenCodeStatusKind.Busy);
            Assert(Green() && RowAnimating() && IsAnimating(indicator), "new Coordinator output animates both indicators");
            Refresh(OpenCodeStatusKind.NeedsInput);
            Assert(((SolidColorBrush)coordinator.Background).Color == Color.FromRgb(154, 87, 0) && !RowAnimating() && !IsAnimating(indicator),
                "approval overrides Coordinator green and stops animation");
            foreach (var status in new[] { OpenCodeStatusKind.Unknown, OpenCodeStatusKind.Idle, OpenCodeStatusKind.Pending })
            {
                Refresh(status);
                Assert(!Green() && !RowAnimating() && !IsAnimating(indicator), "Coordinator " + status + " cannot claim active execution");
            }
            typeof(OpenCodeAutomationClient).GetProperty("LastReadWarning")!.SetValue(client, "fixture connection failure");
            Refresh(OpenCodeStatusKind.Busy);
            Assert(!Green() && !IsAnimating(indicator), "read failure overrides cached busy");
            typeof(OpenCodeAutomationClient).GetProperty("LastReadWarning")!.SetValue(client, null);
            stageType.GetProperty("ResultReady")!.SetValue(stage, true);
            Refresh(OpenCodeStatusKind.Busy);
            Assert(!Green() && !IsAnimating(indicator), "finished Coordinator result cannot remain green on cached busy");
            stageType.GetProperty("ResultReady")!.SetValue(stage, false);
            runs[claim.RunId] = originalRun with { State = AutomationState.Stopped };
            Refresh(OpenCodeStatusKind.Busy);
            Assert(!Green() && !IsAnimating(indicator), "manual stop overrides remaining native busy");
            runs[claim.RunId] = originalRun;
            SetField(Field<ConPtySession>(pane, "_session"), "_running", false);
            Refresh(OpenCodeStatusKind.Busy);
            Assert(!Green() && !IsAnimating(indicator), "dead Coordinator cannot remain green on cached busy");
        }
        finally
        {
            runs[claim.RunId] = originalRun;
            stageField.SetValue(runtime, null);
            SetField(Field<ConPtySession>(pane, "_session"), "_running", false);
            Update(window);
            Call(window, "UpdateProjectTaskActivity");
        }
    }

    private static void VerifyCheckpointActivity(MainWindow window, string fixture, AutomationStepClaim claim, object indicator)
    {
        using var pane = new TerminalPane(fixture);
        using var client = new OpenCodeAutomationClient(new Uri("http://127.0.0.1:43127/"), fixture);
        var monitor = new OpenCodeStatusMonitor(new Uri("http://127.0.0.1:43127/"), fixture);
        SetField(pane, "_openCodeStatus", monitor);
        SetField(monitor, "_snapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Busy, false, "checkpoint"));
        SetField(Field<ConPtySession>(pane, "_session"), "_running", true);
        typeof(OpenCodeAutomationClient).GetProperty("LastActivityAt")!.SetValue(client, DateTimeOffset.UtcNow);
        var runtime = Call(window, "RuntimeFor", fixture);
        var checkpoints = (System.Collections.IDictionary)runtime.GetType().GetProperty("MicroStages")!.GetValue(runtime)!;
        var microType = typeof(MainWindow).GetNestedType("MicroManagementStage", BindingFlags.NonPublic)!;
        object Create(Guid token) => Activator.CreateInstance(microType, AutomationRole.Reviewer, token, pane, client)!;
        try
        {
            checkpoints[AutomationRole.Reviewer] = Create(Guid.NewGuid());
            Update(window);
            Assert(!IsAnimating(indicator), "a stale checkpoint cannot animate the current project");
            checkpoints[AutomationRole.Reviewer] = Create(claim.DispatchToken);
            Update(window);
            Assert(IsAnimating(indicator) && Field<TextBlock>(window, "_automationProgressRole").Text == "검토" &&
                Field<TextBlock>(window, "_automationProgressState").Text.Contains("중간 점검"),
                "the right panel shows actual checkpoint work while the primary stage waits");
            typeof(OpenCodeAutomationClient).GetProperty("LastActivityAt")!.SetValue(client, DateTimeOffset.UtcNow.AddSeconds(-30));
            Update(window);
            Assert(!IsAnimating(indicator) && ((SolidColorBrush)Field<TextBlock>(window, "_automationProgressState").Foreground).Color == Color.FromRgb(64, 211, 146),
                "quiet checkpoint stays green without a false activity animation");
            SetField(Field<ConPtySession>(pane, "_session"), "_running", false);
            Update(window);
            Assert(!IsAnimating(indicator), "a terminated checkpoint cannot remain green on a cached busy status");
        }
        finally
        {
            checkpoints.Clear();
            SetField(Field<ConPtySession>(pane, "_session"), "_running", false);
            Update(window);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static bool IsAnimating(object indicator) => (bool)indicator.GetType().GetProperty("IsAnimating")!.GetValue(indicator)!;
    private static void AssertTone(OpenCodeStatusKind status, DateTimeOffset? activity, string tone, string label, string name, DateTimeOffset now, bool animating = false)
    {
        object result = typeof(MainWindow).GetMethod("ClassifyNativeProgressStatus", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { status, true, activity, now })!;
        Assert(result.GetType().GetField("Item1")!.GetValue(result)!.ToString() == tone &&
            result.GetType().GetField("Item2")!.GetValue(result)!.ToString() == label &&
            (bool)result.GetType().GetField("Item3")!.GetValue(result)! == animating, name);
    }
    private static void Update(MainWindow window) => Call(window, "UpdateAutomationProgressPanel");
    private static async Task DispatcherIdle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(40);
    }

    private static bool SaveVisualScreenshot(FrameworkElement target, string path)
    {
        target.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)target.ActualWidth), Math.Max(1, (int)target.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // Render through a brush so the workbench's offset within the window does
        // not place all of its pixels outside this tightly cropped bitmap.
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(target) { Stretch = Stretch.Fill }, null,
                new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        bitmap.Render(visual);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        if (!pixels.Where((_, index) => index % 4 != 3).Any(value => value > 100))
        {
            Console.WriteLine("SKIP screenshot: this desktop returned an empty WPF bitmap; control/state/layout assertions were checked independently.");
            return false;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return true;
    }


    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void SetField(object instance, string name, object? value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object Call(object instance, string name, params object?[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args)!;
    private static object CallStatic(object instance, string name, params object?[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
}
