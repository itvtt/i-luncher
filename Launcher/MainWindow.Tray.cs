using System.ComponentModel;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ILauncher;

/// <summary>
/// 트레이 아이콘: 창을 닫으면(X) 트레이로 숨고 계속 실행 (목록·업데이트 확인을 이어 감).
/// 트레이 아이콘을 두 번 누르면 큰 창(홈 화면). 한 번 누름에는 아무것도 뜨지 않음.
/// 오른쪽 클릭 메뉴의 [종료]로 끝냄. 다시 실행해도 큰 창을 엶 (App 의 신호).
/// </summary>
public partial class MainWindow
{
    private Forms.NotifyIcon? _tray;
    private bool _exiting;

    /// <summary>App 이 부름 (화면 확인용으로 띄운 창에는 트레이를 만들지 않음)</summary>
    public void EnableTray()
    {
        if (_tray is not null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApp());
        _tray = new Forms.NotifyIcon { Text = "i-luncher", ContextMenuStrip = menu, Visible = true, Icon = LoadTrayIcon() };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
        // Windows 를 끌 때는 그냥 끝냄
        Application.Current.SessionEnding += (_, _) => _exiting = true;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ILauncher.ico"));
        if (resource is null) return System.Drawing.SystemIcons.Application;
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    /// <summary>트레이에서 (또는 다시 실행했을 때) 큰 창을 앞으로</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ───────────────────────── 끝내기 ─────────────────────────

    /// <summary>정말 끝냄 (트레이 메뉴의 종료, 런처 자체 업데이트)</summary>
    public void ExitApp()
    {
        _exiting = true;
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 트레이가 있으면 X 는 숨기기: 목록과 업데이트 확인은 계속
        if (_tray is not null && !_exiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>창이 정말 닫힐 때 (OnClosed): 트레이 아이콘이 남지 않게</summary>
    private void DisposeTray()
    {
        if (_tray is null) return;
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Icon?.Dispose();
        _tray.Dispose();
        _tray = null;
    }
}
