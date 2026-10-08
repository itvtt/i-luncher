using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TerminalNotes;

internal static class Program
{
    private static int _exitCode;

    [STAThread]
    public static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "TerminalNotes.UiResponsivenessTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string logPath = Path.Combine(directory, "ui-diagnostics.jsonl");
            int recoveryNoticeCount = 0;
            var watchdog = new DispatcherHeartbeatWatchdog(
                app.Dispatcher,
                logPath,
                _ => Interlocked.Increment(ref recoveryNoticeCount),
                checkInterval: TimeSpan.FromMilliseconds(50),
                stallThreshold: TimeSpan.FromMilliseconds(400));
            watchdog.UpdateTerminalContext(3, "fixture-project", "resize_started", 1234, newSession: true);

            _ = Task.Run(() => ExerciseBlockedDispatcher(watchdog, app.Dispatcher, logPath, () => Volatile.Read(ref recoveryNoticeCount)))
                .ContinueWith(task => app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (task.IsFaulted) throw task.Exception!.GetBaseException();
                        Console.WriteLine("PASS dispatcher stall is logged off-thread and deduplicated");
                        Console.WriteLine("PASS terminal lifecycle faults are logged off-UI and deduplicated");
                        Console.WriteLine("PASS dispatcher recovery is logged and posts one recovery notice");
                        Console.WriteLine("PASS watchdog disposal stops further diagnostics");
                    }
                    catch (Exception ex)
                    {
                        _exitCode = 1;
                        Console.Error.WriteLine("FAIL " + ex.Message);
                    }
                    finally
                    {
                        watchdog.Dispose();
                        app.Shutdown();
                    }
                })));
        };
        app.Run();
        return _exitCode;
    }

    private static async Task ExerciseBlockedDispatcher(
        DispatcherHeartbeatWatchdog watchdog,
        Dispatcher dispatcher,
        string logPath,
        Func<int> recoveryNoticeCount)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task blocker = Task.Run(() =>
        {
            Thread.Sleep(150);
            dispatcher.Invoke(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(8)))
                    throw new TimeoutException("Test did not release the blocked dispatcher.");
            });
        });

        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Dispatcher blocker did not start.");
            await WaitForEventAsync(logPath, "dispatcher_unresponsive", TimeSpan.FromSeconds(4));
            watchdog.RecordTerminalFailure(2, "pane-a", 2222, "resize_faulted_System.IO.IOException", "resize", "System.IO.IOException");
            watchdog.RecordTerminalFailure(2, "pane-a", 2222, "resize_faulted_System.IO.IOException", "resize", "System.IO.IOException");
            watchdog.RecordTerminalFailure(3, "fixture-project", 1234, "close_faulted_System.InvalidOperationException", "close", "System.InvalidOperationException");
            await WaitForEventAsync(logPath, "terminal_lifecycle_fault", TimeSpan.FromSeconds(3));
            await Task.Delay(1_300);
            string[] whileBlocked = ReadLines(logPath);
            if (whileBlocked.Count(line => EventName(line) == "dispatcher_unresponsive") != 1)
                throw new InvalidOperationException("Blocked dispatcher diagnostics were not deduplicated.");
            if (whileBlocked.Count(line => EventName(line) == "terminal_lifecycle_fault") != 2)
                throw new InvalidOperationException("Terminal lifecycle fault diagnostics were not deduplicated or serialized.");

            release.Set();
            await blocker.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForEventAsync(logPath, "dispatcher_recovered", TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => recoveryNoticeCount() == 1, TimeSpan.FromSeconds(3));

            string[] events = ReadLines(logPath);
            if (events.Count(line => EventName(line) == "dispatcher_recovered") != 1)
                throw new InvalidOperationException("Dispatcher recovery event was not recorded once.");
            using var stall = JsonDocument.Parse(events.Single(line => EventName(line) == "dispatcher_unresponsive"));
            if (stall.RootElement.GetProperty("terminalSlot").GetInt32() != 3 ||
                stall.RootElement.GetProperty("project").GetString() != "fixture-project" ||
                stall.RootElement.GetProperty("terminalStage").GetString() != "resize_started" ||
                stall.RootElement.GetProperty("processId").GetUInt32() != 1234)
                throw new InvalidOperationException("Stall record omitted safe terminal context.");
            using var paneAFault = JsonDocument.Parse(events.Single(line => EventName(line) == "terminal_lifecycle_fault" && TerminalSlot(line) == 2));
            if (paneAFault.RootElement.GetProperty("project").GetString() != "pane-a" ||
                paneAFault.RootElement.GetProperty("processId").GetUInt32() != 2222 ||
                paneAFault.RootElement.GetProperty("exceptionType").GetString() != "System.IO.IOException")
                throw new InvalidOperationException("Lifecycle fault was dropped or attributed to another pane.");
            using var paneBFault = JsonDocument.Parse(events.Single(line => EventName(line) == "terminal_lifecycle_fault" && TerminalSlot(line) == 3));
            if (paneBFault.RootElement.GetProperty("project").GetString() != "fixture-project" ||
                paneBFault.RootElement.GetProperty("processId").GetUInt32() != 1234 ||
                paneBFault.RootElement.GetProperty("exceptionType").GetString() != "System.InvalidOperationException")
                throw new InvalidOperationException("Interleaved lifecycle fault lost pane B identity.");
            using var recovery = JsonDocument.Parse(events.Single(line => EventName(line) == "dispatcher_recovered"));
            if (recovery.RootElement.GetProperty("durationSeconds").GetDouble() < 1.5)
                throw new InvalidOperationException("Recovery duration did not include the full blocked interval.");

            watchdog.Dispose();
            int countBeforeDelay = ReadLines(logPath).Length;
            await Task.Delay(200);
            if (ReadLines(logPath).Length != countBeforeDelay)
                throw new InvalidOperationException("Disposed watchdog wrote another diagnostic.");

            await ExerciseDisposeWithBlockedWriteAsync(dispatcher);
        }
        finally
        {
            release.Set();
            try { await blocker.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }
        }
    }

    private static async Task ExerciseDisposeWithBlockedWriteAsync(Dispatcher dispatcher)
    {
        using var writeEntered = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        using var writeCompleted = new ManualResetEventSlim();
        int writes = 0;
        var watchdog = new DispatcherHeartbeatWatchdog(
            dispatcher,
            "unused-by-injected-writer",
            checkInterval: TimeSpan.FromSeconds(30),
            stallThreshold: TimeSpan.FromMinutes(1),
            appendDiagnostic: (_, _) =>
            {
                Interlocked.Increment(ref writes);
                writeEntered.Set();
                if (!releaseWrite.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Test did not release the blocked diagnostic write.");
                writeCompleted.Set();
            });

        watchdog.RecordTerminalFailure(1, "dispose-test", 9876, "close_faulted", "close", "IOException");
        if (!writeEntered.Wait(TimeSpan.FromSeconds(3)))
            throw new TimeoutException("Diagnostic writer did not enter the blocking test sink.");

        try
        {
            await Task.Run(watchdog.Dispose).WaitAsync(TimeSpan.FromMilliseconds(500));
            watchdog.RecordTerminalFailure(1, "dispose-test", 9876, "close_faulted", "close", "IOException");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Watchdog disposal waited for a diagnostic write.");
        }
        finally
        {
            releaseWrite.Set();
        }

        if (!writeCompleted.Wait(TimeSpan.FromSeconds(3)))
            throw new TimeoutException("An in-flight diagnostic write did not finish after release.");
        await Task.Delay(150);
        if (Volatile.Read(ref writes) != 1)
            throw new InvalidOperationException("Disposal allowed a new diagnostic write to be queued.");
    }

    private static async Task WaitForEventAsync(string path, string eventName, TimeSpan timeout)
    {
        await WaitUntilAsync(() => ReadLines(path).Any(line => EventName(line) == eventName), timeout);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for dispatcher watchdog event.");
    }

    private static string[] ReadLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (IOException) { return []; }
    }

    private static string? EventName(string line)
    {
        try { return JsonDocument.Parse(line).RootElement.GetProperty("eventName").GetString(); }
        catch (JsonException) { return null; }
    }

    private static int? TerminalSlot(string line)
    {
        try
        {
            using JsonDocument record = JsonDocument.Parse(line);
            return record.RootElement.GetProperty("terminalSlot").GetInt32();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}
