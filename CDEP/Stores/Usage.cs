using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>
/// 앱마다 실제로 얼마나 쓰는지, 날짜별: 앱이 보낸 기록(AppKit AppUsage: 사용 · 가동 초, 실행, 앱마다의 작업 수)과 GitHub 중계가 본 것
/// (앱이 자기 최신 릴리스를 확인 = 시작, 릴리스 파일 받기 = 설치 · 업데이트). 달마다 데이터 폴더의 usage\YYYY-MM.json.
/// 사용자는 해시로만 (앱이 PC · 계정을, 서버가 주소를 해시), 읽기 API 는 수만 돌려줌. ImPerformance 가 읽어 성과로 바꿈
/// </summary>
internal sealed partial class Usage
{
    public const int BodyMax = 65_536;
    private const int FlushSeconds = 15;
    private const double CountMax = 1_000_000, AmountMax = 1e15;
    private static readonly string[] TimeMetrics = ["active", "running"];

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")] private static partial Regex AppName();
    [GeneratedRegex(@"^[a-f0-9]{6,64}$")] private static partial Regex UserHash();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,39}$")] private static partial Regex MetricName();
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$")] public static partial Regex DayText();
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}$")] private static partial Regex MonthText();

    private readonly string _directory;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, JsonObject> _months = [];
    private readonly HashSet<string> _dirty = [];
    private Timer? _timer;

    public Usage(string directory) => _directory = directory;

    public void StartFlushing() => _timer ??= new Timer(_ =>
    {
        try { Flush(); }
        catch (Exception error) { Log.Exception("usage flush failed", error); }
    }, null, FlushSeconds * 1000, FlushSeconds * 1000);

    // 기록 ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>앱의 POST /launcher/usage: {app, version, user, days: {YYYY-MM-DD: {지표: [건수, 양]}}}. 남긴 날 수</summary>
    public int AddReport(JsonNode? body)
    {
        if (body is not JsonObject report) throw new RelayError(400, "JSON 객체가 필요합니다.");
        string? app = Util.AsString(report["app"]), user = Util.AsString(report["user"]);
        if (app is null || !AppName().IsMatch(app)) throw new RelayError(400, "app 이름이 올바르지 않습니다.");
        if (user is null || !UserHash().IsMatch(user)) throw new RelayError(400, "user 가 올바르지 않습니다.");
        if (report["days"] is not JsonObject days || days.Count > 120) throw new RelayError(400, "days 가 올바르지 않습니다.");
        var today = Util.Now().ToLocalTime().Date;
        var clean = new List<(string Day, List<(string Name, double Count, double Amount)> Metrics)>();
        foreach (var (day, node) in days)
        {
            if (!DayText().IsMatch(day) || node is not JsonObject metrics || metrics.Count > 64) throw new RelayError(400, "days 형식이 올바르지 않습니다.");
            if (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
                throw new RelayError(400, "날짜가 올바르지 않습니다.");
            // 너무 오래됐거나 시계가 틀린 PC 의 날: 거절하지 않고 버림 (앱은 다시 보내기만 함)
            if (moment < today.AddDays(-100) || moment > today.AddDays(1)) continue;
            var kept = new List<(string, double, double)>();
            foreach (var (name, value) in metrics)
            {
                if (!MetricName().IsMatch(name)) throw new RelayError(400, "지표 이름이 올바르지 않습니다.");
                if (value is not JsonArray pair || pair.Count != 2 || pair.Any(part => Util.Kind(part) != JsonValueKind.Number))
                    throw new RelayError(400, "지표 값은 [건수, 양] 입니다.");
                double count = Util.Number(pair[0]), amount = Util.Number(pair[1]);
                if (!double.IsFinite(count) || !double.IsFinite(amount) || count < 0 || amount < 0) throw new RelayError(400, "지표 값이 올바르지 않습니다.");
                amount = TimeMetrics.Contains(name) ? Math.Min(amount, 86400.0) : Math.Min(amount, AmountMax);
                kept.Add((name, Math.Min(count, CountMax), amount));
            }
            clean.Add((day, kept));
        }
        lock (_lock)
        {
            foreach (var (day, metrics) in clean)
            {
                var entry = Entry(day, app);
                var users = (JsonArray)entry["users"]!;
                if (!users.Any(item => Util.AsString(item) == user)) users.Add(user);
                var totals = (JsonObject)entry["metrics"]!;
                foreach (var (name, count, amount) in metrics)
                {
                    var total = totals[name] as JsonArray ?? (JsonArray)(totals[name] = new JsonArray(0, 0));
                    total[0] = Math.Round(Util.Number(total[0]) + count, 3);
                    total[1] = Math.Round(Util.Number(total[1]) + amount, 3);
                }
            }
        }
        return clean.Count;
    }

    /// <summary>
    /// GitHub 중계가 본 것: 앱이 자기 최신 릴리스를 물음(User-Agent 의 제품 이름 = 저장소 이름) = 방금 시작, octet-stream 으로 받은 릴리스 파일 = 설치 · 업데이트.
    /// 아직 기록을 보내지 않는 앱도 셈
    /// </summary>
    public void ObserveGitHub(IReadOnlyList<string> parts, string agent, string accept, string address)
    {
        if (parts.Count < 4 || parts[0] != "repos") return;
        string name = parts[2];
        var tail = parts.Skip(3).ToList();
        if (!AppName().IsMatch(name)) return;
        string product = (agent ?? "").Split('/', 2)[0].Trim().ToLowerInvariant();
        string metric;
        if (tail is ["releases", "latest"] && product == name.ToLowerInvariant()) metric = "check";
        else if (tail.Count == 3 && tail[0] == "releases" && tail[1] == "assets" && (accept ?? "").Contains("octet-stream", StringComparison.OrdinalIgnoreCase)) metric = "download";
        else return;
        string day = Util.LocalDay(Util.Now());
        string pc = Util.Sha256Hex(Util.Utf8.GetBytes("relay:" + address))[..12];
        lock (_lock)
        {
            var entry = Entry(day, name);
            var pcs = (JsonArray)entry["pcs"]!;
            if (!pcs.Any(item => Util.AsString(item) == pc)) pcs.Add(pc);
            var totals = (JsonObject)entry["metrics"]!;
            var total = totals[metric] as JsonArray ?? (JsonArray)(totals[metric] = new JsonArray(0, 0));
            total[0] = Util.Number(total[0]) + 1;
        }
    }

    private JsonObject Entry(string day, string app)
    {
        var month = Month(day[..7]);
        _dirty.Add(day[..7]);
        var days = (JsonObject)(month["days"] ??= new JsonObject());
        var apps = days[day] as JsonObject ?? (JsonObject)(days[day] = new JsonObject());
        var entry = apps[app] as JsonObject ?? (JsonObject)(apps[app] = new JsonObject());
        if (entry["users"] is not JsonArray) entry["users"] = new JsonArray();
        if (entry["pcs"] is not JsonArray) entry["pcs"] = new JsonArray();
        if (entry["metrics"] is not JsonObject) entry["metrics"] = new JsonObject();
        return entry;
    }

    private JsonObject Month(string key)
    {
        if (_months.TryGetValue(key, out var month)) return month;
        month = Util.ReadJsonObject(Path.Combine(_directory, key + ".json")) is { } read && read["days"] is JsonObject ? read : new JsonObject { ["days"] = new JsonObject() };
        _months[key] = month;
        return month;
    }

    // 읽기 ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Totals
    {
        public HashSet<string> Users = [], Pcs = [];
        public long UserDays, PcDays;
        public Dictionary<string, double[]> Metrics = [];
    }

    /// <summary>두 날 사이(둘 다 넣음) 앱마다의 합: users(앱 기록) · pcs(중계), 사람-날, 지표, 같은 것을 달마다</summary>
    public JsonObject Summary(string start, string end)
    {
        foreach (string value in new[] { start, end })
            if (!DayText().IsMatch(value ?? "")) throw new RelayError(400, "from·to 는 YYYY-MM-DD 입니다.");
        if (string.CompareOrdinal(start, end) > 0) (start, end) = (end, start);
        var apps = new Dictionary<string, (Totals Total, SortedDictionary<string, Totals> Months)>(StringComparer.Ordinal);
        var order = new List<string>();
        string? since = null;
        lock (_lock)
        {
            var keys = _months.Keys.Concat(Files()).Distinct().Order(StringComparer.Ordinal).ToList();
            foreach (string key in keys)
            {
                if (Month(key)["days"] is not JsonObject days) continue;
                foreach (string day in days.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToList())
                {
                    if (since is null || string.CompareOrdinal(day, since) < 0) since = day;
                    if (string.CompareOrdinal(day, start) < 0 || string.CompareOrdinal(day, end) > 0) continue;
                    if (days[day] is not JsonObject dayApps) continue;
                    foreach (var (app, node) in dayApps)
                    {
                        if (node is not JsonObject entry) continue;
                        if (!apps.TryGetValue(app, out var found))
                        {
                            apps[app] = found = (new Totals(), new SortedDictionary<string, Totals>(StringComparer.Ordinal));
                            order.Add(app);
                        }
                        if (!found.Months.TryGetValue(day[..7], out var month)) found.Months[day[..7]] = month = new Totals();
                        var users = (entry["users"] as JsonArray ?? []).Select(Util.Text).ToList();
                        var pcs = (entry["pcs"] as JsonArray ?? []).Select(Util.Text).ToList();
                        foreach (var target in new[] { found.Total, month })
                        {
                            target.Users.UnionWith(users);
                            target.UserDays += users.Count;
                            target.PcDays += pcs.Count;
                            foreach (var (name, value) in entry["metrics"] as JsonObject ?? [])
                            {
                                if (value is not JsonArray pair || pair.Count < 2) continue;
                                if (!target.Metrics.TryGetValue(name, out var summed)) target.Metrics[name] = summed = [0, 0];
                                summed[0] = Math.Round(summed[0] + Util.Number(pair[0]), 3);
                                summed[1] = Math.Round(summed[1] + Util.Number(pair[1]), 3);
                            }
                        }
                        found.Total.Pcs.UnionWith(pcs);
                    }
                }
            }
        }
        var result = new JsonObject();
        foreach (string app in order)
        {
            var (total, months) = apps[app];
            var monthly = new JsonObject();
            foreach (var (key, month) in months)
                monthly[key] = new JsonObject { ["users"] = month.Users.Count, ["userDays"] = month.UserDays, ["pcDays"] = month.PcDays, ["metrics"] = Metrics(month.Metrics) };
            result[app] = new JsonObject
            {
                ["users"] = total.Users.Count, ["pcs"] = total.Pcs.Count, ["userDays"] = total.UserDays, ["pcDays"] = total.PcDays,
                ["metrics"] = Metrics(total.Metrics), ["months"] = monthly,
            };
        }
        return new JsonObject
        {
            ["from"] = start, ["to"] = end, ["since"] = since is null ? null : JsonValue.Create(since), ["today"] = Util.LocalDay(Util.Now()), ["apps"] = result,
        };
    }

    private static JsonObject Metrics(Dictionary<string, double[]> metrics)
    {
        var result = new JsonObject();
        foreach (var (name, pair) in metrics) result[name] = new JsonArray(pair[0], pair[1]);
        return result;
    }

    private IEnumerable<string> Files()
    {
        try
        {
            return Directory.Exists(_directory)
                ? Directory.EnumerateFiles(_directory, "*.json").Select(path => Path.GetFileNameWithoutExtension(path)!).Where(name => MonthText().IsMatch(name)).ToList()
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    // 저장 ─────────────────────────────────────────────────────────────────────────────────────────

    public void Flush()
    {
        List<(string Key, byte[] Data)> pending;
        lock (_lock)
        {
            pending = _dirty.Order(StringComparer.Ordinal).Select(key => (key, Util.Utf8.GetBytes(_months[key].ToJsonString(Util.Compact)))).ToList();
            _dirty.Clear();
        }
        foreach (var (key, data) in pending)
        {
            try { Util.AtomicWrite(Path.Combine(_directory, key + ".json"), data); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Exception($"usage {key} not saved", error);
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
