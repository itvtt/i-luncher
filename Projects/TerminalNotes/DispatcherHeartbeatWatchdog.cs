using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace TerminalNotes;

/// <summary>
/// Monitors WPF dispatcher responsiveness from a thread-pool timer so a blocked
/// dispatcher can still be recorded to disk.
/// </summary>
internal sealed class DispatcherHeartbeatWatchdog : IDisposable
{
    private static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromSeconds(15);
    private readonly Dispatcher _dispatcher;
    private readonly string _logPath;
    private readonly Action<TimeSpan>? _recovered;
    private readonly Action<string, string>? _appendDiagnostic;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _stallThreshold;
    private readonly Timer _timer;
    private readonly object _contextLock = new();
    private readonly object _writeLock = new();
    private long _lastHeartbeatTimestamp;
    private long _maxStallDurationTicks;
    private int _heartbeatPending;
    private int _checkInProgress;
    private int _stalled;
    private int _disposed;
    private int? _terminalSlot;
    private uint? _sessionProcessId;
    private string? _projectName;
    private string? _terminalStage;
    private string? _lastTerminalFailureKey;
    private DateTimeOffset _lastTerminalFailureAt;

    internal DispatcherHeartbeatWatchdog(Dispatcher dispatcher, string logPath, Action<TimeSpan>? recovered = null,
        TimeSpan? checkInterval = null, TimeSpan? stallThreshold = null, Action<string, string>? appendDiagnostic = null)
    {
        _dispatcher = dispatcher;
        _logPath = logPath;
        _recovered = recovered;
        _appendDiagnostic = appendDiagnostic;
        _checkInterval = checkInterval ?? DefaultCheckInterval;
        _stallThreshold = stallThreshold ?? DefaultStallThreshold;
        _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
        _timer = new Timer(CheckDispatcher, null, _checkInterval, _checkInterval);
        QueueHeartbeat();
    }

    public void UpdateTerminalContext(int slot, string? projectName, string? stage, uint? processId, bool newSession = false)
    {
        lock (_contextLock)
        {
            if (newSession || _terminalSlot != slot || !string.Equals(_projectName, projectName, StringComparison.Ordinal))
                _sessionProcessId = processId;
            else if (processId is not null)
                _sessionProcessId = processId;
            _terminalSlot = slot;
            _projectName = projectName;
            _terminalStage = stage;
        }
    }

    public void RecordTerminalFailure(int slot, string? projectName, uint? processId, string stage, string operation, string exceptionType)
    {
        if (!IsSafeCode(operation) || !IsSafeCode(exceptionType) || !IsSafeCode(stage) || Volatile.Read(ref _disposed) != 0) return;
        var context = new TerminalContext(slot, processId, SafeProjectName(projectName), stage);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_contextLock)
        {
            string key = operation + "\n" + exceptionType + "\n" + slot + "\n" + context.Project;
            if (string.Equals(_lastTerminalFailureKey, key, StringComparison.Ordinal) &&
                now - _lastTerminalFailureAt < TimeSpan.FromMinutes(5))
                return;
            _lastTerminalFailureKey = key;
            _lastTerminalFailureAt = now;
        }

        ThreadPool.QueueUserWorkItem(_ => WriteDiagnostic("terminal_lifecycle_fault", TimeSpan.Zero, operation, exceptionType, context));
    }

    private void CheckDispatcher(object? state)
    {
        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0) return;
        try { CheckDispatcherCore(); }
        catch (Exception ex)
        {
            Trace.WriteLine("UI heartbeat check failed: " + ex.GetType().Name);
        }
        finally { Volatile.Write(ref _checkInProgress, 0); }
    }

    private void CheckDispatcherCore()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        long lastHeartbeat = Volatile.Read(ref _lastHeartbeatTimestamp);
        var delay = Stopwatch.GetElapsedTime(lastHeartbeat);
        if (delay >= _stallThreshold)
        {
            if (Interlocked.CompareExchange(ref _stalled, 1, 0) == 0)
            {
                WriteDiagnostic("dispatcher_unresponsive", delay);
            }
            Interlocked.Exchange(ref _maxStallDurationTicks, delay.Ticks);
        }
        else if (Interlocked.CompareExchange(ref _stalled, 0, 1) == 1)
        {
            long stalledTicks = Interlocked.Exchange(ref _maxStallDurationTicks, 0);
            var duration = TimeSpan.FromTicks(Math.Max(stalledTicks, delay.Ticks));
            WriteDiagnostic("dispatcher_recovered", duration);
            try
            {
                if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Volatile.Read(ref _disposed) == 0) _recovered?.Invoke(duration);
                    }), DispatcherPriority.Background);
            }
            catch (InvalidOperationException) { }
        }

        QueueHeartbeat();
    }

    private void QueueHeartbeat()
    {
        if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished ||
            Interlocked.CompareExchange(ref _heartbeatPending, 1, 0) != 0)
            return;

        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                    Volatile.Write(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                Volatile.Write(ref _heartbeatPending, 0);
            }), DispatcherPriority.Normal);
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref _heartbeatPending, 0);
        }
    }

    private void WriteDiagnostic(string eventName, TimeSpan duration)
    {
        TerminalContext context;
        lock (_contextLock) context = CaptureTerminalContextLocked();
        WriteDiagnostic(eventName, duration, null, null, context);
    }

    private TerminalContext CaptureTerminalContextLocked() =>
        new(_terminalSlot, _sessionProcessId, _projectName, _terminalStage);

    private void WriteDiagnostic(string eventName, TimeSpan duration, string? operation, string? exceptionType, TerminalContext context)
    {
        var record = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            eventName,
            durationSeconds = Math.Round(duration.TotalSeconds, 1),
            terminalSlot = context.Slot,
            processId = context.ProcessId,
            project = context.Project,
            terminalStage = context.Stage,
            operation,
            exceptionType
        };

        try
        {
            lock (_writeLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                string? directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string jsonLine = JsonSerializer.Serialize(record) + Environment.NewLine;
                if (_appendDiagnostic is not null)
                    _appendDiagnostic(_logPath, jsonLine);
                else
                    File.AppendAllText(_logPath, jsonLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.WriteLine("UI responsiveness diagnostic could not be written: " + ex.GetType().Name);
        }
    }

    private static bool IsSafeCode(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '+' or '`');

    private static string? SafeProjectName(string? value) => value is { Length: > 0 and <= 255 } &&
        value.All(character => !char.IsControl(character)) ? value : null;

    private sealed record TerminalContext(int? Slot, uint? ProcessId, string? Project, string? Stage);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
    }
}
