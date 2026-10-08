using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TerminalNotes.Services;

/// <summary>
/// Independent, windowless helper. The main process owns its stdin writer; EOF releases
/// the lease even when that process crashes. Only volatile SPI state is changed.
/// </summary>
public static class ScreenSaverLeaseHost
{
    public const string HostArgument = "--terminal-notes-screensaver-lease-host";
    public static bool IsHostInvocation(IReadOnlyList<string> args) => args.Count == 1 && args[0] == HostArgument;

    public static IDisposable StartLease()
    {
        string executable = Path.ChangeExtension(typeof(ScreenSaverLeaseHost).Assembly.Location, ".exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(HostArgument);
        return new ProcessLease(start);
    }

    public static int RunHost()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            string name = @"Local\TerminalNotes.ScreenSaver." + (identity.User?.Value ?? Environment.UserName) + "." + process.SessionId;
            return RunLease(Console.In, Console.Out, name, ReadEnabled, SetEnabled);
        }
        catch (Exception ex)
        {
            try { Console.Out.WriteLine("ERROR:" + ex.Message.ReplaceLineEndings(" ")); Console.Out.Flush(); }
            catch (IOException) { }
            return 1;
        }
    }

    internal static int RunLease(TextReader input, TextWriter output, string mutexName,
        Func<bool> read, Action<bool> write, TimeSpan? restoreRetryDelay = null, TimeSpan? refreshInterval = null)
    {
        // With no owning writer (e.g. a direct launch) there is no lease to acquire.
        if (input.ReadLine() != "ACQUIRE") return 0;
        using var ownership = new Mutex(false, mutexName);
        var stop = new ManualResetEvent(false);
        // Keep this reader and event alive until it completes, including acquisition failures.
        var inputClosed = Task.Run(() =>
        {
            try { input.ReadLine(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            finally { stop.Set(); }
        });
        bool owned = false;
        var state = new ScreenSaverLeaseState(read, write);
        int result = 0;
        try
        {
            try { owned = WaitHandle.WaitAny([stop, ownership]) == 1; }
            catch (AbandonedMutexException ex) when (ex.MutexIndex == 1) { owned = true; }
            if (!owned || stop.WaitOne(0)) return 0;
            state.Acquire();
            Report("ACTIVE");
            while (!stop.WaitOne(refreshInterval ?? TimeSpan.FromSeconds(30))) state.Refresh();
        }
        catch (Exception ex)
        {
            result = 1;
            Report("ERROR:" + ex.Message.ReplaceLineEndings(" "));
        }
        finally
        {
            if (owned)
            {
                // A locked/suspended desktop can reject SPI. Retain ownership and retry
                // restoration after unlock rather than abandoning a disabled screen saver.
                while (true)
                {
                    try { state.Restore(); Report("RESTORED"); break; }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                    {
                        Report("ERROR:화면보호기 복원 대기 · " + ex.Message.ReplaceLineEndings(" "));
                        Thread.Sleep(restoreRetryDelay ?? TimeSpan.FromSeconds(5));
                    }
                }
                ownership.ReleaseMutex();
            }
            // Closing stdin in the parent follows ERROR/RESTORED too. Until then do not
            // dispose the event under the reader. The process may exit with that reader.
            _ = inputClosed.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        }
        return result;

        void Report(string message)
        {
            try { output.WriteLine(message); output.Flush(); }
            catch (IOException) { /* The parent may have crashed; restoration still must run. */ }
        }
    }

    internal static bool ReadEnabled()
    {
        if (!GetParameter(0x0010, 0, out int enabled, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return enabled != 0;
    }

    private static void SetEnabled(bool enabled)
    {
        // fWinIni=0: neither persist to the user profile nor broadcast a settings change.
        if (!SetParameter(0x0011, enabled ? 1u : 0u, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetParameter(uint action, uint parameter, out int value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetParameter(uint action, uint parameter, IntPtr value, uint flags);

    private sealed class ProcessLease : IScreenSaverHostLease
    {
        private readonly Process _process;
        private int _disposed, _active, _exited;
        private string? _error;
        public bool IsActive => Volatile.Read(ref _active) != 0 && Volatile.Read(ref _disposed) == 0;
        public bool HasExited => Volatile.Read(ref _exited) != 0;
        public string? LastError => Volatile.Read(ref _error);

        public ProcessLease(ProcessStartInfo start)
        {
            _process = new Process { StartInfo = start };
            bool started = false;
            try
            {
                if (!_process.Start()) throw new InvalidOperationException("복원 프로세스를 시작하지 못했습니다.");
                started = true;
                _ = ObserveAsync();
                _process.StandardInput.WriteLine("ACQUIRE");
                _process.StandardInput.Flush();
            }
            catch
            {
                if (started) Dispose();
                else _process.Dispose();
                throw;
            }
        }

        private async Task ObserveAsync()
        {
            var stderr = _process.StandardError.ReadToEndAsync();
            try
            {
                bool receivedReply = false;
                while (true)
                {
                    var next = _process.StandardOutput.ReadLineAsync();
                    string? line = receivedReply ? await next.ConfigureAwait(false) :
                        await next.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                    if (line is null) break;
                    receivedReply = true;
                    if (line == "ACTIVE") Volatile.Write(ref _active, 1);
                    else if (line == "RESTORED")
                    {
                        Volatile.Write(ref _active, 0);
                        if (Volatile.Read(ref _disposed) != 0) Volatile.Write(ref _error, null);
                        CloseInput();
                    }
                    else if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                    {
                        Volatile.Write(ref _active, 0);
                        Volatile.Write(ref _error, "화면보호기 억제 · " + line[6..]);
                        CloseInput();
                    }
                }
                await _process.WaitForExitAsync().ConfigureAwait(false);
                string runtimeError = (await stderr.ConfigureAwait(false)).Trim().ReplaceLineEndings(" ");
                if (Volatile.Read(ref _disposed) == 0 && LastError is null)
                    Volatile.Write(ref _error, "화면보호기 억제 프로세스가 종료되었습니다." +
                        (runtimeError.Length == 0 ? "" : " · " + runtimeError[..Math.Min(runtimeError.Length, 500)]));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                Volatile.Write(ref _error, "화면보호기 복원 프로세스 연결 실패 · " + ex.Message);
                CloseInput();
                try { await _process.WaitForExitAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
            finally
            {
                Volatile.Write(ref _active, 0);
                Volatile.Write(ref _exited, 1);
                _process.Dispose();
            }
        }

        private void CloseInput()
        {
            try { _process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Volatile.Write(ref _active, 0);
            // Never kill this process: it may still owe restoration after a manual lock.
            CloseInput();
        }
    }
}

internal sealed class ScreenSaverLeaseState(Func<bool> read, Action<bool> write)
{
    private bool _restoreRequired;
    public bool IsSuppressed { get; private set; }

    public void Acquire()
    {
        if (IsSuppressed) return;
        if (read())
        {
            _restoreRequired = true;
            write(false);
            if (read()) throw new Win32Exception(5, "Windows가 화면보호기 억제를 적용하지 않았습니다.");
        }
        IsSuppressed = true;
    }

    public void Refresh()
    {
        if (!IsSuppressed || !read()) return;
        // A later settings refresh enabled the saver. Remember that state for release.
        _restoreRequired = true;
        write(false);
        if (read()) throw new Win32Exception(5, "Windows가 화면보호기 억제를 유지하지 않았습니다.");
    }

    public void Restore()
    {
        if (_restoreRequired)
        {
            if (!read()) write(true);
            if (!read()) throw new Win32Exception(5, "Windows가 화면보호기 복원을 적용하지 않았습니다.");
            _restoreRequired = false;
        }
        IsSuppressed = false;
    }
}
