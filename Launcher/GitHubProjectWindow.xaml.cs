using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// GitHub 프로젝트 추가 · 내 앱 편집.
/// 추가: 저장소 주소를 넣고 [확인] (또는 Enter) 하면 GitHub 에 바로 최신 릴리스와 설치 파일(zip · 설치 프로그램)을 확인
/// (비공개라 읽지 못하면 그 자리에서 이 프로젝트의 토큰을 받아 다시 확인. 토큰은 ResultToken 으로 돌려줘 저장소마다 저장).
/// 이름 · 분류 · 설명 · 아이콘을 정해 [추가]하면 Result 가 이 PC 의 내 앱 (Personal, 설치됨 탭).
/// 이미 있는 저장소 · 릴리스 없음 · 열 수 없는 저장소는 알려 줌.
/// 편집(editing): 저장소는 그대로, 나머지를 바꿔 [저장]. 아이콘 그림은 CustomIcons.Folder 에 복사해 둠.
/// (앱 페이지를 없애며 제작자 · 상세 설명 · 스크린샷 칸도 뺌. 예전에 넣은 값은 편집해도 그대로 남음)
/// </summary>
public partial class GitHubProjectWindow : Window
{
    private readonly AppManager _manager;
    private readonly string? _defaultHost;
    private readonly IReadOnlyList<string> _categories;
    private readonly Func<GitHubRepository, string?> _existingName;
    private readonly AppDefinition? _editing;
    private GitHubRepository? _checked;
    private CancellationTokenSource? _checking;

    public GitHubProjectWindow(AppManager manager, string? defaultHost, IReadOnlyList<string> categories, Func<GitHubRepository, string?> existingName,
        AppDefinition? editing = null)
    {
        _manager = manager;
        _defaultHost = defaultHost;
        _categories = categories;
        _existingName = existingName;
        _editing = editing;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        IconChoice.AutoText = "저장소 아이콘";
        IconChoice.AutoMissingText = "저장소에서 찾은 아이콘을 씁니다 (없으면 글자). 바꾸려면 기본 아이콘이나 이미지 파일을 고르세요.";
        IconChoice.Changed += (_, _) => { UpdatePreviewIcon(); Field_Changed(this, null); };

        if (editing is not null)
        {
            Title = "내 앱 편집";
            TitleText.Text = "내 앱 편집";
            SubtitleText.Text = "이름 · 분류 · 설명 · 아이콘을 바꿉니다. 저장소는 바꿀 수 없습니다.";
            AddButton.Content = "저장";
            RepoBox.Text = editing.Repository;
            RepoBox.IsReadOnly = true;
            CheckButton.Visibility = Visibility.Collapsed;
            _checked = GitHubRepository.Parse(editing.Repository, defaultHost);
            NameBox.Text = editing.Name;
            CategoryBox.Text = editing.Category;
            DescriptionBox.Text = editing.Description;
            IconChoice.Value = CustomIcons.IsCustom(editing.Icon) ? editing.Icon! : "";
            PreviewName.Text = _checked?.FullName ?? editing.Repository;
            PreviewVersion.Text = "확인 중";
            Details.Visibility = Visibility.Visible;
        }
        RenderCategoryChips();
        UpdatePreviewIcon();
        Field_Changed(this, null);
    }

    /// <summary>[추가] · [저장]한 앱 (내 앱)</summary>
    public AppDefinition? Result { get; private set; }

    /// <summary>비공개라 확인할 때 넣은 이 프로젝트의 토큰 (없으면 null)</summary>
    public string? ResultToken { get; private set; }

