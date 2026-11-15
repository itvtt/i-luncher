using System.Windows;
using System.Windows.Controls;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 런처 오른쪽 화면의 설정 (일반 사용자용): 런처 자동 업데이트. 누르면 바로 저장.
/// 서버 주소는 빌드 설정(API_SERVER)이 정하고, 앱 목록 같은 관리 일은 관리자 메뉴(마스터 계정)에서
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly LauncherSettings _settings;
    private readonly Action _save;
    private bool _loading = true;

    public SettingsView(LauncherSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        InitializeComponent();
        AutoUpdateBox.IsChecked = settings.User.AutoUpdateLauncher;
        _loading = false;
    }

    private void AutoUpdateBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.User.AutoUpdateLauncher = AutoUpdateBox.IsChecked == true;
        _save();
    }
}
