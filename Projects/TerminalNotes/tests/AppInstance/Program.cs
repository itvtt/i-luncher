using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _failure;
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--activate")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            return AppInstanceCoordinator.TryActivateAsync(args[1], args.Length > 2 ? args[2] : null, timeout.Token).GetAwaiter().GetResult() ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "--hold")
        {
            using var owner = AppInstanceCoordinator.TryAcquire(args[1]);
            if (owner is null) return 2;
            File.WriteAllText(args[2], "owned");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await VerifyAsync(); }
            catch (Exception exception) { _failure = 1; Console.Error.WriteLine(exception); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return _failure;
    }

    private static async Task VerifyAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes Instance 한글 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings + ".instance.lock", "stale owner");
        File.WriteAllText(settings + ".automation.lock", "stale scheduler");
        using var owner = AppInstanceCoordinator.TryAcquire(settings) ?? throw new Exception("An old lock file prevented startup.");
        Require(AppInstanceCoordinator.TryAcquire(settings) is null, "a concurrent launch cannot own the same dashboard");
        Require(AppInstanceCoordinator.ReadOwnerProcessId(settings) == Environment.ProcessId, "activation can identify the existing owner without breaking its lock");
        using (var unrelated = AppInstanceCoordinator.TryAcquire(Path.Combine(root, "other.json")))
            Require(unrelated is not null, "independent settings remain isolated");
        var window = new MainWindow(settings, autoStartCodex: false);
        string? forwardedProject = null;
        int callbacks = 0;
        try
        {
            window.Show();
            await UntilAsync(() => Field(window, "_automationOwnership") is not null);
            object scheduler = Field(window, "_automationOwnership")!;
            window.WindowState = WindowState.Minimized;
            window.Hide();
            owner.StartListening(async project => await window.Dispatcher.InvokeAsync(() =>
            {
                callbacks++;
                forwardedProject = project;
                // Verify the actual restore path without opening any configured CLI.
                return (bool)Call(window, "ActivateFromLauncher", new object?[] { null })!;
            }));
            using (var launcher = StartChild("--activate", settings))
            {
                await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Require(launcher.ExitCode == 0 && window.IsVisible && window.WindowState == WindowState.Normal,
                    "a second process restores the hidden/minimized window instead of creating a blocked window");
            }
            Require(ReferenceEquals(scheduler, Field(window, "_automationOwnership")) && Application.Current.Windows.Count == 1,
                "activation preserves the same scheduler and single window");
            string requestedProject = Path.Combine(root, "project with spaces 한글");
            using (var launcher = StartChild("--activate", settings, requestedProject))
            {
                await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Require(launcher.ExitCode == 0 && forwardedProject == requestedProject,
                    "workspace arguments survive the cross-process handoff");
            }
            using (var partial = new NamedPipeClientStream(".", owner.PipeName, PipeDirection.Out, PipeOptions.Asynchronous))
            {
                await partial.ConnectAsync(3000);
                await partial.WriteAsync(new byte[] { 4 });
            }
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
                Require(await AppInstanceCoordinator.TryActivateAsync(settings, null, timeout.Token) && callbacks == 3,
                    "a disconnected partial launch does not disable subsequent activation");
        }
        finally { Call(window, "DisposeResources"); window.Close(); }
        owner.Dispose();
        await Task.Delay(100);
        using (var next = AppInstanceCoordinator.TryAcquire(settings))
            Require(next is not null && File.Exists(settings + ".instance.lock"), "normal exit permits relaunch without deleting lock files");

        string crashSettings = Path.Combine(root, "crash.json"), ready = Path.Combine(root, "owner.ready");
        using (var child = StartChild("--hold", crashSettings, ready))
        {
            try
            {
                await UntilAsync(() => File.Exists(ready));
                Require(AppInstanceCoordinator.TryAcquire(crashSettings) is null, "a live child process retains exclusive ownership");
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                using var recovered = AppInstanceCoordinator.TryAcquire(crashSettings);
                Require(recovered is not null, "a crashed owner's OS handle is released despite its remaining lock file");
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        }
        string blockedSettings = Path.Combine(root, "denied.json");
        Directory.CreateDirectory(blockedSettings + ".instance.lock");
        try { using var ignored = AppInstanceCoordinator.TryAcquire(blockedSettings); throw new Exception("Access failure was ignored."); }
        catch (UnauthorizedAccessException) { Console.WriteLine("PASS access failures remain distinct from a live competing instance"); }
        string deniedScheduler = Path.Combine(root, "scheduler-denied.json");
        Directory.CreateDirectory(deniedScheduler + ".automation.lock");
        var deniedWindow = new MainWindow(deniedScheduler, autoStartCodex: false);
        try
        {
            deniedWindow.Show();
            await UntilAsync(() => Field(deniedWindow, "_automationInitializationError") is string);
            string reason = (string)Field(deniedWindow, "_automationInitializationError")!;
            Require(reason.Contains("접근 실패") && !reason.Contains("이미 열린"),
                "automation access failures identify the file error instead of claiming another window owns it");
        }
        finally { Call(deniedWindow, "DisposeResources"); deniedWindow.Close(); }
        Console.WriteLine("APP INSTANCE TESTS PASSED");
    }

    private static Process StartChild(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }
}
