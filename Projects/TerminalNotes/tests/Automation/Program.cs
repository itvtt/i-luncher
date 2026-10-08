using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TerminalNotes.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("questions never touch disk", QuestionsNeverTouchDisk),
    ("duplicate survives service recreation", DuplicateSurvivesServiceRecreation),
    ("duplicate reservation is serialized", DuplicateReservationIsSerialized),
    ("malformed ledger fails closed", MalformedLedgerFailsClosed),
    ("scheduled run is claimed once", ScheduledRunClaimedOnce),
    ("startup recovery pauses scheduled work", StartupRecoveryPausesScheduledWork),
    ("pause waits for the current stage and stop rejects stale completion", PauseAndStopProtectState),
    ("reviewed plan is required before implementation", PlanReviewIsRequiredBeforeImplementation),
    ("planning requires a structured non-perfunctory plan", PlanningOutputRequiresStructuredContract),
    ("every implementation review returns to commander", ReviewFeedbackReturnsThroughCommander),
    ("delivery and final reviewer evidence gate completion", DeliveryEvidenceGatesCompletion),
    ("invalid stage output retries the same role before advancing", BlankSuccessfulOutputFails),
    ("stage failures retry without a cap and obey pause or stop", StageFailuresRetryWithoutCap),
    ("legacy automation run loads without reviewer feedback fields", LegacyRunWithoutReviewerFeedbackLoads),
    ("interrupted run requires explicit resume", InterruptedRunRequiresResume),
    ("automatic restart restores only unfinished authorized work", AutomaticRestartRecovery),
    ("explicit stopped resume preserves history and reviews partial implementation", ExplicitStoppedResume),
    ("interrupted implementer stages route to review", InterruptedStageResumeRoutesImplementerToReviewer),
    ("interrupted delivery is reviewed before service reuse", InterruptedDeliveryRoutesThroughVerification),
    ("watchdog recovery reviews partial writes then returns control to commander", WatchdogRecoveryReturnsThroughCommander),
    ("orphaned running claim recovery checks token and revision", OrphanedRunningClaimRecoveryIsConditional),
    ("legacy active positions are migrated through plan review", LegacyActivePositionsResumeThroughPlanReview),
    ("legacy paused run cannot resume beside active work", LegacyPausedResumeRejectsActiveRun),
    ("stopped runs cannot resume and failed stages retry", TerminalRunsCannotResume),
    ("new approved plan retires legacy paused history atomically", ApprovedPlanReplacesLegacyPause),
    ("new approved plan rejects active runs without ledger changes", ApprovedPlanRejectsActiveRuns),
    ("concurrent approved starts create only one active run", ConcurrentApprovedStartsCreateOneActiveRun),
    ("continuous improvement verifies each cycle and respects pause stop and duplicate plans", ContinuousImprovementCycles),
    ("live improvement preferences and deadline preserve results and stop new cycles", LiveImprovementOptions),
};

int failures = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

return failures == 0 ? 0 : 1;

static async Task ContinuousImprovementCycles()
{
    foreach (bool pause in new[] { false, true })
    {
        using var temp = new TempDirectory();
        var store = new AutomationStore(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var run = await store.StartApprovedPlanAsync("Improve within the project scope",
            options: new AutomationExecutionOptions { ContinuousImprovement = true }, scheduledFor: now);
        var claim = Single(await store.ClaimDueAsync(now));
        while (claim.WorkflowPhase != AutomationWorkflowPhase.FinalVerification)
            claim = NotNull((await store.CompleteStepAsync(Success(claim,
                claim.WorkflowPhase == AutomationWorkflowPhase.Verification, 1, now))).NextClaim);
        if (pause) await store.PauseAsync(run.Id);
        var final = Success(claim, true, 1, now);
        // PauseAsync increments revision; fetch the owned current revision.
        final = final with { ExpectedRevision = (await store.GetAsync(run.Id))!.Revision };
        var completed = await store.CompleteStepAsync(final);
        True(completed.Accepted);
        Equal(pause ? AutomationState.Paused : AutomationState.Scheduled, completed.Run.State);
        Equal(2, completed.Run.ImprovementCycle);
        Equal(1, completed.Run.ImprovementHistory.Count);
        Equal(AutomationWorkflowPhase.Planning, completed.Run.WorkflowPhase);
        False(completed.Run.PlanApproved);
        Equal(null, completed.Run.DeliveryEvidence);
        True(completed.Run.ImprovementHistory[0].Evidence.Contains("HTTP 200"));
        False((await store.CompleteStepAsync(final)).Accepted);
        Equal(0, (await store.ClaimDueAsync(now.AddSeconds(29))).Count);
        var loaded = await new AutomationStore(temp.Path).GetAsync(run.Id);
        True(loaded!.Options.ContinuousImprovement && loaded.ImprovementHistory.Count == 1);
        if (pause) { Equal(0, (await store.ClaimDueAsync(now.AddHours(1))).Count); continue; }
        var next = Single(await store.ClaimDueAsync(now.AddSeconds(31)));
        Equal(2, next.ImprovementCycle);
        Equal(AutomationRole.Coordinator, next.Role);
        var duplicate = await store.CompleteStepAsync(Success(next, false, 1, now.AddSeconds(31)));
        Equal(AutomationState.Scheduled, duplicate.Run.State);
        Equal(AutomationWorkflowPhase.Planning, duplicate.Run.WorkflowPhase);
        Equal(null, duplicate.NextClaim);
        var retry = Single(await store.ClaimDueAsync(now.AddMinutes(2)));
        var novel = Success(retry, false, 1, now.AddMinutes(2)) with
        { PlanningPlan = ValidPlanningPlan() with { GoalsAndScope = "Fix a newly observed edge case with an independent regression test." } };
        var approved = await store.CompleteStepAsync(novel);
        Equal(AutomationWorkflowPhase.PlanReview, NotNull(approved.NextClaim).WorkflowPhase);
        await store.StopAsync(run.Id);
        False((await store.CompleteStepAsync(Success(approved.NextClaim!, false, 1, now.AddMinutes(2)))).Accepted);
        Equal(0, (await store.ClaimDueAsync(now.AddHours(1))).Count);
    }
}

static async Task LiveImprovementOptions()
{
    foreach (string scenario in new[] { "disable", "expire", "future", "scheduled-disable", "scheduled-expire", "race" })
    {
        using var temp = new TempDirectory();
        var store = new AutomationStore(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var run = await store.StartApprovedPlanAsync("Continuous improvement deadline",
            options: new AutomationExecutionOptions { ContinuousImprovement = true }, scheduledFor: now);
        var claim = Single(await store.ClaimDueAsync(now));
        while (claim.WorkflowPhase != AutomationWorkflowPhase.FinalVerification)
            claim = NotNull((await store.CompleteStepAsync(Success(claim,
                claim.WorkflowPhase == AutomationWorkflowPhase.Verification, 1, now))).NextClaim);
        DateTimeOffset? until = scenario is "expire" or "scheduled-expire" or "future" ? now.AddMinutes(1) : null;
        var changed = await store.SetContinuousImprovementAsync(run.Id, scenario != "disable", until);
        Equal(claim.Revision, changed.Revision);
        Equal(claim.DispatchToken, changed.ActiveDispatchToken);
        var persisted = await new AutomationStore(temp.Path).GetAsync(run.Id);
        Equal(until, persisted!.Options.ImprovementUntil);
        var final = Success(claim, true, 1, scenario == "expire" ? now.AddMinutes(2) : now);
        AutomationCompletionResult completion;
        if (scenario == "race")
        {
            var completionTask = store.CompleteStepAsync(final);
            var disableTask = new AutomationStore(temp.Path).SetContinuousImprovementAsync(run.Id, false, null);
            await Task.WhenAll(completionTask, disableTask);
            completion = await completionTask;
        }
        else completion = await store.CompleteStepAsync(final);
        True(completion.Accepted);
        if (scenario == "scheduled-disable") await store.SetContinuousImprovementAsync(run.Id, false, null);
        if (scenario == "scheduled-expire") Equal(0, (await store.ClaimDueAsync(now.AddMinutes(2))).Count);
        var saved = (await store.GetAsync(run.Id))!;
        Equal(scenario == "future" ? AutomationState.Scheduled : AutomationState.Completed, saved.State);
        if (scenario == "future") Equal(1, (await store.ClaimDueAsync(now.AddSeconds(31))).Count);
        else
        {
            True(!string.IsNullOrWhiteSpace(saved.FinalVerificationEvidence));
            Equal(0, (await store.ClaimDueAsync(now.AddHours(1))).Count);
        }
        False((await store.CompleteStepAsync(final)).Accepted);
    }
    using var pausedTemp = new TempDirectory();
    var pausedStore = new AutomationStore(pausedTemp.Path);
    var paused = await pausedStore.StartApprovedPlanAsync("Pause must survive an option change");
    var active = Single(await pausedStore.ClaimDueAsync(DateTimeOffset.UtcNow));
    await pausedStore.PauseAsync(paused.Id);
    var optionsChanged = await pausedStore.SetContinuousImprovementAsync(paused.Id, true, DateTimeOffset.UtcNow.AddHours(2));
    True(optionsChanged.PauseAfterCurrentStage);
    Equal(active.DispatchToken, optionsChanged.ActiveDispatchToken);
    Equal(AutomationState.Paused, (await pausedStore.CompleteStepAsync(Success(active, false, 1, DateTimeOffset.UtcNow))).Run.State);
}

static async Task QuestionsNeverTouchDisk()
{
    using var temp = new TempDirectory();
    string project = Path.Combine(temp.Path, "new-project");
    const string secretQuestion = "Will this secret be hashed?";
    var journal = new ProjectWorkJournal(project);

    JournalSubmissionResult result = await journal.SubmitAsync(
        TerminalInputMode.Question,
        secretQuestion,
        slot: 1);

    Equal(JournalSubmissionKind.NotPersisted, result.Kind);
    False(Directory.Exists(project));

    Directory.CreateDirectory(project);
    string existing = Path.Combine(project, "existing.txt");
    await File.WriteAllTextAsync(existing, "unchanged");
    DateTime before = File.GetLastWriteTimeUtc(existing);
    await journal.SubmitAsync(TerminalInputMode.Question, secretQuestion, slot: 1);
    Equal("unchanged", await File.ReadAllTextAsync(existing));
    Equal(before, File.GetLastWriteTimeUtc(existing));
    Equal(1, Directory.GetFiles(project, "*", SearchOption.AllDirectories).Length);
}

static async Task DuplicateSurvivesServiceRecreation()
{
    using var temp = new TempDirectory();
    DateTimeOffset submitted = DateTimeOffset.Parse("2026-09-19T09:30:00+09:00");
    var firstService = new ProjectWorkJournal(temp.Path);
    JournalSubmissionResult first = await firstService.SubmitAsync(
        TerminalInputMode.Plan,
        "cafe\u0301\r\nnext step  ",
        slot: 2,
        submittedAt: submitted);
    Equal(JournalSubmissionKind.Reserved, first.Kind);
    NotNull(first.Entry);
    True(File.Exists(Path.Combine(temp.Path, first.Entry!.DocumentPath)));

    var secondService = new ProjectWorkJournal(temp.Path);
    JournalSubmissionResult duplicate = await secondService.SubmitAsync(
        TerminalInputMode.Plan,
        "caf\u00e9\nnext step",
        slot: 7);
    Equal(JournalSubmissionDisposition.Duplicate, duplicate.Disposition);
    Equal(first.Entry.Id, duplicate.DuplicateEntry?.Id);
    True(duplicate.CanForceManualRerun);

    JournalSubmissionResult automationDuplicate = await secondService.SubmitAsync(
        TerminalInputMode.Plan,
        "caf\u00e9\nnext step",
        slot: 7,
        isAutomation: true);
    Equal(JournalSubmissionKind.Duplicate, automationDuplicate.Kind);
    False(automationDuplicate.CanForceManualRerun);

    JournalSubmissionResult forced = await secondService.SubmitAsync(
        TerminalInputMode.Plan,
        "caf\u00e9\nnext step",
        slot: 7,
        forceManualRerun: true);
    Equal(JournalSubmissionKind.Reserved, forced.Kind);
    Equal(2, (await secondService.LoadAsync()).Count);
}

static async Task MalformedLedgerFailsClosed()
{
    using var temp = new TempDirectory();
    string directory = Path.Combine(temp.Path, "terminal-notes");
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "work-journal.json");
    byte[] malformed = Encoding.UTF8.GetBytes("{ this is not JSON");
    await File.WriteAllBytesAsync(path, malformed);

    await ThrowsAsync<InvalidDataException>(() =>
        new ProjectWorkJournal(temp.Path).SubmitAsync(
            TerminalInputMode.Build,
            "do work",
            slot: 1));
    SequenceEqual(malformed, await File.ReadAllBytesAsync(path));
    Equal(0, Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories).Length);

    string automationPath = Path.Combine(directory, "automation-runs.json");
    await File.WriteAllBytesAsync(automationPath, malformed);
    await ThrowsAsync<InvalidDataException>(() => new AutomationStore(temp.Path).LoadAsync());
    SequenceEqual(malformed, await File.ReadAllBytesAsync(automationPath));
}

