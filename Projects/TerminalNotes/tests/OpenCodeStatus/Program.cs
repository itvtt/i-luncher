using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TerminalNotes.Services;

internal static class Program
{
    private const string DirectoryPath = "C:\\OpenCode Test\\project";
    private static readonly Uri ServerUri = new("http://127.0.0.1:43129/");

    private static async Task<int> Main()
    {
        try
        {
            await VerifyMonitorPhaseTransitionsAsync();
            await VerifySlowMetadataBaselineAsync();
            await VerifyDelayedNativeSubmissionConfirmationAsync();
            await VerifyApiSubmissionReceiptIsExactAndNonCompletingAsync();
            await VerifyRecoverySignalsAsync();
            await VerifyDedicatedScopeAsync();
            await VerifyMissedReplyWithoutDisconnectAsync();
            await VerifyStuckEventHandshakeReconnectsAsync();
            await VerifyNativeBusyThenIdleAsync();
            await VerifyCurrentRequestRecentActivityAsync();
            await VerifyMissedBusyStillAcceptsExplicitIdleAsync();
            await VerifyBaselineCannotCompleteSubmissionAsync();
            await VerifyChildBusyKeepsPaneBusyAsync();
            await VerifyApprovalEventsAsync();
            await VerifyPendingInputBaselineAsync();
            await VerifyDisconnectAndReconnectAsync();
            await VerifyMissedApprovalReplyAfterReconnectAsync();
            await VerifySubmissionDuringInitialDisconnectAsync();
            await VerifyDisconnectBeforeFirstBaselineAsync();
            await VerifyFailedBaselineReconnectAsync();
            await VerifyMalformedBaselineReconnectAsync();
            await VerifyMalformedFramesAndCleanupAsync();
            Console.WriteLine("PASS: OpenCode native status monitor follows explicit server events.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static async Task VerifyMonitorPhaseTransitionsAsync()
    {
        var baselineGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeOpenCodeHandler
        {
            FailEventAttempts = 1,
            BaselineProvider = (_, _) => baselineGate.Task
        };
        ControlledSseStream stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        Require(monitor.MonitorPhase == OpenCodeStatusMonitorPhase.NotMonitoring,
            "A monitor that has not started reports that it is not monitoring.");
        monitor.Start();
        await WaitUntilAsync(() => monitor.MonitorPhase == OpenCodeStatusMonitorPhase.ReconnectWaiting,
            "failed event connection enters reconnect backoff");
        Require(handler.EventRequestCount == 1 && monitor.Snapshot.Kind == OpenCodeStatusKind.Unknown,
            "A failed event connection reports reconnect waiting with unknown status.");
        await WaitUntilAsync(() => monitor.MonitorPhase == OpenCodeStatusMonitorPhase.Checking,
            "reconnected monitor checks the native baseline");
        Require(handler.EventRequestCount >= 2 && handler.StatusRequestCount == 1,
            "The checking phase begins for the reconnect baseline request.");
        await Task.Delay(25);
        Require(monitor.MonitorPhase == OpenCodeStatusMonitorPhase.Checking,
            "A held native baseline remains in the checking phase.");
        baselineGate.SetResult("{}");
        await WaitUntilAsync(() => monitor.MonitorPhase == OpenCodeStatusMonitorPhase.Monitoring,
            "live SSE monitoring after baseline completes");
        Require(monitor.Snapshot.Kind == OpenCodeStatusKind.Idle,
            "The monitor became active before its native baseline was applied.");
        stream.Complete();
        await WaitUntilAsync(() => monitor.MonitorPhase == OpenCodeStatusMonitorPhase.ReconnectWaiting,
            "live SSE disconnect returns to reconnect backoff");
        monitor.Dispose();
        Require(monitor.MonitorPhase == OpenCodeStatusMonitorPhase.NotMonitoring,
            "A disposed monitor must not appear to be checking or monitoring.");
    }

    private static async Task VerifyDedicatedScopeAsync()
    {
        var handler = new FakeOpenCodeHandler
        {
            BaselineJson = "{\"ses_old\":{\"type\":\"busy\"}}",
            PendingProvider = (kind, _, _) => Task.FromResult(kind == "permission"
                ? "[{\"id\":\"old-approval\",\"sessionID\":\"ses_old\"}]" : "[]")
        };
        var stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.SetSessionScope(null);
        monitor.Start();
        await WaitForSnapshotAsync(monitor, state => state.Kind == OpenCodeStatusKind.Idle, "fresh role ignores historical approval");
        monitor.SetSessionScope("ses_owned");
        monitor.ObserveSubmission();
        stream.SendEvent(StatusEvent("ses_old", "idle"));
        stream.SendEvent(StatusEvent("ses_owned", "busy"));
        await WaitForSnapshotAsync(monitor, state => state.Kind == OpenCodeStatusKind.Busy, "only owned request is busy");
        Require(!monitor.Snapshot.HasCompletedTurn, "Another session's idle completed the owned request.");
        stream.SendEvent(JsonSerializer.Serialize(new { directory = DirectoryPath, payload = new
        { type = "session.created", properties = new { info = new { id = "ses_child", parentID = "ses_owned" } } } }));
        stream.SendEvent(InputEvent("question.asked", "owned-question", "ses_child"));
        await WaitForSnapshotAsync(monitor, state => state.Kind == OpenCodeStatusKind.NeedsInput, "owned descendant approval stays protected");
        stream.SendEvent(InputEvent("question.replied", "owned-question", "ses_child"));
        stream.SendEvent(StatusEvent("ses_owned", "idle"));
        await WaitForSnapshotAsync(monitor, state => state.Kind == OpenCodeStatusKind.Idle && state.HasCompletedTurn, "owned completion ignores old approval");
        monitor.SetSessionScope("ses_next");
        monitor.ObserveSubmission();
        monitor.ConfirmOwnedTurnCompleted();
        Expect(monitor.Snapshot, OpenCodeStatusKind.Idle, true, "exact native result releases pending even when idle event was missed");
    }

    private static async Task VerifyMissedReplyWithoutDisconnectAsync()
    {
        bool waiting = true;
        var handler = new FakeOpenCodeHandler
        {
            PendingProvider = (kind, _, _) => Task.FromResult(kind == "question" && waiting
                ? "[{\"id\":\"q1\",\"sessionID\":\"ses_owned\"}]" : "[]")
        };
        handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.SetSessionScope("ses_owned");
        monitor.Start();
        await WaitForSnapshotAsync(monitor, state => state.Kind == OpenCodeStatusKind.NeedsInput, "question baseline");
        waiting = false;
        await WaitForSnapshotAsync(monitor, state => state.Kind == OpenCodeStatusKind.Idle, "periodic reconciliation clears a missed reply");
        Require(!monitor.Snapshot.HasCompletedTurn, "Reconciliation inferred completion from an idle baseline.");
    }

    private static async Task VerifyStuckEventHandshakeReconnectsAsync()
    {
        var handler = new FakeOpenCodeHandler { HangFirstEventHandshake = true };
        handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.NativeStatusHandshakeTimeoutOverride = TimeSpan.FromMilliseconds(300);
        monitor.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (monitor.Snapshot.Kind != OpenCodeStatusKind.Idle && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Require(handler.EventRequestCount >= 2 && monitor.Snapshot.Kind == OpenCodeStatusKind.Idle,
            "An SSE handshake that never returns headers must time out and reconnect.");
    }

    private static async Task VerifySlowMetadataBaselineAsync()
    {
        var handler = new FakeOpenCodeHandler
        {
            BaselineProvider = async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2500), cancellationToken);
                return "{\"ses_slow\":{\"type\":\"idle\"}}";
            }
        };
        _ = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitForSnapshotAsync(monitor, state => state.Kind == OpenCodeStatusKind.Idle,
            "metadata baseline slower than the old two-second deadline", TimeSpan.FromSeconds(6));
        Require(handler.EventRequestCount == 1, "a slow metadata baseline does not force an SSE reconnect");
    }

    private static async Task VerifyDelayedNativeSubmissionConfirmationAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{}" };
        ControlledSseStream stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.SetSessionScope("ses_slow");
        monitor.Start();
        await WaitForSnapshotAsync(monitor, state => state.Kind == OpenCodeStatusKind.Idle, "slow confirmation baseline");
        OpenCodeSubmissionObservation submission = monitor.ObserveSubmission();
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSubmissionConfirmed(OpenCodeSubmissionObservation observation)
        {
            if (observation.Generation == submission.Generation && observation.ConfirmationVersion > submission.ConfirmationVersion)
                confirmation.TrySetResult();
        }
        monitor.SubmissionConfirmed += OnSubmissionConfirmed;
        try
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(12));
                stream.SendEvent(UserMessageEvent("msg_slow", "ses_slow", DateTimeOffset.UtcNow));
            });
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            bool accepted = await TerminalExecutionTiming.WaitForNativeSubmissionConfirmationAsync(
                confirmation.Task,
                () => monitor.SubmissionGeneration == submission.Generation,
                timeoutOverride: TimeSpan.FromSeconds(15));
            Require(accepted && elapsed.Elapsed > TimeSpan.FromSeconds(10),
                "a fresh native user-message confirmation after the old ten-second deadline is accepted");
        }
        finally
        {
            monitor.SubmissionConfirmed -= OnSubmissionConfirmed;
        }

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        bool completedButCancelled = await TerminalExecutionTiming.WaitForNativeSubmissionConfirmationAsync(
            Task.CompletedTask,
            () => true,
            cancellationToken: alreadyCancelled.Token);
        Require(!completedButCancelled,
            "a completed native confirmation cannot override a cancelled dispatch token");

        var stillOwned = System.Diagnostics.Stopwatch.StartNew();
        bool cancelled = await TerminalExecutionTiming.WaitForNativeSubmissionConfirmationAsync(
            Task.Delay(Timeout.Infinite),
            () => stillOwned.Elapsed < TimeSpan.FromMilliseconds(50));
        Require(!cancelled && stillOwned.Elapsed < TimeSpan.FromMilliseconds(500),
            "a missing native confirmation stops promptly after dispatch ownership is lost");
    }

    private static async Task VerifyApiSubmissionReceiptIsExactAndNonCompletingAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{}" };
        ControlledSseStream stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.SetSessionScope("ses_api");
        monitor.Start();
        await WaitForSnapshotAsync(monitor, state => state.Kind == OpenCodeStatusKind.Idle, "API receipt baseline");

        OpenCodeSubmissionObservation submission = monitor.ObserveSubmission();
        int confirmedEvents = 0;
        OpenCodeSubmissionObservation confirmed = default;
        monitor.SubmissionConfirmed += observation => { confirmedEvents++; confirmed = observation; };
        long versionBeforeReceipt = monitor.SubmissionConfirmationVersion;
        monitor.ConfirmApiSubmission("ses_unrelated", "msg_api", submission.Generation);
        monitor.ConfirmApiSubmission("ses_api", "msg_api", submission.Generation + 1);
        Require(monitor.SubmissionConfirmationVersion == versionBeforeReceipt && confirmedEvents == 0,
            "An unrelated session or stale generation cannot confirm an API receipt.");

        monitor.ConfirmApiSubmission("ses_api", "msg_api", submission.Generation);
        Require(monitor.SubmissionConfirmationVersion == versionBeforeReceipt + 1 && confirmedEvents == 1 &&
                confirmed.Generation == submission.Generation && confirmed.ConfirmationVersion == versionBeforeReceipt + 1,
            "The exact API message receipt advances and publishes confirmation for its current generation.");
        Require(monitor.Snapshot.Kind == OpenCodeStatusKind.Pending && !monitor.Snapshot.HasCompletedTurn,
            "An API receipt confirms submission without synthesizing native busy or turn completion.");

        await monitor.RefreshConfirmedApiStatusAsync("ses_api", "msg_api", submission.Generation, CancellationToken.None);
        Require(monitor.Snapshot.Kind == OpenCodeStatusKind.Idle && !monitor.Snapshot.HasCompletedTurn,
            "An exact API baseline settles only transport pending and does not complete the request.");
        stream.SendEvent(AssistantMessageEvent("msg_assistant_api", "ses_api", "msg_api"));
        stream.SendEvent(PartEvent("ses_api", "msg_assistant_api", "part_api", "text", "late assistant activity"));
        await WaitUntilAsync(() => monitor.LastActivityAt is not null, "assistant activity after API baseline");
        Require(!monitor.Snapshot.HasCompletedTurn,
            "Assistant activity remains observable after an API baseline without synthesizing completion.");
        stream.SendEvent(StatusEvent("ses_api", "idle"));
        await WaitForSnapshotAsync(monitor, state => state.Kind == OpenCodeStatusKind.Idle && state.HasCompletedTurn,
            "explicit native idle after API baseline");

        OpenCodeSubmissionObservation newer = monitor.ObserveSubmission();
        long versionAfterNewerSubmission = monitor.SubmissionConfirmationVersion;
        monitor.ConfirmApiSubmission("ses_api", "msg_api", submission.Generation);
        await monitor.RefreshConfirmedApiStatusAsync("ses_api", "msg_api", submission.Generation, CancellationToken.None);
        Require(newer.Generation > submission.Generation &&
                monitor.SubmissionConfirmationVersion == versionAfterNewerSubmission && confirmedEvents == 1 &&
                monitor.Snapshot.Kind == OpenCodeStatusKind.Pending && !monitor.Snapshot.HasCompletedTurn,
            "A receipt or baseline from an older generation cannot revive or confirm a later pending submission.");
    }
    private static async Task VerifyRecoverySignalsAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{}" };
        ControlledSseStream stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        var signals = new ConcurrentQueue<OpenCodeRecoverySignal>();
        monitor.RecoverySignalReceived += signals.Enqueue;
        monitor.Start();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "recovery baseline");
        long created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string Message(string directory, string role) => JsonSerializer.Serialize(new
        {
            directory, payload = new { type = "message.updated", properties = new
            { info = new { sessionID = "ses_root", id = "msg_current", role, time = new { created } } } }
        });
        stream.SendEvent(Message("C:\\unrelated", "user"));
        stream.SendEvent(Message(DirectoryPath, "assistant"));
        stream.SendEvent(Message(DirectoryPath, "user"));
        stream.SendEvent(JsonSerializer.Serialize(new { directory = DirectoryPath, payload = new
        { type = "todo.updated", properties = new { sessionID = "ses_root", todos = Array.Empty<object>() } } }));
        stream.SendEvent(StatusEvent("ses_root", "busy"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Busy, "recovery frames processed");
        var received = signals.ToArray();
        Require(received.Length == 2, "Recovery must filter other directories and assistant messages.");
        Require(received[0].MessageId == "msg_current" && received[0].MessageCreatedAt?.ToUnixTimeMilliseconds() == created,
            "Recovery must retain exact user message identity and native creation time.");
        Require(received[1].Type == "todo.updated" && received[1].SessionId == "ses_root" && received[1].Version > received[0].Version,
            "Todo recovery evidence must preserve causal event order.");
    }

    private static async Task VerifyNativeBusyThenIdleAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{}" };
        ControlledSseStream stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "initial idle baseline");

        monitor.ObserveSubmission();
        Expect(monitor.Snapshot, OpenCodeStatusKind.Pending, completed: false, "submission is pending until OpenCode responds");

        stream.SendEvent(StatusEvent("session.root", "busy"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Busy, "native busy status");
        Require(!monitor.Snapshot.HasCompletedTurn, "A busy event marked the turn complete.");

        stream.SendMultilineEvent(StatusEvent("session.root", "idle"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle && snapshot.HasCompletedTurn, "multiline native idle status");
        Require(monitor.Snapshot.SessionId == "session.root", "The monitor did not retain the session ID from its status event.");
        Require(handler.EventUris.Any(uri => Uri.UnescapeDataString(uri.Query).Contains(DirectoryPath, StringComparison.Ordinal)), "The SSE request did not include the project directory.");
    }

    private static async Task VerifyCurrentRequestRecentActivityAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{}" };
        ControlledSseStream stream = handler.QueueEventStream();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T00:00:00Z"));
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client, clock);
        monitor.SetSessionScope("ses_owned");
        monitor.Start();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "activity baseline");
        OpenCodeSubmissionObservation observation = monitor.ObserveSubmission();
        long confirmationBaseline = observation.ConfirmationVersion;
        stream.SendEvent(StatusEvent("ses_owned", "busy"));
        stream.SendEvent(UserMessageEvent("msg_old", "ses_owned", clock.GetUtcNow() - TimeSpan.FromSeconds(20)));
        stream.SendEvent(AssistantMessageEvent("msg_old_assistant", "ses_owned", "msg_old"));
        stream.SendEvent(PartEvent("ses_owned", "msg_old_assistant", "part_old", "text", "prior-turn text"));
        stream.SendEvent(UserMessageEvent("msg_child", "ses_other", clock.GetUtcNow()));
        stream.SendEvent(UserMessageEvent("msg_current", "ses_owned", clock.GetUtcNow()));
        stream.SendEvent(PartEvent("ses_other", "msg_other", "part_other", "text", "unrelated session"));
        stream.SendEvent(AssistantMessageEvent("msg_current_assistant", "ses_owned", "msg_current"));
        stream.SendEvent(PartEvent("ses_owned", "msg_current_assistant", "part_empty", "reasoning", "   "));
        await WaitUntilAsync(() => monitor.Snapshot.Kind == OpenCodeStatusKind.Busy, "native busy before activity");
        await WaitUntilAsync(() => monitor.SubmissionConfirmationVersion > confirmationBaseline, "in-scope user message confirms native submission");
        Require(monitor.SubmissionConfirmationVersion == confirmationBaseline + 1,
            "historical and child-session user messages did not confirm submission");
        stream.SendEvent(UserMessageEvent("msg_child", "ses_other", clock.GetUtcNow()));
        stream.SendEvent(UserMessageEvent("msg_current", "ses_owned", clock.GetUtcNow()));
        await Task.Delay(50);
        Require(monitor.SubmissionConfirmationVersion == confirmationBaseline + 1,
            "repeated updates for one native message do not create another confirmation");
        Require(!monitor.HasRecentActivity, "Busy alone, an old turn, an unrelated session, or an empty reasoning part counted as activity.");

        stream.SendEvent(PartEvent("ses_owned", "msg_current_assistant", "part_text", "text", "working"));
        await WaitUntilAsync(() => monitor.HasRecentActivity, "current request text activity");
        DateTimeOffset firstActivity = monitor.LastActivityAt!.Value;
        clock.Advance(TimeSpan.FromSeconds(7));
        stream.SendEvent(StatusEvent("ses_owned", "busy"));
        stream.SendEvent(PartEvent("ses_owned", "msg_current_assistant", "part_text", "text", "working"));
        await Task.Delay(50);
        Require(monitor.LastActivityAt == firstActivity, "Repeated busy and identical part events refreshed activity.");
        Require(monitor.HasRecentActivity, "Current activity expired before the eight-second window.");

        clock.Advance(TimeSpan.FromSeconds(2));
        Require(!monitor.HasRecentActivity, "Activity remained recent beyond the eight-second window.");
        stream.SendEvent(PartEvent("ses_owned", "msg_current_assistant", "part_text", "text", "working further"));
        await WaitUntilAsync(() => monitor.HasRecentActivity && monitor.LastActivityAt > firstActivity, "new current request text refreshes activity");
        DateTimeOffset secondActivity = monitor.LastActivityAt!.Value;
        clock.Advance(TimeSpan.FromSeconds(1));
        stream.SendEvent(DeltaEvent("ses_owned", "msg_current_assistant", "part_delta", "text", "thinking "));
        stream.SendEvent(DeltaEvent("ses_owned", "msg_current_assistant", "part_delta", "text", "through"));
        await WaitUntilAsync(() => monitor.LastActivityAt > secondActivity, "streamed text deltas refresh activity");
        DateTimeOffset deltaActivity = monitor.LastActivityAt!.Value;
        stream.SendEvent(PartEvent("ses_owned", "msg_current_assistant", "part_delta", "text", "thinking through"));
        await Task.Delay(50);
        Require(monitor.LastActivityAt == deltaActivity, "A full part snapshot duplicated the activity already seen in text deltas.");

        clock.Advance(TimeSpan.FromSeconds(1));
        stream.SendEvent(ToolPartEvent("ses_owned", "msg_current_assistant", "part_tool", "running", "shell", "compile"));
        await WaitUntilAsync(() => monitor.LastActivityAt > deltaActivity, "tool state change refreshes activity");
        OpenCodeSubmissionObservation nextObservation = monitor.ObserveSubmission();
        Require(nextObservation.Generation == observation.Generation + 1,
            "a newer local submission advances the correlation generation");
        stream.SendEvent(UserMessageEvent("msg_current", "ses_owned", clock.GetUtcNow()));
        await Task.Delay(50);
        Require(monitor.SubmissionConfirmationVersion == confirmationBaseline + 1,
            "a repeated prior message cannot confirm a newer local submission");
        stream.SendEvent(UserMessageEvent("msg_next", "ses_owned", clock.GetUtcNow()));
        await WaitUntilAsync(() => monitor.SubmissionConfirmationVersion == confirmationBaseline + 2,
            "a fresh native user message confirms the newer generation");
    }

    private static async Task VerifyMissedBusyStillAcceptsExplicitIdleAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{\"session.existing\":{\"type\":\"idle\"}}" };
        ControlledSseStream stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "startup baseline before short turn");

        monitor.ObserveSubmission();
        Expect(monitor.Snapshot, OpenCodeStatusKind.Pending, completed: false, "submission before a missed busy frame");

        // The short response may complete before the monitor sees any busy frame.
        stream.SendEvent(StatusEvent("session.existing", "idle"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle && snapshot.HasCompletedTurn, "explicit idle without a busy frame");
    }

    private static async Task VerifyBaselineCannotCompleteSubmissionAsync()
    {
        var baselineGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeOpenCodeHandler
        {
            BaselineProvider = (attempt, _) => attempt == 1 ? baselineGate.Task : Task.FromResult("{}")
        };
        ControlledSseStream stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitUntilAsync(() => handler.StatusRequestCount == 1, "initial baseline request");

        monitor.ObserveSubmission();
        baselineGate.SetResult("{\"session.stale\":{\"type\":\"idle\"}}");
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Pending, "stale baseline after submission");
        Require(!monitor.Snapshot.HasCompletedTurn, "An idle baseline completed a newly submitted turn.");

        stream.SendEvent(StatusEvent("session.stale", "idle"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle && snapshot.HasCompletedTurn, "post-submit idle event after stale baseline");
    }

    private static async Task VerifyChildBusyKeepsPaneBusyAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{}" };
        ControlledSseStream stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "baseline before child job");
        monitor.ObserveSubmission();

        stream.SendEvent(StatusEvent("session.parent", "busy"));
        stream.SendEvent(StatusEvent("session.child", "busy"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Busy, "parent and child busy");
        stream.SendEvent(StatusEvent("session.parent", "idle"));
        await WaitUntilAsync(() => monitor.Snapshot.Kind == OpenCodeStatusKind.Busy, "child remains busy after parent idle");
        Require(!monitor.Snapshot.HasCompletedTurn, "The parent idle event completed a turn while a child remained busy.");

        stream.SendEvent(StatusEvent("session.child", "idle"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle && snapshot.HasCompletedTurn, "all sessions idle");
    }

    private static async Task VerifyApprovalEventsAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{}" };
        ControlledSseStream stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "baseline before approval");
        monitor.ObserveSubmission();

        stream.SendEvent(InputEvent("permission.asked", "request-1", "session.approval"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.NeedsInput, "permission asked");
        Require(!monitor.Snapshot.HasCompletedTurn, "A permission request marked the turn complete.");

        stream.SendEvent(InputEvent("permission.replied", "request-1", "session.approval"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Pending, "permission replied while turn remains pending");
        stream.SendEvent(StatusEvent("session.approval", "retry"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Busy, "retry status is active work");
        stream.SendEvent(StatusEvent("session.approval", "idle"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle && snapshot.HasCompletedTurn, "approval turn completed");
    }

    private static async Task VerifyDisconnectAndReconnectAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{\"session.reconnect\":{\"type\":\"idle\"}}" };
        ControlledSseStream first = handler.QueueEventStream();
        ControlledSseStream second = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "baseline before disconnect");
        monitor.ObserveSubmission();

        first.Complete();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Unknown, "disconnect becomes unknown");
        await WaitUntilAsync(() => handler.StatusRequestCount >= 2, "reconnect status baseline");
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "idle reconnect baseline clears missed completion");
        Require(!monitor.Snapshot.HasCompletedTurn, "A reconnect baseline claimed a missed turn completion.");

        monitor.ObserveSubmission();
        second.SendEvent(StatusEvent("session.reconnect", "busy"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Busy, "fresh work after reconnect");
        second.SendEvent(StatusEvent("session.reconnect", "idle"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle && snapshot.HasCompletedTurn, "fresh idle event completes post-reconnect work");
    }

    private static async Task VerifyPendingInputBaselineAsync()
    {
        var handler = new FakeOpenCodeHandler
        {
            BaselineJson = "{}",
            PendingProvider = (kind, _, _) => Task.FromResult(kind == "permission"
                ? "[{\"id\":\"permission-existing\",\"sessionID\":\"session.approval\"}]"
                : "[]")
        };
        handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.NeedsInput, "pending permission from native list baseline");
        Require(!monitor.Snapshot.HasCompletedTurn, "A pending-input baseline marked the turn complete.");
        Require(handler.PermissionRequestCount == 1 && handler.QuestionRequestCount == 1, "The monitor did not request both pending-input lists.");
    }

    private static async Task VerifyMissedApprovalReplyAfterReconnectAsync()
    {
        var permissionBaselineGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeOpenCodeHandler
        {
            BaselineJson = "{}",
            PendingProvider = (kind, attempt, _) => kind == "permission" && attempt == 2
                ? permissionBaselineGate.Task
                : Task.FromResult("[]")
        };
        ControlledSseStream first = handler.QueueEventStream();
        ControlledSseStream second = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "baseline before reconnect approval");

        first.SendEvent(InputEvent("permission.asked", "request-gap", "session.gap"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.NeedsInput, "approval before lost connection");
        first.Complete();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Unknown, "approval state is unknown during disconnect");
        await WaitUntilAsync(
            () => handler.EventRequestCount >= 2 && handler.StatusRequestCount >= 2 && handler.PermissionRequestCount >= 2,
            "reconnect baseline requests");
        Require(monitor.Snapshot.Kind == OpenCodeStatusKind.Unknown, "The monitor published old approval state before the reconnect baseline completed.");

        second.SendEvent(InputEvent("permission.replied", "request-gap", "session.gap"));
        second.SendEvent(InputEvent("permission.asked", "request-sync", "session.gap"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.NeedsInput, "subsequent request confirms the earlier reply was processed");
        second.SendEvent(InputEvent("permission.replied", "request-sync", "session.gap"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Unknown, "all current requests resolved while baseline is in flight");
        permissionBaselineGate.SetResult("[{\"id\":\"request-gap\",\"sessionID\":\"session.gap\"}]");
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "post-reconnect reply wins over stale list response");
        Require(!monitor.Snapshot.HasCompletedTurn, "A reconnect baseline inferred completion after a missed approval reply.");
    }

    private static async Task VerifySubmissionDuringInitialDisconnectAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{}", FailEventAttempts = 1 };
        ControlledSseStream stream = handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitUntilAsync(() => handler.EventRequestCount >= 1, "initial failed event connection");
        monitor.ObserveSubmission();
        await WaitUntilAsync(() => handler.EventRequestCount >= 2, "event connection retry after local submission");
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Pending, "initial local submission survives reconnect before first baseline");

        stream.SendEvent(StatusEvent("session.startup", "busy"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Busy, "startup submission becomes busy");
        stream.SendEvent(StatusEvent("session.startup", "idle"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle && snapshot.HasCompletedTurn, "startup submission completes from native idle");
    }

    private static async Task VerifyDisconnectBeforeFirstBaselineAsync()
    {
        var handler = new FakeOpenCodeHandler
        {
            BaselineProvider = async (attempt, cancellationToken) =>
            {
                if (attempt == 1)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "{}";
            }
        };
        ControlledSseStream first = handler.QueueEventStream();
        handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitUntilAsync(() => handler.StatusRequestCount == 1, "first baseline still pending");
        monitor.ObserveSubmission();
        first.Complete();
        await WaitUntilAsync(() => handler.StatusRequestCount >= 2, "reconnect before first baseline succeeded");
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle && !snapshot.HasCompletedTurn,
            "fresh idle baseline retires uncertain submission after a dropped established stream");
    }

    private static async Task VerifyFailedBaselineReconnectAsync()
    {
        var handler = new FakeOpenCodeHandler
        {
            BaselineProvider = async (attempt, cancellationToken) =>
            {
                if (attempt == 1)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "{}";
            }
        };
        ControlledSseStream first = handler.QueueEventStream();
        handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.MetadataRequestTimeoutOverride = TimeSpan.FromMilliseconds(300);
        monitor.Start();
        await WaitUntilAsync(() => handler.EventRequestCount >= 2 && handler.StatusRequestCount >= 2, "reconnect after a stalled status baseline");
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "new baseline recovers after timeout");
        Require(monitor.LastConnectionFailure is null, "A recovered connection kept reporting the previous failure.");
        await WaitUntilAsync(() => first.IsDisposed, "stalled baseline tears down the old stream");
    }

    private static async Task VerifyMalformedBaselineReconnectAsync()
    {
        var handler = new FakeOpenCodeHandler
        {
            BaselineProvider = (attempt, _) => Task.FromResult(attempt == 1 ? "[]" : "{}")
        };
        ControlledSseStream first = handler.QueueEventStream();
        handler.QueueEventStream();
        using var client = new HttpClient(handler);
        using var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitUntilAsync(() => handler.StatusRequestCount >= 2, "retry after malformed status baseline");
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "valid baseline after malformed response");
        await WaitUntilAsync(() => first.IsDisposed, "malformed baseline tears down the old stream");
    }

    private static async Task VerifyMalformedFramesAndCleanupAsync()
    {
        var handler = new FakeOpenCodeHandler { BaselineJson = "{}" };
        ControlledSseStream stream = handler.QueueEventStream();
        var client = new HttpClient(handler);
        var monitor = new OpenCodeStatusMonitor(ServerUri, DirectoryPath, client);
        monitor.Start();
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Idle, "baseline before malformed frame");
        monitor.ObserveSubmission();

        stream.SendRaw(": keepalive\n\ndata: {not-json}\n\n");
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Pending, "malformed event ignored");
        Require(!monitor.Snapshot.HasCompletedTurn, "A malformed event changed completion state.");
        stream.SendEvent(StatusEvent("session.malformed", "busy"));
        await WaitForSnapshotAsync(monitor, snapshot => snapshot.Kind == OpenCodeStatusKind.Busy, "valid event after malformed frame");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        monitor.Dispose();
        watch.Stop();
        Require(watch.Elapsed < TimeSpan.FromSeconds(1), "Dispose waited on the streaming reader.");
        await WaitUntilAsync(() => stream.IsDisposed, "stream cleanup after nonblocking dispose");
        client.Dispose();
    }

    private static string StatusEvent(string sessionId, string status) => JsonSerializer.Serialize(new
    {
        directory = DirectoryPath,
        payload = new
        {
            type = "session.status",
            properties = new { sessionID = sessionId, status = new { type = status } }
        }
    });

    private static string InputEvent(string eventType, string requestId, string sessionId) => JsonSerializer.Serialize(new
    {
        directory = DirectoryPath,
        payload = new
        {
            type = eventType,
            properties = new { requestID = requestId, permissionID = requestId, id = requestId, sessionID = sessionId }
        }
    });

    private static string UserMessageEvent(string messageId, string sessionId, DateTimeOffset createdAt) => JsonSerializer.Serialize(new
    {
        directory = DirectoryPath,
        payload = new { type = "message.updated", properties = new { info = new
        { id = messageId, sessionID = sessionId, role = "user", time = new { created = createdAt.ToUnixTimeMilliseconds() } } } }
    });

    private static string AssistantMessageEvent(string messageId, string sessionId, string parentId) => JsonSerializer.Serialize(new
    {
        directory = DirectoryPath,
        payload = new { type = "message.updated", properties = new { info = new { id = messageId, sessionID = sessionId, role = "assistant", parentID = parentId } } }
    });

    private static string PartEvent(string sessionId, string messageId, string partId, string type, string text) => JsonSerializer.Serialize(new
    {
        directory = DirectoryPath,
        payload = new
        {
            type = "message.part.updated",
            properties = new { part = new { id = partId, sessionID = sessionId, messageID = messageId, type, text } }
        }
    });

    private static string DeltaEvent(string sessionId, string messageId, string partId, string field, string delta) => JsonSerializer.Serialize(new
    {
        directory = DirectoryPath,
        payload = new
        {
            type = "message.part.delta",
            properties = new { sessionID = sessionId, messageID = messageId, partID = partId, field, delta }
        }
    });

    private static string ToolPartEvent(string sessionId, string messageId, string partId, string status, string tool, string title) => JsonSerializer.Serialize(new
    {
        directory = DirectoryPath,
        payload = new
        {
            type = "message.part.updated",
            properties = new { part = new
            {
                id = partId, sessionID = sessionId, messageID = messageId, type = "tool", tool,
                state = new { status, title }
            } }
        }
    });

    private static void Expect(OpenCodeStatusSnapshot snapshot, OpenCodeStatusKind kind, bool completed, string context)
    {
        Require(snapshot.Kind == kind, $"Expected {kind} for {context}, got {snapshot.Kind}.");
        Require(snapshot.HasCompletedTurn == completed, $"Unexpected completion flag for {context}.");
    }

    private static async Task WaitForSnapshotAsync(
        OpenCodeStatusMonitor monitor,
        Func<OpenCodeStatusSnapshot, bool> predicate,
        string context,
        TimeSpan? timeout = null)
    {
        if (predicate(monitor.Snapshot))
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(OpenCodeStatusSnapshot snapshot)
        {
            if (predicate(snapshot))
                completion.TrySetResult();
        }

        monitor.StatusChanged += Handler;
        try
        {
            if (predicate(monitor.Snapshot))
                return;
            await WaitTaskAsync(completion.Task, context, timeout);
        }
        finally
        {
            monitor.StatusChanged -= Handler;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string context)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for " + context + ".");
    }

    private static async Task WaitTaskAsync(Task task, string context, TimeSpan? timeout = null)
    {
        Task timeoutTask = Task.Delay(timeout ?? TimeSpan.FromSeconds(3));
        if (await Task.WhenAny(task, timeoutTask) != task)
            throw new TimeoutException("Timed out waiting for " + context + ".");
        await task;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakeOpenCodeHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<ControlledSseStream> _streams = new();
        private int _eventRequestCount;
        private int _statusRequestCount;
        private int _permissionRequestCount;
        private int _questionRequestCount;

        public string BaselineJson { get; set; } = "{}";
        public Func<int, CancellationToken, Task<string>>? BaselineProvider { get; set; }
        public Func<string, int, CancellationToken, Task<string>>? PendingProvider { get; set; }
        public int FailEventAttempts { get; set; }
        public bool HangFirstEventHandshake { get; set; }
        public int EventRequestCount => Volatile.Read(ref _eventRequestCount);
        public int StatusRequestCount => Volatile.Read(ref _statusRequestCount);
        public int PermissionRequestCount => Volatile.Read(ref _permissionRequestCount);
        public int QuestionRequestCount => Volatile.Read(ref _questionRequestCount);
        public ConcurrentQueue<Uri> EventUris { get; } = new();

        public ControlledSseStream QueueEventStream()
        {
            var stream = new ControlledSseStream();
            _streams.Enqueue(stream);
            return stream;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/event")
            {
                int attempt = Interlocked.Increment(ref _eventRequestCount);
                EventUris.Enqueue(request.RequestUri);
                if (attempt == 1 && HangFirstEventHandshake)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (attempt <= FailEventAttempts)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

                if (!_streams.TryDequeue(out ControlledSseStream? stream))
                    stream = new ControlledSseStream();

                var eventResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream)
                };
                eventResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return eventResponse;
            }

            if (request.RequestUri?.AbsolutePath is "/permission/" or "/question/")
            {
                string kind = request.RequestUri.AbsolutePath == "/permission/" ? "permission" : "question";
                int attempt = kind == "permission"
                    ? Interlocked.Increment(ref _permissionRequestCount)
                    : Interlocked.Increment(ref _questionRequestCount);
                string json = PendingProvider is null
                    ? "[]"
                    : await PendingProvider(kind, attempt, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri?.AbsolutePath == "/session/status")
            {
                int attempt = Interlocked.Increment(ref _statusRequestCount);
                string json = BaselineProvider is null
                    ? BaselineJson
                    : await BaselineProvider(attempt, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class ControlledSseStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        private byte[]? _current;
        private int _offset;
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void SendEvent(string json) => SendRaw("data: " + json + "\n\n");

        public void SendMultilineEvent(string json)
        {
            int split = json.IndexOf(",\"properties\"", StringComparison.Ordinal);
            if (split < 0)
                throw new InvalidOperationException("Test event could not be split into multiline SSE data.");
            string first = json[..(split + 1)];
            string second = json[(split + 1)..];
            SendRaw("data: " + first + "\ndata: " + second + "\n\n");
        }

        public void SendRaw(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset >= _current.Length)
            {
                try
                {
                    _current = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            int count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref _disposed, 1);
            _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
