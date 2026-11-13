using System.Windows;
using System.Windows.Media;

namespace ILauncher;

/// <summary>스토어형 화면 맨 위의 큰 홍보 카드: 작은 머리글 · 제목 · 설명, 큰 아이콘, 아래 앱 이름과 버튼. 색은 아이콘 대표 색</summary>
public sealed class HeroItem(AppCard card, string eyebrow, string title, string subtitle)
{
    public AppCard Card { get; } = card;
    public string Eyebrow { get; } = eyebrow;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;

    /// <summary>카드 배경: 아이콘 색에서 어둡게 내려가는 그라데이션</summary>
    public Brush Art
    {
        get
        {
            var accent = CardColors.Accent(Card.Icon) ?? CardColors.Fallback;
            static Color Shade(Color color, double factor) => Color.FromRgb((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));
            var brush = new LinearGradientBrush(Shade(accent, 0.85), Shade(accent, 0.38), new Point(0, 0), new Point(1, 1));
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
