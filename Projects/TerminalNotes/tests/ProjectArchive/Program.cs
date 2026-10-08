using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _fail;
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); Console.WriteLine("PROJECT ARCHIVE TESTS PASSED"); }
            catch (Exception ex) { _fail++; Console.WriteLine("FAIL " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run(); return _fail;
    }

    private static async Task RunAsync()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "TerminalNotes archive " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(workspace, "app-data"));
        string source = Path.Combine(workspace, "한글 project");
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        await File.WriteAllTextAsync(Path.Combine(source, ".git", "config"), "keep git config");
        await File.WriteAllBytesAsync(Path.Combine(source, "binary.dat"), [0, 1, 255, 10]);
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(source);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        await File.WriteAllTextAsync(plan, "# original plan\n");
        var store = new AutomationStore(source);
        var run = await store.StartApprovedPlanAsync("approved immutable snapshot");
        await store.PauseAsync(run.Id);
        byte[] originalLedger = await File.ReadAllBytesAsync(store.StorePath);
        string archive = await ProjectArchiveService.MoveAsync(workspace, source, "보관 프로젝트", false);
        Assert(!Directory.Exists(source) && Directory.Exists(archive), "archive physically moves the project under workspace/archive");
        bool staleRejected = false;
        try { await store.LoadAsync(); } catch (DirectoryNotFoundException) { staleRejected = true; }
        Assert(staleRejected && !Directory.Exists(source), "stale automation readers cannot recreate the former project directory");
        Assert((await File.ReadAllBytesAsync(Path.Combine(archive, "binary.dat"))).SequenceEqual(new byte[] { 0, 1, 255, 10 }) &&
            File.ReadAllText(Path.Combine(archive, ".git", "config")) == "keep git config", "binary files and git metadata survive the move");
        var movedStore = new AutomationStore(archive);
        var movedRun = (await movedStore.GetAsync(run.Id))!;
        Assert(movedRun.ProjectDirectory == archive && movedRun.Id == run.Id && movedRun.ApprovedPlanSnapshot == run.ApprovedPlanSnapshot && movedRun.State == AutomationState.Paused,
            "archived history keeps run identity, original plan and paused state with the new live path");
        byte[] archivedLedger = await File.ReadAllBytesAsync(movedStore.StorePath);
        Assert(originalLedger.SequenceEqual(archivedLedger), "moving and loading preserve the original ledger bytes");
        var resumed = await movedStore.ResumeAsync(run.Id, DateTimeOffset.UtcNow);
        var archivedClaim = (await movedStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddSeconds(1))).Single();
        Assert(archivedClaim.ProjectDirectory == archive && archivedClaim.RunId == run.Id, "development resumes and claims work directly inside the archive");
        await Reject(() => ProjectArchiveService.MoveAsync(workspace, archive, "test", false), "running archive cannot be restored");
        await movedStore.StopAsync(run.Id);
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "collision.txt"), "do not overwrite");
        await Reject(() => ProjectArchiveService.MoveAsync(workspace, archive, "test", false), "restore collision never merges or overwrites projects");
        Assert(File.ReadAllText(Path.Combine(source, "collision.txt")) == "do not overwrite" && Directory.Exists(archive), "both colliding folders remain intact");
        string occupied = Path.Combine(workspace, "occupied-project");
        Directory.Move(source, occupied);
        await File.WriteAllTextAsync(Path.Combine(archive, "continued.txt"), "developed while archived");
        string restoredPath = await ProjectArchiveService.MoveAsync(workspace, archive, "보관 프로젝트", false);
        Assert(restoredPath == source && !Directory.Exists(archive) && File.ReadAllText(Path.Combine(source, "continued.txt")) == "developed while archived",
            "restore returns the developed project to the archive folder's parent");
        Assert((await new AutomationStore(source).GetAsync(run.Id))!.ProjectDirectory == source, "restored execution history binds back to the workspace project");
        await Reject(() => ProjectArchiveService.MoveAsync(workspace, workspace, "test", true), "workspace root cannot be archived");

        string settings = Path.Combine(workspace, "settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        var window = new MainWindow(settings, autoStartCodex: false);
        try
        {
            window.Show();
            await Until(() => Field<System.Collections.IDictionary>(window, "_projectTaskRows").Contains(source), "task discovery");
            Field<System.Windows.Threading.DispatcherTimer>(window, "_automationTimer").Stop();
            Call(window, "SelectTaskProject", source, true);
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            await Until(() => editor.IsEnabled, "plan ready");
            editor.Text = "# draft saved by archive\n";
            await (Task)Call(window, "MoveProjectArchiveAsync", source)!;
            Assert(!Directory.Exists(source) && File.ReadAllText(TerminalTabDocumentStore.GetProjectPlanPath(archive)) == "# draft saved by archive\n",
                "UI archive saves the project draft before moving");
            Assert(Field<RadioButton>(window, "_archivedTasksTab").IsChecked == true && VisiblePaths(window).Contains(archive) && !VisiblePaths(window).Contains(occupied),
                "archive tab shows archived projects separately from normal tasks");
            Assert(!Field<System.Collections.IDictionary>(window, "_projectTaskRows").Contains(Path.Combine(workspace, "archive")), "archive container never appears as a task");
            Field<RadioButton>(window, "_normalTasksTab").IsChecked = true;
            Assert(VisiblePaths(window).Contains(occupied) && !VisiblePaths(window).Contains(archive), "main tab filters archived projects without deleting their rows");
            Field<RadioButton>(window, "_archivedTasksTab").IsChecked = true;
            Call(window, "OpenProjectTask", archive);
            var tabs = (TabControl)window.FindName("TerminalTabs");
            await Until(() => tabs.Items.Count == 1 && Pane((TabItem)tabs.Items[0]).IsReady, "archived single terminal ready");
            var pane = Pane((TabItem)tabs.Items[0]);
            Assert(pane.WorkingDirectory == archive && Directory.Exists(archive) && !Directory.Exists(source), "opening an archived task creates its terminal at the archived path without restoring it");
            await Until(() => editor.IsEnabled, "archived plan ready");
            editor.Text = "# edited inside archive\n";
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            Assert(File.ReadAllText(TerminalTabDocumentStore.GetProjectPlanPath(archive)) == editor.Text, "the archived task saves its plan at the archived path");
            Field<RadioButton>(window, "_normalTasksTab").IsChecked = true;
            Field<RadioButton>(window, "_archivedTasksTab").IsChecked = true;
            Assert(pane.IsRunning && ReferenceEquals(Pane((TabItem)tabs.Items[0]), pane), "switching main and archive tabs preserves the archived terminal process");
            Call(window, "ShowProjectTaskList");
            await Until(() => (bool)Call(window, "CanMoveProject", archive)!, "archived terminal settles");
            await (Task)Call(window, "MoveProjectArchiveAsync", archive)!;
            Assert(Directory.Exists(source) && !Directory.Exists(archive) && !pane.IsRunning && Field<RadioButton>(window, "_normalTasksTab").IsChecked == true,
                "UI restore retires only its idle old-path terminal and returns to the main tab");
            Call(window, "SelectTaskProject", source, true);
            await Until(() => editor.IsEnabled, "restored plan ready");
            editor.Text = "unsaved conflicting draft";
            await File.WriteAllTextAsync(plan, "external edit");
            await (Task)Call(window, "MoveProjectArchiveAsync", source)!;
            Assert(Directory.Exists(source) && !Directory.Exists(archive) && editor.Text == "unsaved conflicting draft" && File.ReadAllText(plan) == "external edit",
                "a plan conflict aborts archive and preserves both the draft and external edit");
        }
        finally { Call(window, "DisposeResources"); window.Close(); }
        var fresh = new MainWindow(settings, autoStartCodex: false);
        try
        {
            fresh.Show();
            await Until(() => Field<System.Collections.IDictionary>(fresh, "_projectTaskRows").Contains(source), "restored project discovery after restart");
            Assert((string)Call(fresh, "ProjectDisplayName", source)! == "보관 프로젝트" &&
                !Field<Dictionary<string, bool>>(fresh, "_projectTaskMultiModes")[source], "project display name and mode persist through archive, restore and restart");
        }
        finally { Call(fresh, "DisposeResources"); fresh.Close(); }
        Console.WriteLine("Fixture: " + workspace);
    }

    private static IEnumerable<string> VisiblePaths(MainWindow window) =>
        Field<TreeViewItem>(window, "_taskOrderGroup").Items.Cast<TreeViewItem>().Select(node => (string)node.Tag);
    private static TerminalPane Pane(TabItem tab) => (TerminalPane)tab.Tag.GetType().GetProperty("Pane")!.GetValue(tab.Tag)!;
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    private static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args);
    private static void Assert(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static async Task Until(Func<bool> condition, string name)
    {
        for (int i = 0; i < 250; i++) { if (condition()) return; await Task.Delay(100); }
        throw new TimeoutException(name);
    }
    private static async Task Reject(Func<Task<string>> action, string name)
    {
        bool failed = false;
        try { await action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { failed = true; }
        Assert(failed, name);
    }
}
