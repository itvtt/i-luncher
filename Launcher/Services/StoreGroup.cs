using System.Text;
using ILauncher.Models;

namespace ILauncher.Services;

/// <summary>
/// 스토어 그룹. 일반 = 런처에 든 기본 목록(저장소의 basic-apps.json, 런처 버전과 함께 바뀜)만,
/// 부서 = 기본 목록 + API 서버의 앱 목록(관리자 메뉴에서 고치면 바로 반영).
/// 설치 파일이 데이터 폴더(%LOCALAPPDATA%\i-luncher)의 store-group.txt 에 적음:
/// i-luncher-Team-Setup.exe = team, i-luncher-Setup.exe = 파일이 없을 때만 basic (부서를 일반으로 되돌리지 않음).
/// 런처 업데이트 · 제거해도 이 폴더는 남으므로 그룹이 유지됨.
/// </summary>
public static class StoreGroup
{
    public const string FileName = "store-group.txt";
    public const string Basic = "basic", Team = "team";
    public const string ResourceName = "basic-apps.json";

    /// <summary>
    /// 이 PC · 사용자의 그룹. 표시가 없으면: 이 기능 전부터 런처를 쓰던 PC(설정·목록 파일이 이미 있음)는 예전처럼 모든 앱이 보이게 부서,
    /// 아니면 일반. 정한 값은 파일에 남김
    /// </summary>
    public static string Resolve(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, FileName);
        try
        {
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path).Trim();
                if (text.Equals(Team, StringComparison.OrdinalIgnoreCase)) return Team;
                if (text.Equals(Basic, StringComparison.OrdinalIgnoreCase)) return Basic;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Basic; }

        bool usedBefore = new[] { "settings.json", "catalog-cache.json", "catalog.json", "my-apps.json", "favorites.json", "usage.json" }
            .Any(name => File.Exists(Path.Combine(dataDirectory, name)));
        string group = usedBefore ? Team : Basic;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(path, group + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return group;
    }

    /// <summary>런처에 든 기본 목록. 저장소 칸이 이름만이면(DeskCal) 런처 저장소와 같은 호스트 · 소유자</summary>
    public static List<AppDefinition> LoadBasicApps(GitHubRepository? launcherRepository)
    {
        using Stream? stream = typeof(StoreGroup).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return [];
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return ParseBasicApps(reader.ReadToEnd(), launcherRepository);
    }

    public static List<AppDefinition> ParseBasicApps(string json, GitHubRepository? launcherRepository)
    {
        var apps = CatalogSerializer.Parse(json);
        foreach (var app in apps)
            app.Repository = ResolveRepository(app.Repository, launcherRepository);
        return apps;
    }

    public static string ResolveRepository(string repository, GitHubRepository? launcherRepository)
    {
        string text = repository.Trim();
        if (launcherRepository is null || text.Length == 0 || text.Contains('/') || text.Contains(':')) return text;
        string name = text.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;
        return (launcherRepository with { Name = name }).WebUrl;
    }

    /// <summary>부서: API 목록 + 기본 목록에만 있는 앱 (같은 앱이면 API 쪽 값)</summary>
    public static List<AppDefinition> Combine(IReadOnlyList<AppDefinition> basic, IReadOnlyList<AppDefinition> api)
    {
        var all = api.ToList();
        foreach (var app in basic)
            if (!all.Any(other => other.HasId(app.Id) || app.HasId(other.Id))) all.Add(app);
        return all;
    }
}
