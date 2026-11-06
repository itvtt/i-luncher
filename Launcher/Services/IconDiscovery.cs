using System.Text.RegularExpressions;

namespace ILauncher.Services;

/// <summary>
/// 앱 저장소에서 아이콘 파일 고르기 (설치하기 전에도 카드에 앱 아이콘을 보이려고).
/// 1. .NET 프로젝트(.csproj)의 &lt;ApplicationIcon&gt; — 실행 파일 아이콘의 원본이라 가장 정확
/// 2. 이름으로 고르기: Assets·Resources 아래, 이름에 icon·logo·앱 이름이 들어간 .ico/.png
/// 테스트·빌드 결과·문서·설치 프로그램 폴더는 보지 않음.
/// </summary>
public static partial class IconDiscovery
{
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".github", ".vs", "packages", "third_party", "thirdparty", "vendor", "external",
        "docs", "doc", "screenshots", "screenshot", "samples", "sample", "examples", "installer", "setup", "benchmarks",
        "artifacts", "out", "dist", "publish", "build", "tools"
    };

    private static readonly HashSet<string> AssetFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets", "asset", "resources", "resource", "res", "images", "image", "img", "icons", "icon", "art", "branding", "logo", "logos"
    };

    public static bool IsSkipped(string path)
    {
        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string segment = segments[i];
            if (SkipFolders.Contains(segment) || segment.StartsWith('.') && segment.Length > 1 ||
                segment.Contains("test", StringComparison.OrdinalIgnoreCase) || segment.EndsWith(".Checks", StringComparison.OrdinalIgnoreCase) ||
                segment.EndsWith(".Setup", StringComparison.OrdinalIgnoreCase) || segment.Equals("Distribution", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Simplify(string text) => NonAlphaNumeric().Replace(text.ToLowerInvariant(), "");

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphaNumeric();

    /// <summary>비교용 이름들 (앱 이름, 설치 이름, 저장소 이름) — 소문자·기호 없음</summary>
    public static IReadOnlyList<string> Names(params string?[] names) =>
        names.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => Simplify(name!)).Where(name => name.Length >= 2).Distinct().ToList();

    /// <summary>아이콘을 읽어 볼 프로젝트 파일 (앱 이름과 같은 것, 실행 앱처럼 보이는 것 먼저, 최대 3개)</summary>
    public static IReadOnlyList<string> ProjectCandidates(IEnumerable<GitHubTreeEntry> entries, IReadOnlyList<string> names) =>
        entries.Where(entry => entry.IsFile && (entry.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || entry.Path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                               && !IsSkipped(entry.Path))
            .Select(entry =>
            {
                string file = Path.GetFileNameWithoutExtension(entry.Path);
                string simple = Simplify(file);
                int score = names.Any(name => simple == name) ? 20
                    : names.Any(name => simple.StartsWith(name) || name.StartsWith(simple)) ? 10
                    : names.Any(name => simple.Contains(name)) ? 6 : 0;
                if (file.EndsWith(".App", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".Wpf", StringComparison.OrdinalIgnoreCase)) score += 4;
                if (file.Contains("Core", StringComparison.OrdinalIgnoreCase) || file.Contains("Library", StringComparison.OrdinalIgnoreCase)) score -= 6;
                score -= entry.Path.Count(c => c == '/');
                return (entry.Path, score);
            })
            .OrderByDescending(item => item.score).ThenBy(item => item.Path.Length)
            .Take(3).Select(item => item.Path).ToList();

    [GeneratedRegex(@"<ApplicationIcon>\s*([^<]+?)\s*</ApplicationIcon>", RegexOptions.IgnoreCase)]
    private static partial Regex ApplicationIconPattern();

    /// <summary>프로젝트 파일의 &lt;ApplicationIcon&gt; 을 저장소 기준 경로로 (없거나 MSBuild 속성을 쓰면 null)</summary>
    public static string? ParseApplicationIcon(string projectText, string projectPath)
    {
        foreach (Match match in ApplicationIconPattern().Matches(projectText))
        {
            string value = match.Groups[1].Value.Trim();
            if (value.Length == 0 || value.Contains("$(") || value.Contains("%(") || Path.IsPathRooted(value)) continue;
            var parts = new List<string>(projectPath.Split('/')[..^1]);
            foreach (string part in value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(part);
            }
            return string.Join('/', parts);
        }
        return null;
    }

    /// <summary>이름으로 가장 그럴듯한 아이콘 (.ico/.png). 근거가 약하면 null</summary>
    public static string? ChooseByName(IEnumerable<GitHubTreeEntry> entries, IReadOnlyList<string> names)
    {
        var candidates = entries.Where(entry => entry.IsFile && !IsSkipped(entry.Path) && entry.Size is 0 or (> 100 and < 1_500_000)
                                                && (entry.Path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || entry.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        int icoCount = candidates.Count(entry => entry.Path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
        (string Path, int Score)? best = null;
        foreach (var entry in candidates)
        {
            string[] segments = entry.Path.Split('/');
            string file = Path.GetFileNameWithoutExtension(entry.Path);
            string simple = Simplify(file);
            bool ico = entry.Path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);
            int score = ico ? 5 : 2;
            bool named = false;
            if (simple.Contains("icon") || simple.Contains("logo")) { score += 6; named = true; }
            if (names.Any(name => simple == name || simple.StartsWith(name))) { score += 6; named = true; }
            if (segments[..^1].Any(segment => AssetFolders.Contains(segment))) score += 3;
            if (ico && icoCount == 1) { score += 4; named = true; }
            if (Regex.IsMatch(simple, "splash|banner|screenshot|background|wallpaper|preview|hero|social|badge|button|toolbar|favicon"))
                score -= 8;
            var size = SizeHint().Match(file);
            if (size.Success && int.TryParse(size.Groups[1].Value, out int pixels))
                score += pixels >= 128 ? 3 : pixels >= 48 ? 1 : -3;
            score -= Math.Max(0, segments.Length - 3);
            if (!named) continue;
            if (best is null || score > best.Value.Score || score == best.Value.Score && entry.Path.Length < best.Value.Path.Length)
                best = (entry.Path, score);
        }
        return best is { Score: >= 8 } chosen ? chosen.Path : null;
    }

    [GeneratedRegex(@"(?:^|[^0-9])(16|20|24|32|48|64|96|128|150|180|192|256|310|512|1024)(?:[^0-9]|$)")]
    private static partial Regex SizeHint();
}
