using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ILauncher.Models;

namespace ILauncher.Services;

/// <summary>
/// 기본 목록: 모든 런처(일반 · 마스터)의 스토어에 보이는 앱.
/// 원본은 GitHub 공지 저장소(기본 i-luncher-notices, 공개)의 basic-apps.json + basic-media\ 스크린샷: 런처가 시작 · 새로고침 때 읽으므로
/// GitHub 에서 파일만 고치면 런처를 릴리스하지 않아도 반영됨. 읽은 목록은 이 PC 에 보관하고, GitHub 에 닿지 못하면 보관한 목록,
/// 그것도 없으면(처음 실행이 오프라인) 런처에 든 basic-apps.json.
/// 서버 앱 목록은 마스터 계정(빌드 설정 MASTER_USERS)만 API 서버에서 읽어 둘을 합쳐 보여 줌. 일반은 이 목록만 (API 서버에 묻지 않음).
/// </summary>
public static class BasicApps
{
    public const string ResourceName = "basic-apps.json";
    /// <summary>공지 저장소 안의 기본 목록 파일 (빌드 설정 BASIC_APPS_PATH 로 바꿈)</summary>
    public const string DefaultPath = "basic-apps.json";
    /// <summary>스크린샷 폴더: 목록 파일과 같은 폴더의 basic-media</summary>
    public const string MediaFolder = "basic-media";
    /// <summary>GitHub 기본 목록의 스크린샷 표시: "basic:&lt;저장소 안 경로&gt;#&lt;git blob sha&gt;" (sha 가 바뀌면 다시 받음)</summary>
    public const string MediaScheme = "basic:";
    public const string CacheFileName = "basic-apps-cache.json";

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

    /// <summary>
    /// GitHub 의 기본 목록. 파일이 없으면 null (런처에 든 목록을 씀). 형식이 틀리면 JsonException, 못 읽으면 GitHubApiException 등.
    /// 스크린샷 이름은 저장소의 basic-media\ 파일을 가리키게 바꿈 (그 파일의 sha 를 붙여, 사진을 바꾸면 다시 받게)
    /// </summary>
    public static async Task<List<AppDefinition>?> FetchAsync(GitHubClient client, GitHubRepository repository, string path,
        GitHubRepository? launcherRepository, CancellationToken cancellationToken)
    {
        GitHubFile? file = await client.GetFileAsync(repository, path, null, cancellationToken).ConfigureAwait(false);
        if (file is null) return null;
        var apps = Parse(file.Content, launcherRepository);
        string folder = MediaFolderFor(path);
        IReadOnlyDictionary<string, string> shas = apps.Any(app => app.Screenshots is { Count: > 0 })
            ? await client.GetFolderFilesAsync(repository, folder, null, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, string>();
        foreach (var app in apps)
            if (app.Screenshots is { Count: > 0 } shots)
                app.Screenshots = shots.Select(shot => MediaReference(shot, folder, shas)).ToList();
        return apps;
    }

    /// <summary>목록 파일 옆의 basic-media (목록이 lists/basic-apps.json 이면 lists/basic-media)</summary>
    public static string MediaFolderFor(string listPath)
    {
        string trimmed = listPath.Replace('\\', '/').Trim('/');
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? MediaFolder : trimmed[..slash] + "/" + MediaFolder;
    }

    /// <summary>파일 이름만 적힌 스크린샷 → "basic:basic-media/이름#sha". 주소 · 경로는 그대로</summary>
    public static string MediaReference(string shot, string folder, IReadOnlyDictionary<string, string> shas)
    {
        string name = shot.Trim();
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':')) return shot;
        return MediaScheme + folder + "/" + name + (shas.TryGetValue(name, out string? sha) ? "#" + sha : "");
    }

    /// <summary>"basic:..." 스크린샷의 저장소 안 경로 · 파일 이름 · sha (아니면 false)</summary>
    public static bool TryParseMedia(string shot, out string path, out string name, out string sha)
    {
        path = name = sha = "";
        if (!shot.StartsWith(MediaScheme, StringComparison.Ordinal)) return false;
        string rest = shot[MediaScheme.Length..];
        int hash = rest.IndexOf('#');
        if (hash >= 0) { sha = rest[(hash + 1)..]; rest = rest[..hash]; }
        path = rest;
        name = rest[(rest.LastIndexOf('/') + 1)..];
        return name.Length > 0;
    }

    /// <summary>git 이 파일 내용으로 만드는 blob sha (GitHub 폴더 목록의 sha 와 같음)</summary>
    public static string GitBlobSha(byte[] data)
    {
        byte[] header = Encoding.ASCII.GetBytes($"blob {data.Length}\0");
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(header);
        sha1.AppendData(data);
        return Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>GitHub 에서 읽은 목록을 이 PC 에 보관 (다음에 GitHub 에 닿지 못할 때 씀)</summary>
    public static void SaveCache(string path, IReadOnlyList<AppDefinition> apps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, CatalogSerializer.Serialize(apps), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>보관한 목록 (없거나 깨졌으면 null)</summary>
    public static List<AppDefinition>? LoadCache(string path)
    {
        try { return File.Exists(path) ? CatalogSerializer.Parse(File.ReadAllText(path, Encoding.UTF8)) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
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
