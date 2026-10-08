using System.ComponentModel;
using System.IO;

namespace TerminalNotes.Services;

/// <summary>Owns one temporary screen-saver lease without waiting on the UI thread.</summary>
public sealed class ScreenSaverPreventionService : IDisposable
{
    private readonly Func<IDisposable> _acquire;
    private readonly TimeProvider _time;
    private IDisposable? _request;
    private bool _stopping, _disposed;
    private DateTimeOffset _retryAt;
    private string? _lastError;

    public bool IsActive => !_stopping && (_request is IScreenSaverHostLease host ? host.IsActive : _request is not null);
    public string? LastError => (_request as IScreenSaverHostLease)?.LastError ?? _lastError;

    public ScreenSaverPreventionService(Func<IDisposable>? acquire = null, TimeProvider? timeProvider = null)
    {
        _acquire = acquire ?? ScreenSaverLeaseHost.StartLease;
        _time = timeProvider ?? TimeProvider.System;
    }

    public void SetRequired(bool required)
    {
        if (_disposed) return;
        if (_request is IScreenSaverHostLease { HasExited: true } host)
        {
            bool unexpected = !_stopping;
            _lastError = host.LastError;
            _request.Dispose();
            _request = null;
            _stopping = false;
            if (unexpected)
            {
                _lastError ??= "화면보호기 억제 프로세스가 종료되었습니다.";
                _retryAt = _time.GetUtcNow().AddSeconds(30);
            }
        }
        if (!required)
        {
            Release();
            _retryAt = default;
            // Preserve a helper's restoration errors until restoration finishes.
            if (_request is null) _lastError = null;
            return;
        }
        if (_request is not null || _time.GetUtcNow() < _retryAt) return;
        try
        {
            _request = _acquire();
            _lastError = null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _lastError = "화면보호기 억제 요청 실패 · " + ex.Message;
            _retryAt = _time.GetUtcNow().AddSeconds(30);
        }
    }

    public void ResetAfterResume()
    {
        if (_disposed) return;
        Release();
        _retryAt = default;
    }

    private void Release()
    {
        if (_request is null || _stopping) return;
        _stopping = true;
        _request.Dispose();
        if (_request is not IScreenSaverHostLease)
        {
            _request = null;
            _stopping = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}

internal interface IScreenSaverHostLease : IDisposable
{
    bool IsActive { get; }
    bool HasExited { get; }
    string? LastError { get; }
}
