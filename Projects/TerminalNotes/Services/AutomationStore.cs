using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerminalNotes.Services;

/// <summary>
/// Atomic project-local persistence and transition logic for scheduled automation runs.
/// The host must remain open (or in its tray lifetime) to poll and dispatch scheduled work.
/// </summary>
public sealed class AutomationStore
{
    private const int StoreVersion = 1;
    private const string LegacyIterationLimitDetail = "Maximum iteration count reached.";
    private const string InterruptedStageDetail = "앱 종료로 결과 미확인; 기록 확인 후 재개";
    private const string OrphanedStageDetail = "스케줄러 소유권 종료 후 복구됨. 재개 전에 기록을 확인하세요.";
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _projectDirectory;
    private readonly string _storeDirectory;
    private readonly string _storePath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _processGate;

    public AutomationStore(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        _projectDirectory = Path.GetFullPath(projectDirectory);
        _storeDirectory = Path.Combine(_projectDirectory, "terminal-notes");
        _storePath = Path.Combine(_storeDirectory, "automation-runs.json");
        _lockPath = Path.Combine(_storeDirectory, ".automation-runs.lock");
        _processGate = ProcessGates.GetOrAdd(_storePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string ProjectDirectory => _projectDirectory;
    public string StorePath => _storePath;

    public async Task<AutomationRun> CreateDraftAsync(
        string approvedPlanSnapshot,
        int? maxIterations = null,
        AutomationExecutionOptions? options = null,
        DateTimeOffset? createdAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedPlanSnapshot);
        options ??= new AutomationExecutionOptions();
        ValidateOptions(options);
        DateTimeOffset now = createdAt ?? DateTimeOffset.UtcNow;

        return await MutateAsync((ledger, _) =>
        {
            var run = new AutomationRun(
                Guid.NewGuid(),
                _projectDirectory,
                approvedPlanSnapshot,
                options,
                maxIterations,
                Iteration: 1,
                AutomationState.Draft,
                AutomationRole.Coordinator,
                now,
                now,
                ScheduledFor: null,
                ActiveDispatchToken: null,
                Revision: 0,
                PauseAfterCurrentStage: false,
                TotalTokenUsage: 0,
                LastSummary: null,
                NextInstruction: null,
                StatusDetail: null)
            {
                WorkflowPhase = AutomationWorkflowPhase.Planning
            };
            ledger.Runs.Add(run);
            return (run, true);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<AutomationRun>> LoadAsync(
        CancellationToken cancellationToken = default) =>
        await ReadAsync(ledger => (IReadOnlyList<AutomationRun>)ledger.Runs.ToArray(), cancellationToken);

    public async Task<AutomationRun?> GetAsync(
        Guid runId,
        CancellationToken cancellationToken = default) =>
        await ReadAsync(ledger => ledger.Runs.FirstOrDefault(run => run.Id == runId), cancellationToken);

    public async Task<AutomationRun> ScheduleAsync(
        Guid runId,
        DateTimeOffset scheduledFor,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        await MutateAsync((ledger, now) =>
        {
            int index = FindRun(ledger, runId);
            AutomationRun run = ledger.Runs[index];
            CheckRevision(run, expectedRevision);
            if (run.State != AutomationState.Draft)
            {
                throw new InvalidOperationException("Only a draft automation run can be scheduled.");
            }

            AutomationRun updated = run with
            {
                State = AutomationState.Scheduled,
                ScheduledFor = scheduledFor,
                UpdatedAt = now,
                Revision = run.Revision + 1,
                StatusDetail = null
            };
            ledger.Runs[index] = updated;
            return (updated, true);
        }, cancellationToken);

    /// <summary>
    /// Starts a newly approved plan in one ledger transaction. Existing active runs
    /// block the request; paused runs are retired but remain in history.
    /// </summary>
    public async Task<AutomationRun> StartApprovedPlanAsync(
        string approvedPlanSnapshot,
        int? maxIterations = null,
        AutomationExecutionOptions? options = null,
        DateTimeOffset? scheduledFor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedPlanSnapshot);
        options ??= new AutomationExecutionOptions();
        ValidateOptions(options);

        return await MutateAsync((ledger, now) =>
        {
            if (ledger.Runs.Any(run => run.State is AutomationState.Running or AutomationState.Scheduled))
                throw new InvalidOperationException("An active automation run must finish or stop before starting a new approved plan.");

            for (int index = 0; index < ledger.Runs.Count; index++)
            {
                AutomationRun run = ledger.Runs[index];
                if (run.State != AutomationState.Paused) continue;

                ledger.Runs[index] = run with
                {
                    State = AutomationState.Stopped,
                    ActiveDispatchToken = null,
                    ScheduledFor = null,
                    PauseAfterCurrentStage = false,
                    UpdatedAt = now,
                    Revision = run.Revision + 1
                };
            }

            var started = new AutomationRun(
                Guid.NewGuid(),
                _projectDirectory,
                approvedPlanSnapshot,
                options,
                maxIterations,
                Iteration: 1,
                AutomationState.Scheduled,
                AutomationRole.Coordinator,
                now,
                now,
                ScheduledFor: scheduledFor ?? now,
                ActiveDispatchToken: null,
                Revision: 0,
                PauseAfterCurrentStage: false,
                TotalTokenUsage: 0,
                LastSummary: null,
                NextInstruction: null,
                StatusDetail: null)
            {
                WorkflowPhase = AutomationWorkflowPhase.Planning
            };
            ledger.Runs.Add(started);
            return (started, true);
        }, cancellationToken);
    }

    /// <summary>
    /// Claims every due scheduled run in one persisted transaction. A claimed run remains Running
    /// across restarts and is never returned by this method again.
    /// </summary>
    public Task<IReadOnlyList<AutomationStepClaim>> ClaimDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ClaimDueAsync(now, int.MaxValue, cancellationToken);

    public async Task<IReadOnlyList<AutomationStepClaim>> ClaimDueAsync(
        DateTimeOffset now,
        int maxClaims,
        CancellationToken cancellationToken = default)
    {
        if (maxClaims < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxClaims));
        }

        return await MutateAsync((ledger, _) =>
        {
            var claims = new List<AutomationStepClaim>();
            bool changed = false;
            int[] dueIndexes = ledger.Runs
                .Select((run, index) => (Run: run, Index: index))
                .Where(item => item.Run.State == AutomationState.Scheduled &&
                    item.Run.ScheduledFor is not null &&
                    item.Run.ScheduledFor <= now)
                .OrderBy(item => item.Run.ScheduledFor)
                .ThenBy(item => item.Run.CreatedAt)
                .Take(maxClaims)
                .Select(item => item.Index)
                .ToArray();
            foreach (int index in dueIndexes)
            {
                AutomationRun run = ledger.Runs[index];
                if (FinishWaitingImprovement(run, now) is { } finished)
                {
                    ledger.Runs[index] = finished;
                    changed = true;
                    continue;
                }
                run = NormalizePhaseRole(run);
                Guid token = Guid.NewGuid();
                AutomationRun claimed = run with
                {
                    State = AutomationState.Running,
                    ActiveDispatchToken = token,
                    ScheduledFor = null,
                    UpdatedAt = now,
                    Revision = run.Revision + 1,
                    StatusDetail = null
                };
                ledger.Runs[index] = claimed;
                claims.Add(ToClaim(claimed, token, now));
            }

            return ((IReadOnlyList<AutomationStepClaim>)claims, changed || claims.Count > 0);
        }, cancellationToken);
    }

    /// <summary>
    /// Recovers work from a prior app lifetime, optionally scheduling authorized unfinished work.
    /// Queued user pauses remain paused. The host must hold its single-scheduler lock before calling.
    /// </summary>
    public async Task<IReadOnlyList<AutomationRun>> RecoverInterruptedAsync(
        DateTimeOffset? recoveredAt = null,
        CancellationToken cancellationToken = default,
        bool includeScheduled = false,
        bool resumeAutomatically = false) =>
        await MutateAsync((ledger, now) =>
        {
            DateTimeOffset timestamp = recoveredAt ?? now;
            var recovered = new List<AutomationRun>();
            for (int index = 0; index < ledger.Runs.Count; index++)
            {
                AutomationRun run = ledger.Runs[index];
                bool interrupted = run.State == AutomationState.Running;
                bool abandonedSchedule = includeScheduled && run.State == AutomationState.Scheduled;
                if (!interrupted && !abandonedSchedule)
                {
                    continue;
                }

                AutomationRun updated = run with
                {
                    State = AutomationState.Paused,
                    ActiveDispatchToken = null,
                    ScheduledFor = null,
                    PauseAfterCurrentStage = false,
                    UpdatedAt = timestamp,
                    Revision = run.Revision + 1,
                    StatusDetail = interrupted
                        ? InterruptedStageDetail
                        : "앱 시작 시 예약 작업 복구; 기록 확인 후 재개"
                };
                // Only previously authorized work is restored. A user-requested
                // pause survives a crash, including a pause queued during a stage.
                if (resumeAutomatically && !run.PauseAfterCurrentStage)
                {
                    updated = NormalizePhaseRole(updated);
                    if (interrupted) updated = RouteInterruptedImplementerToReviewer(updated);
                    updated = updated with
                    {
                        State = AutomationState.Scheduled,
                        ScheduledFor = abandonedSchedule && run.ScheduledFor > timestamp ? run.ScheduledFor : timestamp,
                        StatusDetail = "중단된 연결 복구 · 기존 작업 자동 재개"
                    };
                }
                ledger.Runs[index] = updated;
                recovered.Add(updated);
            }

            return ((IReadOnlyList<AutomationRun>)recovered, recovered.Count > 0);
        }, cancellationToken);

    /// <summary>
    /// Pauses one Running claim after its scheduler owner has independently established that no
    /// live scheduler still owns it. The expected dispatch token and revision prevent recovery
    /// from invalidating a claim that has since advanced or changed.
    /// </summary>
    public async Task<AutomationRun?> TryRecoverOrphanedRunningAsync(
        Guid runId,
        Guid expectedDispatchToken,
        long expectedRevision,
        DateTimeOffset? recoveredAt = null,
        CancellationToken cancellationToken = default,
        bool resumeAutomatically = false)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("A run ID is required.", nameof(runId));
        }

        if (expectedDispatchToken == Guid.Empty)
        {
            throw new ArgumentException("A dispatch token is required.", nameof(expectedDispatchToken));
        }

        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        return await MutateAsync((ledger, now) =>
        {
            int index = FindRun(ledger, runId);
            AutomationRun run = ledger.Runs[index];
            if (run.State != AutomationState.Running ||
                run.ActiveDispatchToken != expectedDispatchToken ||
                run.Revision != expectedRevision)
            {
                return (null, false);
            }

            AutomationRun updated = run with
            {
                State = AutomationState.Paused,
                ActiveDispatchToken = null,
                ScheduledFor = null,
                PauseAfterCurrentStage = false,
                UpdatedAt = recoveredAt ?? now,
                Revision = run.Revision + 1,
                StatusDetail = OrphanedStageDetail
            };
            if (resumeAutomatically && !run.PauseAfterCurrentStage)
            {
                updated = RouteInterruptedImplementerToReviewer(NormalizePhaseRole(updated)) with
                {
                    State = AutomationState.Scheduled,
                    ScheduledFor = recoveredAt ?? now,
                    StatusDetail = "실행 연결 복구 · 기존 작업 자동 재개"
                };
            }
            ledger.Runs[index] = updated;
            return ((AutomationRun?)updated, true);
        }, cancellationToken);
    }

