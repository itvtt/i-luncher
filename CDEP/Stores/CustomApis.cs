using System.Text.Json.Nodes;

namespace IApi;

/// <summary>
/// 관리 창에서 만든 API 하나 (사용자 API). 종류: text = 적어 둔 JSON · 글을 돌려줌 ({이름} 은 주소의 값),
/// script = PowerShell 스크립트를 돌려 그 출력을 돌려줌, forward = 다른 주소로 그대로 전달.
/// 부를 수 있는 사람: open = 누구나, key = 앱 키(RELAY_INGEST_KEY), admin = 관리자 비밀번호. PowerShell 은 이 PC 에서 돌므로 누구나로 열 수 없음
/// </summary>
internal sealed record CustomApi(string Id, string Title, string Description, string Method, string Path, string Access, string Kind, string Body, int TimeoutSeconds)
{
    public static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE"];
    public static readonly string[] Accesses = ["open", "key", "admin"];
    public static readonly string[] Kinds = ["text", "script", "forward"];
    public const int DefaultTimeout = 30, MaxTimeout = 120, MaxBody = 512 * 1024;

    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["title"] = Title, ["description"] = Description, ["method"] = Method, ["path"] = Path,
        ["access"] = Access, ["kind"] = Kind, ["body"] = Body, ["timeoutSeconds"] = TimeoutSeconds,
    };

    public const string ScriptOpenMessage = "PowerShell API 는 '누구나'로 열 수 없습니다. 앱 키나 관리자를 고르세요.";

    /// <summary>
    /// JSON 에서 (id 는 따로). 값이 틀리면 RelayError 400 (관리 창이 그 글을 그대로 보여 줌).
    /// stored = 이미 저장돼 있던 것 (custom.json · 기록): 예전에 누구나로 저장한 PowerShell API 는 앱 키로 바꿔 읽음
    /// </summary>
    public static CustomApi FromJson(string id, JsonObject data, bool stored = false)
    {
        string Text(string key) => Util.AsString(data[key])?.Trim() ?? "";
        string title = Text("title"), description = Text("description"), method = Text("method").ToUpperInvariant(), path = Text("path");
        string access = Text("access"), kind = Text("kind"), body = Util.AsString(data["body"]) ?? "";
        int timeout = data["timeoutSeconds"] is null ? DefaultTimeout : (int)Math.Clamp(Util.AsInt(data["timeoutSeconds"], DefaultTimeout), -1, 10_000);
        if (title.Length is 0 or > 100) throw new RelayError(400, "이름을 100자까지 쓰세요.");
        if (description.Length > 500) throw new RelayError(400, "설명은 500자까지 씁니다.");
        if (!Methods.Contains(method)) throw new RelayError(400, "방법은 GET · POST · PUT · DELETE 중 하나입니다.");
        if (!Accesses.Contains(access)) throw new RelayError(400, "부를 수 있는 사람이 올바르지 않습니다.");
        if (!Kinds.Contains(kind)) throw new RelayError(400, "종류가 올바르지 않습니다.");
        if (path.Length is 0 or > 200 || !path.StartsWith('/') || path == "/") throw new RelayError(400, "주소는 / 로 시작해 200자까지 씁니다. 예: /hello/{name}");
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            bool value = segment.Length > 2 && segment[0] == '{' && segment[^1] == '}' && segment[1..^1].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
            bool literal = segment.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '~');
            if (!value && !literal) throw new RelayError(400, $"주소 조각이 올바르지 않습니다: {segment} (영문 · 숫자 · - _ . ~ 또는 {{이름}})");
        }
        if (body.Length > MaxBody) throw new RelayError(400, "내용이 너무 깁니다 (512 KB 까지).");
        if (kind == "script" && body.Trim().Length == 0) throw new RelayError(400, "PowerShell 스크립트를 쓰세요.");
        if (kind == "script" && access == "open")
        {
            if (!stored) throw new RelayError(400, ScriptOpenMessage);
            access = "key";
        }
        if (kind == "forward" && !(Uri.TryCreate(body.Trim(), UriKind.Absolute, out var target) && target.Scheme is "http" or "https"))
            throw new RelayError(400, "전달할 주소를 http:// 또는 https:// 로 쓰세요.");
        if (timeout is < 1 or > MaxTimeout) throw new RelayError(400, $"기다리는 시간은 1~{MaxTimeout}초입니다.");
        return new CustomApi(id, title, description, method, "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries)), access, kind,
            kind == "forward" ? body.Trim() : body, timeout);
    }

    /// <summary>주소의 모양 ({이름} 은 모두 같은 자리): 겹치는지 견줄 때</summary>
    public string Shape => string.Join('/', Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment => segment.StartsWith('{') ? "{}" : segment));
}

