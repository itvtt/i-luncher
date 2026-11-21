using System.Text;
using ILauncher.Models;

namespace ILauncher.Services;

/// <summary>
/// 기본 목록: 런처에 든 basic-apps.json (저장소 맨 위, 런처 버전과 함께 바뀜). 누구에게나 보임.
/// 서버 앱 목록은 마스터 계정(빌드 설정 MASTER_USERS)만 API 서버에서 읽어 둘을 합쳐 보여 줌. 일반은 이 목록만 (서버에 묻지 않음).
/// </summary>
public static class BasicApps
{
    public const string ResourceName = "basic-apps.json";

    /// <summary>런처에 든 기본 목록. 저장소 칸이 이름만이면(DeskCal) 런처 저장소와 같은 호스트 · 소유자</summary>
    public static List<AppDefinition> Load(GitHubRepository? launcherRepository)
    {
        using Stream? stream = typeof(BasicApps).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return [];
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd(), launcherRepository);
    }

    public static List<AppDefinition> Parse(string json, GitHubRepository? launcherRepository)
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

    /// <summary>서버 앱 목록 + 기본 목록에만 있는 앱 (같은 앱이면 서버 목록 값)</summary>
    public static List<AppDefinition> Combine(IReadOnlyList<AppDefinition> basic, IReadOnlyList<AppDefinition> server)
    {
        var all = server.ToList();
        foreach (var app in basic)
            if (!all.Any(other => other.HasId(app.Id) || app.HasId(other.Id))) all.Add(app);
        return all;
    }
}
