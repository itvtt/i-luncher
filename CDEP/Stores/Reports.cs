using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>보고서 하나 (목록 · 화면에 보이는 것)</summary>
internal sealed record ReportInfo(string Id, DateTimeOffset ReceivedAt, string RemoteAddress, string AppVersion, string Summary, bool HasLogs, string Status, string Note)
{
    /// <summary>관리 창에 (시간은 ISO 8601)</summary>
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["receivedAt"] = Util.Iso(ReceivedAt), ["remoteAddress"] = RemoteAddress, ["appVersion"] = AppVersion,
        ["summary"] = Summary, ["hasLogs"] = HasLogs, ["status"] = Status, ["note"] = Note,
    };
}

/// <summary>Terminal Notes 문제 보고: 보고서마다 폴더 (report.txt, logs.zip, meta.json)</summary>
internal sealed partial class Reports
{
    public const int MaxChars = 200_000;
    public const string AutoTitle = "[Terminal Notes 자동 문제 보고]";
    public static readonly (string Key, string Label)[] Statuses = [("new", "새 보고"), ("checking", "확인 중"), ("resolved", "해결")];

    [GeneratedRegex("^[0-9]{8}-[0-9]{6}-[a-f0-9]{8}$")] private static partial Regex ReportId();
    [GeneratedRegex(@"^\s*(.+?)\s+(\d{1,6})\s*$")] private static partial Regex ProblemCount();
    [GeneratedRegex(@"^\s*([A-Za-z0-9_.\-]{1,80})\s*[×x]\s*(\d{1,6})\s*$")] private static partial Regex EventCount();

    private readonly string _directory;
    private readonly Lock _lock = new();

