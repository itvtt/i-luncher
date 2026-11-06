using System.Text.RegularExpressions;

namespace ILauncher.Services;

/// <summary>
/// 릴리스 태그(v1.2.3, 1.2.3.4, app-v1.2.3-beta.1 …)와 설치된 실행 파일 버전(1.2.3+abc, 1.2.3.0)을 같은 기준으로 비교.
/// 숫자 부분은 4자리까지(빈 자리는 0), 정식 버전이 같은 번호의 프리릴리스(-beta)보다 높고, 빌드 메타데이터(+…)는 무시.
/// </summary>
public sealed partial class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private readonly int[] _numbers;

    private ReleaseVersion(int[] numbers, string prerelease)
    {
        _numbers = numbers;
        Prerelease = prerelease;
    }

    /// <summary>숫자 부분 (4자리, 빈 자리는 0)</summary>
    public IReadOnlyList<int> Numbers => _numbers;
    public string Prerelease { get; }
    public bool IsPrerelease => Prerelease.Length > 0;

    [GeneratedRegex(@"(?<![0-9A-Za-z])[vV]?(?<num>\d+(?:\.\d+){0,3})(?:-(?<pre>[0-9A-Za-z][0-9A-Za-z.\-]*))?(?:\+[0-9A-Za-z.\-]+)?$")]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"(?<![0-9A-Za-z.])[vV]?(?<num>\d+(?:\.\d+){1,3})(?:-(?<pre>[0-9A-Za-z][0-9A-Za-z.\-]*))?")]
    private static partial Regex AnyVersion();

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        string value = text?.Trim() ?? "";
        if (value.Length == 0 || value.Length > 128) return false;
        // 끝에 붙은 버전 (v1.2.3, deskcal-v1.2.3) 을 먼저, 없으면 본문 안의 첫 x.y 버전 ("Release 1.2.3 (beta)")
        Match match = TrailingVersion().Match(value);
        if (!match.Success) match = AnyVersion().Match(value);
        if (!match.Success) return false;
        string[] parts = match.Groups["num"].Value.Split('.');
        var numbers = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        version = new ReleaseVersion(numbers, match.Groups["pre"].Success ? match.Groups["pre"].Value : "");
        return true;
    }

    public static ReleaseVersion? Parse(string? text) => TryParse(text, out var version) ? version : null;

    /// <summary>화면용: 1.2.3 (4번째 자리가 0이 아니면 1.2.3.4), 프리릴리스는 1.2.3-beta.1</summary>
    public override string ToString()
    {
        string core = _numbers[3] != 0
            ? string.Join('.', _numbers)
            : $"{_numbers[0]}.{_numbers[1]}.{_numbers[2]}";
        return IsPrerelease ? core + "-" + Prerelease : core;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (int i = 0; i < 4; i++)
        {
            int compare = _numbers[i].CompareTo(other._numbers[i]);
            if (compare != 0) return compare;
        }
        if (!IsPrerelease || !other.IsPrerelease) return other.IsPrerelease.CompareTo(IsPrerelease);
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    // SemVer 2.0 의 프리릴리스 순서: 점으로 나눈 항목별로 숫자는 숫자로, 글자는 서수로, 숫자 < 글자, 항목이 더 많으면 더 높음
    private static int ComparePrerelease(string left, string right)
    {
        string[] a = left.Split('.'), b = right.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNumber = long.TryParse(a[i], out long aValue), bNumber = long.TryParse(b[i], out long bValue);
            int compare = aNumber && bNumber ? aValue.CompareTo(bValue)
                : aNumber ? -1
                : bNumber ? 1
                : string.CompareOrdinal(a[i], b[i]);
            if (compare != 0) return Math.Sign(compare);
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_numbers[0], _numbers[1], _numbers[2], _numbers[3], Prerelease);

    public static bool operator >(ReleaseVersion? left, ReleaseVersion? right) => left is not null && left.CompareTo(right) > 0;
    public static bool operator <(ReleaseVersion? left, ReleaseVersion? right) => right is not null && right.CompareTo(left) > 0;
    public static bool operator >=(ReleaseVersion? left, ReleaseVersion? right) => !(left < right);
    public static bool operator <=(ReleaseVersion? left, ReleaseVersion? right) => !(left > right);
}
