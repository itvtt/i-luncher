using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>
/// 관리자 메뉴의 릴리스 화면. 프로젝트: 앱 목록의 GitHub 저장소, 그다음 LAUNCHER_GITHUB_REPOS 의 소유자/이름 (예: 런처 자신).
/// 프로젝트마다 마지막 릴리스, 다음 버전, .github/workflows/release.yml 의 최근 실행. 릴리스는 고른 버전으로 그 워크플로를 실행(workflow_dispatch)
/// </summary>
internal sealed partial class Releases
{
    public const string Workflow = "release.yml";
    private static readonly string[] Active = ["requested", "queued", "pending", "waiting", "in_progress"];
    private const int RequestShownSeconds = 180;  // 시작한 릴리스를 GitHub 이 실행 목록에 올릴 때까지 "요청함"으로 보이는 시간

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,4})\.[0-9]\.[0-9]$")] private static partial Regex VersionText();
    [GeneratedRegex(@"(\d+)\.(\d+)\.(\d+)")] private static partial Regex VersionParts();
    [GeneratedRegex(@"[A-Za-z0-9_.\\/-]+\.csproj")] private static partial Regex ProjectFile();
    [GeneratedRegex(@"<Version>\s*(\d+)\.(\d+)(?:\.(\d+))?")] private static partial Regex ProjectVersion();

    private readonly Config _config;
    private readonly LauncherStore _launcher;
    private readonly GitHubProxy _github;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Version, DateTimeOffset When)> _requested = [];

    public Releases(Config config, LauncherStore launcher, GitHubProxy github)
    {
        _config = config;
        _launcher = launcher;
        _github = github;
    }

    public GitHubProxy GitHub => _github;

    /// <summary>태그 · 버전 (v1.2.3) 의 (주, 부, 수), 없으면 null</summary>
    public static (int, int, int)? ReleaseVersion(string? text)
    {
        var match = VersionParts().Match(text ?? "");
        return match.Success && int.TryParse(match.Groups[1].Value, out int major) && int.TryParse(match.Groups[2].Value, out int minor) && int.TryParse(match.Groups[3].Value, out int patch)
            ? (major, minor, patch) : null;
    }

    /// <summary>릴리스 워크플로가 스스로 고르는 버전: 마지막 릴리스 + 1, 자리마다 0~9 (1.0.9 → 1.1.0). 프로젝트 파일의 &lt;Version&gt; 이 바닥값</summary>
    public static string NextVersion((int Major, int Minor, int Patch)? latest, (int Major, int Minor, int Patch)? floor)
    {
        var bottom = floor ?? (1, 0, 0);
        if (latest is { } last && last.CompareTo(bottom) >= 0)
        {
            int major = last.Major, minor = last.Minor, patch = last.Patch + 1;
            if (patch > 9) (minor, patch) = (minor + 1, 0);
            if (minor > 9) (major, minor, patch) = (major + 1, 0, 0);
            return $"{major}.{minor}.{patch}";
        }
        return $"{bottom.Major}.{bottom.Minor}.{bottom.Patch}";
    }

    /// <summary>[(소유자/이름, 보이는 이름)] 런처의 순서로</summary>
    public List<(string Full, string Name)> Projects()
    {
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var apps = _launcher.Catalog()["apps"]!.AsArray().OfType<JsonObject>().Select((app, index) => (app, index))
            .OrderBy(item => Util.AsInt(item.app["order"], 1L << 30)).ThenBy(item => item.index);
        foreach (var (app, _) in apps)
        {
            if (GitHubLinks.RepositoryOf(Util.Or(app["repository"], "").Trim(), _config.GitHubHostsAll) is not { } full) continue;
            if (seen.Add(full.ToLowerInvariant())) found.Add((full, Util.Or(app["name"], full.Split('/')[1])));
        }
        foreach (string pattern in _config.GitHubProxyRepos)
            if (pattern.Count(c => c == '/') == 1 && !pattern.EndsWith("/*", StringComparison.Ordinal) && seen.Add(pattern)) found.Add((pattern, pattern.Split('/', 2)[1]));
        return found;
    }

    public static string Base(string full)
    {
        int slash = full.IndexOf('/');
        string owner = slash < 0 ? full : full[..slash], name = slash < 0 ? "" : full[(slash + 1)..];
        if (!GitHubProxy.OwnerName().IsMatch(owner) || !GitHubProxy.RepositoryName().IsMatch(name) || name is "." or "..")
            throw new RelayError(400, $"저장소 이름이 올바르지 않습니다: {full}");
        return "/repos/" + Util.Quote(owner, "") + "/" + Util.Quote(name, "");
    }

    /// <summary>GitHub API GET (기억한 응답이 ttl 초 동안 쓰임). raw 면 글, 아니면 JSON</summary>
    public (int Status, JsonNode? Json) Get(string path, int ttl)
    {
        var (status, body) = _github.Json(_config.GitHubApi + path, "application/vnd.github+json", ttl);
        try { return (status, body.Length > 0 ? JsonNode.Parse(body) : null); }
        catch (JsonException) { return (status, null); }
    }

    public (int Status, string Text) GetRaw(string path, int ttl)
    {
        var (status, body) = _github.Json(_config.GitHubApi + path, "application/vnd.github.raw", ttl);
        return (status, status == 200 ? Encoding.UTF8.GetString(Util.StripBom(body)) : "");
    }

    public static string Problem(int status, JsonNode? answer)
    {
        string message = answer is JsonObject value ? Util.Or(value["message"], "") : "";
        return $"GitHub 오류 {status}" + (message.Length > 0 ? ": " + message : "");
    }

    public JsonObject Info(string full, string name)
    {
        var entry = new JsonObject
        {
            ["repository"] = full, ["name"] = name, ["latest"] = "", ["latestTag"] = "", ["latestAt"] = "", ["floor"] = "", ["next"] = "",
            ["chooseVersion"] = false, ["run"] = null, ["error"] = "",
        };
        try
        {
            string repoBase = Base(full);
            var (status, release) = Get(repoBase + "/releases/latest", 10);
            (int, int, int)? latest = null;
            if (status == 200 && release is JsonObject found)
            {
                entry["latestTag"] = Util.Or(found["tag_name"], "");
                entry["latestAt"] = Util.Or(found["published_at"], Util.Or(found["created_at"], ""));
                latest = ReleaseVersion(Util.Text(entry["latestTag"]));
                entry["latest"] = latest is { } value ? $"{value.Item1}.{value.Item2}.{value.Item3}" : "";
            }
            else if (status != 404) throw new RelayError(502, Problem(status, release));
            var (workflowStatus, workflow) = GetRaw($"{repoBase}/contents/.github/workflows/{Workflow}", 60);
            if (workflowStatus != 200)
                throw new RelayError(404, workflowStatus == 404 ? $"릴리스 워크플로(.github/workflows/{Workflow})가 없습니다." : $"GitHub 오류 {workflowStatus}");
            entry["chooseVersion"] = workflow.Contains("inputs.version", StringComparison.Ordinal);
            // 워크플로가 빌드하는 프로젝트 파일: 그 <Version> 이 바닥값 (워크플로의 버전 단계와 같게)
            (int, int, int)? floor = null;
            var project = ProjectFile().Match(workflow);
            if (project.Success)
            {
                string path = string.Join("/", project.Value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(part => Util.Quote(part, "")));
                var (projectStatus, text) = GetRaw($"{repoBase}/contents/{path}", 60);
                var version = projectStatus == 200 ? ProjectVersion().Match(text) : Match.Empty;
                if (version.Success)
                {
                    floor = (int.Parse(version.Groups[1].Value), int.Parse(version.Groups[2].Value), version.Groups[3].Success ? int.Parse(version.Groups[3].Value) : 0);
                    entry["floor"] = $"{floor.Value.Item1}.{floor.Value.Item2}.{floor.Value.Item3}";
                }
            }
            entry["next"] = NextVersion(latest, floor);
            var (runsStatus, runs) = Get($"{repoBase}/actions/workflows/{Workflow}/runs?per_page=1", 0);
            if (runsStatus == 200 && runs is JsonObject list && list["workflow_runs"] is JsonArray { Count: > 0 } items && items[0] is JsonObject run)
                entry["run"] = new JsonObject
                {
                    ["status"] = Util.Or(run["status"], ""), ["conclusion"] = Util.Or(run["conclusion"], ""), ["event"] = Util.Or(run["event"], ""),
                    ["createdAt"] = Util.Or(run["created_at"], ""), ["url"] = Util.Or(run["html_url"], ""), ["number"] = run["run_number"]?.DeepClone(),
                };
        }
        catch (RelayError error) { entry["error"] = error.Message; }
        // 여기서 시작했지만 GitHub 이 아직 실행 목록에 올리지 않음 (몇 초 걸림): 요청함으로
        (string Version, DateTimeOffset When)? requested;
        lock (_lock) requested = _requested.TryGetValue(full.ToLowerInvariant(), out var value) ? value : null;
        if (requested is { } started && (Util.Now() - started.When).TotalSeconds < RequestShownSeconds)
        {
            var run = entry["run"] as JsonObject;
            if (run is null || Util.ParseTime(run["createdAt"], DateTimeOffset.MinValue) < started.When.AddSeconds(-5))
                entry["run"] = new JsonObject
                {
                    ["status"] = "requested", ["conclusion"] = "", ["event"] = "workflow_dispatch", ["createdAt"] = Util.Iso(started.When), ["url"] = "",
                    ["number"] = null, ["version"] = started.Version,
                };
            else if (Util.Text(run["event"]) == "workflow_dispatch") run["version"] = started.Version;
        }
        return entry;
    }

    public JsonArray List()
    {
        var projects = Projects();
        var results = new JsonObject[projects.Count];
        Parallel.For(0, projects.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index => results[index] = Info(projects[index].Full, projects[index].Name));
        return new JsonArray(results.Select(item => (JsonNode)item).ToArray());
    }

    public JsonObject Release(JsonNode? repositoryNode, JsonNode? versionNode)
    {
        if (Util.AsString(repositoryNode) is not { } repository || Util.AsString(versionNode) is not { } version) throw new RelayError(400, "저장소와 버전이 필요합니다.");
        version = version.Trim();
        if (version.StartsWith('v') || version.StartsWith('V')) version = version[1..];
        var known = Projects().FirstOrDefault(project => project.Full.ToLowerInvariant() == repository.Trim().ToLowerInvariant());
        if (known.Full is null) throw new RelayError(404, "릴리스 목록에 없는 저장소입니다(앱 목록 또는 LAUNCHER_GITHUB_REPOS).");
        var (full, name) = known;
        if (!VersionText().IsMatch(version)) throw new RelayError(400, "버전은 1.2.3 형식으로 쓰세요 (가운데·끝 자리는 0~9).");
        string token = _github.Token();
        if (token.Length == 0) throw new RelayError(503, "GitHub 토큰이 없습니다(RELAY_GITHUB_TOKEN 또는 이 PC의 git 자격 증명).");
        string repoBase = Base(full);
        lock (_lock)
            if (_requested.TryGetValue(full.ToLowerInvariant(), out var requested) && (Util.Now() - requested.When).TotalSeconds < 60)
                throw new RelayError(409, $"{name}은(는) 방금 v{requested.Version} 릴리스를 시작했습니다.");
        _github.Forget(_config.GitHubApi + repoBase);
        var info = Info(full, name);
        if (Util.Text(info["error"]).Length > 0) throw new RelayError(409, $"{name}: {Util.Text(info["error"])}");
        if (info["run"] is JsonObject run && Active.Contains(Util.Text(run["status"]))) throw new RelayError(409, $"{name}의 릴리스가 이미 진행 중입니다. 끝난 뒤에 다시 하세요.");
        var latest = ReleaseVersion(Util.Text(info["latest"]));
        if (latest is { } last && ReleaseVersion(version) is { } wanted && wanted.CompareTo(last) <= 0)
            throw new RelayError(409, $"v{version}은(는) 마지막 릴리스 {Util.Text(info["latestTag"])}보다 커야 합니다.");
        if (!Util.IsTrue(info["chooseVersion"]) && version != Util.Text(info["next"]))
            throw new RelayError(409, $"{name}의 릴리스 워크플로는 버전을 받지 않아 다음 버전 {Util.Text(info["next"])}만 릴리스할 수 있습니다.");
        foreach (string tag in new[] { "v" + version, version })
            if (Get($"{repoBase}/git/ref/tags/{Util.Quote(tag, "")}", 0).Status == 200) throw new RelayError(409, $"{tag} 태그가 이미 있습니다.");
        var (repoStatus, repo) = Get(repoBase, 300);
        string branch = repoStatus == 200 && repo is JsonObject found ? Util.Or(found["default_branch"], "") : "";
        if (branch.Length == 0) throw new RelayError(502, Problem(repoStatus, repo));
        var payload = new JsonObject { ["ref"] = branch };
        if (Util.IsTrue(info["chooseVersion"])) payload["inputs"] = new JsonObject { ["version"] = version };
        var (status, answer) = Call("POST", $"{repoBase}/actions/workflows/{Workflow}/dispatches", token, payload);
        if (status >= 400)
        {
            string hint = status is 401 or 403 or 404 ? " 토큰에 workflow 권한이 있는지 확인하세요." : "";
            throw new RelayError(502, $"GitHub이 릴리스 실행을 거절했습니다. {Problem(status, answer)}{hint}");
        }
        lock (_lock) _requested[full.ToLowerInvariant()] = (version, Util.Now());
        _github.Forget(_config.GitHubApi + repoBase);
        Log.Info($"release {full} v{version} started from the admin window ({branch})");
        return new JsonObject { ["repository"] = full, ["name"] = name, ["version"] = version, ["ref"] = branch };
    }

    /// <summary>바꾸는 GitHub 요청 하나 (기억하지 않음): (상태, JSON)</summary>
    public (int Status, JsonNode? Json) Call(string method, string path, string token, JsonNode? body = null)
    {
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(method), _config.GitHubApi + path);
            foreach (var (name, value) in GitHubProxy.Headers("application/vnd.github+json", token)) message.Headers.TryAddWithoutValidation(name, value);
            if (body is not null) message.Content = new StringContent(body.ToJsonString(Util.Compact), Util.Utf8, "application/json");
            else if (method == "POST") message.Content = new ByteArrayContent([]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = GitHubProxy.Http.Send(message, timeout.Token);
            byte[] data = GitHubLinks.ReadLimited(response.Content.ReadAsStream(timeout.Token), 4 * 1024 * 1024);
            try { return ((int)response.StatusCode, data.Length > 0 ? JsonNode.Parse(data) : null); }
            catch (JsonException) { return ((int)response.StatusCode, null); }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            throw new RelayError(502, $"GitHub에 연결하지 못했습니다: {(error is TaskCanceledException or OperationCanceledException ? "timed out" : error.Message)}");
        }
    }
}
