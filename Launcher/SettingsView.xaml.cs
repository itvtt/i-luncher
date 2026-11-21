using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 런처 오른쪽 화면의 설정 (일반 사용자용): 런처 자동 업데이트(누르면 바로 저장), 내 GitHub 토큰.
/// 토큰은 GitHub 에 맞는지 물어본 뒤 저장하고, 저장 · 지우면 새 버전을 다시 확인함 (비공개 저장소 추가 · 업데이트 확인에 씀).
/// 서버 주소는 빌드 설정(API_SERVER)이 정하고, 앱 목록 같은 관리 일은 관리자 메뉴(마스터 계정)에서
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly LauncherSettings _settings;
    private readonly Action _save;
    private readonly string _tokenHost;
    private readonly Func<string, string, Task<string>> _verifyToken;
    private readonly Action _tokenChanged;
    private bool _loading = true;

    /// <param name="verifyToken">(호스트, 토큰) → 토큰 주인의 계정 이름. 틀리면 GitHubApiException</param>
    /// <param name="tokenChanged">토큰을 저장하거나 지운 뒤 (새 버전 다시 확인)</param>
    public SettingsView(LauncherSettings settings, Action save, string tokenHost, Func<string, string, Task<string>> verifyToken, Action tokenChanged)
    {
        _settings = settings;
        _save = save;
        _tokenHost = tokenHost;
        _verifyToken = verifyToken;
        _tokenChanged = tokenChanged;
        InitializeComponent();
        AutoUpdateBox.IsChecked = settings.User.AutoUpdateLauncher;
        TokenPlaceholder.Text = $"{tokenHost} 토큰 붙여넣기";
        ShowTokenState(settings.HasUserToken(tokenHost) ? "토큰 등록됨" : null);
        _loading = false;
    }

    private void AutoUpdateBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.User.AutoUpdateLauncher = AutoUpdateBox.IsChecked == true;
        _save();
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        bool typed = TokenBox.Password.Trim().Length > 0;
        TokenPlaceholder.Visibility = typed ? Visibility.Collapsed : Visibility.Visible;
        SaveTokenButton.IsEnabled = typed;
    }

    private void TokenBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SaveTokenButton.IsEnabled) SaveToken_Click(sender, e);
    }

    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        string token = TokenBox.Password.Trim();
        if (token.Length == 0) return;
        SaveTokenButton.IsEnabled = false;
        TokenBox.IsEnabled = false;
        ShowTokenState("확인 중…", canClear: false);
        try
        {
            string login = await _verifyToken(_tokenHost, token);
            _settings.User.SetToken(_tokenHost, token);
            _save();
            TokenBox.Clear();
            ShowTokenState(login.Length > 0 ? $"{login} 토큰 등록됨" : "토큰 등록됨");
            _tokenChanged();
        }
        catch (GitHubApiException error)
        {
            // 틀린 토큰은 저장하지 않음 (이미 있던 토큰은 그대로). 연결 실패 · 30초 초과도 이 예외
            ShowTokenState(error.Status == 401 ? "토큰이 올바르지 않거나 만료되었습니다." : error.Message, _settings.HasUserToken(_tokenHost));
        }
        finally
        {
            TokenBox.IsEnabled = true;
            SaveTokenButton.IsEnabled = TokenBox.Password.Trim().Length > 0;
            TokenBox.Focus();
        }
    }

    private void ClearToken_Click(object sender, RoutedEventArgs e)
    {
        _settings.User.SetToken(_tokenHost, null);
        _save();
        ShowTokenState(null);
        _tokenChanged();
    }

    /// <summary>토큰 칸 아래 한 줄 (없으면 숨김). 지우기는 저장된 토큰이 있을 때만</summary>
    private void ShowTokenState(string? text, bool? canClear = null)
    {
        TokenStatePanel.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        TokenStatus.Text = text ?? "";
        ClearTokenButton.Visibility = (canClear ?? _settings.HasUserToken(_tokenHost)) ? Visibility.Visible : Visibility.Collapsed;
    }
}
