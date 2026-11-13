using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher;

/// <summary>카드 하나의 상태: 카탈로그 정의 + 이 PC 의 설치 상태 + GitHub 최신 릴리스</summary>
public sealed class AppCard : INotifyPropertyChanged
{
    // 버전 글자: 흐린 회색 (타일 아래 왼쪽)
    private static readonly Brush NormalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB4, 0xBE, 0xCD)));
    private static readonly Brush WarningBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xC0, 0x7A)));
    private static readonly Brush InstalledTitleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFB)));
    private static readonly Brush NotInstalledTitleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xD0, 0xDC)));

    private AppDefinition _app;
    private InstalledApp? _installed;
    private GitHubRelease? _latest;
    private string? _latestError;
    private bool _checking, _checked, _busy;
    private string? _busyText;
    private double? _progress;
    private ImageSource? _icon;

    public AppCard(AppDefinition app) => _app = app;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppDefinition App { get => _app; set { _app = value; NotifyAll(); } }
    public InstalledApp? Installed { get => _installed; set { _installed = value; NotifyAll(); } }
    public GitHubRelease? Latest { get => _latest; private set { _latest = value; NotifyAll(); } }
    public string? LatestError { get => _latestError; private set { _latestError = value; NotifyAll(); } }
    public DateTime? CheckedAt { get; private set; }
    public bool IsChecking { get => _checking; set { _checking = value; NotifyAll(); } }
    public bool IsBusy { get => _busy; private set { _busy = value; NotifyAll(); } }
    public string? BusyText { get => _busyText; private set { _busyText = value; Notify(); Notify(nameof(DescriptionText)); Notify(nameof(StoreSubtitle)); } }
    public double? Progress { get => _progress; private set { _progress = value; Notify(); Notify(nameof(IsIndeterminate)); Notify(nameof(ProgressValue)); } }
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            _accent = null;
            Notify(); Notify(nameof(IconVisibility)); Notify(nameof(InitialsVisibility)); Notify(nameof(TileBrush));
            Notify(nameof(AccentGlowBrush)); Notify(nameof(CategoryTagBrush)); Notify(nameof(CategoryTextBrush));
        }
    }

    // ── 카드 색 (오로라 글로우): 아이콘의 대표 색, 아이콘이 없으면 이름으로 정한 타일 색 ──
    private Color? _accent;
    private Color Accent => _accent ??= CardColors.Accent(Icon) ?? TilePalettes[PaletteIndex].Top;
    /// <summary>아이콘 뒤로 퍼지는 빛</summary>
    public Brush AccentGlowBrush => CardColors.Glow(Accent);
    /// <summary>카테고리 태그 (앱 색을 옅게)</summary>
    public Brush CategoryTagBrush => CardColors.Tag(Accent).Back;
    public Brush CategoryTextBrush => CardColors.Tag(Accent).Text;

    public void SetRelease(GitHubRelease? release, string? error)
    {
        _checked = error is null;
        CheckedAt = DateTime.Now;
        _latestError = error;
        if (error is null || release is not null) _latest = release;
        _checking = false;
        NotifyAll();
    }

    /// <summary>저장소를 바꿨을 때: 예전 릴리스 정보를 버리고 다시 확인하게 함</summary>
    public void ResetRelease()
    {
        _latest = null;
        _latestError = null;
        _checked = false;
        CheckedAt = null;
        NotifyAll();
    }

    public void BeginWork(string text)
    {
        _busy = true;
        _busyText = text;
        _progress = null;
        NotifyAll();
    }

    public void ReportWork(AppProgress progress)
    {
        BusyText = progress.Message;
        Progress = progress.Fraction;
    }

    public void EndWork()
    {
        _busy = false;
        _busyText = null;
        _progress = null;
        NotifyAll();
    }

    // ── 화면에 보이는 값 ──
    public string Name => App.Name;
    public string Category => string.IsNullOrWhiteSpace(App.Category) ? "기타" : App.Category.Trim();
    public string AutomationId => "App_" + App.Id;
    public bool IsInstalled => Installed is not null;
    public bool UpdateAvailable => Installed?.Version is { } installed && Latest?.Version is { } latest && latest > installed;
    public bool CanInteract => !IsBusy;
    public bool IsIndeterminate => Progress is null;
    public double ProgressValue => (Progress ?? 0) * 100;
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UpdateMenuVisibility => UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InstalledMenuVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReleaseMenuVisibility => Latest is not null ? Visibility.Visible : Visibility.Collapsed;
    // 마스터가 API 서버 관리 프로그램의 앱 목록에서 켬
    public Visibility BetaVisibility => App.Beta ? Visibility.Visible : Visibility.Collapsed;
    // 즐겨찾기 (이 PC 에만, FavoriteStore)
    private bool _favorite;
    public bool IsFavorite { get => _favorite; set { if (_favorite == value) return; _favorite = value; Notify(); Notify(nameof(FavoriteMenuHeader)); Notify(nameof(FavoriteVisibility)); } }
    public string FavoriteMenuHeader => IsFavorite ? "즐겨찾기에서 빼기" : "즐겨찾기에 추가";
    public Visibility FavoriteVisibility => IsFavorite ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NewVisibility => App.New ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility InitialsVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public double IconOpacity => 1.0;
    public Brush TitleBrush => IsInstalled ? InstalledTitleBrush : NotInstalledTitleBrush;

    /// <summary>아이콘이 없을 때 타일 글자: 단어 두 개 이상이면 앞 글자 둘 (Windows Terminal → WT), 아니면 한 글자</summary>
    public string Initials
    {
        get
        {
            string[] words = Name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "?";
            if (words.Length >= 2 && char.IsLetterOrDigit(words[0][0]) && char.IsLetterOrDigit(words[1][0]) && words[0][0] < 0x1100)
                return (words[0][..1] + words[1][..1]).ToUpperInvariant();
            return words[0][..1].ToUpperInvariant();
        }
    }

    // 이름마다 정해지는 색 (보라·검붉은 계열 없음)
    private static readonly (Color Top, Color Bottom)[] TilePalettes =
    [
        (Color.FromRgb(0x5B, 0x9B, 0xFF), Color.FromRgb(0x2B, 0x5F, 0xD9)),  // blue
        (Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0x0F, 0x94, 0x88)),  // teal
        (Color.FromRgb(0x4A, 0xDE, 0x80), Color.FromRgb(0x16, 0xA3, 0x4A)),  // green
        (Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xD9, 0x77, 0x06)),  // amber
        (Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x08, 0x91, 0xB2)),  // cyan
        (Color.FromRgb(0x7D, 0xD3, 0xFC), Color.FromRgb(0x02, 0x84, 0xC7)),  // sky
        (Color.FromRgb(0xFD, 0xBA, 0x74), Color.FromRgb(0xEA, 0x58, 0x0C)),  // orange
    ];
    private static readonly Brush IconTileBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)));

    private int PaletteIndex
    {
        get
        {
            uint hash = 2166136261;
            foreach (char c in App.Id.Length > 0 ? App.Id : Name) hash = (hash ^ c) * 16777619;
            return (int)(hash % (uint)TilePalettes.Length);
        }
    }

    /// <summary>타일 배경: 아이콘이 있으면 투명, 없으면 이름으로 정한 그라데이션</summary>
    public Brush TileBrush
    {
        get
        {
            if (Icon is not null) return IconTileBrush;
            var (top, bottom) = TilePalettes[PaletteIndex];
            var brush = new LinearGradientBrush(top, bottom, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
            brush.Freeze();
            return brush;
        }
    }

    // 버전 배지 오른쪽의 동작 아이콘: 누르면 무엇을 하는지 (업데이트 = 초록 ↑, 받기 = 파랑 ↓). 진행 중에는 아래 진행 막대
    private static readonly Brush GetBack = Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0x5F, 0xD0)));
    private static readonly Brush UpdateBack = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x8F, 0x5F)));

    public string ActionText => IsBusy ? "진행 중" : UpdateAvailable ? $"업데이트 v{Latest?.Version}" : "받기";
    /// <summary>버튼 글자 (각진 단색 버튼): 업데이트 / 받기</summary>
    public string ActionLabel => UpdateAvailable ? "업데이트" : "받기";

    /// <summary>스토어형 줄의 오른쪽 버튼: 진행 중 / 업데이트 / 열기 / 받기 (받을 릴리스가 없으면 숨김)</summary>
    /// <summary>줄의 둘째 줄: 분류 · 설명 (진행 중이면 진행 글자)</summary>
    public string StoreSubtitle => IsBusy && BusyText is { Length: > 0 } busy ? busy : $"{Category} · {DescriptionText}";
    public string StoreButtonText => IsBusy ? "진행 중" : UpdateAvailable ? "업데이트" : IsInstalled ? "열기" : Latest is not null ? "받기" : "";
    public Visibility StoreButtonVisibility => StoreButtonText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Brush ActionBackground => UpdateAvailable ? UpdateBack : GetBack;
    /// <summary>받기·업데이트가 있을 때만. 설치된 최신 앱은 카드를 누르면 열림</summary>
    public Visibility ActionVisibility => !IsBusy && (UpdateAvailable || !IsInstalled && Latest is not null) ? Visibility.Visible : Visibility.Collapsed;

    public string DescriptionText => IsBusy && BusyText is { Length: > 0 } busy ? busy
        : !string.IsNullOrWhiteSpace(App.Description) ? App.Description
        : GitHubRepository.Parse(App.Repository)?.FullName ?? App.Repository;

    /// <summary>카드 오른쪽 위 버전 배지: 짧게 버전만. 상태(확인 실패·릴리스 없음)는 색과 툴팁으로</summary>
    public string VersionText
    {
        get
        {
            string? installed = Installed?.Version?.ToString();
            string? latest = Latest?.Version?.ToString();
            if (IsInstalled)
            {
                return installed is null ? "설치됨" : $"v{installed}";  // 새 버전은 오른쪽 아이콘·툴팁에
            }
            if (IsChecking) return "확인 중…";
            if (latest is not null) return $"v{latest}";
            if (Latest is not null) return Latest.Tag;
            if (LatestError is not null) return "확인 실패";
            return _checked ? "릴리스 없음" : "";
        }
    }

    private bool VersionWarning => LatestError is not null || (_checked && Latest is null);
    public Brush VersionBrush => VersionWarning ? WarningBrush : NormalBrush;

    // 버전 태그 앞 작은 점: 최신 = 초록, 확인 실패·릴리스 없음 = 노랑 (업데이트·받기는 버튼이 보여 줌)
    private static readonly Brush LatestDot = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)));
    private bool IsLatest => IsInstalled && !UpdateAvailable && Latest is not null && LatestError is null && !IsChecking;
    public Brush VersionDotBrush => VersionWarning ? WarningBrush : LatestDot;
    public Visibility VersionDotVisibility => VersionWarning || IsLatest ? Visibility.Visible : Visibility.Collapsed;

    private static readonly Brush WarningBadgeBack = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0xE6, 0xC0, 0x7A)));
    private static readonly Brush NormalBadgeBack = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    public Brush VersionBadgeBackground => VersionWarning ? WarningBadgeBack : NormalBadgeBack;
    public Visibility VersionVisibility => VersionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string VersionToolTip
    {
        get
        {
            var lines = new List<string>();
            if (Installed is { } installed)
            {
                lines.Add($"설치된 버전: {(installed.Version is null ? "알 수 없음" : "v" + installed.Version)}" +
                          (installed.VersionSource is null ? "" : $" ({installed.VersionSource})"));
                if (installed.ExecutableVersion is { } fileVersion && ReleaseVersion.Parse(fileVersion) is { } parsed && installed.Version is { } shown && !parsed.Equals(shown))
                    lines.Add($"실행 파일 버전: {fileVersion} (릴리스 빌드에 -p:Version 을 넘기면 같아집니다)");
                lines.Add($"설치 위치: {installed.Directory}");
            }
            else lines.Add("설치되지 않음");
            if (Latest is { } release)
                lines.Add($"최신 릴리스: {release.Tag}" + (release.PublishedAt is { } date ? $" ({date.ToLocalTime():yyyy-MM-dd HH:mm})" : "") +
                          (release.Prerelease ? " · 프리릴리스" : ""));
            if (LatestError is { } error) lines.Add("확인 실패: " + error);
            else if (_checked && Latest is null) lines.Add("이 저장소에는 아직 릴리스가 없습니다.");
            if (CheckedAt is { } at) lines.Add($"확인 시각: {at:HH:mm:ss}");
            lines.Add($"저장소: {App.Repository}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public string RunToolTip => IsBusy ? BusyText ?? "작업 중…"
        : !IsInstalled ? $"클릭해서 {Name} 설치"
        : UpdateAvailable ? $"클릭하면 v{Latest!.Version}(으)로 업데이트한 뒤 실행합니다"
        : $"클릭해서 {Name} 실행";

    public bool Matches(string search) =>
        search.Length == 0 ||
        Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        Category.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        App.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        App.Repository.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>실행 파일 아이콘 (64px) 을 읽고, 앱을 지워도 카드에 보이도록 PNG 로 보관</summary>
public static class AppIcons
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int width, int height, IntPtr[] icons, int[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    public static ImageSource? Load(string appId, string? executable, string cacheDirectory)
    {
        string cache = Path.Combine(cacheDirectory, AppDefinition.SafeFolderName(appId) + ".png");
        if (executable is not null && File.Exists(executable) && FromExecutable(executable) is { } icon)
        {
            TrySave(icon, cache);
            return icon;
        }
        try
        {
            if (!File.Exists(cache)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(cache);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }

    private static BitmapSource? FromExecutable(string executable) => FromFile(executable);

    /// <summary>파일(실행 파일·dll·ico)의 아이콘 하나를 64px 로</summary>
    public static BitmapSource? FromFile(string file, int index = 0)
    {
        var handles = new IntPtr[1];
        var ids = new int[1];
        try
        {
            if (PrivateExtractIcons(file, index, 64, 64, handles, ids, 1, 0) is 0 or uint.MaxValue || handles[0] == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException) { return null; }
        finally
        {
            if (handles[0] != IntPtr.Zero) DestroyIcon(handles[0]);
        }
    }

    private static void TrySave(BitmapSource icon, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(icon));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }
}
