using System.IO;
using System.Reflection;
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
        string directory = Path.Combine(Path.GetTempPath(), "TerminalNotes NativeAutomationPane " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(directory, "appdata"));

        try
        {
            VerifyNativeIdleGate(directory);
            VerifySubmissionRemainsPendingUntilNativeEvidence(directory);
            VerifySupervisionStatus(directory);
            VerifyStartupRecoveryGuards(directory);
            VerifyInterruptCancelsDispatch(directory);
            VerifyDisposeCancelsDispatch(directory);
        }
        catch (Exception ex)
        {
            _failures++;
            Console.Error.WriteLine("FAIL unhandled: " + ex);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }

        Console.WriteLine(_failures == 0 ? "Native automation pane checks passed." : $"{_failures} native automation pane check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void VerifyNativeIdleGate(string directory)
    {
        using var pane = CreatePane(directory, OpenCodeStatusKind.Idle);
        Check(Get<bool>(pane, "CanStartNativeAutomation"), "a ready native idle pane accepts automation");

        foreach (OpenCodeStatusKind status in new[]
                 {
                     OpenCodeStatusKind.Unknown, OpenCodeStatusKind.Pending,
                     OpenCodeStatusKind.Busy, OpenCodeStatusKind.NeedsInput
                 })
        {
            SetStatus(pane, status);
            Check(!Get<bool>(pane, "CanStartNativeAutomation"), $"native {status} blocks automation start");
        }

        SetStatus(pane, OpenCodeStatusKind.Idle);
        Field<TerminalInputCapture>(pane, "_inputCapture").Observe("unsent draft");
        Check(!Get<bool>(pane, "CanStartNativeAutomation"), "a local unsent draft blocks automation start");
        Field<TerminalInputCapture>(pane, "_inputCapture").Clear();
        Check(Get<bool>(pane, "CanStartNativeAutomation"), "clearing the local draft restores the native idle gate");
    }

    private static void VerifySubmissionRemainsPendingUntilNativeEvidence(string directory)
    {
        using var pane = CreatePane(directory, OpenCodeStatusKind.Idle);
        var monitor = Field<OpenCodeStatusMonitor>(pane, "_openCodeStatus");
        monitor.StatusChanged += snapshot =>
        {
            SetField(pane, "_openCodeSnapshot", snapshot);
            Call(pane, "ApplyActivityChange");
        };

        Call(pane, "BeginNativeAutomation");
        var cancellation = Get<CancellationToken>(pane, "NativeAutomationCancellation");
        Call(pane, "ObserveNativeAutomationSubmission");

        Check(Get<OpenCodeStatusSnapshot>(monitor, "Snapshot").Kind == OpenCodeStatusKind.Pending,
            "the pre-submit hook records an optimistic pending state");
        Check(pane.Activity == TerminalActivity.Starting && !pane.HasCompletedWork &&
              TerminalActivityPalette.Background(pane.Activity, pane.HasCompletedWork) != "#00853F",
            "an optimistic submission stays pending instead of painting a native pane green");
        Check(!string.IsNullOrWhiteSpace(pane.ActivityLabel) && Get<bool>(pane, "IsNativeAutomationActive"),
            "the pending label remains active until native completion evidence arrives");

        Call(pane, "SetAutomationBusy", false);
        Check(!Get<bool>(pane, "IsNativeAutomationActive") && !cancellation.IsCancellationRequested,
            "normal stage completion releases pane ownership without canceling its token");
    }

    private static void VerifySupervisionStatus(string directory)
    {
        using var pane = CreatePane(directory, OpenCodeStatusKind.Idle);
        Call(pane, "BeginNativeAutomation");
        SetStatus(pane, OpenCodeStatusKind.Busy);
        Check(pane.Activity == TerminalActivity.Quiet, "busy alone stays gray until the supervisor observes real response evidence");
        Call(pane, "SetNativeAutomationSupervisionNotice", "정체 작업 중단 확인 중");
        Check(pane.Activity == TerminalActivity.Quiet && pane.ActivityLabel == "정체 작업 중단 확인 중" && !pane.HasCompletedWork,
            "a stalled busy terminal is gray with a recovery label, never marked complete");
        SetStatus(pane, OpenCodeStatusKind.NeedsInput);
        Check(pane.Activity == TerminalActivity.NeedsInput, "native permission keeps priority over recovery status");
        Call(pane, "SetAutomationBusy", false);
        Check(Field<string?>(pane, "_nativeAutomationSupervisionNotice") is null, "releasing a stage clears its supervision notice");
        SetStatus(pane, OpenCodeStatusKind.Busy);
        SetField(Field<ConPtySession>(pane, "_session"), "_running", false);
        Check(pane.Activity == TerminalActivity.Exited && pane.ActivityLabel == "터미널 종료",
            "a terminated native process cannot stay green while an old busy snapshot remains");
    }

    private static void VerifyStartupRecoveryGuards(string directory)
    {
        using var pane = CreatePane(directory, OpenCodeStatusKind.NeedsInput);
        bool Stop() => ((Task<bool>)pane.GetType().GetMethod("StopUnresponsiveStartupProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pane, null)!).GetAwaiter().GetResult();
        Check(!Stop(), "startup recovery preserves native approval and question waits");
        SetStatus(pane, OpenCodeStatusKind.Unknown);
        Field<TerminalInputCapture>(pane, "_inputCapture").Observe("draft");
        Check(!Stop(), "startup recovery preserves manual input");
        Field<TerminalInputCapture>(pane, "_inputCapture").Clear();
        SetField(pane, "_startupUnresponsiveSince", DateTimeOffset.UtcNow.AddMinutes(-4));
        pane.GetType().GetProperty("IsUserInputBlocked", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pane, true);
        Check(!Stop(), "startup recovery preserves blocked pane input");
        Call(pane, "ObserveStartupReadiness");
        Check(Field<DateTimeOffset?>(pane, "_startupUnresponsiveSince") is null, "blocked input does not accumulate startup inactivity");
        pane.GetType().GetProperty("IsUserInputBlocked", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pane, false);
        SetStatus(pane, OpenCodeStatusKind.Idle);
        Call(pane, "BeginNativeAutomation");
        SetStatus(pane, OpenCodeStatusKind.Unknown);
        Check(!Stop(), "startup recovery cannot kill a submitted active dispatch");
    }

    private static void VerifyInterruptCancelsDispatch(string directory)
    {
        using var pane = CreatePane(directory, OpenCodeStatusKind.Idle);
        Call(pane, "BeginNativeAutomation");
        CancellationToken cancellation = Get<CancellationToken>(pane, "NativeAutomationCancellation");
        SetField(Field<ConPtySession>(pane, "_session"), "_running", false);

        pane.Interrupt();

        Check(cancellation.IsCancellationRequested && !Get<bool>(pane, "IsNativeAutomationActive"),
            "Interrupt cancels the in-flight native automation token");
    }

    private static void VerifyDisposeCancelsDispatch(string directory)
    {
        var pane = CreatePane(directory, OpenCodeStatusKind.Idle);
        Call(pane, "BeginNativeAutomation");
        CancellationToken cancellation = Get<CancellationToken>(pane, "NativeAutomationCancellation");
        SetField(Field<ConPtySession>(pane, "_session"), "_running", false);

        pane.Dispose();

        Check(cancellation.IsCancellationRequested && !Get<bool>(pane, "IsNativeAutomationActive"),
            "Dispose cancels the in-flight native automation token");
    }

    private static TerminalPane CreatePane(string directory, OpenCodeStatusKind status)
    {
        var pane = new TerminalPane(directory);
        var monitor = new OpenCodeStatusMonitor(new Uri("http://127.0.0.1:43127/"), directory);
        SetField(monitor, "_connected", true);
        SetField(monitor, "_hasBaseline", true);
        SetField(pane, "_usesNativeOpenCodeStatus", true);
        SetField(pane, "_openCodeStatus", monitor);
        SetField(pane, "_ready", true);
        SetField(Field<ConPtySession>(pane, "_session"), "_running", true);
        SetStatus(pane, status);
        return pane;
    }

    private static void SetStatus(TerminalPane pane, OpenCodeStatusKind status)
    {
        var snapshot = new OpenCodeStatusSnapshot(status, status == OpenCodeStatusKind.Idle, "test-session");
        SetField(Field<OpenCodeStatusMonitor>(pane, "_openCodeStatus"), "_snapshot", snapshot);
        SetField(pane, "_openCodeSnapshot", snapshot);
    }

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target)!;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            _failures++;
            Console.Error.WriteLine("FAIL " + name);
            return;
        }
        Console.WriteLine("PASS " + name);
    }
}
