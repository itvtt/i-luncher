using System.Windows;
using System.Windows.Controls;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 런처 오른쪽 화면의 설정 (일반 사용자용): 서버 주소, 런처 자동 업데이트.
/// 앱 목록 · 마스터만 보기 같은 관리 일은 관리자 메뉴(마스터 계정)에서. 저장하면 Saved 로 알림 (적용은 런처 창이 함)
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly LauncherSettings _settings;
    private string _apiServer = "";
    private bool _autoUpdate;

    /// <summary>저장함 (서버 주소나 자동 업데이트가 바뀜)</summary>
    public event Action? Saved;

    public SettingsView(LauncherSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Load();
    }

    /// <summary>저장된 값으로 채움</summary>
    private void Load()
    {
        var user = _settings.User;
        _apiServer = user.ApiServer ?? "";
        _autoUpdate = user.AutoUpdateLauncher;
        ApiServerBox.Text = _apiServer;
        ApiServerPlaceholder.Text = _settings.Build.ApiServer.Length > 0 ? _settings.Build.ApiServer : "http://<서버>:2003";
        AutoUpdateBox.IsChecked = _autoUpdate;
        ErrorText.Visibility = Visibility.Collapsed;
        Field_Changed(this, new RoutedEventArgs());
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (SaveButton is null) return;
        ApiServerPlaceholder.Visibility = ApiServerBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = ApiServerBox.Text.Trim() != _apiServer || AutoUpdateBox.IsChecked != _autoUpdate;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string apiText = ApiServerBox.Text.Trim();
        Uri? apiUri = null;
        if (apiText.Length > 0 && !(Uri.TryCreate(apiText, UriKind.Absolute, out apiUri) && (apiUri.Scheme == Uri.UriSchemeHttp || apiUri.Scheme == Uri.UriSchemeHttps)))
        {
            ErrorText.Text = "주소를 확인하세요. 예: http://<서버>:2003";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        var user = _settings.User;
        user.ApiServer = apiUri?.GetLeftPart(UriPartial.Authority);
        // 앱 목록 위치는 더 고르지 않음: API 서버가 있으면 그 목록 (예전에 고른 저장소·이 PC 목록은 버림)
        if (_settings.ApiServerUrl is not null)
        {
            user.CatalogMode = null;
            user.UseLocalCatalog = false;
        }
        user.AutoUpdateLauncher = AutoUpdateBox.IsChecked == true;
        Load();
        Saved?.Invoke();
    }
}
