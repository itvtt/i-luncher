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
    private static readonly Brush SelectedNavBrush = MakeBrush(0x22, 0x2A, 0x3A);

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

    public MainWindow(LauncherSettings settings, string? startupMessage = null)
    {
        _settings = settings;
        _startupMessage = startupMessage;
        _favoriteStore = new FavoriteStore(Path.Combine(settings.DataDirectory, "favorites.json"));
        _favorites = _favoriteStore.Load();
        InitializeComponent();
        CreateServices();
        TitleVersionText.Text = "v" + LauncherVersion;
        _periodic = new DispatcherTimer(PeriodicRefresh, DispatcherPriority.Background, async (_, _) => await PeriodicRefreshAsync(), Dispatcher);
        _periodic.Stop();
    }

    public static string LauncherVersion => AppUpdater.CurrentVersion;
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
            LauncherSettings.ModeApi => new ApiCatalogStore(_settings.ApiServerUrl!, () => _settings.User.GetAdminPassword(),
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
        await RefreshAsync(checkReleases: true);
        _periodic.Start();
        _ = RefreshNoticesAsync();
        await CheckLauncherUpdateAsync(automatic: true);
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
            MergeCards(snapshot.Apps);
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

    private async void Card_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppCard card) await OpenAsync(card);
    }

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

    private void Launch(AppCard card)
    {
        if (card.Installed is not { } installed) return;
        try
        {
            _manager.Installer.Launch(card.App, installed);
            StatusText.Text = $"{card.Name} 실행";
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            StatusText.Text = $"{card.Name} 실행을 취소했습니다.";  // UAC 취소
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            ShowError($"{card.Name}을(를) 실행하지 못했습니다: {error.Message}");
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
        if (CardOf(sender) is not { Latest: { } release, IsBusy: false } card) return;
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
            UpdateCounts();
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

    // ───────────────────────── 앱 목록 추가 · 수정 · 삭제 ─────────────────────────

    private async void AddAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (_filter == "favorites") { EditFavorite(null); return; }
        var editor = new AppEditorWindow(null, _manager, _settings.DefaultHost, Categories()) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } app) return;
        await ChangeCatalogAsync(apps =>
        {
            string id = app.Id;
            for (int i = 2; apps.Any(existing => string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)); i++) id = $"{app.Id}-{i}";
            app.Id = id;
            if (app.Order == 0) app.Order = (apps.Count == 0 ? 0 : apps.Max(existing => existing.Order)) + 10;
            apps.Add(app);
            return $"i-luncher: {app.Name} 추가";
        }, $"{app.Name}을(를) 추가했습니다.");
    }

    private async void EditMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        var editor = new AppEditorWindow(card.App, _manager, _settings.DefaultHost, Categories()) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } app) return;
        await ChangeCatalogAsync(apps =>
        {
            int index = apps.FindIndex(existing => string.Equals(existing.Id, app.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) apps.Add(app); else apps[index] = app;
            return $"i-luncher: {app.Name} 수정";
        }, $"{app.Name}을(를) 수정했습니다.");
    }

    private async void DeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        string scope = _store is GitHubCatalogStore or ApiCatalogStore ? "공유 앱 목록에서 삭제합니다 (모든 사용자에게 적용)." : "이 PC의 앱 목록에서 삭제합니다.";
        var confirm = new InstallConfirmationWindow(card.Name, card.Icon, "목록에서 삭제", "이 앱을 목록에서 삭제할까요?",
            scope + (card.IsInstalled ? "\n이 PC에 설치된 앱은 지우지 않습니다. 먼저 '제거'를 하세요." : ""), "삭제") { Owner = this };
        if (confirm.ShowDialog() != true) return;
        string id = card.App.Id, name = card.App.Name;
        await ChangeCatalogAsync(apps =>
        {
            apps.RemoveAll(existing => string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
            return $"i-luncher: {name} 삭제";
        }, $"{name}을(를) 목록에서 삭제했습니다.");
    }

    private async Task ChangeCatalogAsync(Func<List<AppDefinition>, string> change, string done)
    {
        SetCatalogButtons(false);
        StatusText.Text = "앱 목록 저장 중…";
        try
        {
            var apps = await _store.UpdateAsync(change);
            MergeCards(apps);
            await DetectInstallsAsync(_cards.ToList());
            Render();
            StatusText.Text = done;
            HideError();
            await CheckReleasesAsync(_cards.Where(card => card.CheckedAt is null).ToList());
            _ = LoadRepositoryIconsAsync(_cards.ToList());
        }
        catch (GitHubApiException error) when (error.Status is 401 or 403 or 404 && _store is GitHubCatalogStore store)
        {
            StatusText.Text = "앱 목록을 저장하지 못했습니다.";
            ShowError($"앱 목록을 저장하려면 {store.Repository.FullName}에 쓰기 권한(contents: write)이 있는 토큰이 필요합니다. " +
                      $"설정에서 {store.Repository.Host} 토큰을 등록하세요. ({error.Message})");
        }
        catch (Exception error) when (error is GitHubApiException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText.Text = "앱 목록을 저장하지 못했습니다.";
            ShowError("앱 목록을 저장하지 못했습니다: " + error.Message);
        }
        finally
        {
            SetCatalogButtons(true);
        }
    }

    private void SetCatalogButtons(bool enabled)
    {
        AddAppButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
    }

    private IReadOnlyList<string> Categories() =>
        _cards.Select(card => card.Category).Where(category => category != "기타").Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(category => category).ToList();

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
            Application.Current.Shutdown();
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _launcherUpdating = false;
            LauncherUpdateButton.Visibility = Visibility.Visible;
            LauncherUpdateText.Text = $"i-luncher v{update.Version} 업데이트 실패: {error.Message}";
        }
    }

    // ───────────────────────── 화면 ─────────────────────────

    private void Render()
    {
        bool favorites = _filter == "favorites";
        foreach (var card in _cards) card.IsFavorite = _favorites.Any(item => string.Equals(item.AppId, card.App.Id, StringComparison.OrdinalIgnoreCase));
        FavoriteItems.Visibility = favorites ? Visibility.Visible : Visibility.Collapsed;
        AddAppText.Text = favorites ? "프로그램 추가" : "앱 추가";
        AddAppButton.ToolTip = favorites ? "프로그램 추가" : "앱 추가";
        if (favorites) { RenderFavorites(); return; }
        string search = SearchBox.Text.Trim();
        IEnumerable<AppCard> items = _cards.Where(card => card.Matches(search));
        items = _filter switch
        {
            "installed" => items.Where(card => card.IsInstalled),
            "updates" => items.Where(card => card.UpdateAvailable),
            var filter when filter.StartsWith("category:", StringComparison.Ordinal) =>
                items.Where(card => string.Equals(card.Category, filter[9..], StringComparison.CurrentCultureIgnoreCase)),
            _ => items
        };
        var visible = items.OrderBy(card => card.App.Order).ThenBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        LauncherItems.ItemsSource = visible;

        SectionTitle.Text = _filter switch
        {
            "installed" => "설치된 앱",
            "updates" => "업데이트",
            var filter when filter.StartsWith("category:", StringComparison.Ordinal) => filter[9..],
            _ => "전체 앱"
        };
        CountText.Text = $"{visible.Count}개 앱";
        RenderCategories();
        UpdateCounts();

        bool empty = visible.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CardScrollViewer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty) return;
        if (_cards.Count == 0)
        {
            EmptyStateTitle.Text = "아직 등록된 앱이 없습니다";
            EmptyStateText.Text = "";
            EmptyStateButton.Content = "앱 추가";
            EmptyStateButton.Tag = "add";
        }
        else
        {
            EmptyStateTitle.Text = "일치하는 앱이 없습니다";
            EmptyStateText.Text = "";
            EmptyStateButton.Content = "필터 지우기";
            EmptyStateButton.Tag = "clear";
        }
    }

    private void UpdateCounts()
    {
        AllCountText.Text = _cards.Count.ToString();
        InstalledCountText.Text = _cards.Count(card => card.IsInstalled).ToString();
        int updates = _cards.Count(card => card.UpdateAvailable);
        UpdatesCountText.Text = updates > 0 ? updates.ToString() : "";
        UpdateAllButton.Visibility = updates > 0 || _updatingAll ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllButton.IsEnabled = !_updatingAll;
        int favoriteCount = _favorites.Count(item => !item.IsApp) + _cards.Count(card => card.IsFavorite);
        FavoritesCountText.Text = favoriteCount > 0 ? favoriteCount.ToString() : "";
        foreach (var (button, filter) in new[] { (AllAppsButton, "all"), (InstalledAppsButton, "installed"), (UpdatesButton, "updates"), (FavoritesButton, "favorites") })
        {
            button.Background = _filter == filter ? SelectedNavBrush : Brushes.Transparent;
            button.Foreground = _filter == filter ? (Brush)FindResource("TextBrush") : (Brush)FindResource("MutedTextBrush");
        }
    }

    private void RenderCategories()
    {
        CategoryButtons.Children.Clear();
        foreach (var group in _cards.GroupBy(card => card.Category, StringComparer.CurrentCultureIgnoreCase).OrderBy(group => group.Key))
        {
            string filter = "category:" + group.Key;
            var button = new Button
            {
                Style = (Style)FindResource("SidebarNavButtonStyle"),
                Tag = filter,
                Content = new Grid
                {
                    Children =
                    {
                        new TextBlock { Text = group.Key, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 22, 0) },
                        new TextBlock { Text = group.Count().ToString(), HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11 }
                    }
                },
                Background = _filter == filter ? SelectedNavBrush : Brushes.Transparent,
                Foreground = _filter == filter ? (Brush)FindResource("TextBrush") : (Brush)FindResource("MutedTextBrush")
            };
            button.Click += FilterButton_Click;
            CategoryButtons.Children.Add(button);
        }
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string filter)
        {
            _filter = filter;
            Render();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Render();

    private void EmptyStateButton_Click(object sender, RoutedEventArgs e)
    {
        if (EmptyStateButton.Tag as string == "favorite-add") EditFavorite(null);
        else if (EmptyStateButton.Tag as string == "add") AddAppButton_Click(sender, e);
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

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // 마스터에게만 "마스터만 보기" (마스터는 숨긴 앱까지 모두 보므로 _cards = 전체 목록)
        bool master = _settings.Build.IsMaster(Environment.UserName);
        var window = new SettingsWindow(_settings, master ? _cards.Select(card => card.App).ToList() : null) { Owner = this };
        if (window.ShowDialog() != true) return;
        var masterOnly = window.MasterOnlyChanges;
        if (window.SettingsChanged)
        {
            _settings.SaveUser();
            CreateServices();
            _cards.Clear();
            await RefreshAsync(checkReleases: true);
            await RefreshNoticesAsync();
            await CheckLauncherUpdateAsync(automatic: false);
        }
        if (masterOnly.Count > 0)
            await ChangeCatalogAsync(apps =>
            {
                foreach (var app in apps)
                    if (masterOnly.TryGetValue(app.Id, out bool value)) app.MasterOnly = value;
                return "i-luncher: 마스터만 보기 변경";
            }, "마스터만 보기를 바꿨습니다.");
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

    // ───────────────────────── 카드 폭 ─────────────────────────

    private const double MinCardWidth = 268, MaxCardWidth = 400, CardGap = 14;

    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(284.0));

    /// <summary>카드 폭: 창 폭에 맞춰 열 수를 정하고 남는 공간 없이 채움</summary>
    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    private void CardScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardWidth();

    private void CardScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0) UpdateCardWidth();  // 스크롤바가 생기거나 없어질 때
    }

    private void UpdateCardWidth()
    {
        double available = CardScrollViewer.ViewportWidth > 0 ? CardScrollViewer.ViewportWidth : CardScrollViewer.ActualWidth - 14;
        if (available <= 0) return;
        int columns = Math.Max(1, (int)Math.Floor((available + 0.5) / (MinCardWidth + CardGap)));
        double width = Math.Floor(Math.Min(MaxCardWidth, available / columns - CardGap));
        if (Math.Abs(width - CardWidth) >= 1) CardWidth = Math.Max(200, width);
    }

    // ───────────────────────── 창 ─────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
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
        else if (e.Key == Key.F5)
        {
            RefreshButton_Click(sender, e);
            e.Handled = true;
        }
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
        _github.Dispose();
        base.OnClosed(e);
    }

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
