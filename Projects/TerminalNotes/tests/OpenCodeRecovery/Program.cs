using TerminalNotes.Services;

internal static class Program
{
    private static readonly DateTimeOffset Start = new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);

    private static int Main()
    {
        try
        {
            VerifyLivenessAndNoTodoStalls();
            VerifyIdleNeedsKnownTodoAndWorkEvidence();
            VerifyExactCurrentRequestCanSettleFromInitialIdle();
            VerifyIdleSettleIgnoresPollsAndHeartbeats();
            VerifyContinueIsReservedAndAcknowledgedOnce();
            VerifyBusyStallThresholdsAndAbortFlow();
            VerifyRetryToolAndSafetyGates();
            VerifySafetyGatesRestartTimersWithoutResettingBackoff();
            VerifyPollingGapsAndUnknownTodoStalls();
            VerifyAmbiguousActionNeverDuplicates();
            VerifyDisconnectDoesNotCountAsProgress();
            VerifyAbortAndContinueShareOneRecoveryCycle();
            VerifyThirdAbortMayStillContinueItsRecovery();
            VerifyNoProgressAttemptsBackOffWithoutHardLimit();
            VerifyManualStopAndNewWorkEpoch();
            Console.WriteLine("PASS: OpenCode recovery policy is deterministic and conservative.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static void VerifyLivenessAndNoTodoStalls()
    {
        var policy = new OpenCodeRecoveryPolicy();
        var busy = Observation() with { Status = OpenCodeRecoveryExecutionState.Busy, HasKnownTodoList = false,
            UnfinishedTodoCount = 0, HasCurrentUnfinishedRequest = true, ActivityFingerprint = "first text" };
        policy.Evaluate(busy, Start);
        for (int minute = 1; minute <= 30; minute++)
        {
            busy = busy with { ActivityFingerprint = "streamed text " + minute, NativeStatusVersion = minute + 1 };
            Require(policy.Evaluate(busy, Start.AddMinutes(minute)).Action == RecoveryAction.None,
                "A changing current response is alive even if it has not completed a tool or created todos.");
        }
        for (int minute = 31; minute < 45; minute++)
            Require(policy.Evaluate(busy, Start.AddMinutes(minute)).Action == RecoveryAction.None,
                "A recently streaming response receives the full inactivity interval before abort.");
        var abort = policy.Evaluate(busy, Start.AddMinutes(45));
        Require(abort.Action == RecoveryAction.Abort,
            "A confirmed current request that never created todos can be aborted after 15 unchanged minutes.");
        policy.TryRecordActionAttempted(abort, Start.AddMinutes(45));
        policy.RecordActionResult(abort.DecisionId, RecoveryActionResult.Accepted, Start.AddMinutes(45));
        var idle = busy with { Status = OpenCodeRecoveryExecutionState.Idle, NativeStatusVersion = 40 };
        policy.Evaluate(idle, Start.AddMinutes(46));
        Require(policy.Evaluate(idle, Start.AddMinutes(46).AddSeconds(20)).Action == RecoveryAction.Continue,
            "The confirmed unfinished request can resume without a todo after its abort reaches idle.");
    }
    private static void VerifyIdleNeedsKnownTodoAndWorkEvidence()
    {
        var policy = new OpenCodeRecoveryPolicy();
        var idle = Observation() with { Status = OpenCodeRecoveryExecutionState.Idle };
        Expect(policy.Evaluate(idle, Start).Action, RecoveryAction.None, "unknown todos must not continue");
        Expect(policy.Evaluate(idle with { HasKnownTodoList = true, UnfinishedTodoCount = 0 }, Start.AddMinutes(1)).Action,
            RecoveryAction.None, "completed todos must not continue");

        var unfinished = idle with { HasKnownTodoList = true, UnfinishedTodoCount = 2 };
        Expect(policy.Evaluate(unfinished, Start.AddMinutes(2)).Action, RecoveryAction.None,
            "an idle session with a stale task list must wait for request work evidence");

        var freshPolicy = new OpenCodeRecoveryPolicy();
        var busy = Observation() with { Status = OpenCodeRecoveryExecutionState.Busy };
        freshPolicy.Evaluate(busy, Start);
        var finished = unfinished with { NativeStatusVersion = 2 };
        Expect(freshPolicy.Evaluate(finished, Start.AddSeconds(1)).Action, RecoveryAction.None,
            "native idle must settle before continue");
        Expect(freshPolicy.Evaluate(finished, Start.AddSeconds(21)).Action, RecoveryAction.Continue,
            "known unfinished work after a native turn may continue");
    }

    private static void VerifyExactCurrentRequestCanSettleFromInitialIdle()
    {
        var policy = new OpenCodeRecoveryPolicy();
        var confirmedRequest = Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Idle,
            HasKnownTodoList = false,
            UnfinishedTodoCount = 0,
            HasCurrentUnfinishedRequest = true
        };
        Expect(policy.Evaluate(confirmedRequest, Start).Action, RecoveryAction.None,
            "an exact unfinished request waits through the initial idle settle period");
        Expect(policy.Evaluate(confirmedRequest, Start.AddSeconds(19)).Action, RecoveryAction.None,
            "an exact unfinished request does not continue before the initial idle settle period");
        Expect(policy.Evaluate(confirmedRequest, Start.Add(OpenCodeRecoveryPolicy.IdleSettlePeriod)).Action,
            RecoveryAction.Continue,
            "an exact unfinished request can continue after the initial idle settle period without a busy SSE");

        var staleTodoPolicy = new OpenCodeRecoveryPolicy();
        var staleTodoOnly = confirmedRequest with { HasCurrentUnfinishedRequest = false, HasKnownTodoList = true, UnfinishedTodoCount = 1 };
        Expect(staleTodoPolicy.Evaluate(staleTodoOnly, Start.Add(OpenCodeRecoveryPolicy.IdleSettlePeriod)).Action,
            RecoveryAction.None,
            "a stale unfinished todo without exact current-request evidence remains blocked");
    }

    private static void VerifyIdleSettleIgnoresPollsAndHeartbeats()
    {
        var policy = new OpenCodeRecoveryPolicy();
        policy.Evaluate(Observation() with { Status = OpenCodeRecoveryExecutionState.Busy }, Start);
        var idle = Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Idle,
            NativeStatusVersion = 2,
            HasKnownTodoList = true,
            UnfinishedTodoCount = 1
        };
        policy.Evaluate(idle, Start.AddSeconds(1));

        // Fresh status polls do not restart the settle timer, and an unchanged progress
        // fingerprint represents a heartbeat or output repaint rather than real work.
        for (int seconds = 5; seconds < 20; seconds += 5)
            Expect(policy.Evaluate(idle with { NativeStatusVersion = seconds + 2 }, Start.AddSeconds(seconds)).Action,
                RecoveryAction.None, "poll-only updates must not skip the idle settle window");

        Expect(policy.Evaluate(idle with { NativeStatusVersion = 99 }, Start.AddSeconds(21)).Action,
            RecoveryAction.Continue, "poll-only updates must not postpone the idle settle window");
    }

    private static void VerifyContinueIsReservedAndAcknowledgedOnce()
    {
        var policy = new OpenCodeRecoveryPolicy();
        RecoveryDecision decision = StartIdleContinue(policy);
        Require(policy.Evaluate(Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Idle,
            NativeStatusVersion = 2,
            HasKnownTodoList = true,
            UnfinishedTodoCount = 1
        }, Start.AddSeconds(21)).DecisionId == decision.DecisionId,
            "Repeated identical evaluations should produce the same deterministic decision ID.");
        Require(policy.TryRecordActionAttempted(decision, Start.AddSeconds(21)), "The first action reservation should succeed.");
        Require(!policy.TryRecordActionAttempted(decision, Start.AddSeconds(21)), "A duplicate reservation must fail.");
        Require(policy.RecordActionResult(decision.DecisionId, RecoveryActionResult.Accepted, Start.AddSeconds(21)),
            "The accepted result should be recorded.");

        for (int seconds = 30; seconds < 140; seconds += 30)
        {
            RecoveryDecision waiting = policy.Evaluate(
                Observation() with
                {
                    Status = OpenCodeRecoveryExecutionState.Idle,
                    NativeStatusVersion = seconds,
                    HasKnownTodoList = true,
                    UnfinishedTodoCount = 1
                }, Start.AddSeconds(seconds));
            Expect(waiting.Action, RecoveryAction.None, "idle polls alone must not resend a continuation");
        }

        RecoveryDecision timedOut = policy.Evaluate(
            Observation() with
            {
                Status = OpenCodeRecoveryExecutionState.Idle,
                NativeStatusVersion = 200,
                HasKnownTodoList = true,
                UnfinishedTodoCount = 1
            }, Start.AddSeconds(142));
        Expect(timedOut.Action, RecoveryAction.Pause, "an unacknowledged accepted continuation must pause after two minutes");

        var progressPolicy = new OpenCodeRecoveryPolicy();
        RecoveryDecision progressDecision = StartIdleContinue(progressPolicy);
        progressPolicy.TryRecordActionAttempted(progressDecision, Start.AddSeconds(21));
        progressPolicy.RecordActionResult(progressDecision.DecisionId, RecoveryActionResult.Accepted, Start.AddSeconds(21));
        var progressed = Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Idle,
            NativeStatusVersion = 2,
            ProgressFingerprint = "message:2",
            HasKnownTodoList = true,
            UnfinishedTodoCount = 1
        };
        Expect(progressPolicy.Evaluate(progressed, Start.AddSeconds(30)).Action, RecoveryAction.None,
            "a substantive update acknowledges the request but retry backoff still applies");
        Expect(progressPolicy.Evaluate(progressed, Start.AddSeconds(45)).Action, RecoveryAction.None,
            "acknowledged idle work must settle again before a later continuation");
        Expect(progressPolicy.Evaluate(progressed, Start.AddMinutes(2)).Action, RecoveryAction.Continue,
            "newly acknowledged work may continue after settle and backoff");
    }

    private static void VerifyBusyStallThresholdsAndAbortFlow()
    {
        var policy = new OpenCodeRecoveryPolicy();
        var busy = Observation() with { Status = OpenCodeRecoveryExecutionState.Busy, NativeStatusVersion = 1 };
        Expect(policy.Evaluate(busy, Start).Action, RecoveryAction.None, "fresh busy work must be left alone");
        for (int minute = 1; minute <= 5; minute++)
            Expect(policy.Evaluate(busy, Start.AddMinutes(minute)).Action, RecoveryAction.None,
                "five minutes only marks a suspected stall");
        for (int minute = 6; minute < 15; minute++)
            Expect(policy.Evaluate(busy, Start.AddMinutes(minute)).Action, RecoveryAction.None,
                "the abort threshold is fifteen minutes");

        RecoveryDecision abort = policy.Evaluate(busy, Start.AddMinutes(15));
        Expect(abort.Action, RecoveryAction.Abort, "a confirmed stale busy turn may be aborted after fifteen minutes");
        Require(policy.TryRecordActionAttempted(abort, Start.AddMinutes(15)), "The abort should reserve once.");
        policy.RecordActionResult(abort.DecisionId, RecoveryActionResult.Accepted, Start.AddMinutes(15));

        Expect(policy.Evaluate(busy with { NativeStatusVersion = 2 }, Start.AddMinutes(16)).Action,
            RecoveryAction.None, "a busy status after abort is not confirmation that abort completed");
        var freshIdle = busy with { Status = OpenCodeRecoveryExecutionState.Idle, NativeStatusVersion = 3 };
        Expect(policy.Evaluate(freshIdle, Start.AddMinutes(17)).Action, RecoveryAction.None,
            "abort must be followed by a fresh idle settle period");
        Expect(policy.Evaluate(freshIdle, Start.AddMinutes(17).AddSeconds(20)).Action,
            RecoveryAction.Continue, "fresh native idle after abort permits a continuation");
    }

    private static void VerifyRetryToolAndSafetyGates()
    {
        var retryPolicy = new OpenCodeRecoveryPolicy();
        var busy = Observation() with { Status = OpenCodeRecoveryExecutionState.Busy };
        retryPolicy.Evaluate(busy, Start);
        for (int minute = 1; minute < 30; minute++)
            retryPolicy.Evaluate(busy, Start.AddMinutes(minute));
        var retry = busy with { Status = OpenCodeRecoveryExecutionState.Retry, NativeStatusVersion = 2 };
        Expect(retryPolicy.Evaluate(retry, Start.AddMinutes(30)).Action, RecoveryAction.None,
            "native retry is never interrupted");
        for (int minute = 31; minute <= 60; minute++)
            retryPolicy.Evaluate(retry, Start.AddMinutes(minute));

        var resumedBusy = busy with { NativeStatusVersion = 3 };
        Expect(retryPolicy.Evaluate(resumedBusy, Start.AddMinutes(61)).Action, RecoveryAction.None,
            "retry clearance starts a fresh stall interval");
        for (int minute = 62; minute <= 75; minute++)
            Expect(retryPolicy.Evaluate(resumedBusy, Start.AddMinutes(minute)).Action, RecoveryAction.None,
            "retry wait time is excluded from stall timing");
        Expect(retryPolicy.Evaluate(resumedBusy, Start.AddMinutes(76)).Action, RecoveryAction.Abort,
            "fifteen stable minutes after retry clearance may qualify for abort");

        var toolPolicy = new OpenCodeRecoveryPolicy();
        var toolBusy = busy with { RunningToolCount = 1 };
        toolPolicy.Evaluate(toolBusy, Start);
        for (int minute = 1; minute <= 60; minute++)
            Expect(toolPolicy.Evaluate(toolBusy, Start.AddMinutes(minute)).Action, RecoveryAction.None,
                "a long running tool must not be interrupted");
        var noTool = toolBusy with { RunningToolCount = 0 };
        Expect(toolPolicy.Evaluate(noTool, Start.AddHours(1).AddMinutes(1)).Action, RecoveryAction.None,
            "tool completion starts a fresh stall interval");
        for (int minute = 2; minute <= 15; minute++)
            Expect(toolPolicy.Evaluate(noTool, Start.AddHours(1).AddMinutes(minute)).Action, RecoveryAction.None,
            "tool runtime must not count toward the stall timer");
        Expect(toolPolicy.Evaluate(noTool, Start.AddHours(1).AddMinutes(16)).Action, RecoveryAction.Abort,
            "stall timing restarts when the tool is no longer running");

        var launchPolicy = new OpenCodeRecoveryPolicy();
        var launchTool = busy with
        {
            RunningToolCount = 1,
            StalledLaunchToolFingerprint = "stable-project-launch",
            ActivityFingerprint = "stable-tool-state"
        };
        launchPolicy.Evaluate(launchTool, Start);
        for (int minute = 1; minute < 15; minute++)
            Expect(launchPolicy.Evaluate(launchTool with { NativeStatusVersion = minute + 1 }, Start.AddMinutes(minute)).Action,
                RecoveryAction.None, "A positively identified app launch waits through the same bounded 15-minute stall window.");
        Expect(launchPolicy.Evaluate(launchTool with { NativeStatusVersion = 16 }, Start.AddMinutes(15)).Action,
            RecoveryAction.Abort, "Only the unchanged positively identified app launch becomes abortable after 15 stalled minutes.");

        var changedLaunchPolicy = new OpenCodeRecoveryPolicy();
        changedLaunchPolicy.Evaluate(launchTool, Start);
        var changedLaunch = launchTool with { ActivityFingerprint = "new-output-from-tool", NativeStatusVersion = 2 };
        changedLaunchPolicy.Evaluate(changedLaunch, Start.AddMinutes(14));
        Expect(changedLaunchPolicy.Evaluate(changedLaunch with { NativeStatusVersion = 3 }, Start.AddMinutes(28)).Action,
            RecoveryAction.None, "Output change resets the launch tool's no-progress timer.");

        foreach (RecoveryObservation gated in new[]
        {
            busy with { IsConnected = false },
            busy with { Status = OpenCodeRecoveryExecutionState.Unknown },
            busy with { HasPendingApprovalOrQuestion = true },
            busy with { HasError = true },
            busy with { IsMarkdownValid = false },
            busy with { RequestId = null }
        })
        {
            var gatePolicy = new OpenCodeRecoveryPolicy();
            gatePolicy.Evaluate(gated, Start);
            Expect(gatePolicy.Evaluate(gated, Start.AddMinutes(30)).Action, RecoveryAction.None,
                "safety gates must suppress auto-recovery");
        }
    }

    private static void VerifySafetyGatesRestartTimersWithoutResettingBackoff()
    {
        var policy = new OpenCodeRecoveryPolicy();
        var busy = Observation() with { Status = OpenCodeRecoveryExecutionState.Busy };
        policy.Evaluate(busy, Start);
        for (int minute = 1; minute < 10; minute++)
            policy.Evaluate(busy, Start.AddMinutes(minute));
        var approval = busy with { HasPendingApprovalOrQuestion = true };
        policy.Evaluate(approval, Start.AddMinutes(10));
        for (int minute = 11; minute <= 60; minute++)
            policy.Evaluate(approval, Start.AddMinutes(minute));
        var cleared = busy with { NativeStatusVersion = 3 };
        Expect(policy.Evaluate(cleared, Start.AddHours(1).AddMinutes(1)).Action, RecoveryAction.None,
            "clearing permission wait begins a fresh stable busy interval");
        for (int minute = 2; minute <= 15; minute++)
            Expect(policy.Evaluate(cleared, Start.AddHours(1).AddMinutes(minute)).Action, RecoveryAction.None,
            "permission wait duration is excluded from stall timing");
        Expect(policy.Evaluate(cleared, Start.AddHours(1).AddMinutes(16)).Action, RecoveryAction.Abort,
            "stall timing resumes after the fresh stable interval");
    }

    private static void VerifyPollingGapsAndUnknownTodoStalls()
    {
        var noTodoPolicy = new OpenCodeRecoveryPolicy();
        var busyWithoutTodos = Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Busy,
            HasKnownTodoList = false,
            UnfinishedTodoCount = 0
        };
        noTodoPolicy.Evaluate(busyWithoutTodos, Start);
        RecoveryDecision stalled = RecoveryDecision.None("not evaluated");
        for (int minute = 1; minute <= 5; minute++)
            stalled = noTodoPolicy.Evaluate(busyWithoutTodos, Start.AddMinutes(minute));
        Expect(stalled.Action, RecoveryAction.None, "missing unfinished tasks must suppress automatic recovery");
        Require(stalled.Phase == RecoveryPhase.StalledSuspected,
            "a five minute busy turn should still be reported as suspect without todo evidence");

        var gapPolicy = new OpenCodeRecoveryPolicy();
        var busy = Observation() with { Status = OpenCodeRecoveryExecutionState.Busy };
        gapPolicy.Evaluate(busy, Start);
        Expect(gapPolicy.Evaluate(busy, Start.AddMinutes(3)).Action, RecoveryAction.None,
            "a long gap between observations must suspend recovery");
        Expect(gapPolicy.Evaluate(busy, Start.AddMinutes(4)).Action, RecoveryAction.None,
            "the first poll after a long gap must rearm fresh timers");
        for (int minute = 5; minute < 19; minute++)
            Expect(gapPolicy.Evaluate(busy, Start.AddMinutes(minute)).Action, RecoveryAction.None,
                "stall timing must restart after an observation gap");
        Expect(gapPolicy.Evaluate(busy, Start.AddMinutes(19)).Action, RecoveryAction.Abort,
            "fifteen stable minutes after the fresh poll may qualify for abort");
    }

    private static void VerifyAmbiguousActionNeverDuplicates()
    {
        var policy = new OpenCodeRecoveryPolicy();
        RecoveryDecision decision = StartIdleContinue(policy);
        policy.TryRecordActionAttempted(decision, Start.AddSeconds(21));
        policy.RecordActionResult(decision.DecisionId, RecoveryActionResult.Ambiguous, Start.AddSeconds(21));
        Expect(policy.Evaluate(Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Idle,
            NativeStatusVersion = 20,
            HasKnownTodoList = true,
            UnfinishedTodoCount = 1
        }, Start.AddSeconds(60)).Action, RecoveryAction.None, "ambiguous dispatch must remain latched without native evidence");

        var busy = Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Busy,
            NativeStatusVersion = 21,
            HasKnownTodoList = true,
            UnfinishedTodoCount = 1
        };
        Expect(policy.Evaluate(busy, Start.AddSeconds(61)).Action, RecoveryAction.None,
            "native busy resolves an ambiguous continue without permitting a duplicate send");
        Expect(policy.Evaluate(busy, Start.AddSeconds(62)).Action, RecoveryAction.None,
            "busy work remains protected after ambiguous resolution");
    }

    private static void VerifyDisconnectDoesNotCountAsProgress()
    {
        var policy = new OpenCodeRecoveryPolicy();
        RecoveryDecision first = StartIdleContinue(policy);
        policy.TryRecordActionAttempted(first, Start.AddSeconds(21));
        policy.RecordActionResult(first.DecisionId, RecoveryActionResult.Failed, Start.AddSeconds(21));

        var disconnected = Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Unknown,
            IsConnected = false,
            ProgressFingerprint = null
        };
        Expect(policy.Evaluate(disconnected, Start.AddSeconds(30)).Action, RecoveryAction.None,
            "disconnect is a recovery safety gate");
        Expect(policy.Evaluate(disconnected, Start.AddSeconds(40)).Action, RecoveryAction.None,
            "a null disconnected fingerprint is not progress");

        var reconnected = Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Idle,
            HasKnownTodoList = true,
            UnfinishedTodoCount = 1
        };
        Expect(policy.Evaluate(reconnected, Start.AddSeconds(41)).Action, RecoveryAction.None,
            "reconnect starts a fresh stable interval");

        RecoveryDecision second = policy.Evaluate(reconnected, Start.AddSeconds(81));
        Expect(second.Action, RecoveryAction.Continue, "the first retry becomes eligible after its backoff");
        policy.TryRecordActionAttempted(second, Start.AddSeconds(81));
        policy.RecordActionResult(second.DecisionId, RecoveryActionResult.Failed, Start.AddSeconds(81));
        policy.Evaluate(reconnected, Start.AddSeconds(141));
        policy.Evaluate(reconnected, Start.AddSeconds(201));
        RecoveryDecision third = policy.Evaluate(reconnected, Start.AddSeconds(261));
        Expect(third.Action, RecoveryAction.Continue, "the second retry becomes eligible after its longer backoff");
        policy.TryRecordActionAttempted(third, Start.AddSeconds(261));
        policy.RecordActionResult(third.DecisionId, RecoveryActionResult.Failed, Start.AddSeconds(261));
        Expect(policy.Evaluate(reconnected, Start.AddSeconds(262)).Action, RecoveryAction.None,
            "the third failed continuation waits for the capped ten minute backoff instead of hard-pausing");
        for (int minute = 5; minute <= 14; minute++)
            Expect(policy.Evaluate(reconnected, Start.AddMinutes(minute)).Action, RecoveryAction.None,
                "the capped ten minute backoff remains active after the third failure");
        Expect(policy.Evaluate(reconnected, Start.AddMinutes(14).AddSeconds(21)).Action, RecoveryAction.Continue,
            "the same current request may retry after the capped backoff without new user work");
    }

    private static void VerifyAbortAndContinueShareOneRecoveryCycle()
    {
        var policy = new OpenCodeRecoveryPolicy();
        var busy = Observation() with { Status = OpenCodeRecoveryExecutionState.Busy, NativeStatusVersion = 1 };
        policy.Evaluate(busy, Start);
        var idle = busy with { Status = OpenCodeRecoveryExecutionState.Idle, NativeStatusVersion = 2 };
        policy.Evaluate(idle, Start.AddSeconds(1));
        RecoveryDecision firstContinue = policy.Evaluate(idle, Start.AddSeconds(21));
        policy.TryRecordActionAttempted(firstContinue, Start.AddSeconds(21));
        policy.RecordActionResult(firstContinue.DecisionId, RecoveryActionResult.Accepted, Start.AddSeconds(21));

        var busyAgain = busy with { NativeStatusVersion = 3 };
        policy.Evaluate(busyAgain, Start.AddSeconds(22));
        var idleAgain = idle with { NativeStatusVersion = 4 };
        policy.Evaluate(idleAgain, Start.AddSeconds(23));
        RecoveryDecision secondContinue = policy.Evaluate(idleAgain, Start.AddSeconds(81));
        Expect(secondContinue.Action, RecoveryAction.Continue, "the second continuation is within the retry backoff schedule");
        policy.TryRecordActionAttempted(secondContinue, Start.AddSeconds(81));
        policy.RecordActionResult(secondContinue.DecisionId, RecoveryActionResult.Accepted, Start.AddSeconds(81));

        var secondBusy = busy with { NativeStatusVersion = 5 };
        DateTimeOffset secondBusyAt = Start.AddSeconds(82);
        policy.Evaluate(secondBusy, secondBusyAt);
        for (int minute = 1; minute < 15; minute++)
            policy.Evaluate(secondBusy, secondBusyAt.AddMinutes(minute));
        DateTimeOffset abortAt = secondBusyAt.AddMinutes(15);
        RecoveryDecision abort = policy.Evaluate(secondBusy, abortAt);
        Expect(abort.Action, RecoveryAction.Abort, "a later stale busy turn may be aborted");
        policy.TryRecordActionAttempted(abort, abortAt);
        policy.RecordActionResult(abort.DecisionId, RecoveryActionResult.Accepted, abortAt);

        var afterAbort = idle with { NativeStatusVersion = 6 };
        DateTimeOffset idleAfterAbortAt = abortAt.AddSeconds(1);
        policy.Evaluate(afterAbort, idleAfterAbortAt);
        RecoveryDecision pairedContinue = policy.Evaluate(afterAbort, idleAfterAbortAt.AddSeconds(20));
        Expect(pairedContinue.Action, RecoveryAction.Continue,
            "the abort half of recovery must not consume the final continuation attempt");
        Require(policy.TryRecordActionAttempted(pairedContinue, idleAfterAbortAt.AddSeconds(20)),
            "the paired continue should reserve successfully");
    }

    private static void VerifyThirdAbortMayStillContinueItsRecovery()
    {
        var policy = new OpenCodeRecoveryPolicy();
        var busy = Observation() with { Status = OpenCodeRecoveryExecutionState.Busy, NativeStatusVersion = 1 };
        policy.Evaluate(busy, Start);
        for (int minute = 1; minute <= 14; minute++)
            policy.Evaluate(busy, Start.AddMinutes(minute));

        DateTimeOffset firstAbortAt = Start.AddMinutes(15);
        RecoveryDecision firstAbort = policy.Evaluate(busy, firstAbortAt);
        Expect(firstAbort.Action, RecoveryAction.Abort, "first abort attempt should be eligible");
        policy.TryRecordActionAttempted(firstAbort, firstAbortAt);
        policy.RecordActionResult(firstAbort.DecisionId, RecoveryActionResult.Failed, firstAbortAt);

        DateTimeOffset secondAbortAt = firstAbortAt.AddMinutes(1);
        RecoveryDecision secondAbort = policy.Evaluate(busy, secondAbortAt);
        Expect(secondAbort.Action, RecoveryAction.Abort, "second abort attempt should follow its short backoff");
        policy.TryRecordActionAttempted(secondAbort, secondAbortAt);
        policy.RecordActionResult(secondAbort.DecisionId, RecoveryActionResult.Failed, secondAbortAt);

        policy.Evaluate(busy, secondAbortAt.AddMinutes(1));
        policy.Evaluate(busy, secondAbortAt.AddMinutes(2));
        DateTimeOffset thirdAbortAt = secondAbortAt.AddMinutes(3);
        RecoveryDecision thirdAbort = policy.Evaluate(busy, thirdAbortAt);
        Expect(thirdAbort.Action, RecoveryAction.Abort, "third abort attempt should follow its longer backoff");
        policy.TryRecordActionAttempted(thirdAbort, thirdAbortAt);
        policy.RecordActionResult(thirdAbort.DecisionId, RecoveryActionResult.Accepted, thirdAbortAt);

        var idle = busy with { Status = OpenCodeRecoveryExecutionState.Idle, NativeStatusVersion = 2 };
        policy.Evaluate(idle, thirdAbortAt.AddMinutes(1));
        RecoveryDecision continuation = policy.Evaluate(idle, thirdAbortAt.AddMinutes(1).AddSeconds(20));
        Expect(continuation.Action, RecoveryAction.Continue,
            "the third abort must not consume or block its paired continuation");
        Require(policy.TryRecordActionAttempted(continuation, thirdAbortAt.AddMinutes(1).AddSeconds(20)),
            "the paired continuation after the third abort should reserve successfully");
    }

    private static void VerifyNoProgressAttemptsBackOffWithoutHardLimit()
    {
        var policy = new OpenCodeRecoveryPolicy();
        RecoveryObservation idle = Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Idle,
            HasKnownTodoList = true,
            UnfinishedTodoCount = 1
        };
        RecoveryDecision decision = StartIdleContinue(policy);
        DateTimeOffset attemptedAt = Start.AddSeconds(21);
        TimeSpan[] delays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)];

        for (int attempt = 1; attempt <= delays.Length + 1; attempt++)
        {
            Require(policy.TryRecordActionAttempted(decision, attemptedAt), "Retry reservation should succeed.");
            policy.RecordActionResult(decision.DecisionId, RecoveryActionResult.Failed, attemptedAt);
            if (attempt == delays.Length + 1) break;

            TimeSpan delay = delays[attempt - 1];
            DateTimeOffset dueAt = attemptedAt + delay;
            if (delay == TimeSpan.FromMinutes(1))
            {
                Expect(policy.Evaluate(idle, dueAt - TimeSpan.FromSeconds(1)).Action, RecoveryAction.None,
                    "the one minute backoff blocks an early retry");
            }
            else
            {
                for (DateTimeOffset pollAt = attemptedAt + TimeSpan.FromMinutes(1); pollAt < dueAt; pollAt += TimeSpan.FromMinutes(1))
                    Expect(policy.Evaluate(idle, pollAt).Action, RecoveryAction.None,
                        "the three/ten minute capped backoff blocks early retries");
            }

            decision = policy.Evaluate(idle, dueAt);
            Expect(decision.Action, RecoveryAction.Continue,
                $"attempt {attempt + 1} remains available after its capped backoff without new user work or progress");
            attemptedAt = dueAt;
        }

        // After five failures, approval still blocks retries. Clearing it preserves the
        // existing backoff and requires a fresh 20 second idle settle interval.
        var approvalPolicy = new OpenCodeRecoveryPolicy();
        RecoveryDecision approvalDecision = StartIdleContinue(approvalPolicy);
        DateTimeOffset approvalAttemptAt = Start.AddSeconds(21);
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            Require(approvalPolicy.TryRecordActionAttempted(approvalDecision, approvalAttemptAt),
                "Repeated gated test reservation should succeed.");
            approvalPolicy.RecordActionResult(approvalDecision.DecisionId, RecoveryActionResult.Failed, approvalAttemptAt);
            if (attempt < 5)
            {
                TimeSpan delay = TimeSpan.FromMinutes(attempt == 1 ? 1 : attempt == 2 ? 3 : 10);
                DateTimeOffset dueAt = approvalAttemptAt + delay;
                for (DateTimeOffset pollAt = approvalAttemptAt + TimeSpan.FromMinutes(1); pollAt < dueAt; pollAt += TimeSpan.FromMinutes(1))
                    approvalPolicy.Evaluate(idle, pollAt);
                approvalDecision = approvalPolicy.Evaluate(idle, dueAt);
                Expect(approvalDecision.Action, RecoveryAction.Continue, "repeated retries are available before the safety gate test");
                approvalAttemptAt = dueAt;
            }
        }

        DateTimeOffset nextDueAt = approvalAttemptAt.AddMinutes(10);
        var approval = idle with { HasPendingApprovalOrQuestion = true };
        for (DateTimeOffset pollAt = approvalAttemptAt.AddMinutes(1); pollAt <= approvalAttemptAt.AddMinutes(8); pollAt += TimeSpan.FromMinutes(1))
            Expect(approvalPolicy.Evaluate(approval, pollAt).Action, RecoveryAction.None,
                "approval remains a hard safety block after repeated failed continuations");
        var cleared = idle;
        Expect(approvalPolicy.Evaluate(cleared, nextDueAt - TimeSpan.FromSeconds(20)).Action, RecoveryAction.None,
            "clearing approval requires a fresh stable interval and does not bypass the pending backoff");
        Expect(approvalPolicy.Evaluate(cleared, nextDueAt - TimeSpan.FromSeconds(1)).Action, RecoveryAction.None,
            "the capped ten minute backoff remains active after approval clears");
        Expect(approvalPolicy.Evaluate(cleared, nextDueAt).Action, RecoveryAction.Continue,
            "retry becomes available only after both the backoff and fresh settle interval pass");

        var toolPolicy = new OpenCodeRecoveryPolicy();
        var busyWithTool = Observation() with { Status = OpenCodeRecoveryExecutionState.Busy, RunningToolCount = 1 };
        toolPolicy.Evaluate(busyWithTool, Start);
        Expect(toolPolicy.Evaluate(busyWithTool, Start.AddMinutes(1)).Action, RecoveryAction.None,
            "a running tool remains protected from interruption");
        Expect(toolPolicy.Evaluate(busyWithTool, Start.AddMinutes(2)).Action, RecoveryAction.None,
            "a running tool remains protected regardless of the recovery attempt count");

        // Accepted but unacknowledged sends still hard-pause rather than duplicate.
        var acknowledgementPolicy = new OpenCodeRecoveryPolicy();
        RecoveryDecision unacknowledged = StartIdleContinue(acknowledgementPolicy);
        acknowledgementPolicy.TryRecordActionAttempted(unacknowledged, Start.AddSeconds(21));
        acknowledgementPolicy.RecordActionResult(unacknowledged.DecisionId, RecoveryActionResult.Accepted, Start.AddSeconds(21));
        Expect(acknowledgementPolicy.Evaluate(idle, Start.AddSeconds(81)).Action, RecoveryAction.None,
            "an accepted send waits for native acknowledgement");
        Expect(acknowledgementPolicy.Evaluate(idle, Start.AddSeconds(141)).Action, RecoveryAction.Pause,
            "an accepted send without native acknowledgement remains paused and cannot be duplicated");

        var cyclesPolicy = new OpenCodeRecoveryPolicy();
        DateTimeOffset time = Start;
        cyclesPolicy.Evaluate(Observation() with { Status = OpenCodeRecoveryExecutionState.Busy }, time);
        time += TimeSpan.FromSeconds(1);
        var current = Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Idle,
            NativeStatusVersion = 2,
            HasKnownTodoList = true,
            UnfinishedTodoCount = 1
        };
        cyclesPolicy.Evaluate(current, time);
        time += TimeSpan.FromSeconds(20);

        const int cyclesWithMeasuredProgress = 8;
        for (int cycle = 0; cycle < cyclesWithMeasuredProgress; cycle++)
        {
            decision = cyclesPolicy.Evaluate(current, time);
            Expect(decision.Action, RecoveryAction.Continue,
                $"cycle {cycle + 1} continues while each new native message and unfinished todo provide real evidence");
            cyclesPolicy.TryRecordActionAttempted(decision, time);
            cyclesPolicy.RecordActionResult(decision.DecisionId, RecoveryActionResult.Accepted, time);

            time += TimeSpan.FromSeconds(1);
            current = current with { ProgressFingerprint = "completed-todo:" + (cycle + 1) };
            cyclesPolicy.Evaluate(current, time);
            time += TimeSpan.FromMinutes(1);
            time += OpenCodeRecoveryPolicy.IdleSettlePeriod;
        }
    }
    private static void VerifyManualStopAndNewWorkEpoch()
    {
        var policy = new OpenCodeRecoveryPolicy();
        var busy = Observation() with { Status = OpenCodeRecoveryExecutionState.Busy };
        policy.Evaluate(busy, Start);
        Expect(policy.Evaluate(busy with { ManualStopLatched = true }, Start.AddSeconds(1)).Action,
            RecoveryAction.Pause, "manual stop must latch recovery off");
        Expect(policy.Evaluate(busy with { ManualStopLatched = false }, Start.AddMinutes(20)).Action,
            RecoveryAction.Pause, "repainting or clearing the immediate stop flag must not clear the latch");

        var nextWork = busy with
        {
            ManualStopLatched = false,
            UserWorkGeneration = 2,
            RequestId = "user-request-2"
        };
        Expect(policy.Evaluate(nextWork, Start.AddMinutes(21)).Action, RecoveryAction.None,
            "new real user work resets the old stop latch but still must meet stall timing");
    }

    private static RecoveryDecision StartIdleContinue(OpenCodeRecoveryPolicy policy)
    {
        policy.Evaluate(Observation() with { Status = OpenCodeRecoveryExecutionState.Busy }, Start);
        var idle = Observation() with
        {
            Status = OpenCodeRecoveryExecutionState.Idle,
            NativeStatusVersion = 2,
            HasKnownTodoList = true,
            UnfinishedTodoCount = 1
        };
        policy.Evaluate(idle, Start.AddSeconds(1));
        return policy.Evaluate(idle, Start.AddSeconds(21));
    }

    private static RecoveryObservation Observation() => new(
        "session-1", 1, 1, 1, "user-request-1",
        OpenCodeRecoveryExecutionState.Unknown, 1,
        IsConnected: true,
        HasPendingApprovalOrQuestion: false,
        HasError: false,
        RunningToolCount: 0,
        HasKnownTodoList: true,
        UnfinishedTodoCount: 1,
        IsMarkdownValid: true,
        ManualStopLatched: false,
        RecoveryEnabled: true,
        ProgressFingerprint: "initial");

    private static void Expect(RecoveryAction actual, RecoveryAction expected, string message)
    {
        if (actual != expected)
            throw new InvalidOperationException($"{message}. Expected {expected}, got {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