    public async Task<AutomationCompletionResult> CompleteStepAsync(
        AutomationStepResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.TokenUsage < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(result), "Token usage cannot be negative.");
        }

        return await MutateAsync((ledger, _) =>
        {
            int index = FindRun(ledger, result.RunId);
            AutomationRun run = ledger.Runs[index];
            if (run.State != AutomationState.Running ||
                run.ActiveDispatchToken != result.DispatchToken ||
                run.Revision != result.ExpectedRevision)
            {
                return (new AutomationCompletionResult(
                    Accepted: false,
                    run,
                    NextClaim: null,
                    RejectionReason: "The stage completion is stale or the run is no longer running."), false);
            }

            long totalTokens;
            try
            {
                totalTokens = checked(run.TotalTokenUsage + result.TokenUsage);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("The accumulated token usage is too large.", exception);
            }

            long nextRevision = run.Revision + 1;
            AutomationRun updated = run with
            {
                ActiveDispatchToken = null,
                UpdatedAt = result.CompletedAt,
                Revision = nextRevision,
                TotalTokenUsage = totalTokens,
                LastSummary = result.Summary,
                NextInstruction = result.Instruction,
                StatusDetail = null
            };

            AutomationStepClaim? nextClaim = null;
            if (!result.Succeeded)
            {
                updated = ScheduleSameRoleRetry(
                    updated,
                    run,
                    result.CompletedAt,
                    string.IsNullOrWhiteSpace(result.Summary)
                        ? "The automation stage failed."
                        : result.Summary);
                if (result.Interrupted)
                {
                    // The native supervisor has confirmed quiescence. A partially
                    // applied implementation must be inspected before more writes.
                    updated = RouteInterruptedImplementerToReviewer(updated);
                    updated = updated with
                    {
                        DeliveryEvidence = run.NextRole == AutomationRole.Implementer ? null : run.DeliveryEvidence,
                        FinalVerificationEvidence = run.NextRole == AutomationRole.Implementer ? null : run.FinalVerificationEvidence,
                        NextInstruction = run.NextRole == AutomationRole.Implementer
                            ? $"중단 복구: {result.Summary}\n현재 파일·변경분·실행 상태를 독립적으로 검토하고 완료된 부분과 남은 작업을 지휘자에게 보고하세요. 구현을 그대로 반복하지 마세요.\n" + run.NextInstruction
                            : run.NextInstruction,
                        StatusDetail = $"{result.Summary} 다음: {updated.NextRole} · {updated.State}"
                    };
                }
            }
            else if (GetInvalidWorkflowOutputDetail(run, result) is string invalidWorkflowOutputDetail)
            {
                updated = ScheduleSameRoleRetry(updated, run, result.CompletedAt, invalidWorkflowOutputDetail);
            }
            else if (run.Options.ContinuousImprovement && run.WorkflowPhase == AutomationWorkflowPhase.Planning &&
                     result.PlanningPlan is { } proposal && run.ImprovementHistory.Any(prior =>
                         prior.PlanFingerprint == ImprovementPlanFingerprint(proposal)))
            {
                updated = ScheduleSameRoleRetry(updated, run, result.CompletedAt,
                    "이미 검증을 마친 개선 계획입니다. 현재 코드와 남은 문제를 조사해 다른 개선 목표와 인수 테스트를 정하세요.");
            }
            else if (run.WorkflowPhase == AutomationWorkflowPhase.FinalVerification &&
                result.Complete &&
                run.PlanApproved &&
                !string.IsNullOrWhiteSpace(run.DeliveryEvidence))
            {
                updated = updated with
                {
                    State = AutomationState.Completed,
                    FinalVerificationEvidence = result.DeliveryEvidence,
                    LastReviewSummary = result.Summary,
                    LastReviewInstruction = result.Instruction,
                    LastReviewerFeedbackFingerprint = string.IsNullOrWhiteSpace(result.Instruction)
                        ? run.LastReviewerFeedbackFingerprint
                        : FingerprintReviewerFeedback(result.Instruction),
                    LastReviewerFeedbackInstruction = string.IsNullOrWhiteSpace(result.Instruction)
                        ? run.LastReviewerFeedbackInstruction
                        : result.Instruction,
                    PauseAfterCurrentStage = false,
                    CurrentRoleRetryCount = 0,
                    StatusDetail = string.IsNullOrWhiteSpace(result.Summary)
                        ? "The reviewer verified the delivered service."
                        : result.Summary
                };
                if (run.Options.CanStartImprovementCycle(result.CompletedAt))
                {
                    // Persist completion and the next cycle in one transaction.
                    // A crash cannot lose the result or start duplicate cycles.
                    var outcome = new AutomationImprovementOutcome(run.ImprovementCycle,
                        run.CoordinatorPlanningPlan?.GoalsAndScope ?? run.CoordinatorPlan ?? "",
                        ImprovementPlanFingerprint(run.CoordinatorPlanningPlan), result.Summary,
                        result.DeliveryEvidence!, result.CompletedAt);
                    updated = updated with
                    {
                        State = run.PauseAfterCurrentStage ? AutomationState.Paused : AutomationState.Scheduled,
                        ScheduledFor = run.PauseAfterCurrentStage ? null : result.CompletedAt.AddSeconds(30),
                        NextRole = AutomationRole.Coordinator,
                        WorkflowPhase = AutomationWorkflowPhase.Planning,
                        ImprovementCycle = checked(run.ImprovementCycle + 1),
                        ImprovementHistory = [.. run.ImprovementHistory, outcome],
                        PlanApproved = false,
                        CoordinatorPlan = null,
                        CoordinatorPlanningPlan = null,
                        DeliveryEvidence = null,
                        FinalVerificationEvidence = null,
                        NextInstruction = "직전 개선은 독립 검증을 통과했습니다. 최신 MD와 현재 코드를 조사하고 버그 수정·품질 개선·관련 기능 추가 중 다음으로 가치 있는 작은 목표를 선정하세요. 완료한 작업을 반복하지 말고 근거, 변경 범위, 새 인수 테스트를 포함한 계획을 검토자에게 제출하세요.",
                        StatusDetail = run.PauseAfterCurrentStage ? "개선 주기 검증 완료 · 일시정지" : "개선 주기 검증 완료 · 다음 개선 준비"
                    };
                }
            }
            else
            {
                updated = AdvanceWorkflowStage(updated, run, result);

                if (run.PauseAfterCurrentStage)
                {
                    updated = updated with
                    {
                        State = AutomationState.Paused,
                        ActiveDispatchToken = null,
                        ScheduledFor = null,
                        PauseAfterCurrentStage = false,
                        StatusDetail = "Paused after the current stage."
                    };
                }
                else
                {
                    Guid nextToken = Guid.NewGuid();
                    updated = updated with
                    {
                        State = AutomationState.Running,
                        ActiveDispatchToken = nextToken,
                        PauseAfterCurrentStage = false
                    };
                    nextClaim = ToClaim(updated, nextToken, result.CompletedAt);
                }
            }

            ledger.Runs[index] = updated;
            return (new AutomationCompletionResult(true, updated, nextClaim, null), true);
        }, cancellationToken);
    }

    // This preference does not revoke a dispatch. Keep its revision/token valid so
    // the current result can commit and final verification reads the latest option.
    public async Task<AutomationRun> SetContinuousImprovementAsync(Guid runId, bool enabled,
        DateTimeOffset? until, CancellationToken cancellationToken = default) =>
        await MutateAsync((ledger, now) =>
        {
            int index = FindRun(ledger, runId);
            AutomationRun run = ledger.Runs[index];
            if (run.State is AutomationState.Completed or AutomationState.Stopped or AutomationState.Failed)
                return (run, false);
            if (run.Options.ContinuousImprovement == enabled && run.Options.ImprovementUntil == until)
                return (run, false);
            var updated = run with
            {
                Options = run.Options with { ContinuousImprovement = enabled, ImprovementUntil = until },
                UpdatedAt = now
            };
            updated = FinishWaitingImprovement(updated, now) ?? updated;
            ledger.Runs[index] = updated;
            return (updated, true);
        }, cancellationToken);

    private static AutomationRun? FinishWaitingImprovement(AutomationRun run, DateTimeOffset now)
    {
        // Between verified cycles there is no unfinished work to launch. Expiry
        // and manual disable must also win the race with the next scheduler poll.
        if (run.State is not (AutomationState.Scheduled or AutomationState.Paused) ||
            run.WorkflowPhase != AutomationWorkflowPhase.Planning || run.CoordinatorPlan is not null ||
            run.ImprovementHistory.LastOrDefault() is not { } previous ||
            run.Options.CanStartImprovementCycle(now)) return null;
        return run with
        {
            State = AutomationState.Completed, ScheduledFor = null, ActiveDispatchToken = null,
            PauseAfterCurrentStage = false, Revision = run.Revision + 1, UpdatedAt = now,
            WorkflowPhase = AutomationWorkflowPhase.FinalVerification, NextRole = AutomationRole.Reviewer,
            ImprovementCycle = previous.Cycle, LastSummary = previous.Summary,
            FinalVerificationEvidence = previous.Evidence,
            StatusDetail = "Loop 종료 · 마지막 개선 검증 완료"
        };
    }

    /// <summary>
    /// Pauses immediately when idle/scheduled. During an active stage, records a pause request and
    /// accepts that stage's result before entering Paused without issuing another claim.
    /// </summary>
    public async Task<AutomationRun> PauseAsync(
        Guid runId,
        CancellationToken cancellationToken = default) =>
        await MutateAsync((ledger, now) =>
        {
            int index = FindRun(ledger, runId);
            AutomationRun run = ledger.Runs[index];
            if (run.State == AutomationState.Paused)
            {
                return (run, false);
            }

            if (run.State is AutomationState.Completed or AutomationState.Failed or AutomationState.Stopped)
            {
                throw new InvalidOperationException($"A {run.State} automation run cannot be paused.");
            }

            AutomationRun updated;
            if (run.State == AutomationState.Running && run.ActiveDispatchToken is not null)
            {
                // Keep Revision unchanged so the in-flight result remains valid.
                updated = run with
                {
                    PauseAfterCurrentStage = true,
                    UpdatedAt = now,
                    StatusDetail = "Pause requested; waiting for the current stage."
                };
            }
            else
            {
                updated = run with
                {
                    State = AutomationState.Paused,
                    ActiveDispatchToken = null,
                    ScheduledFor = null,
                    PauseAfterCurrentStage = false,
                    UpdatedAt = now,
                    Revision = run.Revision + 1,
                    StatusDetail = "Paused."
                };
            }

            ledger.Runs[index] = updated;
            return (updated, true);
        }, cancellationToken);

    public async Task<AutomationRun> ResumeAsync(
        Guid runId,
        DateTimeOffset scheduledFor,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default,
        bool allowStopped = false) =>
        await MutateAsync((ledger, now) =>
        {
            int index = FindRun(ledger, runId);
            AutomationRun run = ledger.Runs[index];
            CheckRevision(run, expectedRevision);
            if (run.State != AutomationState.Paused &&
                !(allowStopped && run.State is AutomationState.Stopped or AutomationState.Failed))
            {
                throw new InvalidOperationException("Only a paused automation run can be resumed.");
            }

            if (ledger.Runs.Any(candidate => candidate.Id != runId &&
                candidate.State is AutomationState.Running or AutomationState.Scheduled))
            {
                throw new InvalidOperationException(
                    "An active automation run must finish or stop before resuming this run.");
            }

            // Old ledgers lack WorkflowPhase. Their old implementer/reviewer position
            // cannot safely bypass the plan review gate, so resume them at Planning.
            AutomationRun normalized = NormalizePhaseRole(run);
            bool positionChanged = normalized.WorkflowPhase != run.WorkflowPhase ||
                normalized.NextRole != run.NextRole;

            // Old iteration-cap pauses also re-enter through the current plan gate.
            if (string.Equals(run.StatusDetail, LegacyIterationLimitDetail, StringComparison.Ordinal))
            {
                normalized = normalized with
                {
                    WorkflowPhase = AutomationWorkflowPhase.Planning,
                    NextRole = AutomationRole.Coordinator,
                    PlanApproved = false,
                    DeliveryEvidence = null,
                    FinalVerificationEvidence = null
                };
                positionChanged = true;
            }

            bool interruptedStage = string.Equals(run.StatusDetail, InterruptedStageDetail, StringComparison.Ordinal) ||
                string.Equals(run.StatusDetail, OrphanedStageDetail, StringComparison.Ordinal) ||
                run.State is AutomationState.Stopped or AutomationState.Failed;
            if (interruptedStage && !positionChanged)
            {
                normalized = RouteInterruptedImplementerToReviewer(normalized);
            }

            AutomationRun updated = normalized with
            {
                State = AutomationState.Scheduled,
                ScheduledFor = scheduledFor,
                ActiveDispatchToken = null,
                PauseAfterCurrentStage = false,
                UpdatedAt = now,
                Revision = run.Revision + 1,
                StatusDetail = null
            };
            ledger.Runs[index] = updated;
            return (updated, true);
        }, cancellationToken);

    public async Task<AutomationRun> StopAsync(
        Guid runId,
        CancellationToken cancellationToken = default) =>
        await MutateAsync((ledger, now) =>
        {
            int index = FindRun(ledger, runId);
            AutomationRun run = ledger.Runs[index];
            if (run.State == AutomationState.Stopped)
            {
                return (run, false);
            }

            if (run.State is AutomationState.Completed or AutomationState.Failed)
            {
                throw new InvalidOperationException($"A {run.State} automation run cannot be stopped.");
            }

            AutomationRun updated = run with
            {
                State = AutomationState.Stopped,
                ScheduledFor = null,
                ActiveDispatchToken = null,
                PauseAfterCurrentStage = false,
                UpdatedAt = now,
                Revision = run.Revision + 1,
                StatusDetail = "Stopped by the user."
            };
            ledger.Runs[index] = updated;
            return (updated, true);
        }, cancellationToken);

    private static AutomationRun AdvanceWorkflowStage(
        AutomationRun updated,
        AutomationRun original,
        AutomationStepResult result)
    {
        updated = updated with { CurrentRoleRetryCount = 0 };

        return (original.WorkflowPhase, original.NextRole) switch
        {
            (AutomationWorkflowPhase.Planning, AutomationRole.Coordinator) => updated with
            {
                CoordinatorPlan = result.Summary,
                CoordinatorPlanningPlan = result.PlanningPlan,
                PlanApproved = false,
                WorkflowPhase = AutomationWorkflowPhase.PlanReview,
                NextRole = AutomationRole.Reviewer
            },
            (AutomationWorkflowPhase.PlanReview, AutomationRole.Reviewer) => updated with
            {
                PlanApproved = result.PlanApproved,
                LastReviewSummary = result.Summary,
                LastReviewInstruction = result.Instruction,
                LastReviewerFeedbackFingerprint = FingerprintReviewerFeedback(result.Instruction),
                LastReviewerFeedbackInstruction = result.Instruction,
                WorkflowPhase = result.PlanApproved
                    ? AutomationWorkflowPhase.Implementation
                    : AutomationWorkflowPhase.Planning,
                NextRole = AutomationRole.Coordinator
            },
            (AutomationWorkflowPhase.Implementation, AutomationRole.Coordinator) =>
                TryGetCoordinatorNextRole(result.Instruction, out AutomationRole requestedRole) &&
                requestedRole == AutomationRole.Reviewer
                    ? updated with
                    {
                        WorkflowPhase = AutomationWorkflowPhase.Verification,
                        NextRole = AutomationRole.Reviewer
                    }
                    : updated with { NextRole = AutomationRole.Implementer },
            (AutomationWorkflowPhase.Implementation, AutomationRole.Implementer) => updated with
            {
                WorkflowPhase = AutomationWorkflowPhase.Verification,
                NextRole = AutomationRole.Reviewer
            },
            (AutomationWorkflowPhase.Verification, AutomationRole.Reviewer) => updated with
            {
                LastReviewSummary = result.Summary,
                LastReviewInstruction = result.Instruction,
                LastReviewerFeedbackFingerprint = FingerprintReviewerFeedback(result.Instruction),
                LastReviewerFeedbackInstruction = result.Instruction,
                WorkflowPhase = result.Complete
                    ? AutomationWorkflowPhase.Delivery
                    : AutomationWorkflowPhase.Implementation,
                NextRole = AutomationRole.Coordinator,
                Iteration = result.Complete ? original.Iteration : IncrementIteration(original.Iteration)
            },
            (AutomationWorkflowPhase.Delivery, AutomationRole.Coordinator) => updated with
            {
                NextRole = AutomationRole.Implementer
            },
            (AutomationWorkflowPhase.Delivery, AutomationRole.Implementer) =>
                string.IsNullOrWhiteSpace(result.DeliveryEvidence)
                    ? updated with
                    {
                        WorkflowPhase = AutomationWorkflowPhase.Implementation,
                        NextRole = AutomationRole.Coordinator,
                        StatusDetail = "Service launch evidence was missing; Commander review is required."
                    }
                    : updated with
                    {
                        DeliveryEvidence = result.DeliveryEvidence,
                        WorkflowPhase = AutomationWorkflowPhase.FinalVerification,
                        NextRole = AutomationRole.Reviewer
                    },
            (AutomationWorkflowPhase.FinalVerification, AutomationRole.Reviewer) => updated with
            {
                LastReviewSummary = result.Summary,
                LastReviewInstruction = result.Instruction,
                LastReviewerFeedbackFingerprint = FingerprintReviewerFeedback(result.Instruction),
                LastReviewerFeedbackInstruction = result.Instruction,
                WorkflowPhase = AutomationWorkflowPhase.Implementation,
                NextRole = AutomationRole.Coordinator,
                Iteration = IncrementIteration(original.Iteration),
                DeliveryEvidence = null,
                FinalVerificationEvidence = null
            },
            _ => throw new InvalidOperationException(
                $"The workflow cannot advance from {original.WorkflowPhase}/{original.NextRole}.")
        };
    }

    private static AutomationRun NormalizePhaseRole(AutomationRun run)
    {
        if (IsRoleValidForPhase(run.WorkflowPhase, run.NextRole) &&
            (run.WorkflowPhase is AutomationWorkflowPhase.Planning or AutomationWorkflowPhase.PlanReview || run.PlanApproved))
        {
            return run;
        }

        return run with
        {
            WorkflowPhase = AutomationWorkflowPhase.Planning,
            NextRole = AutomationRole.Coordinator,
            PlanApproved = false,
            DeliveryEvidence = null,
            FinalVerificationEvidence = null
        };
    }

    private static AutomationRun RouteInterruptedImplementerToReviewer(AutomationRun run) =>
        (run.WorkflowPhase, run.NextRole) switch
        {
            (AutomationWorkflowPhase.Implementation, AutomationRole.Implementer) => run with
            {
                WorkflowPhase = AutomationWorkflowPhase.Verification,
                NextRole = AutomationRole.Reviewer
            },
            (AutomationWorkflowPhase.Delivery, AutomationRole.Implementer) => run with
            {
                WorkflowPhase = AutomationWorkflowPhase.Verification,
                NextRole = AutomationRole.Reviewer
            },
            _ => run
        };

    private static bool TryGetCoordinatorNextRole(string instruction, out AutomationRole role)
    {
        string trimmed = instruction.Trim();
        if (trimmed.StartsWith("Reviewer verification:", StringComparison.OrdinalIgnoreCase))
        {
            role = AutomationRole.Reviewer;
            return true;
        }

        if (trimmed.StartsWith("Implementer action:", StringComparison.OrdinalIgnoreCase))
        {
            role = AutomationRole.Implementer;
            return true;
        }

        role = default;
        return false;
    }

    private static bool IsRoleValidForPhase(AutomationWorkflowPhase phase, AutomationRole role) =>
        (phase, role) switch
        {
            (AutomationWorkflowPhase.Planning, AutomationRole.Coordinator) => true,
            (AutomationWorkflowPhase.PlanReview, AutomationRole.Reviewer) => true,
            (AutomationWorkflowPhase.Implementation, AutomationRole.Coordinator or AutomationRole.Implementer) => true,
            (AutomationWorkflowPhase.Verification, AutomationRole.Reviewer) => true,
            (AutomationWorkflowPhase.Delivery, AutomationRole.Coordinator or AutomationRole.Implementer) => true,
            (AutomationWorkflowPhase.FinalVerification, AutomationRole.Reviewer) => true,
            _ => false
        };

    private static AutomationRun ScheduleSameRoleRetry(
        AutomationRun updated,
        AutomationRun original,
        DateTimeOffset completedAt,
        string failureDetail)
    {
        int retryCount = original.CurrentRoleRetryCount == int.MaxValue
            ? int.MaxValue
            : original.CurrentRoleRetryCount + 1;

        if (original.PauseAfterCurrentStage)
        {
            return updated with
            {
                State = AutomationState.Paused,
                ScheduledFor = null,
                ActiveDispatchToken = null,
                PauseAfterCurrentStage = false,
                CurrentRoleRetryCount = retryCount,
                NextInstruction = original.NextInstruction,
                StatusDetail = $"{failureDetail} Paused before retrying {original.NextRole}."
            };
        }

        TimeSpan delay = RetryDelay(retryCount);
        return updated with
        {
            State = AutomationState.Scheduled,
            NextRole = original.NextRole,
            Iteration = original.Iteration,
            ScheduledFor = completedAt + delay,
            ActiveDispatchToken = null,
            PauseAfterCurrentStage = false,
            CurrentRoleRetryCount = retryCount,
            NextInstruction = original.NextInstruction,
            StatusDetail = $"{failureDetail} Retrying {original.NextRole} (attempt {retryCount + 1}) in {delay.TotalSeconds:0} seconds."
        };
    }

    private static TimeSpan RetryDelay(int retryCount)
    {
        int exponent = Math.Clamp(retryCount - 1, 0, 5);
        double seconds = Math.Min(Math.Pow(2, exponent), MaximumRetryDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static string? GetInvalidWorkflowOutputDetail(
        AutomationRun run,
        AutomationStepResult result)
    {
        AutomationRole role = run.NextRole;
        if (!IsRoleValidForPhase(run.WorkflowPhase, role))
        {
            return $"The {role} claim does not match workflow phase {run.WorkflowPhase}.";
        }

        if (string.IsNullOrWhiteSpace(result.Summary))
        {
            return $"The {role} returned an empty summary.";
        }

        if (run.WorkflowPhase == AutomationWorkflowPhase.Planning && role == AutomationRole.Coordinator)
        {
            if (result.PlanningPlan is null)
                return "The Planning Coordinator must return a structured planning object.";
            if (!result.PlanningPlan.TryValidate(out string? planningError))
                return planningError ?? "The structured planning object is incomplete.";
        }

        bool completionCandidate = run.WorkflowPhase is AutomationWorkflowPhase.Verification or AutomationWorkflowPhase.FinalVerification &&
            role == AutomationRole.Reviewer && result.Complete;
        bool finalApproval = run.WorkflowPhase == AutomationWorkflowPhase.FinalVerification &&
            role == AutomationRole.Reviewer && result.Complete;
        bool instructionRequired = !completionCandidate;
        if (instructionRequired && string.IsNullOrWhiteSpace(result.Instruction))
        {
            return role == AutomationRole.Reviewer
                ? "The reviewer returned no instruction for the next workflow step."
                : $"The {role} returned no instruction for the next role.";
        }

        if (result.Complete && !completionCandidate)
        {
            return "Only a reviewer can submit a completion candidate.";
        }

        if (finalApproval && string.IsNullOrWhiteSpace(result.DeliveryEvidence))
        {
            return "The final reviewer did not provide independent service verification evidence.";
        }

        if (run.WorkflowPhase is not (AutomationWorkflowPhase.Planning or AutomationWorkflowPhase.PlanReview) &&
            !run.PlanApproved)
        {
            return "Implementation is blocked until the reviewer approves the plan.";
        }

        return null;
    }

    private static string FingerprintReviewerFeedback(string instruction)
    {
        string normalized = string.Join(
            ' ',
            instruction
                .Normalize(NormalizationForm.FormKC)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static AutomationStepClaim ToClaim(
        AutomationRun run,
        Guid token,
        DateTimeOffset claimedAt) =>
        new(
            run.Id,
            token,
            run.Revision,
            run.NextRole,
            run.Iteration,
            run.ProjectDirectory,
            run.ApprovedPlanSnapshot,
            run.Options,
            run.TotalTokenUsage,
            run.LastSummary,
            run.NextInstruction,
            claimedAt)
        {
            WorkflowPhase = run.WorkflowPhase,
            PlanApproved = run.PlanApproved,
            CoordinatorPlan = run.CoordinatorPlan,
            CoordinatorPlanningPlan = run.CoordinatorPlanningPlan,
            LastReviewSummary = run.LastReviewSummary,
            LastReviewInstruction = run.LastReviewInstruction,
            DeliveryEvidence = run.DeliveryEvidence,
            ImprovementCycle = run.ImprovementCycle,
            ImprovementHistory = run.ImprovementHistory
        };

    private static string ImprovementPlanFingerprint(AutomationPlanningOutput? plan) =>
        FingerprintReviewerFeedback(plan is null ? "" : plan.GoalsAndScope + "\n" +
            string.Join("\n", plan.DesignFilesTasks) + "\n" + string.Join("\n", plan.AcceptanceTests));

    private async Task<T> ReadAsync<T>(
        Func<AutomationLedger, T> read,
        CancellationToken cancellationToken)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            ProjectArchiveService.ThrowIfRelocated(_projectDirectory);
            Directory.CreateDirectory(_storeDirectory);
            await using FileStream fileLock = await AcquireFileLockAsync(cancellationToken);
            (AutomationLedger ledger, _) = await ReadLedgerAsync(cancellationToken);
            return read(ledger);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<T> MutateAsync<T>(
        Func<AutomationLedger, DateTimeOffset, (T Result, bool Changed)> mutation,
        CancellationToken cancellationToken)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            ProjectArchiveService.ThrowIfRelocated(_projectDirectory);
            Directory.CreateDirectory(_storeDirectory);
            await using FileStream fileLock = await AcquireFileLockAsync(cancellationToken);
            (AutomationLedger ledger, byte[]? originalBytes) = await ReadLedgerAsync(cancellationToken);
            (T result, bool changed) = mutation(ledger, DateTimeOffset.UtcNow);
            if (changed)
            {
                VerifyUnchanged(originalBytes);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ledger, JsonOptions);
                await AtomicWriteAsync(_storePath, bytes, cancellationToken);
            }

            return result;
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (attempt < 200)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private async Task<(AutomationLedger Ledger, byte[]? OriginalBytes)> ReadLedgerAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return (new AutomationLedger(), null);
        }

        byte[] bytes = await File.ReadAllBytesAsync(_storePath, cancellationToken);
        try
        {
            AutomationLedger? ledger = JsonSerializer.Deserialize<AutomationLedger>(bytes, JsonOptions);
            if (ledger is null || ledger.Version != StoreVersion || ledger.Runs is null)
            {
                throw new InvalidDataException("The automation store has an unsupported structure.");
            }

            ProjectArchiveService.RebindRuns(_projectDirectory, ledger.Runs);
            ValidateLedger(ledger);
            return (ledger, bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The automation store is malformed and was left unchanged: {_storePath}",
                exception);
        }
    }

    private void ValidateLedger(AutomationLedger ledger)
    {
        var ids = new HashSet<Guid>();
        foreach (AutomationRun run in ledger.Runs)
        {
            if (run.Id == Guid.Empty || !ids.Add(run.Id) ||
                string.IsNullOrWhiteSpace(run.ApprovedPlanSnapshot) ||
                !Enum.IsDefined(run.State) ||
                !Enum.IsDefined(run.NextRole) ||
                !Enum.IsDefined(run.WorkflowPhase) ||
                !PathsEqual(run.ProjectDirectory, _projectDirectory) ||
                run.Iteration < 1 ||
                run.Revision < 0 || run.TotalTokenUsage < 0)
            {
                throw new InvalidDataException("The automation store contains an invalid run.");
            }

            ValidateOptions(run.Options);

            bool mustHaveToken = run.State == AutomationState.Running;
            if (mustHaveToken != (run.ActiveDispatchToken is not null) ||
                (run.State == AutomationState.Scheduled) != (run.ScheduledFor is not null))
            {
                throw new InvalidDataException("The automation store contains inconsistent dispatch state.");
            }
        }
    }

    private void VerifyUnchanged(byte[]? originalBytes)
    {
        if (originalBytes is null)
        {
            if (File.Exists(_storePath))
            {
                throw new AutomationStoreConflictException(
                    _storePath,
                    "The automation store appeared while it was being updated.");
            }

            return;
        }

        if (!File.Exists(_storePath) ||
            !File.ReadAllBytes(_storePath).AsSpan().SequenceEqual(originalBytes))
        {
            throw new AutomationStoreConflictException(
                _storePath,
                "The automation store changed outside Terminal Notes.");
        }
    }

    private static async Task AtomicWriteAsync(
        string destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(destination)
            ?? throw new DirectoryNotFoundException(destination);
        string temporaryPath = Path.Combine(directory, $".terminalnotes-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
            {
                File.Move(temporaryPath, destination, overwrite: true);
            }
            else
            {
                File.Move(temporaryPath, destination);
            }

            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try { File.Delete(temporaryPath); } catch (IOException) { }
            }
        }
    }

    private static int FindRun(AutomationLedger ledger, Guid runId)
    {
        int index = ledger.Runs.FindIndex(run => run.Id == runId);
        return index >= 0
            ? index
            : throw new KeyNotFoundException($"Automation run was not found: {runId}");
    }

    private static void CheckRevision(AutomationRun run, long? expectedRevision)
    {
        if (expectedRevision is not null && run.Revision != expectedRevision)
        {
            throw new InvalidOperationException("The automation run changed after it was displayed.");
        }
    }

    private static void ValidateOptions(AutomationExecutionOptions? options)
    {
        if (options is null)
        {
            throw new InvalidDataException("The automation ledger contains missing execution options.");
        }
    }

    private static int IncrementIteration(int iteration) =>
        iteration == int.MaxValue ? int.MaxValue : iteration + 1;

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class AutomationLedger
    {
        public int Version { get; set; } = StoreVersion;
        public List<AutomationRun> Runs { get; set; } = [];
    }
}

public sealed class AutomationStoreConflictException : IOException
{
    public AutomationStoreConflictException(string filePath, string message) : base(message) =>
        FilePath = filePath;

    public string FilePath { get; }
}
