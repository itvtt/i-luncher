using System.Windows;
using System.Windows.Media;

namespace ILauncher;

/// <summary>
/// 분류 버튼의 아이콘(Segoe Fluent Icons)과 색: 이름에 든 낱말로 뜻에 맞는 아이콘, 표에 없으면 이름마다 정해진 예비 아이콘.
/// 색은 분류 줄의 순서대로 돌려 가며 (이웃한 분류끼리 다른 색). 위에서부터 처음 맞는 줄을 씀
/// </summary>
public static class CategoryLook
{
    public const string AllGlyph = "\uE71D";  // 전체 (모든 앱)

    private static readonly (string[] Words, string Glyph)[] Glyphs =
    [
        (["개발", "코드", "code", "dev", "프로그래밍"], "\uE943"),          // </>
        (["일정", "달력", "캘린더", "calendar", "시간"], "\uE787"),        // 달력
        (["디스크", "저장", "용량", "disk", "drive"], "\uEDA2"),           // 드라이브
        (["공유", "share", "전송", "보내"], "\uE72D"),                     // 공유
        (["파일", "폴더", "file", "folder", "복사"], "\uE8B7"),            // 폴더
        (["설비", "모니터", "장비", "상태", "monitor", "운영"], "\uE9D9"), // 맥박
        (["보안", "security", "권한"], "\uEA18"),                          // 방패
        (["문서", "doc", "글"], "\uE8A5"),                                 // 문서
        (["설정", "도구", "tool", "config", "유틸"], "\uE90F"),            // 공구
        (["데이터", "data", "db", "분석", "보고"], "\uE9F9"),              // 차트
        (["기타", "other", "etc"], "\uE8EC"),                              // 꼬리표
    ];

    private static readonly string[] Spare = ["\uEA86", "\uE81E", "\uF0E3", "\uE7C1", "\uE8F1", "\uEB05", "\uE8FD", "\uE734"];

    // 어두운 버튼 위에서 또렷한 밝은 색 (고른 버튼은 흰 바탕이라 같은 계열의 진한 색 = Ink)
    private static readonly (Color Light, Color Ink)[] Colors =
    [
        (Color.FromRgb(0x8B, 0x84, 0xFF), Color.FromRgb(0x43, 0x38, 0xCA)),  // 남보라
        (Color.FromRgb(0x5E, 0xB1, 0xFF), Color.FromRgb(0x1D, 0x4E, 0xD8)),  // 파랑
        (Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0x04, 0x78, 0x57)),  // 초록
        (Color.FromRgb(0xFF, 0x7E, 0xB6), Color.FromRgb(0xBE, 0x18, 0x5D)),  // 분홍
        (Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xB4, 0x53, 0x09)),  // 노랑
        (Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0x03, 0x69, 0xA1)),  // 하늘
        (Color.FromRgb(0xC0, 0x84, 0xFC), Color.FromRgb(0x6D, 0x28, 0xD9)),  // 보라
        (Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0x0F, 0x76, 0x6E)),  // 청록
    ];

    public static string Glyph(string category)
    {
        string name = category.Trim().ToLowerInvariant();
        foreach (var (words, glyph) in Glyphs)
            if (words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))) return glyph;
        int hash = 0;
        foreach (char c in name) hash = unchecked(hash * 31 + c);
        return Spare[(hash & 0x7FFFFFFF) % Spare.Length];
    }

    public static Brush Light(int index) => Frozen(Colors[index % Colors.Length].Light);
    public static Brush Ink(int index) => Frozen(Colors[index % Colors.Length].Ink);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

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
