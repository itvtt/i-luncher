using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>요청을 거절하는 이유 (상태 코드 + 앱 · 관리자 메뉴가 그대로 보여 주는 한국어 문장)</summary>
internal sealed class RelayError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>설정이 잘못돼 서버를 시작하지 않음 (메시지는 그대로 화면 · 로그에)</summary>
internal sealed class ConfigError(string message) : Exception(message);

/// <summary>JSON · 시간 · 파일 도구. 예전 relay.py 와 같은 규칙 (같은 데이터 폴더를 그대로 읽고 씀)</summary>
internal static partial class Util
{
    public static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JavaScriptEncoder Relaxed = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    public static readonly JsonSerializerOptions Compact = new() { Encoder = Relaxed };
    public static readonly JsonSerializerOptions Indent1 = new() { Encoder = Relaxed, WriteIndented = true, IndentSize = 1 };
    public static readonly JsonSerializerOptions Indent2 = new() { Encoder = Relaxed, WriteIndented = true, IndentSize = 2 };

    // ── 시간 ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>확인 코드가 시간을 바꿔 봄 (백업 이름)</summary>
    internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

    public static DateTimeOffset Now() => Clock();

    /// <summary>ISO 8601 (UTC, +00:00). 마이크로초가 0 이면 소수 없이 (Python isoformat 과 같게)</summary>
    public static string Iso(DateTimeOffset moment)
    {
        var utc = moment.ToUniversalTime();
        long micro = utc.Ticks % TimeSpan.TicksPerSecond / 10;
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (micro == 0 ? "" : "." + micro.ToString("D6", CultureInfo.InvariantCulture)) + "+00:00";
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoStart();

    /// <summary>두 서버(.NET · Python)가 쓴 ISO 8601. 못 읽으면 unknown, 없으면 1970</summary>
    public static DateTimeOffset ParseTime(JsonNode? value, DateTimeOffset? unknown = null) => ParseTime(value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null, unknown);

    public static DateTimeOffset ParseTime(string? value, DateTimeOffset? unknown = null)
    {
        var fallback = unknown ?? DateTimeOffset.UnixEpoch;
        if (string.IsNullOrEmpty(value) || !IsoStart().IsMatch(value)) return fallback;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) ? moment : fallback;
    }

