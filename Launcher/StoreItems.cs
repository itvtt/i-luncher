using System.Windows;
using System.Windows.Media;

namespace ILauncher;

/// <summary>카드 배경: 맑은 두 색 그라데이션 (아이콘 색을 어둡게 하면 갈색·흙색이 나와서 정해 둔 색만 씀)</summary>
public static class CardArt
{
    private static readonly (Color From, Color To)[] Palettes =
    [
        (Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x93, 0x33, 0xEA)),  // 남색 → 보라
        (Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x06, 0xB6, 0xD4)),  // 파랑 → 청록
        (Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0xF9, 0x73, 0x16)),  // 분홍 → 주황
        (Color.FromRgb(0x0E, 0xA5, 0xE9), Color.FromRgb(0x63, 0x66, 0xF1)),  // 하늘 → 남보라
        (Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0xDB, 0x27, 0x77)),  // 보라 → 자홍
        (Color.FromRgb(0x05, 0x96, 0x69), Color.FromRgb(0x0E, 0xA5, 0xE9)),  // 초록 → 하늘
    ];

    // 흰 버튼 위 글자색: 그라데이션과 같은 계열의 진한 색 (흰 바탕 대비 4.5:1 이상)
    private static readonly Color[] Inks =
    [
        Color.FromRgb(0x43, 0x38, 0xCA),  // 남색
        Color.FromRgb(0x1D, 0x4E, 0xD8),  // 파랑
        Color.FromRgb(0xBE, 0x18, 0x5D),  // 분홍
        Color.FromRgb(0x03, 0x69, 0xA1),  // 하늘
        Color.FromRgb(0x6D, 0x28, 0xD9),  // 보라
        Color.FromRgb(0x04, 0x78, 0x57),  // 초록
    ];

    public static int Count => Palettes.Length;

    /// <summary>카드 버튼(흰 바탕)의 글자 · 아이콘 색</summary>
    public static Brush Ink(int index)
    {
        var brush = new SolidColorBrush(Inks[((index % Inks.Length) + Inks.Length) % Inks.Length]);
        brush.Freeze();
        return brush;
    }

    /// <summary>글자로 정하는 색 번호 (같은 글자는 늘 같은 색)</summary>
    public static int Stable(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text) hash = (hash ^ c) * 16777619;
        return (int)(hash % (uint)Palettes.Length);
    }

    /// <summary>왼쪽 위에서 오른쪽 아래로 맑은 두 색</summary>
    public static Brush Gradient(int index)
    {
        var (from, to) = Palettes[((index % Palettes.Length) + Palettes.Length) % Palettes.Length];
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }
}
