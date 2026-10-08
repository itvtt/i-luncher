using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task VerifySingleProjectTaskAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "single workspace");
        string project = Path.Combine(workspace, "single project");
        Directory.CreateDirectory(project);
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        await File.WriteAllTextAsync(plan, "# Single task\nImplement the saved plan in this one session.");
        string settings = Path.Combine(fixture, "single-settings.json");
        string startupPromptTrace = Path.Combine(fixture, "fake trace");
        string? originalStartupHold = Environment.GetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_HOLD_FILE");
        await File.WriteAllTextAsync(settings, System.Text.Json.JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        var window = new MainWindow(settings, autoStartCodex: true, startupHooks: FakeStartupHooks);
        try
        {
            window.Show();
            await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null && (bool)FieldValue(window, "_openCodeReady")!, "single OpenCode readiness");
            Call(window, "EnsureProjectTaskRow", project);
            var row = ((IDictionary)FieldValue(window, "_projectTaskRows")!)[project]!;
            ((RadioButton)GetProperty(row, "Single")!).IsChecked = true;
            string startupHold = Path.Combine(fixture, "hold task OpenCode startup");
            await File.WriteAllTextAsync(startupHold, "Hold the fake interactive server before its native API becomes ready.");
            Environment.SetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_HOLD_FILE", startupHold);
            Require((bool)Call(window, "AssignProjectToSlot", project, 0)!, "The project could not be placed in the first terminal slot.");
            var slotLayout = FieldValue(window, "_terminalSlotLayout")!;
            var slots = (IList)GetProperty(slotLayout, "Slots")!;
            Require(string.Equals(slots[0]?.ToString(), project, StringComparison.OrdinalIgnoreCase), "Assigning the task changed its requested slot.");
            var tabs = (TabControl)window.FindName("TerminalTabs");
            Require(tabs.Items.Count == 1, "Placing the task did not create exactly one native pane.");
            var placedTab = (TabItem)tabs.Items[0];
            // Match the sidebar Start action immediately after a card is dropped.
            // This exercises the pane while its native OpenCode/TUI startup is in flight.
            var firstStart = (Task)Call(window, "ToggleProjectTaskAsync", project)!;
            var duplicateStart = (Task)Call(window, "StartProjectTaskAsync", project)!;
            var pane = (TerminalPane)GetProperty(placedTab.Tag, "Pane")!;
            await UntilAsync(() => pane.IsRunning && FieldValue(pane, "_openCodeSnapshot") is OpenCodeStatusSnapshot { Kind: OpenCodeStatusKind.Unknown },
                "the placed pane remains blocked before native OpenCode readiness");
            Require((bool)RuntimeField(Runtime(window, project), "StartingSingle")!, "The sidebar Start did not remain pending during native startup.");
            File.Delete(startupHold);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_HOLD_FILE", null);
            await Task.WhenAll(firstStart, duplicateStart).WaitAsync(TimeSpan.FromSeconds(90));
            var runtime = Runtime(window, project);
            Require((bool)RuntimeField(runtime, "SingleSubmitted")!, "Single mode opened a terminal without submitting its saved plan.");
            string expectedPrompt = ExpectedSavedPlanPrompt(plan);
            await UntilAsync(() => FindStartupPromptReceipts(startupPromptTrace, project).Any(receipt => receipt.Prompt == expectedPrompt),
                "the fake OpenCode API records the saved-plan request");
            var firstReceipts = FindStartupPromptReceipts(startupPromptTrace, project)
                .Where(receipt => receipt.Prompt == expectedPrompt).ToArray();
            Require(firstReceipts.Length == 1 && !string.IsNullOrWhiteSpace(firstReceipts[0].SessionId) &&
                    !string.IsNullOrWhiteSpace(firstReceipts[0].MessageId),
                "The immediate Start path did not produce exactly one API receipt with exact session/message identity.");
            Require(tabs.Items.Count == 1 && Team(runtime).Count == 0, "Single mode created a role team or extra terminal.");
            var original = (TabItem)tabs.Items[0];
            Require(ReferenceEquals(placedTab, original) && string.Equals(slots[0]?.ToString(), project, StringComparison.OrdinalIgnoreCase),
                "Starting immediately after placement replaced the native pane or moved the task to another slot.");
            Require(!((RadioButton)GetProperty(row, "Multi")!).IsEnabled, "A pending single request allowed a concurrent mode switch.");
            var nativeStatus = (TerminalNotes.Services.OpenCodeStatusMonitor)FieldValue(pane, "_openCodeStatus")!;
            long sendId = 999_001;
            Type dispatchType = typeof(TerminalPane).GetNestedType("SavedPlanDispatch", BindingFlags.NonPublic)!;
            object dispatch = Activator.CreateInstance(dispatchType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [expectedPrompt, null, true], culture: null)!;
            ((IDictionary)FieldValue(pane, "_savedPlanDispatches")!).Add(sendId, dispatch);
            nativeStatus.SetSessionScope("ses_fast_completion");
            Call(pane, "PrepareSavedPlanDispatchSubmit", sendId, "\r");
            nativeStatus.ConfirmOwnedTurnCompleted(); // model a fast native idle event before the PTY write returns
            Call(pane, "ObserveSavedPlanDispatchWrite", sendId, "\r");
            Require(nativeStatus.Snapshot.HasCompletedTurn && nativeStatus.Snapshot.Kind == OpenCodeStatusKind.Idle,
                "The saved-plan PTY acknowledgement overwrote a native completion that arrived first.");
            Call(window, "OpenProjectTask", project);
            Call(window, "ShowProjectTaskList");
            await (Task)Call(window, "RefreshProjectTasksAsync")!;
            Call(window, "OpenProjectTask", project);
            Require(ReferenceEquals(original, tabs.Items[0]) && tabs.Items.Count == 1, "Single task navigation replaced its pane.");
            Require((await new AutomationStore(project).LoadAsync()).Count == 0, "Single execution created a commander workflow.");
            await (Task)Call(window, "StopProjectTaskAsync", project)!;
            Require((bool)RuntimeField(runtime, "SingleStopped")!, "Single task stop did not latch user intent.");

            // Repeat with a pane that is already natively ready when Start is clicked.
            Call(window, "RemoveProjectSlot", project);
            string readyProject = Path.Combine(fixture, "already ready project");
            Directory.CreateDirectory(readyProject);
            string readyPlan = TerminalTabDocumentStore.GetProjectPlanPath(readyProject);
            Directory.CreateDirectory(Path.GetDirectoryName(readyPlan)!);
            await File.WriteAllTextAsync(readyPlan, "# Ready single task\nSubmit through the already-ready pane.");
            Call(window, "EnsureProjectTaskRow", readyProject);
            var readyRow = ((IDictionary)FieldValue(window, "_projectTaskRows")!)[readyProject]!;
            ((RadioButton)GetProperty(readyRow, "Single")!).IsChecked = true;
            Require((bool)Call(window, "AssignProjectToSlot", readyProject, 0)!, "The already-ready task could not be placed.");
            var readyTab = (TabItem)RuntimeField(Runtime(window, readyProject), "SingleTerminal")!;
            var readyPane = (TerminalPane)GetProperty(readyTab.Tag, "Pane")!;
            await UntilAsync(() => readyPane.IsReady && readyPane.CanExecuteSavedPlan,
                "a placed pane becomes natively ready before Start");
            await (Task)Call(window, "ToggleProjectTaskAsync", readyProject)!;
            string readyExpectedPrompt = ExpectedSavedPlanPrompt(readyPlan);
            await UntilAsync(() => FindStartupPromptReceipts(startupPromptTrace, readyProject)
                    .Any(receipt => receipt.Prompt == readyExpectedPrompt),
                "the already-ready fake OpenCode API records the second saved-plan request");
            var firstProjectReceipts = FindStartupPromptReceipts(startupPromptTrace, project)
                .Where(receipt => receipt.Prompt == expectedPrompt).ToArray();
            var readyProjectReceipts = FindStartupPromptReceipts(startupPromptTrace, readyProject)
                .Where(receipt => receipt.Prompt == readyExpectedPrompt).ToArray();
            Require(firstProjectReceipts.Length == 1 && readyProjectReceipts.Length == 1 &&
                    firstProjectReceipts[0].SessionId != readyProjectReceipts[0].SessionId &&
                    firstProjectReceipts[0].MessageId != readyProjectReceipts[0].MessageId,
                "Immediate and already-ready Start paths did not each produce exactly one distinct API session/message receipt.");
            Require(tabs.Items.Contains(readyTab) && ReferenceEquals(readyTab, RuntimeField(Runtime(window, readyProject), "SingleTerminal")) &&
                    string.Equals(slots[0]?.ToString(), readyProject, StringComparison.OrdinalIgnoreCase),
                "Starting an already-ready placed task replaced its pane or moved its slot.");
            Console.WriteLine("PASS single TASK: immediate and already-ready Start each produced one exact fake API receipt; pane/slot identity, mode lock, navigation and stop preserved.");
        }
        finally
        {
            Call(window, "DisposeResources"); window.Close();
            Environment.SetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_HOLD_FILE", originalStartupHold);
        }

        await VerifySingleProjectTaskWithStartupInFlightAsync(fixture);
        await VerifyPlanPanelRunWithoutPaneAsync(fixture);
    }

    private sealed record FakeStartupPromptReceipt(string Directory, string SessionId, string MessageId, string Prompt);

    private static string ExpectedSavedPlanPrompt(string planPath)
    {
        var builder = typeof(TerminalPane).GetMethod("BuildSavedPlanPrompt", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(TerminalPane).FullName, "BuildSavedPlanPrompt");
        return (string)(builder.Invoke(null, [Path.GetFullPath(planPath)])
            ?? throw new InvalidOperationException("The saved-plan prompt builder returned null."));
    }

    private static FakeStartupPromptReceipt[] FindStartupPromptReceipts(string traceDirectory, string projectDirectory)
    {
        if (!Directory.Exists(traceDirectory)) return [];
        string expectedDirectory = Path.GetFullPath(projectDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Directory.GetFiles(traceDirectory, "startup-prompt-*.json")
            .Select(path =>
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    var root = document.RootElement;
                    return new FakeStartupPromptReceipt(
                        root.GetProperty("Directory").GetString() ?? "",
                        root.GetProperty("SessionId").GetString() ?? "",
                        root.GetProperty("MessageId").GetString() ?? "",
                        root.GetProperty("Prompt").GetString() ?? "");
                }
                catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    // A just-created trace can be read while the fake API is still writing it.
                    return null;
                }
            })
            .OfType<FakeStartupPromptReceipt>()
            .Where(receipt => !string.IsNullOrWhiteSpace(receipt.Directory) && string.Equals(
                Path.GetFullPath(receipt.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                expectedDirectory,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static async Task VerifySingleProjectTaskWithStartupInFlightAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "auto-start-disabled workspace");
        string project = Path.Combine(workspace, "auto-start-disabled project");
        Directory.CreateDirectory(project);
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        await File.WriteAllTextAsync(plan, "# Native TASK while startup is in flight\nSubmit this plan after OpenCode readiness.");
        string settings = Path.Combine(fixture, "auto-start-disabled-settings.json");
        await File.WriteAllTextAsync(settings, System.Text.Json.JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        string startupPromptTrace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")
            ?? throw new InvalidOperationException("The workflow API trace directory is not configured.");

        var checkEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new OpenCodeStartupHooks(
            async _ => { checkEntered.TrySetResult(true); return await releaseCheck.Task; },
            () => throw new InvalidOperationException("The fixture must never prompt for installation."),
            _ => throw new InvalidOperationException("The fixture must never invoke a real installer."));
        var window = new MainWindow(settings, autoStartCodex: false, startupHooks: hooks);
        try
        {
            window.Show();
            var startup = (Task)Call(window, "PrepareOpenCodeAndStartAsync", null, false, false, null)!;
            await checkEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var start = (Task)Call(window, "StartProjectTaskAsync", project)!;
            await Task.Delay(250);
            var tabs = (TabControl)window.FindName("TerminalTabs");
            Require(tabs.Items.Count == 0 && !(bool)FieldValue(window, "_openCodeReady")!,
                "TASK created a shell pane before the in-flight OpenCode startup check completed.");

            releaseCheck.TrySetResult(true);
            await Task.WhenAll(startup, start).WaitAsync(TimeSpan.FromSeconds(90));
            var runtime = Runtime(window, project);
            var tab = (TabItem)RuntimeField(runtime, "SingleTerminal")!;
            var pane = (TerminalPane)GetProperty(tab.Tag, "Pane")!;
            await UntilAsync(() => pane.IsReady && pane.IsRunning,
                "TASK created a running native OpenCode pane after the delayed startup check");
            string expectedPrompt = ExpectedSavedPlanPrompt(plan);
            await UntilAsync(() => FindStartupPromptReceipts(startupPromptTrace, project).Any(receipt => receipt.Prompt == expectedPrompt),
                "the auto-start-disabled TASK submits its saved plan through the API");
            var receipts = FindStartupPromptReceipts(startupPromptTrace, project);
            Require(receipts.Length == 1 && receipts[0].Prompt == expectedPrompt &&
                    !string.IsNullOrWhiteSpace(receipts[0].SessionId) && !string.IsNullOrWhiteSpace(receipts[0].MessageId),
                "The auto-start-disabled TASK did not deliver exactly one saved-plan API request with exact session/message identity.");
            Require((pane.StartupCommand is "opencode" or "opencode --auto") && tabs.Items.Count == 1 &&
                    (bool)RuntimeField(runtime, "SingleSubmitted")! && Team(runtime).Count == 0,
                "The auto-start-disabled TASK did not use one native OpenCode pane.");
            Console.WriteLine("PASS single TASK startup race: with auto-start disabled, TASK waited for delayed OpenCode readiness and submitted the exact saved plan to a native pane.");
        }
        finally
        {
            releaseCheck.TrySetResult(true);
            Call(window, "DisposeResources"); window.Close();
        }
    }

    private static async Task VerifyPlanPanelRunWithoutPaneAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "plan panel workspace");
        string project = Path.Combine(workspace, "selected project without pane");
        Directory.CreateDirectory(project);
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        await File.WriteAllTextAsync(plan, "# Existing project plan\nInitial saved plan.");
        string settings = Path.Combine(fixture, "plan-panel-settings.json");
        await File.WriteAllTextAsync(settings, System.Text.Json.JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        string startupPromptTrace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")
            ?? throw new InvalidOperationException("The workflow API trace directory is not configured.");

        var checkEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new OpenCodeStartupHooks(
            async _ => { checkEntered.TrySetResult(true); return await releaseCheck.Task; },
            () => throw new InvalidOperationException("The fixture must never prompt for installation."),
            _ => throw new InvalidOperationException("The fixture must never invoke a real installer."));
        var window = new MainWindow(settings, autoStartCodex: false, startupHooks: hooks);
        try
        {
            window.Show();
            Call(window, "SelectTaskProject", project, true);
            var tabs = (TabControl)window.FindName("TerminalTabs");
            Require(tabs.Items.Count == 0, "The selected-project Plan test unexpectedly started with a terminal pane.");
            var execute = Field<Button>(window, "_tabDocumentExecute");
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            const string savedPlan = "# Existing project plan\nRun directly from the selected project's Plan panel.";
            await UntilAsync(() => editor.IsEnabled && editor.Text.Contains("Initial saved plan", StringComparison.Ordinal),
                "the selected project's saved Markdown plan loads without a pane");
            var startup = (Task)Call(window, "PrepareOpenCodeAndStartAsync", null, false, false, null)!;
            await checkEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(execute.IsEnabled,
                "The Plan Run button stayed disabled for a loaded project plan with no pane while OpenCode startup was in flight.");
            editor.Text = savedPlan;
            var run = (Task)Call(window, "SaveTabDocumentAsync", true)!;
            await Task.Delay(250);
            Require(tabs.Items.Count == 0, "Plan Run created a terminal before the in-flight OpenCode readiness check completed.");
            releaseCheck.TrySetResult(true);
            await Task.WhenAll(startup, run).WaitAsync(TimeSpan.FromSeconds(90));
            string expectedPrompt = ExpectedSavedPlanPrompt(plan);
            await UntilAsync(() => FindStartupPromptReceipts(startupPromptTrace, project).Any(receipt => receipt.Prompt == expectedPrompt),
                "Plan Run creates a native pane and submits the saved Markdown through the API");
            var runtime = Runtime(window, project);
            var tab = (TabItem)RuntimeField(runtime, "SingleTerminal")!;
            var pane = (TerminalPane)GetProperty(tab.Tag, "Pane")!;
            var receipts = FindStartupPromptReceipts(startupPromptTrace, project);
            Require(receipts.Length == 1 && receipts[0].Prompt == expectedPrompt &&
                    !string.IsNullOrWhiteSpace(receipts[0].SessionId) && !string.IsNullOrWhiteSpace(receipts[0].MessageId) &&
                    (pane.StartupCommand is "opencode" or "opencode --auto") &&
                    (bool)RuntimeField(runtime, "SingleSubmitted")! && Team(runtime).Count == 0,
                "Plan Run without a pane did not submit the exact saved Markdown to one native OpenCode session.");
            Require((await File.ReadAllTextAsync(plan)) == savedPlan,
                "Plan Run did not save the edited Markdown before submitting it.");
            Console.WriteLine("PASS Plan panel Run: a selected project's saved Markdown enabled Run without a pane, created one native session, and delivered the exact saved plan.");
        }
        finally
        {
            releaseCheck.TrySetResult(true);
            Call(window, "DisposeResources"); window.Close();
        }
    }

    private static async Task VerifyContinuousTaskAsync(string fixture)
    {
        string project = Path.Combine(fixture, "continuous improvement");
        Directory.CreateDirectory(project);
        string path = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "# Continuous task fixture\nKeep improving within this project.");
        var window = new MainWindow(Path.Combine(project, "settings.json"), autoStartCodex: false,
            startupHooks: FakeStartupHooks, initialWorkspace: project);
        try
        {
            window.Show();
            await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null, "loop scheduler startup");
            Call(window, "EnsureProjectTaskRow", project);
            var rows = (IDictionary)FieldValue(window, "_projectTaskRows")!;
            var row = rows[project]!;
            ((RadioButton)GetProperty(row, "Multi")!).IsChecked = true;
            ((CheckBox)GetProperty(row, "Loop")!).IsChecked = true;
            await (Task)Call(window, "StartProjectTaskAsync", project)!;
            Call(window, "ShowProjectTaskList");
            var store = new AutomationStore(project);
            await UntilAsync(async () => (await store.LoadAsync()).Any(run => run.ImprovementCycle >= 3),
                "two complete independently verified improvement cycles without a manual continue", 150);
            var current = (await store.LoadAsync()).Single();
            Require(current.Options.ContinuousImprovement && current.ImprovementHistory.Count == 2 &&
                current.ImprovementHistory.Select(item => item.Goal).Distinct().Count() == 2,
                "Continuous mode lost the completed cycles or repeated the same plan.");
            Require(FieldValue(window, "_displayedTaskProject") is null, "Automatic improvement stole the task list view.");
            await (Task)Call(window, "StopProjectTaskAsync", project)!;
            await (Task)Call(window, "PollAutomationAsync")!;
            Require((await store.GetAsync(current.Id))?.State == AutomationState.Stopped,
                "Task stop was ignored by continuous mode.");
            Console.WriteLine("PASS TASK continuous improvement: two distinct verified cycles, automatic continuation, background list and explicit stop");
        }
        finally { Call(window, "DisposeResources"); window.Close(); }
    }

    private static object Runtime(MainWindow window, string project) => Call(window, "RuntimeFor", project)!;
    private static object? RuntimeField(object runtime, string name) => runtime.GetType().GetField(name)!.GetValue(runtime);
    private static Dictionary<AutomationRole, TabItem> Team(object runtime) => (Dictionary<AutomationRole, TabItem>)runtime.GetType().GetProperty("Team")!.GetValue(runtime)!;

    private static async Task VerifyProjectTasksAsync(string fixture, string holdFile)
    {
        string workspace = Path.Combine(fixture, "task workspace");
        Directory.CreateDirectory(workspace);
        var projects = Enumerable.Range(1, 14).Select(index => Path.Combine(workspace, "Project " + index.ToString("00"))).ToArray();
        foreach (string project in projects) Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        Directory.CreateDirectory(Path.Combine(workspace, "terminal-notes"));
        var window = new MainWindow(Path.Combine(fixture, "project-tasks-settings.json"), autoStartCodex: false, startupHooks: FakeStartupHooks);
        try
        {
            window.Show();
            await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null, "task scheduler ownership");
            SetField(window, "_workspace", workspace);
            await (Task)Call(window, "RefreshProjectTasksAsync")!;
            var rows = (IDictionary)FieldValue(window, "_projectTaskRows")!;
            Require(rows.Count == projects.Length, "Workspace discovery included metadata or lost project rows.");
            Require(projects.All(project => !Directory.Exists(Path.Combine(project, "terminal-notes"))), "Listing projects wrote plan or run files before selection.");
            window.Height = 480;
            window.UpdateLayout();
            var scroll = Field<ScrollViewer>(window, "_taskListScroll");
            Require(scroll.ScrollableHeight > 0, "Project rows cannot be managed through vertical scrolling.");
            var runs = new List<AutomationRun>();
            foreach (string project in projects.Take(3))
            {
                await File.WriteAllTextAsync(Path.Combine(project, ".fixture-historical-approval"), "Unrelated persisted permission/question");
                string planPath = TerminalTabDocumentStore.GetProjectPlanPath(project);
                Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
                await File.WriteAllTextAsync(planPath, "# Concurrent project fixture\nFinish through independent final verification.");
                Call(window, "RememberProjectName", project, Path.GetFileName(project));
                runs.Add(await (Task<AutomationRun>)Call(window, "StartProjectAutomationAsync", project, await File.ReadAllTextAsync(planPath), 1, planPath, null)!);
                await UntilAsync(() => RuntimeField(Runtime(window, project), "Stage") is not null, "each project reaches a native request independently", 60);
            }
            Require(projects.Take(3).All(project => RuntimeField(Runtime(window, project), "Stage") is not null), "An active project blocked another project's commander.");
            var allPanes = projects.Take(3).SelectMany(project => Team(Runtime(window, project)).Values)
                .Select(tab => (TerminalPane)GetProperty(tab.Tag, "Pane")!).ToArray();
            try { await UntilAsync(() => allPanes.All(pane => pane.IsReady && pane.IsRunning), "all nine role terminals start, including hidden projects", 60); }
            catch
            {
                foreach (var pane in allPanes)
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { pane.WorkingDirectory, pane.IsReady, pane.IsRunning, pane.IsLoaded, pane.IsVisible, pane.ActualWidth, pane.ActualHeight,
                        ready = FieldValue(pane, "_ready"), webReady = FieldValue(pane, "_pendingWebReadyDimensions"), failed = FieldValue(pane, "_failed"), background = FieldValue(pane, "_allowBackgroundAutomationStartup") }));
                throw;
            }
            Require(allPanes.Distinct().Count() == 9, "Projects share a role pane or retain the obsolete eight-session ceiling.");
            var registered = System.Text.Json.JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(
                Path.Combine(fixture, "project-tasks-settings.json.automation-projects.json")))!;
            Require(projects.Take(3).All(project => registered.Contains(project, StringComparer.OrdinalIgnoreCase)),
                "Starting another project lost an existing scheduler registration.");
            var originalTeam = Team(Runtime(window, projects[0])).ToDictionary(pair => pair.Key, pair => pair.Value);
            Call(window, "ShowProjectTask", projects[0]);
            var views = (IDictionary)FieldValue(window, "_projectTaskViews")!;
            var visibleView = (Grid)views[projects[0]]!;
            Require(visibleView.Visibility == Visibility.Visible && visibleView.ColumnDefinitions.Count == 3 &&
                    visibleView.Children.Count == 3, "Opening a task did not produce its three role terminals.");
            Call(window, "ShowProjectTaskList");
            Require(scroll.Visibility == Visibility.Visible && visibleView.Visibility == Visibility.Hidden && allPanes.All(pane => pane.IsRunning),
                "Returning to the list stopped or replaced background project terminals.");
            Call(window, "OpenProjectTask", projects[0]);
            Require(originalTeam.All(pair => ReferenceEquals(pair.Value, Team(Runtime(window, projects[0]))[pair.Key])), "Task navigation recreated live role sessions.");
            Call(window, "ShowProjectTaskList");
            await (Task)Call(window, "StopProjectTaskAsync", projects[0])!;
            Require((await new AutomationStore(projects[0]).GetAsync(runs[0].Id))?.State == AutomationState.Stopped &&
                projects.Skip(1).Take(2).All(project => RuntimeField(Runtime(window, project), "Stage") is not null), "Stopping one task interfered with another task's claim.");
            File.Delete(holdFile);
            foreach (int index in new[] { 1, 2 })
                await UntilAsync(async () => (await new AutomationStore(projects[index]).GetAsync(runs[index].Id))?.State == AutomationState.Completed,
                    "hidden project completes while the task list remains selected", 100);
            Require(Field<ScrollViewer>(window, "_taskListScroll").Visibility == Visibility.Visible &&
                    FieldValue(window, "_displayedTaskProject") is null,
                "Background role handoffs stole the task-list view.");
            await UntilAsync(() => projects.Take(3).All(project => ((Button)GetProperty(rows[project]!, "Start")!).IsEnabled &&
                !((Button)GetProperty(rows[project]!, "Stop")!).IsEnabled), "completed and stopped rows release their run controls");
            Require(((TextBlock)GetProperty(rows[projects[1]]!, "State")!).Text == "✓ 완료", "Verified completion is missing from the task row.");
            Require(((System.Windows.Media.SolidColorBrush)((Border)GetProperty(rows[projects[1]]!, "Surface")!).Background).Color ==
                System.Windows.Media.Color.FromRgb(53, 35, 79), "Verified completion is not purple.");
            Require(System.Windows.Automation.AutomationProperties.GetName((Button)GetProperty(rows[projects[0]]!, "Start")!) == "이어서 실행", "Stopped task has no resume control.");
            await (Task)Call(window, "ResumeProjectTaskAsync", projects[0])!;
            await UntilAsync(async () => (await new AutomationStore(projects[0]).GetAsync(runs[0].Id))?.State == AutomationState.Completed,
                "task row resumes the same stopped run to independently verified completion", 100);
            Require((await new AutomationStore(projects[0]).LoadAsync()).Count == 1, "Resume created a new run and discarded the previous workflow.");
            await UntilAsync(() => ((TextBlock)GetProperty(rows[projects[0]]!, "State")!).Text == "✓ 완료", "resumed completion is visible in the task row");
            var planToggle = (Button)window.FindName("FocusButton");
            Require(window.FindName("AutomationButton") is null && planToggle.Content is System.Windows.Shapes.Path &&
                System.Windows.Automation.AutomationProperties.GetName(planToggle).Contains("플랜 패널", StringComparison.Ordinal) &&
                (planToggle.ToolTip as string)?.Contains("플랜", StringComparison.Ordinal) == true &&
                window.FindName("CompactButton") is null, "The accessible plan toggle and monitor-button removal were not applied.");
            window.Width = 820;
            window.Height = 480;
            window.UpdateLayout();
            CaptureVisual(window, Path.Combine(AppContext.BaseDirectory, "project-task-list.png"));
            Console.WriteLine("PASS project TASK list: read-only discovery, vertical scroll, three independent teams / nine live panes, navigation identity, scoped stop and background completion.");
        }
        finally
        {
            window.GetType().GetMethod("DisposeResources", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            window.Close();
        }
    }

    private static async Task VerifyMicroManagementAsync(string fixture)
    {
        string project = Path.Combine(fixture, "micro managed project");
        Directory.CreateDirectory(project);
        string planPath = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
        await File.WriteAllTextAsync(planPath, "# WF_MICRO\nInterrupt the incorrect implementation and review partial work.");
        var window = new MainWindow(Path.Combine(project, "settings.json"), autoStartCodex: false, startupHooks: FakeStartupHooks);
        try
        {
            window.Show();
            await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null, "micro scheduler startup");
            Call(window, "RememberProjectName", project, "Micro fixture");
            var run = await (Task<AutomationRun>)Call(window, "StartProjectAutomationAsync", project, await File.ReadAllTextAsync(planPath), 1, planPath, null)!;
            var runtime = Runtime(window, project);
            await UntilAsync(() => RuntimeField(runtime, "Stage") is { } stage && ((AutomationStepClaim)GetProperty(stage, "Claim")!).Role == AutomationRole.Implementer,
                "implementation reaches the checkpoint target", 60);
            Call(window, "ShowProjectTaskList");
            await UntilAsync(() => ((IDictionary)runtime.GetType().GetProperty("MicroStages")!.GetValue(runtime)!).Count > 0,
                "a real coordinator or reviewer checkpoint runs alongside implementation", 30);
            await UntilAsync(() => RuntimeField(runtime, "RequestedIntervention") is string,
                "the commander consumes the checkpoint response and requests a correction", 30);
            await UntilAsync(async () => (await new AutomationStore(project).GetAsync(run.Id))?.State == AutomationState.Completed,
                "coordinator intervention aborts the exact worker, reviews partial work and reaches final verification", 100);
            string logPath = AutomationDiagnostics.LogPath(project, run.Id);
            await UntilAsync(() => File.Exists(logPath) && File.ReadAllText(logPath).Contains("coordinator_checkpoint") && File.ReadAllText(logPath).Contains("reviewer_checkpoint"),
                "both real model checkpoint results are recorded");
            Require(File.ReadAllText(logPath).Contains("checkpoint_submitted"), "Checkpoint activity was simulated without a native model request.");
            string traceFolder = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")!;
            var checkpointPrompts = Directory.GetFiles(traceFolder, "checkpoint-*.trace").Select(path =>
            {
                using var trace = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                return trace.RootElement.GetProperty("Prompt").GetString() ?? "";
            }).ToArray();
            Require(checkpointPrompts.Any(prompt => prompt.Contains("구조화된 지휘 계획:") && prompt.Contains("## 인수 테스트") &&
                prompt.Contains("계획 승인 상태: 승인됨") && prompt.Contains("원문 MD에 상세 계획이 없다는 이유로")),
                "Coordinator checkpoints lost the structured plan, approval state, or source-plan storage contract.");
            Console.WriteLine("PASS micro management: real read-only Reviewer/Coordinator requests during implementation, explicit coordinator intervention, confirmed abort, review and resumed delivery.");
        }
        finally
        {
            window.GetType().GetMethod("DisposeResources", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            window.Close();
        }
    }
}
