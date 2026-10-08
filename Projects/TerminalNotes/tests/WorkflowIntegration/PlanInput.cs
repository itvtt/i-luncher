using System.Collections;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task VerifySlowReadyGateAsync(string fixture)
    {
        string project = Path.Combine(fixture, "slow readiness gate project");
        Directory.CreateDirectory(project);
        var delayedPane = new TerminalPane(project);
        var cancellationPane = new TerminalPane(project);
        try
        {
            var delayedLoaded = (TaskCompletionSource<Exception?>)FieldValue(delayedPane, "_loadedCompletion")!;
            var delayedStarted = (TaskCompletionSource<Exception?>)FieldValue(delayedPane, "_terminalStartedCompletion")!;
            Require(!delayedPane.IsLoaded && !delayedStarted.Task.IsCompleted,
                "The delayed readiness fixture must stay hidden with terminal startup pending.");
            delayedLoaded.TrySetResult(null);
            using var startup = new CancellationTokenSource(TerminalExecutionTiming.StartupReadyTimeout);
            DateTimeOffset waitStarted = DateTimeOffset.UtcNow;
            Task delayedReady = (Task)Call(delayedPane, "EnsureReadyAsync", startup.Token)!;
            await Task.Delay(TimeSpan.FromSeconds(46));
            Require(!delayedReady.IsCompleted && !delayedStarted.Task.IsCompleted,
                "EnsureReadyAsync returned before its terminal-start signal after 46 seconds.");
            delayedStarted.TrySetResult(null);
            await delayedReady.WaitAsync(TimeSpan.FromSeconds(5));
            TimeSpan elapsed = DateTimeOffset.UtcNow - waitStarted;
            Require(elapsed >= TimeSpan.FromSeconds(45) && delayedReady.IsCompletedSuccessfully,
                "The production readiness gate did not remain pending beyond the retired 45-second limit.");

            var cancelLoaded = (TaskCompletionSource<Exception?>)FieldValue(cancellationPane, "_loadedCompletion")!;
            var cancelStarted = (TaskCompletionSource<Exception?>)FieldValue(cancellationPane, "_terminalStartedCompletion")!;
            cancelLoaded.TrySetResult(null);
            using var cancellation = new CancellationTokenSource();
            Task cancelledReady = (Task)Call(cancellationPane, "EnsureReadyAsync", cancellation.Token)!;
            await Task.Delay(50);
            var cancelTimer = System.Diagnostics.Stopwatch.StartNew();
            cancellation.Cancel();
            try
            {
                await cancelledReady;
                throw new InvalidOperationException("A cancelled pending terminal startup unexpectedly returned ready.");
            }
            catch (OperationCanceledException) { }
            cancelTimer.Stop();
            Require(cancelTimer.Elapsed <= TimeSpan.FromMilliseconds(500) && !cancelStarted.Task.IsCompleted,
                "Cancelling a pending readiness gate did not return promptly or altered the pending startup signal.");
            Console.WriteLine("PASS readiness gate: actual EnsureReadyAsync waits beyond 45 seconds for terminal-start completion and promptly observes cancellation; native delivery is not exercised.");
        }
        finally
        {
            delayedPane.Dispose();
            cancellationPane.Dispose();
        }
    }

    private static async Task VerifySlowStartupToleranceAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "slow startup workspace");
        string project = Path.Combine(workspace, "slow startup project");
        string settings = Path.Combine(fixture, "slow startup settings.json");
        string receipts = Path.Combine(fixture, "slow startup receipts");
        string nativeStarted = Path.Combine(fixture, "slow startup native processes");
        string slowMarker = Path.Combine(project, ".fixture-slow-startup");
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        await File.WriteAllTextAsync(plan, "# Slow startup test\nWait for the fake native session to become idle.", new UTF8Encoding(false));
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        File.WriteAllText(slowMarker, "Keep the fake native session busy until the test releases it.");
        string? previousReceipts = Environment.GetEnvironmentVariable("TN_WORKFLOW_TUI_INPUT_TRACE");
        string? previousStarted = Environment.GetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_TRACE");
        object? previousDataDirectory = AppContext.GetData("TerminalNotes.DataDirectory");
        string isolatedData = Path.Combine(fixture, "slow startup app data");
        Directory.CreateDirectory(Path.Combine(isolatedData, "WebView2"));
        Environment.SetEnvironmentVariable("TN_WORKFLOW_TUI_INPUT_TRACE", receipts);
        Environment.SetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_TRACE", nativeStarted);
        AppContext.SetData("TerminalNotes.DataDirectory", isolatedData);
        MainWindow? window = null;
        try
        {
            window = new MainWindow(settings, autoStartCodex: true, startupHooks: FakeStartupHooks);
            window.Show();
            await UntilAsync(() => (bool)FieldValue(window, "_openCodeReady")!, "slow-startup fake OpenCode discovery");
            Call(window, "EnsureProjectTaskRow", project);
            Call(window, "SelectTaskProject", project, true);

            Task cancelledStart = (Task)Call(window, "StartProjectTaskAsync", project)!;
            object runtime = Runtime(window, project);
            await UntilAsync(() => RuntimeField(runtime, "StartingSingle") is true && RuntimeField(runtime, "SingleTerminal") is not null,
                "single task to remain owned during fake native loading");
            var tab = (TabItem)RuntimeField(runtime, "SingleTerminal")!;
            var pane = (TerminalPane)GetProperty(tab.Tag, "Pane")!;
            await UntilAsync(() => pane.IsReady && ((OpenCodeStatusMonitor)FieldValue(pane, "_openCodeStatus")!).Snapshot.Kind == OpenCodeStatusKind.Busy &&
                Directory.Exists(nativeStarted) && Directory.GetFiles(nativeStarted, "*.started").Length > 0,
                "fake native session to report loading");
            await (Task)Call(window, "StopProjectTaskAsync", project)!;
            await cancelledStart.WaitAsync(TimeSpan.FromSeconds(10));
            Require(RuntimeField(runtime, "SingleStopped") is true && RuntimeField(runtime, "StartingSingle") is false &&
                    RuntimeField(runtime, "SingleError") is null && RuntimeField(runtime, "SingleStartupCancellation") is null &&
                    (!Directory.Exists(receipts) || Directory.GetFiles(receipts, "*.json").Length == 0),
                "Stopping during slow native readiness must cancel promptly without sending or leaving a startup error.");

            File.Delete(slowMarker);
            pane.ClearActivityOverride();
            await UntilAsync(() => pane.CanExecuteSavedPlan, "cancelled fixture pane to return to idle");
            File.WriteAllText(slowMarker, "Delay native idle beyond the retired 45-second readiness limit.");
            Task delayedStart = (Task)Call(window, "StartProjectTaskAsync", project)!;
            await UntilAsync(() => RuntimeField(runtime, "StartingSingle") is true &&
                ((OpenCodeStatusMonitor)FieldValue(pane, "_openCodeStatus")!).Snapshot.Kind == OpenCodeStatusKind.Busy, "retry to wait on fake native loading");
            await Task.Delay(TimeSpan.FromSeconds(46));
            Require(!delayedStart.IsCompleted && RuntimeField(runtime, "SingleError") is null &&
                    RuntimeField(runtime, "StartingSingle") is true &&
                    RuntimeField(runtime, "SingleStartPhase")?.ToString() is string phase && phase.StartsWith("+ ", StringComparison.Ordinal) && phase.Contains('\uCD08') &&
                    (!Directory.Exists(receipts) || Directory.GetFiles(receipts, "*.json").Length == 0),
                "A slow native session must remain pending beyond 45 seconds, show elapsed loading status, and send nothing early.");

            File.Delete(slowMarker);
            await delayedStart.WaitAsync(TimeSpan.FromSeconds(60));
            await UntilAsync(() => Directory.Exists(receipts) && Directory.GetFiles(receipts, "*.json").Length == 1,
                "delayed readiness to release and deliver one native request", seconds: 15);
            Require(RuntimeField(runtime, "SingleSubmitted") is true && RuntimeField(runtime, "SingleError") is null,
                "The delayed native session did not complete the Start request after becoming idle.");
            Console.WriteLine("PASS slow startup: Stop cancels a loading wait, then a real fake-native Start remains pending past 45 seconds and dispatches once when ready.");
        }
        finally
        {
            try { if (File.Exists(slowMarker)) File.Delete(slowMarker); } catch (IOException) { }
            if (window is not null) { Call(window, "DisposeResources"); window.Close(); }
            Environment.SetEnvironmentVariable("TN_WORKFLOW_TUI_INPUT_TRACE", previousReceipts);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_TRACE", previousStarted);
            AppContext.SetData("TerminalNotes.DataDirectory", previousDataDirectory);
        }
    }
    private static async Task VerifyRawPlanInputRetryAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "raw plan input workspace");
        string project = Path.Combine(workspace, "project with spaces");
        string settings = Path.Combine(fixture, "raw plan input settings.json");
        string startupPromptTrace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")
            ?? throw new InvalidOperationException("The workflow fixture must provide its fake API trace directory.");
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(project);
            string planPath = TerminalTabDocumentStore.GetProjectPlanPath(project);
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            const string history = "# Existing plan\n\nPreserve this history.";
            await File.WriteAllTextAsync(planPath, history, new UTF8Encoding(false));
            await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
            { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
            window = new MainWindow(settings, autoStartCodex: true, startupHooks: FakeStartupHooks);
            window.Show();
            await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null &&
                (bool)FieldValue(window, "_openCodeReady")!, "raw-input fixture OpenCode readiness");
            Call(window, "EnsureProjectTaskRow", project);
            var row = ((IDictionary)FieldValue(window, "_projectTaskRows")!)[project]!;
            ((RadioButton)GetProperty(row, "Single")!).IsChecked = true;
            var tab = (TabItem)Call(window, "EnsureSingleProjectTerminal", project)!;
            var tabs = (TabControl)window.FindName("TerminalTabs");
            tabs.SelectedItem = tab;
            Call(window, "SelectTaskProject", project, true);
            Call(window, "UpdateProjectInputComposer");
            var pane = (TerminalPane)GetProperty(tab.Tag, "Pane")!;
            await UntilAsync(() => pane.IsReady && pane.CanExecuteSavedPlan, "raw-input native pane readiness");
            var editor = Field<TextBox>(window, "_projectInputEditor");

            const string firstRequest = "Keep this raw request exactly once.";
            editor.Text = firstRequest;
            pane.MarkNeedsInput();
            await (Task)Call(window, "SubmitProjectInputAsync")!;
            string firstSaved = await File.ReadAllTextAsync(planPath);
            Require(firstSaved.StartsWith(history, StringComparison.Ordinal) &&
                firstSaved[(history.Length)..].Trim() == firstRequest &&
                !firstSaved.Contains("## ", StringComparison.Ordinal) &&
                !firstSaved.Contains("terminal-notes:plan-input:", StringComparison.Ordinal),
                "Plan input did not append only raw text after existing history.");
            Require(editor.Text == firstRequest, "A blocked native send discarded the unsent composer draft.");
            Require(!FindStartupPromptReceipts(startupPromptTrace, project).Any(receipt =>
                    receipt.Prompt.Contains("Keep this raw request exactly once.", StringComparison.Ordinal)),
                "A pane with manual input override received a plan request.");

            pane.ClearActivityOverride();
            await (Task)Call(window, "SubmitProjectInputAsync")!;
            string expectedRawPrompt = $"Read the current project plan at \"{Path.GetFullPath(planPath)}\" and apply the following request. Check each requirement against the current code, changes, and validation evidence before editing. Report already satisfied items with evidence instead of repeating the implementation. Do not rewrite code only because a plan or todo item is marked incomplete; make and validate one minimal change only when you find a confirmed gap or defect. If all requirements are met, reuse valid existing verification evidence and finish without changes.\n\n{firstRequest}";
            await UntilAsync(() => FindStartupPromptReceipts(startupPromptTrace, project)
                    .Any(receipt => receipt.Prompt == expectedRawPrompt),
                "retry delivers one raw-input request to native OpenCode");
            var delivered = FindStartupPromptReceipts(startupPromptTrace, project)
                .Where(receipt => receipt.Prompt == expectedRawPrompt).ToArray();
            Require(delivered.Length == 1 && !string.IsNullOrWhiteSpace(delivered[0].SessionId) &&
                    !string.IsNullOrWhiteSpace(delivered[0].MessageId),
                "The native retry did not deliver exactly one project-plan request with session/message identity.");
            string afterRetry = await File.ReadAllTextAsync(planPath);
            Require(afterRetry == firstSaved && editor.Text.Length == 0,
                "Retry duplicated the raw append or failed to clear a delivered draft.");

            const string changedRequest = "Do not send after the plan changes.";
            editor.Text = changedRequest;
            pane.MarkNeedsInput();
            await (Task)Call(window, "SubmitProjectInputAsync")!;
            string beforeExternalChange = await File.ReadAllTextAsync(planPath);
            Require(beforeExternalChange.Contains(changedRequest, StringComparison.Ordinal) && editor.Text == changedRequest,
                "The second blocked native send did not preserve its saved request and draft.");
            await File.AppendAllTextAsync(planPath, "\nExternal plan edit.\n", new UTF8Encoding(false));
            pane.ClearActivityOverride();
            await (Task)Call(window, "SubmitProjectInputAsync")!;
            var rawReceiptsAfterEdit = FindStartupPromptReceipts(startupPromptTrace, project)
                .Where(receipt => receipt.Prompt.Contains("Keep this raw request exactly once.", StringComparison.Ordinal) ||
                                  receipt.Prompt.Contains(changedRequest, StringComparison.Ordinal)).ToArray();
            Require(rawReceiptsAfterEdit.Length == 1 && rawReceiptsAfterEdit[0].Prompt == expectedRawPrompt && editor.Text == changedRequest &&
                (await File.ReadAllTextAsync(planPath)).EndsWith("External plan edit.\n", StringComparison.Ordinal),
                "Retry sent against an externally changed plan or discarded the draft.");
            Console.WriteLine("PASS raw plan input: raw-only append, blocked-send retry idempotency, and external-edit dispatch guard.");
        }
        finally
        {
            if (window is not null) { Call(window, "DisposeResources"); window.Close(); }
        }
    }
}
