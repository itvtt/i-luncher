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

    public static int Count => Palettes.Length;

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

/// <summary>스토어형 화면 맨 위의 홍보 카드 (앱 카드와 같은 모양): 머리글이 상태(새 버전 · 새 앱 …), 제목에 새 버전 번호</summary>
public sealed class HeroItem(AppCard card, string eyebrow, string title, string subtitle)
{
    public AppCard Card { get; } = card;
    public string Eyebrow { get; } = eyebrow;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;

    /// <summary>색 번호: 처음엔 앱 카드와 같은 색, 나란한 홍보 카드끼리 겹치면 Render 가 바꿈</summary>
    public int Palette { get; set; } = card.Palette;

    public Brush Art => CardArt.Gradient(Palette);
}
