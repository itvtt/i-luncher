using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>
/// API 서버: 저장소들과 API 목록(Functions)을 들고 요청을 나눔. 나누는 순서는 예전 relay.py 와 같음
/// (상태 확인 → GitHub 중계 → i-luncher → 설비 상태 → 앱 키를 보는 /api → 관리자 비밀번호를 보는 나머지)
/// </summary>
internal sealed partial class RelayServer : IDisposable
{
    public Config Config { get; }
    public Skills Skills { get; }
    public Plugins Plugins { get; }
    public Announcements Announcements { get; }
    public LauncherStore Launcher { get; }
    public GitHubProxy GitHub { get; }
    public Releases Releases { get; }
    public EquipmentStatus Equipment { get; }
    public Runners Runners { get; }
    public RateLimiter Limiter { get; } = new();
    public Notifier Notifier { get; }
    public Clients Clients { get; } = new();
    public Usage Usage { get; }
    public ApiSwitches Switches { get; }
    public ApiStats Stats { get; }
    public DateTimeOffset Started { get; } = Util.Now();
    public IReadOnlyList<ApiFunction> Functions { get; }

    private HttpServer? _http, _equipmentHttp;
    private X509Certificate2? _certificate;

    public int Port => _http?.Port ?? Config.Port;
    public int? EquipmentPort => _equipmentHttp?.Port;

    public RelayServer(Config config)
    {
        Config = config;
        Skills = new Skills(config.SkillsDir);
        Plugins = new Plugins(config.PluginsDir);
        Announcements = new Announcements(config.AnnouncementsDir, config.Editors);
        Launcher = new LauncherStore(config.LauncherDir, config.GitHubHostsAll);
        GitHub = new GitHubProxy(config, Launcher);
        Releases = new Releases(config, Launcher, GitHub);
        Equipment = new EquipmentStatus(config);
        Runners = new Runners(config, Releases);
        Notifier = new Notifier(config.WebhookUrl);
        Usage = new Usage(config.UsageDir);
        Switches = new ApiSwitches(config.ApiDir);
        Stats = new ApiStats(config.ApiDir);
        Functions = BuildFunctions();
    }

    /// <summary>포트를 열고 받기 시작 (설비 상태 포트도, 쓰면). 포트가 이미 쓰이면 예외</summary>
    public void Start()
    {
        if (Config.TlsCert.Length > 0) _certificate = LoadCertificate(Config.TlsCert, Config.TlsKey);
        _http = new HttpServer(Config.Bind, Config.Port, _certificate, Handle);
        _http.Start();
        Usage.StartFlushing();
        Stats.StartFlushing();
        if (Config.UsesEquipmentPort)
        {
            try
            {
                _equipmentHttp = new HttpServer(Config.Bind, Config.EquipmentPort, _certificate, HandleEquipmentPort);
                _equipmentHttp.Start();
            }
            catch (Exception error) when (error is System.Net.Sockets.SocketException or IOException)
            {
                Log.Warning($"설비 상태 포트 {Config.EquipmentPort}을(를) 열지 못했습니다({error.Message}). 설비 상태는 {Config.Port} 포트에서만 내려 줍니다.");
            }
        }
    }

    /// <summary>설비 상태 포트를 따로 엶 (확인 코드). 열었으면 true</summary>
    internal bool StartEquipmentPort()
    {
        if (!Config.UsesEquipmentPort) return false;
        try
        {
            _equipmentHttp = new HttpServer(Config.Bind, Config.EquipmentPort, _certificate, HandleEquipmentPort);
            _equipmentHttp.Start();
            return true;
        }
        catch (Exception error) when (error is System.Net.Sockets.SocketException or IOException)
        {
            Log.Warning($"설비 상태 포트 {Config.EquipmentPort}을(를) 열지 못했습니다({error.Message}). 설비 상태는 {Config.Port} 포트에서만 내려 줍니다.");
            return false;
        }
    }