    public Reports(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public static bool IsStatus(string? status) => Statuses.Any(item => item.Key == status);

    public static string Label(string status) => Statuses.FirstOrDefault(item => item.Key == status).Label ?? status;

    public static JsonObject StatusesJson()
    {
        var result = new JsonObject();
        foreach (var (key, label) in Statuses) result[key] = label;
        return result;
    }

    public string Folder(string reportId)
    {
        if (!ReportId().IsMatch(reportId ?? "")) throw new RelayError(404, "보고서를 찾지 못했습니다.");
        string path = Path.Combine(_directory, reportId!);
        if (!File.Exists(Path.Combine(path, "report.txt"))) throw new RelayError(404, "보고서를 찾지 못했습니다.");
        return path;
    }

    public JsonObject Add(string report, byte[]? logs, string address)
    {
        string reportId = Util.Now().UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Util.HexToken(4);
        string folder = Path.Combine(_directory, reportId);
        Directory.CreateDirectory(folder);
        Util.AtomicWrite(Path.Combine(folder, "report.txt"), Util.Utf8.GetBytes(report));
        if (logs is { Length: > 0 }) Util.AtomicWrite(Path.Combine(folder, "logs.zip"), logs);
        var meta = new JsonObject
        {
            ["Id"] = reportId, ["ReceivedAt"] = Util.Iso(Util.Now()), ["RemoteAddress"] = address, ["AppVersion"] = LineAfter(report, "앱 버전:"),
            ["Summary"] = Summarize(report), ["HasLogs"] = logs is { Length: > 0 }, ["Status"] = "new", ["Note"] = "", ["UpdatedAt"] = Util.Iso(Util.Now()),
        };
        Util.WriteJson(Path.Combine(folder, "meta.json"), meta);
        return meta;
    }

    public ReportInfo Meta(string reportId)
    {
        string folder = Folder(reportId);
        var meta = Util.ReadJsonObject(Path.Combine(folder, "meta.json")) ?? [];
        // 글은 메타에 버전이나 요약이 없을 때만 읽음 (긴 목록도 빠르게)
        string text = Util.Truthy(Util.Field(meta, "AppVersion")) && Util.Truthy(Util.Field(meta, "Summary")) ? ""
            : Util.ReadText(Path.Combine(folder, "report.txt"), sig: false, lines: true);
        string status = Util.Text(Util.Field(meta, "Status"));
        return new ReportInfo(reportId,
            // 정리는 이 시각으로 지움: meta.json 을 읽지 못해도 (보고서 글 바로 뒤에 씀) id 의 시각, 1970 이 아님
            Util.ParseTime(Util.Field(meta, "ReceivedAt"), Util.IdTime(reportId)),
            Util.Or(Util.Field(meta, "RemoteAddress"), ""),
            Util.Or(Util.Field(meta, "AppVersion"), LineAfter(text, "앱 버전:")),
            Util.Or(Util.Field(meta, "Summary"), Summarize(text)),
            File.Exists(Path.Combine(folder, "logs.zip")),
            IsStatus(status) && Util.IsString(Util.Field(meta, "Status")) ? status : "new",
            Util.Or(Util.Field(meta, "Note"), ""));
    }

    public List<ReportInfo> All()
    {
        var items = new List<ReportInfo>();
        if (!Directory.Exists(_directory)) return items;
        foreach (string path in Directory.EnumerateDirectories(_directory))
        {
            string name = Path.GetFileName(path);
            if (!ReportId().IsMatch(name) || !File.Exists(Path.Combine(path, "report.txt"))) continue;
            try { items.Add(Meta(name)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or RelayError) { }
        }
        return items.OrderByDescending(item => item.ReceivedAt).ToList();
    }

    public string Text(string reportId) => Util.ReadText(Path.Combine(Folder(reportId), "report.txt"), sig: false, lines: true);

    public void Update(string reportId, string? status, string? note)
    {
        lock (_lock)
        {
            string folder = Folder(reportId);
            var meta = Util.ReadJsonObject(Path.Combine(folder, "meta.json")) ?? [];
            if (status is not null)
            {
                if (!IsStatus(status)) throw new RelayError(400, "상태가 올바르지 않습니다.");
                meta["Status"] = status;
            }
            if (note is not null) meta["Note"] = note.Length > 4000 ? note[..4000] : note;
            meta["UpdatedAt"] = Util.Iso(Util.Now());
            Util.WriteJson(Path.Combine(folder, "meta.json"), meta);
        }
    }

    public void Delete(string reportId)
    {
        lock (_lock)
        {
            string folder = Folder(reportId);
            try { Util.DeleteTree(folder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>보관 기간이 지난 보고서, 그다음 용량 안에 들 때까지 오래된 것부터 지움. 확인 중인 보고서는 남김</summary>
    public int Housekeep(int retentionDays, int maxMb)
    {
        int removed = 0;
        if (retentionDays > 0)
        {
            var limit = Util.Now().AddDays(-retentionDays);
            foreach (var item in All())
                if (item.ReceivedAt < limit && item.Status != "checking")
                {
                    Delete(item.Id);
                    removed++;
                }
        }
        if (maxMb > 0)
        {
            long budget = maxMb * 1024L * 1024L;
            var remaining = All().Where(item => item.Status != "checking").OrderBy(item => item.ReceivedAt).ToList();
            long total = Util.FolderSize(_directory);
            foreach (var item in remaining)
            {
                if (total <= budget) break;
                long size = Util.FolderSize(Path.Combine(_directory, item.Id));
                Delete(item.Id);
                total -= size;
                removed++;
            }
        }
        return removed;
    }

    public static string LineAfter(string report, string prefix)
    {
        foreach (string raw in report.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..].Trim();
        }
        return "";
    }

    public static string Summarize(string report)
    {
        var lines = report.Split('\n').Select(line => line.Trim()).ToList();
        int index = lines.IndexOf("[어떤 문제가 있었나요]");
        string text = index >= 0 && index + 1 < lines.Count ? lines[index + 1] : "";
        return text.Length == 0 ? "(설명 없음)" : text.Length > 80 ? text[..80] + "…" : text;
    }

    private static Dictionary<string, long> Counts(string report, string prefix, Regex pattern, char separator)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string part in LineAfter(report, prefix).Split(separator))
        {
            var found = pattern.Match(part);
            if (!found.Success) continue;
            string name = found.Groups[1].Value.Trim();
            counts[name] = counts.GetValueOrDefault(name) + long.Parse(found.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return counts;
    }

    /// <summary>
    /// 최근 days 일 보고를 모아 셈 (관리 창 → 문제 보고): 자동 · 직접, 자동 보고가 센 문제(Terminal Notes 의 "문제:" · "문제 이벤트:" 줄), 앱 버전, PC, 날짜별
    /// </summary>
    public JsonObject Analysis(int days)
    {
        var since = Util.Now().AddDays(-days);
        var items = All().Where(item => item.ReceivedAt >= since).ToList();
        var problems = new Dictionary<string, long>(StringComparer.Ordinal);
        var events = new Dictionary<string, long>(StringComparer.Ordinal);
        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        var addresses = new Dictionary<string, long>(StringComparer.Ordinal);
        var perDay = new SortedDictionary<string, long>(StringComparer.Ordinal);
        int automatic = 0;
        foreach (var item in items)
        {
            string text;
            try { text = Text(item.Id); }
            catch (RelayError) { text = ""; }
            if (text.StartsWith(AutoTitle, StringComparison.Ordinal)) automatic++;
            foreach (var (target, counts) in new[] { (problems, Counts(text, "문제:", ProblemCount(), '·')), (events, Counts(text, "문제 이벤트:", EventCount(), ',')) })
                foreach (var (name, count) in counts) target[name] = target.GetValueOrDefault(name) + count;
            string version = item.AppVersion.Length > 0 ? item.AppVersion : "버전 미상", address = item.RemoteAddress.Length > 0 ? item.RemoteAddress : "주소 미상";
            versions[version] = versions.GetValueOrDefault(version) + 1;
            addresses[address] = addresses.GetValueOrDefault(address) + 1;
            string day = Util.LocalDay(item.ReceivedAt);
            perDay[day] = perDay.GetValueOrDefault(day) + 1;
        }
        static JsonArray Top(Dictionary<string, long> counts, int limit) => new(counts.OrderBy(pair => -pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(limit)
            .Select(pair => (JsonNode)new JsonObject { ["name"] = pair.Key, ["count"] = pair.Value }).ToArray());
        return new JsonObject
        {
            ["days"] = days, ["total"] = items.Count, ["automatic"] = automatic, ["manual"] = items.Count - automatic,
            ["new"] = items.Count(item => item.Status == "new"), ["problems"] = Top(problems, 10), ["events"] = Top(events, 10),
            ["versions"] = Top(versions, 6), ["addresses"] = Top(addresses, 6),
            ["perDay"] = new JsonArray(perDay.Select(pair => (JsonNode)new JsonObject { ["day"] = pair.Key, ["count"] = pair.Value }).ToArray()),
        };
    }
}
