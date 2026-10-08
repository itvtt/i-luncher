using System.Diagnostics;

namespace TerminalNotes.Services;

/// <summary>Runs blocking native resize and close work on session-owned threads.</summary>
internal sealed class ConPtyLifecycleWorker
{
    private readonly object _gate = new();
    private Task? _worker;
    private (short Columns, short Rows)? _pendingResize;
    private Action<short, short>? _resize;
    private Action? _close;
    private TaskCompletionSource? _closed;
    private bool _closing;

    public event Action<ConPtyLifecycleEvent>? LifecycleChanged;

    public void RequestResize(short columns, short rows, Action<short, short> resize)
    {
        lock (_gate)
        {
            if (_closing) return;
            _pendingResize = (columns, rows);
            _resize = resize;
            EnsureWorkerLocked();
            Monitor.PulseAll(_gate);
        }
    }

    public Task CloseAsync(Action close)
    {
        lock (_gate)
        {
            if (_closed is not null) return _closed.Task;
            _closing = true;
            _close = close;
            _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Native resize can block indefinitely. Closing the owned job and
            // pseudoconsole must have an independent thread so stop remains usable.
            _pendingResize = null;
            _resize = null;
            _ = Task.Factory.StartNew(
                RunClose,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Monitor.PulseAll(_gate);
            return _closed.Task;
        }
    }

    private void EnsureWorkerLocked()
    {
        _worker ??= Task.Factory.StartNew(
            Run,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void Run()
    {
        while (true)
        {
            (short Columns, short Rows)? resize = null;
            Action<short, short>? resizeAction = null;
            lock (_gate)
            {
                while (!_closing && _pendingResize is null) Monitor.Wait(_gate);
                if (_pendingResize is { } pending && _resize is { } pendingAction)
                {
                    resize = pending;
                    _pendingResize = null;
                    resizeAction = pendingAction;
                }
                else if (_closing) return;
            }

            if (resize is not { } requested || resizeAction is null) continue;
            RaiseLifecycle("resize", "started", null);
            try
            {
                resizeAction(requested.Columns, requested.Rows);
                RaiseLifecycle("resize", "completed", null);
            }
            catch (Exception exception)
            {
                RaiseLifecycle("resize", "faulted", exception.GetType().FullName);
            }
        }
    }

    private void RunClose()
    {
        Action? close;
        TaskCompletionSource? closed;
        lock (_gate)
        {
            close = _close;
            _close = null;
            closed = _closed;
        }

        RaiseLifecycle("close", "started", null);
        try
        {
            close?.Invoke();
            RaiseLifecycle("close", "completed", null);
            closed?.TrySetResult();
        }
        catch (Exception exception)
        {
            RaiseLifecycle("close", "faulted", exception.GetType().FullName);
            closed?.TrySetException(exception);
        }
    }

    private void RaiseLifecycle(string operation, string phase, string? exceptionType)
    {
        var diagnostic = new ConPtyLifecycleEvent(DateTimeOffset.UtcNow, operation, phase, exceptionType);
        foreach (Action<ConPtyLifecycleEvent> handler in LifecycleChanged?.GetInvocationList() ?? [])
        {
            try { handler(diagnostic); }
            catch (Exception exception) { Trace.WriteLine($"ConPTY lifecycle observer failed: {exception.GetType().FullName}"); }
        }
    }
}