/// <summary>
/// 사용자 API 목록 (데이터 폴더의 api\custom.json). 관리 창에서 더하고 고치고 지우면 서버를 다시 켜지 않아도 바로 바뀜.
/// 바꿀 때마다 바뀐 뒤의 목록 전체를 api\custom-history\{시각}.json 에 남김 (최근 HistoryKeep 개): 관리 창의 [되돌리기]가 씀
/// </summary>
internal sealed class CustomApis
{
    public const int HistoryKeep = 200;
    private static readonly System.Text.RegularExpressions.Regex HistoryName = new(@"^\d{8}-\d{6}-\d{6}(-\d{1,3})?$");
    private static readonly System.Text.RegularExpressions.Regex IdPattern = new(@"^custom-[a-z0-9-]{1,40}$");
    private readonly string _file, _history;
    private readonly Lock _lock = new();
    private List<CustomApi> _items;

    public CustomApis(string directory)
    {
        _file = Path.Combine(directory, "custom.json");
        _history = Path.Combine(directory, "custom-history");
        _items = Parse(Util.ReadJsonObject(_file), "custom.json");
    }

    /// <summary>저장해 둔 목록 읽기 (틀린 것은 건너뜀)</summary>
    private static List<CustomApi> Parse(JsonObject? data, string where)
    {
        var items = new List<CustomApi>();
        foreach (var node in data?["apis"] as JsonArray ?? [])
        {
            if (node is not JsonObject api || Util.AsString(api["id"]) is not { Length: > 0 } id) continue;
            try { items.Add(CustomApi.FromJson(id, api, stored: true)); }
            catch (RelayError error) { Log.Warning($"custom API {id} in {where} skipped: {error.Message}"); }
        }
        return items;
    }

    /// <summary>사용자 API 의 id 로 쓸 수 있는 값 (가져온 파일의 id 를 그대로 쓸 때)</summary>
    public static bool IsId(string id) => IdPattern.IsMatch(id);

    public static string NewId() => "custom-" + Util.HexToken(4);

    public List<CustomApi> All()
    {
        lock (_lock) return [.. _items];
    }

    public void Save(CustomApi api)
    {
        lock (_lock)
        {
            var items = new List<CustomApi>(_items);
            int index = items.FindIndex(item => item.Id == api.Id);
            if (index >= 0) items[index] = api;
            else items.Add(api);
            Commit(items, (index >= 0 ? "고침: " : "더함: ") + api.Title);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (_items.FirstOrDefault(item => item.Id == id) is not { } api) return false;
            Commit(_items.Where(item => item.Id != id).ToList(), "지움: " + api.Title);
            return true;
        }
    }

    /// <summary>목록 전체를 바꿈 (가져오기 · 되돌리기)</summary>
    public void Replace(List<CustomApi> items, string change)
    {
        lock (_lock) Commit(items, change);
    }

    /// <summary>custom.json 을 쓰고 기록에 남김. 기록이 처음이면 바꾸기 전 목록도 남김 (기록이 생기기 전에 만든 API 도 되돌릴 수 있게)</summary>
    private void Commit(List<CustomApi> items, string change)
    {
        Directory.CreateDirectory(_history);
        if (_items.Count > 0 && !Directory.EnumerateFiles(_history, "*.json").Any()) Record(_items, "처음 기록 전");
        Util.WriteJson(_file, ListJson(items));
        _items = items;
        Record(items, change);
    }

    private static JsonObject ListJson(IEnumerable<CustomApi> items) =>
        new() { ["apis"] = new JsonArray(items.Select(item => (JsonNode)item.ToJson()).ToArray()) };

    private void Record(List<CustomApi> items, string change)
    {
        string name = Util.Stamp(), path = Path.Combine(_history, name + ".json");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(_history, $"{name}-{n}.json");
        var entry = ListJson(items);
        entry["at"] = Util.Iso(Util.Now());
        entry["change"] = change;
        Util.WriteJson(path, entry);
        foreach (string old in HistoryFiles().Skip(HistoryKeep))
            try { File.Delete(old); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>기록 파일, 새것부터</summary>
    private IEnumerable<string> HistoryFiles() =>
        Directory.Exists(_history)
            ? Directory.EnumerateFiles(_history, "*.json").Where(path => HistoryName.IsMatch(Path.GetFileNameWithoutExtension(path)))
                .OrderByDescending(path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal)
            : [];

    /// <summary>바뀐 기록 (새것부터): name · at · change · count</summary>
    public JsonArray History()
    {
        lock (_lock)
        {
            var list = new JsonArray();
            foreach (string path in HistoryFiles())
            {
                var entry = Util.ReadJsonObject(path);
                if (entry is null) continue;
                list.Add(new JsonObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(path), ["at"] = Util.AsString(entry["at"]) ?? "", ["change"] = Util.AsString(entry["change"]) ?? "",
                    ["count"] = (entry["apis"] as JsonArray)?.Count ?? 0,
                });
            }
            return list;
        }
    }

    /// <summary>기록 하나의 목록과 그때 시각. 없으면 404</summary>
    public (List<CustomApi> Apis, string At) Snapshot(string name)
    {
        string path = Path.Combine(_history, name + ".json");
        if (!HistoryName.IsMatch(name) || Util.ReadJsonObject(path) is not { } entry) throw new RelayError(404, "그 기록을 찾지 못했습니다.");
        return (Parse(entry, name), Util.AsString(entry["at"]) ?? "");
    }
}
