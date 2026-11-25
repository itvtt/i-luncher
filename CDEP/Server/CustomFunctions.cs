using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace IApi;

/// <summary>
/// 사용자 API (관리 창 → API → [+ API 추가]): 저장하면 바로 이 서버의 API 목록에 들어가 기본 API 와 똑같이 켜고 끄고 호출 수 · 실패 · 걸린 시간을 셈.
/// 기본 API 와 같은 방법 · 주소는 만들 수 없고, 요청이 기본 API 에 맞으면 늘 기본 API 가 먼저
/// </summary>
internal sealed partial class RelayServer
{
    private const string GroupCustom = "사용자 API";
    public CustomApis Customs { get; }
    private readonly List<ApiFunction> _builtins;
    private volatile IReadOnlyList<ApiFunction> _functions = [];
    private volatile Dictionary<string, CustomApi> _customById = [];

    /// <summary>모든 API: 사용자 API 가 먼저 (화면 맨 위), 그다음 기본 API</summary>
    public IReadOnlyList<ApiFunction> Functions => _functions;

    private void RebuildFunctions()
    {
        var customs = Customs.All();
        _customById = customs.ToDictionary(api => api.Id);
        _functions = [.. customs.Select(ToFunction), .. _builtins];
    }

    private ApiFunction ToFunction(CustomApi api) => new()
    {
        Id = api.Id, Area = "custom", Group = GroupCustom, Method = api.Method, Pattern = api.Path, Title = api.Title,
        Description = api.Description, Handle = request => RunCustom(api, request),
    };

    /// <summary>사용자 API 에 맞는 요청이면 (기본 API 에 맞지 않을 때만) 부를 수 있는 사람을 확인하고 부름</summary>
    private bool DispatchedCustom(Request request)
    {
        var customs = _customById;
        if (customs.Count == 0) return false;
        foreach (var function in _functions)
        {
            if (function.Area != "custom" || !function.Matches(request.Method, request.Parts, out var values)) continue;
            if (_builtins.Any(builtin => builtin.Id != "pages" && builtin.Matches(request.Method, request.Parts, out _))) return false;
            switch (customs[function.Id].Access)
            {
                case "key": RequireKey(request); break;
                case "admin": RequireAdmin(request); break;
            }
            Invoke(request, function, values);
            return true;
        }
        return false;
    }

    // ── 실행 ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly HttpClient ForwardHttp = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    private void RunCustom(CustomApi api, Request request)
    {
        switch (api.Kind)
        {
            case "text":
                string text = api.Body;
                foreach (var (name, value) in request.Values) text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
                Answer(request, 200, text);
                return;
            case "script":
                RunScript(api, request);
                return;
            default:
                Forward(api, request);
                return;
        }
    }

    /// <summary>글이 JSON 이면 JSON 으로, 아니면 글로</summary>
    private static void Answer(Request request, int status, string text)
    {
        bool json = false;
        try { json = text.Trim().Length > 0 && JsonNode.Parse(text) is not null; }
        catch (System.Text.Json.JsonException) { }
        request.Send(status, Util.Utf8.GetBytes(text), json ? "application/json; charset=utf-8" : "text/plain; charset=utf-8");
    }

    private static byte[] RequestBody(Request request) => request.Method is "POST" or "PUT" or "DELETE" ? request.Body() : [];

