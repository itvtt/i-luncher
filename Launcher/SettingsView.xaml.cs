using System.Windows;
using System.Windows.Controls;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 런처 오른쪽 화면의 설정: API 서버 주소, 관리자 비밀번호(앱 목록 편집용), 런처 자동 업데이트, (마스터에게만) 마스터만 보는 앱.
/// 저장하면 Saved 로 알림 (적용은 런처 창이 함)
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly LauncherSettings _settings;
    private string _apiServer = "";
    private bool _autoUpdate;
    private readonly List<(string Id, bool Original, CheckBox Box)> _masterOnly = [];

    /// <summary>저장함: (API 서버·비밀번호·자동 업데이트가 바뀌었는지, 바꾼 마스터만 보기 앱 id → 새 값)</summary>
    public event Action<bool, Dictionary<string, bool>>? Saved;

    /// <param name="apps">마스터면 앱 목록 (마스터만 보기 고르기), 아니면 null</param>
    public SettingsView(LauncherSettings settings, IReadOnlyList<AppDefinition>? apps = null)
    {
        _settings = settings;
        InitializeComponent();
        if (apps is { Count: > 0 })
        {
            MasterOnlyPanel.Visibility = Visibility.Visible;
            foreach (var app in apps.OrderBy(app => app.Order).ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var box = new CheckBox { Content = app.Name, IsChecked = app.MasterOnly, FontSize = 13, Margin = new Thickness(0, 10, 0, 0) };
                box.Checked += Field_Changed;
                box.Unchecked += Field_Changed;
                MasterOnlyList.Children.Add(box);
                _masterOnly.Add((app.Id, app.MasterOnly, box));
            }
        }
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
        AdminPasswordBox.Password = "";
        AdminPasswordPlaceholder.Text = user.GetAdminPassword() is { Length: > 0 } ? "저장됨" : "비어 있음";
        AutoUpdateBox.IsChecked = _autoUpdate;
        ErrorText.Visibility = Visibility.Collapsed;
        Field_Changed(this, new RoutedEventArgs());
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (SaveButton is null) return;
        ApiServerPlaceholder.Visibility = ApiServerBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AdminPasswordPlaceholder.Visibility = AdminPasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = SettingsDirty || MasterOnlyChanges.Count > 0;
    }

    private bool SettingsDirty => ApiServerBox.Text.Trim() != _apiServer || AdminPasswordBox.Password.Length > 0 || AutoUpdateBox.IsChecked != _autoUpdate;

    /// <summary>바꾼 "마스터만 보기": 앱 id → 새 값</summary>
    private Dictionary<string, bool> MasterOnlyChanges =>
        _masterOnly.Where(item => (item.Box.IsChecked == true) != item.Original).ToDictionary(item => item.Id, item => item.Box.IsChecked == true, StringComparer.OrdinalIgnoreCase);

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
        bool changed = SettingsDirty;
        var masterOnly = MasterOnlyChanges;
        if (changed)
        {
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
        }
        foreach (var (id, _, box) in _masterOnly.ToList())
            if (masterOnly.ContainsKey(id))
            {
                int index = _masterOnly.FindIndex(item => item.Id == id);
                _masterOnly[index] = (id, box.IsChecked == true, box);
            }
        Load();
        Saved?.Invoke(changed, masterOnly);
    }
}
