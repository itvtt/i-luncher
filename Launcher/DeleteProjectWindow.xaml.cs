using System.Windows;
using System.Windows.Input;

namespace ILauncher;

/// <summary>
/// 내 앱(GitHub 프로젝트로 더한 앱) 삭제 확인. 설치되어 있으면 "목록에서만 빼기"(처음 고름) / "설치된 앱도 제거"를 고름.
/// [삭제]하면 DialogResult true, Uninstall 은 설치된 앱도 제거할지
/// </summary>
public partial class DeleteProjectWindow : Window
{
    public DeleteProjectWindow(AppCard card)
    {
        InitializeComponent();
        AppNameText.Text = card.Name;
        AppNameText.ToolTip = card.Name;
        RepositoryText.Text = GitHubName(card.App.Repository);
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
        if (card.Installed is { } installed)
        {
            QuestionText.Text = "홈 화면에서 뺄까요? 설치된 앱은 어떻게 할지 고르세요.";
            UninstallDetail.Text = installed.ManagedByLauncher
                ? $"설치 폴더를 지웁니다: {installed.Directory}\n폴더 밖에 저장된 데이터와 설정은 남습니다."
                : "앱의 제거 프로그램을 실행합니다.";
            Choices.Visibility = Visibility.Visible;
        }
        else QuestionText.Text = "홈 화면에서 뺄까요? 설치되어 있지 않아 지울 파일은 없습니다.";
    }

    /// <summary>설치된 앱도 제거</summary>
    public bool Uninstall => Choices.Visibility == Visibility.Visible && UninstallToo.IsChecked == true;

    private static string GitHubName(string repository) =>
        Services.GitHubRepository.Parse(repository) is { } parsed ? $"{parsed.Host}/{parsed.FullName}" : repository;

    private void Window_Loaded(object sender, RoutedEventArgs e) => CancelButton.Focus();

    private void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;

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
