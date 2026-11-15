using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ApiManager;

/// <summary>
/// 관리자 메뉴 (i-luncher 오른쪽 화면, MASTER_USERS 계정만). 서버(relay.py)를 배포·시작·중지하고, 예전 브라우저 관리 페이지의 일을 함:
/// i-luncher 앱 목록·런처 공지, Terminal Notes 공지, 문제 보고, 스킬. 관리자 비밀번호는 서버 설정에서 읽어 자동으로 씀.
/// 런처가 한 번 만들어 두고 다시 씀 (다른 화면으로 가도 고치던 내용이 남음). 보이는 동안만 서버 상태를 확인함.
/// </summary>
public partial class ManagerView : UserControl
{
    private static readonly Brush Good = Brush("#FF3DD68C"), Warn = Brush("#FFE6C07A"), Muted = Brush("#FFA1A1A6"), Text1 = Brush("#FFF5F5F7");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private ServerAddress _address = ServerManager.ReadAddress(ServerManager.DeployedScript);
    private AdminClient? _admin;
    private string? _python;
    private bool _busy, _healthy;
    private readonly HashSet<string> _loaded = [];

    // 앱 목록
    private readonly List<AppRow> _apps = [];
    private string _catalogRevision = "";
    private bool _appsDirty, _fillingAppForm;
    // 런처 공지 · 공지
    private string _launcherNoticesRevision = "", _launcherNoticesSaved = "";
    private string _announcementRevision = "", _announcementSaved = "";
    private bool _announcementExists;
    // 문제 보고
    private List<ReportRow> _reports = [];
    private JsonObject _statuses = new() { ["new"] = "새 보고", ["checking"] = "확인 중", ["resolved"] = "해결" };
    private ReportRow? _report;
    // 스킬
    private string? _skill;
    // 아래 알림 줄: 결과는 잠깐 보였다 사라지고, 오류는 누를 때까지 남음
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private string _versionText = "";

    private bool _started;
    private Window? _host;

