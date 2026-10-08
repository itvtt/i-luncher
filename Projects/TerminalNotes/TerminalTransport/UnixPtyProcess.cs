using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace TerminalNotes.TerminalTransport;

/// <summary>Unix PTY transport hosted by the bundled native forkpty helper.</summary>
internal sealed class UnixPtyProcess : ITerminalProcess
{
    private const int MaxControlPayload = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _controlWrite = new(1, 1);
    private Process? _helper;
    private Task? _readTask;
    private Task? _stderrTask;
    private Task? _waitTask;
    private Task? _closeTask;
    private Exception? _inputError;
    private StringBuilder _stderr = new();
    private bool _started;
    private bool _running;

    public event Action<byte[]>? OutputReceived;
    public event Action<TerminalProcessExit>? Exited;
    public event Action<Exception>? InputFailed;
    public bool IsRunning { get { lock (_gate) return _running; } }

    public Task StartAsync(TerminalProcessOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();

        string cwd = Path.GetFullPath(options.WorkingDirectory);
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
        string helperPath = Path.GetFullPath(options.UnixHelperPath ?? Path.Combine(AppContext.BaseDirectory, "terminalnotes-pty"));
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The native PTY helper was not found. Build/package TerminalTransport/native/terminalnotes-pty for this OS and architecture.", helperPath);

        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("A terminal process can be started only once.");
            _started = true;
            var start = new ProcessStartInfo(helperPath)
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--cwd"); start.ArgumentList.Add(cwd);
            start.ArgumentList.Add("--columns"); start.ArgumentList.Add(options.Columns.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--rows"); start.ArgumentList.Add(options.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(options.FileName);
            foreach (var arg in options.Arguments) start.ArgumentList.Add(arg);
            foreach (var (key, value) in options.Environment)
            {
                if (value is null) start.Environment.Remove(key);
                else start.Environment[key] = value;
            }
            start.Environment.TryAdd("TERM", "xterm-256color");
            start.Environment.TryAdd("COLORTERM", "truecolor");
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("The native PTY helper did not start.");
            }
            catch { process.Dispose(); throw; }
            _helper = process;
            _running = true;
            _readTask = ReadOutputAsync(process);
            _stderrTask = ReadErrorAsync(process);
            _waitTask = WatchExitAsync(process);
        }
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty) return;
        if (data.Length > MaxControlPayload) throw new ArgumentOutOfRangeException(nameof(data), "A single terminal write is limited to 16 MiB.");
        await SendControlAsync(1, data, cancellationToken).ConfigureAwait(false);
    }

    public void Resize(int columns, int rows)
    {
        ValidateSize(columns, rows);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(size, columns);
        BinaryPrimitives.WriteInt32LittleEndian(size[4..], rows);
        SendControlAsync(2, size.ToArray(), CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Task close;
        lock (_gate)
        {
            if (_closeTask is null)
            {
                _running = false;
                _closeTask = CloseCoreAsync();
            }
            close = _closeTask;
        }
        return cancellationToken.CanBeCanceled ? close.WaitAsync(cancellationToken) : close;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _controlWrite.Dispose();
    }

    private async Task SendControlAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        Process process;
        lock (_gate)
        {
            if (!_running || _helper is null) throw new InvalidOperationException("The terminal process is not running.");
            if (_inputError is { } inputError) throw new IOException("The terminal control writer has failed.", inputError);
            process = _helper;
        }
        await _controlWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] header = new byte[5];
            header[0] = type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
            await process.StandardInput.BaseStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            if (!payload.IsEmpty) await process.StandardInput.BaseStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            var failure = new IOException("The native PTY control pipe is unavailable.", ex);
            bool notify;
            lock (_gate)
            {
                notify = _inputError is null && _closeTask is null;
                if (notify) _inputError = failure;
            }
            if (notify)
                foreach (Action<Exception> handler in InputFailed?.GetInvocationList() ?? [])
                    try { handler(failure); } catch { }
            throw failure;
        }
        finally { _controlWrite.Release(); }
    }

    private async Task ReadOutputAsync(Process process)
    {
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int count = await process.StandardOutput.BaseStream.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                byte[] bytes = buffer.AsSpan(0, count).ToArray();
                foreach (Action<byte[]> handler in OutputReceived?.GetInvocationList() ?? [])
                    try { handler(bytes); } catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task ReadErrorAsync(Process process)
    {
        char[] buffer = new char[1024];
        try
        {
            while (true)
            {
                int count = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                lock (_gate)
                {
                    _stderr.Append(buffer, 0, count);
                    if (_stderr.Length > 4096) _stderr.Remove(0, _stderr.Length - 4096);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task WatchExitAsync(Process process)
    {
        int? exitCode = null;
        string? error = null;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                if (_stderrTask is not null) await _stderrTask.ConfigureAwait(false);
                lock (_gate) error = string.IsNullOrWhiteSpace(_stderr.ToString()) ? "The native PTY helper exited with an error." : _stderr.ToString().Trim();
            }
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        lock (_gate) _running = false;
        foreach (Action<TerminalProcessExit> handler in Exited?.GetInvocationList() ?? [])
            try { handler(new TerminalProcessExit(exitCode, error)); } catch { }
    }

    private async Task CloseCoreAsync()
    {
        Process? process;
        lock (_gate) process = _helper;
        if (process is null) return;
        try
        {
            await _controlWrite.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!process.HasExited)
                {
                    byte[] close = [3, 0, 0, 0, 0];
                    await process.StandardInput.BaseStream.WriteAsync(close).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
                }
                process.StandardInput.Close();
            }
            finally { _controlWrite.Release(); }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        if (_readTask is not null) try { await _readTask.ConfigureAwait(false); } catch { }
        if (_stderrTask is not null) try { await _stderrTask.ConfigureAwait(false); } catch { }
        if (_waitTask is not null) try { await _waitTask.ConfigureAwait(false); } catch { }
        lock (_gate) { _helper?.Dispose(); _helper = null; }
    }

    private static void ValidateOptions(TerminalProcessOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ValidateSize(options.Columns, options.Rows);
        ValidateArgument(options.FileName, nameof(options.FileName));
        foreach (string? arg in options.Arguments) ValidateArgument(arg, nameof(options.Arguments));
        foreach (var (key, value) in options.Environment)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0')) throw new ArgumentException("Invalid environment variable name.", nameof(options.Environment));
            if (value?.Contains('\0') == true) throw new ArgumentException("Environment values cannot contain NUL.", nameof(options.Environment));
        }
    }
    private static void ValidateArgument(string? value, string name)
    {
        if (value is null || value.Contains('\0')) throw new ArgumentException("Arguments must be non-null and cannot contain NUL.", name);
    }
    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));
    }
}