    /// <summary>스크립트가 받는 요청 ($request): method · path · values (주소의 {이름}) · query · body (글) · json (본문이 JSON 이면) · remote · contentType</summary>
    private static JsonObject RequestJson(Request request, byte[] body)
    {
        string text = Util.Utf8.GetString(body);
        JsonNode? parsed = null;
        try { if (text.Trim().Length > 0) parsed = JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { }
        return new JsonObject
        {
            ["method"] = request.Method, ["path"] = request.Path,
            ["values"] = new JsonObject(request.Values.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
            ["query"] = new JsonObject(request.Query.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)(pair.Value.Count == 1 ? pair.Value[0] : string.Join(",", pair.Value))))),
            ["body"] = text, ["json"] = parsed, ["remote"] = request.RemoteAddress, ["contentType"] = request.HeaderOr("Content-Type"),
        };
    }

    /// <summary>
    /// PowerShell 스크립트 (Windows PowerShell, 없으면 pwsh): 스크립트 안에서 $request 로 요청을 읽고, 출력한 글이 응답 (JSON 이면 JSON).
    /// exit 400~599 면 그 상태 코드로, 다른 오류는 500. 정한 시간 안에 끝나지 않으면 504
    /// </summary>
    private static void RunScript(CustomApi api, Request request)
    {
        string folder = Path.Combine(Path.GetTempPath(), "i-api-scripts");
        Directory.CreateDirectory(folder);
        string name = Guid.NewGuid().ToString("N");
        string script = Path.Combine(folder, name + ".ps1"), input = Path.Combine(folder, name + ".json");
        try
        {
            File.WriteAllText(input, RequestJson(request, RequestBody(request)).ToJsonString(), new UTF8Encoding(false));
            File.WriteAllText(script,
                "$ErrorActionPreference = 'Stop'\r\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\r\n$OutputEncoding = [System.Text.Encoding]::UTF8\r\n" +
                "$request = Get-Content -LiteralPath $env:IAPI_REQUEST_FILE -Raw -Encoding UTF8 | ConvertFrom-Json\r\n" + api.Body.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                new UTF8Encoding(true));
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = folder,
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(argument);
            start.Environment["IAPI_REQUEST_FILE"] = input;
            using var process = Process.Start(start) ?? throw new RelayError(500, "PowerShell 을 시작하지 못했습니다.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(api.TimeoutSeconds * 1000))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                throw new RelayError(504, $"스크립트가 {api.TimeoutSeconds}초 안에 끝나지 않았습니다.");
            }
            process.WaitForExit();
            string text = output.Result.TrimEnd(), problem = errors.Result.Trim();
            int code = process.ExitCode;
            if (code == 0) Answer(request, 200, text);
            else if (code is >= 400 and <= 599) Answer(request, code, text.Length > 0 ? text : problem);
            else throw new RelayError(500, "스크립트 오류: " + Shorten(problem.Length > 0 ? problem : text));
        }
        catch (System.ComponentModel.Win32Exception error) { throw new RelayError(500, "PowerShell 을 시작하지 못했습니다: " + error.Message); }
        finally
        {
            foreach (string file in new[] { script, input })
                try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Shorten(string text) => text.Length <= 600 ? text : text[..600] + "…";

    /// <summary>다른 주소로 그대로: 방법 · 쿼리 · 본문 · Content-Type 을 보내고 받은 상태 · 본문을 돌려줌</summary>
    private static void Forward(CustomApi api, Request request)
    {
        int question = request.Target.IndexOf('?');
        string url = api.Body + (question >= 0 ? (api.Body.Contains('?') ? "&" : "?") + request.Target[(question + 1)..] : "");
        foreach (var (name, value) in request.Values) url = url.Replace("{" + name + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), url);
        byte[] body = RequestBody(request);
        if (body.Length > 0)
        {
            message.Content = new ByteArrayContent(body);
            if (request.Header("Content-Type") is { Length: > 0 } type) message.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(api.TimeoutSeconds));
        try
        {
            using var response = ForwardHttp.Send(message, timeout.Token);
            byte[] data = response.Content.ReadAsByteArrayAsync(timeout.Token).GetAwaiter().GetResult();
            request.Send((int)response.StatusCode, data, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new RelayError(error is HttpRequestException ? 502 : 504, $"{api.Body} 에 전달하지 못했습니다: {error.Message}");
        }
    }

    // ── 관리 창: 더하기 · 고치기 · 지우기 ──────────────────────────────────────────────────────────

    /// <summary>서버가 쓰는 자리 · 기본 API · others 의 사용자 API 와 겹치면 그 까닭 (겹치지 않으면 null)</summary>
    private string? Conflict(CustomApi api, IEnumerable<CustomApi> others)
    {
        var parts = api.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(part => part.StartsWith('{') ? "x" : part).ToList();
        if (parts[0] is "health" or "admin" or "launcher" || parts is ["api", "v3", ..] || parts is ["api", "v1", "equipment-status", ..])
            return $"{api.Path} 은(는) 서버가 쓰는 자리입니다. 다른 주소를 쓰세요.";
        if (_builtins.FirstOrDefault(builtin => builtin.Id != "pages" && builtin.Matches(api.Method, parts, out _)) is { } taken)
            return $"{api.Method} {api.Path} 은(는) 기본 API '{taken.Title}' 이(가) 씁니다.";
        if (others.FirstOrDefault(other => other.Id != api.Id && other.Method == api.Method && other.Shape == api.Shape) is { } same)
            return $"같은 방법 · 주소의 사용자 API 가 있습니다: {same.Title}";
        return null;
    }

    private void CheckFree(CustomApi api)
    {
        if (Conflict(api, Customs.All()) is { } reason) throw new RelayError(409, reason);
    }

    private void CustomCreate(Request request)
    {
        var api = CustomApi.FromJson(CustomApis.NewId(), request.JsonObjectBody());
        CheckFree(api);
        Customs.Save(api);
        RebuildFunctions();
        Log.Info($"custom API {api.Id} added: {api.Method} {api.Path} ({request.RemoteAddress})");
        request.Json(200, FunctionJson(Function(api.Id), null, Util.LocalDay(Util.Now())));
    }

    private void CustomChange(Request request)
    {
        string id = request.Value("id");
        if (!_customById.ContainsKey(id)) throw new RelayError(404, "그 사용자 API를 찾지 못했습니다.");
        if (request.Method == "DELETE")
        {
            Customs.Delete(id);
            Switches.Set(id, true);
            RebuildFunctions();
            Log.Info($"custom API {id} deleted ({request.RemoteAddress})");
            request.Json(200, new JsonObject { ["deleted"] = id });
            return;
        }
        var api = CustomApi.FromJson(id, request.JsonObjectBody());
        CheckFree(api);
        Customs.Save(api);
        RebuildFunctions();
        Log.Info($"custom API {id} changed: {api.Method} {api.Path} ({request.RemoteAddress})");
        request.Json(200, FunctionJson(Function(id), Stats.PerDay(30).GetValueOrDefault(id), Util.LocalDay(Util.Now())));
    }

    // ── 관리 창: 내보내기 · 가져오기 · 되돌리기 ───────────────────────────────────────────────────────

    /// <summary>[내보내기]: 사용자 API 전체 (가져오기와 같은 모양: apis). 다른 서버로 옮기거나 따로 보관</summary>
    private void CustomExport(Request request) => request.Json(200, new JsonObject
    {
        ["format"] = "i-api-custom-apis", ["exportedAt"] = Util.Iso(Util.Now()),
        ["apis"] = new JsonArray(Customs.All().Select(api => (JsonNode)api.ToJson()).ToArray()),
    });

    /// <summary>
    /// [가져오기]: 파일의 사용자 API 를 더함. 같은 id, 없으면 같은 방법 · 주소의 사용자 API 는 파일 내용으로 바뀜 (그 id 그대로).
    /// 틀렸거나 기본 API · 서버 자리와 겹치는 것은 건너뛰고 까닭을 돌려줌. 한 번에 기록 하나
    /// </summary>
    private void CustomImport(Request request)
    {
        if (request.JsonObjectBody()["apis"] is not JsonArray incoming) throw new RelayError(400, "apis 목록이 있는 i-api 내보내기 파일이 아닙니다.");
        var items = Customs.All();
        int added = 0, changed = 0;
        var skipped = new JsonArray();
        foreach (var node in incoming)
        {
            string title = Util.AsString(node?["title"]) ?? "(이름 없음)";
            try
            {
                if (node is not JsonObject data) throw new RelayError(400, "API 가 아닙니다.");
                string fileId = Util.AsString(data["id"]) ?? "";
                var probe = CustomApi.FromJson(CustomApis.IsId(fileId) ? fileId : CustomApis.NewId(), data);
                int index = items.FindIndex(item => item.Id == probe.Id);
                if (index < 0) index = items.FindIndex(item => item.Method == probe.Method && item.Shape == probe.Shape);
                var api = index >= 0 ? probe with { Id = items[index].Id } : probe;
                if (Conflict(api, items) is { } reason) throw new RelayError(409, reason);
                if (index >= 0) { items[index] = api; changed++; }
                else { items.Add(api); added++; }
            }
            catch (RelayError error) { skipped.Add(new JsonObject { ["title"] = title, ["reason"] = error.Message }); }
        }
        if (added + changed > 0)
        {
            Customs.Replace(items, $"가져옴: {added + changed}개");
            RebuildFunctions();
        }
        Log.Info($"custom APIs imported: {added} added, {changed} changed, {skipped.Count} skipped ({request.RemoteAddress})");
        request.Json(200, new JsonObject { ["added"] = added, ["changed"] = changed, ["skipped"] = skipped });
    }

    private void CustomHistory(Request request) => request.Json(200, new JsonObject { ["history"] = Customs.History() });

    /// <summary>[되돌리기]: 사용자 API 목록을 그 기록 때로 (지금 목록도 기록에 남아 다시 돌릴 수 있음). 그 뒤 기본 API 와 겹치게 된 것은 건너뜀</summary>
    private void CustomRestore(Request request)
    {
        var (apis, at) = Customs.Snapshot(request.Value("name"));
        var items = new List<CustomApi>();
        var skipped = new JsonArray();
        foreach (var api in apis)
        {
            if (Conflict(api, items) is { } reason) skipped.Add(new JsonObject { ["title"] = api.Title, ["reason"] = reason });
            else items.Add(api);
        }
        var before = Customs.All();
        string when = DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var moment) ? Util.LocalText(moment) : at;
        Customs.Replace(items, $"되돌림: {when}");
        foreach (var gone in before.Where(old => items.All(api => api.Id != old.Id))) Switches.Set(gone.Id, true);
        RebuildFunctions();
        Log.Info($"custom APIs restored to {request.Value("name")}: {items.Count} ({request.RemoteAddress})");
        request.Json(200, new JsonObject { ["count"] = items.Count, ["skipped"] = skipped });
    }
}
