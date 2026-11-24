using System.Diagnostics;
using System.Windows;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 런처 오른쪽 화면의 설정: 런처 자동 업데이트(누르면 바로 저장), 재설정 (목록 · 전체, 런처를 다시 시작).
/// 마스터만: API 서버 주소 (서버 목록 · 스크린샷 · 앱 사용 기록이 가는 곳, 비우면 빌드 설정 API_SERVER), 런처 공지를 GitHub 에서 편집.
/// GitHub 토큰은 프로젝트마다 앱 우클릭 → 토큰… 으로 넣음 (마스터 PC 는 이 PC 의 git 로그인을 자동으로 씀).
/// API 서버의 관리(앱 목록 · 릴리스 · 러너 · 공지 · API 켜고 끄기)는 서버 PC 의 i-api.exe 관리 창에서
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly LauncherSettings _settings;
    private readonly Action _save;
    private readonly Action<string> _reset;
    private readonly Func<string, string?>? _changeServer;
    private readonly Func<Task<string?>>? _noticesEditUrl;
    private readonly bool _loading = true;
    private string _pendingReset = "";

    /// <param name="changeServer">마스터만: 주소를 바꿈 ("" = 빌드 설정의 주소). 틀린 주소면 null, 아니면 지금 쓰는 주소</param>
    /// <param name="noticesEditUrl">마스터만: 런처 공지를 GitHub 에서 고치는 페이지</param>
    public SettingsView(LauncherSettings settings, Action save, Action<string> reset, Func<string, string?>? changeServer = null, Func<Task<string?>>? noticesEditUrl = null)
    {
        _settings = settings;
        _save = save;
        _reset = reset;
        _changeServer = changeServer;
        _noticesEditUrl = noticesEditUrl;
        InitializeComponent();
        AutoUpdateBox.IsChecked = settings.User.AutoUpdateLauncher;
        DataFolderText.Text = settings.DataDirectory;
        if (changeServer is not null)
        {
            MasterSection.Visibility = Visibility.Visible;
            ApiServerBox.Text = settings.User.ApiServer ?? "";
            ShowServer(null);
        }
        _loading = false;
    }

    /// <summary>주소 칸 아래: 지금 쓰는 주소와 (확인했으면) 응답</summary>
    private void ShowServer(bool? answers)
    {
        string current = _settings.ApiServerUrl ?? "없음";
        ApiServerHint.Text = answers switch
        {
            true => $"연결했습니다: {current}",
            false => $"응답이 없습니다: {current} (서버 PC 에서 i-api 가 켜져 있는지 확인하세요)",
            _ => $"지금 {current} · IP:포트로 씀" + (_settings.BuildApiServerUrl is not null ? ", 비우면 빌드 설정의 주소" : ""),
        };
        ApiServerHint.Foreground = answers == false ? new SolidColorBrush(Color.FromRgb(0xE6, 0xC0, 0x7A)) : new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9D));
    }

    private void ApiServerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Connect_Click(sender, e);
    }

    /// <summary>[연결]: 주소를 저장하고 (런처가 서버 목록을 새 주소에서 다시 읽음) 그 서버가 답하는지 확인</summary>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_changeServer is null) return;
        if (_changeServer(ApiServerBox.Text) is not { } url)
        {
            ApiServerHint.Text = "주소를 IP:포트로 쓰세요. 예: 192.168.0.10:2003 · https://서버";
            ApiServerHint.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xC0, 0x7A));
            ApiServerBox.Focus();
            return;
        }
        ApiServerBox.Text = _settings.User.ApiServer ?? "";
        if (url.Length == 0)
        {
            ShowServer(null);
            return;
        }
        ConnectButton.IsEnabled = false;
        bool answers = false;
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await http.GetAsync(url.TrimEnd('/') + "/health");
            answers = response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
        finally { ConnectButton.IsEnabled = true; }
        ShowServer(answers);
    }

    private async void EditNotices_Click(object sender, RoutedEventArgs e)
    {
        string? url = _noticesEditUrl is null ? null : await _noticesEditUrl();
        if (url is null)
        {
            ApiServerHint.Text = "공지 저장소를 알 수 없습니다 (빌드 설정 LAUNCHER_REPOSITORY · NOTICES_REPOSITORY).";
            return;
        }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void AutoUpdateBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.User.AutoUpdateLauncher = AutoUpdateBox.IsChecked == true;
        _save();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.DataDirectory}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    // 재설정: 누르면 그 줄 아래에 확인 (창을 띄우지 않음), 확인하면 런처를 다시 시작하며 지움
    private void ResetList_Click(object sender, RoutedEventArgs e) => AskReset(DataFolder.ResetList);
    private void ResetAll_Click(object sender, RoutedEventArgs e) => AskReset(DataFolder.ResetAll);

    private void AskReset(string scope)
    {
        _pendingReset = scope;
        ConfirmText.Text = scope == DataFolder.ResetAll ? "전체를 지우고 다시 시작합니다." : "목록을 되돌리고 다시 시작합니다.";
        ConfirmPanel.Visibility = Visibility.Visible;
        ResetListButton.IsEnabled = ResetAllButton.IsEnabled = false;
        ConfirmButton.Focus();
    }

    private void CancelReset_Click(object sender, RoutedEventArgs e)
    {
        _pendingReset = "";
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ResetListButton.IsEnabled = ResetAllButton.IsEnabled = true;
    }

    private void ConfirmReset_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingReset.Length == 0) return;
        ConfirmButton.IsEnabled = false;
        _reset(_pendingReset);
    }
}