static async Task DuplicateReservationIsSerialized()
{
    using var temp = new TempDirectory();
    var first = new ProjectWorkJournal(temp.Path);
    var second = new ProjectWorkJournal(temp.Path);
    Task<JournalSubmissionResult>[] attempts =
    [
        first.SubmitAsync(TerminalInputMode.Command, "same command", slot: 1),
        second.SubmitAsync(TerminalInputMode.Command, "same command", slot: 2)
    ];

    JournalSubmissionResult[] results = await Task.WhenAll(attempts);
    Equal(1, results.Count(result => result.Kind == JournalSubmissionKind.Reserved));
    Equal(1, results.Count(result => result.Kind == JournalSubmissionKind.Duplicate));
    Equal(1, Directory.GetFiles(
        Path.Combine(temp.Path, "terminal-notes", "commands"),
        "*.md").Length);
}

static async Task ScheduledRunClaimedOnce()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.Parse("2026-09-19T10:00:00+09:00");
    var store = new AutomationStore(temp.Path);
    AutomationRun draft = await store.CreateDraftAsync(
        "approved plan",
        maxIterations: 3,
        new AutomationExecutionOptions(
            CoordinatorModel: "coordinator",
            ImplementerModel: "implementer",
            ReviewerModel: "reviewer",
            TokenBudget: 5000,
            MaxStageMinutes: 30),
        createdAt: now.AddMinutes(-5));
    Equal((int?)3, draft.MaxIterations);
    await store.ScheduleAsync(draft.Id, now.AddMinutes(-1), draft.Revision);

    Task<IReadOnlyList<AutomationStepClaim>> firstAttempt =
        new AutomationStore(temp.Path).ClaimDueAsync(now, maxClaims: 1);
    Task<IReadOnlyList<AutomationStepClaim>> secondAttempt =
        new AutomationStore(temp.Path).ClaimDueAsync(now, maxClaims: 1);
    IReadOnlyList<AutomationStepClaim>[] concurrentClaims =
        await Task.WhenAll(firstAttempt, secondAttempt);
    Equal(1, concurrentClaims.Sum(claims => claims.Count));
    AutomationStepClaim onlyClaim = concurrentClaims.SelectMany(claims => claims).Single();
    Equal(AutomationRole.Coordinator, onlyClaim.Role);
    Equal("approved plan", onlyClaim.ApprovedPlanSnapshot);

    Equal(0, (await new AutomationStore(temp.Path).ClaimDueAsync(now.AddHours(1))).Count);
    Equal(AutomationState.Running, (await store.GetAsync(draft.Id))?.State);
}

static async Task StartupRecoveryPausesScheduledWork()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun scheduled = await store.CreateDraftAsync("startup plan", createdAt: now);
    scheduled = await store.ScheduleAsync(scheduled.Id, now, scheduled.Revision);

    IReadOnlyList<AutomationRun> recovered = await store.RecoverInterruptedAsync(
        now.AddMinutes(1),
        includeScheduled: true);

    AutomationRun paused = Single(recovered);
    Equal(scheduled.Id, paused.Id);
    Equal(AutomationState.Paused, paused.State);
    Equal(null, paused.ScheduledFor);
    Equal(AutomationWorkflowPhase.Planning, paused.WorkflowPhase);
    Equal(AutomationRole.Coordinator, paused.NextRole);
    True(paused.StatusDetail?.Contains("예약 작업", StringComparison.Ordinal) == true);
    Equal(0, (await store.ClaimDueAsync(now.AddDays(1))).Count);
}

