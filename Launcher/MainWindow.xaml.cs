using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ILauncher.Models;
using ILauncher.Services;
using ILuncher.AppKit;

namespace ILauncher;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PeriodicRefresh = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ClickRecheckAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RunningCheck = TimeSpan.FromSeconds(4);

    private readonly LauncherSettings _settings;
    private readonly string? _startupMessage;
    private readonly List<AppCard> _cards = [];
    private readonly DispatcherTimer _periodic;
    private GitHubClient _github = null!;
    private AppManager _manager = null!;
    private ICatalogStore _store = null!;
    private NoticeService _notices = null!;
    private AppIconService _icons = null!;
    private NoticeWindow? _noticeWindow;
    private bool _noticeRefreshFailed;
    private string _filter = "all";
    private int _refreshGeneration;
    private bool _refreshing;
    private AvailableUpdate? _launcherUpdate;
    private bool _launcherUpdating;
    private bool _updatingAll;
    private readonly FavoriteStore _favoriteStore;
    private List<FavoriteProgram> _favorites;
    private readonly MyAppStore _myAppStore;
    private List<AppDefinition> _myApps;
    private readonly UsageStore _usage;
    private readonly DispatcherTimer _runningTimer;
    private bool _scanningRunning;
    /// <summary>지금 보이는 화면 이름 (전체 앱 · 분류 · 즐겨찾기 · 업데이트 · 검색): 앱 페이지의 뒤로 글자</summary>
    private string _title = "전체 앱";

    public MainWindow(LauncherSettings settings, string? startupMessage = null)
    {
        _settings = settings;
        _startupMessage = startupMessage;
        CustomIcons.Folder = Path.Combine(settings.DataDirectory, "custom-icons");
        SiteInfo.CacheFolder = Path.Combine(settings.DataDirectory, "site-icons");
        MyAppStore.MediaFolder = Path.Combine(settings.DataDirectory, "my-media");
        _favoriteStore = new FavoriteStore(Path.Combine(settings.DataDirectory, "favorites.json"));
        _favorites = _favoriteStore.Load();
        _myAppStore = new MyAppStore(Path.Combine(settings.DataDirectory, "my-apps.json"));
        _myApps = _myAppStore.Load();
        _usage = new UsageStore(Path.Combine(settings.DataDirectory, "usage.json"));
        InitializeComponent();
        CreateServices();
        TitleVersionText.Text = "v" + LauncherVersion;
        ApiManagerButton.Visibility = IsApiManager ? Visibility.Visible : Visibility.Collapsed;
        AppCard.AdminMenus = IsApiManager;
        // 관리자 메뉴에서 앱 목록을 저장하면 바로 다시 읽음 (분류·이름·순서, 새로 넣은 앱의 릴리스도 확인)
        ApiManager.ManagerHost.CatalogSaved += () => Dispatcher.InvokeAsync(async () => await RefreshAsync(checkReleases: true));
        PreviewMouseUp += Window_PreviewMouseUp;
        _periodic = new DispatcherTimer(PeriodicRefresh, DispatcherPriority.Background, async (_, _) => await PeriodicRefreshAsync(), Dispatcher);
        _periodic.Stop();
        // 실행 중 표시: 창이나 트레이 창이 보일 때만 몇 초마다
        _runningTimer = new DispatcherTimer(RunningCheck, DispatcherPriority.Background, async (_, _) => await ScanRunningAsync(), Dispatcher);
        _runningTimer.Stop();
    }

    /// <summary>지금 보이는 화면 이름 (검사·화면 확인용)</summary>
    public string ViewTitle => _title;

    public static string LauncherVersion => AppUpdater.CurrentVersion;

    /// <summary>관리자 메뉴 (앱 목록 · 릴리스 · 러너 · 공지 · 서버): MASTER_USERS 에 이름이 있는 계정만, 비밀번호 없이</summary>
    private bool IsApiManager => _settings.Build.IsListedMaster(Environment.UserName);

    private void ApiManagerButton_Click(object sender, RoutedEventArgs e) => OpenAdminMenu();

    // 오른쪽 화면 전체를 쓰는 화면: 관리자 메뉴 (한 번 만들어 다시 씀, 고치던 내용이 남음), 설정 (열 때마다 새로)
    private ApiManager.ManagerView? _managerView;
    private SettingsView? _settingsView;
    private bool OnPage => _filter is "manager" or "settings";

    /// <summary>앱 목록 또는 오른쪽 화면 (관리자 메뉴 · 설정)</summary>
    private void ShowPage()
    {
        if (_filter == "manager" && !IsApiManager) _filter = "all";
        FrameworkElement? page = _filter switch
        {
            "manager" => _managerView ??= ApiManager.ManagerHost.CreateView(),
            "settings" => _settingsView ??= CreateSettingsView(),
            _ => null
        };
        if (!ReferenceEquals(PageContent.Content, page)) PageContent.Content = page;
        PagePane.Visibility = page is null ? Visibility.Collapsed : Visibility.Visible;
        // 앱 페이지는 목록 위에 (목록의 스크롤 위치는 그대로 남음)
        bool detail = page is null && _detail is not null;
        DetailPane.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        StorePane.Visibility = page is null && !detail ? Visibility.Visible : Visibility.Collapsed;
    }
    private string IconCacheDirectory => Path.Combine(_settings.DataDirectory, "icons");

    private void CreateServices()
    {
        _github?.Dispose();
        // API 서버가 있으면 github.com 저장소는 그 서버의 GitHub 중계로 읽음 (토큰 없이 비공개 릴리스)
        _github = new GitHubClient(host => _settings.GetToken(host), LauncherVersion, apiBaseOverride: repository => _settings.ApiBaseFor(repository));
        _manager = new AppManager(_github, new AppInstaller(), Path.Combine(_settings.DataDirectory, "downloads"), () => _settings.DefaultHost);
        (_store as IDisposable)?.Dispose();
        _store = _settings.CatalogMode switch
        {
            LauncherSettings.ModeApi => new ApiCatalogStore(_settings.ApiServerUrl!, () => null,
                Path.Combine(_settings.DataDirectory, "catalog-cache.json")),
            LauncherSettings.ModeGitHub when _settings.CatalogRepository is { } repository =>
                new GitHubCatalogStore(_github, repository, _settings.CatalogPath, _settings.CatalogBranch, Path.Combine(_settings.DataDirectory, "catalog-cache.json")),
            _ => new LocalCatalogStore(Path.Combine(_settings.DataDirectory, "catalog.json"))
        };
        _notices = new NoticeService(FetchNoticesAsync, _settings.DataDirectory);
        _icons = new AppIconService(_github, IconCacheDirectory, () => _settings.DefaultHost);
        CatalogSourceText.Text = "앱 목록: " + _store.Location;
    }

    private async Task<string?> FetchNoticesAsync(CancellationToken cancellationToken)
    {
        if (_store is ApiCatalogStore api)
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.GetAsync(api.Server + "/launcher/notices", cancellationToken);
            if (!response.IsSuccessStatusCode) throw new IOException($"API 서버 공지 응답 {(int)response.StatusCode}");
            using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null;
        }
        if (_settings.CatalogRepository is not { } repository)
        {
            string local = Path.Combine(_settings.DataDirectory, LauncherSettings.DefaultNoticesPath);
            return File.Exists(local) ? await File.ReadAllTextAsync(local, cancellationToken) : null;
        }
        return (await _github.GetFileAsync(repository, _settings.NoticesPath, _settings.CatalogBranch, cancellationToken))?.Content;
    }

    // ───────────────────────── 시작 · 새로고침 ─────────────────────────

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = _startupMessage ?? $"i-luncher v{LauncherVersion}";
        // 런처가 스스로 파일을 바꾸므로 Windows "앱" 목록의 버전을 실행 중인 버전과 맞춤
        if (AppUpdater.IsInstalledCopy("i-luncher")) AppUpdater.SyncInstalledVersion("i-luncher");
        ShowNotices(_notices.CachedNotices);
        // 서버 PC 의 마스터: 런처에 든 relay.py 가 더 새 버전이면 서버를 바꾸고, 꺼져 있으면 켬 (앱 목록을 읽기 전에)
        if (IsApiManager) await ApiManager.ManagerHost.ApplyAsync();
        await RefreshAsync(checkReleases: true);
        _periodic.Start();
        _runningTimer.Start();
        _ = ScanRunningAsync();
        _ = RefreshNoticesAsync();
        _ = RefreshSiteIconsAsync();
        await CheckLauncherUpdateAsync(automatic: true);
    }

    private void Window_Activated(object? sender, EventArgs e) => _ = ScanRunningAsync();

    /// <summary>
    /// 설치된 앱 중 실행 중인 것을 칸 · 독 · 트레이 창에 표시. 창도 트레이 창도 안 보이면 건너뜀.
    /// 실행 중인 앱이 바뀌면 독(즐겨찾기가 아닌 실행 중인 앱)을 다시 그림
    /// </summary>
    internal async Task ScanRunningAsync()
    {
        if (_scanningRunning || (!IsVisible && _quick?.IsVisible != true)) return;
        _scanningRunning = true;
        try
        {
            var installed = _cards.Where(card => card.Installed is not null).Select(card => (card, directory: card.Installed!.Directory)).ToList();
            var running = await Task.Run(() => AppInstaller.RunningDirectories(installed.Select(item => item.directory)));
            bool changed = false;
            foreach (var card in _cards)
            {
                bool now = card.Installed is { } app && running.Contains(app.Directory);
                if (card.IsRunning == now) continue;
                card.IsRunning = now;
                changed = true;
            }
            if (changed) RenderDock();
        }
        finally { _scanningRunning = false; }
    }

    private async Task PeriodicRefreshAsync()
    {
        if (_cards.Any(card => card.IsBusy)) return;
        await RefreshAsync(checkReleases: true);
        await RefreshNoticesAsync();
        await CheckLauncherUpdateAsync(automatic: false);
    }

    private async Task RefreshAsync(bool checkReleases)
    {
        if (_refreshing) return;
        _refreshing = true;
        int generation = ++_refreshGeneration;
        RefreshButton.IsEnabled = false;
        try
        {
            CatalogSnapshot snapshot = await _store.LoadAsync();
            if (generation != _refreshGeneration) return;
            if (snapshot.Warning is { } warning) ShowError(warning); else HideError();
            MigrateFavorites(snapshot.Apps);
            MergeCards(WithMyApps(snapshot.Apps));
            await DetectInstallsAsync(_cards.ToList());
            Render();
            if (checkReleases) await CheckReleasesAsync(_cards.ToList());
            _ = LoadRepositoryIconsAsync(_cards.ToList());
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>앱 목록 + 내 앱 (앱 목록에 같은 저장소 · id 가 있으면 앱 목록 것만). 내 앱은 앱 목록 뒤에</summary>
    private List<AppDefinition> WithMyApps(IReadOnlyList<AppDefinition> apps)
    {
        var all = apps.ToList();
        int order = 100000;
        foreach (var mine in _myApps)
        {
            if (all.Any(app => app.Id.Equals(mine.Id, StringComparison.OrdinalIgnoreCase)
                               || MyAppStore.SameRepository(app.Repository, mine.Repository, _settings.DefaultHost))) continue;
            var copy = mine.Clone();
            copy.Personal = true;
            copy.Order = order++;
            all.Add(copy);
        }
        return all;
    }

    private void MergeCards(IReadOnlyList<AppDefinition> apps)
    {
        // "마스터만" 앱은 마스터 계정(빌드 설정 MASTER_USERS)에만. 목록을 고칠 때는 서버의 전체 목록에 적용하므로 숨긴 앱도 남음
        bool master = _settings.Build.IsMaster(Environment.UserName);
        apps = apps.Where(app => !app.MasterOnly || master).ToList();
        var byId = _cards.ToDictionary(card => card.App.Id, StringComparer.OrdinalIgnoreCase);
        var next = new List<AppCard>();
        foreach (var app in apps)
        {
            if (byId.TryGetValue(app.Id, out var card))
            {
                bool releaseSourceChanged = !string.Equals(card.App.Repository, app.Repository, StringComparison.OrdinalIgnoreCase)
                                            || card.App.IncludePrerelease != app.IncludePrerelease;
                card.App = app;
                if (releaseSourceChanged) card.ResetRelease();
                next.Add(card);
            }
            else next.Add(new AppCard(app));
        }
        _cards.Clear();
        _cards.AddRange(next);
    }

    private async Task DetectInstallsAsync(IReadOnlyList<AppCard> cards)
    {
        var installer = _manager.Installer;
        var icons = _icons;
        var results = await Task.Run(() => cards.Select(card =>
        {
            var installed = installer.Detect(card.App);
            return (card, installed, icon: icons.Current(card.App, installed));
        }).ToList());
        foreach (var (card, installed, icon) in results)
        {
            card.Installed = installed;
            card.Icon = icon;
        }
    }

    private async Task CheckReleasesAsync(IReadOnlyList<AppCard> cards)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(cards.Select(async card =>
        {
            await gate.WaitAsync();
            try { await CheckReleaseAsync(card, TimeSpan.FromSeconds(30)); }
            finally { gate.Release(); }
        }));
        Render();
    }

    /// <summary>저장소에서 앱 아이콘을 찾아 (캐시가 오래됐거나 없을 때만) 카드에 반영. 실패해도 조용히 넘어감</summary>
    private async Task LoadRepositoryIconsAsync(IReadOnlyList<AppCard> cards)
    {
        using var gate = new SemaphoreSlim(3);
        await Task.WhenAll(cards.Select(async card =>
        {
            await gate.WaitAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var app = card.App;
                if (!await _icons.RefreshAsync(app, timeout.Token)) return;
                var installed = card.Installed;
                card.Icon = await Task.Run(() => _icons.Current(app, installed));
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
                Debug.WriteLine($"Icon for {card.App.Id}: {error.Message}");
            }
            finally { gate.Release(); }
        }));
    }

    private async Task<bool> CheckReleaseAsync(AppCard card, TimeSpan timeout)
    {
        card.IsChecking = true;
        try
        {
            using var cancel = new CancellationTokenSource(timeout);
            var release = await _manager.GetLatestAsync(card.App, cancel.Token);
            card.SetRelease(release, null);
            UpdateCounts();
            return true;
        }
        catch (Exception error) when (error is GitHubApiException or InvalidOperationException or OperationCanceledException or IOException)
        {
            card.SetRelease(null, error is OperationCanceledException ? "응답이 없습니다." : error.Message);
            return false;
        }
    }

    // ───────────────────────── 카드 동작 ─────────────────────────

    /// <summary>
    /// 카드 클릭: 미설치면 설치 여부를 묻고 설치한 뒤 실행. 설치돼 있으면 최신 릴리스를 확인해
    /// 설치된 버전보다 높으면 바로 업데이트하고 실행 (확인에 실패하면 지금 버전을 실행).
    /// </summary>
    private async Task OpenAsync(AppCard card)
    {
        if (card.IsBusy) return;
        card.Installed = await Task.Run(() => _manager.Installer.Detect(card.App));
        if (card.Installed is null)
        {
            await InstallWithConfirmationAsync(card, reinstall: false);
            return;
        }

        if (card.CheckedAt is null || DateTime.Now - card.CheckedAt > ClickRecheckAfter)
        {
            card.BeginWork("최신 버전 확인 중…");
            await CheckReleaseAsync(card, TimeSpan.FromSeconds(8));
            card.EndWork();
        }
        if (card.UpdateAvailable && card.Latest is { } release)
        {
            // 실행 중이면 종료하고 업데이트할지 묻고, 아니라고 하면 지금 버전을 그대로 엶
            if ((await CloseIfRunningAsync(card, "업데이트", UpdateDetail(card, release))).Proceed &&
                !await InstallAsync(card, release, $"v{release.Version}(으)로 업데이트 중…"))
            {
                // 업데이트에 실패해도 설치된 버전은 그대로 쓸 수 있음
                if (card.Installed is null) return;
            }
        }
        Launch(card);
    }

    private static string UpdateDetail(AppCard card, GitHubRelease release) =>
        $"v{card.Installed?.Version?.ToString() ?? "?"} → v{release.Version?.ToString() ?? release.Tag} · 업데이트한 뒤 다시 실행합니다.";

    /// <summary>
    /// 앱이 실행 중이면 종료하고 진행할지 묻고, 그렇다고 하면 닫음 (Windows를 끌 때처럼 앱이 정리하고 닫힘).
    /// 닫히지 않으면 강제로 끝낼지 한 번 더 물음. Proceed: 계속해도 되는지, Closed: 이 동작으로 닫았는지 (끝난 뒤 다시 실행)
    /// </summary>
    private async Task<(bool Proceed, bool Closed)> CloseIfRunningAsync(AppCard card, string action, string detail, bool ask = true)
    {
        if (card.Installed is not { } installed || !await Task.Run(() => AppInstaller.IsRunning(installed.Directory))) return (true, false);
        if (ask && new InstallConfirmationWindow(card.Name, card.Icon, $"앱 {action}", $"{card.Name}이(가) 실행 중입니다. 종료하고 {action}할까요?",
                detail, action) { Owner = this }.ShowDialog() != true) return (false, false);
        card.BeginWork("종료하는 중…");
        try
        {
            if (await AppInstaller.CloseRunningAsync(installed.Directory, force: false)) return (true, true);
            var force = new InstallConfirmationWindow(card.Name, card.Icon, "강제 종료", $"{card.Name}이(가) 종료되지 않았습니다. 강제로 종료할까요?",
                "저장하지 않은 작업은 사라질 수 있습니다.", "강제 종료") { Owner = this };
            if (force.ShowDialog() != true) return (false, false);
            if (await AppInstaller.CloseRunningAsync(installed.Directory, force: true)) return (true, true);
            ShowError($"{card.Name}을(를) 종료하지 못했습니다. 직접 종료한 뒤 다시 시도하세요.");
            return (false, false);
        }
        finally { card.EndWork(); }
    }

    private async Task<bool> InstallWithConfirmationAsync(AppCard card, bool reinstall)
    {
        card.BeginWork("최신 릴리스 확인 중…");
        bool checkedOk = await CheckReleaseAsync(card, TimeSpan.FromSeconds(20));
        card.EndWork();
        if (card.Latest is not { } release)
        {
            ShowError(checkedOk
                ? $"{card.Name} 저장소에 아직 릴리스가 없어 설치할 수 없습니다. 저장소의 Actions 릴리스를 먼저 만드세요."
                : $"{card.Name}의 최신 릴리스를 확인하지 못했습니다: {card.LatestError}");
            return false;
        }

        string kind = AppManager.ResolveKind(card.App, release);
        GitHubAsset? asset = kind == InstallKinds.Zip ? AppManager.SelectZipAsset(card.App, release) : AppManager.SelectSetupAsset(card.App, release);
        string detail = $"{(release.Version is { } version ? "v" + version : release.Tag)}" +
                        (asset is null ? " · 설치 파일 없음" : $" · {asset.Name}" + (asset.Size > 0 ? $" ({asset.Size / 1048576.0:N1} MB)" : "")) +
                        (kind == InstallKinds.Zip ? $"\n설치 위치: {_manager.Installer.GetInstallDirectory(card.App)}" : "\n앱의 설치 프로그램을 실행합니다.");
        var confirm = new InstallConfirmationWindow(card.Name, card.Icon, reinstall ? "다시 설치" : "앱 설치",
            reinstall ? "최신 릴리스로 다시 설치할까요?" : "이 앱을 설치할까요?", detail, reinstall ? "다시 설치" : "설치") { Owner = this };
        if (confirm.ShowDialog() != true) return false;
        var (proceed, closed) = reinstall ? await CloseIfRunningAsync(card, "다시 설치", "다시 설치한 뒤 다시 실행합니다.") : (true, false);
        if (!proceed || !await InstallAsync(card, release, "설치 준비 중…")) return false;
        if (!reinstall || closed) Launch(card);
        return true;
    }

    private async Task<bool> InstallAsync(AppCard card, GitHubRelease release, string firstMessage)
    {
        card.BeginWork(firstMessage);
        var progress = new Progress<AppProgress>(card.ReportWork);
        try
        {
            var installed = await _manager.InstallAsync(card.App, release, progress);
            card.Installed = installed;
            card.Icon = await Task.Run(() => _icons.Current(card.App, installed));
            StatusText.Text = $"{card.Name} v{installed.Version?.ToString() ?? release.Tag} 설치 완료";
            HideError();
            return true;
        }
        catch (RuntimeMissingException missing)
        {
            if (MessageBox.Show(this, missing.Message + "\n\n.NET 다운로드 페이지를 열까요? 설치한 뒤 다시 시도하세요.", card.Name,
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenUrl(missing.DownloadUrl);
            return false;
        }
        catch (Exception error) when (error is GitHubApiException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            ShowError($"{card.Name}: {error.Message}");
            return false;
        }
        finally
        {
            card.EndWork();
            card.Installed = await Task.Run(() => _manager.Installer.Detect(card.App));
            UpdateCounts();
        }
    }

    /// <summary>설치된 앱 실행. 실행하지 못했으면 false (오류는 알림에)</summary>
    private bool Launch(AppCard card)
    {
        if (card.Installed is not { } installed) return false;
        try
        {
            _manager.Installer.Launch(card.App, installed);
            StatusText.Text = $"{card.Name} 실행";
            _usage.Add(UsageStore.AppKey(card.App.Id));
            return true;
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            StatusText.Text = $"{card.Name} 실행을 취소했습니다.";  // UAC 취소
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            ShowError($"{card.Name}을(를) 실행하지 못했습니다: {error.Message}");
            return false;
        }
    }

    private static AppCard? CardOf(object sender) => (sender as FrameworkElement)?.Tag as AppCard;

    private async void LaunchMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        card.Installed = await Task.Run(() => _manager.Installer.Detect(card.App));
        Launch(card);
    }

    private async void UpdateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) await UpdateCardAsync(card);
    }

    /// <summary>앱 하나 업데이트: 실행 중이면 종료할지 묻고, 닫았으면 업데이트한 뒤 다시 실행</summary>
    private async Task UpdateCardAsync(AppCard card)
    {
        if (card is not { Latest: { } release, IsBusy: false }) return;
        var (proceed, closed) = await CloseIfRunningAsync(card, "업데이트", UpdateDetail(card, release));
        if (proceed && await InstallAsync(card, release, $"v{release.Version}(으)로 업데이트 중…") && closed) Launch(card);
    }

    private async void UpdateAllButton_Click(object sender, RoutedEventArgs e) => await UpdateAllAsync();

    /// <summary>
    /// 업데이트가 있는 앱을 화면 순서대로 하나씩 업데이트. 실행 중인 앱이 있으면 한 번만 묻고
    /// (취소하면 그 앱들만 건너뜀), 닫았던 앱은 업데이트한 뒤 다시 실행
    /// </summary>
    private async Task UpdateAllAsync()
    {
        if (_updatingAll) return;
        _updatingAll = true;
        UpdateCounts();
        var queued = new List<AppCard>();
        try
        {
            // 확인한 지 오래된 앱은 카드 클릭과 같은 기준으로 다시 확인
            var stale = _cards.Where(card => card is { IsInstalled: true, IsBusy: false } && (card.CheckedAt is null || DateTime.Now - card.CheckedAt > ClickRecheckAfter)).ToList();
            if (stale.Count > 0)
            {
                StatusText.Text = "최신 버전 확인 중…";
                await CheckReleasesAsync(stale);
            }
            var targets = _cards.Where(card => card is { UpdateAvailable: true, IsBusy: false, Latest: not null })
                .OrderBy(card => card.App.Order).ThenBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (targets.Count == 0)
            {
                StatusText.Text = "모두 최신 버전입니다.";
                return;
            }

            var running = (await Task.Run(() => targets.Where(card => card.Installed is { } installed && AppInstaller.IsRunning(installed.Directory)).ToList())).ToHashSet();
            bool closeRunning = running.Count == 0 || new InstallConfirmationWindow(
                running.Count == 1 ? running.First().Name : $"{running.First().Name} 외 {running.Count - 1}개", running.First().Icon, "모두 업데이트",
                "실행 중입니다. 종료하고 업데이트할까요?", "업데이트한 뒤 다시 실행합니다.", "업데이트") { Owner = this }.ShowDialog() == true;
            if (!closeRunning && targets.RemoveAll(running.Contains) > 0 && targets.Count == 0)
            {
                StatusText.Text = "업데이트를 취소했습니다.";
                return;
            }

            foreach (var card in targets) card.BeginWork("대기 중…");
            queued.AddRange(targets);
            int done = 0;
            var relaunch = new List<AppCard>();
            foreach (var card in targets)
            {
                var release = card.Latest!;
                var (proceed, closed) = running.Contains(card) ? await CloseIfRunningAsync(card, "업데이트", UpdateDetail(card, release), ask: false) : (true, false);
                if (proceed && await InstallAsync(card, release, $"v{release.Version}(으)로 업데이트 중…")) done++;
                if (closed) relaunch.Add(card);
            }
            // 닫았던 앱은 모두 끝난 뒤에 다시 실행 (API 서버 관리 프로그램이 다시 시작하며 중계를 재시작해도 남은 다운로드에 영향 없음)
            foreach (var card in relaunch) Launch(card);
            int failed = targets.Count - done, skipped = closeRunning ? 0 : running.Count;
            StatusText.Text = $"{done}개 앱 업데이트 완료" + (failed > 0 ? $" · {failed}개 실패" : "") + (skipped > 0 ? $" · {skipped}개 건너뜀" : "");
        }
        finally
        {
            foreach (var card in queued.Where(card => card.IsBusy)) card.EndWork();
            _updatingAll = false;
            Render();  // 업데이트 섹션에서 끝난 앱을 뺌
        }
    }

    private async void ReinstallMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { IsBusy: false } card) await InstallWithConfirmationAsync(card, reinstall: card.IsInstalled);
    }

    private async void UninstallMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { IsBusy: false, Installed: { } installed } card) return;
        string detail = installed.ManagedByLauncher
            ? $"설치 폴더를 지웁니다: {installed.Directory}\n설치 폴더 밖에 저장된 데이터와 설정은 그대로 남습니다."
            : "앱의 제거 프로그램을 실행합니다.";
        var confirm = new InstallConfirmationWindow(card.Name, card.Icon, "앱 제거", "이 앱을 제거할까요?", detail, "제거") { Owner = this };
        if (confirm.ShowDialog() != true) return;
        if (!(await CloseIfRunningAsync(card, "제거", "앱을 닫은 뒤 제거합니다.")).Proceed) return;
        card.BeginWork("제거 중…");
        try
        {
            await _manager.Installer.UninstallAsync(card.App, installed);
            StatusText.Text = $"{card.Name} 제거 완료";
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError($"{card.Name}: {error.Message}");
        }
        finally
        {
            card.EndWork();
            card.Installed = await Task.Run(() => _manager.Installer.Detect(card.App));
            Render();
        }
    }

    private void OpenRepositoryMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) OpenUrl(GitHubRepository.Parse(card.App.Repository, _settings.DefaultHost)?.WebUrl ?? card.App.Repository);
    }

    private void OpenReleaseMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { Latest: { HtmlUrl.Length: > 0 } release }) OpenUrl(release.HtmlUrl);
    }

    private void OpenFolderMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { Installed: { } installed } && Directory.Exists(installed.Directory))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{installed.Directory}\"") { UseShellExecute = true })?.Dispose();
    }

    // ───────────────────────── 앱 목록 고치기 = 관리자 메뉴 ─────────────────────────

    /// <summary>독 끝의 + (프로그램 추가). 앱 추가는 관리자 메뉴 → 앱 목록</summary>
    /// <summary>
    /// GitHub 프로젝트 추가: 저장소를 확인해 내 앱으로 (이 PC 에만). 추가하면 홈 화면에 넣고 그 앱 페이지를 엶 (받기는 거기서)
    /// </summary>
    private async void AddGitHubProject()
    {
        var dialog = new GitHubProjectWindow(_manager, _settings.DefaultHost, Categories(),
            repository => _cards.FirstOrDefault(card => MyAppStore.SameRepository(card.App.Repository, repository.WebUrl, _settings.DefaultHost))?.Name)
        { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } app) return;
        string id = app.Id;
        for (int n = 2; _cards.Any(card => card.App.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                        || _myApps.Any(mine => mine.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); n++) id = $"{app.Id}-{n}";
        app.Id = id;
        app.Order = 100000 + _myApps.Count;
        _myApps.Add(app);
        try
        {
            _myAppStore.Save(_myApps);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _myApps.Remove(app);
            ShowError("내 앱을 저장하지 못했습니다: " + error.Message);
            return;
        }
        var card = new AppCard(app.Clone());
        _cards.Add(card);
        await DetectInstallsAsync([card]);
        Render();
        ShowDetail(card);
        StatusText.Text = $"{app.Name} 추가함 (내 앱)";
        await CheckReleaseAsync(card, TimeSpan.FromSeconds(20));
        _ = LoadRepositoryIconsAsync([card]);
        UpdateCounts();
    }

    /// <summary>내 앱 편집: 이름 · 제작자 · 분류 · 설명 · 상세 설명 · 아이콘 · 스크린샷 (저장소는 그대로)</summary>
    private async void EditMyApp_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { App.Personal: true } card) return;
        int index = _myApps.FindIndex(app => app.Id.Equals(card.App.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var before = _myApps[index];
        var plain = before.Clone();
        plain.Icon = null;  // 자동(저장소 아이콘) 미리 보기용
        var dialog = new GitHubProjectWindow(_manager, _settings.DefaultHost, Categories(), _ => null, before)
        {
            Owner = this,
            RepositoryIcon = _icons.Current(plain, card.Installed)
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } edited) return;
        _myApps[index] = edited;
        try
        {
            _myAppStore.Save(_myApps);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _myApps[index] = before;
            ShowError("내 앱을 저장하지 못했습니다: " + error.Message);
            return;
        }
        card.App = edited.Clone();
        if (before.Icon != edited.Icon)
        {
            card.Icon = await Task.Run(() => _icons.Current(card.App, card.Installed));
            if (!CustomIcons.IsCustom(edited.Icon)) _ = LoadRepositoryIconsAsync([card]);  // 자동으로 되돌림: 저장소에서 다시 찾음
        }
        Render();
        if (ReferenceEquals(_detail, card)) ShowDetail(card);
        StatusText.Text = $"{edited.Name} 수정함";
        HideError();
    }

    /// <summary>내 앱 삭제: 목록에서만 뺄지, 설치된 앱도 제거할지 물어봄</summary>
    private async void DeleteMyApp_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { App.Personal: true, IsBusy: false } card) return;
        var dialog = new DeleteProjectWindow(card) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        bool uninstalled = false;
        if (dialog.Uninstall && card.Installed is { } installed)
        {
            if (!(await CloseIfRunningAsync(card, "제거", "앱을 닫은 뒤 제거하고 목록에서 뺍니다.")).Proceed) return;
            card.BeginWork("제거 중…");
            try
            {
                await _manager.Installer.UninstallAsync(card.App, installed);
                uninstalled = true;
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                card.EndWork();
                ShowError($"{card.Name}: {error.Message} (목록에서는 빼지 않았습니다)");
                return;
            }
            card.EndWork();
        }
        _myApps.RemoveAll(app => app.Id.Equals(card.App.Id, StringComparison.OrdinalIgnoreCase));
        try
        {
            _myAppStore.Save(_myApps);
            // 이 앱에 넣었던 스크린샷 · 아이콘 그림 (이 PC 에 복사해 둔 것)
            foreach (string shot in card.App.Screenshots ?? []) MyAppStore.DeleteMedia(shot);
            CustomIcons.DeleteImported(card.App.Icon);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowError("내 앱을 저장하지 못했습니다: " + error.Message);
        }
        // 즐겨찾기에서도
        if (_favorites.RemoveAll(item => item.IsApp && item.AppId.Equals(card.App.Id, StringComparison.OrdinalIgnoreCase)) > 0)
            _favoriteStore.Save(_favorites);
        if (ReferenceEquals(_detail, card)) CloseDetail();
        _cards.Remove(card);
        StatusText.Text = uninstalled ? $"{card.Name} 제거하고 목록에서 뺌" : $"{card.Name} 목록에서 뺌";
        Render();
    }

    /// <summary>독의 +: 이 PC 프로그램 또는 GitHub 프로젝트 추가 (메뉴로 고름)</summary>
    private void AddAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var program = new MenuItem { Header = "이 PC 프로그램…", Icon = "\uE7F4" };
        program.Click += (_, _) => EditFavorite(null);
        var site = new MenuItem { Header = "웹사이트…", Icon = "\uE774" };
        site.Click += (_, _) => EditFavorite(null, web: true);
        var project = new MenuItem { Header = "GitHub 프로젝트…", Icon = "\uE943" };
        project.Click += (_, _) => AddGitHubProject();
        menu.Items.Add(program);
        menu.Items.Add(site);
        menu.Items.Add(project);
        menu.IsOpen = true;
    }

    /// <summary>카드 메뉴의 "관리자 메뉴에서 편집" (마스터만): 관리자 메뉴 → 앱 목록에서 그 앱을 엶</summary>
    private void EditMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card || !IsApiManager) return;
        OpenAdminMenu();
        _managerView?.ShowApp(card.App.Id);
    }

    private void OpenAdminMenu()
    {
        if (!IsApiManager) return;
        CloseDetail();
        _filter = "manager";
        Render();
    }

    // ───────────────────────── 런처 자체 업데이트 ─────────────────────────

    private UpdateSettings? LauncherUpdateSettings()
    {
        if (_settings.LauncherRepository is not { } repository) return null;
        // API 서버가 있으면 그 서버의 GitHub 중계로 (토큰 없이)
        return _settings.ApiBaseFor(repository) is { } relay
            ? new UpdateSettings(relay, repository.FullName, null, "i-luncher-win-x64.zip", "i-luncher")
            : new UpdateSettings(repository.ApiBase, repository.FullName, _settings.GetToken(repository.TokenHost), "i-luncher-win-x64.zip", "i-luncher");
    }

    private async Task CheckLauncherUpdateAsync(bool automatic)
    {
        if (_launcherUpdating || LauncherUpdateSettings() is not { } settings) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _launcherUpdate = await AppUpdater.CheckAsync(settings, timeout.Token);
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Debug.WriteLine("Launcher update check failed: " + error.Message);
            return;
        }
        if (_launcherUpdate is not { } update)
        {
            LauncherUpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }
        bool installed = AppUpdater.IsInstalledCopy("i-luncher");
        if (installed && automatic && _settings.User.AutoUpdateLauncher && !_cards.Any(card => card.IsBusy))
        {
            await ApplyLauncherUpdateAsync();
            return;
        }
        LauncherUpdateText.Text = installed
            ? $"새 i-luncher v{update.Version}이(가) 있습니다. (지금 v{LauncherVersion})"
            : $"새 i-luncher v{update.Version}이(가) 있습니다. 설치본(i-luncher-Setup.exe)으로 실행하면 자동으로 업데이트됩니다.";
        LauncherUpdateButton.Content = installed ? "지금 업데이트" : "릴리스 열기";
        LauncherUpdateBanner.Visibility = Visibility.Visible;
    }

    private async void LauncherUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launcherUpdate is not { } update) return;
        if (!AppUpdater.IsInstalledCopy("i-luncher"))
        {
            OpenUrl(update.HtmlUrl ?? _settings.LauncherRepository?.WebUrl + "/releases");
            return;
        }
        if (_cards.Any(card => card.IsBusy))
        {
            ShowError("앱 설치·업데이트가 끝난 뒤 다시 누르세요.");
            return;
        }
        await ApplyLauncherUpdateAsync();
    }

    private async Task ApplyLauncherUpdateAsync()
    {
        if (_launcherUpdate is not { } update || LauncherUpdateSettings() is not { } settings) return;
        _launcherUpdating = true;
        LauncherUpdateBanner.Visibility = Visibility.Visible;
        LauncherUpdateButton.Visibility = Visibility.Collapsed;
        LauncherUpdateText.Text = $"i-luncher v{update.Version} 받는 중… 끝나면 다시 시작합니다.";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            string package = await AppUpdater.DownloadAsync(settings, update, timeout.Token);
            AppUpdater.ScheduleApply(package, update.Version, restart: true, "i-luncher");
            ExitApp();  // 트레이로 숨지 않고 정말 끝내야 업데이트가 적용됨
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _launcherUpdating = false;
            LauncherUpdateButton.Visibility = Visibility.Visible;
            LauncherUpdateText.Text = $"i-luncher v{update.Version} 업데이트 실패: {error.Message}";
        }
    }

    // ───────────────────────── 화면 ─────────────────────────

    /// <summary>지금 화면에 보이는 앱 (검사·화면 확인용)</summary>
    public IReadOnlyList<AppCard> VisibleCards { get; private set; } = [];

    private StoreSection? _updatesSection;

    private static List<AppCard> Sorted(IEnumerable<AppCard> cards) =>
        cards.OrderBy(card => card.App.Order).ThenBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>
    /// 홈 화면: 큰 아이콘 칸을 한 덩어리로 (앱 목록 순서, 전체 앱 · 분류 화면은 그 뒤에 이 PC 의 프로그램).
    /// 업데이트 = 업데이트할 앱과 모두 업데이트, 즐겨찾기 = 별 붙인 앱 + 내 PC 프로그램, 검색 = 찾은 앱 + 프로그램. 아래 독은 즐겨찾기 · 실행 중
    /// </summary>
    private void Render()
    {
        if (_detail is not null && !_cards.Contains(_detail)) CloseDetail();  // 목록에서 빠진 앱
        ShowPage();
        foreach (var card in _cards) card.IsFavorite = _favorites.Any(item => string.Equals(item.AppId, card.App.Id, StringComparison.OrdinalIgnoreCase));
        RenderDock();
        if (OnPage)
        {
            UpdateCounts();
            return;
        }
        string search = SearchBox.Text.Trim();
        var programs = ProgramCards();
        // 앱 페이지 큰 버튼의 글자색(Ink): 전체 앱 목록 순서대로 돌려 가며 (화면을 바꿔도 앱마다 같은 색)
        int palette = 0;
        foreach (var card in Sorted(_cards)) card.Palette = palette++ % CardArt.Count;

        string? category = _filter.StartsWith("category:", StringComparison.Ordinal) ? _filter[9..] : null;
        bool InCategory(string name) => string.Equals(name, category, StringComparison.CurrentCultureIgnoreCase);
        bool updates = _filter == "updates" && search.Length == 0;
        List<object> items;
        if (search.Length > 0)
        {
            _title = "검색";
            items = [.. Sorted(_cards.Where(card => card.Matches(search))), .. programs.Where(card => card.Matches(search))];
        }
        else if (_filter == "favorites")
        {
            _title = "즐겨찾기";
            items = [.. Sorted(_cards.Where(card => card.IsFavorite)), .. programs];
        }
        else if (updates)
        {
            _title = "업데이트";
            items = [.. Sorted(_cards.Where(card => card.UpdateAvailable))];
        }
        else if (category is not null)
        {
            _title = category;
            items = [.. Sorted(_cards.Where(card => InCategory(card.Category))), .. programs.Where(card => InCategory(card.Category))];
        }
        else
        {
            _title = "전체 앱";
            items = [.. Sorted(_cards), .. programs];
        }

        var sections = new List<StoreSection>();
        _updatesSection = null;
        if (items.Count > 0) sections.Add(updates ? _updatesSection = UpdatesSection(items) : new StoreSection("", items));
        // 칸은 늘 같은 크기 (창을 키우면 열만 늘어남). 제목 없는 묶음은 한 줄이 안 차면 그 개수만큼만 열을 두어 가운데로
        foreach (var section in sections)
        {
            section.Columns = section.Title.Length > 0 ? RowColumns : Math.Max(1, Math.Min(RowColumns, section.Items.Count));
            section.PanelWidth = section.Columns * CellWidth;
        }
        SectionList.ItemsSource = sections;
        VisibleCards = items.OfType<AppCard>().ToList();
        UpdateCounts();

        bool empty = sections.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CardScrollViewer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty) return;
        if (_filter == "favorites" && search.Length == 0)
        {
            EmptyStateTitle.Text = "즐겨찾기가 없습니다";
            EmptyStateText.Text = "앱: 마우스를 올려 별 · 프로그램: 아래 + 또는 끌어 놓기";
            EmptyStateButton.Content = "프로그램 추가";
            EmptyStateButton.Tag = "favorite-add";
        }
        else if (_cards.Count == 0 && search.Length == 0)
        {
            // 앱은 관리자가 관리자 메뉴 → 앱 목록에서 넣음 (버튼은 마스터에게만)
            EmptyStateTitle.Text = "아직 등록된 앱이 없습니다";
            EmptyStateText.Text = IsApiManager ? "관리자 메뉴 → 앱 목록에서 추가하세요." : "관리자가 앱을 추가하면 여기에 보입니다.";
            EmptyStateButton.Content = "관리자 메뉴";
            EmptyStateButton.Tag = "admin";
        }
        else
        {
            EmptyStateTitle.Text = updates ? "모두 최신입니다" : "일치하는 앱이 없습니다";
            EmptyStateText.Text = "";
            EmptyStateButton.Content = "전체 앱 보기";
            EmptyStateButton.Tag = "clear";
        }
        EmptyStateButton.Visibility = EmptyStateButton.Tag as string == "admin" && !IsApiManager ? Visibility.Collapsed : Visibility.Visible;
    }

    private StoreSection UpdatesSection(IReadOnlyList<object> updates) =>
        new("업데이트", updates, $"{updates.Count}개", _updatingAll ? "업데이트 중…" : "모두 업데이트", () => _ = UpdateAllAsync()) { ActionEnabled = !_updatingAll };

    /// <summary>묶음 오른쪽 파란 링크 (모두 업데이트)</summary>
    private void SectionAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StoreSection { Action: { } action }) action();
    }

    /// <summary>모두 업데이트 링크와 분류 버튼 줄의 개수 (릴리스 확인 · 설치가 끝날 때마다)</summary>
    private void UpdateCounts()
    {
        if (_updatesSection is not null)
        {
            _updatesSection.ActionText = _updatingAll ? "업데이트 중…" : "모두 업데이트";
            _updatesSection.ActionEnabled = !_updatingAll;
        }
        RenderCategories();
    }

    // 내 PC 프로그램 칸: 프로그램마다 한 번만 만듦 (아이콘을 파일에서 읽으므로). 편집하면 새 항목이라 다시 만듦
    private readonly Dictionary<FavoriteProgram, FavoriteCard> _programCards = [];

    /// <summary>즐겨찾기로 등록한 이 PC 의 프로그램(exe 등), 이름순</summary>
    private List<FavoriteCard> ProgramCards()
    {
        var programs = _favorites.Where(item => !item.IsApp).ToList();
        foreach (var gone in _programCards.Keys.Where(program => !programs.Contains(program)).ToList()) _programCards.Remove(gone);
        return programs.Select(program => _programCards.TryGetValue(program, out var card) ? card : _programCards[program] = new FavoriteCard(program))
            .OrderBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>즐겨찾기 (등록한 순서): 앱과 내 PC 프로그램</summary>
    private List<object> FavoriteItems()
    {
        var programs = ProgramCards();
        var items = new List<object>();
        foreach (var favorite in _favorites)
        {
            object? item = favorite.IsApp
                ? _cards.FirstOrDefault(card => string.Equals(card.App.Id, favorite.AppId, StringComparison.OrdinalIgnoreCase))
                : programs.FirstOrDefault(card => ReferenceEquals(card.Program, favorite));
            if (item is not null) items.Add(item);
        }
        return items;
    }

    private const int DockFavoritesMax = 12, DockRunningMax = 6;

    /// <summary>아래 독: 즐겨찾기(등록한 순서, 12개까지) | 즐겨찾기가 아닌 실행 중인 앱. 끝의 + 는 프로그램 추가</summary>
    private void RenderDock()
    {
        var favorites = FavoriteItems().Take(DockFavoritesMax).ToList();
        var running = Sorted(_cards.Where(card => card.IsRunning && !card.IsFavorite)).Take(DockRunningMax).ToList();
        DockFavorites.ItemsSource = favorites;
        DockRunning.ItemsSource = running;
        DockDivider.Visibility = favorites.Count > 0 && running.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>분류 이름 (앱과 즐겨찾기로 등록한 프로그램의 분류, 가나다순)</summary>
    private List<string> Categories() =>
        [.. _cards.Select(card => card.Category).Concat(ProgramCards().Select(card => card.Category))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)];

    private static readonly Brush ChipOnBack = Brushes.White, ChipOnText = MakeBrush(0x1C, 0x1C, 0x1E);
    private static readonly Brush StarLight = MakeBrush(0xFF, 0xD1, 0x5C), StarInk = MakeBrush(0xB4, 0x53, 0x09);
    private static readonly Brush UpdateLight = MakeBrush(0x4A, 0xA3, 0xFF), UpdateInk = MakeBrush(0x1D, 0x4E, 0xD8);

    /// <summary>
    /// 검색 아래 가운데 버튼 줄 (앱스토어처럼): 전체 · 즐겨찾기 · 업데이트(있을 때), 그리고 분류마다 색이 다른 아이콘과 개수.
    /// 고른 것은 흰 버튼. 검색하는 동안은 숨김
    /// </summary>
    private void RenderCategories()
    {
        CategoryButtons.Children.Clear();
        bool show = SearchBox.Text.Trim().Length == 0;
        CategoryButtons.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var programs = ProgramCards();
        AddChip("전체", null, "all", CategoryLook.AllGlyph, Brushes.White, ChipOnText);
        int favorites = _cards.Count(card => card.IsFavorite) + programs.Count;
        if (favorites > 0 || _filter == "favorites") AddChip("즐겨찾기", favorites, "favorites", "\uE735", StarLight, StarInk);
        int updates = _cards.Count(card => card.UpdateAvailable);
        if (updates > 0 || _filter == "updates") AddChip("업데이트", updates, "updates", "\uE896", UpdateLight, UpdateInk);
        var groups = _cards.Select(card => card.Category).Concat(programs.Select(card => card.Category))
            .GroupBy(name => name, StringComparer.CurrentCultureIgnoreCase).OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        int index = 0;
        foreach (var group in groups)
        {
            AddChip(group.Key, group.Count(), "category:" + group.Key, CategoryLook.Glyph(group.Key), CategoryLook.Light(index), CategoryLook.Ink(index));
            index++;
        }

        void AddChip(string name, int? count, string filter, string glyph, Brush color, Brush ink)
        {
            bool on = _filter == filter;
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run(name));
            if (count is { } n)
                text.Inlines.Add(new System.Windows.Documents.Run("  " + n) { FontWeight = FontWeights.Normal, Foreground = on ? Brushes.Gray : (Brush)FindResource("MutedTextBrush") });
            var icon = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, FontWeight = FontWeights.Normal,
                Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = on ? ink : color
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, text } };
            var button = new Button { Style = (Style)FindResource("CategoryChipStyle"), Content = content, Tag = on ? "on" : "off" };
            if (on) { button.Background = ChipOnBack; button.Foreground = ChipOnText; }
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => SelectFilter(filter);
            CategoryButtons.Children.Add(button);
        }
    }

    /// <summary>분류 버튼 줄에서 고름: 전체 · 즐겨찾기 · 업데이트 · category:이름</summary>
    internal void SelectFilter(string filter)
    {
        CloseDetail();
        _filter = filter;
        Render();
        CardScrollViewer.ScrollToTop();
    }

    // 제목줄의 로고·이름: 홈 (전체 앱, 검색 지움, 맨 위로)
    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        CloseDetail();
        _filter = "all";
        SearchBox.Text = "";
        Render();
        CardScrollViewer.ScrollToTop();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 다른 화면에서 검색하면 앱 목록으로
        if (OnPage && SearchBox.Text.Trim().Length > 0) _filter = "all";
        if (SearchBox.Text.Trim().Length > 0) CloseDetail();
        Render();
    }

    private void EmptyStateButton_Click(object sender, RoutedEventArgs e)
    {
        if (EmptyStateButton.Tag as string == "favorite-add") EditFavorite(null);
        else if (EmptyStateButton.Tag as string == "admin") OpenAdminMenu();
        else
        {
            _filter = "all";
            SearchBox.Text = "";
            Render();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(checkReleases: true);
        await RefreshNoticesAsync();
        await CheckLauncherUpdateAsync(automatic: false);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsView = null;
        _detail = null;
        _filter = "settings";
        Render();
    }

    private SettingsView CreateSettingsView() => new(_settings, _settings.SaveUser);

    // 설정 · 관리자 메뉴의 뒤로 (Esc): 앱 목록 (전체 앱)
    private void PageBack_Click(object sender, RoutedEventArgs e)
    {
        _filter = "all";
        Render();
    }

    private void CatalogSourceText_Click(object sender, MouseButtonEventArgs e)
    {
        if (_store.WebUrl is { } url) OpenUrl(url);
        else if (_store is LocalCatalogStore) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.DataDirectory}\"") { UseShellExecute = true })?.Dispose();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorPanel.Visibility = Visibility.Collapsed;
    private void DismissErrorButton_Click(object sender, RoutedEventArgs e) => HideError();

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception error) { ShowError("브라우저를 열지 못했습니다: " + error.Message); }
    }

    // ───────────────────────── 공지 ─────────────────────────

    private async Task RefreshNoticesAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var notices = await _notices.RefreshAsync(timeout.Token);
            _noticeRefreshFailed = false;
            ShowNotices(notices);
        }
        catch (Exception error) when (error is GitHubApiException or IOException or OperationCanceledException or FormatException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
        {
            _noticeRefreshFailed = true;
            ShowNotices(_notices.CachedNotices);
        }
    }

    private void ShowNotices(IReadOnlyList<Notice> notices)
    {
        var unread = notices.Where(notice => !_notices.IsRead(notice)).ToList();
        NoticeUnreadDot.Visibility = unread.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticeBanner.Visibility = unread.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticeBannerTitle.Text = unread.Count switch { 0 => "", 1 => unread[0].Title, _ => $"{unread[0].Title} 외 {unread.Count - 1}건" };
        NoticeBanner.Tag = unread.FirstOrDefault()?.Id;
        _noticeWindow?.SetNotices(notices, _noticeRefreshFailed);
    }

    private void NoticeButton_Click(object sender, RoutedEventArgs e) => OpenNotices(null);
    private void NoticeBanner_Click(object sender, RoutedEventArgs e) => OpenNotices(NoticeBanner.Tag as string);

    private void OpenNotices(string? selectId)
    {
        if (_noticeWindow is null)
        {
            _noticeWindow = new NoticeWindow(_notices.CachedNotices, _noticeRefreshFailed, notice =>
            {
                _notices.MarkRead(notice);
                ShowNotices(_notices.CachedNotices);
            }, RefreshNoticesAsync) { Owner = this };
            _noticeWindow.Closed += (_, _) => _noticeWindow = null;
            _noticeWindow.Show();
        }
        else _noticeWindow.Activate();
        if (selectId is not null) _noticeWindow.SelectNotice(selectId);
    }

    // ───────────────────────── 칸 열 수 ─────────────────────────

    // 홈 화면 칸 한 칸의 폭 (아이콘 칸 132 + 양옆 4). 창 폭에 맞춰 열 수가 바뀜
    private const double CellWidth = 140;

    public static readonly DependencyProperty RowColumnsProperty =
        DependencyProperty.Register(nameof(RowColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(6));

    public int RowColumns
    {
        get => (int)GetValue(RowColumnsProperty);
        set => SetValue(RowColumnsProperty, value);
    }

    private void CardScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRowColumns();

    private void CardScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0) UpdateRowColumns();  // 스크롤바가 생기거나 없어질 때
    }

    private void UpdateRowColumns()
    {
        double available = CardScrollViewer.ViewportWidth > 0 ? CardScrollViewer.ViewportWidth : CardScrollViewer.ActualWidth - 14;
        if (available <= 0) return;
        int columns = Math.Clamp((int)Math.Floor(available / CellWidth), 1, 12);
        if (columns == RowColumns) return;
        RowColumns = columns;
        if (IsLoaded) Render();  // 줄마다 카드 수가 열 수를 따름
    }

    // ───────────────────────── 창 ─────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleDetailKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key is Key.F or Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && !OnPage)
        {
            RefreshButton_Click(sender, e);
            e.Handled = true;
        }
    }

    // 설정 · 관리자 메뉴에서 Esc: 뒤로. 펼친 목록 등이 Esc 를 먼저 쓰면 (Handled) 여기까지 오지 않음
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !OnPage) return;
        e.Handled = true;
        PageBack_Click(sender, e);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        // 테두리 없는 창을 최대화하면 화면 밖으로 넘치는 만큼 안쪽으로
        BorderThickness = maximized ? new Thickness(7) : new Thickness(0);
    }

    protected override void OnClosed(EventArgs e)
    {
        _periodic.Stop();
        _runningTimer.Stop();
        _github.Dispose();
        DisposeTray();
        base.OnClosed(e);
    }

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
