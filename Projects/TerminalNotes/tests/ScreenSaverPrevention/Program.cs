using System.ComponentModel;
using System.Collections.Concurrent;
using System.Threading;
using TerminalNotes.Services;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (ScreenSaverLeaseHost.IsHostInvocation(args))
        {
            // Child-process protocol test: all saver state remains this process-local fake.
            bool fakeEnabled = true;
            string? testMutex = Environment.GetEnvironmentVariable("TERMINALNOTES_TEST_SCREENSAVER_MUTEX");
            if (string.IsNullOrWhiteSpace(testMutex)) return 11;
            return ScreenSaverLeaseHost.RunLease(Console.In, Console.Out, testMutex,
                () => fakeEnabled, value => fakeEnabled = value);
        }

        try
        {
            VerifyLeaseStateRestoresEnabledSaver();
            VerifyLeaseStateLeavesDisabledSaverAlone();
            VerifyLeaseStateFailedDisableIsNotOwned();
            VerifyLeaseStateFailedRestoreCanRetry();
            VerifyHostRestoresOnInputEof();
            VerifyHostSkipsAcquireWhenInputClosesBeforeMutex();
            VerifyHostRetriesFailedRestore();
            VerifyHostReportsAcquireFailure();
            VerifyProcessLeaseProtocol();
            VerifyServiceLifecycleAndResume();
            VerifyServiceFailureThrottleAndRecovery();
            VerifyServiceObservesUnexpectedHelperExit();
            Console.WriteLine("SCREEN SAVER PREVENTION TESTS PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            return 1;
        }
    }

    private static void VerifyLeaseStateRestoresEnabledSaver()
    {
        bool active = true;
        var writes = new List<bool>();
        var state = new ScreenSaverLeaseState(() => active, value => { writes.Add(value); active = value; });

        state.Acquire();
        Assert(state.IsSuppressed && !active, "an initially enabled saver is disabled and owned by the lease");
        state.Restore();
        Assert(!state.IsSuppressed && active, "release restores the initially enabled saver");
        Assert(writes.SequenceEqual(new[] { false, true }), "enabled saver receives one disable and one restore write");
    }

    private static void VerifyLeaseStateLeavesDisabledSaverAlone()
    {
        bool active = false;
        int writes = 0;
        var state = new ScreenSaverLeaseState(() => active, _ => writes++);

        state.Acquire();
        Assert(state.IsSuppressed && writes == 0, "lease is active while an initially disabled saver remains unmodified");
        state.Restore();
        Assert(writes == 0, "release does not enable a saver that was disabled before acquisition");
    }

    private static void VerifyLeaseStateFailedDisableIsNotOwned()
    {
        bool active = true;
        int writes = 0;
        var state = new ScreenSaverLeaseState(() => active, _ => { writes++; throw new Win32Exception(5); });

        Throws<Win32Exception>(state.Acquire, "failed disable is surfaced");
        Assert(!state.IsSuppressed, "failed disable never claims suppression");
        state.Restore();
        Assert(writes == 1 && active, "cleanup after failed disable does not issue a restore write");
    }

    private static void VerifyLeaseStateFailedRestoreCanRetry()
    {
        bool active = true;
        int restoreAttempts = 0;
        var state = new ScreenSaverLeaseState(() => active, value =>
        {
            if (!value)
            {
                active = false;
                return;
            }
            if (++restoreAttempts == 1) throw new Win32Exception(329, "operation in progress");
            active = true;
        });

        state.Acquire();
        Throws<Win32Exception>(state.Restore, "failed restore remains visible to the host retry loop");
        Assert(state.IsSuppressed && !active, "failed restore retains lease ownership for retry");
        state.Restore();
        Assert(!state.IsSuppressed && active && restoreAttempts == 2, "a later restore retry succeeds");
    }

    private static void VerifyHostRestoresOnInputEof()
    {
        bool active = true;
        var writes = new List<bool>();
        var input = new BlockingLeaseInput();
        var output = new SignalWriter();
        string mutexName = TestMutexName();
        Task<int> host = Task.Run(() => ScreenSaverLeaseHost.RunLease(input, output, mutexName,
            () => active, value => { writes.Add(value); active = value; },
            restoreRetryDelay: TimeSpan.FromMilliseconds(10), refreshInterval: TimeSpan.FromHours(1)));
        try
        {
            Assert(output.WaitFor("ACTIVE"), "helper announces readiness only after owning its lease");
            Assert(!active, "active host lease suppresses the initially enabled saver");
            input.SignalEof();
            Assert(host.Wait(TimeSpan.FromSeconds(5)) && host.Result == 0, "parent stdin EOF cleanly ends the helper lease");
            Assert(active && writes.SequenceEqual(new[] { false, true }), "helper restores original enabled state after EOF");
            Assert(output.Contains("RESTORED"), "helper reports restoration before exit");
        }
        finally { input.SignalEof(); }
    }

    private static void VerifyHostSkipsAcquireWhenInputClosesBeforeMutex()
    {
        string mutexName = TestMutexName();
        using var blocker = new Mutex(false, mutexName);
        Assert(blocker.WaitOne(TimeSpan.FromSeconds(1)), "test owns the helper mutex before startup");
        int reads = 0, writes = 0;
        var input = new BlockingLeaseInput();
        var output = new SignalWriter();
        Task<int> host = Task.Run(() => ScreenSaverLeaseHost.RunLease(input, output, mutexName,
            () => { reads++; return true; }, _ => writes++,
            restoreRetryDelay: TimeSpan.FromMilliseconds(10), refreshInterval: TimeSpan.FromHours(1)));
        try
        {
            Thread.Sleep(100);
            input.SignalEof();
            Assert(host.Wait(TimeSpan.FromSeconds(5)) && host.Result == 0, "EOF cancels a helper still waiting for mutex ownership");
            Assert(reads == 0 && writes == 0 && !output.Contains("ACTIVE"), "cancelled startup performs no late screensaver mutation");
        }
        finally
        {
            input.SignalEof();
            blocker.ReleaseMutex();
        }
    }

    private static void VerifyHostRetriesFailedRestore()
    {
        bool active = true;
        int restoreAttempts = 0;
        var input = new BlockingLeaseInput();
        var output = new SignalWriter();
        Task<int> host = Task.Run(() => ScreenSaverLeaseHost.RunLease(input, output, TestMutexName(),
            () => active, value =>
            {
                if (!value) { active = false; return; }
                if (Interlocked.Increment(ref restoreAttempts) == 1) throw new Win32Exception(329, "operation in progress");
                active = true;
            }, restoreRetryDelay: TimeSpan.FromMilliseconds(10), refreshInterval: TimeSpan.FromHours(1)));
        try
        {
            Assert(output.WaitFor("ACTIVE"), "helper reaches active state before restore retry test");
            input.SignalEof();
            Assert(host.Wait(TimeSpan.FromSeconds(5)) && host.Result == 0, "helper exits after the retried restore succeeds");
            Assert(active && restoreAttempts == 2, "helper retains ownership and retries a failed restore");
            Assert(output.ContainsPrefix("ERROR:") && output.Contains("RESTORED"), "restore failure and eventual recovery are observable");
        }
        finally { input.SignalEof(); }
    }

    private static void VerifyHostReportsAcquireFailure()
    {
        bool active = true;
        int writes = 0;
        var input = new BlockingLeaseInput();
        var output = new SignalWriter();
        Task<int> host = Task.Run(() => ScreenSaverLeaseHost.RunLease(input, output, TestMutexName(),
            () => active, _ => { writes++; throw new Win32Exception(5, "denied"); },
            restoreRetryDelay: TimeSpan.FromMilliseconds(10), refreshInterval: TimeSpan.FromHours(1)));
        try
        {
            Assert(host.Wait(TimeSpan.FromSeconds(5)), "failed helper acquisition exits without waiting for parent EOF");
            Assert(host.Result != 0 && output.ContainsPrefix("ERROR:"), "helper reports startup failure rather than readiness");
            Assert(!output.Contains("ACTIVE") && active && writes == 1, "failed suppression is never announced active or left changed");
        }
        finally { input.SignalEof(); }
    }

    private static void VerifyProcessLeaseProtocol()
    {
        string variable = "TERMINALNOTES_TEST_SCREENSAVER_MUTEX";
        string? previous = Environment.GetEnvironmentVariable(variable);
        string mutexName = TestMutexName();
        Environment.SetEnvironmentVariable(variable, mutexName);
        IScreenSaverHostLease? childLease = null;
        var service = new ScreenSaverPreventionService(() => childLease = (IScreenSaverHostLease)ScreenSaverLeaseHost.StartLease());
        try
        {
            service.SetRequired(true);
            Assert(WaitUntil(() => service.IsActive, TimeSpan.FromSeconds(5)), "real child process reports ACTIVE over redirected stdout");
            Assert(childLease is { HasExited: false }, "child lease remains alive while parent stdin stays open");
            service.SetRequired(false);
            Assert(WaitUntil(() => childLease?.HasExited == true, TimeSpan.FromSeconds(5)), "closing redirected stdin sends EOF and child exits");
            service.SetRequired(false);
            Assert(!service.IsActive && service.LastError is null, "normal child EOF release is not reported as a helper failure");
        }
        finally
        {
            service.Dispose();
            if (childLease is not null && !childLease.HasExited)
            {
                // Dispose sends EOF; wait for the fake host to finish and restore its private state.
                WaitUntil(() => childLease.HasExited, TimeSpan.FromSeconds(5));
            }
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static void VerifyServiceLifecycleAndResume()
    {
        int acquired = 0, released = 0;
        using var service = new ScreenSaverPreventionService(() =>
        {
            acquired++;
            return new Lease(() => released++);
        });

        service.SetRequired(false);
        Assert(acquired == 0 && !service.IsActive, "idle service does not acquire a screensaver lease");
        for (int i = 0; i < 10; i++) service.SetRequired(true);
        Assert(acquired == 1 && service.IsActive, "repeated polling holds only one screensaver lease");
        service.ResetAfterResume();
        Assert(!service.IsActive && released == 1, "resume releases the stale helper lease");
        service.SetRequired(true);
        Assert(acquired == 2 && service.IsActive, "required protection reacquires after resume");
        service.SetRequired(false);
        service.SetRequired(false);
        Assert(!service.IsActive && released == 2, "turning protection off releases exactly once");
        service.SetRequired(true);
        service.Dispose();
        service.Dispose();
        service.SetRequired(true);
        Assert(!service.IsActive && acquired == 3 && released == 3, "dispose releases once and prevents future acquisition");
    }

    private static void VerifyServiceFailureThrottleAndRecovery()
    {
        var clock = new TestClock();
        int attempts = 0;
        using var service = new ScreenSaverPreventionService(() =>
        {
            if (++attempts == 1) throw new Win32Exception(5, "helper unavailable");
            return new Lease(() => { });
        }, clock);

        service.SetRequired(true);
        Assert(!service.IsActive && service.LastError is not null, "helper startup failure is reported without claiming protection");
        service.SetRequired(true);
        Assert(attempts == 1, "failed helper startup is throttled instead of retrying on each poll");
        clock.Now = clock.Now.AddSeconds(31);
        service.SetRequired(true);
        Assert(service.IsActive && service.LastError is null && attempts == 2, "helper startup retries and clears the error after recovery");
    }

    private static void VerifyServiceObservesUnexpectedHelperExit()
    {
        var clock = new TestClock();
        int starts = 0;
        var leases = new List<FakeHostLease>();
        using var service = new ScreenSaverPreventionService(() =>
        {
            starts++;
            var lease = new FakeHostLease();
            leases.Add(lease);
            return lease;
        }, clock);

        service.SetRequired(true);
        Assert(service.IsActive && starts == 1, "service recognizes a live helper lease");
        leases[0].ExitUnexpectedly();
        service.SetRequired(true);
        Assert(!service.IsActive && service.LastError is not null && leases[0].DisposeCount == 1,
            "unexpected helper exit is surfaced and its lease is removed");
        service.SetRequired(true);
        Assert(starts == 1, "unexpected helper exit recovery is throttled");
        clock.Now = clock.Now.AddSeconds(31);
        service.SetRequired(true);
        Assert(service.IsActive && starts == 2, "service can reacquire after an unexpected helper exit backoff");
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class FakeHostLease : IScreenSaverHostLease
    {
        private bool _disposed;
        public bool IsActive => !_disposed && !HasExited;
        public bool HasExited { get; private set; }
        public string? LastError => null;
        public int DisposeCount { get; private set; }
        public void ExitUnexpectedly() => HasExited = true;
        public void Dispose() { DisposeCount++; _disposed = true; }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BlockingLeaseInput : TextReader
    {
        private readonly ManualResetEventSlim _closed = new();
        private int _firstLine;
        public override string? ReadLine()
        {
            if (Interlocked.Exchange(ref _firstLine, 1) == 0) return "ACQUIRE";
            _closed.Wait();
            return null;
        }
        public void SignalEof() => _closed.Set();
        protected override void Dispose(bool disposing) { if (disposing) _closed.Dispose(); base.Dispose(disposing); }
    }

    private sealed class SignalWriter : TextWriter
    {
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly ManualResetEventSlim _changed = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) { _lines.Enqueue(value ?? ""); _changed.Set(); }
        public bool Contains(string value) => _lines.Contains(value);
        public bool ContainsPrefix(string prefix) => _lines.Any(line => line.StartsWith(prefix, StringComparison.Ordinal));
        public bool WaitFor(string value)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (Contains(value)) return true;
                _changed.Wait(TimeSpan.FromMilliseconds(50));
            }
            return Contains(value);
        }
    }

    private static string TestMutexName() => @"Local\TerminalNotes.ScreenSaver.Tests." + Guid.NewGuid().ToString("N");
    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            Thread.Sleep(20);
        }
        return predicate();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Console.WriteLine("PASS " + message); return; }
        throw new InvalidOperationException(message + " (expected " + typeof(T).Name + ")");
    }
}
