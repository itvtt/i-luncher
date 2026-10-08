using System.Globalization;

namespace TerminalNotes.Services;

public enum TaskNotificationKind { Completed, Failed, Stalled }

public sealed record TaskNotificationNotice(string ProjectKey, string AttemptId, TaskNotificationKind Kind);

public sealed record TaskNotificationObservation(
    string ProjectKey, string AttemptId, bool IsArmed, bool IsStopped, bool IsStarting,
    bool HasAcceptedSubmission, bool HasVerifiedCompletion, bool IsAwaitingInput,
    bool HasDefiniteFailure, bool HasVerifiedStall);

public sealed class TaskNotificationTracker
{
    private static readonly TimeSpan CompletionStability = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FailureStability = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StallStability = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumObservationGap = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<string, AttemptState> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public TaskNotificationNotice? Observe(TaskNotificationObservation observation, DateTimeOffset now, bool notificationsAllowed)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.ProjectKey) || string.IsNullOrWhiteSpace(observation.AttemptId)) return null;

        lock (_gate)
        {
            if (!_attempts.TryGetValue(observation.ProjectKey, out AttemptState? state) ||
                !string.Equals(state.AttemptId, observation.AttemptId, StringComparison.Ordinal))
                _attempts[observation.ProjectKey] = state = new AttemptState(observation.AttemptId);

            if (state.LastObservedAt is { } previous && (now < previous || now - previous > MaximumObservationGap))
                state.ResetStability();
            state.LastObservedAt = now;

            if (!observation.IsArmed || observation.IsStopped || observation.IsStarting)
            {
                state.ResetStability();
                return null;
            }

            if (observation.HasAcceptedSubmission && observation.HasVerifiedCompletion &&
                !observation.IsAwaitingInput && !observation.HasDefiniteFailure)
            {
                state.FailureSince = state.StallSince = null;
                state.CompletionSince ??= now;
                if (!state.Emitted.Contains(TaskNotificationKind.Completed) && IsStable(state.CompletionSince, now, CompletionStability))
                {
                    state.Emitted.Add(TaskNotificationKind.Completed);
                    return notificationsAllowed ? new(observation.ProjectKey, observation.AttemptId, TaskNotificationKind.Completed) : null;
                }
                return null;
            }
            state.CompletionSince = null;

            if (observation.IsAwaitingInput)
            {
                state.FailureSince = state.StallSince = null;
                return null;
            }

            if (observation.HasDefiniteFailure)
            {
                state.StallSince = null;
                state.FailureSince ??= now;
                if (notificationsAllowed && !state.Emitted.Contains(TaskNotificationKind.Failed) &&
                    IsStable(state.FailureSince, now, FailureStability))
                {
                    state.Emitted.Add(TaskNotificationKind.Failed);
                    return new(observation.ProjectKey, observation.AttemptId, TaskNotificationKind.Failed);
                }
                return null;
            }
            state.FailureSince = null;

            if (observation.HasAcceptedSubmission && observation.HasVerifiedStall)
            {
                state.StallSince ??= now;
                if (notificationsAllowed && !state.Emitted.Contains(TaskNotificationKind.Stalled) &&
                    IsStable(state.StallSince, now, StallStability))
                {
                    state.Emitted.Add(TaskNotificationKind.Stalled);
                    return new(observation.ProjectKey, observation.AttemptId, TaskNotificationKind.Stalled);
                }
                return null;
            }

            state.StallSince = null;
            return null;
        }
    }

    public void Forget(string projectKey)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) return;
        lock (_gate) _attempts.Remove(projectKey);
    }

    private static bool IsStable(DateTimeOffset? since, DateTimeOffset now, TimeSpan required) =>
        since is { } started && now >= started && now - started >= required;

    private sealed class AttemptState(string attemptId)
    {
        public string AttemptId { get; } = attemptId;
        public DateTimeOffset? LastObservedAt { get; set; }
        public DateTimeOffset? CompletionSince { get; set; }
        public DateTimeOffset? FailureSince { get; set; }
        public DateTimeOffset? StallSince { get; set; }
        public HashSet<TaskNotificationKind> Emitted { get; } = [];
        public void ResetStability() => CompletionSince = FailureSince = StallSince = null;
    }
}

public sealed record TaskNotificationPreferences(bool Enabled = true, DateTimeOffset? SnoozedUntilUtc = null)
{
    public bool CanNotify(DateTimeOffset now) => Enabled && (SnoozedUntilUtc is not { } until || now >= until);

    public static bool TryGetSnoozeUntil(string minutes, DateTimeOffset now, out DateTimeOffset until)
    {
        until = default;
        if (!int.TryParse(minutes?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0)
            return false;
        try
        {
            until = now.AddMinutes(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            until = default;
            return false;
        }
    }
}
