using System.Reflection;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static int _failure;
    private static bool _manualOnly;
    private static bool _supervisionOnly;
    private static bool _projectsOnly;
    private static bool _singleOnly;
    private static bool _rerunOnly;
    private static bool _initialPlanOnly;
    private static bool _emptyPlanNativeOnly;
    private static bool _slowStartupOnly;
    private static bool _slowReadyGateOnly;
    private static readonly OpenCodeStartupHooks FakeStartupHooks = new(
        _ => Task.FromResult(true), () => throw new InvalidOperationException("Only the isolated fake OpenCode is allowed."),
        _ => throw new InvalidOperationException("The integration fixture must never invoke a real installer or fallback discovery."));

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fake-opencode") return RunFakeOpenCode(args[1..]);
        // The fake API fixture drives the retained conversation-view protocol.
        Environment.SetEnvironmentVariable("TERMINALNOTES_OPENCODE_RENDERER", "headless");
        _manualOnly = args.Contains("--manual-only", StringComparer.Ordinal);
        _supervisionOnly = args.Contains("--supervision-only", StringComparer.Ordinal);
        _projectsOnly = args.Contains("--projects-only", StringComparer.Ordinal);
        _singleOnly = args.Contains("--single-only", StringComparer.Ordinal);
        _rerunOnly = args.Contains("--rerun-only", StringComparer.Ordinal);
        _initialPlanOnly = args.Contains("--initial-plan-only", StringComparer.Ordinal);
        _emptyPlanNativeOnly = args.Contains("--empty-plan-native-only", StringComparer.Ordinal);
        _slowStartupOnly = args.Contains("--slow-startup-only", StringComparer.Ordinal);
        _slowReadyGateOnly = args.Contains("--slow-ready-gate-only", StringComparer.Ordinal);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => await RunAsync(app);
        app.Run();
        return _failure;
    }

    private static async Task RunAsync(Application app)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Workflow 한글 'quote' " + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(fixture, "project path 'quoted'");
        string fakeBin = Path.Combine(fixture, "fake bin");
        string trace = Path.Combine(fixture, "fake trace");
        string settings = Path.Combine(fixture, "settings", "settings.json");
        string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        string? originalHoldFile = Environment.GetEnvironmentVariable("TN_WORKFLOW_HOLD_FILE");
        string? originalHeldSignal = Environment.GetEnvironmentVariable("TN_WORKFLOW_HELD_SIGNAL");
        string? originalDeliveryProof = Environment.GetEnvironmentVariable("TN_WORKFLOW_DELIVERY_PROOF");
        string? originalArtifactBlock = Environment.GetEnvironmentVariable("TN_WORKFLOW_BLOCK_NATIVE_ARTIFACT_ONCE");
        string originalDirectory = Environment.CurrentDirectory;
        string holdFile = Path.Combine(fixture, "hold-first-coordinator");
        string heldSignal = Path.Combine(fixture, "first-coordinator-is-busy.signal");
        string deliveryProof = Path.Combine(fixture, "fake-native-delivery-proof.txt");
        string artifactBlockSignal = Path.Combine(fixture, "native-artifact-blocked.path");
        MainWindow? window = null;
        bool succeeded = false;
        try
        {
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(fakeBin);
            Directory.CreateDirectory(trace);
            await File.WriteAllTextAsync(holdFile, "hold first Coordinator response");
            File.WriteAllText(Path.Combine(project, "AGENTS.md"), "# 통합 테스트\n", new UTF8Encoding(false));
            WriteFakeOpenCode(fakeBin);
            Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + originalPath);
            string expectedFakeCommand = Path.GetFullPath(Path.Combine(fakeBin, "opencode.cmd"));
            AutoInstallDetectionResult resolvedOpenCode = await new OpenCodeInstallationService().CheckAsync();
            if (!resolvedOpenCode.IsInstalled || string.IsNullOrWhiteSpace(resolvedOpenCode.Path) ||
                !string.Equals(Path.GetFullPath(resolvedOpenCode.Path), expectedFakeCommand, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Workflow fixture did not resolve its fake OpenCode command; no test window or task prompt was started. " +
                    "Expected=" + expectedFakeCommand + "; resolved=" + (resolvedOpenCode.Path ?? "(none)"));
            }
            Environment.SetEnvironmentVariable("TN_WORKFLOW_TRACE", trace);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_HOLD_FILE", holdFile);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_HELD_SIGNAL", heldSignal);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_DELIVERY_PROOF", deliveryProof);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_BLOCK_NATIVE_ARTIFACT_ONCE", artifactBlockSignal);
            Environment.CurrentDirectory = project;

            if (_singleOnly)
            {
                await VerifySingleProjectTaskAsync(fixture);
                await VerifyRawPlanInputRetryAsync(fixture);
                await VerifyProjectRerunAsync(fixture);
                succeeded = true;
                return;
            }
            if (_initialPlanOnly)
            {
                await VerifyInitialStartPlanRefreshAsync(fixture);
                await VerifyEmptyPlanStartCapacityGuardAsync(fixture);
                succeeded = true;
                return;
            }
            if (_slowReadyGateOnly)
            {
                await VerifySlowReadyGateAsync(fixture);
                succeeded = true;
                return;
            }
            if (_slowStartupOnly)
            {
                await VerifySlowStartupToleranceAsync(fixture);
                succeeded = true;
                return;
            }
            if (_emptyPlanNativeOnly)
            {
                await VerifyEmptyPlanStartAsync(fixture);
                succeeded = true;
                return;
            }
            if (_rerunOnly)
            {
                await VerifyProjectRerunAsync(fixture);
                succeeded = true;
                return;
            }
            if (_projectsOnly)
            {
                Environment.SetEnvironmentVariable("TN_WORKFLOW_BLOCK_NATIVE_ARTIFACT_ONCE", null);
                await VerifyProjectTasksAsync(fixture, holdFile);
                Environment.SetEnvironmentVariable("TN_WORKFLOW_HOLD_FILE", null);
                await VerifyContinuousTaskAsync(fixture);
                await VerifyMicroManagementAsync(fixture);
                succeeded = true;
                return;
            }
            if (_supervisionOnly)
            {
                Environment.SetEnvironmentVariable("TN_WORKFLOW_HOLD_FILE", null);
                Environment.SetEnvironmentVariable("TN_WORKFLOW_BLOCK_NATIVE_ARTIFACT_ONCE", null);
                await VerifySupervisionAsync(fixture);
                succeeded = true;
                return;
            }

            await VerifyDefaultOpenCodeLaunchAsync(project, fixture);
            await VerifyDelayedStartupStopAndOrphanRecoveryAsync(fixture);

            window = new MainWindow(settings, autoStartCodex: false, startupHooks: FakeStartupHooks, initialWorkspace: project);
            window.Show();
            await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null, "automation scheduler initialization");

            var projects = Field<HashSet<string>>(window, "_automationProjects");
            projects.Add(project);
            var tabs = (TabControl)window.FindName("TerminalTabs");
            TabItem popupTargetTab = (TabItem)tabs.SelectedItem;
            object popupTargetState = popupTargetTab.Tag;
            int popupTargetSlot = (int)GetProperty(popupTargetState, "Sequence")!;
            string planPath = TerminalTabDocumentStore.GetProjectPlanPath(project);
            string terminalPlanPath = (string)GetProperty(popupTargetState, "PlanDocumentPath")!;
            Require(string.Equals(terminalPlanPath, planPath, StringComparison.OrdinalIgnoreCase),
                "The selected project terminal uses the canonical terminal-notes/plan.md path.");
            var tabDocumentStore = new TerminalTabDocumentStore(project);
            const string initialPlanText = "# Initial cached plan\n\nPlan shared by the workbench and automation window.";
            var legacyDocumentStore = new TerminalTabDocumentStore(project);
            string otherSlotPlanPath = Path.Combine(project, "terminal-notes", "tabs", "terminal-01", "plan.md");
            const string otherSlotPlanText = "legacy slot Plan remains unchanged";
            Directory.CreateDirectory(Path.GetDirectoryName(otherSlotPlanPath)!);
            await File.WriteAllTextAsync(otherSlotPlanPath, otherSlotPlanText, new UTF8Encoding(false));
            var legacyDocuments = new Dictionary<TerminalInputMode, string>
            {
                [TerminalInputMode.Build] = "legacy build stays intact",
                [TerminalInputMode.Review] = "legacy review stays intact",
                [TerminalInputMode.Command] = "legacy command stays intact"
            };
            foreach (var (mode, text) in legacyDocuments)
            {
                string legacyPath = legacyDocumentStore.GetDocumentPath(popupTargetSlot, mode);
                Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
                await File.WriteAllTextAsync(legacyPath, text, new UTF8Encoding(false));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            await File.WriteAllTextAsync(planPath, initialPlanText, new UTF8Encoding(false));
            tabs.SelectedItem = popupTargetTab;
            Call(window, "UpdateWorkbenchTarget");
            await (Task)Call(window, "ReloadTabDocumentAsync")!;
            var initialCoordinatorPane = (TerminalPane)GetProperty(popupTargetState, "Pane")!;
            await UntilAsync(() => initialCoordinatorPane.IsReady, "initial project terminal shell readiness");
            var documentEditor = Field<TextBox>(window, "_tabDocumentEditor");
            await UntilAsync(() => documentEditor.IsEnabled && documentEditor.Text == initialPlanText,
                "selected coordinator Plan cache loaded");
            var initialBrowser = Field<WebView2>(initialCoordinatorPane, "_browser");
            async Task<string> ManualSubmitAsync(string text)
            {
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Submitted(string submitted) => completion.TrySetResult(submitted);
                initialCoordinatorPane.InputSubmitted += Submitted;
                try
                {
                    await initialBrowser.ExecuteScriptAsync("terminal.input(" + JsonSerializer.Serialize(text + "\r") + ")");
                    return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { initialCoordinatorPane.InputSubmitted -= Submitted; }
            }
            Require(await ManualSubmitAsync("Write-Output 'MANUAL_BEFORE_한글'") == "Write-Output 'MANUAL_BEFORE_한글'",
                "Manual capture failed before programmatic input.");
            await initialBrowser.ExecuteScriptAsync("send({type:'input',data:'\\u001b[?999h'})");
            await initialCoordinatorPane.SendTextAsync("Write-Output 'PROGRAMMATIC_BOUNDARY'");
            Require(await ManualSubmitAsync("Write-Output 'MANUAL_AFTER_한글'") == "Write-Output 'MANUAL_AFTER_한글'",
                "Manual capture remained poisoned after programmatic input.");
            if (_manualOnly)
            {
                Console.WriteLine("Manual capture UI smoke passed before and after programmatic submission.");
                succeeded = true;
                return;
            }

            var store = new AutomationStore(project);
            AutomationRun tokenLimitedPausedRun = await SeedLegacyTokenLimitedPausedRunAsync(store);
            Require(tokenLimitedPausedRun.State == AutomationState.Paused &&
                    tokenLimitedPausedRun.TotalTokenUsage == 1 &&
                    tokenLimitedPausedRun.Options.TokenBudget == 1 &&
                    tokenLimitedPausedRun.StatusDetail == "Token budget reached.",
                "The isolated project loads a serialized legacy token-limited Paused history run without model dispatch.");
            Call(window, "ShowAutomationWindow");
            var automationWindow = Field<Window>(window, "_automationWindow");
            await UntilAsync(() => automationWindow.IsLoaded && automationWindow.ActualWidth > 0, "automation window rendering");
            var automationInputs = Descendants(automationWindow).OfType<TextBox>().ToArray();
            Require(automationInputs.Length == 2 &&
                    automationInputs.Any(input => input.Name == "ProjectAutomationNameInput") &&
                    automationInputs.Any(input => input.Name == "AutomationProjectContent"),
                "The automation window must expose a project-name field and the selected Plan editor.");
            Require(!Descendants(automationWindow).Any(element => element is CheckBox or DatePicker),
                "The automation window exposed model, limit, schedule, approval, or team setup controls.");
            TextBox projectNameInput = automationInputs.Single(input => input.Name == "ProjectAutomationNameInput");
            TextBox automationContent = automationInputs.Single(input => input.Name == "AutomationProjectContent");
            Require(projectNameInput.Text == Path.GetFileName(Path.TrimEndingDirectorySeparator(project)),
                "An unnamed project prefills the name field from its folder name.");
            await UntilAsync(() => automationContent.IsEnabled && automationContent.Text == initialPlanText,
                "automation window loads the same canonical project Plan draft");
            Require(string.Equals(automationContent.ToolTip as string, planPath, StringComparison.OrdinalIgnoreCase),
                "The automation popup is bound to the canonical project Plan path.");
            Button automationStart = Descendants(automationWindow).OfType<Button>()
                .Single(button => button.Name == "AutomationStartButton");

            const string acceptedProjectName = "Workflow named project";
            projectNameInput.Text = "";
            automationContent.Text = "this content must not save without a project name";
            Require(!automationStart.IsEnabled, "A blank project name must disable automation before Plan saving.");
            automationStart.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(100);
            Require((await store.LoadAsync()).Count == 1 && await File.ReadAllTextAsync(planPath) == initialPlanText &&
                    !(bool)Call(window, "HasProjectName", project)!,
                "A blank project name cannot save the Plan, create a run, or persist a group name.");

            projectNameInput.Text = acceptedProjectName;
            automationContent.Text = " \t \r\n";
            Require(!automationStart.IsEnabled, "Whitespace-only project content incorrectly enabled Run.");
            const string approvedProjectContent = "# Popup saved plan\n\n프로젝트 승인 본문 'quotes' ; & | $() ✅";
            automationContent.Text = approvedProjectContent;
            Require(automationStart.IsEnabled, "Run did not enable after project content was entered.");
            automationStart.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await UntilAsync(() => FieldValue(window, "_startingProjectAutomation") is true,
                "project automation startup latch");
            var automationTimer = Field<System.Windows.Threading.DispatcherTimer>(window, "_automationTimer");
            var timerPriority = (System.Windows.Threading.DispatcherPriority)typeof(System.Windows.Threading.DispatcherTimer)
                .GetField("_priority", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(automationTimer)!;
            Require(timerPriority >= System.Windows.Threading.DispatcherPriority.Loaded,
                "OpenCode scheduling must run even while Win32 input prevents background processing.");
            Require(!automationStart.IsEnabled && automationContent.IsReadOnly,
                "Run did not disable repeated submission while startup was pending.");
            automationStart.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await UntilAsync(async () => (await store.LoadAsync()).Count == 2 &&
                    (await store.GetAsync(tokenLimitedPausedRun.Id))?.State == AutomationState.Stopped,
                "single automation run creation");
            Require((bool)Call(window, "HasProjectName", project)! &&
                    (string)Call(window, "ProjectDisplayName", project)! == acceptedProjectName,
                "The accepted project name is remembered under the canonical project path.");
            var team = Field<Dictionary<AutomationRole, TabItem>>(window, "_automationTeam");
            Require(team.Count == 3, "The named project did not create its three-role pane group.");
            TabItem initialCoordinatorTab = team[AutomationRole.Coordinator];
            object initialCoordinatorState = initialCoordinatorTab.Tag;
            int coordinatorSlot = (int)GetProperty(initialCoordinatorState, "Sequence")!;
            foreach (var (mode, text) in legacyDocuments)
            {
                string legacyPath = legacyDocumentStore.GetDocumentPath(coordinatorSlot, mode);
                Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
                await File.WriteAllTextAsync(legacyPath, text, new UTF8Encoding(false));
            }
            foreach (var (role, roleTab) in team)
            {
                Require(string.Equals((string)GetProperty(roleTab.Tag, "PlanDocumentPath")!, planPath, StringComparison.OrdinalIgnoreCase),
                    $"All role panes share the named project's Plan path: {role}.");
                Require((string)GetProperty(roleTab.Tag, "Title")! != acceptedProjectName,
                    $"The project group name does not replace the role's own title: {role}.");
            }
            var afterFirstApprovalRuns = await store.LoadAsync();
            AutomationRun draft = afterFirstApprovalRuns.Single(run => run.Id != tokenLimitedPausedRun.Id);
            AutomationRun stoppedTokenLimitedRun = afterFirstApprovalRuns.Single(run => run.Id == tokenLimitedPausedRun.Id);
            Require(stoppedTokenLimitedRun.State == AutomationState.Stopped &&
                    stoppedTokenLimitedRun.ProjectDirectory == tokenLimitedPausedRun.ProjectDirectory &&
                    stoppedTokenLimitedRun.ApprovedPlanSnapshot == tokenLimitedPausedRun.ApprovedPlanSnapshot &&
                    stoppedTokenLimitedRun.Options == tokenLimitedPausedRun.Options &&
                    stoppedTokenLimitedRun.MaxIterations == tokenLimitedPausedRun.MaxIterations &&
                    stoppedTokenLimitedRun.Iteration == tokenLimitedPausedRun.Iteration &&
                    stoppedTokenLimitedRun.NextRole == tokenLimitedPausedRun.NextRole &&
                    stoppedTokenLimitedRun.TotalTokenUsage == tokenLimitedPausedRun.TotalTokenUsage &&
                    stoppedTokenLimitedRun.LastSummary == tokenLimitedPausedRun.LastSummary &&
                    stoppedTokenLimitedRun.NextInstruction == tokenLimitedPausedRun.NextInstruction &&
                    stoppedTokenLimitedRun.StatusDetail == tokenLimitedPausedRun.StatusDetail,
                "A new explicit approval stops the token-limited run while preserving its plan, limits, stage, and usage history.");
            Require(draft.ApprovedPlanSnapshot.StartsWith(approvedProjectContent, StringComparison.Ordinal) &&
                    draft.ApprovedPlanSnapshot.Contains(planPath, StringComparison.Ordinal),
                "Run retains the user Plan and records its persisted Markdown path.");
            var popupSavedPlan = await tabDocumentStore.LoadAsync(coordinatorSlot, TerminalInputMode.Plan);
            Require(popupSavedPlan.Path == planPath && popupSavedPlan.Text == approvedProjectContent,
                "The automation popup saves into the canonical project Plan Markdown file.");
            Require(draft.MaxIterations is null && draft.Options.TokenBudget is null && draft.Options.MaxStageMinutes is null,
                "Default approval unexpectedly imposed iteration, aggregate-token, or per-stage limits.");
            Require(draft.State is AutomationState.Scheduled or AutomationState.Running or AutomationState.Completed,
                $"Run did not schedule the automation immediately: {draft.State}.");

            await VerifyExpiredClaimRemainsPendingAsync(window, store, draft.Id, holdFile, heldSignal);

            await UntilAsync(async () => (await store.GetAsync(draft.Id))?.State == AutomationState.Completed,
                "continuous commander workflow completion", seconds: 120);
            AutomationRun completed = (await store.GetAsync(draft.Id))!;
            const long expectedFirstRunTokenUsage = 180_867 + (168 * 7);
            Require(completed.TotalTokenUsage == expectedFirstRunTokenUsage,
                $"Expected {expectedFirstRunTokenUsage} exact tokens across eight native stages, received {completed.TotalTokenUsage}.");
            Require(completed.Options.TokenBudget is null && completed.WorkflowPhase == AutomationWorkflowPhase.FinalVerification &&
                    completed.PlanApproved && !string.IsNullOrWhiteSpace(completed.DeliveryEvidence),
                "The completed workflow retains approved planning and actual delivery evidence at final verification.");
            Require(completed.LastSummary is string reviewerSummary &&
                    reviewerSummary.Contains("최종 검증", StringComparison.Ordinal),
                "The final Reviewer summary was not preserved after natural-language contract normalization.");
            Require(File.Exists(deliveryProof) && completed.DeliveryEvidence!.Contains(
                    File.ReadAllText(deliveryProof, Encoding.UTF8), StringComparison.Ordinal),
                "The completed run did not retain the actual local fixture service launch proof.");

            string runDirectory = Path.Combine(project, "terminal-notes", "runs", draft.Id.ToString("N"));
            await UntilAsync(() => Directory.GetFiles(runDirectory, "completion.json", SearchOption.AllDirectories).Length == 8,
                "all eight accepted workflow stage completion markers to be persisted");
            string[] completionMarkers = Directory.GetFiles(runDirectory, "completion.json", SearchOption.AllDirectories);
            Require(completionMarkers.Length == 8, $"Expected eight workflow stage completion markers, found {completionMarkers.Length}.");
            Require(DispatchRolesInSubmissionOrder(completionMarkers).SequenceEqual(
                    [AutomationRole.Coordinator, AutomationRole.Reviewer, AutomationRole.Coordinator, AutomationRole.Implementer,
                     AutomationRole.Reviewer, AutomationRole.Coordinator, AutomationRole.Implementer, AutomationRole.Reviewer]),
                "The successful run must route Planning → PlanReview → Implementation → Verification → Delivery → FinalVerification.");
            RequireAutomationTeamRemainsOpen(tabs, team, "first project run");
            var nativeDispatches = new HashSet<string>(StringComparer.Ordinal);
            foreach (string markerPath in completionMarkers)
            {
                using JsonDocument marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath));
                JsonElement root = marker.RootElement;
                Require(root.GetProperty("source").GetString() == "opencode-native-session" &&
                        root.GetProperty("success").GetBoolean(),
                    "A stage completion marker did not come from a successful native OpenCode session.");
                JsonElement completionDispatch = root.GetProperty("dispatch");
                string sessionId = completionDispatch.GetProperty("SessionId").GetString()!;
                string messageId = completionDispatch.GetProperty("MessageId").GetString()!;
                Require(nativeDispatches.Add(sessionId + "/" + messageId),
                    "Two stages reused one native OpenCode dispatch identity.");
                string nativeSessionPath = Path.Combine(Path.GetDirectoryName(markerPath)!, "native-session.json");
                // The artifact-failure regression intentionally replaces one artifact
                // with a directory; it is inspected and removed below.
                if (Directory.Exists(nativeSessionPath)) continue;
                using JsonDocument nativeSession = JsonDocument.Parse(await File.ReadAllTextAsync(nativeSessionPath));
                Require(nativeSession.RootElement.GetProperty("SessionId").GetString() == sessionId &&
                        nativeSession.RootElement.GetProperty("MessageId").GetString() == messageId,
                    "The completion marker did not match the exact persisted native session/message identity.");
            }
            string[] firstRunTraces = TracesForPlan(trace, approvedProjectContent);
            Require(firstRunTraces.Length == 8 && WorkflowPhasesInTraceOrder(firstRunTraces).SequenceEqual(
                    ["Planning", "PlanReview", "Implementation", "Implementation", "Verification", "Delivery", "Delivery", "FinalVerification"]),
                "The native role prompts did not carry the expected machine-readable phase sequence.");
            await UntilAsync(() => File.Exists(artifactBlockSignal),
                "fake native server to block one post-submit native-session artifact");
            string blockedArtifactPath = await File.ReadAllTextAsync(artifactBlockSignal, Encoding.UTF8);
            Require(Directory.Exists(blockedArtifactPath),
                "The isolated fake server created the native-session artifact failure fixture.");
            string diagnosticsPath = AutomationDiagnostics.LogPath(project, draft.Id);
            await UntilAsync(() => File.Exists(diagnosticsPath) &&
                File.ReadAllText(diagnosticsPath, Encoding.UTF8).Contains("native_artifact_failed", StringComparison.Ordinal),
                "post-submit native artifact failure diagnostic");
            string diagnosticText = await File.ReadAllTextAsync(diagnosticsPath, Encoding.UTF8);
            Require(diagnosticText.Contains("prompt_submitted", StringComparison.Ordinal) &&
                    diagnosticText.Contains("native_artifact_failed", StringComparison.Ordinal) &&
                    diagnosticText.Contains(draft.Id.ToString(), StringComparison.OrdinalIgnoreCase),
                "A post-submit artifact failure records the same run without hiding the successful native dispatch.");
            Directory.Delete(blockedArtifactPath, recursive: true);
            Require(firstRunTraces.Length == 8,
                "A post-submit native-session artifact failure does not duplicate the native role prompt.");
            foreach (string tracePath in firstRunTraces)
            {
                using JsonDocument stageTrace = JsonDocument.Parse(await File.ReadAllTextAsync(tracePath));
                Require(stageTrace.RootElement.GetProperty("SourcePath").GetString() == planPath &&
                        stageTrace.RootElement.GetProperty("SourcePlan").GetString() == approvedProjectContent &&
                        stageTrace.RootElement.GetProperty("Prompt").GetString()!.Contains("최신 저장 내용을 따르세요", StringComparison.Ordinal) &&
                        stageTrace.RootElement.GetProperty("Model").ValueKind == JsonValueKind.Null,
                    "Every native OpenCode role re-read the persisted popup Plan path and latest saved text while inheriting the configured model.");
            }

            await window.Dispatcher.InvokeAsync(new Action(() =>
            {
                ((TabControl)window.FindName("TerminalTabs")).SelectedItem = initialCoordinatorTab;
                Call(window, "UpdateWorkbenchTarget");
            })).Task;
            await UntilAsync(() => documentEditor.IsEnabled && documentEditor.Text == approvedProjectContent,
                "popup save updates the same right-pane Plan cache");
            const string popupRetryPlan = "# Popup retry plan\n\nThe same automation window can save and run again.";
            await (Task)Call(window, "RefreshAutomationRunsAsync")!;
            var automationRunHistory = Field<ListBox>(window, "_automationRunsList");
            automationRunHistory.SelectedItem = automationRunHistory.Items.Cast<object>().Single(item =>
                (Guid)GetProperty(GetProperty(item, "Run")!, "Id")! == draft.Id);
            Require((Guid)GetProperty(GetProperty(automationRunHistory.SelectedItem!, "Run")!, "Id")! == draft.Id,
                "The integration fixture keeps an earlier completed run selected before a different Reviewer result arrives.");
            automationContent.Text = popupRetryPlan;
            automationStart.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await UntilAsync(async () => (await store.LoadAsync()).Count == 3,
                "second popup-triggered run creation");
            AutomationRun popupRetryRun = (await store.LoadAsync()).Single(run =>
                run.Id != draft.Id && run.Id != tokenLimitedPausedRun.Id);
            Require(popupRetryRun.ProjectDirectory == Path.GetFullPath(project) &&
                    popupRetryRun.ApprovedPlanSnapshot.StartsWith(popupRetryPlan, StringComparison.Ordinal),
                "A successful popup save refreshes its snapshot for a later run.");
            await UntilAsync(async () => (await store.GetAsync(popupRetryRun.Id))?.State == AutomationState.Completed,
                "second popup run completes all roles", seconds: 90);
            string popupRetryRunDirectory = Path.Combine(project, "terminal-notes", "runs", popupRetryRun.Id.ToString("N"));
            await UntilAsync(() => Directory.GetFiles(popupRetryRunDirectory, "completion.json", SearchOption.AllDirectories).Length == 8,
                "the second same-project run's eight completion markers to be persisted");
            string[] popupRetryCompletionMarkers = Directory.GetFiles(popupRetryRunDirectory, "completion.json", SearchOption.AllDirectories);
            Require(DispatchRolesInSubmissionOrder(popupRetryCompletionMarkers).SequenceEqual(
                    [AutomationRole.Coordinator, AutomationRole.Reviewer, AutomationRole.Coordinator, AutomationRole.Implementer,
                     AutomationRole.Reviewer, AutomationRole.Coordinator, AutomationRole.Implementer, AutomationRole.Reviewer]),
                "The second run for the same project follows the complete continuous commander sequence.");
            RequireAutomationTeamRemainsOpen(tabs, team, "second same-project run");
            var popupRetryCompleted = (await store.GetAsync(popupRetryRun.Id))!;
            string popupRetrySummary = popupRetryCompleted.LastSummary ?? "";
            Require(popupRetrySummary.Length > 0, "The new Reviewer completion persisted a summary for the result view.");
            await UntilAsync(() =>
            {
                Guid? selectedRunId = (automationRunHistory.SelectedItem is { } item
                    ? (Guid)GetProperty(GetProperty(item, "Run")!, "Id")!
                    : null);
                return selectedRunId == popupRetryRun.Id &&
                    Field<TextBlock>(window, "_automationRunStatus").Text.Contains(popupRetrySummary, StringComparison.Ordinal);
            }, "new Reviewer completion to replace the older history selection and expose its summary");
            Require((await tabDocumentStore.LoadAsync(coordinatorSlot, TerminalInputMode.Plan)).Text == popupRetryPlan,
                "A later popup run updates the same Plan file and cache.");
            await window.Dispatcher.InvokeAsync(new Action(() =>
            {
                ((TabControl)window.FindName("TerminalTabs")).SelectedItem = initialCoordinatorTab;
                Call(window, "UpdateWorkbenchTarget");
            })).Task;
            await UntilAsync(() => documentEditor.IsEnabled && documentEditor.Text == popupRetryPlan,
                "popup retry refreshes the coordinator cache");
            Require(await File.ReadAllTextAsync(otherSlotPlanPath) == otherSlotPlanText,
                "Saving the project Plan leaves an unrelated legacy slot file unchanged.");
            foreach ((AutomationRole role, TabItem roleTab) in team)
            {
                object roleState = roleTab.Tag;
                string rolePath = (string)GetProperty(roleState, "PlanDocumentPath")!;
                var roleStore = new TerminalTabDocumentStore(project);
                Require(string.Equals(rolePath, planPath, StringComparison.OrdinalIgnoreCase) &&
                        (await roleStore.LoadAsync((int)GetProperty(roleState, "Sequence")!, TerminalInputMode.Plan)).Text == popupRetryPlan,
                    $"The canonical project Plan and latest draft are visible in role pane {role}.");
            }
            foreach (var (mode, text) in legacyDocuments)
                Require(await File.ReadAllTextAsync(tabDocumentStore.GetDocumentPath(coordinatorSlot, mode)) == text,
                    $"Legacy {mode} Markdown remains unchanged by Plan automation.");

            const string rightPlanText = "# Right-pane saved plan\n\nRun the same role team from the selected Plan editor.";
            automationContent.Text = "stale popup draft must not overwrite the right pane";
            documentEditor.Text = rightPlanText;
            automationStart.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await UntilAsync(() => Descendants(automationWindow).OfType<TextBlock>()
                    .Any(block => block.Name == "AutomationStartStatus" && block.Text.Contains("오른쪽 플랜 초안이 창을 연 뒤 바뀌었습니다", StringComparison.Ordinal)),
                "popup detects a newer right-pane draft");
            Require((await store.LoadAsync()).Count == 3 &&
                    (await tabDocumentStore.LoadAsync(coordinatorSlot, TerminalInputMode.Plan)).Text == popupRetryPlan &&
                    documentEditor.Text == rightPlanText,
                "A stale popup cannot overwrite a newer right-pane draft or schedule a run.");

            TabItem modalTab = team[AutomationRole.Coordinator];
            var modalPane = (TerminalPane)GetProperty(modalTab.Tag, "Pane")!;
            Type executionModeType = window.GetType().GetField("_planExecutionMode", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType;
            Call(window, "SetPlanExecutionMode", Enum.Parse(executionModeType, "Conductor"));
            await UntilAsync(() => Field<Button>(window, "_tabDocumentExecute").IsEnabled,
                "right-pane save-and-run becomes available after the prior run completes");
            var rightRunTask = (Task)Call(window, "SaveTabDocumentAsync", true)!;
            await rightRunTask;
            try
            {
                await UntilAsync(async () => (await store.LoadAsync()).Count == 4,
                    "right-pane save-and-run creates an automation run");
            }
            catch (TimeoutException exception)
            {
                string diagnostic = await window.Dispatcher.InvokeAsync(() =>
                {
                    var mode = FieldValue(window, "_planExecutionMode");
                    var source = FieldValue(window, "_composerState");
                    string? block = source is null ? null : (string?)Call(window, "PlanExecutionBlockReason", source, mode);
                    string status = ((TextBlock)window.FindName("StatusLabel")).Text;
                    string documentStatus = Field<TextBlock>(window, "_tabDocumentStatus").Text;
                    bool starting = Field<bool>(window, "_startingProjectAutomation");
                    object? nativeStage = FieldValue(window, "_startingNativeAutomation");
                    object? automationStage = FieldValue(window, "_automationStage");
                    object? path = FieldValue(window, "_activeTabDocumentPath");
                    return $"mode={mode}; block={block}; status={status}; document={documentStatus}; " +
                        $"starting={starting}; nativeStage={nativeStage}; automationStage={automationStage}; path={path}";
                }).Task;
                throw new TimeoutException(exception.Message + " Current UI state: " + diagnostic, exception);
            }
            AutomationRun rightRun = (await store.LoadAsync()).Single(run =>
                run.Id != draft.Id && run.Id != popupRetryRun.Id && run.Id != tokenLimitedPausedRun.Id);
            Require(rightRun.ApprovedPlanSnapshot.StartsWith(rightPlanText, StringComparison.Ordinal) &&
                    rightRun.ApprovedPlanSnapshot.Contains(planPath, StringComparison.Ordinal),
                "Right-pane save-and-run preserves the Plan and persisted path in the approved snapshot.");
            Task<bool> modalRun = Task.Run(async () =>
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                try
                {
                    while (DateTime.UtcNow < deadline)
                    {
                        if ((await store.GetAsync(rightRun.Id))?.State == AutomationState.Completed)
                        {
                            return await window.Dispatcher.InvokeAsync(() =>
                            {
                                var compact = FieldValue(window, "_compactTerminal") as Window;
                                bool remainedOpen = compact is { IsVisible: true } &&
                                    ReferenceEquals(Field<TerminalPane>(compact, "_pane"), modalPane);
                                compact?.Close();
                                return remainedOpen;
                            }).Task;
                        }
                        await Task.Delay(100);
                    }
                    throw new TimeoutException("Second automation run did not complete while the compact terminal modal was open.");
                }
                finally
                {
                    await window.Dispatcher.InvokeAsync(() => (FieldValue(window, "_compactTerminal") as Window)?.Close());
                }
            });
            Call(window, "OpenCompactTerminal", modalTab);
            Require(await modalRun, "The compact terminal modal closed or changed pane before the right-pane run completed.");
            AutomationRun rightCompleted = (await store.GetAsync(rightRun.Id))!;
            Require(rightCompleted.TotalTokenUsage == 1_344,
                "The right-pane run did not persist exact token usage for eight native workflow stages.");
            string rightRunDirectory = Path.Combine(project, "terminal-notes", "runs", rightRun.Id.ToString("N"));
            await UntilAsync(() => Directory.GetFiles(rightRunDirectory, "completion.json", SearchOption.AllDirectories).Length == 8,
                "the right-pane run's eight completion markers to be persisted");
            Require(Directory.GetFiles(rightRunDirectory, "completion.json", SearchOption.AllDirectories).Length == 8,
                "The right-pane run did not create all eight workflow stage completion markers.");
            Require(Directory.GetFiles(trace, "*.trace", SearchOption.TopDirectoryOnly).Length == 24,
                "The fake native OpenCode server did not receive eight prompts in each of the first three runs.");
            foreach (string tracePath in TracesForPlan(trace, rightPlanText))
            {
                using JsonDocument stageTrace = JsonDocument.Parse(await File.ReadAllTextAsync(tracePath));
                Require(stageTrace.RootElement.GetProperty("SourcePath").GetString() == planPath,
                    "Each role in the right-pane run re-read the selected Plan Markdown path.");
            }
            await UntilAsync(() => team.All(pair =>
                    ((TerminalPane)GetProperty(pair.Value.Tag, "Pane")!).TokenUsageLabel.EndsWith(
                        pair.Key switch
                        {
                            AutomationRole.Coordinator => "182.21K",
                            AutomationRole.Implementer => "1.01K",
                            _ => "1.51K"
                        }, StringComparison.Ordinal)),
                "terminal token output drainage");
            foreach ((AutomationRole role, TabItem roleTab) in team)
            {
                var rolePane = (TerminalPane)GetProperty(roleTab.Tag, "Pane")!;
                string expectedRoleTokens = role switch
                {
                    AutomationRole.Coordinator => "182.21K",
                    AutomationRole.Implementer => "1.01K",
                    _ => "1.51K"
                };
                Require(rolePane.TokenUsageLabel.EndsWith(expectedRoleTokens, StringComparison.Ordinal),
                    $"Repeated same-role CLI usage was suppressed or miscounted: {rolePane.TokenUsageLabel}");
                Require(rolePane.HasCompletedWork,
                    $"A finished automation role did not expose response completion to the mini dashboard: {role}, activity={rolePane.Activity}");
            }

            TabItem coordinatorTab = team[AutomationRole.Coordinator];
            ((TabControl)window.FindName("TerminalTabs")).SelectedItem = coordinatorTab;
            Call(window, "UpdateWorkbenchTarget");
            object coordinatorState = coordinatorTab.Tag;
            var coordinatorPane = (TerminalPane)GetProperty(coordinatorState, "Pane")!;
            await UntilAsync(() => coordinatorPane.IsReady, "coordinator shell readiness");

            // Even an already-satisfied plan follows the mandatory approval, implementation, delivery, and final-review phases.
            SetProperty(coordinatorState, "InputMode", TerminalInputMode.Plan);
            Call(window, "UpdateWorkbenchTarget");
            documentEditor = Field<TextBox>(window, "_tabDocumentEditor");
            await UntilAsync(() => documentEditor.IsEnabled, "Plan editor for already-satisfied workflow run");
            const string alreadySatisfiedPlan = "# WF_ALREADY_SATISFIED\n\nThe current project state already meets every requirement.";
            documentEditor.Text = alreadySatisfiedPlan;
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            AutomationRun satisfiedRun = await (Task<AutomationRun>)Call(window, "StartProjectAutomationAsync",
                project, alreadySatisfiedPlan, coordinatorSlot, planPath, null)!;
            await UntilAsync(async () => (await store.GetAsync(satisfiedRun.Id))?.State == AutomationState.Completed,
                "already-satisfied plan completes through the full workflow", seconds: 120);
            string satisfiedRunDirectory = Path.Combine(project, "terminal-notes", "runs", satisfiedRun.Id.ToString("N"));
            await UntilAsync(() => Directory.GetFiles(satisfiedRunDirectory, "completion.json", SearchOption.AllDirectories).Length == 8,
                "already-satisfied run's eight completion markers to be persisted");
            string[] satisfiedCompletionMarkers = Directory.GetFiles(satisfiedRunDirectory, "completion.json", SearchOption.AllDirectories);
            Require(satisfiedCompletionMarkers.Length == 8 && DispatchRolesInSubmissionOrder(satisfiedCompletionMarkers).SequenceEqual(
                    [AutomationRole.Coordinator, AutomationRole.Reviewer, AutomationRole.Coordinator, AutomationRole.Implementer,
                     AutomationRole.Reviewer, AutomationRole.Coordinator, AutomationRole.Implementer, AutomationRole.Reviewer]),
                "An already-satisfied plan still passes planning, implementation verification, delivery, and final verification.");
            var satisfiedTraces = TracesForPlan(trace, alreadySatisfiedPlan)
                .Select(path =>
                {
                    using JsonDocument traceDocument = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                    JsonElement root = traceDocument.RootElement;
                    return new
                    {
                        Role = root.GetProperty("Role").GetString(),
                        Phase = root.GetProperty("WorkflowPhase").GetString(),
                        Prompt = root.GetProperty("Prompt").GetString() ?? ""
                    };
                }).ToArray();
            Require(satisfiedTraces.Length == 8 &&
                    satisfiedTraces.Select(item => item.Phase).SequenceEqual(
                        ["Planning", "PlanReview", "Implementation", "Implementation", "Verification", "Delivery", "Delivery", "FinalVerification"]),
                "The already-satisfied plan prompts preserve the full machine-readable workflow phase sequence.");
            Require(satisfiedTraces.Single(item => item.Phase == "PlanReview").Prompt.Contains(
                    "Reviewer verification: 인수 기준을 기준으로 플랜을 검토하세요.", StringComparison.Ordinal),
                "The coordinator's planning instruction was not forwarded into the PlanReview prompt.");

            const string reviewLoopPlan = "# WF_PLAN_REVIEW_REJECT_ONCE WF_REPEATED_REVIEW_FEEDBACK\n\nReject the first plan review, then return the same implementation review issue twice before passing it.";
            documentEditor.Text = reviewLoopPlan;
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            AutomationRun reviewLoopRun = await (Task<AutomationRun>)Call(window, "StartProjectAutomationAsync",
                project, reviewLoopPlan, coordinatorSlot, planPath, null)!;
            await UntilAsync(async () => (await store.GetAsync(reviewLoopRun.Id))?.State == AutomationState.Completed,
                "plan revision and repeated reviewer feedback to reach final delivery completion", seconds: 180);
            AutomationRun reviewLoopCompleted = (await store.GetAsync(reviewLoopRun.Id))!;
            Require(reviewLoopCompleted.PlanApproved && reviewLoopCompleted.WorkflowPhase == AutomationWorkflowPhase.FinalVerification &&
                    !string.IsNullOrWhiteSpace(reviewLoopCompleted.DeliveryEvidence),
                "The revised plan was approved and the run completed after repeated feedback with retained service evidence.");
            string reviewLoopRunDirectory = Path.Combine(project, "terminal-notes", "runs", reviewLoopRun.Id.ToString("N"));
            await UntilAsync(() => Directory.GetFiles(reviewLoopRunDirectory, "completion.json", SearchOption.AllDirectories).Length == 16,
                "all sixteen plan-revision and repeated-review stage markers");
            string[] reviewLoopMarkers = Directory.GetFiles(reviewLoopRunDirectory, "completion.json", SearchOption.AllDirectories);
            Require(DispatchRolesInSubmissionOrder(reviewLoopMarkers).SequenceEqual(
                    [AutomationRole.Coordinator, AutomationRole.Reviewer,
                     AutomationRole.Coordinator, AutomationRole.Reviewer,
                     AutomationRole.Coordinator, AutomationRole.Implementer, AutomationRole.Reviewer,
                     AutomationRole.Coordinator, AutomationRole.Implementer, AutomationRole.Reviewer,
                     AutomationRole.Coordinator, AutomationRole.Implementer, AutomationRole.Reviewer,
                     AutomationRole.Coordinator, AutomationRole.Implementer, AutomationRole.Reviewer]),
                "A rejected plan returns to Coordinator planning; each failed review returns to Coordinator then Implementer without pausing on repeated feedback.");
            string[] reviewLoopPhases = WorkflowPhasesInTraceOrder(TracesForPlan(trace, reviewLoopPlan));
            Require(reviewLoopPhases.SequenceEqual(
                    ["Planning", "PlanReview", "Planning", "PlanReview",
                     "Implementation", "Implementation", "Verification",
                     "Implementation", "Implementation", "Verification",
                     "Implementation", "Implementation", "Verification",
                     "Delivery", "Delivery", "FinalVerification"]),
                "A rejected plan blocks implementation until approval, and repeated verification feedback routes through the Commander and Implementer.");

            var journal = new ProjectWorkJournal(project);
            int journalCount = (await journal.LoadAsync()).Count;
            const string questionText = "QUESTION_SENTINEL_한글";
            Call(window, "ObserveManualSubmission", coordinatorState, questionText);
            Require((await journal.LoadAsync()).Count == journalCount,
                "Direct terminal question input does not persist a work-journal entry.");
            Require(!Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                    .Any(path => File.ReadAllText(path).Contains(questionText, StringComparison.Ordinal)),
                "Direct terminal question input is absent from project files.");

            SetProperty(coordinatorState, "InputMode", TerminalInputMode.Plan);
            Call(window, "UpdateWorkbenchTarget");
            documentEditor = Field<TextBox>(window, "_tabDocumentEditor");
            await UntilAsync(() => documentEditor.IsEnabled, "Plan document editor load");
            const string planText = "# PLAN_SENTINEL_한글\n\nworkflow integration plan";
            documentEditor.Text = planText;
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            var savedPlan = await tabDocumentStore.LoadAsync(coordinatorSlot, TerminalInputMode.Plan);
            Require(savedPlan.Exists && savedPlan.Path == planPath && savedPlan.Text == planText,
                "Plan mode saves the canonical project Markdown instead of a journal input entry.");
            Require(!(await journal.LoadAsync()).Any(entry => entry.Mode == TerminalInputMode.Plan && entry.OriginalText == planText),
                "Plan document content is not duplicated into the work journal.");

            automationWindow.Close();
            Call(window, "ShowAutomationWindow");
            automationWindow = Field<Window>(window, "_automationWindow");
            await UntilAsync(() => automationWindow.IsLoaded && automationWindow.ActualWidth > 0,
                "automation window reopens on the current Plan file");
            TextBox reopenedProjectName = Descendants(automationWindow).OfType<TextBox>()
                .Single(input => input.Name == "ProjectAutomationNameInput");
            Require(reopenedProjectName.Text == acceptedProjectName,
                "Reopening the automation popup prefills the project's remembered name.");
            TextBox conflictPopupContent = Descendants(automationWindow).OfType<TextBox>()
                .Single(input => input.Name == "AutomationProjectContent");
            Button conflictPopupRun = Descendants(automationWindow).OfType<Button>()
                .Single(button => button.Name == "AutomationStartButton");
            await UntilAsync(() => conflictPopupContent.IsEnabled && conflictPopupContent.Text == planText,
                "reopened automation window loads the current shared Plan");
            const string externalPlanText = "external disk edit must survive popup conflict";
            await File.WriteAllTextAsync(planPath, externalPlanText, new UTF8Encoding(false));
            conflictPopupContent.Text = "popup must not overwrite a concurrent disk edit";
            conflictPopupRun.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await UntilAsync(() => Descendants(automationWindow).OfType<TextBlock>()
                    .Any(block => block.Name == "AutomationStartStatus" && block.Text.Contains("외부에서 변경", StringComparison.Ordinal)),
                "popup save reports an external file conflict");
            Require((await store.LoadAsync()).Count == 6 &&
                    await File.ReadAllTextAsync(planPath) == externalPlanText &&
                    documentEditor.Text == planText,
                "External disk conflict protects both the file and cached editor draft without scheduling a run.");
            var retainedReviewerPane = (TerminalPane)GetProperty(team[AutomationRole.Reviewer].Tag, "Pane")!;
            Call(window, "ReleaseAutomationPane", retainedReviewerPane, true);
            Require(retainedReviewerPane.IsReady && retainedReviewerPane.IsRunning &&
                    tabs.Items.Contains(team[AutomationRole.Reviewer]),
                "A role-result failure must remain visible without disposing its OpenCode terminal.");
            foreach (var (mode, text) in legacyDocuments)
                Require(await File.ReadAllTextAsync(tabDocumentStore.GetDocumentPath(coordinatorSlot, mode)) == text,
                    $"Legacy {mode} Markdown survives both Plan run entry points.");

            SetProperty(coordinatorState, "InputMode", TerminalInputMode.Plan);
            const string manualText = "Write-Output 'MANUAL_CAPTURE_한글'";
            int journalCountBeforeManualInput = (await journal.LoadAsync()).Count;
            var browser = Field<WebView2>(coordinatorPane, "_browser");
            var captured = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCaptured(string text) => captured.TrySetResult(text);
            coordinatorPane.InputSubmitted += OnCaptured;
            await browser.ExecuteScriptAsync("terminal.input(" + JsonSerializer.Serialize(manualText + "\r") + ")");
            string capturedText = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            coordinatorPane.InputSubmitted -= OnCaptured;
            Require(capturedText == manualText, "Manual input event did not preserve the submitted text.");
            Require(!coordinatorPane.HasCompletedWork,
                "Manual input does not fabricate a completed native OpenCode turn without assistant idle evidence.");
            Require((await journal.LoadAsync()).Count == journalCountBeforeManualInput,
                "Manual terminal input does not create a work-journal entry.");

            CaptureVisual(automationWindow, Path.Combine(AppContext.BaseDirectory, "workflow-automation.png"));
            window.Width = 920;
            window.Height = 700;
            window.Activate();
            await Task.Delay(250);
            CaptureVisual(window, Path.Combine(AppContext.BaseDirectory, "workflow-main-small.png"));

            Call(window, "DisposeResources");
            window.Close();
            window = new MainWindow(settings, autoStartCodex: false, startupHooks: FakeStartupHooks, initialWorkspace: project);
            window.Show();
            await UntilAsync(() => ((TabControl)window.FindName("TerminalTabs")).Items.Count == 1 &&
                    ((TerminalPane)GetProperty(((TabItem)((TabControl)window.FindName("TerminalTabs")).Items[0]).Tag, "Pane")!).IsReady,
                "project-name restoration after a new app session");
            Require((bool)Call(window, "HasProjectName", project)! &&
                    (string)Call(window, "ProjectDisplayName", project)! == acceptedProjectName,
                "The named automation group is restored from settings in a new app session.");

            Console.WriteLine("Workflow integration tests passed: named group, plan approval gate, repeated reviewer feedback loop, phase-aware continuous commander, >100k stage, retained fixture launch proof, unlimited token budget, composer modes, manual capture.");
            succeeded = true;
        }
        catch (Exception exception)
        {
            _failure = 1;
            Console.Error.WriteLine(exception);
        }
        finally
        {
            try { if (File.Exists(holdFile)) File.Delete(holdFile); } catch (IOException) { }
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_TRACE", null);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_HOLD_FILE", originalHoldFile);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_HELD_SIGNAL", originalHeldSignal);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_DELIVERY_PROOF", originalDeliveryProof);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_BLOCK_NATIVE_ARTIFACT_ONCE", originalArtifactBlock);
            Environment.SetEnvironmentVariable("TN_DEFAULT_PROBE", null);
            Environment.CurrentDirectory = originalDirectory;
            if (window is not null)
            {
                Call(window, "DisposeResources");
                window.Close();
            }
            app.Shutdown();
            if (succeeded)
            {
                try { Directory.Delete(fixture, recursive: true); } catch (IOException) { }
            }
            else Console.Error.WriteLine("Workflow fixture retained: " + fixture);
        }
    }

    private static int RunFakeOpenCode(string[] args)
        => FakeNativeOpenCodeServer.Run(args);

    private static AutomationRole[] DispatchRolesInSubmissionOrder(IEnumerable<string> markerPaths) =>
        markerPaths
            .Select(path =>
            {
                using JsonDocument marker = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                JsonElement dispatch = marker.RootElement.GetProperty("dispatch");
                return (
                    Role: (AutomationRole)dispatch.GetProperty("Role").GetInt32(),
                    SubmittedAt: dispatch.GetProperty("SubmittedAt").GetDateTimeOffset());
            })
            .OrderBy(dispatch => dispatch.SubmittedAt)
            .Select(dispatch => dispatch.Role)
            .ToArray();

    private static string[] TracesForPlan(string traceDirectory, string plan) =>
        Directory.GetFiles(traceDirectory, "*.trace", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                using JsonDocument trace = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                return trace.RootElement.GetProperty("SourcePlan").GetString() == plan;
            })
            .OrderBy(path =>
            {
                using JsonDocument trace = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                return trace.RootElement.GetProperty("RecordedAt").GetDateTimeOffset();
            })
            .ToArray();

    private static string[] WorkflowPhasesInTraceOrder(IEnumerable<string> tracePaths) =>
        tracePaths.Select(path =>
            {
                using JsonDocument trace = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                return trace.RootElement.GetProperty("WorkflowPhase").GetString()!;
            })
            .ToArray();

    private static void RequireAutomationTeamRemainsOpen(
        TabControl tabs,
        IReadOnlyDictionary<AutomationRole, TabItem> team,
        string runDescription)
    {
        Require(team.Count == 3, $"The three-role terminal team disappeared after the {runDescription}.");
        foreach ((AutomationRole role, TabItem roleTab) in team)
        {
            Require(tabs.Items.Contains(roleTab),
                $"The {role} terminal tab was removed after the {runDescription}.");
            var pane = (TerminalPane)GetProperty(roleTab.Tag, "Pane")!;
            Require(pane.IsReady && pane.IsRunning,
                $"The {role} terminal stopped after the {runDescription}: ready={pane.IsReady}, running={pane.IsRunning}.");
        }
    }

    private static async Task VerifyDelayedStartupStopAndOrphanRecoveryAsync(string fixture)
    {
        string project = Path.Combine(fixture, "startup recovery project");
        string settings = Path.Combine(fixture, "startup recovery settings", "settings.json");
        string trace = Path.Combine(fixture, "startup recovery trace");
        string nativeStarted = Path.Combine(fixture, "startup native processes");
        string startupHold = Path.Combine(fixture, "hold-native-startup");
        string? originalTrace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE");
        string? originalHold = Environment.GetEnvironmentVariable("TN_WORKFLOW_HOLD_FILE");
        string? originalHeldSignal = Environment.GetEnvironmentVariable("TN_WORKFLOW_HELD_SIGNAL");
        string? originalStartupHold = Environment.GetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_HOLD_FILE");
        string? originalStartupTrace = Environment.GetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_TRACE");
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(trace);
            Directory.CreateDirectory(nativeStarted);
            File.WriteAllText(Path.Combine(project, "AGENTS.md"), "# Startup regression fixture\n", new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("TN_WORKFLOW_TRACE", trace);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_HOLD_FILE", null);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_HELD_SIGNAL", null);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_HOLD_FILE", null);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_TRACE", nativeStarted);

            window = new MainWindow(settings, autoStartCodex: false, startupHooks: FakeStartupHooks, initialWorkspace: project);
            window.Show();
            await UntilAsync(() => FieldValue(window, "_automationOwnership") is not null,
                "isolated startup regression scheduler initialization");
            var tabs = (TabControl)window.FindName("TerminalTabs");
            await UntilAsync(() => tabs.Items.Count == 1 &&
                ((TerminalPane)GetProperty(((TabItem)tabs.Items[0]).Tag, "Pane")!).IsReady,
                "isolated startup regression terminal readiness");
            File.WriteAllText(startupHold, "delay fake native server startup");
            Environment.SetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_HOLD_FILE", startupHold);
            int startedBeforeApproval = Directory.GetFiles(nativeStarted, "*.started").Length;
            object initialState = ((TabItem)tabs.SelectedItem).Tag;
            string planPath = (string)GetProperty(initialState, "PlanDocumentPath")!;
            Require(string.Equals(planPath, TerminalTabDocumentStore.GetProjectPlanPath(project), StringComparison.OrdinalIgnoreCase),
                "An isolated project also receives only its canonical terminal-notes/plan.md path.");
            const string approvedPlan = "# Delayed native startup\n\nWait for OpenCode's native API before sending a role prompt.";

            var store = new AutomationStore(project);
            var orphan = new AutomationRun(
                Id: Guid.NewGuid(),
                ProjectDirectory: Path.GetFullPath(project),
                ApprovedPlanSnapshot: "Historical orphaned Running plan.",
                Options: new AutomationExecutionOptions(TokenBudget: 42, MaxStageMinutes: 13),
                MaxIterations: 5,
                Iteration: 2,
                State: AutomationState.Running,
                NextRole: AutomationRole.Implementer,
                CreatedAt: DateTimeOffset.UtcNow.AddHours(-3),
                UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-20),
                ScheduledFor: null,
                ActiveDispatchToken: Guid.NewGuid(),
                Revision: 7,
                PauseAfterCurrentStage: false,
                TotalTokenUsage: 321,
                LastSummary: "Historical stage result.",
                NextInstruction: "Historical next instruction.",
                StatusDetail: "Claimed before the app closed.");
            var serialization = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            serialization.Converters.Add(new JsonStringEnumConverter());
            Directory.CreateDirectory(Path.GetDirectoryName(store.StorePath)!);
            await File.WriteAllTextAsync(store.StorePath,
                JsonSerializer.Serialize(new { Version = 1, Runs = new[] { orphan } }, serialization), new UTF8Encoding(false));
            Require(!Field<HashSet<string>>(window, "_automationProjects").Contains(project),
                "The orphan fixture is not registered for startup recovery before explicit approval.");

            Call(window, "ShowAutomationWindow");
            var automationWindow = Field<Window>(window, "_automationWindow");
            await UntilAsync(() => automationWindow.IsLoaded && automationWindow.ActualWidth > 0,
                "isolated automation window readiness");
            var nameInput = Descendants(automationWindow).OfType<TextBox>()
                .Single(input => input.Name == "ProjectAutomationNameInput");
            var content = Descendants(automationWindow).OfType<TextBox>()
                .Single(input => input.Name == "AutomationProjectContent");
            await UntilAsync(() => content.IsEnabled, "isolated startup plan load");
            nameInput.Text = "Startup regression project";
            content.Text = approvedPlan;
            Button start = Descendants(automationWindow).OfType<Button>()
                .Single(button => button.Name == "AutomationStartButton");
            Require(start.IsEnabled, "The isolated startup approval button did not enable.");

            start.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await UntilAsync(async () =>
            {
                var runs = await store.LoadAsync();
                return runs.Count == 2 && runs.Any(run => run.Id != orphan.Id &&
                    run.State is AutomationState.Scheduled or AutomationState.Running);
            }, "new approval to replace an orphaned Running run");
            var replacedOrphan = (await store.GetAsync(orphan.Id))!;
            Require(replacedOrphan.State == AutomationState.Stopped && replacedOrphan.ActiveDispatchToken is null &&
                    replacedOrphan.ProjectDirectory == orphan.ProjectDirectory &&
                    replacedOrphan.ApprovedPlanSnapshot == orphan.ApprovedPlanSnapshot &&
                    replacedOrphan.Options == orphan.Options && replacedOrphan.MaxIterations == orphan.MaxIterations &&
                    replacedOrphan.Iteration == orphan.Iteration && replacedOrphan.NextRole == orphan.NextRole &&
                    replacedOrphan.TotalTokenUsage == orphan.TotalTokenUsage &&
                    replacedOrphan.LastSummary == orphan.LastSummary && replacedOrphan.NextInstruction == orphan.NextInstruction,
                "Explicit approval recovers an orphaned Running claim and preserves its history before replacing it.");

            await UntilAsync(() => FieldValue(window, "_automationTeam") is Dictionary<AutomationRole, TabItem> { Count: 3 } &&
                FieldValue(window, "_startingNativeAutomation") is not null,
                "Coordinator startup to wait for the native OpenCode server");
            await UntilAsync(() => Directory.GetFiles(nativeStarted, "*.started").Length > startedBeforeApproval,
                "fake Coordinator process to start behind the native readiness gate");
            var team = Field<Dictionary<AutomationRole, TabItem>>(window, "_automationTeam");
            var coordinatorPane = (TerminalPane)GetProperty(team[AutomationRole.Coordinator].Tag, "Pane")!;
            await ((Task)Call(coordinatorPane, "EnsureReadyAsync", CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(30));
            Require(coordinatorPane.IsReady && coordinatorPane.IsRunning &&
                    coordinatorPane.ActualWidth > 0 && coordinatorPane.ActualHeight > 0 && coordinatorPane.IsVisible,
                "The Coordinator TUI reaches visible, nonzero layout readiness before native status is checked.");
            Guid activeRunId = (await store.LoadAsync()).Single(run => run.Id != orphan.Id).Id;
            await UntilAsync(async () => (await store.GetAsync(activeRunId))?.State == AutomationState.Running &&
                !(bool)GetProperty(coordinatorPane, "CanStartNativeAutomation")!,
                "claimed run to wait for native OpenCode readiness");
            var activeRun = (await store.GetAsync(activeRunId))!;
            // Selecting another terminal must not make the progress panel report that
            // tab as the active worker.  The panel is expected to follow the live claim.
            tabs.SelectedItem = team[AutomationRole.Reviewer];
            Call(window, "UpdateAutomationProgressPanel");
            string activeRoleLabel = (string)(typeof(MainWindow).GetMethod("RoleLabel",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { AutomationRole.Coordinator })!);
            string progressMeta = Field<TextBlock>(window, "_automationProgressRole").Text;
            Require(progressMeta.Contains(activeRoleLabel, StringComparison.Ordinal),
                "The progress panel follows the active Coordinator claim even when the Reviewer tab is selected.");
            var coordinatorMonitor = (OpenCodeStatusMonitor)FieldValue(coordinatorPane, "_openCodeStatus")!;
            Require(coordinatorMonitor.Snapshot.Kind != OpenCodeStatusKind.Idle,
                "A native session that has not opened its loopback endpoint cannot be treated as ready.");
            Require(coordinatorPane.IsReady && coordinatorPane.IsRunning &&
                    coordinatorMonitor.Snapshot.Kind != OpenCodeStatusKind.Idle &&
                    Directory.GetFiles(trace, "*.trace").Length == 0,
                "A prompt is not sent while the terminal is ready but native OpenCode status is unavailable.");
            var compactVisual = ((string Status, System.Windows.Media.Brush Accent,
                System.Windows.Media.Brush Fill, bool Completed))Call(window, "CompactSessionVisual", coordinatorPane)!;
            Require(coordinatorPane.Activity != TerminalActivity.Active && compactVisual.Status == "OpenCode 준비 중" &&
                    !compactVisual.Completed,
                "A live scheduler claim with Unknown native status does not paint the compact session as progressing.");
            await UntilAsync(() => !content.IsReadOnly && start.IsEnabled,
                "automation window to finish the first approval request");

            start.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await UntilAsync(() => Descendants(automationWindow).OfType<TextBlock>()
                    .Any(block => block.Name == "AutomationStartStatus" && block.Text.Contains("기존 지휘자 실행", StringComparison.Ordinal)),
                "a real active native startup claim to guard a second approval");
            var stillActive = (await store.GetAsync(activeRun.Id))!;
            Require((await store.LoadAsync()).Count == 2 && stillActive.State == AutomationState.Running &&
                    stillActive.ActiveDispatchToken == activeRun.ActiveDispatchToken &&
                    FieldValue(window, "_startingNativeAutomation") is not null &&
                    Directory.GetFiles(trace, "*.trace").Length == 0,
                "A live claim remains guarded and no prompt is sent before native readiness.");

            await (Task)Call(window, "RefreshAutomationRunsAsync")!;
            var runList = Field<ListBox>(window, "_automationRunsList");
            object activeListItem = runList.Items.Cast<object>().Single(item =>
                (Guid)GetProperty(GetProperty(item, "Run")!, "Id")! == activeRun.Id);
            runList.SelectedItem = activeListItem;
            await (Task)Call(window, "ControlAutomationAsync", "stop", window)!;
            await UntilAsync(async () => (await store.GetAsync(activeRun.Id))?.State == AutomationState.Stopped &&
                FieldValue(window, "_startingNativeAutomation") is null &&
                FieldValue(window, "_dispatchingAutomationClaim") is null,
                "stop to cancel pending native startup", seconds: 30);
            Require(Directory.GetFiles(trace, "*.trace").Length == 0 &&
                    File.Exists(startupHold),
                "Stopping while OpenCode startup is held sends no native prompt and leaves the hold active for the retry case.");
            string startupDiagnosticsPath = AutomationDiagnostics.LogPath(project, activeRun.Id);
            await UntilAsync(() => File.Exists(startupDiagnosticsPath) &&
                File.ReadAllText(startupDiagnosticsPath, Encoding.UTF8).Contains("stage_preparing", StringComparison.Ordinal),
                "startup preparation diagnostic log");
            string startupDiagnostics = await File.ReadAllTextAsync(startupDiagnosticsPath, Encoding.UTF8);
            Require(startupDiagnostics.Contains("Coordinator", StringComparison.Ordinal) &&
                    startupDiagnostics.Contains("Planning", StringComparison.Ordinal) &&
                    startupDiagnostics.Contains("stage_start_cancelled", StringComparison.Ordinal) &&
                    !startupDiagnostics.Contains("startup_retry_scheduled", StringComparison.Ordinal),
                "startup cancellation diagnostics retain the first Coordinator Planning role without scheduling a retry.");

            File.Delete(startupHold);
            await UntilAsync(() => !content.IsReadOnly && start.IsEnabled,
                "automation window to re-enable after stop");
            start.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            try
            {
                await UntilAsync(async () => (await store.LoadAsync()).Count == 3,
                    "fresh explicit approval after startup stop");
            }
            catch (TimeoutException exception)
            {
                string runs = string.Join(", ", (await store.LoadAsync()).Select(run =>
                    $"{run.Id:N}:{run.State}:{run.ActiveDispatchToken}"));
                string diagnostic = await window.Dispatcher.InvokeAsync(() =>
                {
                    string popupStatus = Descendants(automationWindow).OfType<TextBlock>()
                        .Single(block => block.Name == "AutomationStartStatus").Text;
                    return $"popup={popupStatus}; contentReadOnly={content.IsReadOnly}; startEnabled={start.IsEnabled}; " +
                        $"startingProject={Field<bool>(window, "_startingProjectAutomation")}; " +
                        $"polling={Field<bool>(window, "_automationPolling")}; " +
                        $"startingNative={FieldValue(window, "_startingNativeAutomation")}; " +
                        $"stage={FieldValue(window, "_automationStage")}; dispatching={FieldValue(window, "_dispatchingAutomationClaim")}";
                }).Task;
                throw new TimeoutException(exception.Message + $" Current runs: {runs}. UI state: {diagnostic}", exception);
            }
            AutomationRun retry = (await store.LoadAsync()).Single(run => run.Id != orphan.Id && run.Id != activeRun.Id);
            await UntilAsync(async () => (await store.GetAsync(retry.Id))?.State == AutomationState.Completed,
                "retry after startup stop to complete with native readiness", seconds: 90);
            Require(Directory.GetFiles(trace, "*.trace").Length == 8,
                "The successful retry sends each phase-aware native prompt only after OpenCode becomes ready.");
            automationWindow.Close();
        }
        finally
        {
            try { if (File.Exists(startupHold)) File.Delete(startupHold); } catch (IOException) { }
            Environment.SetEnvironmentVariable("TN_WORKFLOW_TRACE", originalTrace);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_HOLD_FILE", originalHold);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_HELD_SIGNAL", originalHeldSignal);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_HOLD_FILE", originalStartupHold);
            Environment.SetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_TRACE", originalStartupTrace);
            if (window is not null)
            {
                Call(window, "DisposeResources");
                window.Close();
            }
        }
    }

    private static async Task VerifyExpiredClaimRemainsPendingAsync(
        MainWindow window,
        AutomationStore store,
        Guid runId,
        string holdFile,
        string heldSignal)
    {
        await UntilAsync(() => File.Exists(heldSignal) && FieldValue(window, "_automationStage") is not null &&
                               !Field<bool>(window, "_automationPolling"),
            "fake Coordinator to remain busy with a pending native response", seconds: 90);

        object stage = FieldValue(window, "_automationStage")!;
        var claim = (AutomationStepClaim)GetProperty(stage, "Claim")!;
        var pane = (TerminalPane)GetProperty(stage, "Pane")!;
        var monitor = (OpenCodeStatusMonitor)FieldValue(pane, "_openCodeStatus")!;
        await UntilAsync(() => monitor.Snapshot.Kind == OpenCodeStatusKind.Busy &&
                               (bool)GetProperty(pane, "IsNativeAutomationActive")! &&
                               !((CancellationToken)GetProperty(pane, "NativeAutomationCancellation")!).IsCancellationRequested &&
                               !Field<bool>(window, "_automationPolling"),
            "native busy/read-pending status for the claimed Coordinator stage");

        var expiredClaim = claim with
        {
            ClaimedAt = DateTimeOffset.UtcNow.AddDays(-2),
            Options = claim.Options with { TokenBudget = 1, MaxStageMinutes = 1 }
        };
        object expiredStage = CopyAutomationStageWithClaim(stage, expiredClaim);
        SetField(window, "_automationStage", expiredStage);
        var agedLegacyClaim = (AutomationStepClaim)GetProperty(expiredStage, "Claim")!;
        Require(agedLegacyClaim.ClaimedAt < DateTimeOffset.UtcNow.AddDays(-1) &&
                agedLegacyClaim.Options.TokenBudget == 1 && agedLegacyClaim.Options.MaxStageMinutes == 1,
            "the live Coordinator claim is aged beyond any former stage timeout and carries legacy one-minute caps");

        await (Task)Call(window, "PollAutomationAsync")!;
        AutomationRun afterExpiredClaimPoll = (await store.GetAsync(runId))!;
        Require(afterExpiredClaimPoll.State == AutomationState.Running &&
                afterExpiredClaimPoll.ActiveDispatchToken == claim.DispatchToken &&
                FieldValue(window, "_automationStage") is not null &&
                monitor.Snapshot.Kind == OpenCodeStatusKind.Busy &&
                (bool)GetProperty(pane, "IsNativeAutomationActive")! &&
                !((CancellationToken)GetProperty(pane, "NativeAutomationCancellation")!).IsCancellationRequested,
            "an old ClaimedAt does not make Poll fail, stop, abort, or replace a live busy native stage");

        File.Delete(holdFile);
    }

    private static object CopyAutomationStageWithClaim(object stage, AutomationStepClaim claim)
    {
        ConstructorInfo constructor = stage.GetType()
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(AutomationStepClaim)))
            .Single();
        object?[] arguments = constructor.GetParameters().Select(parameter =>
            string.Equals(parameter.Name, "Claim", StringComparison.OrdinalIgnoreCase)
                ? claim
                : GetProperty(stage, parameter.Name!)).ToArray();
        return constructor.Invoke(arguments);
    }

    private static async Task<AutomationRun> SeedLegacyTokenLimitedPausedRunAsync(AutomationStore store)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var legacyPausedRun = new AutomationRun(
            Id: Guid.NewGuid(),
            ProjectDirectory: Path.GetFullPath(store.ProjectDirectory),
            ApprovedPlanSnapshot: "Historical token-limited Plan fixture.",
            Options: new AutomationExecutionOptions(TokenBudget: 1, MaxStageMinutes: 30),
            MaxIterations: 5,
            Iteration: 1,
            State: AutomationState.Paused,
            NextRole: AutomationRole.Coordinator,
            CreatedAt: now.AddDays(-2),
            UpdatedAt: now.AddHours(-1),
            ScheduledFor: null,
            ActiveDispatchToken: null,
            Revision: 4,
            PauseAfterCurrentStage: false,
            TotalTokenUsage: 1,
            LastSummary: "Historical coordinator stage reached its token limit.",
            NextInstruction: "Historical fixture only.",
            StatusDetail: "Token budget reached.");
        var serialization = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        serialization.Converters.Add(new JsonStringEnumConverter());
        string json = JsonSerializer.Serialize(new { Version = 1, Runs = new[] { legacyPausedRun } }, serialization);
        Directory.CreateDirectory(Path.GetDirectoryName(store.StorePath)!);
        await File.WriteAllTextAsync(store.StorePath, json, new UTF8Encoding(false));
        AutomationRun loaded = (await store.LoadAsync()).Single();
        Require(loaded.Id == legacyPausedRun.Id && loaded.State == AutomationState.Paused &&
                loaded.Options.TokenBudget == 1 && loaded.StatusDetail == "Token budget reached.",
            "The legacy token-limited fixture did not load from the on-disk ledger as expected.");
        return loaded;
    }

    private static void WriteFakeOpenCode(string directory)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test executable path is unavailable.");
        File.WriteAllText(Path.Combine(directory, "opencode.cmd"),
            "@echo off\r\n\"" + executable + "\" --fake-opencode %*\r\nexit /b %ERRORLEVEL%\r\n", Encoding.ASCII);
    }

    private static async Task VerifyDefaultOpenCodeLaunchAsync(string project, string fixture)
    {
        string probePath = Path.Combine(fixture, "default-opencode.trace");
        string settingsPath = Path.Combine(fixture, "legacy-settings", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var legacySettings = new
        {
            Workspace = project,
            Document = (string?)null,
            CodexWorkspace = project,
            LaunchDefaultsVersion = 0,
            TerminalProfiles = new object[]
            {
                new { Slot = 1, Tool = (int)TerminalTool.Codex, Title = "프로젝트 작업", Command = (string?)null, WorkingDirectory = project, Mode = (int)TerminalPermissionMode.Auto },
                new { Slot = 2, Tool = (int)TerminalTool.ClaudeCode, Title = "Claude Code", Command = (string?)null, WorkingDirectory = project, Mode = (int)TerminalPermissionMode.Auto }
            }
        };
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(legacySettings), new UTF8Encoding(false));
        Environment.SetEnvironmentVariable("TN_DEFAULT_PROBE", probePath);
        var startupHooks = new OpenCodeStartupHooks(
            _ => Task.FromResult(true),
            () => throw new InvalidOperationException("The ready-path workflow probe must not ask to install OpenCode."),
            _ => throw new InvalidOperationException("The ready-path workflow probe must not invoke the installer."));
        var probeWindow = new MainWindow(settingsPath, autoStartCodex: true, initialWorkspace: project, startupHooks: startupHooks);
        try
        {
            probeWindow.Show();
            var tabs = (TabControl)probeWindow.FindName("TerminalTabs");
            try
            {
                await UntilAsync(() => tabs.Items.Count == 1 && File.Exists(probePath), "default OpenCode launch", seconds: 20);
            }
            catch (TimeoutException exception)
            {
                string diagnostic = await probeWindow.Dispatcher.InvokeAsync(() =>
                {
                    object? state = tabs.Items.Count == 0 ? null : ((TabItem)tabs.Items[0]).Tag;
                    var pane = state is null ? null : (TerminalPane)GetProperty(state, "Pane")!;
                    string paneState = pane is null ? "no pane" :
                        $"pane loaded={pane.IsLoaded}, visible={pane.IsVisible}, size={pane.ActualWidth:0.##}x{pane.ActualHeight:0.##}, " +
                        $"ready={pane.IsReady}, running={pane.IsRunning}, failed={Field<bool>(pane, "_failed")}, " +
                        $"pending={FieldValue(pane, "_pendingWebReadyDimensions")}, notice={Field<TextBlock>(pane, "_notice").Text}";
                    return $"tabs={tabs.Items.Count}; startupBusy={Field<bool>(probeWindow, "_openCodeStartupBusy")}; " +
                        $"openCodeReady={Field<bool>(probeWindow, "_openCodeReady")}; windowLoaded={probeWindow.IsLoaded}; " +
                        $"windowVisible={probeWindow.IsVisible}; windowSize={probeWindow.ActualWidth:0.##}x{probeWindow.ActualHeight:0.##}; " +
                        $"status={((TextBlock)probeWindow.FindName("StatusLabel")).Text}; {paneState}";
                }).Task;
                throw new TimeoutException(exception.Message + " Current UI state: " + diagnostic, exception);
            }
            object state = ((TabItem)tabs.Items[0]).Tag;
            var pane = (TerminalPane)GetProperty(state, "Pane")!;
            Require(!pane.LaunchesCodex, "The OpenCode default terminal was marked as a direct Codex launch.");
            Require((TerminalTool)GetProperty(state, "Tool")! == TerminalTool.OpenCode, "The default terminal did not migrate to OpenCode.");
            Require((string)GetProperty(state, "Title")! == "프로젝트 작업", "A custom legacy profile title was not preserved.");
            string trace = await File.ReadAllTextAsync(probePath, Encoding.UTF8);
            Require(trace.StartsWith(project + "\n", StringComparison.OrdinalIgnoreCase) && trace.EndsWith("--auto", StringComparison.Ordinal),
                "The migrated default did not start opencode --auto in the configured working directory.");
            var profiles = Field<List<TerminalLaunchProfile>>(probeWindow, "_terminalProfiles");
            TerminalLaunchProfile migratedBuiltIn = profiles.Single(profile => profile.Slot == 2);
            Require(migratedBuiltIn.Tool == TerminalTool.OpenCode && migratedBuiltIn.Title == "Claude Code" &&
                    migratedBuiltIn.Mode == TerminalPermissionMode.Standard && migratedBuiltIn.WorkingDirectory is null &&
                    Field<bool>(probeWindow, "_useAutoMode"),
                "Legacy slot-one Auto mode migrates to the global setting while profile-specific folders and modes are removed.");
            using JsonDocument saved = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            Require(saved.RootElement.GetProperty("LaunchDefaultsVersion").GetInt32() == 2,
                "OpenCode-only profile normalization was not persisted as version 2.");
        }
        finally
        {
            Call(probeWindow, "DisposeResources");
            probeWindow.Close();
            Environment.SetEnvironmentVariable("TN_DEFAULT_PROBE", null);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string name, int seconds = 30) =>
        await UntilAsync(() => Task.FromResult(condition()), name, seconds);

    private static async Task UntilAsync(Func<Task<bool>> condition, string name, int seconds = 30)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for " + name + ".");
    }

    private static T Field<T>(object target, string name) =>
        (T)(FieldValue(target, name) ?? throw new InvalidOperationException("Null field: " + name));

    private static object? FieldValue(object target, string name)
    {
        if (target is MainWindow window)
        {
            var runtimeNames = new Dictionary<string, string> { ["_automationTeam"] = "Team", ["_automationStage"] = "Stage", ["_startingNativeAutomation"] = "StartingNative", ["_dispatchingAutomationClaim"] = "Dispatching", ["_startingProjectAutomation"] = "StartingRun" };
            if (runtimeNames.TryGetValue(name, out var mapped))
            {
                var runtimes = ((System.Collections.IDictionary)typeof(MainWindow).GetField("_automationRuntimes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Values.Cast<object>().ToArray();
                object? Read(object runtime) => runtime.GetType().GetField(mapped)?.GetValue(runtime) ?? runtime.GetType().GetProperty(mapped)?.GetValue(runtime);
                if (name == "_startingProjectAutomation") return runtimes.Any(runtime => Read(runtime) is true);
                return runtimes.Select(Read).FirstOrDefault(value => value is not null && (value is not System.Collections.IDictionary dictionary || dictionary.Count > 0));
            }
            if (name == "_automationPolling") return typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        }
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        return field.GetValue(target);
    }

    private static void SetField(object target, string name, object? value)
    {
        if (target is MainWindow window && name == "_automationStage" && value is not null)
        {
            var claim = (AutomationStepClaim)GetProperty(value, "Claim")!;
            target = Call(window, "RuntimeFor", claim.ProjectDirectory)!;
            name = "Stage";
        }
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }
    private static object? Call(object target, string name, params object?[] args)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        return method.Invoke(target, args);
    }

    private static void SetProperty(object target, string name, object value) =>
        (target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().FullName, name)).SetValue(target, value);

    private static object? GetProperty(object target, string name) =>
        (target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().FullName, name)).GetValue(target);

    private static void CaptureVisual(Window window, string path)
    {
        window.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