    private string? TypedToken => TokenRow.Visibility == Visibility.Visible && TokenBox.Password.Trim() is { Length: > 0 } token ? token : null;

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        TokenPlaceholder.Visibility = TokenBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_editing is null && _checked is not null)
        {
            _checked = null;
            Details.Visibility = Visibility.Collapsed;
            AddButton.IsEnabled = false;
        }
    }

    /// <summary>편집: 저장소 아이콘 (자동일 때 미리 보기, 없으면 null)</summary>
    public ImageSource? RepositoryIcon
    {
        get => IconChoice.AutoIcon;
        init { IconChoice.AutoIcon = value; UpdatePreviewIcon(); }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_editing is null)
        {
            RepoBox.Focus();
            return;
        }
        NameBox.Focus();
        NameBox.CaretIndex = NameBox.Text.Length;
        await CheckAsync(fillFields: false);
    }

    // ───────── 저장소 확인 ─────────

    private void RepoBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RepoPlaceholder.Visibility = RepoBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_editing is not null) return;
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
        if (e.Key != Key.Enter || _editing is not null) return;
        e.Handled = true;
        await CheckAsync(fillFields: true);
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync(fillFields: true);

    /// <summary>최신 릴리스 · 설치 파일 확인. fillFields = 이름 · 제작자 · 설명을 저장소 값으로 채움 (추가할 때)</summary>
    private async Task CheckAsync(bool fillFields)
    {
        var repository = _editing is not null ? _checked : GitHubRepository.Parse(RepoBox.Text.Trim(), _defaultHost);
        if (repository is null)
        {
            Say("저장소 주소를 알아보지 못했습니다. 예: https://github.com/소유자/저장소 또는 소유자/저장소");
            return;
        }
        if (_editing is null && _existingName(repository) is { } existing)
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
        string? token = TypedToken;
        // 토큰을 넣었으면 이 프로젝트는 그 토큰으로 확인 (저장은 [추가]할 때)
        using var withToken = token is null ? null : _manager.GitHub.WithToken(token);
        var github = withToken ?? _manager.GitHub;
        try
        {
            var release = await github.GetLatestReleaseAsync(repository, includePrerelease: false, cancel);
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
            if (fillFields)
            {
                try { description = (await github.GetRepositoryAsync(repository, cancel)).Description; }
                catch (Exception error) when (error is not OperationCanceledException) { }  // 설명은 없어도 됨
            }

            _checked = repository;
            ResultToken = token;
            PreviewName.Text = repository.FullName;
            PreviewVersion.Text = version;
            PreviewFile.Text = $"{asset.Name} · {SizeText(asset.Size)}" + (release.PublishedAt is { } at ? $" · {at.LocalDateTime:yyyy-MM-dd}" : "");
            PreviewFile.ToolTip = PreviewFile.Text;
            if (fillFields)
            {
                NameBox.Text = repository.Name;
                DescriptionBox.Text = description.Trim();
            }
            UpdatePreviewIcon();
            Details.Visibility = Visibility.Visible;
            Field_Changed(this, null);
            if (fillFields)
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }
        }
        catch (OperationCanceledException)
        {
            if (_checking?.Token != cancel) return;  // 그사이 다시 확인을 시작함
            Say("응답이 없습니다. 다시 누르세요.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // 비공개라 못 읽음: 이 프로젝트의 토큰을 받아 다시 확인. 그 밖(한도 초과 등)은 이유를 알려 줌
            if (_editing is null && error is GitHubApiException { Status: 401 or 403 or 404 } access && !access.Message.Contains("한도", StringComparison.Ordinal))
            {
                TokenRow.Visibility = Visibility.Visible;
                Say(token is null
                    ? $"{repository.FullName} 을(를) 읽지 못했습니다. 비공개 저장소면 이 프로젝트의 토큰을 넣고 다시 확인하세요."
                    : "이 토큰으로 읽지 못했습니다. 토큰과 읽기 권한을 확인하세요.");
                TokenBox.Focus();
            }
            else Say(error is GitHubApiException ? error.Message : $"{repository.FullName} 을(를) 확인하지 못했습니다: {error.Message}");
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
        if (_editing is not null)
        {
            // 편집: 릴리스를 확인하지 못해도 정보는 고칠 수 있음
            PreviewVersion.Text = "확인 못 함";
            PreviewFile.Text = text;
            PreviewFile.ToolTip = text;
            return;
        }
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>미리 보기 칸의 아이콘: 고른 아이콘 · 저장소 아이콘, 없으면 이름 첫 글자</summary>
    private void UpdatePreviewIcon()
    {
        if (PreviewImage is null) return;
        var tile = new AppCard(new AppDefinition { Id = "probe", Name = NameBox.Text.Trim() is { Length: > 0 } name ? name : _checked?.Name ?? "?", Repository = _checked?.WebUrl ?? "" });
        var image = CustomIcons.IsCustom(IconChoice.Value) ? CustomIcons.Load(IconChoice.Value) : IconChoice.AutoIcon;
        PreviewImage.Source = image;
        PreviewTile.Background = image is null ? tile.TileBrush : Brushes.Transparent;
        PreviewInitials.Text = image is null ? tile.Initials : "";
        IconChoice.FallbackText = tile.Initials;
        IconChoice.FallbackBrush = tile.TileBrush;
        IconChoice.Refresh();
    }

    // ───────── 이름 · 분류 · 설명 ─────────

    private void Field_Changed(object? sender, TextChangedEventArgs? e)
    {
        if (AddButton is null || IconChoice is null) return;
        DescriptionPlaceholder.Visibility = DescriptionBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (sender == NameBox) UpdatePreviewIcon();
        AddButton.IsEnabled = _checked is not null && NameBox.Text.Trim().Length > 0;
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
                chip.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
                chip.Foreground = Brushes.White;
            }
            chip.Click += (_, _) =>
            {
                CategoryBox.Text = category;
                CategoryBox.CaretIndex = category.Length;
            };
            CategoryChips.Children.Add(chip);
        }
    }

    // ───────── 추가 · 저장 · 닫기 ─────────

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_checked is not { } repository) return;
        var app = _editing?.Clone() ?? new AppDefinition
        {
            Id = "my-" + AppDefinition.CreateId(repository.Name),
            Repository = repository.WebUrl,
            InstallName = AppDefinition.SafeFolderName(repository.Name),
            Personal = true
        };
        app.Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : repository.Name;
        app.Category = CategoryBox.Text.Trim();
        app.Description = DescriptionBox.Text.Trim();
        app.Icon = IconChoice.Value.Length > 0 ? IconChoice.Value : null;
        app.Personal = true;
        Result = app;

        // 쓰지 않게 된 아이콘 그림 정리
        IconChoice.CleanUp(keep: app.Icon);
        if (_editing?.Icon is { } before && before != app.Icon) CustomIcons.DeleteImported(before);
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
        // 취소: 이번에 복사한 아이콘 그림만 지움 (편집 전 것은 그대로)
        if (Result is null) IconChoice.CleanUp(keep: _editing?.Icon);
        base.OnClosed(e);
    }
}
