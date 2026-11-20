using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ILauncher.Services;

/// <summary>
/// 이미 실행 중인 앱을 다시 누르면 새로 실행하지 않고 그 창을 앞으로 (최소화돼 있으면 되살림). Ctrl+클릭은 하나 더 실행.
/// 창이 없는 앱(트레이에만 있음)은 false: 부르는 쪽이 실행 파일을 다시 열어 앱이 스스로 창을 보이게 함
/// </summary>
public static class WindowFront
{
    /// <summary>설치 폴더에서 도는 앱의 창을 앞으로. 앞으로 가져온 창이 있으면 true</summary>
    public static bool ActivateApp(string directory) => Activate(AppInstaller.RunningProcessIds(directory));

    /// <summary>실행 파일(.exe) 경로로 도는 프로그램의 창을 앞으로 (내 PC 프로그램)</summary>
    public static bool ActivateProgram(string exePath)
    {
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exePath)) return false;
        var ids = new List<int>();
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            try
            {
                if (process.MainModule?.FileName is { } path && string.Equals(Path.GetFullPath(path), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                    ids.Add(process.Id);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        return Activate(ids);
    }

    public static bool Activate(IReadOnlyCollection<int> processIds)
    {
        if (processIds.Count == 0) return false;
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint id);
            if (!processIds.Contains((int)id) || !IsWindowVisible(window)) return true;
            // 보이는 창이 주인인 창(대화 상자)은 건너뜀. 작업 표시줄에 안 보이는 WPF 창은 숨은 주인이 있으므로 셈
            var owner = GetWindow(window, GwOwner);
            if (owner != IntPtr.Zero && IsWindowVisible(owner)) return true;
            if (GetWindowTextLength(window) == 0 && !IsIconic(window)) return true;  // 이름 없는 도구 창 · 숨은 창은 건너뜀
            GetWindowRect(window, out var rect);
            long area = IsIconic(window) ? 1 : (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea) { bestArea = area; best = window; }
            return true;
        }, IntPtr.Zero);
        if (best == IntPtr.Zero) return false;
        if (IsIconic(best)) ShowWindow(best, SwRestore);
        SetForegroundWindow(best);
        return true;
    }

    private const int SwRestore = 9;
    private const uint GwOwner = 4;
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
