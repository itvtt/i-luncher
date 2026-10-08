using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task VerifySupervisionAsync(string fixture)
    {
        await VerifyProcessFailureRecoveryAsync(fixture);
        foreach (bool ignoreAbort in new[] { false, true })
        {
            string project = Path.Combine(fixture, ignoreAbort ? "process recovery" : "native abort recovery");
            Directory.CreateDirectory(project);
            string planPath = TerminalTabDocumentStore.GetProjectPlanPath(project);
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            string plan = ignoreAbort ? "# WF_SUPERVISION_IGNORE_ABORT\nRecover the hung implementation." : "# WF_SUPERVISION\nRecover the hung implementation.";
            await File.WriteAllTextAsync(planPath, plan);
            var window = new MainWindow(Path.Combine(project, "settings.json"), autoStartCodex: false, startupHooks: FakeStartupHooks, initialWorkspace: project);
            try
            {
                window.Show();
                await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null, "supervision scheduler startup");
                var tabs = (TabControl)window.FindName("TerminalTabs");
                await UntilAsync(() => tabs.Items.Count > 0 &&
                    ((TerminalPane)GetProperty(((TabItem)tabs.Items[0]).Tag, "Pane")!).IsReady, "unrelated terminal startup");
                var unrelatedPane = (TerminalPane)GetProperty(((TabItem)tabs.Items[0]).Tag, "Pane")!;
                var lazyTeam = new Dictionary<AutomationRole, TabItem>((Dictionary<AutomationRole, TabItem>)Call(window, "EnsureAutomationTeam", project)!);
                Require(lazyTeam.Values.Any(tab => !((TerminalPane)GetProperty(tab.Tag, "Pane")!).IsReady), "Lazy-pane fixture unexpectedly finished startup synchronously.");
                var reusedTeam = (Dictionary<AutomationRole, TabItem>)Call(window, "EnsureAutomationTeam", project)!;
                Require(lazyTeam.All(pair => ReferenceEquals(pair.Value, reusedTeam[pair.Key])),
                    "A role still waiting for Loaded/ConPTY was discarded on the next team readiness check.");
                Call(window, "RememberProjectName", project, "Supervision fixture");
                var run = await (Task<AutomationRun>)Call(window, "StartProjectAutomationAsync", project, plan, 1, planPath, null)!;
                var store = new AutomationStore(project);
                await UntilAsync(() => FieldValue(window, "_automationStage") is { } candidate &&
                    ((AutomationStepClaim)GetProperty(candidate, "Claim")!).Role == AutomationRole.Implementer &&
                    !Field<bool>(window, "_automationPolling"), "implementation reaches its stalled native request", 60);
                var timer = Field<DispatcherTimer>(window, "_automationTimer");
                timer.Stop();
                object stage = FieldValue(window, "_automationStage")!;
                var pane = (TerminalPane)GetProperty(stage, "Pane")!;
                var client = (OpenCodeAutomationClient)GetProperty(stage, "Client")!;
                var dispatch = (OpenCodeAutomationDispatch)GetProperty(stage, "Dispatch")!;
                var claim = (AutomationStepClaim)GetProperty(stage, "Claim")!;
                await (Task)Call(window, "PollAutomationAsync")!;
                Require(client.LastProgressAt is not null, "The scheduler did not observe exact native progress.");
                SetField(client, "<LastProgressAt>k__BackingField", DateTimeOffset.UtcNow.AddMinutes(-4));
                await (Task)Call(window, "PollAutomationAsync")!;
                Require(client.IsRecovering && ReferenceEquals(stage, FieldValue(window, "_automationStage")),
                    "Abort did not retain the original stage until shutdown confirmation.");
                Call(window, "UpdateAutomationProgressPanel");
                Require(Field<TextBlock>(window, "_automationProgressState").Text.Contains("중단 확인", StringComparison.Ordinal),
                    "The progress panel still presented a stalled recovery as healthy running work.");
                if (ignoreAbort)
                {
                    var endpoint = (Uri)GetProperty(pane, "NativeOpenCodeEndpoint")!;
                    using var probe = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false })
                    { Timeout = TimeSpan.FromSeconds(3) };
                    using JsonDocument identity = JsonDocument.Parse(await probe.GetStringAsync(
                        new Uri(endpoint, "session/" + dispatch.SessionId + "?directory=" + Uri.EscapeDataString(project))));
                    using var nativeProcess = System.Diagnostics.Process.GetProcessById(identity.RootElement.GetProperty("testProcessId").GetInt32());
                    SetField(client, "_firstAbortAt", DateTimeOffset.UtcNow.AddMinutes(-2));
                    Task recoverWithModal = window.Dispatcher.InvokeAsync(async () =>
                        await (Task)Call(window, "PollAutomationAsync")!, DispatcherPriority.Background).Task.Unwrap();
                    Call(window, "OpenCompactTerminal", lazyTeam[AutomationRole.Reviewer]);
                    await recoverWithModal;
                    Require(FieldValue(window, "_compactTerminal") is null && !Field<bool>(window, "_arrangingTerminals"),
                        "An open compact terminal prevented replacement of the stalled role.");
                    Require(!pane.IsRunning, "An ignored native abort left the owned role process alive.");
                    await UntilAsync(() => nativeProcess.HasExited, "old native role server actually exits with its ConPTY job", 15);
                }
                else
                {
                    await (Task)Call(window, "PollAutomationAsync")!;
                    await Task.Delay(1100);
                    await (Task)Call(window, "PollAutomationAsync")!;
                    Require(pane.IsRunning, "A successful native abort unnecessarily killed the visible role terminal.");
                }
                Require(unrelatedPane.IsRunning, "Recovery terminated an unrelated terminal process.");
                AutomationRun recovering = (await store.GetAsync(run.Id))!;
                Require(recovering.NextRole == AutomationRole.Reviewer && recovering.WorkflowPhase == AutomationWorkflowPhase.Verification &&
                        recovering.ActiveDispatchToken != claim.DispatchToken && recovering.PlanApproved,
                    "Interrupted writes were replayed or the approved plan was lost instead of routing through review.");
                timer.Start();
                await UntilAsync(async () => (await store.GetAsync(run.Id))?.State == AutomationState.Completed,
                    "review, commander delivery instruction, implementation, and independent final verification", 90);
                string runDirectory = Path.Combine(project, "terminal-notes", "runs", run.Id.ToString("N"));
                await UntilAsync(() => Directory.GetFiles(runDirectory, "completion.json", SearchOption.AllDirectories).Length == 8,
                    "all completion artifacts after recovery");
                var roles = DispatchRolesInSubmissionOrder(Directory.GetFiles(runDirectory, "completion.json", SearchOption.AllDirectories));
                Require(roles.SequenceEqual(new[]
                {
                    AutomationRole.Coordinator, AutomationRole.Reviewer, AutomationRole.Coordinator, AutomationRole.Implementer,
                    AutomationRole.Reviewer, AutomationRole.Coordinator, AutomationRole.Implementer, AutomationRole.Reviewer
                }), "Recovery failed to return control through review and commander before another implementation.");
                Require((await store.GetAsync(run.Id))!.FinalVerificationEvidence is { Length: > 0 }, "Recovery bypassed final verification.");
                Console.WriteLine("PASS WPF supervision " + (ignoreAbort ? "owned process restart" : "native abort") +
                    ": partial implementation → reviewer → commander → delivery → final reviewer.");
            }
            finally
            {
                Call(window, "DisposeResources");
                window.Close();
            }
        }
    }

    private static async Task VerifyProcessFailureRecoveryAsync(string fixture)
    {
        foreach (bool outage in new[] { true, false })
        {
            string project = Path.Combine(fixture, outage ? "api outage" : "process crash");
            Directory.CreateDirectory(project);
            string path = TerminalTabDocumentStore.GetProjectPlanPath(project);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string plan = "# WF_SUPERVISION\nResume partial work after native service failure.";
            await File.WriteAllTextAsync(path, plan);
            var window = new MainWindow(Path.Combine(project, "settings.json"), autoStartCodex: false,
                startupHooks: FakeStartupHooks, initialWorkspace: project);
            try
            {
                window.Show();
                await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null, "failure fixture scheduler startup");
                Call(window, "RememberProjectName", project, "Failure fixture");
                var run = await (Task<AutomationRun>)Call(window, "StartProjectAutomationAsync", project, plan, 1, path, null)!;
                await UntilAsync(() => FieldValue(window, "_automationStage") is { } candidate &&
                    ((AutomationStepClaim)GetProperty(candidate, "Claim")!).Role == AutomationRole.Implementer &&
                    !Field<bool>(window, "_automationPolling"), "owned implementation starts before service failure", 60);
                var timer = Field<DispatcherTimer>(window, "_automationTimer");
                timer.Stop();
                object stage = FieldValue(window, "_automationStage")!;
                var pane = (TerminalPane)GetProperty(stage, "Pane")!;
                var client = (OpenCodeAutomationClient)GetProperty(stage, "Client")!;
                var dispatch = (OpenCodeAutomationDispatch)GetProperty(stage, "Dispatch")!;
                await (Task)Call(window, "PollAutomationAsync")!;
                var endpoint = (Uri)GetProperty(pane, "NativeOpenCodeEndpoint")!;
                using var probe = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false });
                using JsonDocument identity = JsonDocument.Parse(await probe.GetStringAsync(
                    new Uri(endpoint, "session/" + dispatch.SessionId + "?directory=" + Uri.EscapeDataString(project))));
                using var process = System.Diagnostics.Process.GetProcessById(identity.RootElement.GetProperty("testProcessId").GetInt32());
                if (outage)
                {
                    await File.WriteAllTextAsync(Path.Combine(project, ".fixture-api-outage"), "503");
                    await (Task)Call(window, "PollAutomationAsync")!;
                    Require(client.LastReadWarning is not null && pane.IsRunning, "Transient outage killed the process immediately.");
                    SetField(client, "_transportFailureSince", DateTimeOffset.UtcNow.AddMinutes(-4));
                    SetField(client, "_transportFailures", 3);
                }
                else
                {
                    process.Kill(); // Exact PID supplied by this isolated fixture's endpoint.
                    await process.WaitForExitAsync();
                    await UntilAsync(() => !pane.IsRunning, "owned process crash reaches the pane", 15);
                }
                await (Task)Call(window, "PollAutomationAsync")!;
                await UntilAsync(() => !pane.IsRunning && process.HasExited, "old owned process exits before recovery", 15);
                File.Delete(Path.Combine(project, ".fixture-api-outage"));
                var store = new AutomationStore(project);
                var recovered = (await store.GetAsync(run.Id))!;
                Require(recovered.NextRole == AutomationRole.Reviewer && recovered.WorkflowPhase == AutomationWorkflowPhase.Verification,
                    "Failure recovery replayed partial implementation instead of inspecting changes.");
                timer.Start();
                await UntilAsync(async () => (await store.GetAsync(run.Id))?.State == AutomationState.Completed,
                    "automatic review and commander continue through final verification without user input", 90);
                Require(Team(Runtime(window, project)).Values.All(tab => ((TerminalPane)GetProperty(tab.Tag, "Pane")!).IsRunning),
                    "The failed role terminal was not replaced for subsequent work.");
                Console.WriteLine("PASS WPF " + (outage ? "persistent API outage" : "unexpected process crash") + " → owned restart → partial review → automatic completion");
            }
            finally { Call(window, "DisposeResources"); window.Close(); }
        }
    }
}
