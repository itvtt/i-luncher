using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ILauncher.Services;
using ILuncher.AppKit;

namespace ILauncher;

public partial class SettingsWindow : Window
{
    private readonly LauncherSettings _settings;
    private readonly string _version;

    public SettingsWindow(LauncherSettings settings, string version)
    {
        _settings = settings;
        _version = version;
        InitializeComponent();
        var user = settings.User;
        var build = settings.Build;
        ApiCatalogRadio.IsChecked = settings.CatalogMode == LauncherSettings.ModeApi;
        RepositoryCatalogRadio.IsChecked = settings.CatalogMode == LauncherSettings.ModeGitHub;
        LocalCatalogRadio.IsChecked = settings.CatalogMode == LauncherSettings.ModeLocal;
        ApiServerBox.Text = user.ApiServer ?? "";
        ApiServerHint.Text = build.ApiServer.Length > 0
            ? $"비우면 빌드 설정의 {build.ApiServer}. github.com 저장소의 릴리스도 이 서버의 GitHub 중계로 받아 토큰이 필요 없습니다."
            : "예: http://192.168.0.32:2003 (ApiServer 프로그램이 보여 주는 주소). github.com 저장소의 릴리스도 이 서버로 받아 토큰이 필요 없습니다.";
        AdminPasswordHint.Text = user.GetAdminPassword() is { Length: > 0 } ? "저장됨 (새로 입력하면 바뀜, 비워 두면 그대로)" : "저장된 비밀번호 없음 (목록 보기·설치에는 필요 없음)";
        CatalogRepositoryBox.Text = user.CatalogRepository ?? "";
        CatalogPathBox.Text = user.CatalogPath ?? "";
        CatalogBranchBox.Text = user.CatalogBranch ?? "";
        string buildRepository = build.CatalogRepository.Length > 0 ? build.CatalogRepository : build.LauncherRepository;
        CatalogRepositoryHint.Text = buildRepository.Length > 0
            ? $"비우면 빌드 설정의 {buildRepository} · {(build.CatalogPath.Length > 0 ? build.CatalogPath : LauncherSettings.DefaultCatalogPath)}"
            : "이 빌드에는 기본 저장소가 없습니다 (.env 의 LAUNCHER_REPOSITORY / CATALOG_REPOSITORY). 주소를 입력하거나 '이 PC에만 저장'을 고르세요.";
        TokenHostBox.Text = settings.CatalogRepository?.Host ?? settings.DefaultHost ?? "github.com";
        AutoUpdateBox.IsChecked = user.AutoUpdateLauncher;
        LauncherInfoText.Text = $"지금 버전 v{version}" + (settings.LauncherRepository is { } repository ? $" · 릴리스 저장소 {repository.WebUrl}" : " · 릴리스 저장소가 설정되지 않아 업데이트를 확인하지 않습니다")
                                + (AppUpdater.IsInstalledCopy("i-luncher") ? "" : "\n설치본이 아닌 사본(개발 빌드·압축 해제)은 자동 업데이트하지 않습니다. i-luncher-Setup.exe 로 설치하세요.");
        UpdateCatalogFields();
        UpdateTokenStatus();
    }

    /// <summary>설정을 저장한 뒤 바로 런처 업데이트를 확인할지</summary>
    public bool CheckLauncherUpdateNow { get; private set; }

    private string Host => GitHubRepository.NormalizeHost(TokenHostBox.Text.Length > 0 ? TokenHostBox.Text : "github.com");

    private void CatalogRadio_Checked(object sender, RoutedEventArgs e) => UpdateCatalogFields();

    private void UpdateCatalogFields()
    {
        if (RepositoryFields is null || ApiFields is null) return;
        RepositoryFields.IsEnabled = RepositoryCatalogRadio.IsChecked == true;
        RepositoryFields.Opacity = RepositoryFields.IsEnabled ? 1 : 0.5;
        ApiFields.IsEnabled = ApiCatalogRadio.IsChecked == true;
        ApiFields.Opacity = ApiFields.IsEnabled ? 1 : 0.5;
    }

    private void TokenHostBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTokenStatus();

    private void UpdateTokenStatus()
    {
        if (TokenStatusText is null) return;
        bool registered = _settings.HasUserToken(Host);
        bool builtIn = !registered && _settings.GetToken(Host) is { Length: > 0 };
        TokenStatusText.Text = registered ? $"{Host}: 토큰 등록됨 (새로 입력하면 바뀜)"
            : builtIn ? $"{Host}: 빌드에 들어간 읽기 전용 토큰을 씀 (앱 목록을 바꾸려면 쓰기 토큰 등록)"
            : $"{Host}: 토큰 없음 (공개 저장소만 읽을 수 있음)";
        RemoveTokenButton.IsEnabled = registered;
        var hosts = _settings.User.Tokens.Keys.OrderBy(host => host).ToList();
        RegisteredHostsText.Text = hosts.Count == 0 ? "" : "등록된 호스트: " + string.Join(", ", hosts);
    }

    private GitHubRepository? TargetRepository()
    {
        string text = CatalogRepositoryBox.Text.Trim();
        return text.Length > 0 ? GitHubRepository.Parse(text, _settings.DefaultHost) : _settings.CatalogRepository ?? _settings.LauncherRepository;
    }

