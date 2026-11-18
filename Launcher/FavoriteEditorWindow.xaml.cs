using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 즐겨찾기 추가 · 편집.
///   이 PC 프로그램: 설치된 프로그램에서 고르거나 경로를 직접 (실행 파일·바로 가기·아무 파일·폴더), 아이콘은 자동 = 그 파일의 아이콘.
///   웹사이트: 주소 → [확인](또는 Enter)으로 그 사이트에서 이름 · 아이콘을 가져옴. 못 가져오면 기본 아이콘 · 이미지 파일에서 고름.
/// </summary>
public partial class FavoriteEditorWindow : Window
{
    private sealed record ProgramItem(string Name, string Path);

    private readonly FavoriteProgram? _editing;
    private readonly IReadOnlyList<string> _categories;
    private readonly bool _web;
    private List<ProgramItem> _programs = [];
    private CancellationTokenSource? _fetching;
    // 가져온 사이트 아이콘 (그 주소일 때만 저장)
    private string? _fetchedAddress;
    private byte[]? _fetchedIcon;
    // 웹사이트 확인 결과: null = 아직, "none" = 아이콘 없음, "failed" = 연결 못 함
    private string? _siteState;

    /// <param name="categories">지금 있는 분류 (분류 칸 아래 고르기 버튼)</param>
    /// <param name="web">웹사이트 추가 (편집은 항목을 보고 정함)</param>
    public FavoriteEditorWindow(FavoriteProgram? editing, IReadOnlyList<string>? categories = null, bool web = false)
    {
        _editing = editing;
        _categories = categories ?? [];
        _web = editing?.IsWeb ?? web;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 24;

        if (_web)
        {
            HeaderGlyph.Text = "\uE774";
            TitleText.Text = editing is null ? "웹사이트 추가" : "웹사이트 편집";
            SubtitleText.Text = "주소를 넣고 확인을 누르면 사이트 이름과 아이콘을 가져옵니다.";
            PickerPanel.Visibility = Visibility.Collapsed;
            ArgumentsPanel.Visibility = Visibility.Collapsed;
            PathLabel.Text = "주소";
            PathPlaceholder.Text = "https://example.com";
            PathButton.Content = "확인";
            DescriptionPlaceholder.Text = "카드에 보일 한 줄 (비우면 주소)";
            IconChoice.AutoText = "사이트 아이콘";
        }
        else
        {
            PathPlaceholder.Text = "프로그램 · 파일 · 폴더 경로";
            IconChoice.AutoText = "프로그램 아이콘";
            if (editing is not null)
            {
                TitleText.Text = "프로그램 편집";
                SubtitleText.Text = "이름 · 경로 · 아이콘 · 분류를 바꿉니다.";
                PickerPanel.Visibility = Visibility.Collapsed;
            }
        }
        if (editing is not null)
        {
            SaveButton.Content = "저장";
            NameBox.Text = editing.Name;
            PathBox.Text = editing.Path;
            ArgumentsBox.Text = editing.Arguments;
            CategoryBox.Text = editing.Category;
            DescriptionBox.Text = editing.Description;
            IconChoice.Value = editing.Icon;
        }
        UpdateAutoIcon();
        IconChoice.Changed += (_, _) => Field_Changed(this, new RoutedEventArgs());
        RenderCategoryChips();
        Field_Changed(this, new RoutedEventArgs());

        Loaded += async (_, _) =>
        {
            if (_web && _editing is null && InitialAddress is { Length: > 0 } initial)
            {
                PathBox.Text = initial;
                await FetchSiteAsync();
                NameBox.Focus();
                NameBox.SelectAll();
                return;
            }
            if (_editing is not null || _web) { (_web && _editing is null ? PathBox : NameBox).Focus(); return; }
            FilterBox.Focus();
            _programs = (await Task.Run(InstalledPrograms.Scan)).Select(item => new ProgramItem(item.Name, item.Path)).ToList();
            ApplyFilter();
        };
    }

    public FavoriteProgram? Result { get; private set; }

    /// <summary>웹사이트 추가: 넣어 둘 주소 (끌어 놓은 링크). 창이 열리면 바로 사이트를 확인</summary>
    public string? InitialAddress { get; init; }

