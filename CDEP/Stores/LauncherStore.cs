using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>
/// i-luncher 의 서버 앱 목록(catalog.json, 런처가 저장소에 두던 것과 같은 JSON)과 런처 공지. revision = 저장한 바이트의 SHA-256:
/// 저장할 때 시작한 revision 을 함께 보내므로 두 관리자가 서로 덮어쓰지 않음
/// </summary>
internal sealed partial class LauncherStore
{
    public const int CatalogMax = 1_048_576, HistoryKeep = 50, MediaMax = 8 * 1024 * 1024, ScreenshotsMax = 12, DetailsMax = 20_000;
    private const int MediaKeepUnusedSeconds = 86400;
    public static readonly Dictionary<string, string> MediaTypes = new() { ["png"] = "image/png", ["jpg"] = "image/jpeg" };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")] private static partial Regex AppId();
    /// <summary>앱 페이지 스크린샷: 내용의 SHA-256 앞 16자리라 이름의 뜻이 바뀌지 않고 런처가 계속 캐시함</summary>
    [GeneratedRegex(@"^[a-f0-9]{16}\.(png|jpg)$")] public static partial Regex MediaName();
    [GeneratedRegex(@"^https?://\S{1,500}$")] private static partial Regex WebAddress();

    private readonly string _directory;
    private readonly string[] _hosts;
    private readonly Lock _lock = new();
    private HashSet<string>? _repositories;

    public LauncherStore(string directory, string[]? hosts = null)
    {
        _directory = directory;
        _hosts = hosts ?? Config.GitHubHosts;
        Directory.CreateDirectory(directory);
    }

    private (byte[] Data, string Revision) ReadFile(string name)
    {
        string path = Path.Combine(_directory, name);
        if (!File.Exists(path)) return ([], "");
        byte[] data = Util.ReadBytes(path);
        return (data, Util.Sha256Hex(data));
    }

