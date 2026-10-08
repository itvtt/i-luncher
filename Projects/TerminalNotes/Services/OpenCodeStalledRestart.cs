namespace TerminalNotes.Services;

/// <summary>A verified stalled local request; the guard must still pass immediately before stopping its process.</summary>
public sealed record OpenCodeStalledRestart(string SessionId, string UserMessageId, string PlanPath,
    string PlanHash, string Prompt, Func<bool> CanRestart, Func<Task<bool>> VerifyAsync)
{
    public string? OriginalInstruction { get; init; }
}