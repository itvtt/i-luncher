using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ILauncher;

/// <summary>타일 색: 아이콘에서 대표 색을 뽑아 아이콘 뒤 은은한 빛과 카테고리 태그 색을 만듦</summary>
public static class CardColors
{
    private static readonly Dictionary<Color, Brush> Glows = [];

    /// <summary>아이콘의 대표 색: 채도 높은 픽셀 위주 평균. 회색·흰색 위주 아이콘이면 null</summary>
    public static Color? Accent(ImageSource? icon)
    {
        if (icon is not BitmapSource source) return null;
        try
        {
            var pixels = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = pixels.PixelWidth, height = pixels.PixelHeight;
            if (width == 0 || height == 0) return null;
            var data = new byte[width * height * 4];
            pixels.CopyPixels(data, width * 4, 0);
            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < data.Length; i += 4)
            {
                if (data[i + 3] < 128) continue;
                double blue = data[i] / 255.0, green = data[i + 1] / 255.0, red = data[i + 2] / 255.0;
                double max = Math.Max(red, Math.Max(green, blue)), min = Math.Min(red, Math.Min(green, blue));
                double saturation = max == 0 ? 0 : (max - min) / max;
                double w = saturation * saturation * max;  // 선명한 색일수록 크게
                r += red * w; g += green * w; b += blue * w; weight += w;
            }
            if (weight < 0.02 * width * height / 4) return null;
            return Vivid(r / weight, g / weight, b / weight);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>어두운 카드 위에서 잘 보이게: 밝기와 채도를 일정 수준으로</summary>
    private static Color Vivid(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double hue = 0, delta = max - min;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }
        if (hue < 0) hue += 360;
        double saturation = Math.Clamp(max == 0 ? 0 : delta / max, 0.55, 0.85);
        return FromHsv(hue, saturation, 0.95);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>아이콘 뒤 은은한 빛 (카드 위쪽 가운데, 약하게)</summary>
    public static Brush Glow(Color accent)
    {
        lock (Glows)
        {
            if (Glows.TryGetValue(accent, out var cached)) return cached;
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.34), GradientOrigin = new Point(0.5, 0.34), RadiusX = 0.75, RadiusY = 0.42,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x29, accent.R, accent.G, accent.B), 0),
                    new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1)
                }
            };
            brush.Freeze();
            return Glows[accent] = brush;
        }
    }

    private static readonly Dictionary<Color, (Brush Back, Brush Text)> Tags = [];

    /// <summary>카테고리 태그: 앱 색을 옅게 깐 배경과 밝게 섞은 글자</summary>
    public static (Brush Back, Brush Text) Tag(Color accent)
    {
        lock (Tags)
        {
            if (Tags.TryGetValue(accent, out var cached)) return cached;
            var back = new SolidColorBrush(Color.FromArgb(0x29, accent.R, accent.G, accent.B));
            static byte Lift(byte value) => (byte)(value + (255 - value) * 0.55);
            var text = new SolidColorBrush(Color.FromRgb(Lift(accent.R), Lift(accent.G), Lift(accent.B)));
            back.Freeze();
            text.Freeze();
            return Tags[accent] = (back, text);
        }
    }

    /// <summary>즐겨찾기 프로그램 카드처럼 앱 색이 없을 때 (아이콘에서 뽑고, 없으면 차분한 파랑)</summary>
    public static Color Fallback => Color.FromRgb(0x5B, 0x9B, 0xFF);
}
