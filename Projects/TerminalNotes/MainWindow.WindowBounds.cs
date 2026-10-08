using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TerminalNotes;

public partial class MainWindow
{
    private const uint MonitorDefaultToNearest = 2;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int SpiSetWorkArea = 0x002F;
    private double _configuredWindowMinWidth, _configuredWindowMinHeight;
    private Thickness _configuredWindowContentMargin;
    private HwndSource? _windowBoundsSource;
    private IntPtr _effectiveMinMonitor;
    private bool _updatingWindowBounds, _pendingWindowBoundsRevalidation, _maximizedBoundsUpdateQueued;

    private void CaptureConfiguredWindowMinimums()
    {
        _configuredWindowMinWidth = MinWidth;
        _configuredWindowMinHeight = MinHeight;
        if (Content is FrameworkElement content) _configuredWindowContentMargin = content.Margin;
    }

    private void AttachWindowBoundsHook()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        _windowBoundsSource = HwndSource.FromHwnd(handle);
        _windowBoundsSource?.AddHook(WindowBoundsHook);
        UpdateEffectiveWindowMinimums();
    }

    private void DetachWindowBoundsHook()
    {
        if (_windowBoundsSource is not null)
        {
            _windowBoundsSource.RemoveHook(WindowBoundsHook);
            _windowBoundsSource = null;
        }
    }

    private void ApplyInitialWindowBounds(double requestedWidth, double requestedHeight)
    {
        if (!TryGetWindowWorkArea(out var workArea)) return;
        UpdateEffectiveWindowMinimums(workArea);
        double minWidth = Math.Min(_configuredWindowMinWidth, workArea.Width);
        double minHeight = Math.Min(_configuredWindowMinHeight, workArea.Height);
        double width = Math.Clamp(double.IsFinite(requestedWidth) ? requestedWidth : Width,
            minWidth, Math.Max(minWidth, workArea.Width));
        double height = Math.Clamp(double.IsFinite(requestedHeight) ? requestedHeight : Height,
            minHeight, Math.Max(minHeight, workArea.Height));
        _updatingWindowBounds = true;
        try
        {
            Width = width;
            Height = height;
            Left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
            Top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
        }
        finally { _updatingWindowBounds = false; }
    }

    private void UpdateEffectiveWindowMinimums()
    {
        if (_updatingWindowBounds || _windowBoundsSource is null || !TryGetWindowWorkArea(out var workArea)) return;
        UpdateEffectiveWindowMinimums(workArea);
    }

    private void UpdateEffectiveWindowMinimums(Rect workArea)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;
        double minWidth = Math.Min(_configuredWindowMinWidth, workArea.Width);
        double minHeight = Math.Min(_configuredWindowMinHeight, workArea.Height);
        if (_effectiveMinMonitor == monitor && Math.Abs(MinWidth - minWidth) < 0.5 && Math.Abs(MinHeight - minHeight) < 0.5) return;
        _effectiveMinMonitor = monitor;
        _updatingWindowBounds = true;
        try
        {
            MinWidth = minWidth;
            MinHeight = minHeight;
        }
        finally { _updatingWindowBounds = false; }
    }

    private void OnWindowBoundsStateChanged()
    {
        if (WindowState != WindowState.Maximized && Content is FrameworkElement content)
            content.Margin = _configuredWindowContentMargin;
        if (WindowState == WindowState.Normal && _pendingWindowBoundsRevalidation)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RevalidateNormalWindowBounds));
        else if (WindowState == WindowState.Maximized)
            QueueMaximizedWindowBoundsCorrection();
    }

    private void RevalidateNormalWindowBounds()
    {
        if (WindowState != WindowState.Normal || !TryGetWindowWorkArea(out var workArea)) return;
        _pendingWindowBoundsRevalidation = false;
        UpdateEffectiveWindowMinimums(workArea);
        double minWidth = Math.Min(MinWidth, workArea.Width);
        double minHeight = Math.Min(MinHeight, workArea.Height);
        double width = Math.Clamp(Width, minWidth, Math.Max(minWidth, workArea.Width));
        double height = Math.Clamp(Height, minHeight, Math.Max(minHeight, workArea.Height));
        double left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        double top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        if (Math.Abs(width - Width) < 0.5 && Math.Abs(height - Height) < 0.5 &&
            Math.Abs(left - Left) < 0.5 && Math.Abs(top - Top) < 0.5) return;
        _updatingWindowBounds = true;
        try
        {
            Width = width;
            Height = height;
            Left = left;
            Top = top;
        }
        finally { _updatingWindowBounds = false; }
    }

    private void QueueMaximizedWindowBoundsCorrection()
    {
        if (_maximizedBoundsUpdateQueued || _windowBoundsSource is null) return;
        _maximizedBoundsUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                var handle = _windowBoundsSource?.Handle ?? IntPtr.Zero;
                if (handle == IntPtr.Zero || !IsZoomed(handle) || IsIconic(handle) ||
                    !TryGetMonitorInfo(handle, out _, out _, out var work) ||
                    !GetClientRect(handle, out var client) || Content is not FrameworkElement content) return;
                var origin = new NativePoint();
                if (!ClientToScreen(handle, ref origin)) return;
                var transform = DeviceToDipTransform(handle);
                var inset = transform.Transform(new Vector(
                    Math.Max(0, work.Left - origin.X), Math.Max(0, work.Top - origin.Y)));
                var remainingInset = transform.Transform(new Vector(
                    Math.Max(0, origin.X + client.Right - work.Right),
                    Math.Max(0, origin.Y + client.Bottom - work.Bottom)));
                // Maximized Windows frames can extend beyond rcWork. Keep the
                // application content inside it while preserving native maximize,
                // monitor movement, and the saved normal-window geometry.
                var margin = new Thickness(
                    _configuredWindowContentMargin.Left + inset.X,
                    _configuredWindowContentMargin.Top + inset.Y,
                    _configuredWindowContentMargin.Right + remainingInset.X,
                    _configuredWindowContentMargin.Bottom + remainingInset.Y);
                if (content.Margin != margin) content.Margin = margin;
            }
            finally { _maximizedBoundsUpdateQueued = false; }
        }));
    }

    private IntPtr WindowBoundsHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmWindowPosChanged && IsZoomed(hwnd) && !IsIconic(hwnd))
            QueueMaximizedWindowBoundsCorrection();
        else if (message == WmGetMinMaxInfo && TryGetMonitorInfo(hwnd, out _, out var monitorRect, out var workRect))
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            GetDpiScale(hwnd, out double scaleX, out double scaleY);
            info.MaxPosition.X = workRect.Left - monitorRect.Left;
            info.MaxPosition.Y = workRect.Top - monitorRect.Top;
            info.MaxSize.X = workRect.Right - workRect.Left;
            info.MaxSize.Y = workRect.Bottom - workRect.Top;
            info.MinTrackSize.X = Math.Max(1, (int)Math.Ceiling(MinWidth * scaleX));
            info.MinTrackSize.Y = Math.Max(1, (int)Math.Ceiling(MinHeight * scaleY));
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
        }
        else if (message is WmDisplayChange or WmDpiChanged || message == WmSettingChange && wParam.ToInt32() == SpiSetWorkArea)
        {
            _pendingWindowBoundsRevalidation = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                UpdateEffectiveWindowMinimums();
                if (WindowState == WindowState.Normal) RevalidateNormalWindowBounds();
                else if (WindowState == WindowState.Maximized) QueueMaximizedWindowBoundsCorrection();
            }));
        }
        return IntPtr.Zero;
    }

    private bool TryGetWindowWorkArea(out Rect workArea)
    {
        workArea = Rect.Empty;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !TryGetMonitorInfo(handle, out _, out _, out var workRect)) return false;
        var transform = DeviceToDipTransform(handle);
        var topLeft = transform.Transform(new Point(workRect.Left, workRect.Top));
        var bottomRight = transform.Transform(new Point(workRect.Right, workRect.Bottom));
        workArea = new Rect(topLeft, bottomRight);
        return workArea.Width > 0 && workArea.Height > 0;
    }

    private static Matrix DeviceToDipTransform(IntPtr hwnd)
    {
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) return target.TransformFromDevice;
        GetDpiScale(hwnd, out double scaleX, out double scaleY);
        return new Matrix(1 / scaleX, 0, 0, 1 / scaleY, 0, 0);
    }

    private static void GetDpiScale(IntPtr hwnd, out double scaleX, out double scaleY)
    {
        double dpiX = 96, dpiY = 96;
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) dpiX = dpiY = dpi;
        }
        catch (EntryPointNotFoundException) { }
        scaleX = scaleY = Math.Max(1, dpiX) / 96d;
    }

    private static bool TryGetMonitorInfo(IntPtr hwnd, out IntPtr monitor, out NativeRect monitorRect, out NativeRect workRect)
    {
        monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (TryGetMonitorInfo(monitor, out var info))
        {
            monitorRect = info.Monitor;
            workRect = info.Work;
            return true;
        }
        monitorRect = default;
        workRect = default;
        return false;
    }

    private static bool TryGetMonitorInfo(IntPtr monitor, out NativeMonitorInfo info)
    {
        info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }
}
