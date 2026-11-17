using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// GitHub 프로젝트 추가: 저장소 주소를 넣고 [확인] (또는 Enter) 하면 API 서버의 GitHub 중계로 최신 릴리스와 설치 파일(zip · 설치 프로그램)을 확인.
/// 이름 · 분류 · 설명을 정해 [추가]하면 Result 가 이 PC 의 내 앱 (Personal). 이미 있는 저장소 · 릴리스 없음 · 열 수 없는 저장소는 알려 줌
/// </summary>
public partial class GitHubProjectWindow : Window
{
    private readonly AppManager _manager;
    private readonly string? _defaultHost;
    private readonly IReadOnlyList<string> _categories;
    private readonly Func<GitHubRepository, string?> _existingName;
    private GitHubRepository? _checked;
    private CancellationTokenSource? _checking;

    public GitHubProjectWindow(AppManager manager, string? defaultHost, IReadOnlyList<string> categories, Func<GitHubRepository, string?> existingName)
    {
        _manager = manager;
        _defaultHost = defaultHost;
        _categories = categories;
        _existingName = existingName;
        InitializeComponent();
        RenderCategoryChips();
    }

    /// <summary>[추가]한 앱 (내 앱)</summary>
    public AppDefinition? Result { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e) => RepoBox.Focus();

    // ───────── 저장소 확인 ─────────

    private void RepoBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RepoPlaceholder.Visibility = RepoBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 주소를 고치면 다시 확인해야 함
        if (_checked is not null)
        {
            _checked = null;
            Details.Visibility = Visibility.Collapsed;
            AddButton.IsEnabled = false;
        }
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private async void RepoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await CheckAsync();
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        var repository = GitHubRepository.Parse(RepoBox.Text.Trim(), _defaultHost);
        if (repository is null)
        {
            Say("저장소 주소를 알아보지 못했습니다. 예: https://github.com/소유자/저장소 또는 소유자/저장소");
            return;
        }
        if (_existingName(repository) is { } existing)
        {
            Say($"이미 홈 화면에 있는 앱입니다: {existing}");
            return;
        }
        _checking?.Cancel();
        _checking = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancel = _checking.Token;
        CheckButton.IsEnabled = false;
        CheckButton.Content = "확인 중…";
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var release = await _manager.GitHub.GetLatestReleaseAsync(repository, includePrerelease: false, cancel);
            if (release is null)
            {
                Say($"{repository.FullName} 에 릴리스가 없습니다. 설치할 수 있게 릴리스(zip 또는 설치 프로그램)를 먼저 올려 주세요.");
                return;
            }
            var probe = new AppDefinition { Id = "probe", Name = repository.Name, Repository = repository.WebUrl };
            var asset = AppManager.SelectZipAsset(probe, release) ?? AppManager.SelectSetupAsset(probe, release);
            string version = release.Version is { } v ? $"v{v}" : release.Tag;
            if (asset is null)
            {
                Say($"최신 릴리스({version})에 설치할 파일(win-x64 zip 또는 설치 프로그램)이 없습니다.");
                return;
            }
            string description = "";
            try { description = (await _manager.GitHub.GetRepositoryAsync(repository, cancel)).Description; }
            catch (Exception error) when (error is not OperationCanceledException) { }  // 설명은 없어도 됨

            _checked = repository;
            var tile = new AppCard(probe);
            PreviewTile.Background = tile.TileBrush;
            PreviewInitials.Text = tile.Initials;
            PreviewName.Text = repository.FullName;
            PreviewVersion.Text = version;
            PreviewFile.Text = $"{asset.Name} · {SizeText(asset.Size)}" + (release.PublishedAt is { } at ? $" · {at.LocalDateTime:yyyy-MM-dd}" : "");
            PreviewFile.ToolTip = PreviewFile.Text;
            NameBox.Text = repository.Name;
            DescriptionBox.Text = description.Trim();
            Details.Visibility = Visibility.Visible;
            AddButton.IsEnabled = true;
            NameBox.Focus();
            NameBox.SelectAll();
        }
        catch (OperationCanceledException)
        {
            if (_checking?.Token != cancel) return;  // 그사이 다시 확인을 시작함
            Say("응답이 없습니다. API 서버가 켜져 있는지 확인하고 다시 누르세요.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // API 서버는 앱 목록 · LAUNCHER_GITHUB_REPOS · 서버 토큰 계정의 저장소만 열어 줌
            Say(error.Message.Contains("열려 있지 않습니다")
                ? $"{repository.FullName} 은(는) API 서버가 열어 주지 않는 저장소입니다. 관리자에게 서버 설정 LAUNCHER_GITHUB_REPOS 에 넣어 달라고 하세요."
                : error.Message.Contains("404") || error.Message.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
                    ? $"{repository.FullName} 저장소를 찾지 못했습니다. 주소를 확인하세요."
                    : $"{repository.FullName} 을(를) 확인하지 못했습니다: {error.Message}");
        }
        finally
        {
            CheckButton.IsEnabled = true;
            CheckButton.Content = "확인";
        }
    }

    private static string SizeText(long bytes) => bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private void Say(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    // ───────── 이름 · 분류 · 설명 ─────────

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (DescriptionPlaceholder is not null) DescriptionPlaceholder.Visibility = DescriptionBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (AddButton is not null) AddButton.IsEnabled = _checked is not null && NameBox.Text.Trim().Length > 0;
    }

    private void Category_Changed(object sender, TextChangedEventArgs e) => RenderCategoryChips();

    /// <summary>분류 칸 아래: 지금 있는 분류 (누르면 그 분류, 고른 것은 파랑)</summary>
    private void RenderCategoryChips()
    {
        if (CategoryChips is null) return;
        CategoryPlaceholder.Visibility = CategoryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CategoryChips.Children.Clear();
        string current = CategoryBox.Text.Trim();
        foreach (string category in _categories)
        {
            bool on = string.Equals(category, current, StringComparison.CurrentCultureIgnoreCase);
            var chip = new Button { Content = category, Style = (Style)FindResource("DialogChipStyle") };
            if (on)
            {
                chip.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF));
                chip.Foreground = System.Windows.Media.Brushes.White;
            }
            chip.Click += (_, _) =>
            {
                CategoryBox.Text = category;
                CategoryBox.CaretIndex = category.Length;
            };
            CategoryChips.Children.Add(chip);
        }
    }

    // ───────── 추가 · 닫기 ─────────

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_checked is not { } repository) return;
        Result = new AppDefinition
        {
            Id = "my-" + AppDefinition.CreateId(repository.Name),
            Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : repository.Name,
            Repository = repository.WebUrl,
            Description = DescriptionBox.Text.Trim(),
            Category = CategoryBox.Text.Trim(),
            InstallName = AppDefinition.SafeFolderName(repository.Name),
            Personal = true
        };
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
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }

    protected override void OnClosed(EventArgs e)
    {
        _checking?.Cancel();
        base.OnClosed(e);
    }
}