static async Task PlanReviewIsRequiredBeforeImplementation()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun draft = await store.CreateDraftAsync("initial plan", createdAt: now);
    await store.ScheduleAsync(draft.Id, now);

    AutomationStepClaim coordinator = Single(await store.ClaimDueAsync(now));
    Equal(AutomationWorkflowPhase.Planning, coordinator.WorkflowPhase);
    AutomationStepClaim planReviewer = NotNull((await store.CompleteStepAsync(
        Success(coordinator, complete: false, tokens: 10, now))).NextClaim);
    Equal(AutomationRole.Reviewer, planReviewer.Role);
    Equal(AutomationWorkflowPhase.PlanReview, planReviewer.WorkflowPhase);
    Equal("stage summary", planReviewer.CoordinatorPlan);

    AutomationStepClaim revisionCoordinator = NotNull((await store.CompleteStepAsync(
        Success(planReviewer, complete: false, tokens: 20, now) with
        {
            PlanApproved = false,
            Instruction = "Add a rollback procedure and acceptance checks."
        })).NextClaim);
    Equal(AutomationRole.Coordinator, revisionCoordinator.Role);
    Equal(AutomationWorkflowPhase.Planning, revisionCoordinator.WorkflowPhase);
    Equal("Add a rollback procedure and acceptance checks.", revisionCoordinator.LastReviewInstruction);

    AutomationStepClaim secondPlanReviewer = NotNull((await store.CompleteStepAsync(
        Success(revisionCoordinator, complete: false, tokens: 30, now) with
        {
            Summary = "Revised plan with rollback procedure and acceptance checks."
        })).NextClaim);
    Equal(AutomationWorkflowPhase.PlanReview, secondPlanReviewer.WorkflowPhase);
    Equal("Revised plan with rollback procedure and acceptance checks.", secondPlanReviewer.CoordinatorPlan);

    AutomationStepClaim implementationCoordinator = NotNull((await store.CompleteStepAsync(
        Success(secondPlanReviewer, complete: false, tokens: 40, now))).NextClaim);
    Equal(AutomationWorkflowPhase.Implementation, implementationCoordinator.WorkflowPhase);
    True(implementationCoordinator.PlanApproved);
    AutomationStepClaim implementer = NotNull((await store.CompleteStepAsync(
        Success(implementationCoordinator, complete: false, tokens: 50, now))).NextClaim);
    Equal(AutomationRole.Implementer, implementer.Role);
    Equal(AutomationWorkflowPhase.Implementation, implementer.WorkflowPhase);
}

static async Task PlanningOutputRequiresStructuredContract()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun draft = await store.CreateDraftAsync("original plan", createdAt: now);
    await store.ScheduleAsync(draft.Id, now);

    AutomationStepClaim coordinator = Single(await store.ClaimDueAsync(now));
    AutomationCompletionResult invalid = await store.CompleteStepAsync(
        Success(coordinator, complete: false, tokens: 1, now) with { PlanningPlan = null });
    Equal(AutomationRole.Coordinator, invalid.Run.NextRole);
    Equal(AutomationWorkflowPhase.Planning, invalid.Run.WorkflowPhase);
    Equal(null, invalid.Run.CoordinatorPlan);
    Equal(null, invalid.Run.CoordinatorPlanningPlan);
    Equal(AutomationState.Scheduled, invalid.Run.State);

    AutomationStepClaim retry = Single(await store.ClaimDueAsync(now.AddSeconds(2)));
    AutomationCompletionResult accepted = await store.CompleteStepAsync(
        Success(retry, complete: false, tokens: 1, now.AddSeconds(2)));
    Equal(AutomationWorkflowPhase.PlanReview, accepted.Run.WorkflowPhase);
    Equal(AutomationRole.Reviewer, accepted.Run.NextRole);
    Equal("stage summary", accepted.Run.CoordinatorPlan);
    NotNull(accepted.Run.CoordinatorPlanningPlan);
    NotNull(NotNull(accepted.NextClaim).CoordinatorPlanningPlan);
}

static async Task ReviewFeedbackReturnsThroughCommander()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationStepClaim implementer = await StartAtImplementerAsync(store, now);
    AutomationStepClaim reviewer = NotNull((await store.CompleteStepAsync(
        Success(implementer, complete: false, tokens: 10, now))).NextClaim);
    Equal(AutomationWorkflowPhase.Verification, reviewer.WorkflowPhase);

    const string repeatedFeedback = "Add a startup health check and exercise it in the acceptance path.";
    AutomationStepClaim commander = NotNull((await store.CompleteStepAsync(
        Success(reviewer, complete: false, tokens: 20, now) with
        {
            Complete = false,
            Instruction = repeatedFeedback
        })).NextClaim);
    Equal(AutomationRole.Coordinator, commander.Role);
    Equal(AutomationWorkflowPhase.Implementation, commander.WorkflowPhase);
    Equal(repeatedFeedback, commander.LastReviewInstruction);

    AutomationStepClaim correctionImplementer = NotNull((await store.CompleteStepAsync(
        Success(commander, complete: false, tokens: 30, now))).NextClaim);
    AutomationStepClaim secondReviewer = NotNull((await store.CompleteStepAsync(
        Success(correctionImplementer, complete: false, tokens: 40, now))).NextClaim);
    AutomationCompletionResult repeatedReview = await store.CompleteStepAsync(
        Success(secondReviewer, complete: false, tokens: 50, now) with
        {
            Summary = "The same acceptance gap remains.",
            Instruction = repeatedFeedback
        });

    Equal(AutomationState.Running, repeatedReview.Run.State);
    Equal(AutomationWorkflowPhase.Implementation, repeatedReview.Run.WorkflowPhase);
    Equal(AutomationRole.Coordinator, repeatedReview.Run.NextRole);
    Equal(3, repeatedReview.Run.Iteration);
    False(repeatedReview.Run.StatusDetail?.Contains(
        "same review issue repeated", StringComparison.OrdinalIgnoreCase) == true);
    Equal(AutomationRole.Coordinator, NotNull(repeatedReview.NextClaim).Role);
}

static async Task DeliveryEvidenceGatesCompletion()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationStepClaim implementer = await StartAtImplementerAsync(store, now);
    AutomationStepClaim reviewer = NotNull((await store.CompleteStepAsync(
        Success(implementer, complete: false, tokens: 10, now))).NextClaim);
    AutomationStepClaim commander = NotNull((await store.CompleteStepAsync(
        Success(reviewer, complete: true, tokens: 20, now))).NextClaim);
    Equal(AutomationWorkflowPhase.Delivery, commander.WorkflowPhase);
    Equal(AutomationRole.Coordinator, commander.Role);
    Equal("stage summary", commander.LastReviewSummary);

    AutomationStepClaim deliveryImplementer = NotNull((await store.CompleteStepAsync(
        Success(commander, complete: false, tokens: 30, now))).NextClaim);
    Equal(AutomationWorkflowPhase.Delivery, deliveryImplementer.WorkflowPhase);
    AutomationStepResult missingLaunchEvidence = Success(
        deliveryImplementer,
        complete: false,
        tokens: 40,
        now) with { DeliveryEvidence = null };
    AutomationStepClaim launchRecoveryCommander = NotNull((await store.CompleteStepAsync(
        missingLaunchEvidence)).NextClaim);
    Equal(AutomationWorkflowPhase.Implementation, launchRecoveryCommander.WorkflowPhase);
    Equal(AutomationRole.Coordinator, launchRecoveryCommander.Role);
    True((await store.GetAsync(launchRecoveryCommander.RunId))?.StatusDetail?.Contains(
        "launch evidence", StringComparison.OrdinalIgnoreCase) == true);

    AutomationStepClaim correctionImplementer = NotNull((await store.CompleteStepAsync(
        Success(launchRecoveryCommander, complete: false, tokens: 50, now))).NextClaim);
    AutomationStepClaim postBuildReviewer = NotNull((await store.CompleteStepAsync(
        Success(correctionImplementer, complete: false, tokens: 60, now))).NextClaim);
    AutomationStepClaim deliveryCommander = NotNull((await store.CompleteStepAsync(
        Success(postBuildReviewer, complete: true, tokens: 70, now))).NextClaim);
    AutomationStepClaim realDeliveryImplementer = NotNull((await store.CompleteStepAsync(
        Success(deliveryCommander, complete: false, tokens: 80, now))).NextClaim);
    AutomationStepClaim finalReviewer = NotNull((await store.CompleteStepAsync(
        Success(realDeliveryImplementer, complete: false, tokens: 90, now))).NextClaim);
    Equal(AutomationWorkflowPhase.FinalVerification, finalReviewer.WorkflowPhase);
    True(!string.IsNullOrWhiteSpace(finalReviewer.DeliveryEvidence));

    AutomationCompletionResult completed = await store.CompleteStepAsync(
        Success(finalReviewer, complete: true, tokens: 100, now));
    Equal(AutomationState.Completed, completed.Run.State);
    True(completed.Run.PlanApproved);
    True(!string.IsNullOrWhiteSpace(completed.Run.DeliveryEvidence));
    True(!string.IsNullOrWhiteSpace(completed.Run.FinalVerificationEvidence));
    Equal(null, completed.NextClaim);
}

