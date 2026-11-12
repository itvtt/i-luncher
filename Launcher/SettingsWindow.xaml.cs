using System.Windows;
using System.Windows.Input;
using ILauncher.Services;

namespace ILauncher;

/// <summary>설정: API 서버 주소, 관리자 비밀번호(앱 목록 편집용), 런처 자동 업데이트</summary>
public partial class SettingsWindow : Window
{
    private readonly LauncherSettings _settings;
    private readonly string _apiServer;
    private readonly bool _autoUpdate;

    public SettingsWindow(LauncherSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        var user = settings.User;
        _apiServer = user.ApiServer ?? "";
        _autoUpdate = user.AutoUpdateLauncher;
        ApiServerBox.Text = _apiServer;
        ApiServerPlaceholder.Text = settings.Build.ApiServer.Length > 0 ? settings.Build.ApiServer : "http://<서버>:2003";
        AdminPasswordPlaceholder.Text = user.GetAdminPassword() is { Length: > 0 } ? "저장됨" : "앱 목록 편집용";
        AutoUpdateBox.IsChecked = _autoUpdate;
        Field_Changed(this, new RoutedEventArgs());
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (SaveButton is null) return;
        ApiServerPlaceholder.Visibility = ApiServerBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AdminPasswordPlaceholder.Visibility = AdminPasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = ApiServerBox.Text.Trim() != _apiServer || AdminPasswordBox.Password.Length > 0 || AutoUpdateBox.IsChecked != _autoUpdate;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
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
        if (AdminPasswordBox.Password.Length > 0) user.SetAdminPassword(AdminPasswordBox.Password);
        user.AutoUpdateLauncher = AutoUpdateBox.IsChecked == true;
        DialogResult = true;
    }
}
