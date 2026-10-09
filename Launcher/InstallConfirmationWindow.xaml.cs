using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ILauncher;

public partial class InstallConfirmationWindow : Window
{
    public InstallConfirmationWindow(string appName, ImageSource? iconSource)
    {
        InitializeComponent();
        AppNameText.Text = string.IsNullOrWhiteSpace(appName) ? "앱" : appName.Trim();
        AppNameText.ToolTip = AppNameText.Text;
        if (iconSource is null)
        {
            string initials = AppNameText.Text;
            AppInitialsText.Text = initials.Length > 0 ? initials[..1] : "앱";
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
