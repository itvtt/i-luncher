using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ApiManager;

/// <summary>
/// 직접 그린 제목 표시줄: 최소화·최대화·닫기 단추, 최대화하면 작업 표시줄을 가리지 않고 화면의 작업 영역만 채움,
/// Windows 11 에서는 둥근 모서리와 테마에 맞춘 창 테두리 색 (Windows 10 에서는 창이 그린 1px 테두리).
/// </summary>
public partial class ManagerWindow
{
    private const string MaximizeGlyph = "", RestoreGlyph = "";
    private bool _systemBorder;

    private void InitializeTitleBar()
    {
        SourceInitialized += (_, _) => ApplySystemFrame();
        StateChanged += (_, _) => ApplyWindowState();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplySystemFrame()
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        int round = DwmRound;
        bool rounded = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref round, sizeof(int)) == 0;
        if (rounded && FindResource("LineBrush") is SolidColorBrush line)
        {
            int color = line.Color.R | (line.Color.G << 8) | (line.Color.B << 16);   // COLORREF 0x00BBGGRR
            _systemBorder = DwmSetWindowAttribute(handle, DwmBorderColor, ref color, sizeof(int)) == 0;
        }
        ApplyWindowState();
    }

    private void ApplyWindowState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? RestoreGlyph : MaximizeGlyph;
        MaximizeButton.ToolTip = maximized ? "이전 크기로" : "최대화";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "이전 크기로" : "최대화");
        Frame.BorderThickness = new Thickness(maximized || _systemBorder ? 0 : 1);
    }

    // 테두리 없는 창은 최대화하면 모니터 전체(작업 표시줄 포함)를 덮으므로, 최대화 위치·크기를 그 모니터의 작업 영역으로 알려 줌.
    // 최소 크기(MinWidth/MinHeight)는 WPF 가 같은 메시지에서 따로 채우므로 handled 는 그대로 둠.
    private static IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new Point32 { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        limits.MaxSize = new Point32 { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        Marshal.StructureToPtr(limits, lParam, false);
        return IntPtr.Zero;
    }

    private const int DwmWindowCornerPreference = 33, DwmBorderColor = 34, DwmRound = 2;
    private const int WmGetMinMaxInfo = 0x0024, MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public Point32 Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect32 Monitor, Work; public int Flags; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
