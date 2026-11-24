using System.Windows;

namespace IApi;

/// <summary>
/// i-api.exe 를 손으로 실행하면 (더블클릭 · 바로 가기 · --setup) 뜨는 관리 창 (Manager.ManagerView). 이 exe 자신이 서버:
/// 포트를 정해 [실행] (설정 파일 · 로그인 자동 실행 · 방화벽 · 시작), 다시 시작 · 멈추기 · 업데이트, API 마다 켜고 끄기 · 호출 기록,
/// 서버 앱 목록 · 릴리스 · 러너 · 공지 · 문제 보고 · 스킬. 포트가 정해져 있고 서버가 꺼져 있으면 열 때 켬. 창은 하나만 (다시 실행하면 떠 있는 창을 앞으로)
/// </summary>
internal sealed partial class ManagerWindow : Window
{
    public ManagerWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + Program.Version;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        // 테두리 없는 창을 최대화하면 화면 밖으로 넘치는 만큼 안쪽으로 (작업 표시줄은 그대로 보임)
        BorderThickness = maximized ? new Thickness(7) : new Thickness(0);
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>창을 띄우고 닫힐 때까지 (WPF 는 STA 스레드). 이미 떠 있으면 그 창을 앞으로 하고 끝냄</summary>
    public static int Run()
    {
        string signalName = Setup.WindowEventName(Program.ExecutablePath);
        if (EventWaitHandle.TryOpenExisting(signalName, out var existing))
        {
            using (existing) existing.Set();
            return 0;
        }
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
        int code = 0;
        bool reopen = false;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new ManagerWindow();
            // 업데이트한 뒤: 이 창을 닫고 (창은 하나만) 새 i-api.exe 의 창을 엶
            Manager.ManagerHost.RestartWindow = () => { reopen = true; window.Close(); };
            using var closed = new CancellationTokenSource();
            window.Closed += (_, _) => closed.Cancel();
            var watcher = new Thread(() =>
            {
                try
                {
                    while (WaitHandle.WaitAny([signal, closed.Token.WaitHandle]) == 0)
                        window.Dispatcher.BeginInvoke(window.BringToFront);
                }
                catch (ObjectDisposedException) { }  // 창을 닫는 중
            }) { IsBackground = true };
            watcher.Start();
            code = app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        signal.Dispose();
        if (reopen) Manager.Updater.OpenNewWindow();
        return code;
    }
}
