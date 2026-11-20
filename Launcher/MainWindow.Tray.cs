using System.ComponentModel;
using System.Windows;
using ILauncher.Services;
using Forms = System.Windows.Forms;

namespace ILauncher;

/// <summary>
/// 트레이 아이콘: 창을 닫으면(X) 트레이로 숨고 계속 실행 (목록·업데이트 확인을 이어 감).
/// 트레이 아이콘을 한 번 누르면 작은 트레이 창(자주 쓰는 앱 · 업데이트), 두 번 누르면 큰 창(홈 화면).
/// 오른쪽 클릭 메뉴의 [종료]로 끝냄. 다시 실행해도 큰 창을 엶 (App 의 신호).
/// </summary>
public partial class MainWindow
{
    private Forms.NotifyIcon? _tray;
    private TrayPopup? _quick;
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
        // 한 번 = 트레이 창 (열려 있으면 닫음). 두 번 = 큰 창: 첫 번째 누름에 뜬 트레이 창은 두 번째 누름에 닫히고 큰 창이 열림
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleQuick(); };
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

    private void ToggleQuick()
    {
        _quick ??= new TrayPopup(this);
        if (_quick.IsVisible)
        {
            _quick.Hide();
            return;
        }
        if (_quick.JustClosed) return;  // 이 누름으로 트레이 창 바깥을 눌러 방금 닫힘: 다시 열지 않음
        _quick.ShowNearTray();
        _ = ScanRunningAsync();
    }

    /// <summary>트레이에서 (또는 다시 실행했을 때) 큰 창을 앞으로</summary>
    public void ShowFromTray()
    {
        _quick?.Hide();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ───────────────────────── 트레이 창이 쓰는 것 ─────────────────────────

    /// <summary>
    /// 트레이 창의 칸: 검색하면 찾은 앱 · 프로그램, 아니면 즐겨찾기(등록한 순서) → 나머지 설치된 앱 · 프로그램을 많이 연 순
    /// (같으면 앱 목록 순서). max 개까지
    /// </summary>
    internal List<object> QuickItems(string search, int max)
    {
        var programs = ProgramCards();
        if (search.Length > 0)
            return [.. Sorted(_cards.Where(card => card.Matches(search))).Cast<object>().Concat(programs.Where(card => card.Matches(search))).Take(max)];
        var items = FavoriteItems();
        int Uses(object item) => item switch
        {
            AppCard card => _usage.Count(UsageStore.AppKey(card.App.Id)),
            FavoriteCard program => _usage.Count(UsageStore.ProgramKey(program.Program.Id)),
            _ => 0
        };
        var others = Sorted(_cards.Where(card => card.IsInstalled)).Cast<object>().Concat(programs).Where(item => !items.Contains(item)).ToList();
        items.AddRange(others.OrderByDescending(Uses));  // 안정 정렬: 연 횟수가 같으면 앞의 순서 그대로
        return [.. items.Take(max)];
    }

    /// <summary>트레이 창의 업데이트 줄</summary>
    internal List<AppCard> QuickUpdates() => Sorted(_cards.Where(card => card is { UpdateAvailable: true, IsBusy: false }));

    /// <summary>
    /// 트레이 창에서 누름: 설치된 앱 · 프로그램은 바로 실행 (트레이 창은 이미 닫힘).
    /// 받지 않은 앱은 큰 창의 앱 페이지, 실행하지 못했을 때는 큰 창을 열어 그곳에서 (오류 알림)
    /// </summary>
    internal async Task QuickOpenAsync(object item)
    {
        if (item is FavoriteCard program)
        {
            if (!RunFavorite(program)) ShowFromTray();
            return;
        }
        if (item is not AppCard card) return;
        if (card.IsInstalled) card.Installed = await Task.Run(() => _manager.Installer.Detect(card.App));
        if (card.IsInstalled)
        {
            if (!await BringToFrontAsync(card) && !Launch(card)) ShowFromTray();
            return;
        }
        // 받지 않은 앱: 큰 창의 스토어 탭에서 받기 (진행은 그 줄에)
        ShowFromTray();
        ShowTab("store");
        await OpenAsync(card);
    }

    /// <summary>트레이 창의 업데이트: 큰 창을 열고 그 앱을 업데이트 (실행 중이면 물어봄, 진행은 칸에)</summary>
    internal async Task QuickUpdateAsync(AppCard card)
    {
        ShowFromTray();
        await UpdateCardAsync(card);
    }

    /// <summary>트레이 창의 설정</summary>
    internal void ShowSettingsFromTray()
    {
        ShowFromTray();
        SettingsButton_Click(this, new RoutedEventArgs());
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

    /// <summary>창이 정말 닫힐 때 (OnClosed): 트레이 아이콘과 트레이 창이 남지 않게</summary>
    private void DisposeTray()
    {
        _quick?.CloseForGood();
        _quick = null;
        if (_tray is null) return;
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Icon?.Dispose();
        _tray.Dispose();
        _tray = null;
    }
}