    private static X509Certificate2 LoadCertificate(string certificate, string key)
    {
        using var pem = X509Certificate2.CreateFromPemFile(certificate, key);
        // Windows 의 SslStream 은 PEM 의 임시 키를 못 씀: PFX 로 한 번 내보내 다시 읽음
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null);
    }

    public void Dispose()
    {
        _http?.Dispose();
        _equipmentHttp?.Dispose();
        Usage.Stop();
        Stats.Stop();
    }

    // ── 나누기 ───────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE"];

    private void Handle(Request request) => Dispatch(request, Route);

    private void HandleEquipmentPort(Request request) => Dispatch(request, RouteEquipmentPort);

    private void Dispatch(Request request, Action<Request> route)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!Methods.Contains(request.Method)) throw new RelayError(501, $"Unsupported method ({request.Method})");
            route(request);
        }
        catch (HandledException) { }
        catch (RelayError error) { request.Error(error.Status, error.Message); }
        catch (IOException) when (request.Status != 0) { }  // 받는 쪽이 끊음 (큰 파일)
        catch (Exception error)
        {
            Log.Exception($"request failed: {request.Method} {request.Path}", error);
            request.Error(500, "서버 오류가 났습니다. 서버 로그를 확인하세요.");
        }
        finally
        {
            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (request.Function is { } function) Stats.Record(function.Id, request.Status, milliseconds, request.FailureMessage);
            // 앱마다 1분에 한 번 공지를 묻는 일상 응답은 로그를 덮으므로 빼고 (상태 화면이 PC 목록을 보여 줌), 로그는 PC 마다 첫 접속을 남김
            bool routine = request.Method == "GET" && request.Path is "/api/announcements" or "/health" && request.Status is 200 or 304;
            if (!routine) Log.Info($"{request.RemoteAddress} {request.Method} {request.Path} {request.Status} {milliseconds:0}ms");
        }
    }

    /// <summary>API 를 부름: 꺼 둔 API 면 503 (관리 창 → API 에서 켬)</summary>
    private void Invoke(Request request, ApiFunction function, Dictionary<string, string>? values = null)
    {
        request.Function = function;
        request.Values = values ?? [];
        if (function.Switchable && !Switches.IsOn(function.Id)) throw new RelayError(503, $"이 API는 꺼져 있습니다: {function.Title} (관리 창 → API)");
        function.Handle(request);
    }

    /// <summary>area 안에서 방법 · 주소가 맞는 API (없으면 null)</summary>
    private (ApiFunction Function, Dictionary<string, string> Values)? Find(string area, string method, IReadOnlyList<string> parts)
    {
        foreach (var function in Functions)
            if (function.Area == area && function.Matches(method, parts, out var values)) return (function, values);
        return null;
    }

    private bool Dispatched(Request request, string area)
    {
        if (Find(area, request.Method, request.Parts) is not { } found) return false;
        Invoke(request, found.Function, found.Values);
        return true;
    }

    private void Route(Request request)
    {
        var parts = request.Parts;
        string method = request.Method;
        if (parts is ["health"] && method == "GET")
        {
            Invoke(request, Function("health"));
            return;
        }
        if (parts.Count >= 2 && parts[0] == "api" && parts[1] == "v3")
        {
            // GitHub 중계: 읽기만, 앱 키 없이 (런처와 모든 앱이 씀), 주소마다 넉넉한 제한
            if (method != "GET") throw new RelayError(405, "GitHub 중계는 읽기(GET)만 합니다.");
            Invoke(request, Function("github.repos"));
            return;
        }
        if (parts.Count >= 1 && parts[0] == "launcher")
        {
            Clients.Touch(request.RemoteAddress, request.HeaderOr("User-Agent"), "launcher");
            if (Dispatched(request, "launcher")) return;
            var rest = parts.Skip(1).ToList();
            throw new RelayError(rest is ["catalog"] or ["notices"] or ["media"] or ["usage"] ? 405 : 404, "찾을 수 없는 주소입니다.");
        }
        if (parts is ["api", "v1", "equipment-status"])
        {
            Invoke(request, Function("eqstatus.status"));
            return;
        }
        if (parts.Count >= 1 && parts[0] == "api")
        {
            try { RequireKey(request); }
            catch (RelayError)
            {
                // 다른 .env 의 키로 빌드한 앱도 목록에: 서버에 닿지만 거절됨
                Clients.Touch(request.RemoteAddress, request.HeaderOr("User-Agent"), "badkey");
                throw;
            }
            Clients.Touch(request.RemoteAddress, request.HeaderOr("User-Agent"), parts.Count > 1 ? parts[1] : "");
            if (Dispatched(request, "app")) return;
            throw new RelayError(404, "찾을 수 없는 주소입니다.");
        }
        RequireAdmin(request);
        if (parts.Count >= 2 && parts[0] == "admin" && parts[1] == "api")
        {
            // 바꾸기는 JSON 본문만: 다른 사이트의 페이지는 이 서버가 답하지 않는 CORS 확인 없이 JSON 을 보내지 못함
            if (method is "POST" or "PUT" && !request.HeaderOr("Content-Type").Contains("application/json", StringComparison.OrdinalIgnoreCase))
                throw new RelayError(415, "JSON(application/json)으로 보내야 합니다.");
            if (Dispatched(request, "admin")) return;
            throw new RelayError(404, "찾을 수 없는 주소입니다.");
        }
        Invoke(request, Function("pages"));
    }

    /// <summary>EQUIPMENT_PORT: 상태 확인과 설비 표만 (나머지는 RELAY_PORT 에)</summary>
    private void RouteEquipmentPort(Request request)
    {
        if (request.Parts is ["health"] && request.Method == "GET")
        {
            Invoke(request, Function("health"));
            return;
        }
        if (request.Parts is ["api", "v1", "equipment-status"])
        {
            Invoke(request, Function("eqstatus.status"));
            return;
        }
        throw new RelayError(404, "이 포트는 설비 상태(/api/v1/equipment-status)만 내려 줍니다.");
    }

    public ApiFunction Function(string id) => Functions.First(function => function.Id == id);

    // ── 확인 ─────────────────────────────────────────────────────────────────────────────────────

    private void RequireKey(Request request)
    {
        if (Config.IngestKey.Length == 0) return;
        if (!Util.SecretEquals(request.HeaderOr("X-TerminalNotes-Key"), Config.IngestKey)) throw new RelayError(401, "키가 맞지 않습니다.");
    }

    public void RequireAdmin(Request request)
    {
        if (Config.AdminPassword.Length == 0) return;
        string address = request.RemoteAddress;
        if (Limiter.Count("admin-fail", address, 600) >= 10) throw new RelayError(429, "비밀번호를 여러 번 틀렸습니다. 10분 뒤에 다시 시도하세요.");
        string header = request.HeaderOr("Authorization");
        if (header.StartsWith("basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(header[6..].Trim()));
                int colon = decoded.IndexOf(':');
                if (colon >= 0 && Util.SecretEquals(decoded[(colon + 1)..], Config.AdminPassword)) return;
            }
            catch (Exception error) when (error is FormatException or DecoderFallbackException) { }
            Limiter.Allow("admin-fail", address, 1_000_000, 600);
        }
        request.Challenge();
        throw new HandledException();
    }

    /// <summary>러너를 바꾸는 것은 서버 PC 에서만</summary>
    internal static Func<string, bool> IsThisPc = address => Config.IsLoopback(address) || address == "::1" || Config.IsLocalAddress(address);

    private static void RequireThisPc(Request request)
    {
        if (!IsThisPc(request.RemoteAddress)) throw new RelayError(403, "러너는 서버 PC에서만 바꿀 수 있습니다.");
    }

    private static void RequireSameOrigin(Request request)
    {
        // 다른 사이트의 양식은 브라우저에 저장된 관리자 로그인을 함께 보냄: 이 페이지 자신의 출처만 받음
        string host = request.HeaderOr("Host");
        foreach (string header in new[] { "Origin", "Referer" })
        {
            string? value = request.Header(header);
            if (string.IsNullOrEmpty(value)) continue;
            if (!GitHubLinks.SplitUrl(value).Netloc.Equals(host, StringComparison.OrdinalIgnoreCase)) throw new RelayError(403, "다른 사이트에서 온 요청은 받지 않습니다.");
            return;
        }
    }

    private void Limit(Request request, string bucket, int limit, int window)
    {
        if (!Limiter.Allow(bucket, request.RemoteAddress, limit, window)) throw new RelayError(429, "너무 자주 보냈습니다. 잠시 후 다시 시도하세요.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9.\-\[\]:]{1,255}$")] private static partial Regex HostHeader();

    /// <summary>클라이언트가 이 서버에 닿은 방법 (scheme 과 Host): GitHub 중계의 응답 주소가 같은 길로 돌아오게</summary>
    private string PublicBase(Request request)
    {
        string host = request.HeaderOr("Host").Trim();
        if (!HostHeader().IsMatch(host)) return Config.PublicUrl;
        return (Config.TlsCert.Length > 0 ? "https" : "http") + "://" + host;
    }
}
