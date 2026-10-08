using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _failures;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--terminal-fixture"))
        {
            Console.WriteLine("PLAN_EXECUTION_NAVIGATION_FIXTURE");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(app); }
            catch (Exception ex)
            {
                _failures++;
                Console.Error.WriteLine("FAIL unhandled: " + ex);
            }
            finally { app.Shutdown(_failures); }
        };
        return app.Run();
    }

    private static async Task RunAsync(Application app)
    {
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes PlanExecution " + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project");
        string otherProject = Path.Combine(root, "other-project");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(otherProject);
        Environment.CurrentDirectory = root;
        string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        string fakeBin = Path.Combine(root, "fake bin");
        Directory.CreateDirectory(fakeBin);
        File.WriteAllText(Path.Combine(fakeBin, "opencode.cmd"),
            "@echo off\r\n\"" + Environment.ProcessPath + "\" --terminal-fixture %*\r\n", System.Text.Encoding.ASCII);
        Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + originalPath);

        MainWindow? window = null;
        try
        {
            var startupHooks = new OpenCodeStartupHooks(
                _ => Task.FromResult(true),
                () => throw new InvalidOperationException("The isolated PlanExecution fixture must never prompt to install OpenCode."),
                _ => throw new InvalidOperationException("The isolated PlanExecution fixture must never run an installer."),
                (workspace, useAutoMode) => Task.FromResult<WorkspaceSetupResult?>(new(workspace, useAutoMode)));
            window = new MainWindow(Path.Combine(root, "settings.json"), autoStartCodex: false,
                initialWorkspace: project, startupHooks: startupHooks);
            window.Show();
            await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null,
                "the isolated test window obtains its conductor scheduler lock");
            var tabs = (TabControl)window.FindName("TerminalTabs");
            await UntilAsync(() => tabs.Items.Count == 1 && Pane((TabItem)tabs.Items[0]).IsRunning,
                "test window starts exactly one local shell terminal");
            // Later checks explicitly open another terminal and create a project; keep slots available.
            Call(window, "ChangeTerminalLayout", 4, false);

            var sourceTab = (TabItem)tabs.Items[0];
            object source = sourceTab.Tag!;
            var sourcePane = Pane(sourceTab);
            var modeField = window.GetType().GetField("_planExecutionMode", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Type modeType = modeField.FieldType;
            var modeButtons = (IDictionary)FieldValue(window, "_planExecutionModes")!;
            var currentModeButton = (RadioButton)modeButtons[EnumValue(modeType, "CurrentTerminal")]!;
            var executeButton = Field<Button>(window, "_tabDocumentExecute");
            await UntilAsync(() => Field<TextBox>(window, "_tabDocumentEditor").IsEnabled,
                "the selected Plan editor finishes loading");
            string planPath = (string)GetProperty(source, "PlanDocumentPath")!;
            const string naturalLanguagePlan = "Please implement the documented feature and explain the changes.";
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            await File.WriteAllTextAsync(planPath, naturalLanguagePlan);
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            await (Task)Call(window, "ReloadTabDocumentAsync")!;
            await UntilAsync(() => editor.IsEnabled && editor.Text == naturalLanguagePlan,
                "the existing populated Plan reloads from disk into the selected editor");
            var snapshot = await new TerminalTabDocumentStore(project, planPath)
                .LoadAsync((int)GetProperty(source, "Sequence")!, TerminalInputMode.Plan);

            Check(modeField.GetValue(window)!.ToString() == "CurrentTerminal",
                "the safe default destination is the current terminal");
            Check(currentModeButton.IsChecked == true,
                "the stored execution destination defaults to the current terminal");
            Check(modeButtons.Values.Cast<RadioButton>().All(button => !button.IsVisible),
                "legacy destination selectors remain hidden in the single-terminal workspace");
            Check(tabs.Items.Count == 1 && !sourcePane.LaunchesCodex,
                "default selection keeps one non-team local shell and does not start model work");

            string? shellBlock = (string?)Call(window, "PlanExecutionBlockReason", source, EnumValue(modeType, "CurrentTerminal"));
            Check(!string.IsNullOrWhiteSpace(shellBlock),
                "Current Terminal refuses a shell pane without native OpenCode readiness");
            Exception? shellExecution = await InvokeTaskFailure(window, "ExecuteSavedPlanAsync",
                source, snapshot, EnumValue(modeType, "CurrentTerminal"));
            Check(shellExecution is InvalidOperationException && tabs.Items.Count == 1,
                "natural-language Markdown is never dispatched to the current shell");

            const string editedNaturalLanguagePlan = "Please implement the documented feature, test it, and explain the changes.";
            editor.Text = editedNaturalLanguagePlan;
            await (Task)Call(window, "SaveTabDocumentAsync", true)!;
            Check(File.ReadAllText(planPath) == editedNaturalLanguagePlan && tabs.Items.Count == 1 &&
                  (executeButton.Content as string) == "실행",
                "Execute first saves the current Plan while refusing to send it to the shell or create a team");
            snapshot = await new TerminalTabDocumentStore(project, planPath)
                .LoadAsync((int)GetProperty(source, "Sequence")!, TerminalInputMode.Plan);

            Check(GateReason(OpenCodeStatusKind.Idle) is null,
                "the native execution gate allows only a ready idle session");
            Check(!string.IsNullOrWhiteSpace(GateReason(OpenCodeStatusKind.Busy)) &&
                  !string.IsNullOrWhiteSpace(GateReason(OpenCodeStatusKind.NeedsInput)) &&
                  !string.IsNullOrWhiteSpace(GateReason(OpenCodeStatusKind.Unknown)),
                "native busy, approval/question, and unknown states all block plan execution");

            // Exercise MainWindow's integrated route with synthetic native monitor state while
            // retaining the shell-only test pane. It proves these states stop before SendTextAsync.
            Call(sourcePane, "BindSavedPlanExecutionPath", planPath);
            SetField(sourcePane, "_usesNativeOpenCodeStatus", true);
            foreach (OpenCodeStatusKind blockedStatus in new[] { OpenCodeStatusKind.Busy, OpenCodeStatusKind.NeedsInput })
            {
                SetField(sourcePane, "_openCodeSnapshot", new OpenCodeStatusSnapshot(blockedStatus, false, "fake-session"));
                string? reason = (string?)Call(window, "PlanExecutionBlockReason", source, EnumValue(modeType, "CurrentTerminal"));
                Exception? blockedExecution = await InvokeTaskFailure(window, "ExecuteSavedPlanAsync",
                    source, snapshot, EnumValue(modeType, "CurrentTerminal"));
                Check(!string.IsNullOrWhiteSpace(reason) && blockedExecution is InvalidOperationException && tabs.Items.Count == 1,
                    $"synthetic native {blockedStatus} state blocks dispatch before creating another session");
            }
            SetField(sourcePane, "_openCodeSnapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Busy, false, "fake-session"));
            Call(window, "RefreshTabDocumentControls", GetDocumentEditState(window, planPath));
            Check(!executeButton.IsEnabled,
                "the default current-terminal destination stays blocked while native OpenCode is busy");

            SetField(sourcePane, "_manualInputOverride", true);
            string automationLedgerPath = Path.Combine(project, "terminal-notes", "automation-runs.json");
            foreach (string unsupportedMode in new[] { "Conductor", "NewTerminal" })
            {
                object requestedMode = EnumValue(modeType, unsupportedMode);
                Call(window, "SetPlanExecutionMode", requestedMode);
                Check(modeField.GetValue(window)!.ToString() == "CurrentTerminal" &&
                      modeButtons.Values.Cast<RadioButton>().All(button => !button.IsVisible),
                    $"requesting unsupported {unsupportedMode} mode cannot change the hidden single-terminal destination");
                int paneCount = tabs.Items.Count;
                Exception? rejectedMode = await InvokeTaskFailure(window, "ExecuteSavedPlanAsync", source, snapshot, requestedMode);
                object projectRuntime = Call(window, "RuntimeFor", project)!;
                Check(rejectedMode is InvalidOperationException && tabs.Items.Count == paneCount &&
                      File.ReadAllText(planPath) == editedNaturalLanguagePlan && !File.Exists(automationLedgerPath) &&
                      projectRuntime.GetType().GetField("StartingRun")!.GetValue(projectRuntime) is false &&
                      projectRuntime.GetType().GetField("Stage")!.GetValue(projectRuntime) is null,
                    $"unsupported {unsupportedMode} execution rejects without dispatch, pane creation, run ledger, or Plan changes");
            }
            SetField(sourcePane, "_manualInputOverride", false);
            SetField(sourcePane, "_openCodeSnapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Busy, false, "fake-session"));
            Call(window, "RefreshTabDocumentControls", GetDocumentEditState(window, planPath));
            Check(modeField.GetValue(window)!.ToString() == "CurrentTerminal" && !executeButton.IsEnabled,
                "the only supported destination remains blocked while native OpenCode is busy");
            await VerifyAutomationOwnershipRetryAsync(window, planPath, editedNaturalLanguagePlan);

            SetField(sourcePane, "_manualInputOverride", false);
            SetField(sourcePane, "_usesNativeOpenCodeStatus", false);

            // Mismatched project and stale document binding fail before the pane's dispatch method.
            Call(window, "SetPlanExecutionMode", EnumValue(modeType, "CurrentTerminal"));
            SetField(window, "_selectedProjectPath", otherProject);
            string? mismatchReason = (string?)Call(window, "PlanExecutionBlockReason", source, EnumValue(modeType, "CurrentTerminal"));
            Exception? projectMismatch = await InvokeTaskFailure(window, "ExecuteSavedPlanAsync",
                source, snapshot, EnumValue(modeType, "CurrentTerminal"));
            Check(!string.IsNullOrWhiteSpace(mismatchReason) && projectMismatch is InvalidOperationException && tabs.Items.Count == 1,
                "a changed project target rejects the saved snapshot without creating or dispatching a session");
            SetField(window, "_selectedProjectPath", null);

            string reboundPath = Path.Combine(project, "terminal-notes", "rebound-plan.md");
            SetProperty(source, "PlanDocumentPath", reboundPath);
            Call(window, "SetPlanExecutionMode", EnumValue(modeType, "CurrentTerminal"));
            Exception? rebound = await InvokeTaskFailure(window, "ExecuteSavedPlanAsync",
                source, snapshot, EnumValue(modeType, "CurrentTerminal"));
            Check(rebound is InvalidOperationException && tabs.Items.Count == 1,
                "a re-bound Markdown path rejects an old snapshot without dispatch");
            SetProperty(source, "PlanDocumentPath", planPath);

            Call(window, "SetPlanExecutionMode", EnumValue(modeType, "NewTerminal"));
            int beforeNewMode = tabs.Items.Count;
            Exception? newTerminal = await InvokeTaskFailure(window, "ExecuteSavedPlanAsync",
                source, snapshot, EnumValue(modeType, "NewTerminal"));
            Check(newTerminal is InvalidOperationException && tabs.Items.Count == beforeNewMode &&
                  modeField.GetValue(window)!.ToString() == "CurrentTerminal",
                "explicit New Terminal mode is rejected without creating a pane or changing the destination");
            Check(tabs.Items.Cast<TabItem>().Count(tab => Pane(tab).LaunchesCodex) == 0,
                "test harness never starts OpenCode or sends a real model request");

            string originalPlanAfterNewMode = File.ReadAllText(planPath);
            int beforeCreateProject = tabs.Items.Count;
            Call(window, "CreateNewProject", "fresh");
            string createdProject = Path.Combine(project, "fresh");
            string createdPlan = Path.Combine(createdProject, "terminal-notes", "plan.md");
            await UntilAsync(() => tabs.Items.Count == beforeCreateProject + 1 &&
                                   Pane((TabItem)tabs.SelectedItem!).WorkingDirectory == createdProject,
                "creating a new project selects its single project terminal");
            string freshPlanText = await File.ReadAllTextAsync(createdPlan);
            Check(File.Exists(createdPlan) && freshPlanText.Contains("# fresh", StringComparison.Ordinal) &&
                  tabs.Items.Count == beforeCreateProject + 1 && File.ReadAllText(planPath) == originalPlanAfterNewMode,
                "Create New Project creates a project-specific Plan and terminal without changing the existing Plan");
            Call(window, "CreateNewProject", "fresh");
            Check(tabs.Items.Count == beforeCreateProject + 1 && await File.ReadAllTextAsync(createdPlan) == freshPlanText,
                "duplicate project creation preserves the existing Plan and pane count");

            Call(window, "SelectProject", project);
            await UntilAsync(() => tabs.Items.Count >= 1 && Pane((TabItem)tabs.SelectedItem!).WorkingDirectory == project,
                "returning to the original project before closing its terminals");
            foreach (TabItem openTab in tabs.Items.Cast<TabItem>().ToArray()) Pane(openTab).Dispose();
            Call(window, "CloseAllTerminals", false);
            SetField(window, "_selectedProjectPath", project);
            Call(window, "UpdateWorkbenchTarget");
            var zeroTerminalEditor = Field<TextBox>(window, "_tabDocumentEditor");
            await UntilAsync(() => tabs.Items.Count == 0 && zeroTerminalEditor.IsEnabled && zeroTerminalEditor.Text == originalPlanAfterNewMode,
                "the saved project Plan remains editable after all terminals close");
            var noTerminalCurrentButton = (RadioButton)modeButtons[EnumValue(modeType, "CurrentTerminal")]!;
            var noTerminalNewButton = (RadioButton)modeButtons[EnumValue(modeType, "NewTerminal")]!;
            var noTerminalConductorButton = (RadioButton)modeButtons[EnumValue(modeType, "Conductor")]!;
            Check(!noTerminalCurrentButton.IsEnabled && !noTerminalNewButton.IsVisible && !noTerminalConductorButton.IsVisible,
                "zero-terminal workspace keeps unsupported destination controls hidden");
            zeroTerminalEditor.Text = "zero-terminal saved draft";
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            Check(File.ReadAllText(planPath) == "zero-terminal saved draft",
                "the selected project Plan can be saved while no terminals are open");
            object zeroTerminalState = GetDocumentEditState(window, planPath);
            var zeroTerminalSnapshot = (TabDocumentSnapshot)GetProperty(zeroTerminalState, "Snapshot")!;
            string zeroTerminalDraft = (string)GetProperty(zeroTerminalState, "Draft")!;
            Check(zeroTerminalDraft == zeroTerminalSnapshot.Text,
                "saving the zero-terminal Plan leaves no dirty draft before external reload");
            if (zeroTerminalDraft != zeroTerminalSnapshot.Text)
                throw new InvalidOperationException("The saved zero-terminal Plan is still dirty; refusing to open a reload confirmation dialog in the test.");
            await File.WriteAllTextAsync(planPath, "zero-terminal external edit");
            await (Task)Call(window, "ReloadTabDocumentAsync")!;
            object reloadingState = GetDocumentEditState(window, planPath);
            var reloadTask = (Task)GetProperty(reloadingState, "LoadTask")!;
            await reloadTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check(zeroTerminalEditor.IsEnabled && zeroTerminalEditor.Text == "zero-terminal external edit",
                "the selected project Plan can be reloaded while no terminals are open");
            var noTerminalSnapshot = await new TerminalTabDocumentStore(project, planPath)
                .LoadAsync(1, TerminalInputMode.Plan);
            SetField(window, "_selectedProjectPath", null);
            Exception? noTerminalCurrent = await InvokeTaskFailure(window, "ExecuteSavedPlanAsync",
                null, noTerminalSnapshot, EnumValue(modeType, "CurrentTerminal"));
            Check(noTerminalCurrent is InvalidOperationException && tabs.Items.Count == 0,
                "Current Terminal refuses dispatch when neither a terminal source nor project is selected");
            Call(window, "SetPlanExecutionMode", EnumValue(modeType, "NewTerminal"));
            int beforeNoSourceLaunch = tabs.Items.Count;
            Exception? noSourceLaunch = await InvokeTaskFailure(window, "ExecuteSavedPlanAsync",
                null, noTerminalSnapshot, EnumValue(modeType, "NewTerminal"));
            Check(noSourceLaunch is InvalidOperationException && tabs.Items.Count == beforeNoSourceLaunch &&
                  modeField.GetValue(window)!.ToString() == "CurrentTerminal",
                "zero-source explicit New Terminal mode rejects without a project or creating a pane");
        }
        finally
        {
            if (window is not null) Call(window, "DisposeResources");
            window?.Close();
            Environment.SetEnvironmentVariable("PATH", originalPath);
            string resolvedTemp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolvedRoot = Path.GetFullPath(root);
            if (!resolvedRoot.StartsWith(resolvedTemp, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing recursive cleanup outside the unique PlanExecution temporary fixture directory.");
            try { Directory.Delete(resolvedRoot, recursive: true); } catch { }
        }

        Console.WriteLine(_failures == 0 ? "PlanExecution checks passed." : $"{_failures} PlanExecution check(s) failed.");
    }

    private static TerminalPane Pane(TabItem tab) =>
        (TerminalPane)GetProperty(tab.Tag!, "Pane")!;

    private static object? GetProperty(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetProperty(object target, string name, object? value) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? FieldValue(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static object GetDocumentEditState(object window, string path)
    {
        var states = (IDictionary)FieldValue(window, "_tabDocumentStates")!;
        return states[Path.GetFullPath(path)]
            ?? throw new InvalidOperationException("The existing Plan is not present in the document cache.");
    }

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static object EnumValue(Type enumType, string name) => Enum.Parse(enumType, name);

    private static async Task VerifyAutomationOwnershipRetryAsync(
        MainWindow window,
        string planPath,
        string savedPlan)
    {
        string settingsPath = (string)FieldValue(window, "_settingsPath")!;
        string ownershipPath = settingsPath + ".automation.lock";
        FileStream? originalOwnership = FieldValue(window, "_automationOwnership") as FileStream;
        Check(originalOwnership is not null && (bool)FieldValue(window, "_automationInitialized")!,
            "the isolated window initially owns automation scheduling");

        originalOwnership?.Dispose();
        SetField(window, "_automationOwnership", null);
        SetField(window, "_automationInitialized", false);
        using (var competingOwner = new FileStream(ownershipPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            bool initializedWhileContended = await (Task<bool>)Call(window, "TryInitializeAutomationAsync")!;
            Check(!initializedWhileContended && FieldValue(window, "_automationOwnership") is null,
                "a second scheduler cannot initialize while another process owns the isolated lock");
            Check(!string.IsNullOrWhiteSpace((string?)FieldValue(window, "_automationInitializationError")),
                "lock contention is reported while the scheduler remains uninitialized");
        }

        await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null &&
                               (bool)FieldValue(window, "_automationInitialized")!,
            "the retry timer acquires automation ownership after the competing owner releases it");
        Call(window, "RefreshTabDocumentControls", GetDocumentEditState(window, planPath));
        Check(string.IsNullOrWhiteSpace((string?)FieldValue(window, "_automationInitializationError")),
            "successful lock reacquisition clears the initialization error");
        Check(File.ReadAllText(planPath) == savedPlan &&
              FieldValue(window, "_planExecutionMode")!.ToString() == "CurrentTerminal" &&
              !File.Exists(Path.Combine(Path.GetDirectoryName(planPath)!, "automation-runs.json")),
            "automation ownership recovery preserves the Plan without enabling an unsupported destination or starting a run");
    }

    private static string? GateReason(OpenCodeStatusKind status)
    {
        var method = typeof(TerminalPane).GetMethod("GetSavedPlanExecutionGateBlockReason",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var stateType = method.GetParameters().Single().ParameterType;
        var constructor = stateType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(OpenCodeStatusKind)],
            modifiers: null)!;
        object state = constructor.Invoke([true, true, false, false, true, false, false, false, false, status]);
        return (string?)method.Invoke(null, [state]);
    }

    private static async Task<Exception?> InvokeTaskFailure(object target, string method, params object?[] args)
    {
        try
        {
            var task = (Task)Call(target, method, args)!;
            await task;
            return null;
        }
        catch (TargetInvocationException ex) { return ex.InnerException ?? ex; }
        catch (Exception ex) { return ex; }
    }

    private static async Task UntilAsync(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(label);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            _failures++;
            Console.Error.WriteLine("FAIL " + message);
            return;
        }
        Console.WriteLine("PASS " + message);
    }
}
