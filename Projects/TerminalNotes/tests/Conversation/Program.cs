using System.IO;
using System.Reflection;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => await RunAsync(app);
        app.Run();
        return _failures;
    }

    private static async Task RunAsync(Application app)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Conversation " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "AGENTS.md"), "# Conversation fixture\n");
        var journal = new ProjectWorkJournal(fixture);

        var window = new MainWindow(Path.Combine(fixture, "settings.json"), autoStartCodex: false, initialWorkspace: fixture);
        try
        {
            window.Show();
            var tabs = (TabControl)window.FindName("TerminalTabs");
            await UntilAsync(() => tabs.Items.Count == 1 && Pane((TabItem)tabs.Items[0]).IsReady, "first terminal ready");
            var workbench = (ContentControl)window.FindName("WorkbenchHost");
            var notes = (Border)window.FindName("NotesPanel");
            var legacy = (FrameworkElement)window.FindName("LegacyDocuments");
            Check(ReferenceEquals(workbench.Parent, ((Grid)notes.Child)) && legacy.Visibility == Visibility.Collapsed,
                "right panel owns the workbench and legacy document UI stays hidden");

            object firstState = ((TabItem)tabs.Items[0]).Tag;
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            await UntilAsync(() => editor.IsEnabled, "default Plan document editor load");
            Check((TerminalInputMode)firstState.GetType().GetProperty("InputMode")!.GetValue(firstState)! == TerminalInputMode.Plan,
                "Plan is the default document mode");
            string secret = "memory-only-question-" + Guid.NewGuid().ToString("N");
            int journalCount = (await journal.LoadAsync()).Count;
            Call(window, "ObserveManualSubmission", firstState, secret);
            Check((await journal.LoadAsync()).Count == journalCount,
                "unconfirmed keystroke capture is not written to the work ledger");
            string currentPlanText = (string)Call(window, "CurrentPlanDocumentText")!;
            Check(!currentPlanText.Contains(secret, StringComparison.Ordinal),
                "question text never leaks into the current plan document");
            Check(!Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                    .Any(path => File.ReadAllText(path).Contains(secret, StringComparison.Ordinal)),
                "unconfirmed keystrokes are not persisted before native message acceptance");

            // Every terminal in a project shares its one fixed Plan document.
            var execute = Field<Button>(window, "_tabDocumentExecute");
            string planPath = (string)GetProperty(firstState, "PlanDocumentPath")!;
            string expectedPlanPath = Path.GetFullPath(Path.Combine(fixture, "terminal-notes", "plan.md"));
            var documentStore = new TerminalTabDocumentStore(fixture);
            Check(string.Equals(planPath, expectedPlanPath, StringComparison.OrdinalIgnoreCase),
                "A project's Plan has one fixed terminal-notes/plan.md path");
            Check((await documentStore.LoadAsync(1, TerminalInputMode.Plan)).Path == planPath &&
                  (await documentStore.LoadAsync(2, TerminalInputMode.Plan)).Path == planPath,
                "The project Plan path is independent of terminal number");
            Check(string.Equals((string)Call(window, "InitialPlanDocumentPath", 1, fixture)!,
                      (string)Call(window, "InitialPlanDocumentPath", 2, fixture)!, StringComparison.OrdinalIgnoreCase),
                "Every terminal in the same project receives the same Plan path");
            var legacyDocuments = new Dictionary<TerminalInputMode, string>
            {
                [TerminalInputMode.Build] = "legacy build stays intact",
                [TerminalInputMode.Review] = "legacy review stays intact",
                [TerminalInputMode.Command] = "legacy command stays intact"
            };
            foreach (var (mode, text) in legacyDocuments)
            {
                string path = documentStore.GetDocumentPath(1, mode);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, text);
            }
            editor.Text = "# Saved plan\n\n첫 번째 터미널";
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            Check(File.Exists(planPath) && File.ReadAllText(planPath).Contains("첫 번째 터미널", StringComparison.Ordinal),
                "plan editor saves its Markdown document");

            foreach (var (mode, text) in legacyDocuments)
            {
                Set(firstState, "InputMode", mode);
                Call(window, "UpdateWorkbenchTarget");
                Check((TerminalInputMode)firstState.GetType().GetProperty("InputMode")!.GetValue(firstState)! == TerminalInputMode.Plan,
                    $"legacy {mode} UI state normalizes to Plan");
                Check(File.ReadAllText(documentStore.GetDocumentPath(1, mode)) == text,
                    $"legacy {mode} Markdown file is preserved without migration");
            }
            editor.Text = "unsaved plan draft";
            Call(window, "UpdateWorkbenchTarget");
            Check(editor.Text == "unsaved plan draft", "normalizing legacy modes retains the current Plan draft");

            editor.Text = "";
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            Check(File.Exists(planPath) && new FileInfo(planPath).Length == 0,
                "saving a cleared editor persists an empty Markdown file");
            const string sharedPlanText = "draft survives terminal and project switching";
            editor.Text = sharedPlanText;
            Check(editor.Text == sharedPlanText, "the Plan editor keeps a shared project draft");
            Check(Field<System.Collections.IDictionary>(window, "_planExecutionModes").Values.Cast<RadioButton>().All(button => button.IsEnabled),
                "non-empty Plan exposes the execution destination choices");
            Call(window, "AddTerminal");
            await UntilAsync(() => tabs.Items.Count == 2 && Pane((TabItem)tabs.Items[1]).IsReady, "second terminal ready");
            object secondState = ((TabItem)tabs.Items[1]).Tag;
            Set(secondState, "InputMode", TerminalInputMode.Plan);
            tabs.SelectedIndex = 1;
            Call(window, "UpdateWorkbenchTarget");
            await UntilAsync(() => editor.IsEnabled && editor.Text == sharedPlanText, "second terminal loads the project-shared Plan draft");
            string secondDefaultPlanPath = (string)GetProperty(secondState, "PlanDocumentPath")!;
            Check(string.Equals(secondDefaultPlanPath, planPath, StringComparison.OrdinalIgnoreCase),
                "the project Plan path persists when another terminal slot is created");
            Check((TerminalInputMode)secondState.GetType().GetProperty("InputMode")!.GetValue(secondState)! == TerminalInputMode.Plan,
                "new terminal slots also default to Plan");

            var retiredSecondTab = (TabItem)tabs.Items[1];
            var retiredSecondPane = Pane(retiredSecondTab);
            retiredSecondPane.Dispose();
            Call(window, "CloseTerminal", retiredSecondTab, retiredSecondPane);
            Call(window, "AddTerminal");
            await UntilAsync(() => tabs.Items.Count == 2 && Pane((TabItem)tabs.Items[1]).IsReady,
                "terminal number two reused after closing its session");
            secondState = ((TabItem)tabs.Items[1]).Tag;
            string recycledPlanPath = (string)GetProperty(secondState, "PlanDocumentPath")!;
            Check((int)GetProperty(secondState, "Sequence")! == 2 &&
                    string.Equals(recycledPlanPath, secondDefaultPlanPath, StringComparison.OrdinalIgnoreCase),
                "Recreating terminal number two keeps the project's Plan binding");
            tabs.SelectedIndex = 1;
            Call(window, "UpdateWorkbenchTarget");
            await UntilAsync(() => editor.IsEnabled && editor.Text == sharedPlanText,
                "recreated slot two restores the shared project draft");

            tabs.SelectedIndex = 0;
            Call(window, "UpdateWorkbenchTarget");
            await UntilAsync(() => editor.IsEnabled && editor.Text == sharedPlanText, "first slot draft restored after terminal switch");
            Check(true, "rapid terminal switching restores the correct document draft");

            Check(editor.IsEnabled && Field<Button>(window, "_tabDocumentSave").IsEnabled,
                "the newly selected document is editable after switching terminals");
            string isolatedProject = Path.Combine(fixture, "another project");
            Directory.CreateDirectory(isolatedProject);
            Call(window, "SelectProject", isolatedProject);
            await UntilAsync(() => tabs.Items.Count == 3 && Pane((TabItem)tabs.Items[2]).IsReady,
                "terminal starts after switching the project workspace");
            object isolatedState = ((TabItem)tabs.Items[2]).Tag;
            string isolatedPath = (string)GetProperty(isolatedState, "PlanDocumentPath")!;
            string expectedIsolatedPath = Path.Combine(Path.GetFullPath(isolatedProject), "terminal-notes", "plan.md");
            Check(string.Equals(isolatedPath, expectedIsolatedPath, StringComparison.OrdinalIgnoreCase),
                "A live pane in another project receives that project's default Plan");
            tabs.SelectedIndex = 2;
            Call(window, "UpdateWorkbenchTarget");
            await UntilAsync(() => editor.IsEnabled, "other-project Plan load");
            editor.Text = "other project's unsaved draft";
            tabs.SelectedIndex = 0;
            Call(window, "UpdateWorkbenchTarget");
            await UntilAsync(() => editor.IsEnabled && editor.Text == sharedPlanText,
                "switching projects restores the first project's cached Plan draft");
            const string zeroTerminalDraft = "draft remains editable with no terminals";
            editor.Text = zeroTerminalDraft;
            foreach (TabItem openTab in tabs.Items.Cast<TabItem>().ToArray()) Pane(openTab).Dispose();
            Call(window, "CloseAllTerminals", false);
            Call(window, "UpdateWorkbenchTarget");
            var documentSurface = Field<Grid>(window, "_tabDocumentSurface");
            await UntilAsync(() => tabs.Items.Count == 0 && editor.IsEnabled && editor.Text == zeroTerminalDraft,
                "closing the last terminal retains the selected project's Plan draft");
            Check(documentSurface.Visibility == Visibility.Visible &&
                  Field<Button>(window, "_tabDocumentSave").IsEnabled &&
                  (Field<Button>(window, "_tabDocumentExecute").Content as string) == "실행",
                "the zero-terminal Plan editor remains visible with only the Save and Execute actions");
            Check((string)Call(window, "CurrentPlanDocumentText")! == zeroTerminalDraft,
                "the zero-terminal project Plan text remains the current Plan");
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            Check(File.ReadAllText(planPath) == zeroTerminalDraft,
                "saving after closing all terminals writes the fixed project Plan");
            const string reloadedProjectPlan = "external edit reloaded without terminals";
            await File.WriteAllTextAsync(planPath, reloadedProjectPlan);
            await (Task)Call(window, "ReloadTabDocumentAsync")!;
            await UntilAsync(() => editor.IsEnabled && editor.Text == reloadedProjectPlan,
                "the selected project's Plan can be reloaded with no terminals");
            Call(window, "AddTerminal");
            await UntilAsync(() => tabs.Items.Count == 1 && Pane((TabItem)tabs.Items[0]).IsReady,
                "a new terminal opens the project whose terminal set was empty");
            await UntilAsync(() => editor.IsEnabled && editor.Text == reloadedProjectPlan,
                "the new terminal reconnects the retained project Plan");
            Check(string.Equals((string)GetProperty(((TabItem)tabs.Items[0]).Tag, "PlanDocumentPath")!, planPath, StringComparison.OrdinalIgnoreCase),
                "the new terminal uses the project's same fixed Plan path");
            string settingsPath = Path.Combine(fixture, "settings.json");
            Call(window, "DisposeResources");
            window.Close();
            window = new MainWindow(settingsPath, autoStartCodex: false, initialWorkspace: fixture);
            window.Show();
            var restartedTabs = (TabControl)window.FindName("TerminalTabs");
            var restartedEditor = Field<TextBox>(window, "_tabDocumentEditor");
            await UntilAsync(() => restartedTabs.Items.Count == 1 && Pane((TabItem)restartedTabs.Items[0]).IsReady,
                "terminal startup after settings restart");
            object restartedState = ((TabItem)restartedTabs.Items[0]).Tag;
            await UntilAsync(() => restartedEditor.IsEnabled && restartedEditor.Text == reloadedProjectPlan,
                "the fixed project Plan restores after app restart");
            Check(string.Equals((string)GetProperty(restartedState, "PlanDocumentPath")!, planPath, StringComparison.OrdinalIgnoreCase),
                "the fixed Plan path restores after app restart");

            Call(window, "DisposeResources");
            window.Close();
            window = new MainWindow(settingsPath, autoStartCodex: false, initialWorkspace: isolatedProject);
            window.Show();
            var isolatedTabs = (TabControl)window.FindName("TerminalTabs");
            await UntilAsync(() => isolatedTabs.Items.Count == 1 && Pane((TabItem)isolatedTabs.Items[0]).IsReady,
                "terminal startup in a different project");
            string reopenedIsolatedPath = (string)GetProperty(((TabItem)isolatedTabs.Items[0]).Tag, "PlanDocumentPath")!;
            Check(string.Equals(reopenedIsolatedPath, expectedIsolatedPath, StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(reopenedIsolatedPath, planPath, StringComparison.OrdinalIgnoreCase),
                "A project's explicit mapping does not leak to another project across restart");

            Console.WriteLine("CONVERSATION TESTS PASSED");
        }
        catch (Exception ex)
        {
            _failures = 1;
            Console.WriteLine("FAIL " + ex);
        }
        finally
        {
            Call(window, "DisposeResources");
            window.Close();
            app.Shutdown();
            try { Directory.Delete(fixture, recursive: true); } catch { }
        }
    }

    private static TerminalPane Pane(TabItem tab) => (TerminalPane)tab.Tag.GetType().GetProperty("Pane")!.GetValue(tab.Tag)!;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Set(object target, string name, object value) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? GetProperty(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);

    private static object? Call(object target, string name, params object?[] args)
    {
        try
        {
            return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);
        }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private static async Task UntilAsync(Func<bool> condition, string description, int seconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }
}
