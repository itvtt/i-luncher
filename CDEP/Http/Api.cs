using System.Globalization;
using System.Text.Json.Nodes;

namespace IApi;

/// <summary>
/// API 하나 (함수 하나): 주소 · 방법 · 이름 · 설명과 처리기. 서버의 모든 API 는 ApiCatalog 에 이것으로 등록되고,
/// 관리 창 → API 가 이 목록으로 API 마다 켜고 끄며 호출 수 · 실패 · 걸린 시간을 봄.
/// 주소 조각: 글자는 그대로, {이름} 은 조각 하나, 마지막 ** 는 나머지 모두
/// </summary>
internal sealed class ApiFunction
{
    public required string Id { get; init; }
    /// <summary>요청을 먼저 거르는 곳 (Router): health · github · launcher · equipment · app · admin · page</summary>
    public required string Area { get; init; }
    public required string Group { get; init; }
    public required string Method { get; init; }
    public required string Pattern { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    /// <summary>끌 수 있음 (관리 창이 쓰는 API · 상태 확인은 끄지 못함)</summary>
    public bool Switchable { get; init; } = true;
    public required Action<Request> Handle { get; init; }

    private string[]? _segments;
    private string[] Segments => _segments ??= Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>화면에 보이는 주소 (GET · POST 처럼 방법이 여럿이면 Method 에 함께)</summary>
    public string Display => Method + " " + Pattern;

    public bool Matches(string method, IReadOnlyList<string> parts, out Dictionary<string, string> values)
    {
        values = [];
        if (Method != "*" && !Method.Split('|').Contains(method)) return false;
        return MatchesPath(parts, values);
    }

    public bool MatchesPath(IReadOnlyList<string> parts, Dictionary<string, string>? values = null)
    {
        var segments = Segments;
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (segment == "**") return true;
            if (index >= parts.Count) return false;
            if (segment.StartsWith('{') && segment.EndsWith('}'))
            {
                if (values is not null) values[segment[1..^1]] = parts[index];
                continue;
            }
            if (!string.Equals(segment, parts[index], StringComparison.Ordinal)) return false;
        }
        return parts.Count == segments.Length;
    }
}

/// <summary>꺼 둔 API (데이터 폴더의 api\functions.json: {"off": [id, …]}). 서버를 다시 켜도 그대로</summary>
internal sealed class ApiSwitches
{
    private readonly string _file;
    private readonly HashSet<string> _off;
    private readonly Lock _lock = new();

    public ApiSwitches(string directory)
    {
        _file = Path.Combine(directory, "functions.json");
        _off = (Util.ReadJsonObject(_file)?["off"] as JsonArray ?? []).Select(Util.AsString).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    public bool IsOn(string id)
    {
        lock (_lock) return !_off.Contains(id);
    }

    public void Set(string id, bool on)
    {
        lock (_lock)
        {
            if (on ? !_off.Remove(id) : !_off.Add(id)) return;
            Util.WriteJson(_file, new JsonObject { ["off"] = new JsonArray(_off.Order(StringComparer.Ordinal).Select(item => (JsonNode)item).ToArray()) });
        }
    }
}

/// <summary>
/// API 마다 날짜별 호출 · 실패(400 이상) · 걸린 시간 (데이터 폴더의 api\stats-YYYY-MM.json, 15초마다 저장).
/// 마지막 호출 · 마지막 실패는 서버를 켠 뒤로만 (메모리)
/// </summary>
internal sealed class ApiStats
{
    private const int FlushSeconds = 15;
    private readonly string _directory;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, JsonObject> _months = [];
    private readonly HashSet<string> _dirty = [];
    private readonly Dictionary<string, (DateTimeOffset At, int Status, string Message)> _lastFailure = [];
    private readonly Dictionary<string, DateTimeOffset> _lastCall = [];
    private Timer? _timer;

    public ApiStats(string directory) => _directory = directory;

    public void StartFlushing() => _timer ??= new Timer(_ => Flush(), null, FlushSeconds * 1000, FlushSeconds * 1000);

    public void Record(string id, int status, double milliseconds, string? failure)
    {
        var now = Util.Now();
        string day = Util.LocalDay(now);
        lock (_lock)
        {
            var month = Month(day[..7]);
            _dirty.Add(day[..7]);
            var days = (JsonObject)(month["days"] ??= new JsonObject());
            var entries = (JsonObject)(days[day] ??= new JsonObject());
            var entry = entries[id] as JsonArray;
            if (entry is null || entry.Count < 3) entries[id] = entry = new JsonArray(0, 0, 0);
            entry[0] = Util.AsInt(entry[0]) + 1;
            if (status >= 400) entry[1] = Util.AsInt(entry[1]) + 1;
            entry[2] = Math.Round(Util.Number(entry[2]) + milliseconds, 1);
            _lastCall[id] = now;
            if (status >= 400) _lastFailure[id] = (now, status, failure ?? "");
        }
    }

    private JsonObject Month(string key)
    {
        if (_months.TryGetValue(key, out var month)) return month;
        month = Util.ReadJsonObject(Path.Combine(_directory, $"stats-{key}.json")) is { } read && read["days"] is JsonObject ? read : new JsonObject { ["days"] = new JsonObject() };
        _months[key] = month;
        return month;
    }

    /// <summary>날짜별 [호출, 실패, ms] (오래된 날부터), days 일 동안</summary>
    public Dictionary<string, List<(string Day, long Calls, long Failures, double Milliseconds)>> PerDay(int days)
    {
        var today = Util.Now().ToLocalTime().Date;
        var wanted = Enumerable.Range(0, days).Select(back => today.AddDays(-(days - 1) + back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
        var result = new Dictionary<string, List<(string, long, long, double)>>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (string day in wanted)
            {
                if (Month(day[..7])["days"] is not JsonObject months || months[day] is not JsonObject entries) continue;
                foreach (var (id, node) in entries)
                {
                    if (node is not JsonArray entry || entry.Count < 3) continue;
                    if (!result.TryGetValue(id, out var list)) result[id] = list = [];
                    list.Add((day, Util.AsInt(entry[0]), Util.AsInt(entry[1]), Util.Number(entry[2])));
                }
            }
        }
        return result;
    }

    public DateTimeOffset? LastCall(string id)
    {
        lock (_lock) return _lastCall.TryGetValue(id, out var moment) ? moment : null;
    }

    public (DateTimeOffset At, int Status, string Message)? LastFailure(string id)
    {
        lock (_lock) return _lastFailure.TryGetValue(id, out var failure) ? failure : null;
    }

    public void Flush()
    {
        List<(string Key, byte[] Data)> pending;
        lock (_lock)
        {
            pending = _dirty.Order().Select(key => (key, Util.Utf8.GetBytes(_months[key].ToJsonString(Util.Compact)))).ToList();
            _dirty.Clear();
        }
        foreach (var (key, data) in pending)
        {
            try { Util.AtomicWrite(Path.Combine(_directory, $"stats-{key}.json"), data); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"api stats {key} not saved: {error.Message}");
                lock (_lock) _dirty.Add(key);
            }
        }
    }

    public void Stop()
    {
        _timer?.Dispose();
        Flush();
    }
}
