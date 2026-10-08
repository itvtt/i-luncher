using System.Collections.Concurrent;
using System.Windows.Media;

namespace DiskAtlas.App;

/// <summary>Stable, shared colors for file extensions used by the map and its legend.</summary>
public static class FileTypeColors
{
    private static readonly IReadOnlyDictionary<string, Color> Known = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        [".zip"] = Color.FromRgb(245, 158, 11), [".7z"] = Color.FromRgb(251, 113, 20), [".rar"] = Color.FromRgb(234, 88, 12),
        [".gz"] = Color.FromRgb(217, 119, 6), [".tar"] = Color.FromRgb(180, 83, 9), [".iso"] = Color.FromRgb(202, 138, 4),
        [".mp4"] = Color.FromRgb(236, 72, 153), [".mkv"] = Color.FromRgb(219, 39, 119), [".mov"] = Color.FromRgb(244, 63, 94),
        [".avi"] = Color.FromRgb(225, 29, 72), [".webm"] = Color.FromRgb(190, 24, 93),
        [".mp3"] = Color.FromRgb(168, 85, 247), [".wav"] = Color.FromRgb(139, 92, 246), [".flac"] = Color.FromRgb(124, 58, 237),
        [".m4a"] = Color.FromRgb(192, 38, 211), [".ogg"] = Color.FromRgb(147, 51, 234),
        [".jpg"] = Color.FromRgb(34, 197, 94), [".jpeg"] = Color.FromRgb(34, 197, 94), [".png"] = Color.FromRgb(16, 185, 129),
        [".gif"] = Color.FromRgb(20, 184, 166), [".webp"] = Color.FromRgb(13, 148, 136), [".svg"] = Color.FromRgb(132, 204, 22),
        [".bmp"] = Color.FromRgb(5, 150, 105), [".tif"] = Color.FromRgb(21, 128, 61), [".tiff"] = Color.FromRgb(21, 128, 61),
        [".psd"] = Color.FromRgb(6, 182, 212), [".ai"] = Color.FromRgb(234, 179, 8), [".raw"] = Color.FromRgb(14, 165, 233),
        [".exe"] = Color.FromRgb(239, 68, 68), [".dll"] = Color.FromRgb(220, 38, 38), [".msi"] = Color.FromRgb(185, 28, 28),
        [".sys"] = Color.FromRgb(127, 29, 29), [".appx"] = Color.FromRgb(248, 113, 113),
        [".pdf"] = Color.FromRgb(220, 38, 38), [".doc"] = Color.FromRgb(37, 99, 235), [".docx"] = Color.FromRgb(37, 99, 235),
        [".xls"] = Color.FromRgb(22, 163, 74), [".xlsx"] = Color.FromRgb(22, 163, 74), [".ppt"] = Color.FromRgb(234, 88, 12),
        [".pptx"] = Color.FromRgb(234, 88, 12), [".odt"] = Color.FromRgb(2, 132, 199),
        [".csv"] = Color.FromRgb(5, 150, 105), [".json"] = Color.FromRgb(202, 138, 4), [".xml"] = Color.FromRgb(217, 119, 6),
        [".md"] = Color.FromRgb(59, 130, 246), [".txt"] = Color.FromRgb(100, 116, 139), [".log"] = Color.FromRgb(71, 85, 105),
        [".cs"] = Color.FromRgb(126, 34, 206), [".cpp"] = Color.FromRgb(2, 132, 199), [".c"] = Color.FromRgb(3, 105, 161),
        [".h"] = Color.FromRgb(14, 116, 144), [".js"] = Color.FromRgb(234, 179, 8), [".ts"] = Color.FromRgb(37, 99, 235),
        [".py"] = Color.FromRgb(14, 165, 233), [".java"] = Color.FromRgb(249, 115, 22), [".html"] = Color.FromRgb(239, 68, 68),
        [".css"] = Color.FromRgb(99, 102, 241), [".sql"] = Color.FromRgb(8, 145, 178), [".db"] = Color.FromRgb(8, 145, 178),
        [".ttf"] = Color.FromRgb(217, 70, 239), [".otf"] = Color.FromRgb(192, 38, 211), [".woff"] = Color.FromRgb(168, 85, 247),
        [""] = Color.FromRgb(100, 116, 139)
    };

    private static readonly ConcurrentDictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Brush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    public static Color GetColor(string? extension)
    {
        var key = Normalize(extension);
        if (Known.TryGetValue(key, out var known)) return known;
        return Colors.GetOrAdd(key, static value => FromHash(value));
    }

    public static Brush GetBrush(string? extension) => Brushes.GetOrAdd(Normalize(extension), static value =>
    {
        var color = GetColor(value);
        var brush = new LinearGradientBrush
        {
            StartPoint = new(0, 0),
            EndPoint = new(1, 1),
            GradientStops =
            {
                new GradientStop(Adjust(color, 1.14), 0),
                new GradientStop(color, .55),
                new GradientStop(Adjust(color, .78), 1)
            }
        };
        brush.Freeze();
        return brush;
    });

    private static string Normalize(string? extension)
    {
        var value = (extension ?? "").Trim().ToLowerInvariant();
        if (value.Equals("(확장자 없음)", StringComparison.OrdinalIgnoreCase)) return "";
        if (value.Length == 0) return "";
        return value[0] == '.' ? value : "." + value;
    }

    private static Color FromHash(string value)
    {
        uint hash = 2166136261;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }
        var hue = (hash % 3600) / 10d;
        var saturation = .66 + ((hash >> 12) % 17) / 100d;
        var lightness = .43 + ((hash >> 20) % 12) / 100d;
        return FromHsl(hue, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var section = hue / 60;
        var x = chroma * (1 - Math.Abs(section % 2 - 1));
        var (r, g, b) = section switch
        {
            < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d), < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma), < 5 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        var m = lightness - chroma / 2;
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static Color Adjust(Color color, double factor) => Color.FromRgb(
        (byte)Math.Clamp(Math.Round(color.R * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(color.G * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(color.B * factor), 0, 255));
}
