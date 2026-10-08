using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task VerifyProjectRerunAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "rerun workspace");
        string project = Path.Combine(workspace, "quoted project '한글'");
        Directory.CreateDirectory(project);
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        const string cachedDraft = "# Loaded editor snapshot\nnonempty old contents";
        await File.WriteAllTextAsync(plan, cachedDraft, new UTF8Encoding(false));
        string settings = Path.Combine(fixture, "rerun-settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        var window = new MainWindow(settings, autoStartCodex: false, startupHooks: FakeStartupHooks);
        try
        {
            window.Show();
            Call(window, "EnsureProjectTaskRow", project);
            Call(window, "SelectTaskProject", project, true);
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            await UntilAsync(() => editor.IsEnabled, "rerun plan editor load");
            Require(editor.Text == cachedDraft, "The fixture did not load the original plan snapshot into the editor.");

            await File.WriteAllTextAsync(plan, " \r\n\t", new UTF8Encoding(false));
            await (Task)Call(window, "RerunProjectTaskAsync", project)!;
            var runtime = Call(window, "RuntimeFor", project)!;
            var readyTab = RuntimeField(runtime, "SingleTerminal") as TabItem
                ?? throw new InvalidOperationException("Empty-plan Rerun did not open an interactive terminal.");
            var readyPane = (TerminalPane)GetProperty(readyTab.Tag, "Pane")!;
            string trace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE") ?? "";
            Require(readyPane.IsReady && readyPane.IsRunning && editor.Text == cachedDraft &&
                FindStartupPromptReceipts(trace, project).Length == 0,
                "Empty-plan Rerun did not preserve the editor draft and open a ready TUI without submitting a prompt.");
            Require(Field<System.Collections.Generic.HashSet<string>>(window, "_rerunningProjectTasks").Count == 0,
                "Empty-plan Rerun left a rerun reservation behind.");

            File.Delete(plan);
            await (Task)Call(window, "RerunProjectTaskAsync", project)!;
            Require(ReferenceEquals(RuntimeField(runtime, "SingleTerminal"), readyTab) && readyPane.IsRunning &&
                !File.Exists(plan) && FindStartupPromptReceipts(trace, project).Length == 0,
                "Missing-plan Rerun did not reuse the ready pane without creating a plan or submitting a prompt.");

            string before = ((TextBlock)window.FindName("StatusLabel")!).Text;
            var reservations = (System.Collections.Generic.HashSet<string>)FieldValue(window, "_rerunningProjectTasks")!;
            reservations.Add(project);
            await (Task)Call(window, "RerunProjectTaskAsync", project)!;
            reservations.Remove(project);
            Require(((TextBlock)window.FindName("StatusLabel")!).Text == before &&
                ReferenceEquals(RuntimeField(runtime, "SingleTerminal"), readyTab),
                "A duplicate rerun request bypassed the per-project single-flight reservation.");
            Console.WriteLine("PASS TASK rerun guards: empty/missing plans preserve drafts, reuse one interactive pane without API prompts, and duplicate requests remain single-flight.");
        }
        finally { Call(window, "DisposeResources"); window.Close(); }
    }
}