    /// <summary>보고서 id 앞의 UTC 시각, 날짜가 아니면 지금</summary>
    public static DateTimeOffset IdTime(string reportId) =>
        reportId.Length >= 15 && DateTime.TryParseExact(reportId[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment) ? new DateTimeOffset(moment, TimeSpan.Zero) : Now();

    public static string LocalText(DateTimeOffset moment) => moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string LocalDay(DateTimeOffset moment) => moment.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Stamp() => Now().UtcDateTime.ToString("yyyyMMdd-HHmmss-ffffff", CultureInfo.InvariantCulture);

    // ── 글자 ──────────────────────────────────────────────────────────────────────────────────────

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static bool SecretEquals(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>글자면 공백을 하나로 모은 한 줄, 아니면 ""</summary>
    public static string OneLine(JsonNode? value) => IsString(value) ? Spaces().Replace(value!.GetValue<string>(), " ").Trim() : "";

    public static string YamlString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string HexToken(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    /// <summary>Windows 계정 (DOMAIN\name → name)</summary>
    public static string AccountName(string value) => value[(value.LastIndexOf('\\') + 1)..].Trim();

    public static List<string> AccountList(string value) =>
        Regex.Split(value ?? "", "[,; ]+").Where(user => user.Trim().Length > 0).Select(AccountName).ToList();

    /// <summary>앱이 보낸 Windows 계정 (퍼센트 인코딩, DOMAIN\name → name), 소문자. 없으면 ""</summary>
    public static string ViewerName(JsonNode? value) => IsString(value) ? ViewerName(value!.GetValue<string>()) : "";

    public static string ViewerName(string? value) => value is null ? "" : AccountName(Uri.UnescapeDataString(value)).ToLowerInvariant();

    // ── JSON 값 (예전 서버의 dict 와 같은 규칙) ───────────────────────────────────────────────────────

    public static JsonValueKind Kind(JsonNode? node) => node?.GetValueKind() ?? JsonValueKind.Null;

    public static bool IsString(JsonNode? node) => Kind(node) == JsonValueKind.String;

    public static bool IsTrue(JsonNode? node) => Kind(node) == JsonValueKind.True;

    public static bool IsBool(JsonNode? node) => Kind(node) is JsonValueKind.True or JsonValueKind.False;

    public static string? AsString(JsonNode? node) => IsString(node) ? node!.GetValue<string>() : null;

    /// <summary>JSON 숫자 (정수 · 소수 모두)</summary>
    public static double Number(JsonNode? node) =>
        Kind(node) == JsonValueKind.Number ? double.Parse(node!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) : 0;

    /// <summary>소수점 없는 JSON 정수</summary>
    public static bool IsInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (Kind(node) != JsonValueKind.Number) return false;
        string text = node!.ToJsonString();
        return text.IndexOfAny(['.', 'e', 'E']) < 0 && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>값이 있음 (빈 글자 · 0 · false · null · 빈 목록이 아님)</summary>
    public static bool Truthy(JsonNode? node) => Kind(node) switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.True => true,
        JsonValueKind.String => node!.GetValue<string>().Length > 0,
        JsonValueKind.Number => Number(node) != 0,
        JsonValueKind.Array => ((JsonArray)node!).Count > 0,
        JsonValueKind.Object => ((JsonObject)node!).Count > 0,
        _ => true,
    };

    /// <summary>값을 글자로 (숫자는 그대로, 글자는 따옴표 없이)</summary>
    public static string Text(JsonNode? node) => Kind(node) switch
    {
        JsonValueKind.String => node!.GetValue<string>(),
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => node!.ToJsonString(),
    };

    /// <summary>값이 있으면 그 글자, 없으면 fallback</summary>
    public static string Or(JsonNode? node, string fallback) => Truthy(node) ? Text(node) : fallback;

    /// <summary>정수로 (글자 "12" 도), 안 되면 default</summary>
    public static long AsInt(JsonNode? node, long fallback = 0)
    {
        switch (Kind(node))
        {
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.Number:
                double number = Number(node);
                return double.IsFinite(number) ? (long)Math.Truncate(number) : fallback;
            case JsonValueKind.String:
                string text = node!.GetValue<string>().Trim().Replace("_", "");
                return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) ? value : fallback;
            default: return fallback;
        }
    }

    /// <summary>예전 .NET 서버의 메타 파일은 PascalCase: camelCase 도 받음</summary>
    public static JsonNode? Field(JsonObject? meta, string name)
    {
        if (meta is null) return null;
        if (meta.TryGetPropertyValue(name, out var value)) return value;
        return meta.TryGetPropertyValue(char.ToLowerInvariant(name[0]) + name[1..], out value) ? value : null;
    }

    public static JsonNode? Copy(JsonNode? node) => node?.DeepClone();

    public static JsonValue Json(string value) => JsonValue.Create(value);

    public static JsonNode? Json(string? value, bool nullWhenEmpty) => nullWhenEmpty && string.IsNullOrEmpty(value) ? null : JsonValue.Create(value);

    public static string ToJson(JsonNode? node) => node is null ? "null" : node.ToJsonString(Compact);

    public static JsonNode? ParseJson(byte[] data)
    {
        try { return JsonNode.Parse(StripBom(data)); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or DecoderFallbackException) { throw new RelayError(400, "형식이 올바르지 않습니다."); }
    }

    // ── 파일 ──────────────────────────────────────────────────────────────────────────────────────

    public static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> data) => data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? data[3..] : data;

    /// <summary>다른 스레드가 바꾸는 중이어도 읽음 (지우기 · 바꾸기를 막지 않게)</summary>
    public static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>UTF-8 글 (sig: 앞의 BOM 을 뺌, lines: 줄바꿈을 \n 으로 = 예전 서버가 글로 읽던 것). 잘못된 바이트는 대체 문자로</summary>
    public static string ReadText(string path, bool sig = true, bool lines = false)
    {
        byte[] data = ReadBytes(path);
        string text = sig ? Encoding.UTF8.GetString(StripBom(data)) : Encoding.UTF8.GetString(data);
        return lines ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
    }

