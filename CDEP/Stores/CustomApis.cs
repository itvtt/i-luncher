using System.Text.Json.Nodes;

namespace IApi;

/// <summary>
/// 관리 창에서 만든 API 하나 (사용자 API). 종류: text = 적어 둔 JSON · 글을 돌려줌 ({이름} 은 주소의 값),
/// script = PowerShell 스크립트를 돌려 그 출력을 돌려줌, forward = 다른 주소로 그대로 전달.
/// 부를 수 있는 사람: open = 누구나, key = 앱 키(RELAY_INGEST_KEY), admin = 관리자 비밀번호
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

    /// <summary>JSON 에서 (id 는 따로). 값이 틀리면 RelayError 400 (관리 창이 그 글을 그대로 보여 줌)</summary>
    public static CustomApi FromJson(string id, JsonObject data)
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
        if (kind == "forward" && !(Uri.TryCreate(body.Trim(), UriKind.Absolute, out var target) && target.Scheme is "http" or "https"))
            throw new RelayError(400, "전달할 주소를 http:// 또는 https:// 로 쓰세요.");
        if (timeout is < 1 or > MaxTimeout) throw new RelayError(400, $"기다리는 시간은 1~{MaxTimeout}초입니다.");
        return new CustomApi(id, title, description, method, "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries)), access, kind,
            kind == "forward" ? body.Trim() : body, timeout);
    }

    /// <summary>주소의 모양 ({이름} 은 모두 같은 자리): 겹치는지 견줄 때</summary>
    public string Shape => string.Join('/', Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment => segment.StartsWith('{') ? "{}" : segment));
}

/// <summary>사용자 API 목록 (데이터 폴더의 api\custom.json). 관리 창에서 더하고 고치고 지우면 서버를 다시 켜지 않아도 바로 바뀜</summary>
internal sealed class CustomApis
{
    private readonly string _file;
    private readonly Lock _lock = new();
    private List<CustomApi> _items = [];

    public CustomApis(string directory)
    {
        _file = Path.Combine(directory, "custom.json");
        foreach (var node in Util.ReadJsonObject(_file)?["apis"] as JsonArray ?? [])
        {
            if (node is not JsonObject data || Util.AsString(data["id"]) is not { Length: > 0 } id) continue;
            try { _items.Add(CustomApi.FromJson(id, data)); }
            catch (RelayError error) { Log.Warning($"custom API {id} skipped: {error.Message}"); }
        }
    }

    public List<CustomApi> All()
    {
        lock (_lock) return [.. _items];
    }

    public void Save(CustomApi api)
    {
        lock (_lock)
        {
            int index = _items.FindIndex(item => item.Id == api.Id);
            if (index >= 0) _items[index] = api;
            else _items.Add(api);
            Write();
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (_items.RemoveAll(item => item.Id == id) == 0) return false;
            Write();
            return true;
        }
    }

    private void Write() => Util.WriteJson(_file, new JsonObject { ["apis"] = new JsonArray(_items.Select(item => (JsonNode)item.ToJson()).ToArray()) });
}
