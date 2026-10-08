using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        var app = new Application();
        app.Startup += async (_, _) =>
        {
            try { VerifyRealPowerRequestDoesNotMutateScreenSaverSettings(); VerifyRequestLifetime(); await VerifyWindowAsync(); Console.WriteLine("SLEEP PREVENTION TESTS PASSED"); }
            catch (Exception ex) { _failures++; Console.WriteLine("FAIL " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return _failures;
    }

    private static void VerifyRequestLifetime()
    {
        int acquired = 0, released = 0;
        using var service = new SleepPreventionService(() => { acquired++; return new Lease(() => released++); });
        service.SetRequired(false);
        Assert(acquired == 0, "idle does not create a power request");
        for (int i = 0; i < 20; i++) service.SetRequired(true);
        Assert(service.IsActive && acquired == 1, "polling does not increment native request counts repeatedly");
        service.SetRequired(false); service.SetRequired(false);
        Assert(!service.IsActive && released == 1, "last work release is idempotent");
        service.SetRequired(true); service.ResetAfterResume(); service.SetRequired(true);
        Assert(service.IsActive && acquired == 3 && released == 2, "resume creates a fresh request instead of trusting cleared Windows counts");
        service.Dispose(); service.Dispose(); service.SetRequired(true);
        Assert(!service.IsActive && acquired == 3 && released == 3, "disposal releases once and prevents reacquisition");

        var clock = new TestClock();
        int attempts = 0;
        using var failure = new SleepPreventionService(() =>
        {
            if (++attempts == 1) throw new Win32Exception(5);
            return new Lease(() => { });
        }, clock);
        failure.SetRequired(true);
        Assert(!failure.IsActive && failure.LastError is not null, "failed native request is reported without claiming protection");
        failure.SetRequired(true);
        Assert(attempts == 1, "native request failure does not spin on every UI tick");
        clock.Now = clock.Now.AddSeconds(31); failure.SetRequired(true);
        Assert(failure.IsActive && failure.LastError is null && attempts == 2, "failed request retries and recovers");
    }

    private static void VerifyRealPowerRequestDoesNotMutateScreenSaverSettings()
    {
        uint activeBefore = GetSystemParameter(0x0010); // SPI_GETSCREENSAVEACTIVE
        uint timeoutBefore = GetSystemParameter(0x000E); // SPI_GETSCREENSAVETIMEOUT
        using var service = new SleepPreventionService();
        try
        {
            service.SetRequired(true);
            Assert(service.IsActive && service.LastError is null,
                "the real Windows power request was acquired successfully");
            Assert(GetSystemParameter(0x0010) == activeBefore && GetSystemParameter(0x000E) == timeoutBefore,
                "screensaver active state and timeout remain unchanged while the power request is held");
        }
        finally
        {
            service.SetRequired(false);
        }
        Assert(GetSystemParameter(0x0010) == activeBefore && GetSystemParameter(0x000E) == timeoutBefore,
            "screensaver active state and timeout remain unchanged after releasing the power request");
    }

    private static uint GetSystemParameter(uint action)
    {
        if (SystemParametersInfo(action, 0, out uint value, 0)) return value;
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"SystemParametersInfo getter 0x{action:X4} failed.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);

    private static async Task VerifyWindowAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes sleep prevention " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string appData = Path.Combine(fixture, "app-data");
        Directory.CreateDirectory(appData);
        object? previousDataDirectory = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", appData);
        string settings = Path.Combine(fixture, "settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new { Workspace = fixture, CodexWorkspace = fixture, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        var window = new MainWindow(settings, autoStartCodex: false);
        int acquired = 0, released = 0;
        var guard = new SleepPreventionService(() => { acquired++; return new Lease(() => released++); });
        SetField(window, "_sleepPrevention", guard);
        try
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            Call(window, "InitializeSleepPrevention");
            string powerTooltip = (string)Field<System.Windows.Controls.TextBlock>(window, "StatusLabel").ToolTip;
            Assert(powerTooltip == "Loop 중 화면 보호기/자동 절전/화면 꺼짐 보류. 설정 변경 없음.", "power status accurately describes the held power request without claiming settings changes");
            Field<System.Windows.Threading.DispatcherTimer>(window, "_automationProgressTimer").Stop();
            Field<System.Windows.Threading.DispatcherTimer>(window, "_automationTimer").Stop();
            void Refresh() => Call(window, "UpdateProjectTaskActivity");
            Refresh();
            Assert(!guard.IsActive, "an idle application releases its power request");
            string legacyProject = Path.Combine(fixture, "legacy");
            Directory.CreateDirectory(legacyProject);
            var legacy = new AutomationRun(Guid.NewGuid(), legacyProject, "", new AutomationExecutionOptions(), null, 0,
                AutomationState.Running, AutomationRole.Coordinator, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                null, null, 1, false, 0, null, null, null);
            var runs = Field<Dictionary<Guid, AutomationRun>>(window, "_knownAutomationRuns");
            bool powerRequiredBeforeLegacyRun = (bool)Call(window, "HasWorkRequiringSystemAwake")!;
            runs[legacy.Id] = legacy with { State = AutomationState.Running };
            Assert((bool)Call(window, "HasWorkRequiringSystemAwake")! == powerRequiredBeforeLegacyRun,
                "legacy multi-run history does not change power-lease requirements");
            runs.Remove(legacy.Id);

            string singleProject = Path.Combine(fixture, "single");
            Call(window, "EnsureProjectTaskRow", singleProject);
            Field<Dictionary<string, bool>>(window, "_projectTaskMultiModes")[singleProject] = false;
            Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes")[singleProject] = true;
            var runtime = Call(window, "RuntimeFor", singleProject)!;
            runtime.GetType().GetField("StartingSingle")!.SetValue(runtime, true);
            Refresh();
            Assert(guard.IsActive, "enabled continuous mode holds power request during startup");
            runtime.GetType().GetField("StartingSingle")!.SetValue(runtime, false);
            using var pane = new TerminalPane(singleProject);
            using var monitor = new OpenCodeStatusMonitor(new Uri("http://127.0.0.1:43127/"), singleProject);
            using var recovery = new OpenCodeAutoResumeController(singleProject, null,
                new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43127/"), singleProject), () => false);
            SetField(pane, "_openCodeStatus", monitor); SetField(pane, "_autoResume", recovery);
            SetField(Field<ConPtySession>(pane, "_session"), "_running", true);
            SetField(recovery, "_continuousImprovementEnabled", true);
            SetField(recovery, "_hasUnfinishedWork", true);
            var stateType = typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!;
            var terminalState = Activator.CreateInstance(stateType)!;
            stateType.GetProperty("Pane")!.SetValue(terminalState, pane);
            runtime.GetType().GetField("SingleTerminal")!.SetValue(runtime, new TabItem { Tag = terminalState });
            window.WindowState = WindowState.Minimized;
            window.Hide();
            Assert(!window.IsVisible && window.ShowInTaskbar == false,
                "continuous-work fixture stays hidden from the desktop and taskbar");
            runtime.GetType().GetField("StartingSingle")!.SetValue(runtime, true);
            Refresh();
            Assert(guard.IsActive, "hidden or minimized continuous work retains the power request");
            runtime.GetType().GetField("StartingSingle")!.SetValue(runtime, false);
            foreach (var status in new[] { OpenCodeStatusKind.Busy, OpenCodeStatusKind.Idle, OpenCodeStatusKind.NeedsInput, OpenCodeStatusKind.Unknown })
            {
                SetField(monitor, "_snapshot", new OpenCodeStatusSnapshot(status, false, "power fixture"));
                Refresh();
                Assert(guard.IsActive, "unfinished hidden work retains its power request through " + status);
            }
            int acquiredBeforeResume = acquired, releasedBeforeResume = released;
            Call(window, "PowerModeChanged", window, new PowerModeChangedEventArgs(PowerModes.Resume));
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert(guard.IsActive && acquired == acquiredBeforeResume + 1 && released == releasedBeforeResume + 1,
                "power resume replaces the lease for enabled unfinished continuous work");
            SetField(recovery, "_hasUnfinishedWork", false);
            SetField(monitor, "_snapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.NeedsInput, false, "approval before todo"));
            Refresh();
            Assert(guard.IsActive, "continuous-mode approval wait retains the power request before TODO evidence arrives");
            SetField(monitor, "_snapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Idle, false, "suspended"));
            Refresh();
            Assert(!guard.IsActive, "no-progress continuous work releases the power request");
            SetField(recovery, "_hasUnfinishedWork", true);
            Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes")[singleProject] = false;
            SetField(recovery, "_continuousImprovementEnabled", false);
            SetField(monitor, "_snapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Busy, false, "disabled"));
            Refresh();
            Assert(!guard.IsActive, "disabled continuous improvement releases the power request");
            SetField(recovery, "_continuousImprovementEnabled", true);
            SetField(monitor, "_snapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Idle, true, "verified"));
            SetField(recovery, "_hasVerifiedCompletion", true);
            SetField(recovery, "_hasUnfinishedWork", false); Refresh();
            Assert(!guard.IsActive, "verified single completion releases the power request");
            SetField(monitor, "_snapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Busy, false, "new work"));
            SetField(recovery, "_hasUnfinishedWork", true);
            Refresh();
            runtime.GetType().GetField("SingleStopped")!.SetValue(runtime, true); Refresh();
            Assert(!guard.IsActive, "manual single stop releases power request despite cached busy");
            runtime.GetType().GetField("SingleStopped")!.SetValue(runtime, false); Refresh();
            SetField(Field<ConPtySession>(pane, "_session"), "_running", false); Refresh();
            Assert(!guard.IsActive, "a dead single process cannot leave power request active");
            runtime.GetType().GetField("SingleTerminal")!.SetValue(runtime, null);
            Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes")[singleProject] = true;
            SetField(recovery, "_hasUnfinishedWork", true);
            SetField(Field<ConPtySession>(pane, "_session"), "_running", true);
            runtime.GetType().GetField("SingleTerminal")!.SetValue(runtime, new TabItem { Tag = terminalState });
            Refresh();
            Assert(guard.IsActive, "shutdown fixture owns active continuous work");
            int releasedBeforeExit = released;
            Call(window, "DisposeResources");
            Assert(!guard.IsActive && released == releasedBeforeExit + 1, "application shutdown releases its mock power lease");
        }
        finally
        {
            Call(window, "DisposeResources");
            window.Close();
            AppContext.SetData("TerminalNotes.DataDirectory", previousDataDirectory);
            Console.WriteLine("Fixture: " + fixture);
        }
    }

    private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
