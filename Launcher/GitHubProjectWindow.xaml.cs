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
/// 추가: 저장소 주소를 넣고 [확인] (또는 Enter) 하면 API 서버의 GitHub 중계로 최신 릴리스와 설치 파일(zip · 설치 프로그램)을 확인.
/// 이름 · 제작자 · 분류 · 설명 · 상세 설명 · 아이콘 · 스크린샷을 정해 [추가]하면 Result 가 이 PC 의 내 앱 (Personal).
/// 이미 있는 저장소 · 릴리스 없음 · 열 수 없는 저장소는 알려 줌.
/// 편집(editing): 저장소는 그대로, 나머지를 바꿔 [저장]. 스크린샷 · 아이콘 그림은 MyAppStore.MediaFolder · CustomIcons.Folder 에 복사해 둠
/// </summary>
public partial class GitHubProjectWindow : Window
{
    private const int MaxShots = 10;
    private readonly AppManager _manager;
    private readonly string? _defaultHost;
    private readonly IReadOnlyList<string> _categories;
    private readonly Func<GitHubRepository, string?> _existingName;
    private readonly AppDefinition? _editing;
    private readonly List<string> _shots = [];
    private readonly List<string> _importedShots = [];
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
            SubtitleText.Text = "앱 페이지에 보일 정보를 바꿉니다. 저장소는 바꿀 수 없습니다.";
            AddButton.Content = "저장";
            RepoBox.Text = editing.Repository;
            RepoBox.IsReadOnly = true;
            CheckButton.Visibility = Visibility.Collapsed;
            _checked = GitHubRepository.Parse(editing.Repository, defaultHost);
            NameBox.Text = editing.Name;
            AuthorBox.Text = editing.Author ?? "";
            CategoryBox.Text = editing.Category;
            DescriptionBox.Text = editing.Description;
            DetailsBox.Text = (editing.Details ?? "").Replace("\n", "\r\n");
            IconChoice.Value = CustomIcons.IsCustom(editing.Icon) ? editing.Icon! : "";
            _shots.AddRange(editing.Screenshots ?? []);
            PreviewName.Text = _checked?.FullName ?? editing.Repository;
            PreviewVersion.Text = "확인 중";
            Details.Visibility = Visibility.Visible;
        }
        RenderCategoryChips();
        RenderShots();
        UpdatePreviewIcon();
        Field_Changed(this, null);
    }

    /// <summary>[추가] · [저장]한 앱 (내 앱)</summary>
    public AppDefinition? Result { get; private set; }

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
            if (fillFields)
            {
                try { description = (await _manager.GitHub.GetRepositoryAsync(repository, cancel)).Description; }
                catch (Exception error) when (error is not OperationCanceledException) { }  // 설명은 없어도 됨
            }

            _checked = repository;
            PreviewName.Text = repository.FullName;
            PreviewVersion.Text = version;
            PreviewFile.Text = $"{asset.Name} · {SizeText(asset.Size)}" + (release.PublishedAt is { } at ? $" · {at.LocalDateTime:yyyy-MM-dd}" : "");
            PreviewFile.ToolTip = PreviewFile.Text;
            if (fillFields)
            {
                NameBox.Text = repository.Name;
                AuthorBox.Text = repository.Owner;
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

    // ───────── 이름 · 제작자 · 분류 · 설명 ─────────

    private void Field_Changed(object? sender, TextChangedEventArgs? e)
    {
        if (AddButton is null || IconChoice is null) return;
        DescriptionPlaceholder.Visibility = DescriptionBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AuthorPlaceholder.Visibility = AuthorBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsPlaceholder.Visibility = DetailsBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    // ───────── 스크린샷 ─────────

    private void AddShot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "스크린샷 고르기",
            Filter = "그림 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var problems = new List<string>();
        foreach (string file in dialog.FileNames)
        {
            if (_shots.Count >= MaxShots)
            {
                problems.Add($"스크린샷은 {MaxShots}장까지 넣습니다.");
                break;
            }
            try
            {
                if (Thumbnail(file, 10) is null) throw new InvalidDataException("그림으로 읽을 수 없습니다.");
                string copy = MyAppStore.ImportScreenshot(file);
                _importedShots.Add(copy);
                _shots.Add(copy);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problems.Add($"{Path.GetFileName(file)}: {error.Message}");
            }
        }
        RenderShots();
        ShotsEmpty.Text = problems.Count > 0 ? string.Join("\n", problems) : ShotsEmpty.Text;
        if (problems.Count > 0) ShotsEmpty.Visibility = Visibility.Visible;
    }

    /// <summary>스크린샷 줄: 작은 그림 + 오른쪽 위 × (빼기)</summary>
    private void RenderShots()
    {
        ShotStrip.Children.Clear();
        ShotsEmpty.Text = "없음 · 앱 페이지에 보일 화면 그림을 넣을 수 있습니다.";
        ShotsEmpty.Visibility = _shots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (string shot in _shots.ToList())
        {
            var frame = new Grid { Width = 150, Height = 94, Margin = new Thickness(0, 0, 8, 8), ToolTip = Path.GetFileName(shot) };
            var picture = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)),
                BorderThickness = new Thickness(1),
                Background = Thumbnail(shot, 188) is { } image
                    ? new ImageBrush(image) { Stretch = Stretch.UniformToFill }
                    : new SolidColorBrush(Color.FromRgb(0x26, 0x27, 0x2B))
            };
            var remove = new Button
            {
                Style = (Style)FindResource("DialogCloseStyle"),
                Width = 24, Height = 24, Margin = new Thickness(0, 5, 5, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x14, 0x14, 0x16)),
                ToolTip = "빼기",
                Content = new System.Windows.Shapes.Path
                {
                    Width = 8, Height = 8, Stroke = Brushes.White, StrokeThickness = 1.4,
                    Data = Geometry.Parse("M 0.5,0.5 L 7.5,7.5 M 7.5,0.5 L 0.5,7.5")
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, "스크린샷 빼기");
            // 닫기 단추 모양은 바탕이 투명하므로 둥근 판을 따로 깔아 그림 위에서도 보이게
            var plate = new Border
            {
                Width = 24, Height = 24, Margin = remove.Margin, CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x14, 0x14, 0x16)), IsHitTestVisible = false
            };
            remove.Click += (_, _) =>
            {
                _shots.Remove(shot);
                RenderShots();
            };
            frame.Children.Add(picture);
            frame.Children.Add(plate);
            frame.Children.Add(remove);
            ShotStrip.Children.Add(frame);
        }
    }

    private static BitmapSource? Thumbnail(string path, int height)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelHeight = height;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException or FileFormatException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException)
        {
            return null;
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
        app.Author = AuthorBox.Text.Trim() is { Length: > 0 } author ? author : null;
        app.Category = CategoryBox.Text.Trim();
        app.Description = DescriptionBox.Text.Trim();
        app.Details = DetailsBox.Text.Replace("\r\n", "\n").Trim() is { Length: > 0 } details ? details : null;
        app.Icon = IconChoice.Value.Length > 0 ? IconChoice.Value : null;
        app.Screenshots = _shots.Count > 0 ? [.. _shots] : null;
        app.Personal = true;
        Result = app;

        // 쓰지 않게 된 그림 정리: 이번에 넣었다 뺀 것, 편집 전에 있다가 뺀 것 · 바꾼 아이콘
        foreach (string gone in _importedShots.Concat(_editing?.Screenshots ?? []).Where(shot => !_shots.Contains(shot)).ToList()) MyAppStore.DeleteMedia(gone);
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
        if (Result is null)
        {
            // 취소: 이번에 복사한 그림만 지움 (편집 전 것은 그대로)
            foreach (string shot in _importedShots) MyAppStore.DeleteMedia(shot);
            IconChoice.CleanUp(keep: _editing?.Icon);
        }
        base.OnClosed(e);
    }
}
