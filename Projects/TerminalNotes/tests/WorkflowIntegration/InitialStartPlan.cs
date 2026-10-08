using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task VerifyInitialStartPlanRefreshAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "initial plan refresh workspace");
        string project = Path.Combine(workspace, "clean plan project");
        Directory.CreateDirectory(project);
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        await File.WriteAllTextAsync(plan, " \r\n\t", new UTF8Encoding(false));
        string settings = Path.Combine(fixture, "initial-plan-refresh-settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        var window = new MainWindow(settings, autoStartCodex: false, startupHooks: FakeStartupHooks);
        try
        {
            window.Show();
            Call(window, "EnsureProjectTaskRow", project);
            Call(window, "SelectTaskProject", project, true);
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            await UntilAsync(() => editor.IsEnabled, "initial clean plan editor load");
            Require(string.IsNullOrWhiteSpace(editor.Text), "The fixture did not cache an initially blank plan.");

            const string firstDiskVersion = "# Saved implementation\nUse the current project plan.";
            await File.WriteAllTextAsync(plan, firstDiskVersion, new UTF8Encoding(false));
            object session = await LoadForInitialStartAsync(window, project, plan);
            Require(editor.Text == firstDiskVersion && (string)GetProperty(session, "InitialDraft")! == firstDiskVersion,
                "Start-mode plan refresh did not replace a clean cached blank snapshot with nonempty disk contents.");

            const string secondDiskVersion = "# Updated saved implementation\nUse the newest project plan.";
            await File.WriteAllTextAsync(plan, secondDiskVersion, new UTF8Encoding(false));
            session = await LoadForInitialStartAsync(window, project, plan);
            Require(editor.Text == secondDiskVersion && (string)GetProperty(session, "InitialDraft")! == secondDiskVersion,
                "Start-mode plan refresh did not reload a clean stale nonempty snapshot.");

            const string dirtyDraft = "# User edits in progress\nKeep this draft intact.";
            editor.Text = dirtyDraft;
            await File.WriteAllTextAsync(plan, "# External plan update\nDo not overwrite this disk version.", new UTF8Encoding(false));
            session = await LoadForInitialStartAsync(window, project, plan);
            Require(editor.Text == dirtyDraft && (string)GetProperty(session, "InitialDraft")! == dirtyDraft,
                "Refresh replaced a dirty user draft after an external disk edit.");

            bool conflict = false;
            try { await (Task)Call(window, "SaveAutomationPlanAsync", session, dirtyDraft, false)!; }
            catch (IOException) { conflict = true; }
            Require(conflict && await File.ReadAllTextAsync(plan) == "# External plan update\nDo not overwrite this disk version." &&
                editor.Text == dirtyDraft,
                "Saving a dirty draft over an external disk update did not surface a conflict while preserving both versions.");
            Console.WriteLine("PASS initial Start plan refresh: clean blank/nonempty caches reload disk, dirty drafts survive, and external edits conflict without overwrite.");
            await VerifySavedPlanNativeStartAsync(fixture);
        }
        finally { Call(window, "DisposeResources"); window.Close(); }
    }

    private static async Task VerifyEmptyPlanStartCapacityGuardAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "empty-plan capacity workspace");
        string blankProject = Path.Combine(workspace, "blank existing project");
        string missingProject = Path.Combine(workspace, "missing plan project");
        Directory.CreateDirectory(blankProject);
        Directory.CreateDirectory(missingProject);
        string blankPlan = TerminalTabDocumentStore.GetProjectPlanPath(blankProject);
        Directory.CreateDirectory(Path.GetDirectoryName(blankPlan)!);
        await File.WriteAllTextAsync(blankPlan, " \r\n\t", new UTF8Encoding(false));
        string settings = Path.Combine(fixture, "empty-plan-capacity-settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        string receipts = Path.Combine(fixture, "empty-plan capacity receipts");
        string? previousReceipts = Environment.GetEnvironmentVariable("TN_WORKFLOW_TUI_INPUT_TRACE");
        Environment.SetEnvironmentVariable("TN_WORKFLOW_TUI_INPUT_TRACE", receipts);
        var window = new MainWindow(settings, autoStartCodex: false, startupHooks: FakeStartupHooks);
        try
        {
            window.Show();
            var layout = (TerminalSlotLayout)FieldValue(window, "_terminalSlotLayout")!;
            layout.SetCapacity(8);
            for (int slot = 0; slot < 8; slot++)
                layout.Assign(Path.Combine(workspace, "occupied slot " + slot), slot);
            string[] occupiedSlots = ((System.Collections.IEnumerable)GetProperty(layout, "Slots")!).Cast<object?>()
                .Select(value => value?.ToString() ?? "").ToArray();
            Require(occupiedSlots.Length == 8 && occupiedSlots.All(value => value.Length > 0),
                "The fixture did not create eight occupied logical slots.");

            foreach (string project in new[] { blankProject, missingProject })
            {
                Call(window, "EnsureProjectTaskRow", project);
                var runtime = Runtime(window, project);
                bool priorPlanInputState = string.Equals(project, blankProject, StringComparison.OrdinalIgnoreCase);
                if (priorPlanInputState)
                    runtime.GetType().GetField("SinglePlanInputNeeded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                        .SetValue(runtime, true); // stale waiting state without a reusable native pane
                var start = (Task)Call(window, "StartProjectTaskAsync", project)!;
                await start;
                Require(RuntimeField(runtime, "SingleError")?.ToString()?.Contains("터미널 8개 칸이 모두 사용 중입니다", StringComparison.Ordinal) == true &&
                        Equals(RuntimeField(runtime, "SinglePlanInputNeeded"), priorPlanInputState) && RuntimeField(runtime, "SingleSubmitted") is false &&
                        RuntimeField(runtime, "SingleTerminal") is null,
                    "An empty/missing-plan start did not preserve runtime state while enforcing the full-slot guard.");
                Require(((System.Collections.IEnumerable)GetProperty(layout, "Slots")!).Cast<object?>()
                    .Select(value => value?.ToString() ?? "").SequenceEqual(occupiedSlots),
                    "A rejected empty-plan start changed the eight occupied slot assignments.");
                Require(!Directory.Exists(receipts), "Empty-plan Start sent a greeting before native pane capacity was available.");
            }
            Require(await File.ReadAllTextAsync(blankPlan) == " \r\n\t" &&
                    !File.Exists(TerminalTabDocumentStore.GetProjectPlanPath(missingProject)),
                "Empty-plan Start modified the existing blank file or created a file for a missing plan.");
            Console.WriteLine("PASS empty/missing-plan Start routing: capacity is checked before pane creation, state is preserved, and plan files remain untouched.");
        }
        finally
        {
            Call(window, "DisposeResources"); window.Close();
            Environment.SetEnvironmentVariable("TN_WORKFLOW_TUI_INPUT_TRACE", previousReceipts);
        }
    }

    private static async Task VerifyEmptyPlanStartAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "empty-plan start workspace");
        string project = Path.Combine(workspace, "empty-plan existing project");
        string missingProject = Path.Combine(workspace, "missing-plan project");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(missingProject);
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        await File.WriteAllTextAsync(plan, " \r\n\t", new UTF8Encoding(false));
        string missingPlan = TerminalTabDocumentStore.GetProjectPlanPath(missingProject);
        string settings = Path.Combine(fixture, "empty-plan-start-settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        string receipts = Path.Combine(fixture, "empty-plan startup API receipts");
        string? previousTrace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE");
        object? previousDataDirectory = AppContext.GetData("TerminalNotes.DataDirectory");
        string isolatedData = Path.Combine(fixture, "empty-plan app data");
        Directory.CreateDirectory(Path.Combine(isolatedData, "WebView2"));
        Environment.SetEnvironmentVariable("TN_WORKFLOW_TRACE", receipts);
        AppContext.SetData("TerminalNotes.DataDirectory", isolatedData);
        var window = new MainWindow(settings, autoStartCodex: false, startupHooks: FakeStartupHooks);
        try
        {
            window.Show();
            foreach ((string target, string targetPlan, bool shouldExist) in new[]
                { (project, plan, true), (missingProject, missingPlan, false) })
            {
                Call(window, "EnsureProjectTaskRow", target);
                await ((Task)Call(window, "StartProjectTaskAsync", target)!).WaitAsync(TimeSpan.FromSeconds(90));
                var runtime = Runtime(window, target);
                var tab = RuntimeField(runtime, "SingleTerminal") as TabItem
                    ?? throw new InvalidOperationException("No-plan Start did not open a project terminal.");
                var pane = (TerminalPane)GetProperty(tab.Tag, "Pane")!;
                Require(pane.IsReady && pane.IsRunning &&
                        RuntimeField(runtime, "SingleSubmitted") is false && RuntimeField(runtime, "SingleError") is null,
                    "Missing/blank-plan Start did not leave a live native OpenCode TUI ready for direct input.");
                Require(File.Exists(targetPlan) == shouldExist &&
                        (!shouldExist || await File.ReadAllTextAsync(targetPlan) == " \r\n\t"),
                    "No-plan Start created, removed, or changed the project plan file.");
                Require(FindStartupPromptReceipts(receipts, target).Length == 0,
                    "Missing/blank-plan Start sent a fabricated or empty API prompt.");

                int tabCount = ((TabControl)window.FindName("TerminalTabs")).Items.Count;
                await ((Task)Call(window, "StartProjectTaskAsync", target)!).WaitAsync(TimeSpan.FromSeconds(90));
                Require(ReferenceEquals(RuntimeField(runtime, "SingleTerminal"), tab) &&
                        ((TabControl)window.FindName("TerminalTabs")).Items.Count == tabCount &&
                        FindStartupPromptReceipts(receipts, target).Length == 0,
                    "Repeated no-plan Start created another pane or sent an API prompt.");
            }

            object unrelatedRuntime = Runtime(window, missingProject);
            var tabs = (TabControl)window.FindName("TerminalTabs");
            var unrelatedTab = (TabItem)RuntimeField(unrelatedRuntime, "SingleTerminal")!;
            var unrelatedPane = (TerminalPane)GetProperty(unrelatedTab.Tag, "Pane")!;
            // Restart the managed empty-plan terminal through the process lifecycle controls.
            // The fake native API records requests but never invokes a provider/model.
            var lifecycleRuntime = Runtime(window, project);
            var lifecycleTab = (TabItem)RuntimeField(lifecycleRuntime, "SingleTerminal")!;
            var lifecycleState = lifecycleTab.Tag;
            var oldPane = (TerminalPane)GetProperty(lifecycleState, "Pane")!;
            int lifecycleSlot = (int)GetProperty(lifecycleState, "Sequence")!;
            uint? oldServerId = (uint?)GetProperty(oldPane, "OpenCodeServerProcessId");
            Require(oldPane.IsReady && oldServerId is > 0 &&
                    FindStartupPromptReceipts(receipts, project).Length == 0 &&
                    await File.ReadAllTextAsync(plan) == " \r\n\t",
                "The restart case begins with a ready, prompt-free managed pane and unchanged blank plan.");

            tabs.SelectedItem = lifecycleTab;
            await ((Task)Call(window, "RestartSelectedTerminalProcessAsync")!).WaitAsync(TimeSpan.FromSeconds(90));
            var restartedTab = (TabItem)RuntimeField(lifecycleRuntime, "SingleTerminal")!;
            var restartedState = restartedTab.Tag;
            var restartedPane = (TerminalPane)GetProperty(restartedState, "Pane")!;
            Require(!IsProcessRunning(oldServerId),
                "The old owned OpenCode server exited before restart completed.");
            await UntilAsync(() => !IsProcessRunning(oldServerId),
                "old owned OpenCode server exit before lifecycle replacement readiness", seconds: 20);
            await UntilAsync(() => restartedPane.IsReady && restartedPane.IsRunning,
                "restarted managed native pane readiness", seconds: 45);
            uint? newServerId = (uint?)GetProperty(restartedPane, "OpenCodeServerProcessId");
            var planWaitBaselines = (System.Collections.IDictionary)FieldValue(window, "_projectPlanWaitSubmissionBaselines")!;
            object? planWaitBaseline = planWaitBaselines[Path.GetFullPath(project)];
            Require(!ReferenceEquals(lifecycleTab, restartedTab) &&
                    (int)GetProperty(restartedState, "Sequence")! == lifecycleSlot &&
                    string.Equals((string)GetProperty(restartedState, "PlanDocumentPath")!, plan, StringComparison.OrdinalIgnoreCase) &&
                    newServerId is > 0 && newServerId != oldServerId &&
                    ReferenceEquals(RuntimeField(unrelatedRuntime, "SingleTerminal"), unrelatedTab) &&
                    tabs.Items.Contains(unrelatedTab) && unrelatedPane.IsReady && unrelatedPane.IsRunning &&
                    RuntimeField(lifecycleRuntime, "SingleStopped") is true &&
                    planWaitBaseline is not null &&
                    ReferenceEquals(((WeakReference<TerminalPane>)GetProperty(planWaitBaseline, "Pane")!).TryGetTarget(out var baselinePane)
                        ? baselinePane : null, restartedPane) &&
                    (long)GetProperty(restartedPane, "NativeSubmissionGeneration")! == (long)GetProperty(planWaitBaseline, "SubmissionGeneration")! &&
                    await File.ReadAllTextAsync(plan) == " \r\n\t" &&
                    FindStartupPromptReceipts(receipts, project).Length == 0,
                "Restart awaits the old server, rebinds the same managed slot and plan-wait baseline, preserves the blank plan, and sends no automatic prompt.");
            Require(!(await new AutomationStore(project).LoadAsync()).Any(run =>
                    run.State is AutomationState.Running or AutomationState.Scheduled or AutomationState.Paused),
                "Restart leaves persisted project automation stopped until a later explicit Start.");

            // Startup/session retry and exact-receipt reconciliation still apply once a real plan exists.
            File.WriteAllText(Path.Combine(project, ".fixture-session-create-failure-once"), "fail one create request");
            File.WriteAllText(Path.Combine(project, ".fixture-suppress-startup-events"), "require GET reconciliation");
            File.WriteAllText(Path.Combine(project, ".fixture-drop-startup-prompt-response-once"), "accept prompt then lose response");
            const string savedPlan = "# Saved after interactive startup\nInspect the project and follow this plan.";
            await File.WriteAllTextAsync(plan, savedPlan, new UTF8Encoding(false));
            var blankRuntime = Runtime(window, project);
            var blankTab = (TabItem)RuntimeField(blankRuntime, "SingleTerminal")!;
            await ((Task)Call(window, "StartProjectTaskAsync", project)!).WaitAsync(TimeSpan.FromSeconds(90));
            await UntilAsync(() => FindStartupPromptReceipts(receipts, project).Length == 1 &&
                    CountStartupSessionTrace(receipts, project, "startup-session-attempt-*.json") >= 2 &&
                    CountStartupSessionTrace(receipts, project, "startup-session-ses_*.json") >= 1,
                "real-plan Start reconciles one accepted prompt after session-create/response failures", seconds: 60);

            Require(ReferenceEquals(RuntimeField(blankRuntime, "SingleTerminal"), blankTab) &&
                    RuntimeField(blankRuntime, "SingleSubmitted") is true && RuntimeField(blankRuntime, "SingleError") is null,
                "Adding a real plan did not reuse the ready TUI pane for its one exact API submission.");
            FakeStartupPromptReceipt receipt = FindStartupPromptReceipts(receipts, project).Single();
            Require(receipt.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Equals(Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
                    receipt.Prompt == ExpectedSavedPlanPrompt(plan) && receipt.MessageId.Length > 0 && receipt.SessionId.Length > 0 &&
                    await File.ReadAllTextAsync(plan) == savedPlan,
                "Real-plan Start did not submit the exact saved plan once to the matching project session.");
            Console.WriteLine("PASS no-plan Start: missing/blank plans open one ready TUI without API prompts; later real-plan Start reuses it and reconciles one exact receipt.");
        }
        finally
        {
            Call(window, "DisposeResources"); window.Close();
            Environment.SetEnvironmentVariable("TN_WORKFLOW_TRACE", previousTrace);
            AppContext.SetData("TerminalNotes.DataDirectory", previousDataDirectory);
        }
    }

    private static int CountStartupSessionTrace(string traceDirectory, string project, string pattern)
    {
        if (!Directory.Exists(traceDirectory)) return 0;
        string expected = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Directory.GetFiles(traceDirectory, pattern).Count(path =>
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                string? directory = document.RootElement.GetProperty("Directory").GetString();
                return directory is not null && Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(expected, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                return false;
            }
        });
    }

    private static bool IsProcessRunning(uint? processId)
    {
        if (processId is not > 0) return false;
        try { using var process = System.Diagnostics.Process.GetProcessById((int)processId.Value); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task VerifySavedPlanNativeStartAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "saved-plan native startup workspace");
        string project = Path.Combine(workspace, "saved-plan existing project");
        Directory.CreateDirectory(project);
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        const string savedPlan = "# Saved startup plan\nFirst inspect the project, then make one focused change.";
        await File.WriteAllTextAsync(plan, savedPlan, new UTF8Encoding(false));
        string settings = Path.Combine(fixture, "saved-plan-native-start-settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        string receipts = Path.Combine(fixture, "saved-plan startup API receipts");
        string expectedPrompt = ExpectedSavedPlanPrompt(plan);
        string? previousTrace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE");
        object? previousDataDirectory = AppContext.GetData("TerminalNotes.DataDirectory");
        string isolatedData = Path.Combine(fixture, "saved-plan app data");
        Directory.CreateDirectory(Path.Combine(isolatedData, "WebView2"));
        Environment.SetEnvironmentVariable("TN_WORKFLOW_TRACE", receipts);
        AppContext.SetData("TerminalNotes.DataDirectory", isolatedData);
        var window = new MainWindow(settings, autoStartCodex: false, startupHooks: FakeStartupHooks);
        try
        {
            window.Show();
            Call(window, "EnsureProjectTaskRow", project);
            await ((Task)Call(window, "StartProjectTaskAsync", project)!).WaitAsync(TimeSpan.FromSeconds(90));
            await UntilAsync(() => Directory.Exists(receipts) &&
                    Directory.GetFiles(receipts, "startup-session-ses_*.json").Length == 1 &&
                    Directory.GetFiles(receipts, "startup-prompt-*.json").Length == 1,
                "saved-plan Start submits exactly one native prompt", seconds: 45);

            var runtime = Runtime(window, project);
            var paneTab = (TabItem)RuntimeField(runtime, "SingleTerminal")!;
            var pane = (TerminalPane)GetProperty(paneTab.Tag, "Pane")!;
            Require(pane.IsReady && pane.IsRunning && RuntimeField(runtime, "SingleSubmitted") is true &&
                    RuntimeField(runtime, "SinglePlanInputNeeded") is false && RuntimeField(runtime, "SingleError") is null,
                "Saved-plan Start did not accept the native prompt as a submitted task.");
            using (var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(receipts, "startup-prompt-*.json").Single())))
                Require(receipt.RootElement.GetProperty("Prompt").GetString() == expectedPrompt &&
                        receipt.RootElement.GetProperty("MessageId").GetString() is { Length: > 0 } &&
                        receipt.RootElement.GetProperty("SessionId").GetString() is { Length: > 0 },
                    "Saved-plan Start did not submit the exact saved-plan request under one fixed native message ID.");
            Require(await File.ReadAllTextAsync(plan) == savedPlan &&
                    Directory.GetFiles(receipts, "startup-prompt-*.json").Length == 1,
                "Saved-plan Start altered the plan or issued more than one startup prompt POST.");
            Console.WriteLine("PASS saved-plan Start: persisted plan sent once through native startup API with an exact-message receipt.");
        }
        finally
        {
            Call(window, "DisposeResources"); window.Close();
            Environment.SetEnvironmentVariable("TN_WORKFLOW_TRACE", previousTrace);
            AppContext.SetData("TerminalNotes.DataDirectory", previousDataDirectory);
        }
    }

    private static async Task<object> LoadForInitialStartAsync(MainWindow window, string project, string plan)
    {
        MethodInfo load = typeof(MainWindow).GetMethod("LoadAutomationPlanAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task task = (Task)load.Invoke(window, [null, project, 1, plan, true])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
}