static async Task<AutomationStepClaim> StartAtImplementerAsync(
    AutomationStore store,
    DateTimeOffset now)
{
    AutomationRun draft = await store.CreateDraftAsync("complete workflow plan", createdAt: now);
    await store.ScheduleAsync(draft.Id, now);
    AutomationStepClaim coordinator = Single(await store.ClaimDueAsync(now));
    AutomationStepClaim planReviewer = NotNull((await store.CompleteStepAsync(
        Success(coordinator, complete: false, tokens: 1, now))).NextClaim);
    AutomationStepClaim implementationCoordinator = NotNull((await store.CompleteStepAsync(
        Success(planReviewer, complete: false, tokens: 1, now))).NextClaim);
    AutomationStepClaim implementer = NotNull((await store.CompleteStepAsync(
        Success(implementationCoordinator, complete: false, tokens: 1, now))).NextClaim);
    return implementer;
}

static async Task PauseAndStopProtectState()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun draft = await store.CreateDraftAsync("plan", 2, createdAt: now);
    await store.ScheduleAsync(draft.Id, now, draft.Revision);
    AutomationStepClaim coordinator = Single(await store.ClaimDueAsync(now));

    AutomationRun pauseRequested = await store.PauseAsync(draft.Id);
    Equal(AutomationState.Running, pauseRequested.State);
    True(pauseRequested.PauseAfterCurrentStage);
    Equal(coordinator.Revision, pauseRequested.Revision);

    AutomationCompletionResult coordinatorResult = await store.CompleteStepAsync(
        Success(coordinator, complete: false, tokens: 10, now.AddMinutes(1)));
    True(coordinatorResult.Accepted);
    Equal(AutomationState.Paused, coordinatorResult.Run.State);
    Equal(AutomationRole.Reviewer, coordinatorResult.Run.NextRole);
    Equal(AutomationWorkflowPhase.PlanReview, coordinatorResult.Run.WorkflowPhase);
    Equal(null, coordinatorResult.NextClaim);
    Equal(0, (await new AutomationStore(temp.Path).ClaimDueAsync(now.AddDays(1))).Count);

    AutomationRun resumed = await store.ResumeAsync(
        draft.Id,
        now.AddMinutes(2),
        coordinatorResult.Run.Revision);
    AutomationStepClaim reviewer = Single(await store.ClaimDueAsync(now.AddMinutes(2)));
    Equal(AutomationRole.Reviewer, reviewer.Role);

    AutomationRun stopped = await store.StopAsync(draft.Id);
    Equal(AutomationState.Stopped, stopped.State);
    AutomationCompletionResult stale = await store.CompleteStepAsync(
        Success(reviewer, complete: false, tokens: 20, now.AddMinutes(3)));
    False(stale.Accepted);
    Equal(AutomationState.Stopped, stale.Run.State);
    Equal(null, stale.NextClaim);
    Equal(AutomationState.Stopped, (await new AutomationStore(temp.Path).GetAsync(resumed.Id))?.State);
}

static async Task BlankSuccessfulOutputFails()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun draft = await store.CreateDraftAsync("plan", createdAt: now);
    await store.ScheduleAsync(draft.Id, now);
    AutomationStepClaim coordinator = Single(await store.ClaimDueAsync(now));

    AutomationCompletionResult invalidCoordinator = await store.CompleteStepAsync(
        Success(coordinator, complete: false, tokens: 0, now) with { Summary = "  " });
    Equal(AutomationState.Scheduled, invalidCoordinator.Run.State);
    Equal(AutomationRole.Coordinator, invalidCoordinator.Run.NextRole);
    Equal(1, invalidCoordinator.Run.CurrentRoleRetryCount);
    Equal(now.AddSeconds(1), invalidCoordinator.Run.ScheduledFor);
    True(invalidCoordinator.Run.StatusDetail?.StartsWith(
        "The Coordinator returned an empty summary.",
        StringComparison.Ordinal) == true);
    Equal(null, invalidCoordinator.NextClaim);

    AutomationStepClaim coordinatorRetry = Single(await store.ClaimDueAsync(now.AddSeconds(1)));
    Equal(AutomationRole.Coordinator, coordinatorRetry.Role);
    AutomationCompletionResult recoveredCoordinator = await store.CompleteStepAsync(
        Success(coordinatorRetry, complete: false, tokens: 0, now.AddSeconds(1)));
    Equal(AutomationState.Running, recoveredCoordinator.Run.State);
    Equal(AutomationRole.Reviewer, NotNull(recoveredCoordinator.NextClaim).Role);
    Equal(AutomationWorkflowPhase.PlanReview, NotNull(recoveredCoordinator.NextClaim).WorkflowPhase);
    Equal(0, recoveredCoordinator.Run.CurrentRoleRetryCount);
    await store.StopAsync(recoveredCoordinator.Run.Id);

    AutomationRun unprefixedDraft = await store.CreateDraftAsync("another plan", createdAt: now);
    await store.ScheduleAsync(unprefixedDraft.Id, now);
    AutomationStepClaim unprefixedCoordinator = Single(await store.ClaimDueAsync(now));
    AutomationCompletionResult unprefixedCoordinatorResult = await store.CompleteStepAsync(
        Success(unprefixedCoordinator, complete: false, tokens: 0, now) with
        {
            Instruction = "Please review the existing files."
        });
    Equal(AutomationState.Running, unprefixedCoordinatorResult.Run.State);
    Equal(AutomationRole.Reviewer, NotNull(unprefixedCoordinatorResult.NextClaim).Role);
    await store.StopAsync(unprefixedCoordinatorResult.Run.Id);

    using var implementerTemp = new TempDirectory();
    var implementerStore = new AutomationStore(implementerTemp.Path);
    AutomationStepClaim implementer = await StartAtImplementerAsync(implementerStore, now);
    AutomationCompletionResult invalidImplementer = await implementerStore.CompleteStepAsync(
        Success(implementer, complete: false, tokens: 0, now) with { Summary = " " });
    Equal(AutomationState.Scheduled, invalidImplementer.Run.State);
    Equal(AutomationRole.Implementer, invalidImplementer.Run.NextRole);
    True(invalidImplementer.Run.StatusDetail?.StartsWith(
        "The Implementer returned an empty summary.", StringComparison.Ordinal) == true);
    Equal(null, invalidImplementer.NextClaim);

    using var reviewerTemp = new TempDirectory();
    var reviewerStore = new AutomationStore(reviewerTemp.Path);
    AutomationStepClaim reviewImplementer = await StartAtImplementerAsync(reviewerStore, now);
    AutomationStepClaim reviewer = NotNull((await reviewerStore.CompleteStepAsync(
        Success(reviewImplementer, complete: false, tokens: 0, now))).NextClaim);
    AutomationCompletionResult invalidReviewer = await reviewerStore.CompleteStepAsync(
        Success(reviewer, complete: false, tokens: 0, now) with { Instruction = "  " });
    Equal(AutomationState.Scheduled, invalidReviewer.Run.State);
    Equal(AutomationRole.Reviewer, invalidReviewer.Run.NextRole);
    True(invalidReviewer.Run.StatusDetail?.StartsWith(
        "The reviewer returned no instruction for the next workflow step.",
        StringComparison.Ordinal) == true);
    Equal(null, invalidReviewer.NextClaim);
}

static async Task LegacyRunWithoutReviewerFeedbackLoads()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun draft = await store.CreateDraftAsync("legacy plan", createdAt: now);
    JsonObject ledger = JsonNode.Parse(await File.ReadAllTextAsync(store.StorePath))!.AsObject();
    JsonObject legacyRun = ledger["runs"]!.AsArray()[0]!.AsObject();
    legacyRun.Remove("lastReviewerFeedbackFingerprint");
    legacyRun.Remove("lastReviewerFeedbackInstruction");
    await File.WriteAllTextAsync(store.StorePath, ledger.ToJsonString());

    AutomationRun loaded = NotNull(await store.GetAsync(draft.Id));
    Equal(null, loaded.LastReviewerFeedbackFingerprint);
    Equal(null, loaded.LastReviewerFeedbackInstruction);
}

