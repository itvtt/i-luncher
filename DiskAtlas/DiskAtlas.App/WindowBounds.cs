using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DiskAtlas.App;

/// <summary>
/// Keeps a custom-chrome window inside the work area of the monitor that owns it.
/// All values handled by WM_GETMINMAXINFO are physical pixels.
/// </summary>
internal sealed class WindowBounds : IDisposable
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private readonly Window _window;
    private double _normalMinWidth;
    private double _normalMinHeight;
    private HwndSource? _source;
    private bool _minimumsAdjusted;
    private bool _updatingMinimums;
    private bool _disposed;

    private WindowBounds(Window window)
    {
        _window = window;
        _window.SourceInitialized += OnSourceInitialized;
        _window.StateChanged += OnStateChanged;
        _window.Closed += OnClosed;

        if (PresentationSource.FromVisual(window) is HwndSource source)
            Attach(source);
    }

    public static WindowBounds Attach(Window window) => new(window);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _window.SourceInitialized -= OnSourceInitialized;
        _window.StateChanged -= OnStateChanged;
        _window.Closed -= OnClosed;
        if (_source is not null)
        {
            _source.RemoveHook(WindowProc);
            _source = null;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(_window) is HwndSource source)
        {
            Attach(source);
            if (_window.WindowState == WindowState.Maximized) OnStateChanged(sender, e);
        }
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState == WindowState.Maximized && _source is not null)
        {
            var monitor = MonitorFromWindow(_source.Handle, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
                ApplyEffectiveMinimums(monitorInfo.WorkArea);
        }
        else
        {
            RestoreConfiguredMinimums();
        }
    }

    private void Attach(HwndSource source)
    {
        if (_disposed || ReferenceEquals(_source, source)) return;
        if (_source is not null) _source.RemoveHook(WindowProc);
        _source = source;
        _source.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

        var workWidth = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        var workHeight = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        if (workWidth <= 0 || workHeight <= 0) return IntPtr.Zero;

        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        bounds.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        bounds.MaxSize.X = workWidth;
        bounds.MaxSize.Y = workHeight;

        if (_window.WindowState == WindowState.Maximized)
            ApplyEffectiveMinimums(monitorInfo.WorkArea);

        // WPF minimum sizes are DIPs, while this message uses physical pixels.
        // Cap the minimum on compact/high-DPI monitors so maximization can still
        // fit the work area instead of extending beneath the taskbar.
        var dpi = VisualTreeHelper.GetDpi(_window);
        var requestedMinWidth = ToPhysicalPixels(_window.MinWidth, dpi.DpiScaleX);
        var requestedMinHeight = ToPhysicalPixels(_window.MinHeight, dpi.DpiScaleY);
        bounds.MinTrackSize.X = Math.Min(workWidth, Math.Max(bounds.MinTrackSize.X, requestedMinWidth));
        bounds.MinTrackSize.Y = Math.Min(workHeight, Math.Max(bounds.MinTrackSize.Y, requestedMinHeight));

        Marshal.StructureToPtr(bounds, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void ApplyEffectiveMinimums(Rect workArea)
    {
        if (_updatingMinimums) return;
        if (!_minimumsAdjusted)
        {
            _normalMinWidth = _window.MinWidth;
            _normalMinHeight = _window.MinHeight;
            _minimumsAdjusted = true;
        }

        var dpi = VisualTreeHelper.GetDpi(_window);
        var availableWidth = (workArea.Right - workArea.Left) / dpi.DpiScaleX;
        var availableHeight = (workArea.Bottom - workArea.Top) / dpi.DpiScaleY;

        _updatingMinimums = true;
        try
        {
            _window.MinWidth = Math.Min(_normalMinWidth, availableWidth);
            _window.MinHeight = Math.Min(_normalMinHeight, availableHeight);
        }
        finally
        {
            _updatingMinimums = false;
        }
    }

    private void RestoreConfiguredMinimums()
    {
        if (_updatingMinimums || !_minimumsAdjusted) return;
        _updatingMinimums = true;
        try
        {
            _window.MinWidth = _normalMinWidth;
            _window.MinHeight = _normalMinHeight;
            _minimumsAdjusted = false;
        }
        finally
        {
            _updatingMinimums = false;
        }
    }

    private static int ToPhysicalPixels(double value, double scale)
    {
        if (double.IsNaN(value) || value <= 0) return 0;
        return (int)Math.Min(int.MaxValue, Math.Ceiling(value * scale));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect MonitorArea;
        public Rect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
}