    private async void TestTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (TargetRepository() is not { } repository)
        {
            TokenStatusText.Text = "확인할 저장소가 없습니다. 앱 목록 저장소 주소를 입력하세요.";
            return;
        }
        string typed = TokenBox.Password.Trim();
        string? Token(string host) => typed.Length > 0 && string.Equals(host, Host, StringComparison.OrdinalIgnoreCase) ? typed : _settings.GetToken(host);
        TestTokenButton.IsEnabled = false;
        TokenStatusText.Text = $"{repository.FullName} 확인 중…";
        try
        {
            using var client = new GitHubClient(Token, _version);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var info = await client.GetRepositoryAsync(repository, timeout.Token);
            TokenStatusText.Text = $"{info.FullName} 읽기 OK" + (info.Private ? " (비공개)" : "") +
                                   (info.CanPush switch { true => " · 쓰기 가능: 앱 목록을 바꿀 수 있습니다", false => " · 읽기 전용: 앱 목록은 바꿀 수 없습니다", _ => "" });
        }
        catch (Exception error) when (error is GitHubApiException or OperationCanceledException)
        {
            TokenStatusText.Text = error is OperationCanceledException ? "응답이 없습니다." : error.Message;
        }
        finally { TestTokenButton.IsEnabled = true; }
    }

    private void RemoveTokenButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.User.SetToken(Host, null);
        TokenBox.Password = "";
        UpdateTokenStatus();
    }

    private async void CheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.LauncherRepository is not { } repository) return;
        CheckNowButton.IsEnabled = false;
        try
        {
            var settings = _settings.ApiBaseFor(repository) is { } relay
                ? new UpdateSettings(relay, repository.FullName, null, "i-luncher-win-x64.zip", "i-luncher")
                : new UpdateSettings(repository.ApiBase, repository.FullName, _settings.GetToken(repository.TokenHost), "i-luncher-win-x64.zip", "i-luncher");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var update = await AppUpdater.CheckAsync(settings, timeout.Token);
            CheckLauncherUpdateNow = update is not null;
            LauncherInfoText.Text = update is null ? $"v{_version}: 최신 버전입니다." : $"새 버전 v{update.Version}이(가) 있습니다. 저장을 누르면 업데이트 안내가 표시됩니다.";
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            LauncherInfoText.Text = "확인하지 못했습니다: " + error.Message;
        }
        finally { CheckNowButton.IsEnabled = true; }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        string repositoryText = CatalogRepositoryBox.Text.Trim();
        if (RepositoryCatalogRadio.IsChecked == true && repositoryText.Length > 0 && !GitHubRepository.TryParse(repositoryText, out _, _settings.DefaultHost))
        {
            ErrorText.Text = "저장소 주소를 확인하세요. 예: https://github.company.com/team/i-luncher";
            return;
        }
        if (RepositoryCatalogRadio.IsChecked == true && repositoryText.Length == 0 && _settings.Build.CatalogRepository.Length == 0 && _settings.Build.LauncherRepository.Length == 0)
        {
            ErrorText.Text = "기본 저장소가 없는 빌드입니다. 저장소 주소를 입력하거나 '이 PC에만 저장'을 고르세요.";
            return;
        }
        string apiText = ApiServerBox.Text.Trim();
        if (apiText.Length > 0 && !(Uri.TryCreate(apiText, UriKind.Absolute, out var apiUri) && (apiUri.Scheme == Uri.UriSchemeHttp || apiUri.Scheme == Uri.UriSchemeHttps)))
        {
            ErrorText.Text = "API 서버 주소를 확인하세요. 예: http://192.168.0.32:2003";
            return;
        }
        if (ApiCatalogRadio.IsChecked == true && apiText.Length == 0 && _settings.Build.ApiServer.Length == 0)
        {
            ErrorText.Text = "API 서버 주소를 입력하세요.";
            return;
        }
        var user = _settings.User;
        user.ApiServer = apiText.Length > 0 ? new Uri(apiText).GetLeftPart(UriPartial.Authority) : null;
        user.CatalogMode = ApiCatalogRadio.IsChecked == true ? LauncherSettings.ModeApi
            : RepositoryCatalogRadio.IsChecked == true ? LauncherSettings.ModeGitHub : LauncherSettings.ModeLocal;
        user.UseLocalCatalog = LocalCatalogRadio.IsChecked == true;
        if (AdminPasswordBox.Password.Length > 0) user.SetAdminPassword(AdminPasswordBox.Password);
        user.CatalogRepository = repositoryText.Length > 0 ? GitHubRepository.Parse(repositoryText, _settings.DefaultHost)!.WebUrl : null;
        user.CatalogPath = CatalogPathBox.Text.Trim().Length > 0 ? CatalogPathBox.Text.Trim().Replace('\\', '/') : null;
        user.CatalogBranch = CatalogBranchBox.Text.Trim().Length > 0 ? CatalogBranchBox.Text.Trim() : null;
        user.AutoUpdateLauncher = AutoUpdateBox.IsChecked == true;
        if (TokenBox.Password.Trim().Length > 0) user.SetToken(Host, TokenBox.Password.Trim());
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }
}