static async Task StageFailuresRetryWithoutCap()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun draft = await store.CreateDraftAsync("retry forever", createdAt: now);
    await store.ScheduleAsync(draft.Id, now);

    DateTimeOffset claimAt = now;
    for (int failureNumber = 1; failureNumber <= 9; failureNumber++)
    {
        // Recreate the store on each attempt to prove retry identity/count are
        // durable and do not depend on the current process lifetime.
        store = new AutomationStore(temp.Path);
        AutomationStepClaim claim = Single(await store.ClaimDueAsync(claimAt));
        Equal(AutomationRole.Coordinator, claim.Role);
        AutomationCompletionResult failed = await store.CompleteStepAsync(
            Success(claim, complete: false, tokens: 1, claimAt) with
            {
                Succeeded = false,
                Summary = "transient stage failure " + failureNumber
            });
        Equal(AutomationState.Scheduled, failed.Run.State);
        Equal(AutomationRole.Coordinator, failed.Run.NextRole);
        Equal(1, failed.Run.Iteration);
        Equal(failureNumber, failed.Run.CurrentRoleRetryCount);
        Equal(null, failed.NextClaim);
        TimeSpan expectedDelay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(failureNumber - 1, 5)), 30));
        Equal(claimAt + expectedDelay, failed.Run.ScheduledFor);
        claimAt = failed.Run.ScheduledFor
            ?? throw new InvalidOperationException("A failed stage did not persist its next retry time.");
    }

    AutomationRun scheduledRetry = NotNull(await store.GetAsync(draft.Id));
    AutomationRun stopped = await store.StopAsync(scheduledRetry.Id);
    Equal(AutomationState.Stopped, stopped.State);
    Equal(0, (await store.ClaimDueAsync(claimAt.AddDays(1))).Count);

    using var pauseTemp = new TempDirectory();
    var pauseStore = new AutomationStore(pauseTemp.Path);
    AutomationRun pauseDraft = await pauseStore.CreateDraftAsync("pause failed retry", createdAt: now);
    await pauseStore.ScheduleAsync(pauseDraft.Id, now);
    AutomationStepClaim active = Single(await pauseStore.ClaimDueAsync(now));
    await pauseStore.PauseAsync(pauseDraft.Id);
    AutomationCompletionResult paused = await pauseStore.CompleteStepAsync(
        Success(active, complete: false, tokens: 0, now) with
        {
            Succeeded = false,
            Summary = "pause after this failure"
        });
    Equal(AutomationState.Paused, paused.Run.State);
    Equal(AutomationRole.Coordinator, paused.Run.NextRole);
    Equal(1, paused.Run.CurrentRoleRetryCount);
    Equal(null, paused.Run.ScheduledFor);
    Equal(0, (await pauseStore.ClaimDueAsync(now.AddDays(1))).Count);
}

static async Task AutomaticRestartRecovery()
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    foreach (var role in new[] { AutomationRole.Coordinator, AutomationRole.Implementer, AutomationRole.Reviewer })
    {
        using var temp = new TempDirectory();
        var store = new AutomationStore(temp.Path);
        var draft = await store.CreateDraftAsync("recover automatically", createdAt: now);
        await store.ScheduleAsync(draft.Id, now);
        var abandoned = await ClaimForRoleAsync(store, now, role);
        var recovered = Single(await store.RecoverInterruptedAsync(now.AddMinutes(1), includeScheduled: true, resumeAutomatically: true));
        Equal(AutomationState.Scheduled, recovered.State);
        Equal(role == AutomationRole.Implementer ? AutomationRole.Reviewer : role, recovered.NextRole);
        Equal(draft.Id, recovered.Id);
        Equal(null, recovered.ActiveDispatchToken);
        False((await store.CompleteStepAsync(Success(abandoned, false, 0, now))).Accepted);
        Equal(1, (await store.ClaimDueAsync(now.AddMinutes(2))).Count);
        Equal(0, (await store.ClaimDueAsync(now.AddMinutes(2))).Count);
    }
    using var pausedTemp = new TempDirectory();
    var pausedStore = new AutomationStore(pausedTemp.Path);
    var paused = await pausedStore.StartApprovedPlanAsync("respect queued pause", scheduledFor: now);
    await pausedStore.ClaimDueAsync(now);
    await pausedStore.PauseAsync(paused.Id);
    Equal(AutomationState.Paused, Single(await pausedStore.RecoverInterruptedAsync(resumeAutomatically: true)).State);
    Equal(0, (await pausedStore.ClaimDueAsync(now.AddDays(1))).Count);
    await pausedStore.StopAsync(paused.Id);
    Equal(0, (await pausedStore.RecoverInterruptedAsync(includeScheduled: true, resumeAutomatically: true)).Count);
    using var scheduledTemp = new TempDirectory();
    var scheduledStore = new AutomationStore(scheduledTemp.Path);
    var scheduled = await scheduledStore.StartApprovedPlanAsync("keep future schedule", scheduledFor: now.AddDays(1));
    var restored = Single(await scheduledStore.RecoverInterruptedAsync(now, includeScheduled: true, resumeAutomatically: true));
    Equal(scheduled.ScheduledFor, restored.ScheduledFor);
    Equal(0, (await scheduledStore.ClaimDueAsync(now)).Count);
    using var orphanTemp = new TempDirectory();
    var orphanStore = new AutomationStore(orphanTemp.Path);
    var orphan = await orphanStore.StartApprovedPlanAsync("atomic orphan recovery", scheduledFor: now);
    var abandonedOrphan = Single(await orphanStore.ClaimDueAsync(now));
    var atomic = await orphanStore.TryRecoverOrphanedRunningAsync(orphan.Id, abandonedOrphan.DispatchToken,
        abandonedOrphan.Revision, now, resumeAutomatically: true);
    Equal(AutomationState.Scheduled, atomic?.State);
    False((await orphanStore.CompleteStepAsync(Success(abandonedOrphan, false, 0, now))).Accepted);
    var pausedClaim = Single(await orphanStore.ClaimDueAsync(now));
    await orphanStore.PauseAsync(orphan.Id);
    var protectedPause = await orphanStore.TryRecoverOrphanedRunningAsync(orphan.Id, pausedClaim.DispatchToken,
        pausedClaim.Revision, now, resumeAutomatically: true);
    Equal(AutomationState.Paused, protectedPause?.State);
}

static async Task ExplicitStoppedResume()
{
    using var temp = new TempDirectory();
    var store = new AutomationStore(temp.Path);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var draft = await store.CreateDraftAsync("retain original run and partial changes", createdAt: now);
    await store.ScheduleAsync(draft.Id, now);
    var interrupted = await ClaimForRoleAsync(store, now, AutomationRole.Implementer);
    var stopped = await store.StopAsync(draft.Id);
    var resumed = await store.ResumeAsync(stopped.Id, now, stopped.Revision, allowStopped: true);
    Equal(stopped.Id, resumed.Id);
    Equal(stopped.ApprovedPlanSnapshot, resumed.ApprovedPlanSnapshot);
    Equal(stopped.TotalTokenUsage, resumed.TotalTokenUsage);
    Equal(stopped.CoordinatorPlan, resumed.CoordinatorPlan);
    Equal(AutomationRole.Reviewer, resumed.NextRole);
    Equal(AutomationWorkflowPhase.Verification, resumed.WorkflowPhase);
    False((await store.CompleteStepAsync(Success(interrupted, false, 0, now))).Accepted);
    await ThrowsAsync<InvalidOperationException>(() => store.ResumeAsync(resumed.Id, now, allowStopped: true));
    Equal(1, (await store.LoadAsync()).Count);
}

static async Task InterruptedRunRequiresResume()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun draft = await store.CreateDraftAsync("recover", 2, createdAt: now);
    await store.ScheduleAsync(draft.Id, now);
    AutomationStepClaim abandoned = Single(await store.ClaimDueAsync(now));

    AutomationRun recovered = Single(await new AutomationStore(temp.Path).RecoverInterruptedAsync(now.AddMinutes(1)));
    Equal(AutomationState.Paused, recovered.State);
    Equal(AutomationRole.Coordinator, recovered.NextRole);
    Equal(null, recovered.ActiveDispatchToken);
    Equal(abandoned.Revision + 1, recovered.Revision);
    Equal(0, (await store.ClaimDueAsync(now.AddDays(1))).Count);

    AutomationCompletionResult stale = await store.CompleteStepAsync(Success(abandoned, false, 0, now));
    False(stale.Accepted);
}