    public ManagerView()
    {
        InitializeComponent();
        ApplyStatusLabels();
        _timer.Tick += async (_, _) => await RefreshHealthAsync();
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); MessageBar.Visibility = Visibility.Collapsed; };
        Loaded += async (_, _) =>
        {
            if (Window.GetWindow(this) is { } host && host != _host)
            {
                _host = host;
                host.Closing += Host_Closing;
                host.Closed += (_, _) => { _timer.Stop(); _messageTimer.Stop(); _releaseTimer?.Stop(); _runnerTimer?.Stop(); _admin?.Dispose(); };
            }
            if (_started) return;
            _started = true;
            await InitializeAsync();
        };
        // 다른 화면으로 가면 서버 확인을 멈추고, 돌아오면 바로 다시 확인
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) { _timer.Stop(); _releaseTimer?.Stop(); _runnerTimer?.Stop(); return; }
            if (_admin is null) return;
            _timer.Start();
            await RefreshHealthAsync();
            if (CurrentSection is "Server" or "Releases" or "Runners") await LoadSectionAsync(CurrentSection, force: true);
        };
        PreviewKeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.F5) { e.Handled = true; await RefreshCurrentAsync(); } };
    }

    /// <summary>알림 창의 주인: 런처 창</summary>
    private Window OwnerWindow => Window.GetWindow(this) ?? Application.Current.MainWindow;

    private static SolidColorBrush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;

    private void SetMessage(string text, bool warning = false)
    {
        _messageTimer.Stop();
        MessageText.Text = text;
        MessageText.Foreground = warning ? Warn : Muted;
        MessageBar.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length > 0 && !warning) _messageTimer.Start();
    }

    private void MessageBar_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => SetMessage("");

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RestartButton.IsEnabled = StopButton.IsEnabled = !busy && _python is not null;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    // ── 서버 ────────────────────────────────────────────────────────────────────────────────────────

    private async Task InitializeAsync()
    {
        UpdateVersions();
        _python = ServerManager.FindPython();
        if (_python is null)
        {
            SetBusy(false);
            SetMessage("Python 3.8 이상이 필요합니다 (https://www.python.org/downloads/). 설치한 뒤 다시 여세요.", warning: true);
            await RefreshHealthAsync();
            return;
        }
        SetBusy(true);
        try
        {
            string? deployed = await ServerManager.DeployIfNewerAsync(_python);
            _address = ServerManager.ReadAddress(ServerManager.DeployedScript);
            UpdateVersions();
            if (deployed is not null || !await ServerManager.IsHealthyAsync(_address))
            {
                SetMessage((deployed ?? "서버가 응답하지 않아 시작합니다.") + " 시작하는 중…");
                var (exitCode, output) = await ServerManager.RestartAsync(_python);
                SetMessage((deployed is null ? "" : deployed + " ") + (exitCode == 0 ? "서버를 시작했습니다." : "서버가 시작되지 않았습니다: " + LastLine(output)),
                    warning: exitCode != 0);
            }
            else SetMessage("");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or Win32Exception)
        {
            SetMessage(error.Message, warning: true);
        }
        finally { SetBusy(false); }
        ConnectAdmin();
        await RefreshHealthAsync();
        if (IsVisible) _timer.Start();
        await LoadSectionAsync(CurrentSection, force: true);
    }

    private void ConnectAdmin()
    {
        _admin?.Dispose();
        _address = ServerManager.ReadAddress(ServerManager.DeployedScript);
        _admin = new AdminClient(_address.ApiBase, ServerManager.ReadAdminPassword(ServerManager.DeployedScript));
    }

    private void UpdateVersions()
    {
        string app = ManagerHost.LauncherVersion;
        string bundled = ServerManager.ReadVersion(ServerManager.BundledScript)?.ToString() ?? "없음";
        string deployed = ServerManager.ReadVersion(ServerManager.DeployedScript)?.ToString() ?? "설치 안 됨";
        _versionText = $"i-luncher v{app} · 서버 {deployed}" + (bundled != deployed ? $" (포함된 서버 {bundled})" : "");
        VersionsText.Text = _versionText;
    }

    private async Task RefreshHealthAsync()
    {
        bool healthy = await ServerManager.IsHealthyAsync(_address);
        bool changed = healthy != _healthy;
        _healthy = healthy;
        StatusDot.Fill = healthy ? Good : Warn;
        StatusText.Text = healthy ? "실행 중" : _busy ? "확인 중…" : "멈춤";
        AddressText.Text = _address.AdminUrl.TrimEnd('/').Replace("http://", "");
        if (changed && healthy) await LoadSectionAsync(CurrentSection, force: true);
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_python is null || _busy) return;
        SetBusy(true);
        SetMessage("다시 시작하는 중…");
        try
        {
            string? deployed = await ServerManager.DeployIfNewerAsync(_python);
            var (exitCode, output) = await ServerManager.RestartAsync(_python);
            SetMessage((deployed is null ? "" : deployed + " ") + (exitCode == 0 ? "다시 시작했습니다." : "시작되지 않았습니다: " + LastLine(output)), exitCode != 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or Win32Exception)
        {
            SetMessage(error.Message, warning: true);
        }
        finally { SetBusy(false); UpdateVersions(); }
        ConnectAdmin();
        _loaded.Clear();
        await RefreshHealthAsync();
        await LoadSectionAsync(CurrentSection, force: true);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_python is null || _busy) return;
        if (MessageBox.Show(OwnerWindow, "API 서버를 멈출까요?\n\nTerminal Notes의 공지·스킬·문제 보고와 i-luncher 앱 목록·업데이트가 서버를 다시 켤 때까지 동작하지 않습니다.",
                "관리자 메뉴", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            var (_, output) = await ServerManager.HaltAsync(_python);
            SetMessage(LastLine(output) + " · 로그인하면 다시 시작합니다 (시작 폴더의 Terminal Notes Relay.vbs).");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or Win32Exception)
        {
            SetMessage(error.Message, warning: true);
        }
        finally { SetBusy(false); }
        await RefreshHealthAsync();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = MoreButton.ContextMenu;
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SettingsFileButton_Click(object sender, RoutedEventArgs e) => OpenWith("notepad.exe", ServerManager.DeployedScript);
    private void LogButton_Click(object sender, RoutedEventArgs e) => OpenWith("notepad.exe", _address.LogFile);
    private void DataFolderButton_Click(object sender, RoutedEventArgs e) => OpenWith("explorer.exe", _address.DataFolder);

    private void BrowserButton_Click(object sender, RoutedEventArgs e)
    {
        string password = ServerManager.ReadAdminPassword(ServerManager.DeployedScript);
        try
        {
            if (password.Length > 0) Clipboard.SetText(password);
            Process.Start(new ProcessStartInfo(_address.AdminUrl) { UseShellExecute = true })?.Dispose();
            SetMessage(password.Length > 0
                ? "브라우저가 로그인을 물으면 아이디는 아무거나(예: admin), 비밀번호는 붙여넣기(Ctrl+V) 하세요. 비밀번호를 클립보드에 복사했습니다."
                : "관리 페이지를 열었습니다 (비밀번호 없음).");
        }
        catch (Exception error) when (error is Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            SetMessage(error.Message, warning: true);
        }
    }

    private void OpenWith(string program, string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            SetMessage("없습니다: " + path, warning: true);
            return;
        }
        try { Process.Start(new ProcessStartInfo(program, $"\"{path}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception error) { SetMessage(error.Message, warning: true); }
    }

    private async Task LoadStatusAsync()
    {
        var status = (await Admin.GetAsync("admin/api/status")).AsObject();
        var counts = status["counts"]!.AsObject();
        CountReports.Text = Number(counts["reports"]);
        CountNew.Text = Number(counts["newReports"]);
        CountSkills.Text = Number(counts["skills"]);
        var clients = status["clients"]!.AsArray().Select(node => node!.AsObject()).Select(entry => new ClientRow(
            Str(entry["address"]), Str(entry["version"]) is { Length: > 0 } version ? version : "—", LocalTime(entry["last"]),
            Str(entry["announcement"]) switch { "latest" => "최신", "older" => "이전 공지", _ => "—" },
            string.Join(" · ", entry["features"]!.AsArray().Select(feature => Str(feature))))).ToList();
        ClientsList.ItemsSource = clients;
        NoClientsText.Visibility = clients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 방화벽 규칙이 없을 때만 위에 한 줄로 알림 (나머지 설정은 [설정 보기] 안에)
        string firewall = Str(status["firewall"]);
        FirewallText.Text = firewall;
        FirewallText.Visibility = firewall.Contains("규칙 없음", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        SettingsList.ItemsSource = status["settings"]!.AsArray().Select(line => Str(line)).Append(firewall).Select(SettingRow.From).ToList();
        int uptime = (int?)status["uptimeSeconds"] ?? 0;
        string running = uptime >= 86400 ? $"{uptime / 86400}일 {uptime % 86400 / 3600}시간" : uptime >= 3600 ? $"{uptime / 3600}시간 {uptime % 3600 / 60}분" : $"{uptime / 60}분";
        VersionsText.Text = $"{_versionText} · 실행 {running}";
        string size = (((double?)counts["reportsBytes"] ?? 0) / 1048576).ToString("0.0", CultureInfo.InvariantCulture);
        ServerFootText.Text = $"Python {Str(status["python"])} · 보고서 {size} MB · GitHub 중계로 여는 저장소: 앱 목록 {status["githubRepositories"]!.AsArray().Count}개" +
            (status["githubProxyRepositories"]!.AsArray().Count > 0 ? $", LAUNCHER_GITHUB_REPOS {status["githubProxyRepositories"]!.AsArray().Count}개" : "");
    }

    // ── 화면 전환·불러오기 ─────────────────────────────────────────────────────────────────────────────

    private string CurrentSection => (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "Server";

    private AdminClient Admin => _admin ?? throw new InvalidOperationException("서버에 아직 연결하지 않았습니다.");

    private async void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkillsSection is null) return;  // 화면을 만드는 중 (처음 고른 탭)
        string section = CurrentSection;
        foreach (var (name, panel) in new (string, FrameworkElement)[] { ("Server", ServerSection), ("Apps", AppsSection), ("Releases", ReleasesSection),
                     ("Runners", RunnersSection), ("Notices", NoticesSection), ("Reports", ReportsSection), ("Skills", SkillsSection) })
            panel.Visibility = name == section ? Visibility.Visible : Visibility.Collapsed;
        if (!IsLoaded) return;
        if (section != "Runners") _runnerTimer?.Stop();
        await LoadSectionAsync(section, force: section is "Server" or "Releases" or "Runners");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshCurrentAsync();

    /// <summary>머리 줄의 새로 고침 (F5): 보고 있는 화면을 서버에서 다시 읽음. 저장하지 않은 편집이 있으면 먼저 물음</summary>
    private async Task RefreshCurrentAsync()
    {
        string section = CurrentSection;
        bool dirty = section switch
        {
            "Apps" => _appsDirty,
            "Runners" => RunnerEnvDirty,
            "Notices" => NoticeDirty(launcher: NoticeKindLauncher.IsChecked == true),
            _ => false,
        };
        if (dirty && MessageBox.Show(OwnerWindow, "저장하지 않은 변경을 버리고 서버에서 다시 읽을까요?", "관리자 메뉴",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (dirty && section == "Runners") _runnerEnvSaved = RunnerEnvBox.Text.Replace("\r\n", "\n"); // 버린 편집은 서버 내용으로 다시 채움
        await RefreshHealthAsync();
        if (section == "Notices")
        {
            await Run(NoticeKindLauncher.IsChecked == true ? LoadLauncherNoticesAsync : LoadAnnouncementAsync);
            return;
        }
        await LoadSectionAsync(section, force: true);
    }

    /// <summary>처음 열 때 한 번 불러옴 (편집 중인 내용을 덮지 않게). 서버 상태는 열 때마다 새로</summary>
    private async Task LoadSectionAsync(string section, bool force)
    {
        if (_admin is null || (!force && _loaded.Contains(section))) return;
        await Run(async () =>
        {
            switch (section)
            {
                case "Server": await LoadStatusAsync(); break;
                case "Apps": await LoadAppsAsync(); break;
                case "Releases": await LoadReleasesAsync(); break;
                case "Runners": await LoadRunnersAsync(); break;
                case "Notices": await LoadAnnouncementAsync(); await LoadLauncherNoticesAsync(); break;
                case "Reports": await LoadReportsAsync(); break;
                case "Skills": await LoadSkillsAsync(); break;
            }
            _loaded.Add(section);
        }, quiet: section == "Server" && !_healthy);
    }

    /// <summary>서버 요청 하나: 실패하면 아래 줄에 서버가 준 이유를 보여 줌</summary>
    private async Task<bool> Run(Func<Task> action, string? done = null, bool quiet = false)
    {
        try
        {
            await action();
            if (done is not null) SetMessage(done);
            return true;
        }
        catch (Exception error) when (error is AdminException or HttpRequestException or TaskCanceledException or InvalidOperationException or IOException
                                          or NullReferenceException or FormatException or System.Text.Json.JsonException)
        {
            if (!quiet)
                SetMessage(error is HttpRequestException or TaskCanceledException ? "서버에 연결하지 못했습니다: " + error.Message : error.Message, warning: true);
            return false;
        }
    }

    /// <summary>런처를 정말 끝낼 때 (트레이로 숨길 때는 아님): 저장하지 않은 편집이 있으면 물음</summary>
    private void Host_Closing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel) return;
        var unsaved = new List<string>();
        if (_appsDirty) unsaved.Add("앱 목록");
        if (RunnerEnvDirty) unsaved.Add("러너 .env");
        if (_loaded.Contains("Notices") && NoticeDirty(launcher: false)) unsaved.Add("Terminal Notes 공지");
        if (_loaded.Contains("Notices") && NoticeDirty(launcher: true)) unsaved.Add("i-luncher 공지");
        if (unsaved.Count > 0 && MessageBox.Show(OwnerWindow, $"저장하지 않은 변경이 있습니다: {string.Join(", ", unsaved)}\n\n저장하지 않고 닫을까요?", "관리자 메뉴",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    // ── 앱 목록 (i-luncher) ─────────────────────────────────────────────────────────────────────────

    private async Task LoadAppsAsync()
    {
        var catalog = (await Admin.GetAsync("launcher/catalog")).AsObject();
        _catalogRevision = Str(catalog["revision"]);
        _apps.Clear();
        foreach (var node in catalog["apps"]!.AsArray())
            if (node is JsonObject app) _apps.Add(new AppRow((JsonObject)app.DeepClone()));
        // 런처와 같은 순서 (order, 같으면 원래 순서)
        var ordered = _apps.Select((app, index) => (app, index)).OrderBy(item => (int?)item.app.Data["order"] ?? int.MaxValue).ThenBy(item => item.index).Select(item => item.app).ToList();
        _apps.Clear();
        _apps.AddRange(ordered);
        RefreshAppsList(select: TakePendingApp() ?? _apps.FirstOrDefault());
        SetAppsDirty(false);
    }

    private string? _pendingAppId;

    /// <summary>런처 카드의 "관리자 메뉴에서 편집": 앱 목록을 열고 그 앱을 고름 (아직 안 읽었으면 읽은 뒤에)</summary>
    public void ShowApp(string appId)
    {
        _pendingAppId = appId;
        if (_loaded.Contains("Apps") && TakePendingApp() is { } row)
        {
            AppsList.SelectedItem = row;
            AppsList.ScrollIntoView(row);
        }
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().First(item => item.Tag as string == "Apps");
    }

    private AppRow? TakePendingApp()
    {
        if (_pendingAppId is not { } id) return null;
        _pendingAppId = null;
        return _apps.FirstOrDefault(app => string.Equals(Str(app.Data["id"]), id, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshAppsList(AppRow? select)
    {
        AppsList.ItemsSource = null;
        AppsList.ItemsSource = _apps;
        AppsList.SelectedItem = select;
        if (select is null) FillAppForm(null);
    }

    // 저장할 것이 있을 때만 [저장]이 켜짐
    private void SetAppsDirty(bool dirty)
    {
        _appsDirty = dirty;
        SaveAppsButton.IsEnabled = dirty;
    }

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => FillAppForm(AppsList.SelectedItem as AppRow);

    private void FillAppForm(AppRow? app)
    {
        _fillingAppForm = true;
        AppForm.IsEnabled = app is not null;
        RemoveAppButton.IsEnabled = MoveUpButton.IsEnabled = MoveDownButton.IsEnabled = app is not null;
        AppNameBox.Text = app is null ? "" : Str(app.Data["name"]);
        AppRepositoryBox.Text = app is null ? "" : Str(app.Data["repository"]);
        AppIdBox.Text = app is null ? "" : Str(app.Data["id"]);
        AppCategoryBox.Text = app is null ? "" : Str(app.Data["category"]);
        RenderCategoryChips();
        AppDescriptionBox.Text = app is null ? "" : Str(app.Data["description"]);
        AppDetailsBox.Text = app is null ? "" : Str(app.Data["details"]).Replace("\n", "\r\n");
        ShowScreenshots(app);
        AppInstallNameBox.Text = app is null ? "" : Str(app.Data["installName"]);
        bool setup = app is not null && Str(app.Data["kind"]).Equals("setup", StringComparison.OrdinalIgnoreCase);
        AppKindSetup.IsChecked = setup;
        AppKindZip.IsChecked = !setup;
        AppMasterOnlyBox.IsChecked = app is not null && (bool?)app.Data["masterOnly"] == true;
        AppBetaBox.IsChecked = app is not null && Flag(app.Data, "beta");
        AppNewBox.IsChecked = app is not null && Flag(app.Data, "new");
        // 이 창에 칸이 없는 값(icon·executable 등)은 보이지 않아도 저장할 때 그대로 남음
        _fillingAppForm = false;
    }

    private void AppField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_fillingAppForm || AppsList.SelectedItem is not AppRow app) return;
        Put(app.Data, "name", AppNameBox.Text.Trim(), keepEmpty: true);
        Put(app.Data, "repository", AppRepositoryBox.Text.Trim(), keepEmpty: true);
        Put(app.Data, "id", AppIdBox.Text.Trim(), keepEmpty: true);
        Put(app.Data, "category", AppCategoryBox.Text.Trim());
        Put(app.Data, "description", AppDescriptionBox.Text.Trim());
        Put(app.Data, "details", AppDetailsBox.Text.Replace("\r\n", "\n").Trim());
        Put(app.Data, "installName", AppInstallNameBox.Text.Trim());
        app.Changed();
        SetAppsDirty(true);
        if (sender == AppCategoryBox) RenderCategoryChips();
    }

    private static readonly Brush ChipOn = Brush("#FF3864D9"), ChipOnEdge = Brush("#FF6C91FA");

    /// <summary>분류 칸 아래: 다른 앱들이 쓰는 분류 (누르면 그 분류로, 지금 분류는 파랑)</summary>
    private void RenderCategoryChips()
    {
        CategoryChips.Children.Clear();
        if (AppsList.SelectedItem is not AppRow selected) return;
        string current = AppCategoryBox.Text.Trim();
        var categories = _apps.Where(app => app != selected).Select(app => Str(app.Data["category"]).Trim()).Where(category => category.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase);
        foreach (string category in categories)
        {
            var chip = new Button { Content = category, Style = (Style)FindResource("ChipButtonStyle") };
            if (string.Equals(category, current, StringComparison.CurrentCultureIgnoreCase))
            {
                chip.Background = ChipOn;
                chip.BorderBrush = ChipOnEdge;
                chip.Foreground = Text1;
            }
            chip.Click += (_, _) =>
            {
                AppCategoryBox.Text = category;
                AppCategoryBox.CaretIndex = category.Length;
            };
            CategoryChips.Children.Add(chip);
        }
    }

    private void AppKind_Checked(object sender, RoutedEventArgs e)
    {
        if (_fillingAppForm || AppsList.SelectedItem is not AppRow app) return;
        Put(app.Data, "kind", AppKindSetup.IsChecked == true ? "setup" : "");
        app.Changed();
        SetAppsDirty(true);
    }

    private void AppMasterOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_fillingAppForm || AppsList.SelectedItem is not AppRow app) return;
        if (AppMasterOnlyBox.IsChecked == true) app.Data["masterOnly"] = true;
        else app.Data.Remove("masterOnly");
        app.Changed();
        SetAppsDirty(true);
    }

    // i-luncher 카드의 '베타'·'NEW' 배지 (앱 목록 JSON 의 beta·new, 꺼지면 값을 빼서 저장)
    private void AppBadge_Changed(object sender, RoutedEventArgs e)
    {
        if (_fillingAppForm || AppsList.SelectedItem is not AppRow app) return;
        SetFlag(app.Data, "beta", AppBetaBox.IsChecked == true);
        SetFlag(app.Data, "new", AppNewBox.IsChecked == true);
        app.Changed();
        SetAppsDirty(true);
    }

    internal static bool Flag(JsonObject data, string key) =>
        data[key] is JsonValue value && value.TryGetValue<bool>(out bool on) && on;

    private static void SetFlag(JsonObject data, string key, bool on)
    {
        if (on) data[key] = true;
        else data.Remove(key);
    }

    private static void Put(JsonObject data, string key, string value, bool keepEmpty = false)
    {
        if (value.Length == 0 && !keepEmpty) data.Remove(key);
        else data[key] = value;
    }

    private void AddAppButton_Click(object sender, RoutedEventArgs e)
    {
        // 새로 추가한 앱은 NEW 배지가 켜진 채로 시작
        var app = new AppRow(new JsonObject { ["id"] = "", ["name"] = "새 앱", ["repository"] = "https://github.com/", ["new"] = true });
        _apps.Add(app);
        RefreshAppsList(select: app);
        SetAppsDirty(true);
        AppNameBox.Focus();
        AppNameBox.SelectAll();
    }

    private void RemoveAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow app) return;
        if (MessageBox.Show(OwnerWindow, $"{app.Title}을(를) 앱 목록에서 뺄까요?\n\n저장해야 런처에 반영됩니다. 이미 설치한 PC의 앱은 지우지 않습니다.", "앱 목록",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        int index = _apps.IndexOf(app);
        _apps.Remove(app);
        RefreshAppsList(select: _apps.Count == 0 ? null : _apps[Math.Min(index, _apps.Count - 1)]);
        SetAppsDirty(true);
    }

    private void MoveUpButton_Click(object sender, RoutedEventArgs e) => MoveApp(-1);
    private void MoveDownButton_Click(object sender, RoutedEventArgs e) => MoveApp(1);

    private void MoveApp(int step)
    {
        if (AppsList.SelectedItem is not AppRow app) return;
        int index = _apps.IndexOf(app), target = index + step;
        if (target < 0 || target >= _apps.Count) return;
        _apps.RemoveAt(index);
        _apps.Insert(target, app);
        RefreshAppsList(select: app);
        SetAppsDirty(true);
    }

    private async void SaveAppsButton_Click(object sender, RoutedEventArgs e)
    {
        // 목록 순서가 런처의 순서: 10, 20, 30 …
        var apps = new JsonArray();
        for (int index = 0; index < _apps.Count; index++)
        {
            var data = (JsonObject)_apps[index].Data.DeepClone();
            data["order"] = (index + 1) * 10;
            apps.Add(data);
        }
        AppRow? selected = AppsList.SelectedItem as AppRow;
        string? selectedId = selected is null ? null : Str(selected.Data["id"]);
        await Run(async () =>
        {
            var saved = (await Admin.SendAsync(HttpMethod.Put, "launcher/catalog", new JsonObject { ["apps"] = apps, ["revision"] = _catalogRevision })).AsObject();
            _catalogRevision = Str(saved["revision"]);
            await LoadAppsAsync();
            ManagerHost.RaiseCatalogSaved();
            if (selectedId is not null) AppsList.SelectedItem = _apps.FirstOrDefault(app => Str(app.Data["id"]) == selectedId) ?? AppsList.SelectedItem;
        }, done: "앱 목록을 저장했습니다.");
    }

    // ── 공지: Terminal Notes 공지와 i-luncher(런처) 공지를 한 화면에서, 위의 고르기로 바꿔 봄 ─────────────────────

    private bool ShowingLauncherNotices => NoticeKindLauncher.IsChecked == true;
    private string _announcementInfo = "";
    private List<string> _announcementHistory = [];

    private bool NoticeDirty(bool launcher) => launcher
        ? LauncherNoticesBox.Text != _launcherNoticesSaved
        : AnnouncementBox.Text.Replace("\r\n", "\n") != _announcementSaved;

    private void NoticeKind_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool launcher = ShowingLauncherNotices;
        AnnouncementBox.Visibility = launcher ? Visibility.Collapsed : Visibility.Visible;
        LauncherNoticesBox.Visibility = launcher ? Visibility.Visible : Visibility.Collapsed;
        HistoryButton.Visibility = launcher ? Visibility.Collapsed : Visibility.Visible;
        UpdateNoticeState();
    }

    /// <summary>[저장]은 보고 있는 공지에 바뀐 것이 있을 때만 켜짐</summary>
    private void UpdateNoticeState()
    {
        bool launcher = ShowingLauncherNotices;
        SaveNoticeButton.IsEnabled = NoticeDirty(launcher);
        NoticeInfoText.Text = launcher ? "i-luncher 위쪽에 보입니다." : _announcementInfo;
        HistoryButton.IsEnabled = _announcementHistory.Count > 0;
    }

    private void LauncherNoticesBox_TextChanged(object sender, TextChangedEventArgs? e) => UpdateNoticeState();
    private void AnnouncementBox_TextChanged(object sender, TextChangedEventArgs? e) => UpdateNoticeState();

    private async Task LoadLauncherNoticesAsync()
    {
        var notices = (await Admin.GetAsync("launcher/notices")).AsObject();
        _launcherNoticesRevision = Str(notices["revision"]);
        _launcherNoticesSaved = Str(notices["text"]).Replace("\n", "\r\n");
        LauncherNoticesBox.Text = _launcherNoticesSaved;
        UpdateNoticeState();
    }

    private async Task LoadAnnouncementAsync() => ShowAnnouncement((await Admin.GetAsync("admin/api/announcements")).AsObject());

    private void ShowAnnouncement(JsonObject board)
    {
        _announcementRevision = Str(board["revision"]);
        _announcementExists = (bool?)board["exists"] ?? false;
        _announcementSaved = Str(board["text"]);
        AnnouncementBox.Text = _announcementSaved.Replace("\n", "\r\n");
        string editors = string.Join(", ", board["editors"]?.AsArray().Select(editor => Str(editor)) ?? []);
        _announcementInfo = "Terminal Notes 공지 버튼에 보입니다 · 고칠 수 있는 계정 " + (editors.Length > 0 ? editors : "앱에서 정한 계정") +
            (_announcementExists ? $" · 마지막 변경 {LocalTime(board["updatedAt"])} {Str(board["updatedBy"])}" : "");
        _announcementHistory = board["history"]?.AsArray().Select(version => Str(version)).ToList() ?? [];
        UpdateNoticeState();
    }

    private async void SaveNoticeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShowingLauncherNotices)
            await Run(async () =>
            {
                await Admin.SendAsync(HttpMethod.Put, "launcher/notices", new JsonObject { ["text"] = LauncherNoticesBox.Text.Replace("\r\n", "\n"), ["revision"] = _launcherNoticesRevision });
                await LoadLauncherNoticesAsync();
            }, done: "i-luncher 공지를 저장했습니다.");
        else
            await Run(async () =>
            {
                var board = await Admin.SendAsync(HttpMethod.Put, "admin/api/announcements", new JsonObject
                {
                    ["text"] = AnnouncementBox.Text.Replace("\r\n", "\n"), ["revision"] = _announcementRevision, ["exists"] = _announcementExists
                });
                ShowAnnouncement(board.AsObject());
            }, done: "공지를 저장했습니다.");
    }

    /// <summary>[이전 공지]: 고른 공지를 편집 칸에 불러옴 (저장해야 바뀜)</summary>
    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        HistoryMenu.Items.Clear();
        foreach (string version in _announcementHistory)
        {
            var item = new MenuItem { Header = version };
            item.Click += async (_, _) => await Run(async () =>
            {
                var old = await Admin.GetAsync("admin/api/announcements/history/" + Uri.EscapeDataString(version));
                AnnouncementBox.Text = Str(old["text"]).Replace("\n", "\r\n");
            }, done: "이전 공지를 불러왔습니다. 저장해야 바뀝니다.");
            HistoryMenu.Items.Add(item);
        }
        HistoryMenu.PlacementTarget = HistoryButton;
        HistoryMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        HistoryMenu.IsOpen = HistoryMenu.Items.Count > 0;
    }

    // ── 문제 보고 ───────────────────────────────────────────────────────────────────────────────────

    private void ApplyStatusLabels()
    {
        string Label(string key) => Str(_statuses[key]);
        ReportStatusNew.Content = Label("new");
        ReportStatusChecking.Content = Label("checking");
        ReportStatusResolved.Content = Label("resolved");
        int Count(string? key) => key is null ? _reports.Count : _reports.Count(report => report.Status == key);
        ReportFilterAll.Content = $"전체 {Count(null)}";
        ReportFilterNew.Content = $"{Label("new")} {Count("new")}";
        ReportFilterChecking.Content = $"{Label("checking")} {Count("checking")}";
        ReportFilterResolved.Content = $"{Label("resolved")} {Count("resolved")}";
        int fresh = Count("new");
        ReportsNavItem.Content = fresh > 0 ? $"문제 보고 ({fresh})" : "문제 보고";
    }

    private async Task LoadReportsAsync()
    {
        var listing = (await Admin.GetAsync("admin/api/reports")).AsObject();
        if (listing["statuses"] is JsonObject statuses) _statuses = (JsonObject)statuses.DeepClone();
        _reports = listing["reports"]!.AsArray().Select(node => node!.AsObject()).Select(item => new ReportRow(
            Str(item["id"]), LocalTime(item["receivedAt"]), Str(item["appVersion"]), Str(item["summary"]), Str(item["status"]), Str(item["note"]),
            (bool?)item["hasLogs"] ?? false, Str(item["remoteAddress"]), Str(_statuses[Str(item["status"])]),
            Str(item["status"]) switch { "new" => Warn, "checking" => Text1, _ => Muted })).ToList();
        ApplyStatusLabels();
        string? selected = _report?.Id;
        ShowReports();
        // 고르던 보고서를 다시 고르고, 없으면 맨 위 보고서 (오른쪽이 비어 있지 않게)
        var shown = (IEnumerable<ReportRow>?)ReportsList.ItemsSource;
        ReportsList.SelectedItem = shown?.FirstOrDefault(report => report.Id == selected) ?? shown?.FirstOrDefault();
        await LoadReportsAnalysisAsync();
    }

    /// <summary>제목 아래 한 줄: 최근 7일 보고를 서버가 모아 분석한 결과 (자동·직접, 많은 문제, 버전). 자세한 내용은 툴팁</summary>
    private async Task LoadReportsAnalysisAsync()
    {
        try
        {
            var analysis = (await Admin.GetAsync("admin/api/reports/analysis?days=7")).AsObject();
            static string Top(JsonNode? list, int count) => string.Join(", ", (list as JsonArray ?? []).OfType<JsonObject>().Take(count)
                .Select(item => $"{Str(item["name"])} {Number(item["count"])}"));
            int total = (int?)analysis["total"] ?? 0;
            string problems = Top(analysis["problems"], 3), versions = Top(analysis["versions"], 2);
            ReportsAnalysisText.Text = total == 0 ? "최근 7일 보고 없음"
                : $"최근 7일 {total}건 · 자동 {Number(analysis["automatic"])} · 새 보고 {Number(analysis["new"])}" +
                  (problems.Length > 0 ? " · 많은 문제: " + problems : "") + (versions.Length > 0 ? " · 버전: " + versions : "");
            ReportsAnalysisText.ToolTip = total == 0 ? null : string.Join("\n", new[]
            {
                "문제 이벤트: " + Top(analysis["events"], 10),
                "PC: " + Top(analysis["addresses"], 6),
                "날짜별: " + string.Join(", ", (analysis["perDay"] as JsonArray ?? []).OfType<JsonObject>().Select(day => $"{Str(day["day"])[5..]} {Number(day["count"])}")),
            }.Where(line => !line.EndsWith(": ", StringComparison.Ordinal)));
            ReportsAnalysisText.Visibility = Visibility.Visible;
        }
        catch (AdminException error) when (error.Status == System.Net.HttpStatusCode.NotFound)
        {
            ReportsAnalysisText.Visibility = Visibility.Collapsed; // 분석을 모르는 예전 서버
        }
    }

    private void ShowReports()
    {
        string? status = ReportFilterNew.IsChecked == true ? "new" : ReportFilterChecking.IsChecked == true ? "checking" : ReportFilterResolved.IsChecked == true ? "resolved" : null;
        string text = ReportSearchBox.Text.Trim();
        ReportsList.ItemsSource = _reports.Where(report => (status is null || report.Status == status) &&
            (text.Length == 0 || (report.Summary + " " + report.Version + " " + report.Note).Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void ReportFilter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) ShowReports(); }
    private void ReportSearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) ShowReports(); }

    private async void ReportsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _report = ReportsList.SelectedItem as ReportRow;
        ReportDetail.IsEnabled = _report is not null;
        if (_report is null)
        {
            ReportHeaderText.Text = "왼쪽에서 보고서를 고르세요.";
            ReportTextBox.Text = ReportNoteBox.Text = "";
            return;
        }
        var report = _report;
        ReportHeaderText.Text = $"{report.Time} · {(report.Version.Length > 0 ? report.Version : "버전 미상")}" +
            (report.Remote.Length > 0 ? " · " + report.Remote : "") + (report.HasLogs ? " · 로그 있음" : "");
        ReportStatusNew.IsChecked = report.Status == "new";
        ReportStatusChecking.IsChecked = report.Status == "checking";
        ReportStatusResolved.IsChecked = report.Status == "resolved";
        ReportNoteBox.Text = report.Note;
        SaveReportLogsButton.IsEnabled = report.HasLogs;
        ReportTextBox.Text = "불러오는 중…";
        await Run(async () =>
        {
            var detail = await Admin.GetAsync("admin/api/reports/" + Uri.EscapeDataString(report.Id));
            if (ReferenceEquals(_report, report)) ReportTextBox.Text = Str(detail["text"]);
        });
    }

    private async void SaveReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_report is not { } report) return;
        string status = ReportStatusChecking.IsChecked == true ? "checking" : ReportStatusResolved.IsChecked == true ? "resolved" : "new";
        await Run(async () =>
        {
            await Admin.SendAsync(HttpMethod.Put, "admin/api/reports/" + Uri.EscapeDataString(report.Id), new JsonObject { ["status"] = status, ["note"] = ReportNoteBox.Text });
            await LoadReportsAsync();
        }, done: "상태와 메모를 저장했습니다.");
    }

    private async void SaveReportLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_report is not { HasLogs: true } report) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"TerminalNotes-{report.Id}.zip", Filter = "ZIP (*.zip)|*.zip", Title = "로그 저장" };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        await Run(async () =>
        {
            byte[] data = await Admin.GetBytesAsync("admin/api/reports/" + Uri.EscapeDataString(report.Id) + "/logs.zip");
            await File.WriteAllBytesAsync(dialog.FileName, data);
        }, done: "로그를 저장했습니다: " + dialog.FileName);
    }

    private async void DeleteReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_report is not { } report) return;
        if (MessageBox.Show(OwnerWindow, $"이 보고서를 삭제할까요?\n\n{report.Summary}\n\n되돌릴 수 없습니다.", "문제 보고",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            await Admin.SendAsync(HttpMethod.Delete, "admin/api/reports/" + Uri.EscapeDataString(report.Id), null);
            _report = null;
            await LoadReportsAsync();
        }, done: "보고서를 삭제했습니다.");
    }

    // ── 스킬 ────────────────────────────────────────────────────────────────────────────────────────

    private async Task LoadSkillsAsync()
    {
        var skills = (await Admin.GetAsync("admin/api/skills")).AsArray().Select(node => node!.AsObject()).Select(skill => new SkillRow(
            Str(skill["name"]), Str(skill["title"]) is { Length: > 0 } title ? title : Str(skill["name"]),
            $"{Str(skill["name"])} · {Str(skill["author"])} · v{Number(skill["version"])} · {LocalTime(skill["updatedAt"])} · 받은 수 {Number(skill["downloads"])}" +
            (((bool?)skill["private"]) == true ? " · 비공개" : ""))).ToList();
        string? selected = _skill;
        SkillsList.ItemsSource = skills;
        SkillsList.SelectedItem = skills.FirstOrDefault(skill => skill.Name == selected) ?? skills.FirstOrDefault();
        if (SkillsList.SelectedItem is null) ShowSkill(null);
    }

    private async void SkillsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadSkillAsync((SkillsList.SelectedItem as SkillRow)?.Name);

    private void ShowSkill(string? name)
    {
        _skill = name;
        SkillDetail.IsEnabled = name is not null;
        if (name is null)
        {
            SkillHeaderText.Text = "왼쪽에서 스킬을 고르세요.";
            SkillContentBox.Text = "";
            SkillVersionsList.ItemsSource = null;
        }
    }

    private async Task LoadSkillAsync(string? name)
    {
        ShowSkill(name);
        if (name is null) return;
        await Run(async () =>
        {
            var detail = (await Admin.GetAsync("admin/api/skills/" + Uri.EscapeDataString(name))).AsObject();
            if (_skill != name) return;
            var skill = detail["skill"]!.AsObject();
            SkillHeaderText.Text = $"{Str(skill["title"])} · 작성자 {Str(skill["author"])} · 최신 v{Number(skill["latestVersion"])} · 받은 수 {Number(skill["downloads"])}";
            SkillContentBox.Text = Str(skill["content"]).Replace("\n", "\r\n");
            SkillVersionsList.ItemsSource = detail["versions"]!.AsArray().Select(node => node!.AsObject()).Select(version => new VersionRow(
                (int?)version["version"] ?? 0,
                $"v{Number(version["version"])} · {LocalTime(version["publishedAt"])} · {Str(version["author"])}" +
                (Str(version["note"]) is { Length: > 0 } note ? " · " + note : ""))).ToList();
        });
    }

    private async void ShowSkillVersionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skill is not { } name || SkillVersionsList.SelectedItem is not VersionRow version) { SetMessage("버전 기록에서 버전을 고르세요."); return; }
        await Run(async () =>
        {
            var content = await Admin.GetAsync($"admin/api/skills/{Uri.EscapeDataString(name)}/versions/{version.Version}");
            SkillContentBox.Text = Str(content["content"]).Replace("\n", "\r\n");
        }, done: $"v{version.Version} 내용을 보여 줍니다.");
    }

    private async void RestoreSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skill is not { } name || SkillVersionsList.SelectedItem is not VersionRow version) { SetMessage("되돌릴 버전을 버전 기록에서 고르세요."); return; }
        if (MessageBox.Show(OwnerWindow, $"v{version.Version}의 내용을 새 최신 버전으로 올릴까요?\n\n이 스킬을 쓰는 PC에는 업데이트로 보입니다.", "스킬",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            var result = await Admin.SendAsync(HttpMethod.Post, $"admin/api/skills/{Uri.EscapeDataString(name)}/restore", new JsonObject { ["version"] = version.Version });
            await LoadSkillsAsync();
            await LoadSkillAsync(name);
            SetMessage($"v{version.Version}을(를) v{Number(result["version"])}(으)로 올렸습니다.");
        });
    }

    private async void DeleteSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (_skill is not { } name) return;
        if (MessageBox.Show(OwnerWindow, $"스킬 {name}을(를) 스토어에서 삭제할까요?\n\n이미 적용한 PC에는 남습니다. 30일 동안 skills\\.trash에 보관됩니다.", "스킬",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            await Admin.SendAsync(HttpMethod.Delete, "admin/api/skills/" + Uri.EscapeDataString(name), null);
            _skill = null;
            await LoadSkillsAsync();
        }, done: "스킬을 삭제했습니다.");
    }

    // ── 도움 ────────────────────────────────────────────────────────────────────────────────────────

    private static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : node?.ToString() ?? "";
    private static string Number(JsonNode? node) => node?.ToString() ?? "—";

    private static string LocalTime(JsonNode? node) =>
        DateTimeOffset.TryParse(Str(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
            ? moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    private static string LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
}

internal sealed record ClientRow(string Address, string Version, string Last, string Announcement, string Features)
{
    public string Tip => $"받은 공지: {Announcement}" + (Features.Length > 0 ? $"\n쓴 기능: {Features}" : "");
    public override string ToString() => $"{Address} · {Version} · {Last}";
}

internal sealed record ReportRow(string Id, string Time, string Version, string Summary, string Status, string Note, bool HasLogs, string Remote,
    string StatusLabel, Brush StatusBrush)
{
    public string Detail => $"{Time} · {(Version.Length > 0 ? Version : "버전 미상")}" + (Note.Length > 0 ? " · " + Note : "");
    public override string ToString() => $"{Summary} · {StatusLabel}";
}

/// <summary>서버 설정 한 줄 ("항목: 값")</summary>
internal sealed record SettingRow(string Name, string Value, Brush Brush)
{
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0xD1, 0xD1, 0xD6)), Warning = new SolidColorBrush(Color.FromRgb(0xE6, 0xC0, 0x7A));

    public static SettingRow From(string line)
    {
        int colon = line.IndexOf(": ", StringComparison.Ordinal);
        string name = colon > 0 ? line[..colon] : "";
        string value = colon > 0 ? line[(colon + 2)..] : line;
        bool warn = value.Contains("주의", StringComparison.Ordinal) || value.Contains("규칙 없음", StringComparison.Ordinal)
                    || value.Contains("누구나 열 수 있음", StringComparison.Ordinal);
        return new SettingRow(name, value, warn ? Warning : Normal);
    }
}

internal sealed record SkillRow(string Name, string Title, string Detail)
{
    public override string ToString() => Title;
}

internal sealed record VersionRow(int Version, string Text)
{
    public override string ToString() => Text;
}

/// <summary>앱 목록의 한 앱 (서버의 JSON 그대로: 이 창에 칸이 없는 값도 저장할 때 남음)</summary>
internal sealed class AppRow(JsonObject data) : INotifyPropertyChanged
{
    public JsonObject Data { get; } = data;
    public string Title => Data["name"]?.ToString() is { Length: > 0 } name ? name : "(이름 없음)";
    public string Detail => string.Join(" · ", new[] { Data["id"]?.ToString(), Data["category"]?.ToString(), Data["kind"]?.ToString(),
            (bool?)Data["masterOnly"] == true ? "마스터만" : null,
            ManagerView.Flag(Data, "beta") ? "베타" : null, ManagerView.Flag(Data, "new") ? "NEW" : null }
        .Where(part => !string.IsNullOrWhiteSpace(part))) is { Length: > 0 } text ? text : "id 없음";
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Title;
    public void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
    }
}