    public static void AtomicWrite(string path, byte[] data)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, Path.GetFileName(path) + "." + HexToken(4) + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(true);
            }
            Retry(() => File.Move(temporary, path, overwrite: true));
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>다른 프로그램(백신 · 검색)이 잠깐 잡은 파일: 몇 번 다시</summary>
    public static void Retry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < 4)
            {
                Thread.Sleep(60 * (attempt + 1));
            }
        }
    }

    public static void WriteJson(string path, JsonNode value) => AtomicWrite(path, Utf8.GetBytes(value.ToJsonString(Indent1)));

    public static JsonNode? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(StripBom(ReadBytes(path)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    public static JsonObject? ReadJsonObject(string path) => ReadJson(path) as JsonObject;

    public static long FolderSize(string path)
    {
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try { total += file.Length; } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>폴더 아래 어디에든 파일이 있음 (읽지 못하면 있다고 봄: 건드리지 않게)</summary>
    public static bool HasFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    public static bool HasEntries(string folder)
    {
        try { return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>읽기 전용 파일까지 지움 (러너 폴더의 연결 · 정션은 따라가지 않고 연결만)</summary>
    public static void DeleteTree(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var info = new DirectoryInfo(folder);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete();
            return;
        }
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo directory) DeleteTree(directory.FullName);
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                Retry(entry.Delete);
            }
        }
        Retry(info.Delete);
    }

    public static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (string folder in Directory.EnumerateDirectories(source))
            CopyTree(folder, Path.Combine(target, Path.GetFileName(folder)));
    }

    public static bool SamePath(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>folder 가 parent 이거나 그 아래</summary>
    public static bool IsInside(string folder, string parent)
    {
        string inner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        string outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return inner.StartsWith(outer, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // ── multipart/form-data (앱의 문제 보고 올리기) ──────────────────────────────────────────────────

    [GeneratedRegex("boundary=\"?([^\";]+)\"?", RegexOptions.IgnoreCase)]
    private static partial Regex BoundaryPattern();

    [GeneratedRegex("(?:^|;)\\s*name=\"?([^\";]+)\"?")]
    private static partial Regex NamePattern();

    [GeneratedRegex("filename=\"?([^\";]+)\"?")]
    private static partial Regex FilenamePattern();

    [GeneratedRegex(@"charset=([\w-]+)")]
    private static partial Regex CharsetPattern();

    public static (Dictionary<string, string> Fields, Dictionary<string, (string Name, byte[] Data)> Files) ParseMultipart(string contentType, byte[] body)
    {
        var boundary = BoundaryPattern().Match(contentType);
        if (!boundary.Success) throw new RelayError(400, "boundary가 없습니다.");
        byte[] delimiter = Encoding.Latin1.GetBytes("--" + boundary.Groups[1].Value);
        var fields = new Dictionary<string, string>();
        var files = new Dictionary<string, (string, byte[])>();
        var parts = Split(body, delimiter);
        foreach (byte[] raw in parts.Skip(1))
        {
            ReadOnlySpan<byte> part = raw;
            if (part.StartsWith("--"u8)) break;
            if (part.StartsWith("\r\n"u8)) part = part[2..];
            int split = part.IndexOf("\r\n\r\n"u8);
            if (split < 0) continue;
            ReadOnlySpan<byte> head = part[..split], content = part[(split + 4)..];
            if (content.EndsWith("\r\n"u8)) content = content[..^2];
            var headers = new Dictionary<string, string>();
            foreach (string line in Encoding.UTF8.GetString(head).Split("\r\n"))
            {
                int colon = line.IndexOf(':');
                if (colon >= 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
            string disposition = headers.GetValueOrDefault("content-disposition", "");
            var name = NamePattern().Match(disposition);
            if (!name.Success) continue;
            var filename = FilenamePattern().Match(disposition);
            if (filename.Success) files[name.Groups[1].Value] = (filename.Groups[1].Value, content.ToArray());
            else
            {
                var charset = CharsetPattern().Match(headers.GetValueOrDefault("content-type", ""));
                Encoding encoding = Encoding.UTF8;
                if (charset.Success)
                {
                    try { encoding = Encoding.GetEncoding(charset.Groups[1].Value); }
                    catch (ArgumentException) { }
                }
                fields[name.Groups[1].Value] = encoding.GetString(content);
            }
        }
        return (fields, files);
    }

    private static List<byte[]> Split(byte[] data, byte[] delimiter)
    {
        var parts = new List<byte[]>();
        int start = 0;
        while (true)
        {
            int found = data.AsSpan(start).IndexOf(delimiter);
            if (found < 0)
            {
                parts.Add(data[start..]);
                return parts;
            }
            parts.Add(data[start..(start + found)]);
            start += found + delimiter.Length;
        }
    }

    // ── 주소 (application/x-www-form-urlencoded, 쿼리) ─────────────────────────────────────────────

    /// <summary>a=1&amp;b=2 → {a: [1], b: [2]} (+ 는 공백, 빈 값은 keepBlank 일 때만)</summary>
    public static Dictionary<string, List<string>> ParseQuery(string query, bool keepBlank = false)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (string pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            int equals = pair.IndexOf('=');
            if (equals < 0 && !keepBlank) continue;
            string key = Unquote((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            string value = equals < 0 ? "" : Unquote(pair[(equals + 1)..].Replace('+', ' '));
            if (value.Length == 0 && !keepBlank) continue;
            if (!result.TryGetValue(key, out var values)) result[key] = values = [];
            values.Add(value);
        }
        return result;
    }

    /// <summary>%XX 를 UTF-8 로 풂 (잘못된 것은 대체 문자)</summary>
    public static string Unquote(string text)
    {
        if (!text.Contains('%')) return text;
        var bytes = new List<byte>(text.Length);
        var output = new StringBuilder(text.Length);
        void Flush()
        {
            if (bytes.Count == 0) return;
            output.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '%' && index + 2 < text.Length &&
                byte.TryParse(text.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
            {
                bytes.Add(value);
                index += 2;
                continue;
            }
            Flush();
            output.Append(text[index]);
        }
        Flush();
        return output.ToString();
    }

    /// <summary>경로 조각을 % 로 (safe 에 든 글자는 그대로)</summary>
    public static string Quote(string text, string safe = "/")
    {
        var output = new StringBuilder();
        foreach (byte value in Encoding.UTF8.GetBytes(text))
        {
            char c = (char)value;
            if (value < 0x80 && (char.IsAsciiLetterOrDigit(c) || "_.-~".Contains(c) || safe.Contains(c))) output.Append(c);
            else output.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    public static string HtmlEscape(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "").Replace("&#39;", "&#x27;");
}

/// <summary>주소 · 묶음마다 일정 시간 동안 몇 번까지 (미끄러지는 창)</summary>
internal sealed class RateLimiter
{
    private readonly Dictionary<(string, string), Queue<long>> _events = [];
    private readonly Lock _lock = new();

    public bool Allow(string bucket, string address, int limit, double windowSeconds)
    {
        long moment = Environment.TickCount64;
        lock (_lock)
        {
            if (!_events.TryGetValue((bucket, address), out var events)) _events[(bucket, address)] = events = new Queue<long>();
            while (events.Count > 0 && moment - events.Peek() > windowSeconds * 1000) events.Dequeue();
            if (events.Count >= limit) return false;
            events.Enqueue(moment);
            return true;
        }
    }

    public int Count(string bucket, string address, double windowSeconds)
    {
        long moment = Environment.TickCount64;
        lock (_lock)
            return _events.TryGetValue((bucket, address), out var events) ? events.Count(time => moment - time <= windowSeconds * 1000) : 0;
    }
}

/// <summary>RELAY_WEBHOOK_URL 로 짧은 알림 (Slack · Teams · Mattermost text, Discord content). 요청 스레드 밖에서</summary>
internal sealed class Notifier
{
    private readonly string _url;
    private readonly System.Collections.Concurrent.BlockingCollection<string> _queue = new(100);

    /// <summary>확인 코드가 보낸 알림을 봄</summary>
    internal Action<string>? Sent;

    public Notifier(string url)
    {
        _url = url;
        if (url.Length > 0) new Thread(Run) { IsBackground = true, Name = "webhook" }.Start();
    }

    public void Send(string message)
    {
        Sent?.Invoke(message);
        if (_url.Length == 0) return;
        if (!_queue.TryAdd(message)) Log.Warning("webhook queue full; notice dropped");
    }

    private void Run()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        foreach (string message in _queue.GetConsumingEnumerable())
        {
            try
            {
                var body = new JsonObject { ["text"] = message, ["content"] = message };
                using var content = new StringContent(body.ToJsonString(Util.Compact), Util.Utf8, "application/json");
                using var response = http.Send(new HttpRequestMessage(HttpMethod.Post, _url) { Content = content });
            }
            catch (Exception error) { Log.Warning("webhook failed: " + error.GetType().Name); }
        }
    }
}

/// <summary>
/// 서버를 켠 뒤 API 에 닿은 PC (메모리에만): 실제로 접속하는 PC, 앱 버전, 마지막으로 받은 공지. 여기 없는 PC 는 서버에 닿지 못함 (방화벽 · 주소)
/// </summary>
internal sealed partial class Clients
{
    public const int Keep = 2000;
    public static readonly Dictionary<string, string> Features = new()
    {
        ["reports"] = "문제 보고", ["skills"] = "스킬", ["announcements"] = "공지", ["badkey"] = "키 틀림(거절)", ["launcher"] = "i-luncher",
        ["github"] = "GitHub 중계", ["equipment"] = "설비 상태",
    };

    internal sealed class Entry
    {
        public required string Address;
        public DateTimeOffset First, Last;
        public HashSet<string> Used = [];
        public string? Revision;
        public int Requests;
        public string Version = "";
    }

    private readonly Dictionary<string, Entry> _seen = [];
    private readonly Lock _lock = new();

    [GeneratedRegex(@"TerminalNotes/([0-9][0-9.]*)")]
    private static partial Regex AppAgent();

    public void Touch(string address, string agent, string feature)
    {
        var match = AppAgent().Match(agent ?? "");
        string version = match.Success ? match.Groups[1].Value : "";
        bool first;
        lock (_lock)
        {
            first = !_seen.TryGetValue(address, out var entry);
            if (first)
            {
                if (_seen.Count >= Keep) _seen.Remove(_seen.MinBy(pair => pair.Value.Last).Key);
                entry = _seen[address] = new Entry { Address = address, First = Util.Now() };
            }
            entry!.Last = Util.Now();
            entry.Requests++;
            if (version.Length > 0 || first) entry.Version = version;
            if (Features.ContainsKey(feature)) entry.Used.Add(feature);
        }
        if (first) Log.Info($"새 PC 접속: {address} (앱 {(version.Length > 0 ? version : "1.0.518 이하")})");
    }

    public void Received(string address, string revision)
    {
        lock (_lock)
            if (_seen.TryGetValue(address, out var entry)) entry.Revision = revision;
    }

    public int Having(string revision)
    {
        lock (_lock) return _seen.Values.Count(entry => entry.Revision == revision);
    }

    public List<Entry> All()
    {
        lock (_lock)
            return _seen.Values.Select(entry => new Entry
            {
                Address = entry.Address, First = entry.First, Last = entry.Last, Used = [.. entry.Used], Revision = entry.Revision,
                Requests = entry.Requests, Version = entry.Version,
            }).OrderByDescending(entry => entry.Last).ToList();
    }
}

/// <summary>로그 (relay.log, 1 MB 마다 돌려 5개 보관). 화면에도 쓸 수 있음</summary>
internal static class Log
{
    private static readonly Lock Sync = new();
    private static string? _file;
    private static bool _console;

    /// <summary>확인 코드가 로그를 봄</summary>
    internal static event Action<string>? Written;

    public static void Setup(string? file, bool console)
    {
        lock (Sync)
        {
            _file = file;
            _console = console;
            if (file is not null)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("로그 파일을 열지 못했습니다: " + error.Message); }
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARNING", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Exception(string message, Exception error) => Write("ERROR", message + Environment.NewLine + error);

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} {message}";
        Written?.Invoke(line);
        lock (Sync)
        {
            if (_console)
            {
                try { Console.WriteLine(line); } catch (IOException) { }
            }
            if (_file is null) return;
            try
            {
                var info = new FileInfo(_file);
                if (info.Exists && info.Length >= 1_048_576) Rotate(_file);
                File.AppendAllText(_file, line + Environment.NewLine, Util.Utf8);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Rotate(string file)
    {
        for (int index = 4; index >= 1; index--)
            if (File.Exists($"{file}.{index}")) File.Move($"{file}.{index}", $"{file}.{index + 1}", overwrite: true);
        File.Move(file, file + ".1", overwrite: true);
    }
}
