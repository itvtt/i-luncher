using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string directory = Path.Combine(Path.GetTempPath(), "TerminalNotes PlanDispatch " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var pane = new TerminalPane(directory);
        try
        {
            VerifyExecutionTimingPolicy();
            VerifyProjectPlanInputNotice(pane);
            VerifyGateAllowsOnlyReadyNativeIdle();
            VerifyDraftCaptureAndProtocolResponses();
            VerifyPartialPasteFailureLatchesUntilInterrupt(pane, directory);
            VerifyRebindCancelsPartialPaste(pane, directory);
            VerifySuccessfulTrackedSubmitCompletesOnce(pane, directory);
            VerifyTerminalPaneAcceptsDelayedNativeConfirmation(pane, directory);
            VerifyInterruptDuringConfirmationDoesNotRelatch(pane, directory);
            VerifyQuestionTrackedSubmitSkipsRecoveryCorrelation(pane, directory);
            VerifyMultilineRecoveryCapture(pane, directory);
            Check(!pane.IsRunning && !pane.LaunchesCodex,
                "the transport hook checks run without starting OpenCode or sending a model request");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.Error.WriteLine("FAIL unhandled: " + ex);
        }
        finally
        {
            pane.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }

        Console.WriteLine(_failures == 0 ? "PlanDispatch checks passed." : $"{_failures} PlanDispatch check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void VerifyExecutionTimingPolicy()
    {
        Check(TerminalExecutionTiming.StartupReadyTimeout == TimeSpan.FromMinutes(5) &&
              TerminalExecutionTiming.NativeSubmissionConfirmationTimeout == TimeSpan.FromMinutes(2) &&
              TerminalExecutionTiming.MetadataRequestTimeout == TimeSpan.FromSeconds(30) &&
              TerminalExecutionTiming.NativeStatusHandshakeTimeout == TimeSpan.FromSeconds(30) &&
              TerminalExecutionTiming.InstallationProbeTimeout == TimeSpan.FromSeconds(30) &&
              TerminalExecutionTiming.SlowStartupNoticeAfter == TimeSpan.FromSeconds(20),
            "startup, metadata, native acceptance, and slow-start notices use the shared bounded policy");
    }

    private static void VerifyProjectPlanInputNotice(TerminalPane pane)
    {
        Call(pane, "ShowProjectPlanInputNotice", true);
        var notice = Field<System.Windows.Controls.Border>(pane, "_projectPlanInputNotice");
        var text = (System.Windows.Controls.TextBlock)notice.Child;
        Check(notice.Visibility == Visibility.Visible && text.Text.Contains("플랜을 입력해 주세요", StringComparison.Ordinal),
            "a terminal-local plan request is visible without a renderer or model response");
        Check(System.Windows.Controls.Grid.GetRow(Field<object>(pane, "_browser") as UIElement) == 1,
            "the plan notice reserves its own row rather than covering the terminal input");
        Call(pane, "ShowProjectPlanInputNotice", false);
        Check(notice.Visibility == Visibility.Collapsed, "a valid plan can dismiss the plan request");
    }
    private static void VerifyGateAllowsOnlyReadyNativeIdle()
    {
        Check(GateReason(status: OpenCodeStatusKind.Idle) is null,
            "the pure execution gate accepts a ready native idle session");
        foreach (OpenCodeStatusKind status in new[]
                 {
                     OpenCodeStatusKind.Unknown, OpenCodeStatusKind.Pending,
                     OpenCodeStatusKind.Busy, OpenCodeStatusKind.NeedsInput
                 })
            Check(!string.IsNullOrWhiteSpace(GateReason(status)), $"native {status} blocks saved plan dispatch");

        Check(!string.IsNullOrWhiteSpace(GateReason(status: OpenCodeStatusKind.Idle, native: false)),
            "a PowerShell pane blocks even when all other execution state is idle");
        Check(!string.IsNullOrWhiteSpace(GateReason(status: OpenCodeStatusKind.Idle, manual: true)),
            "manual override blocks saved plan dispatch");
        Check(!string.IsNullOrWhiteSpace(GateReason(status: OpenCodeStatusKind.Idle, automationBusy: true)),
            "automation busy blocks saved plan dispatch");
        Check(!string.IsNullOrWhiteSpace(GateReason(status: OpenCodeStatusKind.Idle, userBlocked: true)),
            "user-blocked input blocks saved plan dispatch");
        Check(!string.IsNullOrWhiteSpace(GateReason(status: OpenCodeStatusKind.Idle, processRunning: false)),
            "a dead terminal process blocks saved plan dispatch");
    }

    private static void VerifyDraftCaptureAndProtocolResponses()
    {
        var capture = new TerminalInputCapture();
        capture.Observe("unsent draft");
        Check(capture.HasPendingInput, "captured unsent user text blocks dispatch");
        Check(!string.IsNullOrWhiteSpace(GateReason(status: OpenCodeStatusKind.Idle, pendingInput: true)),
            "the native idle gate rejects a nonempty terminal composer");

        Check(TerminalInputCapture.IsTerminalResponse("\x1b[?1;2c\x1b[12;40R\x1b[0n\x1b[I\x1b[O") &&
              TerminalInputCapture.IsTerminalResponse("\x1b]10;rgb:ffff/0000/1234\x1b\\"),
            "known CSI and OSC responses can be excluded from draft capture");
    }

    private static void VerifyPartialPasteFailureLatchesUntilInterrupt(TerminalPane pane, string directory)
    {
        string firstPath = Path.Combine(directory, "plan.md");
        Call(pane, "BindSavedPlanExecutionPath", firstPath);
        SetField(pane, "_usesNativeOpenCodeStatus", true);
        long sendId = 101;
        object dispatch = AttachTrackedSend(pane, sendId, "saved-plan prompt");
        SetProperty(dispatch, "PromptWasWritten", true);

        bool canWriteEnter = (bool)Call(pane, "CanWriteSavedPlanDispatch", sendId, "\r")!;
        Check(!canWriteEnter && Field<bool>(pane, "_savedPlanComposerMayContainPendingInput"),
            "missing native idle readiness before Enter blocks that write and latches the partial composer text");
        Check(!(bool)Call(pane, "CanWriteSavedPlanDispatch", sendId, "\r")!,
            "a cancelled send ID remains blocked against delayed duplicate Enter");

        string? reason = GateReason(status: OpenCodeStatusKind.Idle, pendingInput: true);
        Check(reason?.Contains("입력창을 비우", StringComparison.Ordinal) == true,
            "the partial composer block tells the user how to clear it");

        var capture = Field<TerminalInputCapture>(pane, "_inputCapture");
        capture.Observe("draft");
        pane.Interrupt();
        Check(!capture.HasPendingInput && !Field<bool>(pane, "_savedPlanComposerMayContainPendingInput"),
            "explicit Interrupt clears both captured input and the partial-paste latch");
    }

    private static void VerifyRebindCancelsPartialPaste(TerminalPane pane, string directory)
    {
        Call(pane, "BindSavedPlanExecutionPath", Path.Combine(directory, "before.md"));
        long sendId = 202;
        _ = AttachTrackedSend(pane, sendId, "saved-plan prompt");
        SetProperty(TrackedDispatch(pane, sendId), "PromptWasWritten", true);

        Call(pane, "BindSavedPlanExecutionPath", Path.Combine(directory, "after.md"));
        Check(!(bool)Call(pane, "CanWriteSavedPlanDispatch", sendId, "\r")! &&
              Field<bool>(pane, "_savedPlanComposerMayContainPendingInput"),
            "changing the bound plan cancels an incomplete paste and blocks its Enter byte");
        Call(pane, "CompleteSavedPlanDispatch", sendId);
        Call(pane, "Interrupt");
    }

    private static void VerifySuccessfulTrackedSubmitCompletesOnce(TerminalPane pane, string directory)
    {
        Call(pane, "BindSavedPlanExecutionPath", Path.Combine(directory, "success.md"));
        long sendId = 303;
        const string multilinePrompt = "saved-plan prompt\r\ncontinued line";
        object dispatch = AttachTrackedSend(pane, sendId, multilinePrompt);

        Call(pane, "PrepareSavedPlanDispatchSubmit", sendId, multilinePrompt);
        Call(pane, "ObserveSavedPlanDispatchWrite", sendId, multilinePrompt);
        Check(!(bool)GetProperty(dispatch, "LocalInputObserved")! && !(bool)GetProperty(dispatch, "Submitted")! &&
              ((IDictionary)GetDictionary(pane, "_savedPlanDispatches")).Contains(sendId),
            "embedded newlines in a multiline paste do not act as the standalone Enter submission");
        Call(pane, "PrepareSavedPlanDispatchSubmit", sendId, "\r");
        Check((bool)GetProperty(dispatch, "LocalInputObserved")!,
            "the local submission is armed immediately before the standalone tracked Enter write");
        Call(pane, "ObserveSavedPlanDispatchWrite", sendId, "\r");

        Check((bool)GetProperty(dispatch, "Submitted")! &&
              !Field<bool>(pane, "_savedPlanComposerMayContainPendingInput") &&
              !((IDictionary)GetDictionary(pane, "_savedPlanDispatches")).Contains(sendId) &&
              (bool)GetProperty(dispatch, "ObserveForAutoResume")!,
            "a proven Enter write completes the tracked submission and removes its partial-input blocker");
        Call(pane, "CompleteSavedPlanDispatch", sendId);

    }

    private static void VerifyInterruptDuringConfirmationDoesNotRelatch(TerminalPane pane, string directory)
    {
        Call(pane, "BindSavedPlanExecutionPath", Path.Combine(directory, "interrupt-confirmation.md"));
        const long sendId = 707;
        object dispatch = AttachTrackedSend(pane, sendId, "submitted prompt");
        Call(pane, "PrepareSavedPlanDispatchSubmit", sendId, "\r");
        Call(pane, "ObserveSavedPlanDispatchWrite", sendId, "submitted prompt");
        Call(pane, "ObserveSavedPlanDispatchWrite", sendId, "\r");
        SetField(pane, "_savedPlanComposerMayContainPendingInput", true);

        long dispatchInterruptVersion = (long)GetProperty(dispatch, "ComposerInterruptVersion")!;
        pane.Interrupt();
        Check(!Field<bool>(pane, "_savedPlanComposerMayContainPendingInput"),
            "Interrupt clears the pending composer marker while native confirmation is outstanding");
        Call(pane, "PreserveComposerAfterUnconfirmedSubmission", dispatch);
        Check(!Field<bool>(pane, "_savedPlanComposerMayContainPendingInput") &&
              Field<long>(pane, "_savedPlanComposerInterruptVersion") > dispatchInterruptVersion,
            "late failed native confirmation cannot restore the composer block after Interrupt");
        Call(pane, "CompleteSavedPlanDispatch", sendId);

        using var monitor = new OpenCodeStatusMonitor(new Uri("http://127.0.0.1:43219/"), directory);
        SetField(pane, "_openCodeStatus", monitor);
        const long newerSendId = 708;
        object supersededDispatch = AttachTrackedSend(pane, newerSendId, "superseded prompt");
        Call(pane, "PrepareSavedPlanDispatchSubmit", newerSendId, "\r");
        Call(pane, "ObserveSavedPlanDispatchWrite", newerSendId, "superseded prompt");
        Call(pane, "ObserveSavedPlanDispatchWrite", newerSendId, "\r");
        SetField(pane, "_savedPlanComposerMayContainPendingInput", false);
        monitor.ObserveSubmission();
        Call(pane, "PreserveComposerAfterUnconfirmedSubmission", supersededDispatch);
        Check(!Field<bool>(pane, "_savedPlanComposerMayContainPendingInput"),
            "a failed confirmation from an older generation cannot block a newer local submission");
        Call(pane, "CompleteSavedPlanDispatch", newerSendId);
        SetField(pane, "_openCodeStatus", null);

    }
    private static void VerifyTerminalPaneAcceptsDelayedNativeConfirmation(TerminalPane pane, string directory)
    {
        var monitor = new OpenCodeStatusMonitor(new Uri("http://127.0.0.1:43219/"), directory);
        object session = Field<object>(pane, "_session");
        bool wasRunning = Field<bool>(session, "_running");
        bool wasNative = Field<bool>(pane, "_usesNativeOpenCodeStatus");
        object? priorMonitor = Field<object?>(pane, "_openCodeStatus");
        // This regression synthesizes the legacy PTY transport; it has no headless host.
        SetField(pane, "_usesNativeOpenCodeStatus", false);
        SetField(pane, "_openCodeStatus", monitor);
        SetField(session, "_running", true);
        try
        {
            monitor.SetSessionScope("root-session");
            const long sendId = 709;
            object dispatch = AttachTrackedSend(pane, sendId, "delayed native confirmation");
            Call(pane, "PrepareSavedPlanDispatchSubmit", sendId, "\r");
            Call(pane, "ObserveSavedPlanDispatchWrite", sendId, "delayed native confirmation");
            Call(pane, "ObserveSavedPlanDispatchWrite", sendId, "\r");

            var stopwatch = Stopwatch.StartNew();
            Task<bool> wait = (Task<bool>)Call(pane, "WaitForNativeSubmissionConfirmationAsync", dispatch)!;
            Task delayedNativeEvent = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(12));
                long created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using JsonDocument document = JsonDocument.Parse(
                    "{\"info\":{\"role\":\"user\",\"sessionID\":\"root-session\",\"id\":\"delayed-user-message\",\"time\":{\"created\":" + created + "}}}");
                MethodInfo handleEvent = typeof(OpenCodeStatusMonitor).GetMethod("HandleOpenCodeEvent",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                handleEvent.Invoke(monitor, ["message.updated", document.RootElement]);
            });

            bool accepted = wait.GetAwaiter().GetResult();
            delayedNativeEvent.GetAwaiter().GetResult();
            stopwatch.Stop();
            Check(accepted && stopwatch.Elapsed >= TimeSpan.FromSeconds(10),
                "TerminalPane accepts an exact native user-message confirmation delayed beyond the former 10-second limit");
            Call(pane, "CompleteSavedPlanDispatch", sendId);
        }
        finally
        {
            SetField(session, "_running", wasRunning);
            SetField(pane, "_usesNativeOpenCodeStatus", wasNative);
            SetField(pane, "_openCodeStatus", priorMonitor);
            monitor.Dispose();
        }
    }
    private static void VerifyQuestionTrackedSubmitSkipsRecoveryCorrelation(TerminalPane pane, string directory)
    {
        Call(pane, "BindSavedPlanExecutionPath", Path.Combine(directory, "question-plan.md"));
        long sendId = 404;
        object dispatch = AttachTrackedSend(pane, sendId, "user question", observeForAutoResume: false);

        Call(pane, "PrepareSavedPlanDispatchSubmit", sendId, "\r");
        Check(!(bool)GetProperty(dispatch, "LocalInputObserved")! &&
              !(bool)GetProperty(dispatch, "ObserveForAutoResume")!,
            "a question does not open the automatic recovery correlation window");
        Call(pane, "ObserveSavedPlanDispatchWrite", sendId, "user question");
        Call(pane, "ObserveSavedPlanDispatchWrite", sendId, "\r");
        Check((bool)GetProperty(dispatch, "Submitted")!,
            "a question submission is confirmed after its exact prompt and Enter writes succeed");
        Call(pane, "CompleteSavedPlanDispatch", sendId);

    }

    private static void VerifyMultilineRecoveryCapture(TerminalPane pane, string directory)
    {
        using var client = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43219/"), directory);
        using var recovery = new OpenCodeAutoResumeController(directory, Path.Combine(directory, "success.md"), client, () => false);
        recovery.SetEnabled(true);
        SetField(pane, "_autoResume", recovery);
        try
        {
            foreach (bool prepareBeforeWrite in new[] { true, false })
            {
                const string prompt = "첫 번째 요구사항\r\n두 번째 요구사항\n마지막 확인 기준";
                long sendId = prepareBeforeWrite ? 505 : 506;
                _ = AttachTrackedSend(pane, sendId, prompt);
                if (prepareBeforeWrite) Call(pane, "PrepareSavedPlanDispatchSubmit", sendId, "\r");
                Call(pane, "ObserveSavedPlanDispatchWrite", sendId, "\r");
                Check(Field<string>(recovery, "_lastUserInstruction") == prompt,
                    "programmatic Enter preserves the full multiline recovery request (pre-armed=" + prepareBeforeWrite + ")");
                Call(pane, "CompleteSavedPlanDispatch", sendId);
            }
        }
        finally { SetField(pane, "_autoResume", null); }
    }
    private static object AttachTrackedSend(TerminalPane pane, long sendId, string prompt, bool observeForAutoResume = true)
    {
        var dispatchType = typeof(TerminalPane).GetNestedType("SavedPlanDispatch", BindingFlags.NonPublic)!;
        object dispatch = Activator.CreateInstance(dispatchType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, [prompt, null, observeForAutoResume], culture: null)!;
        SetField(pane, "_savedPlanDispatchForNextSend", dispatch);
        _ = Call(pane, "AttachSavedPlanDispatch", sendId, prompt, true);
        return dispatch;
    }

    private static object TrackedDispatch(TerminalPane pane, long sendId) =>
        ((IDictionary)GetDictionary(pane, "_savedPlanDispatches"))[sendId]!;

    private static string? GateReason(OpenCodeStatusKind status, bool native = true, bool manual = false,
        bool automationBusy = false, bool userBlocked = false, bool processRunning = true, bool pendingInput = false)
    {
        var method = typeof(TerminalPane).GetMethod("GetSavedPlanExecutionGateBlockReason",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Type stateType = method.GetParameters().Single().ParameterType;
        var constructor = stateType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(OpenCodeStatusKind)],
            modifiers: null)!;
        object state = constructor.Invoke([native, true, false, false, processRunning, userBlocked, manual,
            automationBusy, pendingInput, status]);
        return (string?)method.Invoke(null, [state]);
    }

    private static object GetDictionary(TerminalPane pane, string name) => Field<object>(pane, name);

    private static object? GetProperty(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetProperty(object target, string name, object? value) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            _failures++;
            Console.Error.WriteLine("FAIL " + message);
            return;
        }
        Console.WriteLine("PASS " + message);
    }
}
