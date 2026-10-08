namespace TerminalNotes.Services;

public enum AutomationState
{
    Draft,
    Scheduled,
    Running,
    Paused,
    Stopped,
    Completed,
    Failed
}

public enum AutomationRole
{
    Coordinator,
    Implementer,
    Reviewer
}

/// <summary>
/// Persistent workflow stage. Planning is zero so older JSON ledgers without a phase
/// default safely into the new plan review workflow.
/// </summary>
public enum AutomationWorkflowPhase
{
    Planning,
    PlanReview,
    Implementation,
    Verification,
    Delivery,
    FinalVerification
}

public sealed record AutomationExecutionOptions(
    string? CoordinatorModel = null,
    string? ImplementerModel = null,
    string? ReviewerModel = null,
    int? TokenBudget = null,
    int? MaxStageMinutes = null)
{
    public bool ContinuousImprovement { get; init; }
    public DateTimeOffset? ImprovementUntil { get; init; }
    public bool CanStartImprovementCycle(DateTimeOffset now) =>
        ContinuousImprovement && (ImprovementUntil is null || now < ImprovementUntil);
    public string? ModelFor(AutomationRole role) => role switch
    {
        AutomationRole.Coordinator => CoordinatorModel,
        AutomationRole.Implementer => ImplementerModel,
        AutomationRole.Reviewer => ReviewerModel,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}

/// <summary>
/// Persisted workflow state. The approved plan is fixed; improvement preferences can change while running.
/// Revision and ActiveDispatchToken protect state from stale process completions.
/// </summary>
public sealed record AutomationRun(
    Guid Id,
    string ProjectDirectory,
    string ApprovedPlanSnapshot,
    AutomationExecutionOptions Options,
    // Nullable so new runs can have no iteration cap. A numeric value from an older
    // ledger remains available as historical metadata only.
    int? MaxIterations,
    int Iteration,
    AutomationState State,
    AutomationRole NextRole,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ScheduledFor,
    Guid? ActiveDispatchToken,
    long Revision,
    bool PauseAfterCurrentStage,
    long TotalTokenUsage,
    string? LastSummary,
    string? NextInstruction,
    string? StatusDetail)
{
    // Additive persisted fields keep prior version-1 ledgers readable while retaining
    // review feedback across the Implementer stage for repeat detection.
    public string? LastReviewerFeedbackFingerprint { get; init; }
    public string? LastReviewerFeedbackInstruction { get; init; }

    // Consecutive failures for NextRole. This is persisted so retries survive an app
    // restart and so a role can never be skipped merely because OpenCode returned an
    // invalid response or its process ended. It resets only after a valid stage result.
    public int CurrentRoleRetryCount { get; init; }

    // Missing phase in an older version-1 ledger defaults to Planning. The store
    // migrates incompatible old roles back through Commander planning before resume.
    public AutomationWorkflowPhase WorkflowPhase { get; init; }
    public bool PlanApproved { get; init; }
    public string? CoordinatorPlan { get; init; }
    /// <summary>
    /// Structured plan produced by the Coordinator during Planning. This is kept
    /// separately from the user's original plan.md snapshot; the Coordinator is
    /// instructed to read that file and never modify it.
    /// </summary>
    public AutomationPlanningOutput? CoordinatorPlanningPlan { get; init; }
    public string? LastReviewSummary { get; init; }
    public string? LastReviewInstruction { get; init; }
    public string? DeliveryEvidence { get; init; }
    public string? FinalVerificationEvidence { get; init; }
    public int ImprovementCycle { get; init; } = 1;
    public IReadOnlyList<AutomationImprovementOutcome> ImprovementHistory { get; init; } = [];
}

public sealed record AutomationImprovementOutcome(int Cycle, string Goal, string PlanFingerprint,
    string Summary, string Evidence, DateTimeOffset VerifiedAt);

public sealed record AutomationStepClaim(
    Guid RunId,
    Guid DispatchToken,
    long Revision,
    AutomationRole Role,
    int Iteration,
    string ProjectDirectory,
    string ApprovedPlanSnapshot,
    AutomationExecutionOptions Options,
    long TotalTokenUsage,
    string? PreviousSummary,
    string? Instruction,
    DateTimeOffset ClaimedAt)
{
    public AutomationWorkflowPhase WorkflowPhase { get; init; }
    public bool PlanApproved { get; init; }
    public string? CoordinatorPlan { get; init; }
    public AutomationPlanningOutput? CoordinatorPlanningPlan { get; init; }
    public string? LastReviewSummary { get; init; }
    public string? LastReviewInstruction { get; init; }
    public string? DeliveryEvidence { get; init; }
    public int ImprovementCycle { get; init; } = 1;
    public IReadOnlyList<AutomationImprovementOutcome> ImprovementHistory { get; init; } = [];
}

/// <summary>
/// Structured result from one CLI process. Phase-gated completion additionally requires
/// successful plan approval, launch evidence, and final reviewer verification.
/// TokenUsage is the non-negative usage attributable to this stage.
/// </summary>
public sealed record AutomationStepResult(
    Guid RunId,
    Guid DispatchToken,
    long ExpectedRevision,
    bool Succeeded,
    string Summary,
    string Instruction,
    bool Complete,
    long TokenUsage,
    DateTimeOffset CompletedAt)
{
    public bool Interrupted { get; init; }
    public bool PlanApproved { get; init; }
    public string? DeliveryEvidence { get; init; }
    public AutomationPlanningOutput? PlanningPlan { get; init; }
}

/// <summary>
/// The structured output required from the first Coordinator Planning stage.
/// Each section is intentionally unbounded: validation checks presence and
/// meaningful content, not arbitrary word or item counts.
/// </summary>
public sealed record AutomationPlanningOutput(
    string GoalsAndScope,
    IReadOnlyList<string> IdeasAndChoices,
    IReadOnlyList<string> DesignFilesTasks,
    IReadOnlyList<string> OrderedImplementation,
    IReadOnlyList<string> RisksAndEdgeCases,
    IReadOnlyList<string> AcceptanceTests,
    string ServiceDemo)
{
    /// <summary>Human-readable rendering for the automation detail view and persisted diagnostics.</summary>
    public string ToMarkdown()
    {
        static string Items(IEnumerable<string>? values) => string.Join(Environment.NewLine, (values ?? Array.Empty<string>()).Select(value => $"- {value}"));
        return $"""
            ## 목표와 범위
            {GoalsAndScope}

            ## 아이디어·개선안·선택지
            {Items(IdeasAndChoices)}

            ## 설계·파일·작업
            {Items(DesignFilesTasks)}

            ## 구현 순서
            {Items(OrderedImplementation)}

            ## 위험과 경계 사례
            {Items(RisksAndEdgeCases)}

            ## 인수 테스트
            {Items(AcceptanceTests)}

            ## 실제 서비스 시연
            {ServiceDemo}
            """.Trim();
    }

    public bool TryValidate(out string? error)
    {
        if (string.IsNullOrWhiteSpace(GoalsAndScope))
            return Fail("planning.goalsAndScope must contain meaningful text.", out error);
        if (!ValidateItems(IdeasAndChoices, "planning.ideasAndChoices", out error)) return false;
        if (!ValidateItems(DesignFilesTasks, "planning.designFilesTasks", out error)) return false;
        if (!ValidateItems(OrderedImplementation, "planning.orderedImplementation", out error)) return false;
        if (!ValidateItems(RisksAndEdgeCases, "planning.risksAndEdgeCases", out error)) return false;
        if (!ValidateItems(AcceptanceTests, "planning.acceptanceTests", out error)) return false;
        if (string.IsNullOrWhiteSpace(ServiceDemo))
            return Fail("planning.serviceDemo must describe an actual service demonstration.", out error);

        error = null;
        return true;
    }

    private static bool ValidateItems(IReadOnlyList<string>? values, string path, out string? error)
    {
        if (values is null || values.Count == 0)
            return Fail($"{path} must contain at least one meaningful item.", out error);

        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Fail($"{path} contains an empty item.", out error);
        }

        error = null;
        return true;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}

public sealed record AutomationCompletionResult(
    bool Accepted,
    AutomationRun Run,
    AutomationStepClaim? NextClaim,
    string? RejectionReason);