static async Task InterruptedStageResumeRoutesImplementerToReviewer()
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    (AutomationRole InterruptedRole, AutomationRole ExpectedResumeRole, AutomationWorkflowPhase ExpectedPhase)[] cases =
    [
        (AutomationRole.Coordinator, AutomationRole.Coordinator, AutomationWorkflowPhase.Planning),
        (AutomationRole.Implementer, AutomationRole.Reviewer, AutomationWorkflowPhase.Verification),
        (AutomationRole.Reviewer, AutomationRole.Reviewer, AutomationWorkflowPhase.Verification)
    ];

    foreach ((AutomationRole interruptedRole, AutomationRole expectedResumeRole, AutomationWorkflowPhase expectedPhase) in cases)
    {
        using var temp = new TempDirectory();
        var store = new AutomationStore(temp.Path);
        AutomationRun draft = await store.CreateDraftAsync("interrupted stage", createdAt: now);
        await store.ScheduleAsync(draft.Id, now);
        AutomationStepClaim interrupted = await ClaimForRoleAsync(store, now, interruptedRole);

        AutomationRun recovered = Single(await store.RecoverInterruptedAsync(now.AddMinutes(1)));
        Equal(AutomationState.Paused, recovered.State);
        Equal(interrupted.Role, recovered.NextRole);
        Equal(interrupted.WorkflowPhase, recovered.WorkflowPhase);
        True(recovered.StatusDetail is { Length: > 0 });

        AutomationRun resumed = await store.ResumeAsync(
            draft.Id,
            now.AddMinutes(2),
            recovered.Revision);
        Equal(AutomationState.Scheduled, resumed.State);
        Equal(expectedResumeRole, resumed.NextRole);
        Equal(expectedPhase, resumed.WorkflowPhase);
        Equal(interrupted.Iteration, resumed.Iteration);
        Equal(null, resumed.StatusDetail);

        AutomationStepClaim next = Single(await store.ClaimDueAsync(now.AddMinutes(2)));
        Equal(expectedResumeRole, next.Role);
        Equal(expectedPhase, next.WorkflowPhase);
        Equal(interrupted.Iteration, next.Iteration);
    }
}

static async Task InterruptedDeliveryRoutesThroughVerification()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationStepClaim implementer = await StartAtImplementerAsync(store, now);
    AutomationStepClaim reviewer = NotNull((await store.CompleteStepAsync(
        Success(implementer, complete: false, tokens: 1, now))).NextClaim);
    AutomationStepClaim commander = NotNull((await store.CompleteStepAsync(
        Success(reviewer, complete: true, tokens: 1, now))).NextClaim);
    AutomationStepClaim deliveryCoordinator = NotNull((await store.CompleteStepAsync(
        Success(commander, complete: false, tokens: 1, now))).NextClaim);
    AutomationStepClaim interruptedDelivery = deliveryCoordinator;
    Equal(AutomationWorkflowPhase.Delivery, interruptedDelivery.WorkflowPhase);
    Equal(AutomationRole.Implementer, interruptedDelivery.Role);

    AutomationRun recovered = Single(await store.RecoverInterruptedAsync(now.AddMinutes(1)));
    Equal(AutomationState.Paused, recovered.State);
    Equal(AutomationWorkflowPhase.Delivery, recovered.WorkflowPhase);
    Equal(AutomationRole.Implementer, recovered.NextRole);
    Equal(null, recovered.DeliveryEvidence);

    AutomationRun resumed = await store.ResumeAsync(recovered.Id, now.AddMinutes(2), recovered.Revision);
    Equal(AutomationWorkflowPhase.Verification, resumed.WorkflowPhase);
    Equal(AutomationRole.Reviewer, resumed.NextRole);
    AutomationStepClaim reviewerAfterResume = Single(await store.ClaimDueAsync(now.AddMinutes(2)));
    Equal(AutomationWorkflowPhase.Verification, reviewerAfterResume.WorkflowPhase);
    AutomationStepClaim commanderAfterReview = NotNull((await store.CompleteStepAsync(
        Success(reviewerAfterResume, complete: true, tokens: 1, now.AddMinutes(2)))).NextClaim);
    Equal(AutomationWorkflowPhase.Delivery, commanderAfterReview.WorkflowPhase);
    Equal(AutomationRole.Coordinator, commanderAfterReview.Role);
    Equal(AutomationState.Running, (await store.GetAsync(resumed.Id))?.State);
    Equal(null, (await store.GetAsync(resumed.Id))?.FinalVerificationEvidence);
}

static async Task WatchdogRecoveryReturnsThroughCommander()
{
    foreach (bool pause in new[] { false, true })
    {
        using var temp = new TempDirectory();
        var store = new AutomationStore(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AutomationStepClaim implementer = await StartAtImplementerAsync(store, now);
        if (pause) await store.PauseAsync(implementer.RunId);
        var interrupted = new AutomationStepResult(implementer.RunId, implementer.DispatchToken,
            implementer.Revision, false, "Native stall; exact tree stopped", "", false, 7, now) { Interrupted = true };
        AutomationCompletionResult recovered = await store.CompleteStepAsync(interrupted);
        True(recovered.Accepted);
        Equal(pause ? AutomationState.Paused : AutomationState.Scheduled, recovered.Run.State);
        Equal(AutomationRole.Reviewer, recovered.Run.NextRole);
        Equal(AutomationWorkflowPhase.Verification, recovered.Run.WorkflowPhase);
        Equal(implementer.CoordinatorPlan, recovered.Run.CoordinatorPlan);
        True(recovered.Run.NextInstruction!.Contains("현재 파일", StringComparison.Ordinal));
        True(recovered.Run.NextInstruction.Contains(implementer.Instruction!, StringComparison.Ordinal));
        False((await store.CompleteStepAsync(interrupted)).Accepted);
        if (pause)
        {
            Equal(0, (await store.ClaimDueAsync(now.AddMinutes(1))).Count);
            continue;
        }
        AutomationStepClaim reviewer = Single(await store.ClaimDueAsync(now.AddMinutes(1)));
        Equal(AutomationRole.Reviewer, reviewer.Role);
        AutomationStepClaim commander = NotNull((await store.CompleteStepAsync(
            Success(reviewer, complete: false, tokens: 2, now.AddMinutes(1)))).NextClaim);
        Equal(AutomationRole.Coordinator, commander.Role);
        AutomationStepClaim resumedImplementation = NotNull((await store.CompleteStepAsync(
            Success(commander, complete: false, tokens: 2, now.AddMinutes(1)))).NextClaim);
        Equal(AutomationRole.Implementer, resumedImplementation.Role);
        False(resumedImplementation.DispatchToken == implementer.DispatchToken);
    }

    using var readOnlyTemp = new TempDirectory();
    var readOnlyStore = new AutomationStore(readOnlyTemp.Path);
    DateTimeOffset start = DateTimeOffset.UtcNow;
    var draft = await readOnlyStore.CreateDraftAsync("commander startup", createdAt: start);
    await readOnlyStore.ScheduleAsync(draft.Id, start);
    var claim = Single(await readOnlyStore.ClaimDueAsync(start));
    var retry = await readOnlyStore.CompleteStepAsync(new(claim.RunId, claim.DispatchToken, claim.Revision,
        false, "stalled commander", "", false, 0, start) { Interrupted = true });
    Equal(AutomationRole.Coordinator, retry.Run.NextRole);
    Equal(AutomationWorkflowPhase.Planning, retry.Run.WorkflowPhase);
    Equal(AutomationState.Scheduled, retry.Run.State);

    using var deliveryTemp = new TempDirectory();
    var deliveryStore = new AutomationStore(deliveryTemp.Path);
    var deliveryClaim = await StartAtImplementerAsync(deliveryStore, start);
    while (deliveryClaim.WorkflowPhase != AutomationWorkflowPhase.FinalVerification)
        deliveryClaim = NotNull((await deliveryStore.CompleteStepAsync(
            Success(deliveryClaim, complete: deliveryClaim.Role == AutomationRole.Reviewer, tokens: 1, start))).NextClaim);
    var finalRetry = await deliveryStore.CompleteStepAsync(new(deliveryClaim.RunId, deliveryClaim.DispatchToken,
        deliveryClaim.Revision, false, "Reviewer stalled and stopped", "", false, 1, start) { Interrupted = true });
    Equal(deliveryClaim.DeliveryEvidence, finalRetry.Run.DeliveryEvidence);
    True(!string.IsNullOrWhiteSpace(finalRetry.Run.DeliveryEvidence));
    Equal(AutomationWorkflowPhase.FinalVerification, finalRetry.Run.WorkflowPhase);
    var finalClaim = Single(await deliveryStore.ClaimDueAsync(start.AddMinutes(1)));
    Equal(AutomationState.Completed, (await deliveryStore.CompleteStepAsync(
        Success(finalClaim, complete: true, tokens: 1, start.AddMinutes(1)))).Run.State);
}

static async Task<AutomationStepClaim> ClaimForRoleAsync(
    AutomationStore store,
    DateTimeOffset now,
    AutomationRole targetRole)
{
    AutomationStepClaim coordinator = Single(await store.ClaimDueAsync(now));
    if (targetRole == AutomationRole.Coordinator)
    {
        return coordinator;
    }

    AutomationStepClaim planReviewer = NotNull((await store.CompleteStepAsync(
        Success(coordinator, complete: false, tokens: 10, now))).NextClaim);
    AutomationStepClaim implementationCoordinator = NotNull((await store.CompleteStepAsync(
        Success(planReviewer, complete: false, tokens: 20, now))).NextClaim);
    AutomationStepClaim implementer = NotNull((await store.CompleteStepAsync(
        Success(implementationCoordinator, complete: false, tokens: 30, now))).NextClaim);
    if (targetRole == AutomationRole.Implementer)
    {
        return implementer;
    }

    return NotNull((await store.CompleteStepAsync(
        Success(implementer, complete: false, tokens: 40, now))).NextClaim);
}

static async Task OrphanedRunningClaimRecoveryIsConditional()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    var options = new AutomationExecutionOptions(
        CoordinatorModel: "coordinator",
        ImplementerModel: "implementer",
        ReviewerModel: "reviewer",
        TokenBudget: 5000,
        MaxStageMinutes: 30);
    AutomationRun draft = await store.CreateDraftAsync("approved plan", 3, options, now);
    await store.ScheduleAsync(draft.Id, now, draft.Revision);
    AutomationStepClaim coordinator = Single(await store.ClaimDueAsync(now));
    AutomationStepClaim activeClaim = NotNull((await store.CompleteStepAsync(
        Success(coordinator, complete: false, tokens: 42, now.AddMinutes(1)))).NextClaim);
    AutomationRun running = NotNull(await store.GetAsync(draft.Id));

    byte[] beforeMismatches = await File.ReadAllBytesAsync(store.StorePath);
    Equal(null, await store.TryRecoverOrphanedRunningAsync(
        running.Id,
        Guid.NewGuid(),
        running.Revision,
        now.AddMinutes(2)));
    SequenceEqual(beforeMismatches, await File.ReadAllBytesAsync(store.StorePath));
    Equal(null, await store.TryRecoverOrphanedRunningAsync(
        running.Id,
        activeClaim.DispatchToken,
        running.Revision - 1,
        now.AddMinutes(2)));
    SequenceEqual(beforeMismatches, await File.ReadAllBytesAsync(store.StorePath));
    Equal(AutomationState.Running, (await store.GetAsync(running.Id))?.State);

    await ThrowsAsync<InvalidOperationException>(() => store.StartApprovedPlanAsync("new plan"));
    SequenceEqual(beforeMismatches, await File.ReadAllBytesAsync(store.StorePath));

    AutomationRun recovered = NotNull(await store.TryRecoverOrphanedRunningAsync(
        running.Id,
        activeClaim.DispatchToken,
        activeClaim.Revision,
        now.AddMinutes(2)));
    Equal(AutomationState.Paused, recovered.State);
    Equal(running.Id, recovered.Id);
    Equal(running.ProjectDirectory, recovered.ProjectDirectory);
    Equal(running.ApprovedPlanSnapshot, recovered.ApprovedPlanSnapshot);
    Equal(running.Options, recovered.Options);
    Equal(running.MaxIterations, recovered.MaxIterations);
    Equal(running.Iteration, recovered.Iteration);
    Equal(running.NextRole, recovered.NextRole);
    Equal(running.CreatedAt, recovered.CreatedAt);
    Equal(now.AddMinutes(2), recovered.UpdatedAt);
    Equal(null, recovered.ScheduledFor);
    Equal(null, recovered.ActiveDispatchToken);
    Equal(running.Revision + 1, recovered.Revision);
    False(recovered.PauseAfterCurrentStage);
    Equal(42L, recovered.TotalTokenUsage);
    Equal("stage summary", recovered.LastSummary);
    Equal("Implementer action: next instruction", recovered.NextInstruction);
    True(recovered.StatusDetail is not null);

    AutomationCompletionResult stale = await store.CompleteStepAsync(
        Success(activeClaim, complete: false, tokens: 99, now.AddMinutes(3)));
    False(stale.Accepted);
    Equal(AutomationState.Paused, stale.Run.State);

    AutomationRun started = await store.StartApprovedPlanAsync("new plan");
    Equal(AutomationState.Scheduled, started.State);
    AutomationRun stoppedHistory = NotNull(await store.GetAsync(recovered.Id));
    Equal(AutomationState.Stopped, stoppedHistory.State);
    Equal(recovered.NextRole, stoppedHistory.NextRole);
    Equal(recovered.WorkflowPhase, stoppedHistory.WorkflowPhase);
}

