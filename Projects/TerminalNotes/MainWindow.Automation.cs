using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    // Starting and advancing OpenCode must not depend on the Win32 input queue becoming idle.
    private readonly DispatcherTimer _automationTimer = new(DispatcherPriority.Loaded) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _automationInitializationRetryTimer = new(DispatcherPriority.Loaded) { Interval = TimeSpan.FromSeconds(5) };
    private readonly HashSet<string> _automationProjects = new(StringComparer.OrdinalIgnoreCase);


    private FileStream? _automationOwnership;
    private Window? _automationWindow;
    private ListBox? _automationRunsList;
    private TextBlock? _automationRunStatus;
    private bool _automationInitialized, _automationInitializationInProgress;
    private bool _automationTimerHandlerRegistered, _automationRetryHandlerRegistered;
    private string? _automationInitializationError;

    private readonly Dictionary<Guid, AutomationRun> _knownAutomationRuns = [];
    private readonly Dictionary<TerminalPane, long> _automationPaneTokens = [];
    private readonly HashSet<TerminalPane> _automationPanesToReplace = [];
    private Guid? _preferredAutomationRunId;
    private int _automationRunRefreshDepth;
    private string AutomationProjectsPath => _settingsPath + ".automation-projects.json";

    private sealed record ActiveAutomationStage(AutomationStore Store, AutomationStepClaim Claim, AutomationCommand Command,
        TerminalPane Pane, Guid? JournalId, OpenCodeAutomationClient Client, OpenCodeAutomationDispatch Dispatch)
    {
        public OpenCodeStatusKind? LastObservedStatus { get; set; }
        public DateTimeOffset LastDiagnosticHeartbeat { get; set; }
        public bool ResultReady { get; set; }
    }
    private sealed record AutomationListItem(AutomationRun Run, string ProjectName)
    {
        public override string ToString() => $"{ProjectName} · {AutomationStateLabel(Run.State)} · {Run.CreatedAt.ToLocalTime():MM-dd HH:mm}";
    }
    private static string RoleLabel(AutomationRole role) => role switch { AutomationRole.Coordinator => "지휘", AutomationRole.Implementer => "구현", _ => "검토" };
    private static string WorkflowPhaseLabel(AutomationWorkflowPhase phase) => phase switch
    {
        AutomationWorkflowPhase.Planning => "계획 보완",
        AutomationWorkflowPhase.PlanReview => "계획 검토",
        AutomationWorkflowPhase.Implementation => "구현·수정",
        AutomationWorkflowPhase.Verification => "결과 검증",
        AutomationWorkflowPhase.Delivery => "서비스 실행",
        AutomationWorkflowPhase.FinalVerification => "실행 확인",
        _ => "준비"
    };
    private static string AutomationStateLabel(AutomationState state) => state switch
    {
        AutomationState.Draft => "초안", AutomationState.Scheduled => "예약", AutomationState.Running => "실행 중", AutomationState.Paused => "일시정지",
        AutomationState.Stopped => "중단", AutomationState.Completed => "완료", _ => "실패"
    };

    private async void InitializeAutomation()
    {
        await TryInitializeAutomationAsync();
    }

    private async Task<bool> TryInitializeAutomationAsync()
    {
        if (_closing || _resourcesDisposed) return false;
        if (_automationInitialized && _automationOwnership is not null) return true;
        if (_automationInitializationInProgress)
        {
            EnsureAutomationInitializationRetryTimer();
            return false;
        }
        if (_closePending)
        {
            EnsureAutomationInitializationRetryTimer();
            _automationInitializationRetryTimer.Start();
            return false;
        }

        _automationInitializationInProgress = true;
        FileStream? ownershipCandidate = null;
        try
        {
            EnsureAutomationInitializationRetryTimer();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!);
            // Only one app instance may drive this user's scheduler; the OS releases the lock on crash.
            try
            {
                ownershipCandidate = new FileStream(_settingsPath + ".automation.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (AppInstanceCoordinator.IsSharingViolation(ex))
            {
                throw new InvalidOperationException("이미 열린 Terminal Notes 창이 자동화를 관리하고 있습니다. 트레이의 기존 창을 사용하거나 해당 창의 메뉴에서 종료하면 자동으로 다시 연결합니다.", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("자동화 잠금 파일 접근 실패 · " + _settingsPath + ".automation.lock · " + ex.Message, ex);
            }

            if (File.Exists(AutomationProjectsPath))
                foreach (string project in JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(AutomationProjectsPath)) ?? [])
                    if (Path.IsPathFullyQualified(project)) _automationProjects.Add(project);
            foreach (var project in _automationProjects)
                if (Directory.Exists(project))
                    await StopLegacyRoleRunsAsync(project);

            if (_closing || _closePending) return false;
            await RefreshAutomationRunsAsync();
            if (_closing || _closePending) return false;

            if (!_automationTimerHandlerRegistered)
            {
                _automationTimer.Tick += async (_, _) => await PollAutomationAsync();
                _automationTimerHandlerRegistered = true;
            }
            _automationOwnership = ownershipCandidate;
            ownershipCandidate = null;
            _automationInitialized = true;
            _automationInitializationError = null;
            _automationInitializationRetryTimer.Stop();
            _automationTimer.Start();
            RecordAutomationDiagnostic("scheduler_ready", "자동화 상태 감시를 시작했습니다.");
            return true;
        }
        catch (Exception ex)
        {
            _automationOwnership?.Dispose();
            _automationOwnership = null;
            _automationInitialized = false;
            string reason = ex.Message;
            if (string.IsNullOrWhiteSpace(reason)) reason = ex.GetType().Name;
            if (reason.Length > 240) reason = reason[..240];
            bool changed = !string.Equals(_automationInitializationError, reason, StringComparison.Ordinal);
            _automationInitializationError = reason;
            RecordAutomationDiagnostic("scheduler_initialization_failed", reason, severity: "error", exception: ex, deduplicate: true);
            if (changed && !_closing) SetStatus("자동화 예약을 초기화할 수 없습니다 · " + reason);
            return false;
        }
        finally
        {
            ownershipCandidate?.Dispose();
            _automationInitializationInProgress = false;
            if (!_automationInitialized && !_closing)
            {
                EnsureAutomationInitializationRetryTimer();
                _automationInitializationRetryTimer.Start();
            }
            if (_windowInitialized && !_closing && !_resourcesDisposed)
            {
                try { UpdateWorkbenchTarget(); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
            }
        }
    }

    private void EnsureAutomationInitializationRetryTimer()
    {
        if (_automationRetryHandlerRegistered) return;
        _automationInitializationRetryTimer.Tick += async (_, _) => await TryInitializeAutomationAsync();
        _automationRetryHandlerRegistered = true;
    }

    private bool IsAutomationTerminalBusy(TerminalPane pane) =>
        _automationRuntimes.Values.Any(runtime => runtime.Stage?.Pane == pane || runtime.StartingNative?.Pane == pane ||
            runtime.MicroStages.Values.Any(micro => micro.Pane == pane));

    private bool HasLiveAutomationClaim(AutomationRun run)
    {
        var runtime = RuntimeFor(run.ProjectDirectory);
        return runtime.Stage?.Claim.RunId == run.Id || runtime.StartingNative?.RunId == run.Id ||
            runtime.Dispatching?.RunId == run.Id || runtime.Pending.Any(pending => pending.Claim.RunId == run.Id);
    }
    private void EnqueueAutomationClaim(AutomationStore store, AutomationStepClaim claim, bool prioritizeRun)
    {
        var runtime = RuntimeFor(claim.ProjectDirectory);
        if (runtime.Pending.Any(pending => pending.Claim.DispatchToken == claim.DispatchToken)) return;
        if (!prioritizeRun)
        {
            runtime.Pending.Enqueue((store, claim));
            RecordAutomationDiagnostic("stage_queued", $"{RoleLabel(claim.Role)} · {WorkflowPhaseLabel(claim.WorkflowPhase)} 단계 대기", claim);
            return;
        }

        // A user-approved run and its immediately following stages stay together.
        // Older claims remain queued, but cannot make a different project's Reviewer
        // look like the first stage of the run the user just started.
        var waiting = runtime.Pending.ToArray();
        runtime.Pending.Clear();
        runtime.Pending.Enqueue((store, claim));
        foreach (var pending in waiting) runtime.Pending.Enqueue(pending);
        RecordAutomationDiagnostic("stage_queued", $"{RoleLabel(claim.Role)} · {WorkflowPhaseLabel(claim.WorkflowPhase)} 단계 대기", claim);
    }

    private ResolvedTerminalLaunch ResolveAutomationLaunch(string project, string title) =>
        new(false, TerminalLaunchModes.Command(TerminalTool.OpenCode,
            _useAutoMode ? TerminalPermissionMode.Auto : TerminalPermissionMode.Standard), title, project, TerminalTool.OpenCode);

    private bool HasReadyCompactAutomationTeam(string project) =>
        _compactTerminal is not null && Enum.GetValues<AutomationRole>().All(role =>
            RuntimeFor(project).Team.TryGetValue(role, out var tab) && TerminalTabs.Items.Contains(tab) &&
            tab.Tag is TerminalTabState { ExitCode: null } state && state.Pane.IsReady && state.Pane.IsRunning);

    private Dictionary<AutomationRole, TabItem> EnsureAutomationTeam(string project)
    {
        if (!IsMultiProjectTask(project)) throw new InvalidOperationException("역할 팀 대신 프로젝트의 싱글 터미널을 사용하세요.");
        project = CanonicalProjectPath(project);
        if (_autoStartCodex && !_openCodeReady) throw new InvalidOperationException("OpenCode 설치를 먼저 확인하세요.");
        if (_closing || _closePending) throw new InvalidOperationException("앱 종료 처리 중에는 팀을 만들 수 없습니다.");
        if (!Directory.Exists(project)) throw new DirectoryNotFoundException(project);
        var runtime = RuntimeFor(project);
        bool arranging = _arrangingTerminals;
        if (HasExternalTerminal(project))
        {
            if (runtime.Team.Count > 0) return runtime.Team;
            throw new InvalidOperationException(ExternalTerminalBlockReason);
        }
        _arrangingTerminals = true;
        try
        {
            foreach (var role in ProjectTaskRoles)
            {
                if (runtime.Team.TryGetValue(role, out var old) && old.Tag is TerminalTabState oldState)
                {
                    if (TerminalTabs.Items.Contains(old) && oldState.ExitCode is null &&
                        !oldState.Pane.NativeAutomationDiagnostic.Failed && !oldState.Pane.NativeAutomationDiagnostic.Disposed &&
                        (!oldState.Pane.IsReady || oldState.Pane.IsRunning) && !_automationPanesToReplace.Contains(oldState.Pane)) continue;
                    DetachTaskTerminal(old);
                    TerminalTabs.Items.Remove(old);
                    _automationPanesToReplace.Remove(oldState.Pane);
                    _automationPaneTokens.Remove(oldState.Pane);
                    DisposeSafely(oldState.Pane);
                }
                string title = RoleLabel(role);
                var tab = CreateTerminalTab(NextTerminalSlot(), ResolveAutomationLaunch(project, title), title);
                ((TerminalTabState)tab.Tag).Pane.UseDedicatedAutomationSession();
                tab.Content = null;
                runtime.Team[role] = tab;
                TerminalTabs.Items.Add(tab);
            }
        }
        finally { _arrangingTerminals = arranging; }
        AttachProjectTaskTerminals(runtime);
        UpdateTerminalCount();
        return runtime.Team;
    }

    private async Task PollAutomationAsync()
    {
        if (_closing || _closePending || _automationOwnership is null) return;
        // A slow server or startup in one project must not block another project.
        var projects = _automationProjects.Where(IsMultiProjectTask).ToArray();
        await Task.WhenAll(projects.SelectMany(project =>
        {
            var runtime = RuntimeFor(project);
            return new[] { PollAutomationProjectAsync(runtime), PollMicroManagementAsync(runtime) };
        }));
    }
    private async Task PollAutomationProjectAsync(AutomationProjectRuntime runtime)
    {
        if (_closing || _closePending || runtime.Polling || runtime.StartingRun ||
            _projectTaskControlWrites.Contains(runtime.Project) || HasExternalTerminal(runtime.Project) || _automationOwnership is null) return;
        runtime.Polling = true;
        try
        {
            await RecoverUnownedProjectTaskAsync(runtime);
            if (runtime.Stage is { } stage)
            {
                var nativeStatus = stage.Pane.NativeOpenCodeStatus;
                if (stage.LastObservedStatus != nativeStatus)
                {
                    stage.LastObservedStatus = nativeStatus;
                    RecordAutomationDiagnostic("native_status_changed", NativeAutomationStatusDetail(nativeStatus), stage.Claim,
                        pane: stage.Pane, dispatch: stage.Dispatch,
                        severity: nativeStatus == OpenCodeStatusKind.Unknown ? "warning" : "info");
                }
                if (DateTimeOffset.UtcNow - stage.LastDiagnosticHeartbeat >= TimeSpan.FromSeconds(30))
                {
                    stage.LastDiagnosticHeartbeat = DateTimeOffset.UtcNow;
                    RecordAutomationDiagnostic("stage_observed", "단계 감시 중 · " + NativeAutomationStatusDetail(nativeStatus), stage.Claim,
                        pane: stage.Pane, dispatch: stage.Dispatch);
                }
                var run = await stage.Store.GetAsync(stage.Claim.RunId);
                if (run?.State == AutomationState.Stopped || stage.Pane.NativeAutomationCancellation.IsCancellationRequested)
                {
                    if (stage.Pane.NativeAutomationCancellation.IsCancellationRequested)
                        await StopCancelledAutomationClaimAsync(stage.Store, stage.Claim);
                    await RetireStoppedStageAsync(stage);
                }
                else
                {
                    AutomationStageOutput? result;
                    try
                    {
                        result = await stage.Client.ReadCompletionAsync(stage.Dispatch, stage.Pane.NativeAutomationCancellation);
                        stage.Pane.ObserveOwnedAutomationSessions(stage.Client.ObservedSessionIds);
                        if (stage.Pane.IsRunning)
                            await stage.Client.RefreshTuiSessionAsync(stage.Dispatch,
                                () => Dispatcher.Invoke(() => ReferenceEquals(runtime.Stage, stage) && !_closing && !_closePending &&
                                    stage.Pane.IsNativeAutomationActive &&
                                    (!_knownAutomationRuns.TryGetValue(stage.Claim.RunId, out var ownedRun) ||
                                     (ownedRun.State == AutomationState.Running && !ownedRun.PauseAfterCurrentStage && ownedRun.ActiveDispatchToken == stage.Claim.DispatchToken))),
                                stage.Pane.NativeAutomationCancellation);
                        if (stage.Client.LastReadWarning is { } warning)
                            RecordAutomationDiagnostic("completion_read_retry", warning, stage.Claim, pane: stage.Pane,
                                severity: "warning", dispatch: stage.Dispatch, deduplicate: true);
                    }
                    catch (Exception ex)
                    {
                        // Snapshot reads are repeatable. Keep ownership of this exact
                        // session/message and retry the read instead of turning a
                        // temporary transport error into a stage failure.
                        result = null;
                        RecordAutomationDiagnostic("completion_read_failed", "결과 조회 재시도 · " + ex.Message, stage.Claim,
                            pane: stage.Pane, severity: "warning", exception: ex, dispatch: stage.Dispatch, deduplicate: true);
                        SetStatus("OpenCode 결과 확인 재시도 중 · " + ex.Message);
                    }
                    if (!ReferenceEquals(runtime.Stage, stage) || _closing) return;
                    stage.ResultReady = result is not null;
                    if (result is null && runtime.RequestedIntervention is { } intervention &&
                        run is { State: AutomationState.Running, PauseAfterCurrentStage: false })
                    {
                        result = await stage.Client.RequestCoordinatorInterventionAsync(stage.Dispatch, intervention,
                            () => Dispatcher.Invoke(() => ReferenceEquals(runtime.Stage, stage) && !_closing && !_closePending &&
                                stage.Pane.IsNativeAutomationActive &&
                                (!_knownAutomationRuns.TryGetValue(stage.Claim.RunId, out var current) ||
                                 (current.State == AutomationState.Running && !current.PauseAfterCurrentStage && current.ActiveDispatchToken == stage.Claim.DispatchToken))),
                            stage.Pane.NativeAutomationCancellation);
                    }
                    if (stage.Pane.NativeAutomationCancellation.IsCancellationRequested)
                    {
                        await StopCancelledAutomationClaimAsync(stage.Store, stage.Claim);
                        await RetireStoppedStageAsync(stage);
                        UpdateAutomationProgressPanel();
                        return;
                    }
                    if (result is null && (stage.Client.IsStalled || stage.Client.IsRecovering) &&
                        run is { State: AutomationState.Running, PauseAfterCurrentStage: false } &&
                        run.ActiveDispatchToken == stage.Claim.DispatchToken)
                    {
                        RecordAutomationDiagnostic(stage.Client.IsCoordinatorIntervention ? "coordinator_intervention" : "stage_stalled",
                            stage.Client.IsCoordinatorIntervention ? "지휘자의 수정 지시 · 현재 작업 중단 확인 중" : "실제 진행 정체 · 해당 역할 중단 후 복구 확인 중", stage.Claim,
                            pane: stage.Pane, dispatch: stage.Dispatch, severity: "warning", deduplicate: true);
                        result = await stage.Client.RecoverStalledAsync(stage.Dispatch,
                            () => Dispatcher.Invoke(() => ReferenceEquals(runtime.Stage, stage) && !_closing && !_closePending &&
                                stage.Pane.IsNativeAutomationActive &&
                                (!_knownAutomationRuns.TryGetValue(stage.Claim.RunId, out var current) ||
                                 (current.State == AutomationState.Running && !current.PauseAfterCurrentStage &&
                                  current.ActiveDispatchToken == stage.Claim.DispatchToken))),
                            stage.Pane.NativeAutomationCancellation);
                        if (stage.Client.LastReadWarning is { } recoveryWarning)
                            RecordAutomationDiagnostic("recovery_waiting", recoveryWarning, stage.Claim,
                                pane: stage.Pane, dispatch: stage.Dispatch, severity: "warning", deduplicate: true);
                    }
                    // Disconnection is not completion: even a prolonged outage
                    // requires terminating our owned job before any handoff.
                    if (!ReferenceEquals(runtime.Stage, stage) || stage.Pane.NativeAutomationCancellation.IsCancellationRequested || _closing) return;
                    stage.Pane.SetNativeAutomationSupervisionNotice(stage.Client.IsCoordinatorIntervention ? "지휘 지시로 중단 확인 중" :
                        stage.Client.SupervisionNotice);
                    if (result is null && (!stage.Pane.IsRunning || stage.Client.CanRestartStalledProcess || stage.Client.CanRestartUnresponsiveProcess) &&
                        run is { State: AutomationState.Running, PauseAfterCurrentStage: false } &&
                        run.ActiveDispatchToken == stage.Claim.DispatchToken && !_closePending &&
                        (!_knownAutomationRuns.TryGetValue(run.Id, out var latestRun) ||
                         (latestRun.State == AutomationState.Running && !latestRun.PauseAfterCurrentStage && latestRun.ActiveDispatchToken == stage.Claim.DispatchToken)) &&
                        await stage.Pane.StopNativeAutomationProcessAsync())
                    {
                        _automationPanesToReplace.Add(stage.Pane);
                        // The compact modal owns a pane and defers team layout.
                        // Restore its view so a dead role can be replaced and the
                        // supervisor does not wait forever for the user to close it.
                        if (_compactTerminal is { } compact && runtime.Team.Values.Any(tab =>
                            tab.Tag is TerminalTabState state && ReferenceEquals(state.Pane, compact.Pane)))
                            compact.Close();
                        result = new(false, "역할 프로세스 종료 확인 · 현재 변경 상태부터 복구합니다.", "", false, stage.Client.ObservedTokens) { Interrupted = true };
                        RecordAutomationDiagnostic("role_process_recovered", result.Summary, stage.Claim,
                            pane: stage.Pane, dispatch: stage.Dispatch, severity: "warning");
                    }
                    if (!ReferenceEquals(runtime.Stage, stage) || stage.Pane.NativeAutomationCancellation.IsCancellationRequested || _closing) return;
                    if (result is not null)
                    {
                        stage.ResultReady = true;
                        // A role pane cannot be reused while its read-only checkpoint
                        // still owns a native request. Finish that request first.
                        if (runtime.MicroPolling || runtime.MicroStages.Count > 0) return;
                        // Persist the result before advancing; screen activity is never a completion signal.
                        var completed = await stage.Store.CompleteStepAsync(new(stage.Claim.RunId, stage.Claim.DispatchToken, stage.Claim.Revision, result.Success, result.Summary, result.Instruction, result.Complete, result.Tokens, DateTimeOffset.UtcNow)
                        {
                            PlanApproved = result.PlanApproved,
                            DeliveryEvidence = result.DeliveryEvidence,
                            PlanningPlan = result.PlanningPlan,
                            Interrupted = result.Interrupted || (!result.Success && stage.Claim.Role == AutomationRole.Implementer)
                        });
                        // Take ownership before any cleanup, artifact, or UI operation can fail.
                        if (completed.NextClaim is { } next) EnqueueAutomationClaim(stage.Store, next, prioritizeRun: true);
                        _knownAutomationRuns[completed.Run.Id] = completed.Run;
                        bool validStage = completed.Accepted && result.Success && completed.Run.CurrentRoleRetryCount == 0;
                        RecordAutomationDiagnostic(validStage ? "stage_completed" : "stage_failed",
                            validStage ? $"{RoleLabel(stage.Claim.Role)} · {WorkflowPhaseLabel(stage.Claim.WorkflowPhase)} 결과 저장 완료"
                                : completed.RejectionReason ?? completed.Run.StatusDetail ?? result.Summary,
                            stage.Claim, pane: stage.Pane, dispatch: stage.Dispatch, severity: validStage ? "info" : "error");
                        if (completed.Accepted)
                            RecordAutomationDiagnostic(completed.Run.State == AutomationState.Completed ? "run_completed" : "workflow_transition",
                                completed.Run.State == AutomationState.Completed ? "실제 서비스 실행과 검토 확인을 마쳤습니다."
                                    : $"다음: {RoleLabel(completed.Run.NextRole)} · {WorkflowPhaseLabel(completed.Run.WorkflowPhase)} · {AutomationStateLabel(completed.Run.State)}"
                                        + (completed.Run.CurrentRoleRetryCount > 0 ? $" · 재시도 {completed.Run.CurrentRoleRetryCount}회" : ""),
                                run: completed.Run);
                        runtime.Stage = null;
                        stage.Client.Dispose();
                        stage.Pane.SetAutomationBusy(false);
                        if (completed.Accepted)
                        {
                            try
                            {
                                await File.WriteAllTextAsync(stage.Command.ResultPath, JsonSerializer.Serialize(result));
                                await File.WriteAllTextAsync(stage.Command.CompletionPath, JsonSerializer.Serialize(new
                                {
                                    source = "opencode-native-session", success = result.Success,
                                    dispatch = stage.Dispatch, completedAt = DateTimeOffset.UtcNow
                                }));
                            }
                            catch (Exception artifactError) when (artifactError is IOException or UnauthorizedAccessException)
                            {
                                RecordAutomationDiagnostic("result_artifact_failed", "단계 결과 부가 파일 저장 실패 · " + artifactError.Message,
                                    stage.Claim, severity: "warning", exception: artifactError, dispatch: stage.Dispatch);
                            }
                            // Usage comes from the exact native assistant messages, never console text.
                            long paneTotal = _automationPaneTokens.GetValueOrDefault(stage.Pane) + result.Tokens;
                            _automationPaneTokens[stage.Pane] = paneTotal;
                            stage.Pane.ObserveTokenUsage("\n" + JsonSerializer.Serialize(new { type = "token_count", total_tokens = paneTotal }) + "\n");
                            if (validStage) stage.Pane.MarkWorkCompleted();
                            else
                            {
                                stage.Pane.MarkWorkFailed();
                                if (!stage.Pane.IsRunning) _automationPanesToReplace.Add(stage.Pane);
                            }
                        }
                        else stage.Pane.MarkWorkFailed();
                        string journalWarning = "";
                        if (completed.Accepted && stage.JournalId is Guid journalId)
                        {
                            try { await new ProjectWorkJournal(stage.Claim.ProjectDirectory).UpdateStatusAsync(journalId, validStage ? WorkJournalStatus.Completed : WorkJournalStatus.Failed, result.Summary); }
                            catch (Exception journalError)
                            {
                                journalWarning = " · MD 갱신 실패 (실행 결과 보존됨): " + journalError.Message;
                                RecordAutomationDiagnostic("journal_update_failed", journalWarning, stage.Claim, severity: "warning", exception: journalError);
                            }
                        }
                        SetStatus(completed.Run.State == AutomationState.Completed
                            ? $"완료 · {ProjectDisplayName(stage.Claim.ProjectDirectory)}{journalWarning}"
                            : $"{RoleLabel(stage.Claim.Role)} · {WorkflowPhaseLabel(stage.Claim.WorkflowPhase)} · {AutomationStateLabel(completed.Run.State)}{journalWarning}");
                        RefreshConversationHistory();
                        UpdateAutomationProgressPanel();
                        if (completed.Accepted && completed.Run.State == AutomationState.Completed &&
                            stage.Claim.Role == AutomationRole.Reviewer)
                            await PresentAutomationCompletionAsync(completed.Run);
                    }
                }
            }
            if (!_closePending && runtime.Stage is null && runtime.Pending.Count == 0)
            {
                foreach (string project in new[] { runtime.Project })
                {
                    if (!Directory.Exists(project)) continue;
                    var store = new AutomationStore(project);
                    var claims = await store.ClaimDueAsync(DateTimeOffset.UtcNow, maxClaims: 1);
                    foreach (var claim in claims) EnqueueAutomationClaim(store, claim, prioritizeRun: false);
                    if (runtime.Pending.Count > 0) break;
                }
            }
            if (!_closePending && runtime.Stage is null && runtime.Pending.TryPeek(out var pending) &&
                (!_arrangingTerminals || _compactTerminal is not null))
            {
                runtime.Pending.Dequeue();
                await DispatchAutomationAsync(pending.Store, pending.Claim);
            }
            await RefreshAutomationRunsAsync();
            UpdateWorkbenchTarget();
            UpdateCompactDashboard();
        }
        catch (Exception ex)
        {
            // Keep ownership of the current stage/queued claim. A transient read or UI
            // failure must not silently disable monitoring for the rest of this app lifetime.
            SetStatus("자동화 상태 확인 실패 · 재확인 중 · " + ex.Message);
            RecordAutomationDiagnostic("scheduler_poll_failed", "자동화 상태 재확인 중 · " + ex.Message,
                runtime.Stage?.Claim ?? runtime.Dispatching, severity: "error", exception: ex, deduplicate: true);
            if (_automationRunStatus is not null) _automationRunStatus.Text = "자동화 상태 재확인 중 · " + ex.Message;
        }
        finally { runtime.Polling = false; }
    }

    private async Task DispatchAutomationAsync(AutomationStore store, AutomationStepClaim claim)
    {
        var runtime = RuntimeFor(claim.ProjectDirectory);
        runtime.Dispatching = claim;
        Guid? journalId = null;
        OpenCodeAutomationClient? client = null;
        TerminalPane? ownedPane = null;
        try
        {
            if (_closePending) { EnqueueAutomationClaim(store, claim, prioritizeRun: true); return; }
            var run = await store.GetAsync(claim.RunId);
            if (run?.ActiveDispatchToken != claim.DispatchToken || run.State != AutomationState.Running) return;
            _knownAutomationRuns[run.Id] = run;
            // A modal temporarily owns a pane. Existing ready role panes can receive work
            // without changing selection, rebuilding the team, or reparenting any view.
            bool compactDispatch = _arrangingTerminals && HasReadyCompactAutomationTeam(claim.ProjectDirectory);
            if (_arrangingTerminals && _compactTerminal is null)
            {
                EnqueueAutomationClaim(store, claim, prioritizeRun: true);
                return;
            }
            var team = compactDispatch ? runtime.Team : EnsureAutomationTeam(claim.ProjectDirectory);
            var tab = team[claim.Role]; var state = (TerminalTabState)tab.Tag;
            ownedPane = state.Pane;
            runtime.StartingNative = (claim.RunId, state.Pane);
            RecordAutomationDiagnostic("stage_preparing", $"{RoleLabel(claim.Role)} · {WorkflowPhaseLabel(claim.WorkflowPhase)} OpenCode 준비 대기", claim, pane: state.Pane);
            UpdateAutomationProgressPanel();
            SetStatus($"OpenCode {RoleLabel(claim.Role)} · {WorkflowPhaseLabel(claim.WorkflowPhase)} 준비 중");
            if (!compactDispatch && IsDisplayedTaskProject(claim.ProjectDirectory)) SelectTerminal(tab, false);
            // Await the actual view/ConPTY handoff, then the native OpenCode idle
            // baseline. A selected tab or live PowerShell host is not readiness.
            var end = DateTimeOffset.UtcNow.AddSeconds(45);
            using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                state.Pane.NativeAutomationCancellation, _startupCancellation.Token);
            startupCancellation.CancelAfter(TimeSpan.FromSeconds(45));
            await state.Pane.EnsureReadyAsync(startupCancellation.Token);
            RecordAutomationDiagnostic("terminal_ready", "역할 터미널 준비됨 · OpenCode 대기 상태 확인 중", claim, pane: state.Pane);
            while (!state.Pane.CanStartNativeAutomation &&
                   state.ExitCode is null && !_closing && !_closePending &&
                   !state.Pane.NativeAutomationCancellation.IsCancellationRequested)
            {
                var readiness = state.Pane.NativeAutomationDiagnostic;
                state.Pane.ObserveStartupReadiness();
                if (state.Pane.NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput ||
                    readiness.HasPendingInput || readiness.ManualOverride)
                {
                    // A user's draft or native approval is not a failed launch.
                    // Preserve that pane while waiting instead of replacing it on
                    // the next scheduled startup attempt.
                    end = DateTimeOffset.UtcNow.AddSeconds(45);
                    RecordAutomationDiagnostic("startup_input_wait", "역할 터미널 입력·승인 확인 대기", claim,
                        pane: state.Pane, deduplicate: true);
                }
                else if (DateTimeOffset.UtcNow >= end) break;
                await Task.Delay(100);
            }
            state.Pane.NativeAutomationCancellation.ThrowIfCancellationRequested();
            if (_closing || _closePending)
            {
                if (!_closing) EnqueueAutomationClaim(store, claim, prioritizeRun: true);
                return;
            }
            if (!state.Pane.CanStartNativeAutomation || state.Pane.NativeOpenCodeEndpoint is null)
            {
                // Repeated readiness timeouts on an unresponsive owned role must
                // not reuse the same broken local service forever. No prompt has
                // been submitted here; preserve actual approvals and user input.
                var startupRun = await store.GetAsync(claim.RunId);
                if (startupRun is { State: AutomationState.Running, PauseAfterCurrentStage: false } &&
                    startupRun.ActiveDispatchToken == claim.DispatchToken && !_closing && !_closePending &&
                    runtime.StartingNative?.Pane == state.Pane &&
                    await state.Pane.StopUnresponsiveStartupProcessAsync())
                {
                    _automationPanesToReplace.Add(state.Pane);
                    throw new InvalidOperationException("반복된 서비스 준비 실패 · 해당 역할 프로세스를 종료했고 새 터미널로 자동 재시작합니다.");
                }
                // OpenCode may have been updated after this app's initial version check.
                // Diagnose a failed launch again; never repair the installation or send
                // a prompt merely because an executable exists on PATH.
                string reason = $"{RoleLabel(claim.Role)} OpenCode를 시작하거나 대기 상태를 확인하지 못했습니다.";
                if (!_closing)
                {
                    using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        state.Pane.NativeAutomationCancellation, _startupCancellation.Token);
                    probeCancellation.CancelAfter(TimeSpan.FromSeconds(20));
                    try
                    {
                        var detection = await DetectOpenCodeAsync(probeCancellation.Token);
                        RecordAutomationDiagnostic("startup_installation_check", detection.Message, claim,
                            pane: state.Pane, severity: detection.IsInstalled ? "info" : "error");
                        reason += detection.IsInstalled
                            ? $" 실행 파일 확인됨 · native 상태 {state.Pane.NativeAutomationDiagnostic.NativeStatus} · 연결·입력 상태를 확인하세요."
                            : " 설치 손상 또는 실행 불가 · " + detection.Message;
                    }
                    catch (OperationCanceledException) when (!state.Pane.NativeAutomationCancellation.IsCancellationRequested &&
                                                              !_startupCancellation.IsCancellationRequested)
                    {
                        reason += " 실행 파일 재확인도 시간 초과되었습니다.";
                    }
                    catch (OperationCanceledException) when (_closing || _closePending)
                    {
                        if (!_closing) EnqueueAutomationClaim(store, claim, prioritizeRun: true);
                        return;
                    }
                }
                if (_closing || _closePending)
                {
                    if (!_closing) EnqueueAutomationClaim(store, claim, prioritizeRun: true);
                    return;
                }
                state.Pane.NativeAutomationCancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException(reason);
            }
            RecordAutomationDiagnostic("opencode_ready", "OpenCode 대기 상태 확인 완료", claim, pane: state.Pane);
            claim = claim with { ClaimedAt = DateTimeOffset.UtcNow };
            var latest = await store.GetAsync(claim.RunId);
            if (latest?.ActiveDispatchToken != claim.DispatchToken || latest.State != AutomationState.Running) return;
            string previous = (claim.PreviousSummary ?? "첫 단계입니다.") + "\n\n" + (claim.Instruction ?? "");
            var command = await AutomationCommandBuilder.PrepareNativeAsync(claim.ProjectDirectory, claim, previous);
            if (_closePending) { EnqueueAutomationClaim(store, claim, prioritizeRun: true); return; }
            var mode = claim.Role == AutomationRole.Coordinator ? TerminalInputMode.Plan : claim.Role == AutomationRole.Implementer ? TerminalInputMode.Build : TerminalInputMode.Review;
            var entry = await new ProjectWorkJournal(claim.ProjectDirectory).SubmitAsync(mode,
                $"자동화 {claim.RunId:N} · {claim.Iteration}회 · {RoleLabel(claim.Role)} · 요청 {claim.DispatchToken:N}\n{previous}\n\n{claim.ApprovedPlanSnapshot}", state.Sequence, isAutomation: true);
            if (entry.Kind == JournalSubmissionKind.Duplicate) throw new InvalidOperationException("이미 제출된 자동화 작업입니다. 기록을 확인하세요.");
            journalId = entry.Entry?.Id;
            latest = await store.GetAsync(claim.RunId);
            if (_closing || latest?.ActiveDispatchToken != claim.DispatchToken || latest.State != AutomationState.Running) return;
            if (compactDispatch && (!TerminalTabs.Items.Contains(tab) || !state.Pane.IsReady || !state.Pane.IsRunning ||
                !string.Equals(runtime.Project, claim.ProjectDirectory, StringComparison.OrdinalIgnoreCase) ||
                !runtime.Team.TryGetValue(claim.Role, out var currentTab) || currentTab != tab))
                throw new InvalidOperationException("자동화 역할 터미널이 변경되어 명령을 보내지 않았습니다.");
            state.Pane.BeginNativeAutomation();
            ownedPane = state.Pane;
            runtime.StartingNative = (claim.RunId, state.Pane);
            client = new OpenCodeAutomationClient(state.Pane.NativeOpenCodeEndpoint, claim.ProjectDirectory, useTuiSelection: false);
            UpdateCompactDashboard();
            var dispatch = await client.StartAsync($"{ProjectDisplayName(claim.ProjectDirectory)} · {RoleLabel(claim.Role)} · {claim.Iteration}회",
                command.Command, claim.Options.ModelFor(claim.Role), state.Pane.NativeAutomationCancellation,
                beforeSubmit: () => Dispatcher.Invoke(() =>
                {
                    if (_closing || _closePending || !TerminalTabs.Items.Contains(tab) ||
                        !runtime.Team.TryGetValue(claim.Role, out var current) || current != tab ||
                        !state.Pane.IsNativeAutomationActive)
                        throw new OperationCanceledException("자동화 대상이 변경되어 요청을 보내지 않았습니다.");
                    state.Pane.ObserveNativeAutomationSubmission();
                    RecordAutomationDiagnostic("prompt_submitting", "역할 요청 전송 중", claim, pane: state.Pane);
                }), sessionCreated: session => Dispatcher.Invoke(() => state.Pane.BindNativeAutomationSession(session)));
            // From this point onward the exact native request owns the stage even
            // if a subsequent ledger read or UI/artifact update temporarily fails.
            runtime.Stage = new(store, claim, command, state.Pane, journalId, client, dispatch);
            client = null;
            runtime.NextMicroCheck.Clear();
            runtime.ReviewerAdvice = runtime.CoordinatorAdvice = runtime.RequestedIntervention = null;
            state.Pane.NativeAutomationCancellation.ThrowIfCancellationRequested();
            latest = await store.GetAsync(claim.RunId);
            if (_closing || latest?.State != AutomationState.Running ||
                latest.ActiveDispatchToken != claim.DispatchToken || !state.Pane.IsNativeAutomationActive)
                throw new OperationCanceledException("자동화가 중단되어 시작 결과를 적용하지 않았습니다.");
            RecordAutomationDiagnostic("prompt_submitted", "역할 요청 전달됨 · 실제 OpenCode 상태를 확인합니다.", claim, pane: state.Pane, dispatch: dispatch);
            UpdateAutomationProgressPanel();
            // The native request has already been sent. A local artifact failure must
            // never retire its ownership and schedule a second mutating prompt.
            try { await File.WriteAllTextAsync(Path.Combine(command.Directory, "native-session.json"), JsonSerializer.Serialize(dispatch)); }
            catch (Exception artifactError)
            {
                RecordAutomationDiagnostic("native_artifact_failed", "세션 부가 파일 저장 실패 · 실행 감시는 유지합니다. " + artifactError.Message,
                    claim, severity: "warning", exception: artifactError, dispatch: dispatch);
            }
            if (runtime.Stage?.Claim.DispatchToken != claim.DispatchToken ||
                state.Pane.NativeAutomationCancellation.IsCancellationRequested || _closing) return;
            SetStatus($"OpenCode 자동화 {claim.Iteration}회 · {RoleLabel(claim.Role)} 세션에 작업 전달 완료");
        }
        catch (Exception ex)
        {
            if (ownedPane?.NativeAutomationCancellation.IsCancellationRequested == true && !_closing)
            {
                // Closing or interrupting a role pane is a manual stop, not permission
                // to replay a request which may already have changed project files.
                await StopCancelledAutomationClaimAsync(store, claim);
                RecordAutomationDiagnostic("stage_start_cancelled", "역할 터미널 중단 · 재전송하지 않습니다.", claim, pane: ownedPane);
                client?.Dispose();
                if (runtime.Stage is { } cancelled && cancelled.Claim.DispatchToken == claim.DispatchToken)
                    await RetireStoppedStageAsync(cancelled);
                else
                {
                    ReleaseAutomationPane(ownedPane, failed: true);
                    if (journalId is Guid stoppedJournalId)
                    {
                        try { await new ProjectWorkJournal(claim.ProjectDirectory).UpdateStatusAsync(stoppedJournalId, WorkJournalStatus.Failed, "역할 터미널 준비 중 사용자가 중단했습니다."); }
                        catch (Exception journalError)
                        {
                            RecordAutomationDiagnostic("journal_update_failed", "중단 기록 갱신 실패 · " + journalError.Message,
                                claim, severity: "warning", exception: journalError);
                        }
                    }
                }
                SetStatus("자동화 중단 · 사용자가 역할 터미널을 중단했습니다.");
                return;
            }
            if (runtime.Stage is { } submitted && submitted.Claim.DispatchToken == claim.DispatchToken &&
                !submitted.Pane.NativeAutomationCancellation.IsCancellationRequested && !_closing)
            {
                RecordAutomationDiagnostic("post_submit_reconciliation", "요청 전달 이후 확인 실패 · 동일 세션 감시를 유지합니다. " + ex.Message,
                    claim, pane: submitted.Pane, severity: "warning", exception: ex, dispatch: submitted.Dispatch);
                SetStatus("OpenCode 요청은 전달되었습니다 · 동일 세션 결과 재확인 중");
                return;
            }
            RecordAutomationDiagnostic("stage_start_failed", "단계 시작 실패 · " + ex.Message, claim,
                pane: ownedPane, severity: "error", exception: ex);
            client?.Dispose();
            if (ownedPane is not null) ReleaseAutomationPane(ownedPane, failed: true);
            if (runtime.Stage is { } uncertain && uncertain.Claim.DispatchToken == claim.DispatchToken)
            {
                uncertain.Client.Dispose();
                if (ownedPane != uncertain.Pane) ReleaseAutomationPane(uncertain.Pane, failed: true);
            }
            runtime.Stage = null;
            var failed = await store.CompleteStepAsync(new(claim.RunId, claim.DispatchToken, claim.Revision, false, ex.Message, "", false, 0, DateTimeOffset.UtcNow));
            _knownAutomationRuns[failed.Run.Id] = failed.Run;
            RecordAutomationDiagnostic("startup_retry_scheduled", $"{RoleLabel(failed.Run.NextRole)} · {WorkflowPhaseLabel(failed.Run.WorkflowPhase)} · {AutomationStateLabel(failed.Run.State)}",
                run: failed.Run, severity: failed.Accepted ? "warning" : "info");
            string journalWarning = "";
            if (journalId is Guid id)
            {
                try { await new ProjectWorkJournal(claim.ProjectDirectory).UpdateStatusAsync(id, WorkJournalStatus.Failed, ex.Message); }
                catch (Exception journalError) { journalWarning = " · MD 갱신 실패: " + journalError.Message; }
            }
            SetStatus(failed.Run.State == AutomationState.Stopped ? "자동화 중단" : "자동화 시작 실패 · " + ex.Message + journalWarning);
            RefreshConversationHistory();
        }
        finally { runtime.StartingNative = null; runtime.Dispatching = null; UpdateAutomationProgressPanel(); }
    }

    private async Task RefreshAutomationRunsAsync()
    {
        foreach (string project in _automationProjects.ToArray())
        {
            if (_projectArchiveWrites.Contains(project) || !Directory.Exists(project)) continue;
            foreach (var run in await new AutomationStore(project).LoadAsync())
            {
                if (_projectArchiveWrites.Contains(project) || !_automationProjects.Contains(project)) break;
                if (_knownAutomationRuns.TryGetValue(run.Id, out var current))
                {
                    if (current.Revision > run.Revision || current.UpdatedAt > run.UpdatedAt) continue;
                    if (current.Revision == run.Revision && current.PauseAfterCurrentStage && !run.PauseAfterCurrentStage)
                        continue; // Preserve a locally requested pause while its store write is pending.
                }
                _knownAutomationRuns[run.Id] = run;
            }
        }
        UpdateProjectTaskActivity();
        if (_automationRunsList is null) return;
        Guid? selectedId = _preferredAutomationRunId ?? (_automationRunsList.SelectedItem as AutomationListItem)?.Run.Id;
        _automationRunRefreshDepth++;
        try
        {
            _automationRunsList.ItemsSource = _knownAutomationRuns.Values
                .OrderBy(r => ProjectDisplayName(r.ProjectDirectory), StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => CanonicalProjectPath(r.ProjectDirectory), StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(r => r.CreatedAt)
                .Select(r => new AutomationListItem(r, ProjectDisplayName(r.ProjectDirectory))).ToArray();
            _automationRunsList.SelectedItem = _automationRunsList.Items.Cast<AutomationListItem>().FirstOrDefault(i => i.Run.Id == selectedId) ?? _automationRunsList.Items.Cast<AutomationListItem>().FirstOrDefault();
        }
        finally { _automationRunRefreshDepth--; }
        UpdateAutomationSelection();
    }

    private async Task PresentAutomationCompletionAsync(AutomationRun run)
    {
        _preferredAutomationRunId = run.Id;
        if (_automationWindow is { IsVisible: true } && _automationRunsList is not null)
        {
            await RefreshAutomationRunsAsync();
            return;
        }

        UpdateAutomationProgressPanel();
    }

    private void UpdateAutomationSelection()
    {
        if (_automationRunStatus is null) return;
        if ((_automationRunsList?.SelectedItem as AutomationListItem)?.Run is not { } run) { _automationRunStatus.Text = ""; return; }
        var lines = new[]
        {
            $"{RoleLabel(run.NextRole)} · {WorkflowPhaseLabel(run.WorkflowPhase)} · {run.Iteration}회 · {TerminalTokenUsage.FormatCount(run.TotalTokenUsage)} 토큰",
            run.ScheduledFor is { } when && run.State == AutomationState.Scheduled ? $"예약 {when.ToLocalTime():MM-dd HH:mm}" : null,
            run.PauseAfterCurrentStage ? "이 단계 후 일시정지" : null,
            run.StatusDetail, run.LastSummary,
            run.State == AutomationState.Completed ? run.FinalVerificationEvidence ?? run.DeliveryEvidence : null
        };
        _automationRunStatus.Text = string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)).Distinct());
    }

    private void DisposeAutomation()
    {
        _automationTimer.Stop();
        _automationProgressTimer.Stop();
        _automationProgressIndicator?.SetStatus(false, false, false, System.Windows.Media.Brushes.Gray, "중단");
        _automationInitializationRetryTimer.Stop();
        _automationWindow?.Close(); _automationWindow = null;
        _automationPaneTokens.Clear();
        foreach (var runtime in _automationRuntimes.Values)
        {
            runtime.Stage?.Client.Dispose();
            foreach (var micro in runtime.MicroStages.Values) micro.Client.Dispose();
        }
        _automationOwnership?.Dispose(); _automationOwnership = null;
    }

    private void ReleaseAutomationPane(TerminalPane pane, bool failed)
    {
        pane.SetAutomationBusy(false);
        if (failed)
        {
            pane.MarkWorkFailed();
            _automationPanesToReplace.Add(pane);
        }
    }

    private async Task StopCancelledAutomationClaimAsync(AutomationStore store, AutomationStepClaim claim)
    {
        var current = await store.GetAsync(claim.RunId);
        if (current?.State == AutomationState.Running && current.ActiveDispatchToken == claim.DispatchToken)
            current = await store.StopAsync(claim.RunId);
        if (current is not null) _knownAutomationRuns[current.Id] = current;
    }

    private async Task RetireStoppedStageAsync(ActiveAutomationStage stage)
    {
        StopMicroManagement(RuntimeFor(stage.Claim.ProjectDirectory));
        RecordAutomationDiagnostic("stage_stopped", "사용자가 단계를 중단했습니다.", stage.Claim, pane: stage.Pane, dispatch: stage.Dispatch);
        stage.Client.Dispose();
        ReleaseAutomationPane(stage.Pane, failed: true);
        var runtime = RuntimeFor(stage.Claim.ProjectDirectory);
        if (ReferenceEquals(runtime.Stage, stage)) runtime.Stage = null;
        if (stage.JournalId is not Guid journalId) return;
        try { await new ProjectWorkJournal(stage.Claim.ProjectDirectory).UpdateStatusAsync(journalId, WorkJournalStatus.Failed, "사용자가 자동화를 중단했습니다."); }
        catch (Exception ex) { SetStatus("자동화 중단 완료 · MD 상태 갱신 실패: " + ex.Message); }
        RefreshConversationHistory();
    }
}
