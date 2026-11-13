using System.Windows;
using System.Windows.Media;

namespace ILauncher;

/// <summary>스토어형 화면 맨 위의 큰 홍보 카드: 작은 머리글 · 제목 · 설명, 큰 아이콘, 아래 앱 이름과 버튼</summary>
public sealed class HeroItem(AppCard card, string eyebrow, string title, string subtitle)
{
    // 맑은 두 색 그라데이션 (아이콘 색을 어둡게 하면 갈색·흙색이 나와서 정해 둔 색만 씀)
    private static readonly (Color From, Color To)[] Palettes =
    [
        (Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x93, 0x33, 0xEA)),  // 남색 → 보라
        (Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x06, 0xB6, 0xD4)),  // 파랑 → 청록
        (Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0xF9, 0x73, 0x16)),  // 분홍 → 주황
        (Color.FromRgb(0x0E, 0xA5, 0xE9), Color.FromRgb(0x63, 0x66, 0xF1)),  // 하늘 → 남보라
        (Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0xDB, 0x27, 0x77)),  // 보라 → 자홍
        (Color.FromRgb(0x05, 0x96, 0x69), Color.FromRgb(0x0E, 0xA5, 0xE9)),  // 초록 → 하늘
    ];

    public AppCard Card { get; } = card;
    public string Eyebrow { get; } = eyebrow;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;

    /// <summary>앱마다 정해지는 색 번호 (같은 화면의 카드끼리 겹치지 않게 Render 가 고름)</summary>
    public int Palette { get; set; } = StableIndex(card.App.Id.Length > 0 ? card.App.Id : card.Name);

    private static int StableIndex(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text) hash = (hash ^ c) * 16777619;
        return (int)(hash % (uint)Palettes.Length);
    }

    public static int PaletteCount => Palettes.Length;

    /// <summary>카드 배경: 왼쪽 위에서 오른쪽 아래로 맑은 두 색</summary>
    public Brush Art
    {
        get
        {
            var (from, to) = Palettes[Palette % Palettes.Length];
            var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }
    }
}

/// <summary>순위 목록 한 줄 (최근 업데이트): 번호, 앱, 분류 · 언제</summary>
public sealed class RankItem(AppCard card, int rank, string subtitle)
{
    public AppCard Card { get; } = card;
    public int Rank { get; } = rank;
    public string Subtitle { get; } = subtitle;

    public static string When(DateTimeOffset published)
    {
        int days = (DateTime.Today - published.ToLocalTime().Date).Days;
        return days <= 0 ? "오늘" : days == 1 ? "어제" : days < 7 ? $"{days}일 전" : published.ToLocalTime().ToString("M월 d일");
    }
}