static async Task LegacyActivePositionsResumeThroughPlanReview()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun paused = await SeedLegacyPausedRunAsync(
        store,
        now,
        statusDetail: "Token budget reached.",
        maxIterations: 100,
        iteration: 8,
        nextRole: AutomationRole.Implementer);

    AutomationRun resumed = await store.ResumeAsync(paused.Id, now.AddMinutes(1), paused.Revision);
    Equal(AutomationState.Scheduled, resumed.State);
    Equal(AutomationWorkflowPhase.Planning, resumed.WorkflowPhase);
    Equal(AutomationRole.Coordinator, resumed.NextRole);
    Equal(8, resumed.Iteration);
    Equal(paused.MaxIterations, resumed.MaxIterations);
    Equal(paused.TotalTokenUsage, resumed.TotalTokenUsage);
    Equal(paused.Options, resumed.Options);

    AutomationStepClaim coordinator = Single(await store.ClaimDueAsync(now.AddMinutes(1)));
    AutomationStepClaim planReviewer = NotNull((await store.CompleteStepAsync(
        Success(coordinator, complete: false, tokens: 100, now))).NextClaim);
    Equal(AutomationWorkflowPhase.PlanReview, planReviewer.WorkflowPhase);
    Equal(AutomationRole.Reviewer, planReviewer.Role);
    Equal(101L, (await store.GetAsync(paused.Id))?.TotalTokenUsage);
}

static async Task LegacyPausedResumeRejectsActiveRun()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun paused = await SeedLegacyPausedRunAsync(store, now, "Token budget reached.");
    AutomationRun active = await store.CreateDraftAsync("another plan", createdAt: now);
    await store.ScheduleAsync(active.Id, now, active.Revision);

    byte[] before = await File.ReadAllBytesAsync(store.StorePath);
    await ThrowsAsync<InvalidOperationException>(() =>
        store.ResumeAsync(paused.Id, now.AddMinutes(1), paused.Revision));
    SequenceEqual(before, await File.ReadAllBytesAsync(store.StorePath));
    Equal(AutomationState.Paused, (await store.GetAsync(paused.Id))?.State);
    Equal(AutomationState.Scheduled, (await store.GetAsync(active.Id))?.State);
}

static async Task TerminalRunsCannotResume()
{
    using var stoppedTemp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var stoppedStore = new AutomationStore(stoppedTemp.Path);
    AutomationRun stoppedDraft = await stoppedStore.CreateDraftAsync("stopped plan", createdAt: now);
    AutomationRun stopped = await stoppedStore.StopAsync(stoppedDraft.Id);
    await ThrowsAsync<InvalidOperationException>(() =>
        stoppedStore.ResumeAsync(stopped.Id, now.AddMinutes(1), stopped.Revision));

    using var failedTemp = new TempDirectory();
    var failedStore = new AutomationStore(failedTemp.Path);
    AutomationRun failedDraft = await failedStore.CreateDraftAsync("failed plan", createdAt: now);
    await failedStore.ScheduleAsync(failedDraft.Id, now);
    AutomationStepClaim claim = Single(await failedStore.ClaimDueAsync(now));
    AutomationStepResult failedResult = Success(claim, complete: false, tokens: 0, now) with
    {
        Succeeded = false,
        Summary = "stage error"
    };
    AutomationCompletionResult completion = await failedStore.CompleteStepAsync(failedResult);
    Equal(AutomationState.Scheduled, completion.Run.State);
    Equal(AutomationRole.Coordinator, completion.Run.NextRole);
    Equal(1, completion.Run.CurrentRoleRetryCount);
    AutomationRun failedStopped = await failedStore.StopAsync(completion.Run.Id);
    await ThrowsAsync<InvalidOperationException>(() =>
        failedStore.ResumeAsync(failedStopped.Id, now.AddMinutes(1), failedStopped.Revision));
}

