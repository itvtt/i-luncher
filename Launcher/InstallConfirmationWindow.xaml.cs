using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ILauncher;

/// <summary>설치·제거 같은 확인 창 (앱 아이콘과 이름, 질문, 버전 같은 세부 정보)</summary>
public partial class InstallConfirmationWindow : Window
{
    public InstallConfirmationWindow(string appName, ImageSource? iconSource, string header = "앱 설치",
        string question = "이 앱을 설치할까요?", string? detail = null, string confirmText = "설치")
    {
        InitializeComponent();
        Title = header;
        HeaderText.Text = header;
        QuestionText.Text = question;
        InstallButton.Content = confirmText;
        InstallButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, confirmText);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            DetailText.Text = detail;
            DetailText.Visibility = Visibility.Visible;
        }
        AppNameText.Text = string.IsNullOrWhiteSpace(appName) ? "앱" : appName.Trim();
        AppNameText.ToolTip = AppNameText.Text;
        if (iconSource is null)
        {
            AppInitialsText.Text = AppNameText.Text[..1].ToUpperInvariant();
            AppInitialsText.Visibility = Visibility.Visible;
        }
        else
        {
            AppIconImage.Source = iconSource;
            AppIconImage.Visibility = Visibility.Visible;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        CancelButton.Focus();
        Keyboard.Focus(CancelButton);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InstallButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
