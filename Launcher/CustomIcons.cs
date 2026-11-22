using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ILauncher;

/// <summary>
/// 사용자가 고른 아이콘 (즐겨찾기 · 내 앱). 값은
///   "" = 자동 (프로그램 · 사이트 · 저장소 아이콘),
///   "builtin:&lt;이름&gt;" = 기본 아이콘 (아래 Builtins),
///   전체 경로 = 이미지 파일 (Import 로 C:\i-luncher\custom-icons 에 PNG 로 복사해 둔 것).
/// </summary>
public static class CustomIcons
{
    public const string BuiltinPrefix = "builtin:";

    /// <summary>기본 아이콘 하나: 이름, 보이는 이름, 글리프(Segoe Fluent Icons / MDL2), 판의 위 · 아래 색</summary>
    public sealed record Builtin(string Key, string Label, string Glyph, Color Top, Color Bottom);

    public static IReadOnlyList<Builtin> Builtins { get; } =
    [
        new("web", "웹", "\uE774", Rgb(0x4F, 0x8D, 0xF7), Rgb(0x1F, 0x3F, 0x9A)),
        new("home", "홈", "\uE80F", Rgb(0xF7, 0xB2, 0x4A), Rgb(0xB8, 0x62, 0x12)),
        new("mail", "메일", "\uE715", Rgb(0x4C, 0xC3, 0xF5), Rgb(0x0A, 0x6C, 0xA8)),
        new("chat", "대화", "\uE8BD", Rgb(0x3F, 0xD6, 0x9A), Rgb(0x0B, 0x7A, 0x52)),
        new("calendar", "일정", "\uE787", Rgb(0xFB, 0x7E, 0x93), Rgb(0xC0, 0x26, 0x4B)),
        new("document", "문서", "\uE8A5", Rgb(0x9C, 0xAB, 0xBF), Rgb(0x3A, 0x46, 0x58)),
        new("folder", "폴더", "\uE8B7", Rgb(0xF9, 0xD0, 0x4C), Rgb(0xB0, 0x78, 0x0E)),
        new("code", "코드", "\uE943", Rgb(0xA9, 0x8C, 0xFA), Rgb(0x55, 0x2B, 0xB8)),
        new("tool", "도구", "\uE90F", Rgb(0x8E, 0x95, 0xA3), Rgb(0x34, 0x39, 0x44)),
        new("chart", "차트", "\uE9D2", Rgb(0x38, 0xD3, 0xC2), Rgb(0x0C, 0x6F, 0x6A)),
        new("music", "음악", "\uE8D6", Rgb(0xF6, 0x7C, 0xC4), Rgb(0x9C, 0x1F, 0x6E)),
        new("video", "영상", "\uE714", Rgb(0x8C, 0x94, 0xF8), Rgb(0x3A, 0x36, 0xA8)),
        new("photo", "사진", "\uEB9F", Rgb(0xFB, 0x9A, 0x4E), Rgb(0xC0, 0x45, 0x10)),
        new("cloud", "클라우드", "\uE753", Rgb(0x83, 0xD4, 0xFB), Rgb(0x16, 0x82, 0xC4)),
        new("shop", "쇼핑", "\uE7BF", Rgb(0x5B, 0xDC, 0x7E), Rgb(0x18, 0x7E, 0x3C)),
        new("people", "사람", "\uE716", Rgb(0x6D, 0xA9, 0xFB), Rgb(0x1E, 0x4F, 0xC8)),
        new("star", "별", "\uE734", Rgb(0xF4, 0xDB, 0x8E), Rgb(0xA8, 0x7A, 0x22)),
        new("settings", "설정", "\uE713", Rgb(0x5E, 0x60, 0x68), Rgb(0x1C, 0x1D, 0x22))
    ];

    private static readonly Dictionary<string, ImageSource> Rendered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>이미지 파일을 복사해 둘 폴더 (런처가 시작할 때 정함)</summary>
    public static string Folder { get; set; } = Path.Combine(Services.DataFolder.Fixed, "custom-icons");

