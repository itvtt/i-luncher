using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// i-luncher.exe                       앱 목록
/// i-luncher.exe --uninstall NAME      런처가 설치한 앱 제거 (Windows "앱" 목록의 제거 버튼이 부름). --quiet 이면 묻지 않음
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = e.Args;
        int uninstall = Array.FindIndex(args, arg => string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase));
        if (uninstall >= 0)
        {
            Shutdown(Uninstall(uninstall + 1 < args.Length ? args[uninstall + 1] : "",
                args.Any(arg => string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase))));
            return;
        }

        // 하나만 실행: 이미 실행 중이면 (트레이에 숨어 있어도) 그 창을 앞으로
        _singleInstance = new Mutex(true, @"Local\i-luncher-main-window", out bool first);
        if (!first)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowSignalName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { ActivateExistingWindow(); }
            Shutdown();
            return;
        }

        // ILUNCHER_DATA_DIR: 설정·목록·캐시 폴더를 바꿈 (확인·시험용. 평소에는 %LOCALAPPDATA%\i-luncher)
        var settings = LauncherSettings.Load(Environment.GetEnvironmentVariable("ILUNCHER_DATA_DIR") is { Length: > 0 } data ? data : null);
        var window = new MainWindow(settings);
        MainWindow = window;
        window.EnableTray();
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, -1, executeOnlyOnce: false);
        window.Show();
    }

    private const string ShowSignalName = @"Local\i-luncher-show";
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    private static int Uninstall(string installName, bool quiet)
    {
        if (installName.Trim().Length == 0 || installName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            if (!quiet) MessageBox.Show("제거할 앱 이름이 올바르지 않습니다.", "i-luncher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 2;
        }
        if (!quiet && MessageBox.Show($"{installName}을(를) 제거할까요?\n\n설치 폴더 밖에 저장된 데이터와 설정은 그대로 남습니다.", "i-luncher",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return 1;
        try
        {
            new AppInstaller().UninstallByName(installName);
            if (!quiet) MessageBox.Show($"{installName}을(를) 제거했습니다.", "i-luncher", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!quiet) MessageBox.Show(error.Message, "i-luncher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }

    private static void ActivateExistingWindow()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
            using (process)
            {
                if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(process.MainWindowHandle, 9);  // SW_RESTORE
                SetForegroundWindow(process.MainWindowHandle);
                return;
            }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
