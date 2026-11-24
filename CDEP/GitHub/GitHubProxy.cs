using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>
/// 토큰이 없는 앱 대신 GitHub 에서 읽음: 릴리스 목록 · 릴리스 파일 · 저장소 파일과 트리, 열어 준 저장소만, GET 만.
/// JSON 응답의 api.github.com 주소는 이 서버로 바꿔, 런처 · 앱이 파일 주소를 따라오면 다시 여기로 옴.
/// 릴리스 파일은 흘려보내고, GitHub 이 파일 저장소로 보내는 주소는 토큰 없이 따라감 (저장소가 두 번째 인증을 거절함)
/// </summary>
internal sealed partial class GitHubProxy
{
    private static readonly string[] Accepts = ["application/vnd.github+json", "application/json", "application/octet-stream",
        "application/vnd.github.v3.raw", "application/vnd.github.raw", "application/vnd.github.raw+json"];
    private static readonly string[] QueryKeys = ["ref", "recursive", "per_page", "page"];
    private const int JsonTtl = 60, FileTtl = 300, JsonMax = 32 * 1024 * 1024;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,38}$")] public static partial Regex OwnerName();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")] public static partial Regex RepositoryName();

    /// <summary>되돌려 보내지 않는 HTTP (주소를 따라갈 때 토큰을 같은 호스트에만 보내려고 직접 따라감)</summary>
    internal static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly Config _config;
    private readonly LauncherStore _launcher;
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Url, string Accept), (long Until, int Status, byte[] Body)> _cache = [];
    private (string Value, long Until) _token = ("", 0), _login = ("", 0);

    /// <summary>확인 코드가 git 자격 증명을 흉내 냄</summary>
    internal Func<string, string> CredentialLookup = GitHubLinks.GitCredentialToken;

    public GitHubProxy(Config config, LauncherStore launcher)
    {
        _config = config;
        _launcher = launcher;
    }

    // 토큰과 그 계정 ─────────────────────────────────────────────────────────────────────────────────

    public string Token()
    {
        if (_config.GitHubToken.Length > 0) return _config.GitHubToken;
        if (!_config.GitHubGitCredential) return "";
        lock (_lock)
            if (Environment.TickCount64 < _token.Until) return _token.Value;
        string value = CredentialLookup(_config.GitHubWebHost);
        // 한 시간 기억, 없으면 5분 뒤 다시 물음
        lock (_lock) _token = (value, Environment.TickCount64 + (value.Length > 0 ? 3_600_000 : 300_000));
        return value;
    }

    public string Login()
    {
        string token = Token();
        if (token.Length == 0) return "";
        lock (_lock)
            if (Environment.TickCount64 < _login.Until) return _login.Value;
        string value = "";
        try
        {
            var (status, body) = Get(_config.GitHubApi + "/user", Headers("application/vnd.github+json", token), 15, 1_048_576);
            if (status == 200 && JsonNode.Parse(body) is JsonObject user) value = Util.Text(user["login"]).ToLowerInvariant();
        }
        catch (Exception error) when (error is RelayError or JsonException)
        {
            Log.Warning("GitHub account behind the relay token not known: " + error.Message);
        }
        lock (_lock) _login = (value, Environment.TickCount64 + (value.Length > 0 ? 3_600_000 : 300_000));
        return value;
    }

    public static Dictionary<string, string> Headers(string accept, string token)
    {
        var headers = new Dictionary<string, string> { ["Accept"] = accept, ["User-Agent"] = "ApiServer/" + Program.Version, ["X-GitHub-Api-Version"] = "2022-11-28" };
        if (token.Length > 0) headers["Authorization"] = "Bearer " + token;
        return headers;
    }

    public bool Allowed(string owner, string name)
    {
        string full = $"{owner}/{name}".ToLowerInvariant();
        foreach (string pattern in _config.GitHubProxyRepos)
            if (pattern == full || pattern.EndsWith("/*", StringComparison.Ordinal) && pattern[..^2] == owner.ToLowerInvariant()) return true;
        if (_launcher.Repositories().Contains(full)) return true;
        string login = Login();
        return login.Length > 0 && login == owner.ToLowerInvariant();
    }

    // 요청 ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>/api/v3/repos/&lt;소유자&gt;/&lt;이름&gt;/... 의 열어 준 모양: 소유자, 이름, API 경로</summary>
    public static (string Owner, string Name, string Path) Route(IReadOnlyList<string> parts)
    {
        if (parts.Count < 3 || parts[0] != "repos") throw new RelayError(404, "GitHub 중계는 저장소(/api/v3/repos/...)만 읽습니다.");
        string owner = parts[1], name = parts[2];
        var tail = parts.Skip(3).ToList();
        if (!OwnerName().IsMatch(owner) || !RepositoryName().IsMatch(name) || name is "." or "..") throw new RelayError(404, "저장소 이름이 올바르지 않습니다.");
        bool shapes = tail.Count == 0 || tail is ["releases"] || tail is ["releases", "latest"]
            || tail.Count == 3 && tail[0] == "releases" && tail[1] == "tags"
            || tail.Count == 3 && tail[0] == "releases" && tail[1] == "assets" && tail[2].Length > 0 && tail[2].All(char.IsAsciiDigit)
            || tail.Count >= 2 && tail[0] == "contents" && !tail.Contains("..")
            || tail.Count == 3 && tail[0] == "git" && tail[1] == "trees";
        if (!shapes) throw new RelayError(404, "GitHub 중계가 읽지 않는 주소입니다.");
        string path = "/repos/" + string.Join("/", new[] { owner, name }.Concat(tail).Select(part => Util.Quote(part, "")));
        return (owner, name, path);
    }

    public void Serve(Request request, IReadOnlyList<string> parts, string publicBase)
    {
        var (owner, name, path) = Route(parts);
        if (!Allowed(owner, name))
            throw new RelayError(404, $"{owner}/{name}은(는) 이 서버의 GitHub 중계에 열려 있지 않습니다(LAUNCHER_GITHUB_REPOS 또는 런처 앱 목록).");
        string query = string.Join("&", QueryKeys.Where(key => request.QueryValue(key) is not null)
            .Select(key => Util.Quote(key, "") + "=" + Util.Quote(request.QueryValue(key)!, "").Replace("%20", "+")));
        string accept = (request.Header("Accept") ?? "").Split(',')[0].Trim().ToLowerInvariant();
        if (!Accepts.Contains(accept)) accept = "application/vnd.github+json";
        string url = _config.GitHubApi + path + (query.Length > 0 ? "?" + query : "");
        if (accept == "application/octet-stream" || accept.Contains("raw", StringComparison.Ordinal))
        {
            Stream(request, url, accept);
            return;
        }
        string relayBase = publicBase + "/api/v3";
        var (status, body) = Json(url, accept, path.Contains("/contents/", StringComparison.Ordinal) || path.Contains("/git/trees/", StringComparison.Ordinal) ? FileTtl : JsonTtl);
        // GitHub 이 준 응답 그대로, 그 API 주소만 이 서버로 (파일 주소 · 저장소 주소)
        string text = Encoding.UTF8.GetString(body).Replace(_config.GitHubApi + "/", relayBase + "/");
        request.Send(status, Util.Utf8.GetBytes(text), "application/json; charset=utf-8");
    }

    /// <summary>GET 하나 (같은 호스트로 옮기라는 응답은 토큰과 함께 따라감). (상태, 본문)</summary>
    internal static (int Status, byte[] Body) Get(string url, Dictionary<string, string> headers, int seconds, int limit)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            for (int hop = 0; ; hop++)
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var (name, value) in headers) message.Headers.TryAddWithoutValidation(name, value);
                using var response = Http.Send(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location && hop < 5)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(new Uri(url), location);
                    if (!string.Equals(next.Authority, new Uri(url).Authority, StringComparison.OrdinalIgnoreCase))
                        headers = headers.Where(pair => pair.Key != "Authorization").ToDictionary();
                    url = next.ToString();
                    continue;
                }
                using var stream = response.Content.ReadAsStream(timeout.Token);
                byte[] body = GitHubLinks.ReadLimited(stream, status >= 400 ? 65_536 : limit + 1);
                return (status, body);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or InvalidOperationException or UriFormatException)
        {
            throw new RelayError(502, $"GitHub에 연결하지 못했습니다: {(error is TaskCanceledException or OperationCanceledException ? "timed out" : error.Message)}");
        }
    }

    public (int Status, byte[] Body) Json(string url, string accept, int ttl)
    {
        var key = (url, accept);
        lock (_lock)
            if (_cache.TryGetValue(key, out var hit) && Environment.TickCount64 < hit.Until) return (hit.Status, hit.Body);
        var (status, body) = Get(url, Headers(accept, Token()), 30, JsonMax);
        if (body.Length > JsonMax) throw new RelayError(502, "GitHub 응답이 너무 큽니다.");
        if (status == 200)
        {
            lock (_lock)
            {
                if (_cache.Count > 500) _cache.Clear();
                _cache[key] = (Environment.TickCount64 + ttl * 1000L, status, body);
            }
        }
        return (status, body);
    }

    private void Stream(Request request, string url, string accept)
    {
        string token = Token();
        HttpResponseMessage? response = null;
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            using (var first = new HttpRequestMessage(HttpMethod.Get, url))
            {
                foreach (var (name, value) in Headers(accept, token)) first.Headers.TryAddWithoutValidation(name, value);
                response = Http.Send(first, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                // GitHub 의 파일 저장소: 서명된 주소라 토큰을 보내면 안 됨
                var next = location.IsAbsoluteUri ? location : new Uri(new Uri(url), location);
                response.Dispose();
                var storage = new HttpRequestMessage(HttpMethod.Get, next);
                storage.Headers.TryAddWithoutValidation("User-Agent", "ApiServer/" + Program.Version);
                response = FollowPlain(storage, timeout.Token);
            }
            else if (status >= 400)
            {
                byte[] error = GitHubLinks.ReadLimited(response.Content.ReadAsStream(timeout.Token), 65_536);
                request.Send(status, error, "application/json; charset=utf-8");
                return;
            }
            timeout.CancelAfter(Timeout.Infinite);  // 받는 동안은 소켓마다 기다림 (큰 파일)
            using var source = response.Content.ReadAsStream();
            long? length = response.Content.Headers.ContentLength;
            var target = request.StartStream((int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", length);
            source.CopyTo(target, 65_536);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or UriFormatException
                                          || error is IOException && request.Status == 0)
        {
            throw new RelayError(502, $"GitHub에서 파일을 받지 못했습니다: {(error is TaskCanceledException or OperationCanceledException ? "timed out" : error.Message)}");
        }
        finally
        {
            response?.Dispose();
            timeout.Dispose();
        }
    }

    /// <summary>파일 저장소의 주소를 토큰 없이 (그 안에서 또 옮기면 따라감)</summary>
    private static HttpResponseMessage FollowPlain(HttpRequestMessage message, CancellationToken cancel)
    {
        for (int hop = 0; ; hop++)
        {
            var response = Http.Send(message, HttpCompletionOption.ResponseHeadersRead, cancel);
            int status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location || hop >= 5) return response;
            var next = location.IsAbsoluteUri ? location : new Uri(message.RequestUri!, location);
            response.Dispose();
            message = new HttpRequestMessage(HttpMethod.Get, next);
            message.Headers.TryAddWithoutValidation("User-Agent", "ApiServer/" + Program.Version);
        }
    }

    /// <summary>주소 앞부분 아래의 기억한 응답을 버림 (릴리스를 시작한 저장소)</summary>
    public void Forget(string prefix)
    {
        lock (_lock)
            foreach (var key in _cache.Keys.Where(key => key.Url.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _cache.Remove(key);
    }
}
