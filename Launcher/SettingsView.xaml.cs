using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 런처 오른쪽 화면의 설정 (일반 사용자용): 런처 자동 업데이트(누르면 바로 저장), 재설정 (목록 · 전체, 런처를 다시 시작).
/// GitHub 토큰은 프로젝트마다 앱 우클릭 → 토큰… 으로 넣음 (마스터 PC 는 이 PC 의 git 로그인을 자동으로 씀).
/// 서버 주소는 빌드 설정(API_SERVER)이 정하고, 앱 목록 같은 관리 일은 관리자 메뉴(마스터 계정)에서
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly LauncherSettings _settings;
    private readonly Action _save;
    private readonly Action<string> _reset;
    private readonly bool _loading = true;
    private string _pendingReset = "";

    public SettingsView(LauncherSettings settings, Action save, Action<string> reset)
    {
        _settings = settings;
        _save = save;
        _reset = reset;
        InitializeComponent();
        AutoUpdateBox.IsChecked = settings.User.AutoUpdateLauncher;
        DataFolderText.Text = settings.DataDirectory;
        _loading = false;
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