    private string SaveFile(string name, byte[] data, string expectedRevision)
    {
        lock (_lock)
        {
            var (current, revision) = ReadFile(name);
            if ((expectedRevision ?? "") != revision) throw new RelayError(409, "다른 곳에서 먼저 바꿨습니다. 새로 고친 뒤 다시 저장하세요.");
            if (current.Length > 0)
            {
                string history = Path.Combine(_directory, "history");
                Directory.CreateDirectory(history);
                File.WriteAllBytes(Path.Combine(history, $"{Util.Stamp()}-{name}"), current);
                var files = Directory.EnumerateFileSystemEntries(history).Order(StringComparer.Ordinal).ToList();
                foreach (string old in files.Take(Math.Max(0, files.Count - HistoryKeep * 2)))
                {
                    try { File.Delete(old); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            Util.AtomicWrite(Path.Combine(_directory, name), data);
            _repositories = null;
            return Util.Sha256Hex(data);
        }
    }

    public JsonObject Catalog()
    {
        var (data, revision) = ReadFile("catalog.json");
        var apps = new JsonArray();
        if (data.Length > 0)
        {
            try
            {
                var value = JsonNode.Parse(Util.StripBom(data));
                var list = value is JsonObject document ? document["apps"] as JsonArray : value as JsonArray;
                foreach (var app in list ?? []) if (app is JsonObject) apps.Add(app.DeepClone());
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            {
                Log.Warning("launcher catalog.json is not JSON; serving an empty list");
            }
        }
        return new JsonObject { ["schemaVersion"] = 1, ["apps"] = apps, ["revision"] = revision };
    }

    public static JsonArray Validate(JsonNode? value)
    {
        if (value is not JsonArray apps) throw new RelayError(400, "apps는 목록이어야 합니다.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in apps)
        {
            if (node is not JsonObject app) throw new RelayError(400, "앱마다 JSON 객체여야 합니다.");
            string id = Util.Or(app["id"], "").Trim(), name = Util.Or(app["name"], "").Trim(), repository = Util.Or(app["repository"], "").Trim();
            if (!AppId().IsMatch(id)) throw new RelayError(400, $"앱 id가 올바르지 않습니다: {(id.Length > 0 ? id : "(비어 있음)")}");
            if (!seen.Add(id)) throw new RelayError(400, $"같은 앱 id가 두 번 있습니다: {id}");
            if (name.Length is 0 or > 100) throw new RelayError(400, $"{id}: 이름이 비었거나 너무 깁니다.");
            if (repository.Length is 0 or > 512) throw new RelayError(400, $"{id}: 저장소 주소가 비었거나 너무 깁니다.");
            var details = app["details"];
            if (details is not null && (Util.AsString(details) is not { } detailsText || detailsText.Length > DetailsMax))
                throw new RelayError(400, $"{id}: 상세 설명은 {DetailsMax}자까지 씁니다.");
            var shots = app["screenshots"];
            if (shots is not null && (shots is not JsonArray list || list.Count > ScreenshotsMax ||
                    !list.All(shot => Util.AsString(shot) is { } text && (MediaName().IsMatch(text) || WebAddress().IsMatch(text)))))
                throw new RelayError(400, $"{id}: 스크린샷은 {ScreenshotsMax}장까지, 올린 이미지 이름이나 http(s) 주소로 씁니다.");
        }
        return apps;
    }

    public JsonObject SaveCatalog(JsonNode? apps, string expectedRevision)
    {
        var valid = Validate(apps);
        var document = new JsonObject { ["schemaVersion"] = 1, ["apps"] = valid.DeepClone() };
        byte[] data = Util.Utf8.GetBytes(document.ToJsonString(Util.Indent2).Replace("\r\n", "\n") + "\n");
        if (data.Length > CatalogMax) throw new RelayError(400, "앱 목록이 너무 큽니다(1 MB까지).");
        SaveFile("catalog.json", data, expectedRevision);
        CleanMedia();
        return Catalog();
    }

    // 스크린샷 ────────────────────────────────────────────────────────────────────────────────────

    public string MediaDirectory => Path.Combine(_directory, "media");

    public string MediaFile(string name)
    {
        string path = Path.Combine(MediaDirectory, name);
        if (!MediaName().IsMatch(name) || !File.Exists(path)) throw new RelayError(404, "이미지를 찾지 못했습니다.");
        return path;
    }

    /// <summary>올린 PNG · JPG 를 남김: 이름은 SHA-256 앞 16자리 (같은 그림 두 번은 파일 하나)</summary>
    public string AddMedia(byte[] data)
    {
        if (data.Length == 0) throw new RelayError(400, "이미지가 비어 있습니다.");
        if (data.Length > MediaMax) throw new RelayError(413, "이미지는 8 MB까지 올릴 수 있습니다.");
        string kind = data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]) ? "png"
            : data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "jpg" : "";
        if (kind.Length == 0) throw new RelayError(415, "PNG나 JPG 이미지만 올릴 수 있습니다.");
        string name = $"{Util.Sha256Hex(data)[..16].ToLowerInvariant()}.{kind}";
        string path = Path.Combine(MediaDirectory, name);
        if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);  // 새로 올린 것으로: 앱 목록이 쓸 때까지 남김
        else Util.AtomicWrite(path, data);
        return name;
    }

    /// <summary>어느 앱도 쓰지 않는 이미지를 하루가 지나면 지움 (올린 것은 앱 목록 저장을 기다림)</summary>
    public int CleanMedia()
    {
        var named = Catalog()["apps"]!.AsArray().OfType<JsonObject>().SelectMany(app => app["screenshots"] as JsonArray ?? [])
            .Select(Util.AsString).OfType<string>().ToHashSet(StringComparer.Ordinal);
        int removed = 0;
        if (!Directory.Exists(MediaDirectory)) return 0;
        foreach (var file in new DirectoryInfo(MediaDirectory).EnumerateFiles())
        {
            try
            {
                if (!named.Contains(file.Name) && (DateTime.UtcNow - file.LastWriteTimeUtc).TotalSeconds > MediaKeepUnusedSeconds)
                {
                    file.Delete();
                    removed++;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    // 서버 목록 원본 (seed\apps.json + seed\media, i-api 에 들어 있음) ─────────────────────────────

    public const string SeedAppliedFile = "seed-applied.json";

    /// <summary>
    /// i-api 에 든 서버 목록 원본에서 이 서버가 아직 받은 적 없는 앱을 앱 목록에 더함 (설명 · 상세 설명 · 개발자 · 스크린샷까지 원본 그대로).
    /// 한 번 본 앱 id 는 seed-applied.json 에 남겨, 관리자가 목록에서 뺀 앱은 다시 넣지 않음. 이미 목록에 있는 앱(id · 예전 id)은 건드리지 않음.
    /// 저장소 주소 = 목록의 다른 앱과 같은 호스트/소유자 (목록이 비면 repositoryBase). 모르면 넣지 않고 다음 시작 때 다시 봄.
    /// 더한 앱 이름들을 돌려줌
    /// </summary>
    public List<string> AddSeedApps(JsonArray seed, Func<string, byte[]?> media, string repositoryBase)
    {
        lock (_lock)
        {
            string appliedPath = Path.Combine(_directory, SeedAppliedFile);
            var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(appliedPath))
            {
                try
                {
                    foreach (var id in JsonNode.Parse(Util.StripBom(Util.ReadBytes(appliedPath))) as JsonArray ?? []) if (Util.AsString(id) is { } text) applied.Add(text);
                }
                catch (JsonException) { }
            }
            var fresh = seed.OfType<JsonObject>().Where(app => Util.Or(app["id"], "") is { Length: > 0 } id && !applied.Contains(id)).ToList();
            if (fresh.Count == 0) return [];

            var catalog = Catalog();
            var apps = catalog["apps"]!.AsArray();
            string owner = RepositoryBase(apps) ?? repositoryBase.Trim().TrimEnd('/');
            var added = new List<string>();
            foreach (var app in fresh)
            {
                string id = Util.Or(app["id"], "");
                bool listed = apps.OfType<JsonObject>().Any(existing => string.Equals(Util.Or(existing["id"], ""), id, StringComparison.OrdinalIgnoreCase)
                    || (existing["formerIds"] as JsonArray ?? []).Any(former => string.Equals(Util.AsString(former), id, StringComparison.OrdinalIgnoreCase)));
                if (listed)
                {
                    applied.Add(id);
                    continue;
                }
                string repository = Util.Or(app["repository"], "").Trim();
                if (!repository.Contains("://", StringComparison.Ordinal))
                {
                    if (owner.Length == 0) continue;  // 저장소 주소를 모름: 다음 시작 때 다시
                    repository = $"{owner}/{repository}";
                }
                var copy = (JsonObject)app.DeepClone();
                copy["repository"] = repository;
                if (app["screenshots"] is JsonArray shots)
                {
                    var names = new JsonArray();
                    foreach (string shot in shots.Select(Util.AsString).OfType<string>())
                    {
                        if (media(shot) is { Length: > 0 } image) names.Add(AddMedia(image));
                        else if (MediaName().IsMatch(shot) || WebAddress().IsMatch(shot)) names.Add(shot);
                    }
                    copy["screenshots"] = names;
                }
                apps.Add(copy);
                applied.Add(id);
                added.Add(Util.Or(app["name"], id));
            }
            if (added.Count > 0)
            {
                var document = new JsonObject { ["schemaVersion"] = 1, ["apps"] = Validate(apps).DeepClone() };
                SaveFile("catalog.json", Util.Utf8.GetBytes(document.ToJsonString(Util.Indent2).Replace("\r\n", "\n") + "\n"), Util.Or(catalog["revision"], ""));
            }
            Util.AtomicWrite(appliedPath, Util.Utf8.GetBytes(new JsonArray([.. applied.Order(StringComparer.OrdinalIgnoreCase).Select(id => (JsonNode)id)]).ToJsonString(Util.Indent2) + "\n"));
            return added;
        }
    }

    /// <summary>목록의 앱이 가장 많이 쓰는 저장소 주소 앞부분 (https://호스트/소유자). 없으면 null</summary>
    private static string? RepositoryBase(JsonArray apps) =>
        apps.OfType<JsonObject>().Select(app => Util.Or(app["repository"], "").Trim())
            .Select(repository => Uri.TryCreate(repository, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                && uri.AbsolutePath.Trim('/').Split('/') is [var owner, _, ..] && owner.Length > 0
                    ? $"{uri.Scheme}://{uri.Authority}/{owner}" : null)
            .OfType<string>().GroupBy(prefix => prefix, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).Select(group => group.First()).FirstOrDefault();

    public JsonObject Notices()
    {
        var (data, revision) = ReadFile("notices.txt");
        return new JsonObject { ["text"] = System.Text.Encoding.UTF8.GetString(Util.StripBom(data)), ["revision"] = revision };
    }

    public JsonObject SaveNotices(JsonNode? text, string expectedRevision)
    {
        if (Util.AsString(text) is not { } content) throw new RelayError(400, "공지 내용이 없습니다.");
        byte[] data = Util.Utf8.GetBytes(content.Replace("\r\n", "\n"));
        if (data.Length > Announcements.MaxBytes) throw new RelayError(400, "공지는 UTF-8 기준 256KB까지 저장할 수 있습니다.");
        SaveFile("notices.txt", data, expectedRevision);
        return Notices();
    }

    /// <summary>앱 목록의 GitHub 저장소 소유자/이름 (소문자, github.com 또는 RELAY_GITHUB_API 의 Enterprise 호스트): GitHub 중계가 열어 줌</summary>
    public HashSet<string> Repositories()
    {
        var known = _repositories;
        if (known is not null) return known;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in Catalog()["apps"]!.AsArray().OfType<JsonObject>())
            if (GitHubLinks.RepositoryOf(Util.Or(app["repository"], "").Trim(), _hosts) is { } full) found.Add(full.ToLowerInvariant());
        return _repositories = found;
    }
}
