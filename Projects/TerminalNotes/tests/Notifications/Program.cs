using TerminalNotes.Services;

internal static class Program
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static int Main()
    {
        try { Completion(); Trouble(); Lifecycle(); Muting(); Preferences(); Console.WriteLine("PASS notification policy"); return 0; }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
    }
    private static void Completion()
    {
        var t = new TaskNotificationTracker();
        var quiet = Basic() with { IsArmed = true };
        Need(At(t, quiet, T0) is null, "ordinary quiet/waiting is not a completion");
        var done = quiet with { HasAcceptedSubmission = true, HasVerifiedCompletion = true };
        Need(At(t, done, T0) is null, "completion debounce starts");
        Need(At(t, done, T0.AddSeconds(2.999)) is null, "completion waits three seconds");
        Need(At(t, done, T0.AddSeconds(3))?.Kind == TaskNotificationKind.Completed, "accepted verified completion alerts");
        Need(At(t, done, T0.AddSeconds(4)) is null, "completion alerts once");
        var gap = new TaskNotificationTracker(); At(gap, done, T0); At(gap, done with { HasVerifiedCompletion = false }, T0.AddSeconds(2));
        Need(At(gap, done, T0.AddSeconds(3)) is null, "evidence gap resets debounce");
        Need(At(gap, done, T0.AddSeconds(6))?.Kind == TaskNotificationKind.Completed, "fresh evidence can alert");
        Need(At(new TaskNotificationTracker(), done with { HasAcceptedSubmission = false }, T0.AddMinutes(1)) is null, "no accepted submission means no completion");
        var waiting = new TaskNotificationTracker();
        Need(At(waiting, done with { IsAwaitingInput = true }, T0) is null && At(waiting, done with { IsAwaitingInput = true }, T0.AddSeconds(4)) is null, "waiting for input overrides contradictory completion evidence");
        At(waiting, done, T0.AddSeconds(5));
        Need(At(waiting, done, T0.AddSeconds(8))?.Kind == TaskNotificationKind.Completed, "completion debounce starts only after input wait clears");
        var longGap = new TaskNotificationTracker(); At(longGap, done, T0);
        At(longGap, done with { HasVerifiedCompletion = false }, T0.AddMinutes(2).AddSeconds(1));
        Need(At(longGap, done, T0.AddMinutes(2).AddSeconds(2)) is null, "long observation gap resets completion stability");
        Need(At(longGap, done, T0.AddMinutes(2).AddSeconds(5))?.Kind == TaskNotificationKind.Completed, "completion requires fresh stability after long gap");
    }
    private static void Trouble()
    {
        var fail = Basic() with { IsArmed = true, HasAcceptedSubmission = true, HasDefiniteFailure = true };
        var t = new TaskNotificationTracker(); At(t, fail, T0);
        Need(At(t, fail, T0.AddSeconds(1.999)) is null, "failure debounces two seconds");
        Need(At(t, fail, T0.AddSeconds(2))?.Kind == TaskNotificationKind.Failed, "definite failure alerts");
        var conflict = Basic() with { IsArmed = true, HasAcceptedSubmission = true, HasVerifiedCompletion = true, HasDefiniteFailure = true };
        var conflictTracker = new TaskNotificationTracker(); At(conflictTracker, conflict, T0);
        Need(At(conflictTracker, conflict, T0.AddSeconds(2))?.Kind == TaskNotificationKind.Failed, "definite failure takes precedence over contradictory completion evidence");
        Need(At(conflictTracker, conflict, T0.AddSeconds(4)) is null, "contradictory completion is not separately emitted");
        var startupFailure = Basic() with { IsArmed = true, HasDefiniteFailure = true }; var startup = new TaskNotificationTracker(); At(startup, startupFailure, T0);
        Need(At(startup, startupFailure, T0.AddSeconds(2))?.Kind == TaskNotificationKind.Failed, "current startup failure alerts without accepted submission");
        var gap = new TaskNotificationTracker(); At(gap, fail, T0); At(gap, fail with { HasDefiniteFailure = false, IsStarting = true }, T0.AddSeconds(1));
        Need(At(gap, fail, T0.AddSeconds(2)) is null, "recovery gap resets failure debounce");
        Need(At(gap, fail, T0.AddSeconds(4))?.Kind == TaskNotificationKind.Failed, "failure alerts after fresh evidence");
        var stall = Basic() with { IsArmed = true, HasAcceptedSubmission = true, HasVerifiedStall = true }; var s = new TaskNotificationTracker(); At(s, stall, T0);
        Need(At(s, stall, T0.AddSeconds(29.999)) is null, "stall debounces thirty seconds");
        Need(At(s, stall, T0.AddSeconds(30))?.Kind == TaskNotificationKind.Stalled, "verified stall alerts");
        Need(At(new TaskNotificationTracker(), fail with { IsAwaitingInput = true }, T0.AddMinutes(1)) is null, "input wait is excluded");
        Need(At(new TaskNotificationTracker(), Basic() with { IsArmed = true, HasAcceptedSubmission = true }, T0.AddMinutes(1)) is null, "busy without failure/stall evidence is excluded");
    }
    private static void Lifecycle()
    {
        var first = Basic() with { IsArmed = true, HasAcceptedSubmission = true, HasVerifiedCompletion = true }; var t = new TaskNotificationTracker();
        At(t, first, T0); Need(At(t, first, T0.AddSeconds(3))?.Kind == TaskNotificationKind.Completed, "armed attempt alerts");
        Need(At(t, first, T0.AddSeconds(4)) is null, "attempt deduplicates");
        var next = first with { AttemptId = "attempt-2" };
        Need(At(t, next, T0.AddSeconds(5)) is null, "new attempt gets fresh debounce");
        Need(At(t, next, T0.AddSeconds(8))?.Kind == TaskNotificationKind.Completed, "new attempt rearms");
        var x = new TaskNotificationTracker();
        Need(At(x, first with { IsStopped = true }, T0.AddMinutes(1)) is null, "stopped attempt excluded");
        Need(At(x, first with { IsArmed = false, AttemptId = "historical" }, T0.AddMinutes(1)) is null, "historical/unarmed attempt excluded");
        Need(At(x, first with { AttemptId = "new", HasAcceptedSubmission = false }, T0.AddMinutes(1)) is null, "stale completion does not carry into new attempt");
        t.Forget("project"); Need(At(t, first, T0.AddMinutes(2)) is null, "forget clears attempt debounce history");
    }
    private static void Muting()
    {
        var done = Basic() with { IsArmed = true, HasAcceptedSubmission = true, HasVerifiedCompletion = true }; var t = new TaskNotificationTracker();
        Need(At(t, done, T0, false) is null, "muted completion is silent");
        Need(At(t, done, T0.AddSeconds(3), false) is null, "muted completion is consumed at debounce boundary"); Need(At(t, done, T0.AddSeconds(4)) is null, "muted completion has no backlog");
        var fail = Basic() with { IsArmed = true, HasAcceptedSubmission = true, HasDefiniteFailure = true }; var f = new TaskNotificationTracker();
        Need(At(f, fail, T0, false) is null, "muted trouble is silent"); At(f, fail, T0.AddSeconds(1), false);
        Need(At(f, fail, T0.AddSeconds(2))?.Kind == TaskNotificationKind.Failed, "current trouble alerts when mute expires");
    }
    private static void Preferences()
    {
        var until = T0.AddMinutes(10); Need(!new TaskNotificationPreferences(SnoozedUntilUtc: until).CanNotify(until.AddTicks(-1)), "snooze active before boundary");
        Need(new TaskNotificationPreferences(SnoozedUntilUtc: until).CanNotify(until), "snooze expires at boundary");
        Need(!new TaskNotificationPreferences(Enabled: false).CanNotify(T0), "permanent off suppresses");
        Need(new TaskNotificationPreferences().CanNotify(T0), "default enabled");
        Need(TaskNotificationPreferences.TryGetSnoozeUntil(" 10 ", T0, out var parsed) && parsed == until, "integer snooze accepts whitespace");
        foreach (var bad in new[] { "", "0", "-1", "1.5", "1e2", "2147483648", "999999999999999999999999" })
            Need(!TaskNotificationPreferences.TryGetSnoozeUntil(bad, T0, out _), "reject invalid custom snooze: " + bad);
    }
    private static TaskNotificationObservation Basic() => new("project", "attempt-1", false, false, false, false, false, false, false, false);
    private static TaskNotificationNotice? At(TaskNotificationTracker t, TaskNotificationObservation o, DateTimeOffset now, bool allowed = true) => t.Observe(o, now, allowed);
    private static void Need(bool ok, string message) { if (!ok) throw new Exception(message); }
}
