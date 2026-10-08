using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TerminalNotes.Services;

/// <summary>One process-owned request for active work; never changes the user's power plan.</summary>
public sealed class SleepPreventionService : IDisposable
{
    private readonly Func<IDisposable> _acquire;
    private readonly TimeProvider _time;
    private IDisposable? _request;
    private DateTimeOffset _retryAt;
    private bool _disposed;
    public bool IsActive => _request is not null;
    public string? LastError { get; private set; }

    public SleepPreventionService(Func<IDisposable>? acquire = null, TimeProvider? timeProvider = null)
    {
        _acquire = acquire ?? NativeRequest.Acquire;
        _time = timeProvider ?? TimeProvider.System;
    }

    public void SetRequired(bool required)
    {
        if (_disposed) return;
        if (!required)
        {
            Release();
            LastError = null;
            _retryAt = default;
            return;
        }
        if (_request is not null || _time.GetUtcNow() < _retryAt) return;
        try
        {
            _request = _acquire();
            LastError = null;
        }
        catch (Win32Exception ex)
        {
            LastError = "자동 절전 방지 요청 실패 · " + ex.Message;
            _retryAt = _time.GetUtcNow().AddSeconds(30);
        }
    }

    // Windows can clear requests during a user-initiated sleep. Recreate on resume.
    public void ResetAfterResume()
    {
        if (_disposed) return;
        Release();
        _retryAt = default;
    }

    private void Release()
    {
        var request = _request;
        _request = null;
        request?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }

    private sealed class NativeRequest(SafeFileHandle handle) : IDisposable
    {
        private const int DisplayRequired = 0, SystemRequired = 1, ExecutionRequired = 3;

        public static IDisposable Acquire()
        {
            var context = new ReasonContext { Version = 0, Flags = 1,
                SimpleReasonString = "Terminal Notes · 진행 중인 태스크를 완료하기 위해 자동 절전을 방지합니다." };
            var handle = PowerCreateRequest(ref context);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            var setRequests = new List<int>(3);
            try
            {
                Set(DisplayRequired);
                Set(SystemRequired);
                Set(ExecutionRequired);
                return new NativeRequest(handle);

                void Set(int type)
                {
                    if (!PowerSetRequest(handle, type)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    setRequests.Add(type);
                }
            }
            catch
            {
                foreach (int type in setRequests) PowerClearRequest(handle, type);
                handle.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (handle.IsClosed) return;
            // Closing the handle also removes requests if Windows already cleared their counts.
            int[] requestTypes = [ExecutionRequired, SystemRequired, DisplayRequired];
            try { foreach (int type in requestTypes) PowerClearRequest(handle, type); }
            finally { handle.Dispose(); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ReasonContext
        {
            public uint Version, Flags;
            [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
            // Reserve the full native union (Detailed uses three pointer-sized slots on x64).
            public IntPtr Reserved1, Reserved2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PowerSetRequest(SafeFileHandle request, int type);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PowerClearRequest(SafeFileHandle request, int type);
    }
}
