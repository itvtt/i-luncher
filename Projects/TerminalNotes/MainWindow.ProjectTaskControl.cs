using System.IO;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private readonly HashSet<string> _projectTaskControlWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _startingProjectTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stoppingProjectTasks = new(StringComparer.OrdinalIgnoreCase);

    private bool ProjectTaskActionIsStop(string project) =>
        _startingProjectTasks.Contains(CanonicalProjectPath(project)) ||
        RuntimeFor(CanonicalProjectPath(project)).SingleStartupCancellation is not null ||
        ProjectTaskIsRunning(project);

    private async Task ToggleProjectTaskAsync(string project)
    {
        project = CanonicalProjectPath(project);
        if (_closing || _closePending || _stoppingProjectTasks.Contains(project) || _externalTerminalWrites.Contains(project)) return;
        if (HasExternalTerminal(project)) { SetStatus(ExternalTerminalBlockReason); return; }
        if (ProjectTaskActionIsStop(project)) await StopProjectTaskAsync(project);
        else if (!_projectTaskControlWrites.Contains(project) && !_projectTaskOptionWrites.Contains(project) && !_tabDocumentSaving)
            await StartProjectTaskAsync(project);
    }

    private AutomationRun? LatestProjectRun(string project) => _knownAutomationRuns.Values
        .Where(run => DirectoriesEqual(run.ProjectDirectory, project))
        .OrderByDescending(run => run.State is AutomationState.Running or AutomationState.Scheduled)
        .ThenByDescending(run => run.CreatedAt).FirstOrDefault();

    private async Task RecoverUnownedProjectTaskAsync(AutomationProjectRuntime runtime)
    {
        if (runtime.Stage is not null || runtime.StartingNative is not null || runtime.Dispatching is not null ||
            runtime.Pending.Count > 0 || runtime.MicroStages.Count > 0 || _projectTaskControlWrites.Contains(runtime.Project)) return;
        var store = new AutomationStore(runtime.Project);
        foreach (var run in await store.LoadAsync())
        {
            if (run.State != AutomationState.Running || run.ActiveDispatchToken is not Guid token ||
                HasLiveAutomationClaim(run) || DateTimeOffset.UtcNow - run.UpdatedAt < TimeSpan.FromSeconds(10)) continue;
            var recovered = await store.TryRecoverOrphanedRunningAsync(run.Id, token, run.Revision, resumeAutomatically: true);
            if (recovered is null) continue;
            _knownAutomationRuns[run.Id] = recovered;
            RecordAutomationDiagnostic("run_recovered", recovered.State == AutomationState.Scheduled
                ? "실행 연결 복구 · 기존 작업을 이어갑니다." : "사용자가 요청한 일시정지를 보존했습니다.", run: recovered);
        }
    }

    private async Task ResumeProjectTaskAsync(string project)
    {
        if (!IsMultiProjectTask(project)) { await StartProjectTaskAsync(project); return; }
        if (HasExternalTerminal(project)) { SetStatus(ExternalTerminalBlockReason); return; }
        project = CanonicalProjectPath(project);
        if (_closing || _closePending || !_projectTaskControlWrites.Add(project)) return;
        var runtime = RuntimeFor(project);
        long intent = ++runtime.IntentVersion;
        try
        {
            if (SingleTaskIsRunning(runtime)) throw new InvalidOperationException("싱글 작업을 완료하거나 중단한 뒤 재개하세요.");
            UpdateProjectTaskActivity();
            if (!await TryInitializeAutomationAsync()) throw new InvalidOperationException("지휘자 실행 준비 중입니다.");
            while (runtime.Polling && !_closing && !_closePending) await Task.Delay(25);
            if (_closing || _closePending || intent != runtime.IntentVersion) return;
            if (runtime.Stage is not null || runtime.StartingNative is not null || runtime.Dispatching is not null ||
                runtime.Pending.Count > 0 || runtime.MicroStages.Count > 0)
                throw new InvalidOperationException("이전 역할의 종료를 확인 중입니다. 잠시 후 이어서 실행하세요.");
            var store = new AutomationStore(project);
            var runs = await store.LoadAsync();
            if (runs.Any(run => run.State is AutomationState.Running or AutomationState.Scheduled))
                throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
            var run = runs.OrderByDescending(run => run.CreatedAt).FirstOrDefault();
            if (run is null || run.State is not (AutomationState.Paused or AutomationState.Stopped or AutomationState.Failed)) return;
            string plan = DefaultProjectPlanPath(project);
            if (!File.Exists(plan) || string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(plan)))
                throw new InvalidOperationException("플랜을 저장한 뒤 이어서 실행하세요.");
            var document = await LoadAutomationPlanAsync(null, project, 1, plan);
            await SaveAutomationPlanAsync(document, document.InitialDraft);
            if (_closing || _closePending || intent != runtime.IntentVersion) return;
            _projectTaskMultiModes[project] = true;
            if (DirectoriesEqual(_selectedProjectPath, project)) SyncProjectPlanMode(project);
            SaveSettings();
            EnsureAutomationTeam(project);
            _automationProjects.Add(project);
            await _automationProjectWriteGate.WaitAsync();
            try
            {
                string temporary = AutomationProjectsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(temporary, System.Text.Json.JsonSerializer.Serialize(_automationProjects));
                File.Move(temporary, AutomationProjectsPath, true);
            }
            finally { _automationProjectWriteGate.Release(); }
            if (_closing || _closePending || intent != runtime.IntentVersion) return;
            _knownAutomationRuns[run.Id] = await store.ResumeAsync(run.Id, DateTimeOffset.UtcNow, run.Revision, allowStopped: true);
            if (intent != runtime.IntentVersion)
            {
                _knownAutomationRuns[run.Id] = await store.StopAsync(run.Id);
                return;
            }
            _preferredAutomationRunId = run.Id;
            _automationTimer.Start();
            RecordAutomationDiagnostic("run_resumed", "태스크에서 기존 작업을 이어서 실행했습니다.", run: _knownAutomationRuns[run.Id]);
            SetStatus("기존 작업을 이어서 실행합니다 · " + ProjectDisplayName(project));
        }
        catch (Exception ex) { SetStatus("이어 실행 실패 · " + ex.Message); }
        finally
        {
            _projectTaskControlWrites.Remove(project);
            UpdateProjectTaskActivity();
        }
        await PollAutomationAsync();
    }

    private async Task PauseProjectTaskAsync(string project)
    {
        if (_closing || _closePending || !_projectTaskControlWrites.Add(project)) return;
        try
        {
            if (LatestProjectRun(project) is not { } run) return;
            _knownAutomationRuns[run.Id] = run with { PauseAfterCurrentStage = true };
            UpdateProjectTaskActivity();
            _knownAutomationRuns[run.Id] = await new AutomationStore(project).PauseAsync(run.Id);
            SetStatus("현재 단계가 끝나면 일시정지합니다 · " + ProjectDisplayName(project));
        }
        catch (Exception ex) { SetStatus("일시정지 실패 · " + ex.Message); }
        finally { _projectTaskControlWrites.Remove(project); UpdateProjectTaskActivity(); }
    }
}