    // ───────── 설치된 프로그램 고르기 ─────────

    private void ApplyFilter()
    {
        string text = FilterBox.Text.Trim();
        ProgramList.ItemsSource = text.Length == 0 ? _programs : _programs.Where(item => item.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ProgramList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProgramList.SelectedItem is not ProgramItem item) return;
        NameBox.Text = item.Name;
        PathBox.Text = item.Path;
        ArgumentsBox.Text = "";
    }

    private void ProgramList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProgramList.SelectedItem is ProgramItem && SaveButton.IsEnabled) SaveButton_Click(sender, e);
    }

    // ───────── 경로 · 주소 ─────────

    private async void PathButton_Click(object sender, RoutedEventArgs e)
    {
        if (_web) await FetchSiteAsync();
        else Browse();
    }

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_web) return;
        e.Handled = true;
        await FetchSiteAsync();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "프로그램 고르기",
            Filter = "프로그램 (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|모든 파일 (*.*)|*.*",
            DereferenceLinks = false
        };
        string current = PathBox.Text.Trim().Trim('"');
        if (File.Exists(current)) dialog.InitialDirectory = Path.GetDirectoryName(current);
        if (dialog.ShowDialog(this) != true) return;
        PathBox.Text = dialog.FileName;
        if (NameBox.Text.Trim().Length == 0) NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void Path_Changed(object sender, TextChangedEventArgs e)
    {
        PathPlaceholder.Visibility = PathBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_web && _fetchedAddress is not null && SiteInfo.Normalize(PathBox.Text)?.AbsoluteUri != _fetchedAddress)
        {
            _fetchedAddress = null;
            _fetchedIcon = null;
            _siteState = null;
            PathNote.Visibility = Visibility.Collapsed;
        }
        UpdateAutoIcon();
        Field_Changed(sender, e);
    }

    /// <summary>자동 아이콘 미리 보기: 프로그램은 그 파일의 아이콘, 웹사이트는 가져온(또는 전에 받아 둔) 사이트 아이콘</summary>
    private void UpdateAutoIcon()
    {
        string path = Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'));
        if (_web)
        {
            var address = SiteInfo.Normalize(path);
            IconChoice.AutoIcon = _fetchedIcon is not null && address?.AbsoluteUri == _fetchedAddress ? Png(_fetchedIcon)
                : address is not null ? SiteInfo.Cached(address.AbsoluteUri) : null;
        }
        else IconChoice.AutoIcon = path.Length > 0 && (File.Exists(path) || Directory.Exists(path)) ? FavoriteIcons.Load(path) : null;
        // 자동인데 아이콘이 없을 때 아이콘 옆 안내
        IconChoice.AutoMissingText = _web
            ? _siteState switch
            {
                "none" => "이 사이트에는 아이콘이 없어 글자로 보입니다. 아래에서 고르세요.",
                "failed" => "사이트 아이콘을 가져오지 못해 글자로 보입니다. 아래에서 고르세요.",
                _ => "[확인]을 누르면 사이트 아이콘을 가져옵니다."
            }
            : path.Length == 0 ? "프로그램을 고르면 그 아이콘을 씁니다."
            : "이 파일은 아이콘이 없어 글자로 보입니다. 아래에서 고르세요.";
        IconChoice.FallbackText = NameBox.Text.Trim() is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "?";
        IconChoice.Refresh();
    }

    /// <summary>웹사이트: 그 사이트에서 이름과 아이콘을 가져옴 (못 가져오면 직접 고르게 기본 아이콘을 펼침)</summary>
    private async Task FetchSiteAsync()
    {
        if (SiteInfo.Normalize(PathBox.Text) is not { } address)
        {
            Note("주소를 알아보지 못했습니다. 예: https://example.com", warn: true);
            return;
        }
        _fetching?.Cancel();
        _fetching = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var cancel = _fetching.Token;
        PathButton.IsEnabled = false;
        PathButton.Content = "확인 중…";
        Note("사이트에서 이름과 아이콘을 가져오는 중…", warn: false);
        try
        {
            var result = await SiteInfo.FetchAsync(address, cancel);
            if (cancel.IsCancellationRequested) return;
            if (PathBox.Text.Trim() != address.AbsoluteUri && SiteInfo.Normalize(PathBox.Text)?.AbsoluteUri == address.AbsoluteUri)
                PathBox.Text = address.AbsoluteUri;  // "example.com" → "https://example.com/"
            _fetchedAddress = address.AbsoluteUri;
            _fetchedIcon = result.IconPng;
            _siteState = result.IconPng is null ? "none" : null;
            if (NameBox.Text.Trim().Length == 0) NameBox.Text = result.Title ?? address.Host;
            UpdateAutoIcon();
            if (result.IconPng is not null) Note(result.Title is null ? "사이트 아이콘을 가져왔습니다." : "사이트 이름과 아이콘을 가져왔습니다.", warn: false);
            else
            {
                Note(result.Title is null ? "이 사이트에는 이름과 아이콘이 없습니다." : "이름을 가져왔습니다. 이 사이트에는 아이콘이 없습니다.", warn: true);
                IconChoice.ShowBuiltins();
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (_fetching?.Token != cancel) return;  // 그사이 다시 확인을 시작함
            _siteState = "failed";
            Note("사이트에 연결하지 못했습니다" + (error is HttpRequestException { Message.Length: > 0 } http ? $" ({http.Message})" : "") +
                 ". 주소는 그대로 저장할 수 있습니다.", warn: true);
            if (NameBox.Text.Trim().Length == 0) NameBox.Text = address.Host;
            UpdateAutoIcon();
            IconChoice.ShowBuiltins();
        }
        finally
        {
            PathButton.IsEnabled = true;
            PathButton.Content = "확인";
        }
    }

    private void Note(string text, bool warn)
    {
        PathNote.Text = text;
        PathNote.Foreground = warn ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x7A)) : (Brush)FindResource("DialogMutedBrush");
        PathNote.Visibility = Visibility.Visible;
    }

    private static ImageSource? Png(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException) { return null; }
    }

    // ───────── 나머지 칸 ─────────

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (SaveButton is null || IconChoice is null) return;
        ErrorText.Visibility = Visibility.Collapsed;
        DescriptionPlaceholder.Visibility = DescriptionBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (sender == NameBox) UpdateAutoIcon();
        string path = PathBox.Text.Trim().Trim('"');
        bool filled = path.Length > 0 && (_web || NameBox.Text.Trim().Length > 0);
        bool changed = _editing is null || NameBox.Text.Trim() != _editing.Name || path != _editing.Path || ArgumentsBox.Text.Trim() != _editing.Arguments ||
                       CategoryBox.Text.Trim() != _editing.Category || DescriptionBox.Text.Trim() != _editing.Description || IconChoice.Value != _editing.Icon;
        SaveButton.IsEnabled = filled && changed;
    }

    private void Category_Changed(object sender, TextChangedEventArgs e)
    {
        RenderCategoryChips();
        Field_Changed(sender, e);
    }

    /// <summary>분류 칸 아래: 지금 있는 분류 (누르면 그 분류로, 지금 분류는 파랑)</summary>
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

    // ───────── 저장 · 닫기 ─────────

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string path;
        string name = NameBox.Text.Trim();
        if (_web)
        {
            if (SiteInfo.Normalize(PathBox.Text) is not { } address)
            {
                Error("주소를 알아보지 못했습니다. 예: https://example.com");
                return;
            }
            path = address.AbsoluteUri;
            if (name.Length == 0) name = address.Host;
            if (_fetchedIcon is not null && _fetchedAddress == path)
            {
                try { SiteInfo.SaveCache(path, _fetchedIcon); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }  // 다음에 다시 받음
            }
        }
        else
        {
            path = Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'));
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Error("경로에 파일이 없습니다.");
                return;
            }
        }
        Result = new FavoriteProgram
        {
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = name,
            Path = path,
            Arguments = _web ? "" : ArgumentsBox.Text.Trim(),
            Category = CategoryBox.Text.Trim(),
            Description = DescriptionBox.Text.Trim(),
            Icon = IconChoice.Value
        };
        IconChoice.CleanUp(keep: Result.Icon);
        DialogResult = true;
    }

    private void Error(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
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
        _fetching?.Cancel();
        if (Result is null) IconChoice.CleanUp(keep: _editing?.Icon);
        base.OnClosed(e);
    }
}