    public static bool IsCustom(string? value) => !string.IsNullOrWhiteSpace(value) &&
        (value.StartsWith(BuiltinPrefix, StringComparison.OrdinalIgnoreCase) || Path.IsPathFullyQualified(value));

    public static Builtin? FindBuiltin(string? value) => value is not null && value.StartsWith(BuiltinPrefix, StringComparison.OrdinalIgnoreCase)
        ? Builtins.FirstOrDefault(item => item.Key.Equals(value[BuiltinPrefix.Length..], StringComparison.OrdinalIgnoreCase))
        : null;

    /// <summary>고른 아이콘 (자동 · 없는 파일 · 모르는 기본 아이콘은 null)</summary>
    public static ImageSource? Load(string? value)
    {
        if (!IsCustom(value)) return null;
        if (FindBuiltin(value) is { } builtin) return Render(builtin);
        try
        {
            if (!File.Exists(value)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(value!);
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

    /// <summary>이미지 파일(PNG · JPG · ICO · BMP · GIF)을 256px 이하 PNG 로 복사하고 그 경로. 읽을 수 없는 그림이면 InvalidDataException</summary>
    public static string Import(string sourceFile) => Save(File.ReadAllBytes(sourceFile), "icon");

    /// <summary>그림 바이트를 256px 이하 PNG 로 저장하고 그 경로 (사이트 아이콘도 여기로)</summary>
    public static string Save(byte[] bytes, string prefix)
    {
        if (AppIconService.Decode(bytes) is not { } image) throw new InvalidDataException("그림 파일로 읽을 수 없습니다.");
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"{prefix}-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
    }

    /// <summary>이 폴더에 복사해 둔 파일이면 지움 (다른 곳의 파일은 건드리지 않음)</summary>
    public static void DeleteImported(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return;
        try
        {
            string folder = Path.GetFullPath(Folder).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(value);
            if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) File.Delete(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    /// <summary>
    /// 기본 아이콘 그림 (128px): 위가 밝은 색 판, 위쪽의 은은한 광택, 가는 안쪽 테두리, 흰 글리프와 옅은 그림자
    /// </summary>
    public static ImageSource Render(Builtin builtin)
    {
        lock (Rendered)
        {
            if (Rendered.TryGetValue(builtin.Key, out var done)) return done;
        }
        // 그리기는 화면 스레드에서 (아이콘을 백그라운드에서 읽을 때도)
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) return dispatcher.Invoke(() => Render(builtin));
        const int size = 128;
        const double radius = 29;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var tile = new Rect(0, 0, size, size);
            var face = new LinearGradientBrush(builtin.Top, builtin.Bottom, new Point(0.15, 0), new Point(0.85, 1));
            context.DrawRoundedRectangle(face, null, tile, radius, radius);

            var gloss = new LinearGradientBrush(
                [new GradientStop(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55)],
                new Point(0.5, 0), new Point(0.5, 1));
            context.DrawRoundedRectangle(gloss, null, tile, radius, radius);

            var hairline = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1.5);
            context.DrawRoundedRectangle(null, hairline, new Rect(0.75, 0.75, size - 1.5, size - 1.5), radius - 0.75, radius - 0.75);

            var typeface = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            FormattedText Glyph(Brush brush) => new(builtin.Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 58, brush, 1.0);
            var shadow = Glyph(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)));
            var glyph = Glyph(Brushes.White);
            var at = new Point((size - glyph.Width) / 2, (size - glyph.Height) / 2);
            context.DrawText(shadow, new Point(at.X, at.Y + 2.5));
            context.DrawText(glyph, at);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        // 화면이 잠겨 있으면 WPF 가 아무것도 그리지 않음: 빈 그림은 기억하지 않고 다음에 다시 그림
        if (!IsBlank(bitmap)) lock (Rendered) Rendered[builtin.Key] = bitmap;
        return bitmap;
    }

    /// <summary>가운데 점이 투명하면 빈 그림 (잠긴 화면에서 그린 것)</summary>
    public static bool IsBlank(BitmapSource image)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel[3] == 0;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
