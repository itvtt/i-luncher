using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static class IndependentTerminalSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Members)!.GetValue(value)!;
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Members)!.GetValue(value)!;
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Members)!.Invoke(value, args);
    private static void Set(object value, string name, object? next) => value.GetType().GetField(name, Members)!.SetValue(value, next);
    private static TerminalPane Pane(TabItem tab) => Property<TerminalPane>(tab.Tag, "Pane");
    private static void Assert(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    private static async Task Until(Func<bool> condition, string name, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(name);
    }

    public static async Task RunAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "independent-workspace");
        Directory.CreateDirectory(workspace);
        string settings = Path.Combine(fixture, "independent-settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { CodexWorkspace = workspace, WorkspaceDefaultsVersion = 1, SetupCompleted = true }));
        var window = new MainWindow(settings, autoStartCodex: false);
        try
        {
            window.Show();
            await Task.Delay(800);
            var tabs = (TabControl)window.FindName("TerminalTabs");
            var runtimes = Field<IDictionary>(window, "_automationRuntimes");
            var slots = Field<TerminalSlotLayout>(window, "_terminalSlotLayout");
            var initialSlots = slots.Slots.ToArray();
            Console.WriteLine($"Startup tabs={tabs.Items.Count}, runtimes={runtimes.Count}, selected={Field<string?>(window, "_selectedProjectPath")}, workspace={Field<string>(window, "_workspace")}");
            Assert(tabs.Items.Count == 0 && runtimes.Count == 0 && Field<string?>(window, "_selectedProjectPath") is null,
                "empty startup has no selected task or task runtime");

            async Task<TabItem> Add()
            {
                int count = tabs.Items.Count;
                ((Button)window.FindName("NewTerminalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => tabs.Items.Count == count + 1 && tabs.SelectedItem is TabItem tab && Pane(tab).IsReady && Pane(tab).IsRunning,
                    "independent terminal starts", 60);
                var result = (TabItem)tabs.SelectedItem;
                Assert(Property<bool>(result.Tag, "IsIndependent") && Property<string>(result.Tag, "PlanDocumentPath") == string.Empty &&
                    Field<object?>(Pane(result), "_openCodeProjectContext") is null && Field<object?>(Pane(result), "_autoResume") is null,
                    "new terminal has no plan binding, injected project context, or automatic task progress");
                return result;
            }
            var first = await Add();
            window.UpdateLayout();
            Assert(runtimes.Count == 0 && Field<IDictionary>(window, "_projectTaskRows").Count == 0 &&
                Field<IList>(window, "_documentBindings").Count == 0 && Field<string?>(window, "_selectedProjectPath") is null &&
                initialSlots.SequenceEqual(slots.Slots) && !Directory.EnumerateFileSystemEntries(workspace).Any(),
                "+ terminal in empty workspace does not create a task, plan, project files, or task-slot assignment");
            Assert(Pane(first).IsVisible && Pane(first).ActualWidth > 400 && Pane(first).ActualHeight > 100,
                "independent-only terminal receives the available terminal area");

            string project = Path.Combine(workspace, "existing-task");
            Directory.CreateDirectory(project);
            Set(window, "_selectedProjectPath", project);
            Call(window, "AddTerminal");
            await Until(() => tabs.Items.Count == 2 && tabs.SelectedItem is TabItem tab && !Property<bool>(tab.Tag, "IsIndependent") && Pane(tab).IsReady,
                "existing task starts alongside independent terminal");
            var owner = (TabItem)tabs.SelectedItem;
            var ownerPane = Pane(owner);
            object runtime = Call(window, "RuntimeFor", project)!;
            var document = Field<TextBox>(window, "_tabDocumentEditor");
            await Until(() => document.IsEnabled, "task plan editor loads");
            document.Text = "# Existing task\n\nKeep this unsaved plan draft.";
            string draft = document.Text;
            int taskCount = runtimes.Count;
            int bindings = Field<IList>(window, "_documentBindings").Count;
            long intent = Field<long>(runtime, "IntentVersion");
            var taskSlots = slots.Slots.ToArray();
            void AssertTaskUnchanged()
            {
                Assert(runtimes.Count == taskCount && ReferenceEquals(Field<object>(runtime, "SingleTerminal"), owner) &&
                    Field<long>(runtime, "IntentVersion") == intent && ownerPane.IsRunning &&
                    Field<string?>(window, "_selectedProjectPath") == project &&
                    ReferenceEquals(Field<object?>(window, "_composerState"), owner.Tag) && document.Text == draft &&
                    Field<IList>(window, "_documentBindings").Count == bindings && taskSlots.SequenceEqual(slots.Slots),
                    "task owner, process, intent, selection, plan draft, bindings, and slots remain unchanged");
            }
            Call(window, "SelectTerminal", first, false);
            AssertTaskUnchanged();
            var second = await Add();
            AssertTaskUnchanged();
            Assert(Property<string>(first.Tag, "IndependentSessionKey") != Property<string>(second.Tag, "IndependentSessionKey"),
                "independent terminals in the same directory have separate session keys");
            window.UpdateLayout();
            var panes = new[] { ownerPane, Pane(first), Pane(second) };
            var bounds = panes.Select(pane => pane.TransformToAncestor(window).TransformBounds(new Rect(0, 0, pane.ActualWidth, pane.ActualHeight))).ToArray();
            Assert(panes.All(pane => pane.IsVisible && pane.ActualWidth > 0 && pane.ActualHeight > 0) &&
                !bounds.Where((rect, index) => bounds.Skip(index + 1).Any(other => Rect.Intersect(rect, other) is var overlap && !overlap.IsEmpty && overlap.Width > 1 && overlap.Height > 1)).Any(),
                "task and independent terminals remain visible in separate areas");
            Call(window, "BeginTerminalTaskNameEdit", second);
            var editor = Field<TextBox>(window, "_titleEditor");
            editor.Text = "My independent shell";
            Call(window, "EndTitleEdit", true);
            Assert(Property<string>(second.Tag, "Title") == "My independent shell" && (string)Call(window, "ProjectDisplayName", project)! == "existing-task",
                "editing independent title does not rename its task or project");

            var writes = Field<HashSet<string>>(window, "_projectTaskControlWrites");
            writes.Add(workspace);
            try
            {
                Call(window, "UpdateTerminalLifecycleButtons");
                Assert(((Button)window.FindName("StopTerminalProcessButton")).IsEnabled,
                    "independent lifecycle is available while task control is busy");
                await (Task)Call(window, "StopSelectedTerminalProcessAsync")!;
                Assert(!Pane(second).IsRunning, "independent terminal stops without stopping task");
                AssertTaskUnchanged();
                string key = Property<string>(second.Tag, "IndependentSessionKey");
                await (Task)Call(window, "RestartSelectedTerminalProcessAsync")!;
                await Until(() => tabs.SelectedItem is TabItem tab && !ReferenceEquals(tab, second) && Pane(tab).IsReady, "independent terminal restarts");
                second = (TabItem)tabs.SelectedItem;
                Assert(Property<string>(second.Tag, "IndependentSessionKey") == key && Property<string>(second.Tag, "Title") == "My independent shell",
                    "stop/start retains independent identity and title");
                AssertTaskUnchanged();
            }
            finally { writes.Remove(workspace); }
            await (Task)Call(window, "StopSelectedTerminalProcessAsync")!;
            Call(window, "CloseTerminal", second, Pane(second));
            Call(window, "SelectTerminal", first, false);
            AssertTaskUnchanged();

            // Launch the actual OpenCode executable with native WPF rendering, without a model request.
            Set(window, "_autoStartCodex", true);
            Set(window, "_openCodeReady", true);
            var openCode = await Add();
            await Until(() => Property<bool>(Pane(openCode), "IsOpenCodeTuiAttached") && !string.IsNullOrWhiteSpace(Property<string?>(Pane(openCode), "OpenCodeTuiSessionId")),
                "real OpenCode attaches without a plan", 60);
            string session = Property<string>(Pane(openCode), "OpenCodeTuiSessionId");
            using (var client = new OpenCodeConversationClient(Property<Uri>(Pane(openCode), "NativeOpenCodeEndpoint"), workspace))
                Assert((await client.ReadMessagesAsync(session)).GetArrayLength() == 0,
                    "independent OpenCode opens idle with no automatic hi or plan-required request");
            AssertTaskUnchanged();
            string openCodeKey = Property<string>(openCode.Tag, "IndependentSessionKey");
            await (Task)Call(window, "StopSelectedTerminalProcessAsync")!;
            Assert(!Pane(openCode).IsRunning, "actual independent OpenCode stops");
            await (Task)Call(window, "RestartSelectedTerminalProcessAsync")!;
            await Until(() => tabs.SelectedItem is TabItem tab && !ReferenceEquals(tab, openCode) && Pane(tab).IsReady &&
                Property<bool>(Pane(tab), "IsOpenCodeTuiAttached"), "actual independent OpenCode restarts", 60);
            openCode = (TabItem)tabs.SelectedItem;
            Assert(Property<string>(openCode.Tag, "IndependentSessionKey") == openCodeKey && Property<string>(Pane(openCode), "OpenCodeTuiSessionId") == session,
                "actual OpenCode stop/start restores only its own session");
            AssertTaskUnchanged();
            await ownerPane.SendTextAsync("Set-PSReadLineOption -HistorySaveStyle SaveNothing; Write-Output ('OWNER_' + 'STILL_ALIVE')", true);
            await Until(() => ((string)Call(Field<object>(ownerPane, "_nativeTerminalSurface"), "GetEntireBufferText")!).Contains("OWNER_STILL_ALIVE", StringComparison.Ordinal),
                "original task process remains interactive");
            Assert(true, "original task still accepts commands after independent OpenCode stop/start");

            // Task archive/handoff must not claim an independent terminal using the same folder.
            string idleProject = Path.Combine(workspace, "idle-independent-project");
            Directory.CreateDirectory(idleProject);
            await (Task)Call(window, "RefreshProjectTasksAsync")!;
            Assert((bool)Call(window, "CanMoveProject", idleProject)!, "idle project is initially eligible for archive");
            Set(window, "_autoStartCodex", false);
            Call(window, "AddIndependentTerminal", null, idleProject);
            await Until(() => tabs.SelectedItem is TabItem tab && Pane(tab).WorkingDirectory == idleProject && Pane(tab).IsReady,
                "independent pane opens inside an idle task directory");
            var archivePane = (TabItem)tabs.SelectedItem;
            await (Task)Call(window, "StopSelectedTerminalProcessAsync")!;
            int archiveTabCount = tabs.Items.Count;
            bool blockedBeforeArchive = Property<bool>(Pane(archivePane), "IsUserInputBlocked");
            Assert(!(bool)Call(window, "CanMoveProject", idleProject)!, "even a stopped independent pane protects its working folder from archive");
            await (Task)Call(window, "MoveProjectArchiveAsync", idleProject)!;
            await (Task)Call(window, "ContinueTerminalInCmdAsync", archivePane)!;
            Assert(tabs.Items.Count == archiveTabCount && tabs.Items.Contains(archivePane) && Directory.Exists(idleProject) &&
                !Property<bool>(Pane(archivePane), "WasContinuedExternally") && Property<bool>(Pane(archivePane), "IsUserInputBlocked") == blockedBeforeArchive,
                "task archive and handoff do not take ownership of or close an independent pane");
            Call(window, "CloseTerminal", archivePane, Pane(archivePane));
            Assert((bool)Call(window, "CanMoveProject", idleProject)!, "closing independent pane releases archive eligibility");
        }
        finally
        {
            Call(window, "DisposeResources");
            window.Close();
        }
    }
}
