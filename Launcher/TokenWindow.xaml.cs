using System.Windows;
using System.Windows.Input;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 프로젝트(저장소)마다의 GitHub 토큰. 비공개 저장소를 읽을 때 런처와 그 앱의 자체 업데이터가 씀.
/// [저장]하면 그 저장소를 그 토큰으로 읽어 본 뒤 닫힘 (DialogResult true, Token = 넣은 토큰). [지우기]는 Token = null 로 닫힘
/// </summary>
public partial class TokenWindow : Window
{
    private readonly Func<string, Task> _verify;

    /// <param name="verify">토큰으로 그 저장소를 읽어 봄. 못 읽으면 GitHubApiException</param>
    public TokenWindow(AppCard card, GitHubRepository repository, bool hasToken, Func<string, Task> verify)
    {
        _verify = verify;
        InitializeComponent();
        AppNameText.Text = card.Name;
        AppNameText.ToolTip = card.Name;
        RepositoryText.Text = $"{repository.Host}/{repository.FullName}";
        if (card.Icon is { } icon)
        {
            IconImage.Source = icon;
            IconImage.Visibility = Visibility.Visible;
        }
        else
        {
            IconTile.Background = card.TileBrush;
            IconInitials.Text = card.Initials;
        }
        TokenPlaceholder.Text = hasToken ? "저장된 토큰이 있습니다. 바꾸려면 새 토큰 붙여넣기" : "이 저장소를 읽을 토큰 붙여넣기";
        ClearButton.Visibility = hasToken ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>저장할 토큰. [지우기]로 닫았으면 null</summary>
    public string? Token { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e) => TokenBox.Focus();

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        bool typed = TokenBox.Password.Trim().Length > 0;
        TokenPlaceholder.Visibility = typed ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = typed;
    }

    private void TokenBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SaveButton.IsEnabled) Save_Click(sender, e);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        string token = TokenBox.Password.Trim();
        if (token.Length == 0) return;
        SaveButton.IsEnabled = false;
        TokenBox.IsEnabled = false;
        StateText.Foreground = (System.Windows.Media.Brush)FindResource("DialogMutedBrush");
        StateText.Text = "확인 중…";
        try
        {
            await _verify(token);
            Token = token;
            DialogResult = true;
        }
        catch (GitHubApiException error)
        {
            // 못 읽는 토큰은 저장하지 않음 (이미 있던 토큰은 그대로)
            StateText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x8A));
            StateText.Text = error.Status == 401 ? "토큰이 올바르지 않거나 만료되었습니다." : error.Message;
            TokenBox.IsEnabled = true;
            SaveButton.IsEnabled = true;
            TokenBox.Focus();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Token = null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        DialogResult = false;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