static async Task ApprovedPlanReplacesLegacyPause()
{
    using var temp = new TempDirectory();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var store = new AutomationStore(temp.Path);
    AutomationRun paused = await SeedLegacyPausedRunAsync(store, now, "Token budget reached.");
    Equal(AutomationState.Paused, paused.State);
    Equal("Token budget reached.", paused.StatusDetail);
    Equal(1, paused.TotalTokenUsage);

    var newOptions = new AutomationExecutionOptions(
        CoordinatorModel: "new/coordinator",
        ImplementerModel: "new/implementer",
        ReviewerModel: "new/reviewer",
        TokenBudget: 4321,
        MaxStageMinutes: 17);
    DateTimeOffset schedule = now.AddMinutes(5);
    AutomationRun started = await store.StartApprovedPlanAsync(
        "new approved plan exactly",
        options: newOptions,
        scheduledFor: schedule);

    AutomationRun stoppedHistory = NotNull(await store.GetAsync(paused.Id));
    Equal(AutomationState.Stopped, stoppedHistory.State);
    Equal(paused.Revision + 1, stoppedHistory.Revision);
    Equal(paused.Id, stoppedHistory.Id);
    Equal(paused.ApprovedPlanSnapshot, stoppedHistory.ApprovedPlanSnapshot);
    Equal(paused.Options, stoppedHistory.Options);
    Equal(paused.MaxIterations, stoppedHistory.MaxIterations);
    Equal(paused.Iteration, stoppedHistory.Iteration);
    Equal(paused.NextRole, stoppedHistory.NextRole);
    Equal(paused.TotalTokenUsage, stoppedHistory.TotalTokenUsage);
    Equal(paused.LastSummary, stoppedHistory.LastSummary);
    Equal(paused.NextInstruction, stoppedHistory.NextInstruction);
    Equal(paused.StatusDetail, stoppedHistory.StatusDetail);
    Equal(null, stoppedHistory.ActiveDispatchToken);
    Equal(null, stoppedHistory.ScheduledFor);
    False(stoppedHistory.PauseAfterCurrentStage);

    Equal(AutomationState.Scheduled, started.State);
    Equal("new approved plan exactly", started.ApprovedPlanSnapshot);
    Equal(null, started.MaxIterations);
    Equal(newOptions, started.Options);
    Equal(newOptions.TokenBudget, started.Options.TokenBudget);
    Equal(schedule, started.ScheduledFor);
    Equal(AutomationRole.Coordinator, started.NextRole);
    Equal(1, started.Iteration);
    Equal(0L, started.TotalTokenUsage);
    Equal(null, started.ActiveDispatchToken);
    Equal(null, started.StatusDetail);

    AutomationRun[] ledger = (await store.LoadAsync()).ToArray();
    Equal(2, ledger.Length);
    Equal(1, ledger.Count(run => run.State is AutomationState.Running or AutomationState.Scheduled));
}

static async Task ApprovedPlanRejectsActiveRuns()
{
    foreach (bool running in new[] { false, true })
    {
        using var temp = new TempDirectory();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new AutomationStore(temp.Path);
        AutomationRun paused = await SeedLegacyPausedRunAsync(store, now, "Token budget reached.");
        AutomationRun activeDraft = await store.CreateDraftAsync("active plan", createdAt: now);
        await store.ScheduleAsync(activeDraft.Id, now, activeDraft.Revision);
        if (running) Single(await store.ClaimDueAsync(now));

        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await ThrowsAsync<InvalidOperationException>(() => store.StartApprovedPlanAsync(
            "must not replace active work"));
        SequenceEqual(before, await File.ReadAllBytesAsync(store.StorePath));

        AutomationRun[] ledger = (await store.LoadAsync()).ToArray();
        Equal(2, ledger.Length);
        Equal(AutomationState.Paused, NotNull(ledger.Single(run => run.Id == paused.Id)).State);
        Equal(running ? AutomationState.Running : AutomationState.Scheduled,
            NotNull(ledger.Single(run => run.Id == activeDraft.Id)).State);
    }
}

static async Task ConcurrentApprovedStartsCreateOneActiveRun()
{
    using var temp = new TempDirectory();
    var store = new AutomationStore(temp.Path);

    async Task<(AutomationRun? Run, Exception? Error)> AttemptAsync(string plan)
    {
        try
        {
            return (await store.StartApprovedPlanAsync(plan), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    (AutomationRun? Run, Exception? Error)[] outcomes = await Task.WhenAll(
        AttemptAsync("approved plan one"),
        AttemptAsync("approved plan two"));

    Equal(1, outcomes.Count(outcome => outcome.Run is not null));
    Equal(1, outcomes.Count(outcome => outcome.Error is InvalidOperationException));
    AutomationRun[] ledger = (await store.LoadAsync()).ToArray();
    Equal(1, ledger.Length);
    Equal(1, ledger.Count(run => run.State is AutomationState.Running or AutomationState.Scheduled));
    Equal(AutomationState.Scheduled, ledger[0].State);
    Equal(null, ledger[0].MaxIterations);
    True(ledger[0].ApprovedPlanSnapshot is "approved plan one" or "approved plan two");
}

static async Task<AutomationRun> SeedLegacyPausedRunAsync(
    AutomationStore store,
    DateTimeOffset now,
    string statusDetail,
    int maxIterations = 5,
    int iteration = 5,
    AutomationRole nextRole = AutomationRole.Implementer)
{
    var options = new AutomationExecutionOptions(
        CoordinatorModel: "old/coordinator",
        ImplementerModel: "old/implementer",
        ReviewerModel: "old/reviewer",
        TokenBudget: 1,
        MaxStageMinutes: 30);
    var paused = new AutomationRun(
        Guid.NewGuid(),
        store.ProjectDirectory,
        "old approved plan",
        options,
        maxIterations,
        iteration,
        AutomationState.Paused,
        nextRole,
        now.AddMinutes(-2),
        now,
        ScheduledFor: null,
        ActiveDispatchToken: null,
        Revision: 7,
        PauseAfterCurrentStage: false,
        TotalTokenUsage: 1,
        LastSummary: "old summary",
        NextInstruction: "old instruction",
        StatusDetail: statusDetail);
    var serializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };
    Directory.CreateDirectory(Path.GetDirectoryName(store.StorePath)!);
    JsonObject legacyLedger = JsonNode.Parse(JsonSerializer.Serialize(
        new { version = 1, runs = new[] { paused } },
        serializerOptions))!.AsObject();
    legacyLedger["runs"]!.AsArray()[0]!.AsObject().Remove("workflowPhase");
    await File.WriteAllTextAsync(
        store.StorePath,
        legacyLedger.ToJsonString());
    return NotNull(await store.GetAsync(paused.Id));
}

static AutomationStepResult Success(
    AutomationStepClaim claim,
    bool complete,
    long tokens,
    DateTimeOffset at)
{
    string instruction = claim.Role switch
    {
        AutomationRole.Coordinator => "Implementer action: next instruction",
        AutomationRole.Implementer => "Reviewer verification: review the implementation",
        AutomationRole.Reviewer => "Fix the outstanding review issue",
        _ => throw new ArgumentOutOfRangeException(nameof(claim))
    };

    var result = new AutomationStepResult(
        claim.RunId,
        claim.DispatchToken,
        claim.Revision,
        Succeeded: true,
        Summary: "stage summary",
        Instruction: instruction,
        Complete: complete,
        TokenUsage: tokens,
        CompletedAt: at);
    return result with
    {
        PlanApproved = claim.WorkflowPhase == AutomationWorkflowPhase.PlanReview,
        PlanningPlan = claim.WorkflowPhase == AutomationWorkflowPhase.Planning
            ? ValidPlanningPlan()
            : null,
        DeliveryEvidence = (claim.WorkflowPhase, claim.Role) switch
        {
            (AutomationWorkflowPhase.Delivery, AutomationRole.Implementer) => "Service process PID 1234 is running at http://127.0.0.1:5000/health.",
            (AutomationWorkflowPhase.FinalVerification, AutomationRole.Reviewer) => "Reviewer independently loaded /health and observed HTTP 200.",
            _ => null
        }
    };
}

static AutomationPlanningOutput ValidPlanningPlan() => new(
    "The requested behavior and its bounded project scope.",
    ["Concrete improvement and the selected alternative."],
    ["Relevant design, files, and implementation tasks."],
    ["First inspect, then implement, test, and verify in order."],
    ["Failure and edge-case handling with a recovery path."],
    ["Run the focused tests and observe the expected behavior."],
    "Run the service from its project directory, open its real address or window, and observe the key behavior.");

static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T exception)
    {
        return exception;
    }

    throw new Exception($"Expected {typeof(T).Name}.");
}

static T Single<T>(IReadOnlyList<T> values)
{
    Equal(1, values.Count);
    return values[0];
}

static T NotNull<T>(T? value) where T : class =>
    value ?? throw new Exception($"Expected a non-null {typeof(T).Name}.");

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new Exception($"Expected <{expected}> but got <{actual}>.");
    }
}

static void True(bool value)
{
    if (!value)
    {
        throw new Exception("Expected true.");
    }
}

static void False(bool value) => True(!value);

static void SequenceEqual(byte[] expected, byte[] actual)
{
    if (!expected.AsSpan().SequenceEqual(actual))
    {
        throw new Exception("Byte sequences differ.");
    }
}

sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"TerminalNotes.AutomationTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
    }
}
