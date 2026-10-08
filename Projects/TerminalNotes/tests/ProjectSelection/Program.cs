using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _fail;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--terminal-fixture"))
        {
            // Navigation only: no provider, installation or native API work.
            // Native role execution is covered by WorkflowIntegration.
            Console.WriteLine("PROJECT_NAVIGATION_FIXTURE");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => await Run(app);
        app.Run();
        return _fail;
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static T Property<T>(object instance, string name) =>
        (T)instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static object? Call(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);

    private static string PaneReadiness(TerminalPane pane)
    {
        object? FieldValue(string name) => pane.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(pane);
        object? startup = FieldValue("_terminalStartedCompletion");
        object? initialize = FieldValue("_initializeTask");
        object? browser = FieldValue("_browser");
        string startupState = startup is Task task
            ? $"{task.Status}/{(task.IsCompleted ? (startup.GetType().GetProperty("Result")?.GetValue(startup)?.ToString() ?? "success") : "pending")}" : "missing";
        return $"loaded={pane.IsLoaded}, visible={pane.IsVisible}, size={pane.ActualWidth:F0}x{pane.ActualHeight:F0}, ready={FieldValue("_ready")}, failed={FieldValue("_failed")}, dimensions={FieldValue("_pendingWebReadyDimensions")}, initialize={((initialize as Task)?.Status.ToString() ?? "missing")}, browser={browser?.GetType().GetProperty("CoreWebView2")?.GetValue(browser)?.GetType().Name ?? "not-ready"}, startup={startupState}";
    }

    private static TerminalPane Pane(TabItem tab) =>
        (TerminalPane)tab.Tag!.GetType().GetProperty("Pane")!.GetValue(tab.Tag)!;

    private static TabItem[] ProjectTabs(TabControl tabs, string project) =>
        tabs.Items.Cast<TabItem>().Where(tab => string.Equals(
            Path.GetFullPath(Pane(tab).WorkingDirectory), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase)).ToArray();

    private static string[] ReadDocumentBindings(string settingsPath)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return settings.RootElement.GetProperty("DocumentBindings").EnumerateArray()
            .Select(binding => binding.GetProperty("Project").GetString() + "\0" + binding.GetProperty("Path").GetString())
            .ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task Until(Func<bool> condition, string name, int seconds = 25)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(name);
    }

    private static async Task Run(Application app)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Projects " + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(fixture, "workspace root");
        string firstProject = Path.Combine(fixture, "first project");
        string secondProject = Path.Combine(fixture, "second project");
        string canonicalWinsProject = Path.Combine(fixture, "canonical wins project");
        string emptyProject = Path.Combine(fixture, "empty project");
        string concurrentProject = Path.Combine(fixture, "concurrent project");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(firstProject);
        Directory.CreateDirectory(secondProject);
        Directory.CreateDirectory(canonicalWinsProject);
        Directory.CreateDirectory(emptyProject);
        Directory.CreateDirectory(concurrentProject);
        string settingsPath = Path.Combine(fixture, "settings.json");
        string persistedCustomPlan = Path.Combine(fixture, "old custom plans", "first.md");
        string ignoredCustomPlan = Path.Combine(fixture, "old custom plans", "canonical wins.md");
        Directory.CreateDirectory(Path.GetDirectoryName(persistedCustomPlan)!);
        File.WriteAllText(persistedCustomPlan, "# Persisted custom plan\n\nKeep this source.");
        File.WriteAllText(ignoredCustomPlan, "# Custom copy must stay untouched.");
        string secondLegacyPlan = Path.Combine(secondProject, "terminal-notes", "plans", "old-session", "plan.md");
        Directory.CreateDirectory(Path.GetDirectoryName(secondLegacyPlan)!);
        File.WriteAllText(secondLegacyPlan, "# Legacy plan\n\nKeep the original too.");
        string canonicalWinsPlan = TerminalTabDocumentStore.GetProjectPlanPath(canonicalWinsProject);
        Directory.CreateDirectory(Path.GetDirectoryName(canonicalWinsPlan)!);
        File.WriteAllText(canonicalWinsPlan, "# Canonical is authoritative.");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new
        {
            Workspace = workspace,
            CodexWorkspace = workspace,
            DocumentBindings = new[]
            {
                new { Project = firstProject, Path = persistedCustomPlan },
                new { Project = canonicalWinsProject, Path = ignoredCustomPlan }
            }
        }));
        Environment.CurrentDirectory = fixture;
        string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        string fakeBin = Path.Combine(fixture, "fake bin");
        Directory.CreateDirectory(fakeBin);
        File.WriteAllText(Path.Combine(fakeBin, "opencode.cmd"),
            "@echo off\r\n\"" + Environment.ProcessPath + "\" --terminal-fixture %*\r\n", System.Text.Encoding.ASCII);
        Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + originalPath);

        MainWindow? window = null;
        MainWindow? restored = null;
        try
        {
            window = new MainWindow(settingsPath, autoStartCodex: false, initialWorkspace: workspace);
            window.Show();
            var tabs = (TabControl)window.FindName("TerminalTabs");
            await Until(() => tabs.Items.Count == 1 && Pane((TabItem)tabs.Items[0]).IsRunning, "initial terminal starts");
            Call(window, "ChangeTerminalLayout", 4, false);

            Call(window, "RememberProjectName", firstProject, "Same name");
            Call(window, "RememberProjectName", secondProject, "Same name");
            Call(window, "SelectProject", firstProject);
            try
            {
                await Until(() => tabs.Items.Count == 2 && ProjectTabs(tabs, firstProject) is [{ } firstProjectTab] && Pane(firstProjectTab).IsRunning,
                    "first selected project terminal starts");
            }
            catch (TimeoutException ex)
            {
                string tabState = string.Join("; ", tabs.Items.Cast<TabItem>().Select(tab =>
                    $"{Pane(tab).WorkingDirectory} [{PaneReadiness(Pane(tab))}]"));
                throw new TimeoutException($"{ex.Message}; count={tabs.Items.Count}; selected={tabs.SelectedIndex}; tabs={tabState}", ex);
            }

            var firstTab = (TabItem)tabs.SelectedItem;
            var firstPane = Pane(firstTab);
            string firstPlan = TerminalTabDocumentStore.GetProjectPlanPath(firstProject);
            Assert(File.Exists(firstPlan) && File.ReadAllText(firstPlan) == File.ReadAllText(persistedCustomPlan),
                "opening a project migrates its persisted custom Markdown into the canonical file and preserves the source");
            Assert(ReferenceEquals(firstTab, tabs.SelectedItem), "opening a project selects its terminal");
            Assert(Property<string>(window, "CurrentProjectPath") == firstProject, "selected project becomes the new-terminal target");
            Assert(Property<string>(firstTab.Tag!, "PlanDocumentPath") == firstPlan, "project terminal opens with that project's plan Markdown");
            Assert((string)Call(window, "InitialPlanDocumentPath", 1, firstProject)! == firstPlan,
                "persisted custom bindings never change the runtime canonical Plan path");

            Call(window, "SelectProject", firstProject);
            Assert(tabs.Items.Count == 2 && ProjectTabs(tabs, firstProject) is [{ } reusedFirstTab] &&
                   ReferenceEquals(firstTab, reusedFirstTab) && ReferenceEquals(firstPane, Pane(firstTab)) && ReferenceEquals(firstTab, tabs.SelectedItem),
                "reselecting an open project reuses its live terminal");

            Call(window, "SelectProject", secondProject);
            await Until(() => tabs.Items.Count == 3 && ProjectTabs(tabs, secondProject) is [{ } secondProjectTab] && Pane(secondProjectTab).IsRunning,
                "second same-named project terminal starts");
            var secondTab = (TabItem)tabs.SelectedItem;
            string secondPlan = TerminalTabDocumentStore.GetProjectPlanPath(secondProject);
            Assert(File.Exists(secondPlan) && File.ReadAllText(secondPlan) == File.ReadAllText(secondLegacyPlan),
                "opening a project migrates a legacy plan into the canonical file and preserves the source");
            Assert(Property<string>(window, "CurrentProjectPath") == secondProject && ReferenceEquals(secondTab, tabs.SelectedItem),
                "same display name does not merge project identities");

            var sessionButtons = Field<Dictionary<TabItem, Button>>(window, "_sessionButtons");
            string oneSessionSuffix = "1" + "\uAC1C \uC138\uC158";
            bool HasDistinctProjectGroup(TabItem tab, string project)
            {
                Button button = sessionButtons[tab];
                TextBlock? groupHeading = (button.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault();
                string? toolTip = button.ToolTip?.ToString();
                return groupHeading is not null && groupHeading.Text.Contains("Same name", StringComparison.Ordinal) &&
                    groupHeading.Text.Contains(oneSessionSuffix, StringComparison.Ordinal) &&
                    toolTip is not null && toolTip.Contains(project, StringComparison.OrdinalIgnoreCase);
            }
            Assert(HasDistinctProjectGroup(firstTab, firstProject) && HasDistinctProjectGroup(secondTab, secondProject),
                "same-named single-terminal project groups retain distinct project paths");
            ((Button)window.FindName("NewTerminalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => tabs.Items.Count == 3 && ReferenceEquals(tabs.SelectedItem, secondTab), "new terminal action reuses selected project terminal");
            Assert(Pane(secondTab).WorkingDirectory == secondProject,
                "normal terminal action stays in the selected project's folder");
            tabs.SelectedItem = firstTab;
            Assert(Property<string>(window, "CurrentProjectPath") == firstProject,
                "manually selecting a terminal updates the active project target");
            ((Button)window.FindName("NewTerminalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => tabs.Items.Count == 3 && ReferenceEquals(tabs.SelectedItem, firstTab), "new terminal action follows the selected project terminal");
            Assert(Pane(firstTab).WorkingDirectory == firstProject,
                "terminal action follows the project of the manually selected terminal");
            tabs.SelectedItem = secondTab;
            Assert(Property<string>(window, "CurrentProjectPath") == secondProject,
                "selecting another project's terminal restores its project target");

            string unchangedCanonical = (string)Call(window, "EnsureProjectPlanDocument", canonicalWinsProject)!;
            Assert(unchangedCanonical == canonicalWinsPlan && File.ReadAllText(canonicalWinsPlan) == "# Canonical is authoritative." &&
                   File.ReadAllText(ignoredCustomPlan) == "# Custom copy must stay untouched.",
                "an existing canonical file is never overwritten by a persisted custom binding");
            Call(window, "SelectProject", emptyProject);
            string emptyPlan = TerminalTabDocumentStore.GetProjectPlanPath(emptyProject);
            Assert(emptyPlan == TerminalTabDocumentStore.GetProjectPlanPath(emptyProject) && File.Exists(emptyPlan) &&
                   new FileInfo(emptyPlan).Length == 0 && Property<string>(window, "CurrentProjectPath") == emptyProject,
                "selecting an existing project without a plan creates its empty canonical Markdown file immediately");
            Call(window, "SelectProject", secondProject);
            string[] concurrentPaths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                (string)Call(window, "EnsureProjectPlanDocument", concurrentProject)!)));
            string concurrentPlan = TerminalTabDocumentStore.GetProjectPlanPath(concurrentProject);
            Assert(concurrentPaths.All(path => path == concurrentPlan) && File.Exists(concurrentPlan) &&
                   new FileInfo(concurrentPlan).Length == 0 &&
                   !Directory.EnumerateFiles(Path.GetDirectoryName(concurrentPlan)!, "plan.md.*.tmp").Any(),
                "concurrent project opens atomically create one complete canonical file without leftover temp files");
            Assert(window.GetType().GetMethod("OpenExistingProject", BindingFlags.Instance | BindingFlags.NonPublic) is not null,
                "the existing-project folder picker has a direct entry point for the start UI");

            Call(window, "SaveSettings");
            using (var saved = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                Assert(Path.GetFullPath(saved.RootElement.GetProperty("CodexWorkspace").GetString()!) == workspace,
                    "project selection leaves the configured root workspace intact");
                Assert(Path.GetFullPath(saved.RootElement.GetProperty("SelectedProject").GetString()!) == secondProject,
                    "selected project path persists separately from the root workspace");
            }
            string? persistedDocumentPath;
            using (var saved = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                persistedDocumentPath = saved.RootElement.GetProperty("Document").GetString();

            Call(window, "DisposeResources");
            window.Close();
            window = null;

            string scheduledProject = Path.Combine(fixture, "scheduled automation project");
            Directory.CreateDirectory(scheduledProject);
            var automationStore = new AutomationStore(scheduledProject);
            AutomationRun scheduledRun = await automationStore.StartApprovedPlanAsync(
                "Persisted approved plan; startup must not dispatch it.", scheduledFor: DateTimeOffset.UtcNow.AddMinutes(-1));
            File.WriteAllText(settingsPath + ".automation-projects.json", JsonSerializer.Serialize(new[] { scheduledProject }));

            string nonEmptyPlan = TerminalTabDocumentStore.GetProjectPlanPath(workspace);
            Directory.CreateDirectory(Path.GetDirectoryName(nonEmptyPlan)!);
            const string savedPlanText = "# Saved workspace plan\n\nDo not load this into the fresh screen.";
            File.WriteAllText(nonEmptyPlan, savedPlanText);
            persistedDocumentPath = Path.GetFullPath(secondPlan);
            var settingsNode = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            settingsNode["Document"] = persistedDocumentPath;
            File.WriteAllText(settingsPath, settingsNode.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            string[] persistedDocumentBindings = ReadDocumentBindings(settingsPath);

            restored = new MainWindow(settingsPath, autoStartCodex: false);
            restored.Show();
            var restoredTabs = (TabControl)restored.FindName("TerminalTabs");
            DateTimeOffset recoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
            AutomationRun? recoveredRun = null;
            while (DateTimeOffset.UtcNow < recoveryDeadline)
            {
                recoveredRun = await automationStore.GetAsync(scheduledRun.Id);
                if (recoveredRun?.State == AutomationState.Paused) break;
                await Task.Delay(100);
            }
            await Until(() => restoredTabs.Items.Count == 0, "fresh launch keeps terminal area empty");
            await Until(() => Field<bool>(restored, "_automationInitialized"), "startup automation recovery finishes");
            Assert(Field<string>(restored, "_workspace") == workspace, "restart restores the root workspace independently");
            Assert(Property<string>(restored, "CurrentProjectPath") == workspace && Field<string?>(restored, "_selectedProjectPath") is null,
                "restart does not restore the previously selected project into the active screen");
            var planEditor = Field<TextBox?>(restored, "_tabDocumentEditor");
            Assert(planEditor is not null && planEditor.Text.Length == 0 && !planEditor.IsEnabled &&
                   Field<FrameworkElement?>(restored, "_tabDocumentSurface")?.Visibility == Visibility.Collapsed &&
                   Field<FrameworkElement?>(restored, "_projectStartSurface")?.Visibility == Visibility.Visible,
                "restart shows the project start surface with no selected or loaded plan editor");
            Assert(File.Exists(Path.Combine(secondProject, "terminal-notes", "plan.md")) &&
                   File.ReadAllText(Path.Combine(secondProject, "terminal-notes", "plan.md")) == "# Legacy plan\n\nKeep the original too.",
                "restart leaves the saved project plan Markdown untouched");
            Assert(File.ReadAllText(nonEmptyPlan) == savedPlanText,
                "restart leaves the nonempty workspace plan file untouched");
            using (var saved = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                Assert(Path.GetFullPath(saved.RootElement.GetProperty("SelectedProject").GetString()!) == secondProject,
                    "restart preserves the remembered project preference in settings without auto-opening it");
            Assert(recoveredRun?.State == AutomationState.Stopped && recoveredRun.Id == scheduledRun.Id &&
                   recoveredRun.ApprovedPlanSnapshot == scheduledRun.ApprovedPlanSnapshot &&
                   recoveredRun.Iteration == scheduledRun.Iteration,
                "restart stops due scheduled automation while preserving its run history");
            Assert(restoredTabs.Items.Count == 0 && recoveredRun?.State == AutomationState.Stopped,
                "a due automation run cannot create terminals or resume model work on startup");
            await (Task)Call(restored, "PollAutomationAsync")!;
            await Task.Delay(1_200);
            await (Task)Call(restored, "PollAutomationAsync")!;
            recoveredRun = await automationStore.GetAsync(scheduledRun.Id);
            Assert(recoveredRun?.State == AutomationState.Stopped && restoredTabs.Items.Count == 0,
                "stopped run remains stopped after a scheduler poll tick");
            using (var saved = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                Assert(saved.RootElement.GetProperty("Document").GetString() == persistedDocumentPath,
                    "blank restart retains the nonempty saved document path preference without loading its contents");
            Call(restored, "SaveSettings");
            using (var saved = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                Assert(saved.RootElement.GetProperty("Document").GetString() == persistedDocumentPath,
                    "saving blank startup settings retains the prior document path preference");
            Assert(ReadDocumentBindings(settingsPath).SequenceEqual(persistedDocumentBindings),
                "saving blank startup settings preserves all project document bindings");
            Console.WriteLine("PROJECT SELECTION TESTS PASSED");
        }
        catch (Exception ex)
        {
            _fail = 1;
            Console.WriteLine("FAIL " + ex);
        }
        finally
        {
            if (restored is not null) CloseTestWindow(restored);
            if (window is not null) CloseTestWindow(window);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            app.Shutdown();
            Console.WriteLine("Fixture: " + fixture);
        }
    }

    private static void CloseTestWindow(MainWindow window)
    {
        Call(window, "DisposeResources");
        window.Close();
    }
}
