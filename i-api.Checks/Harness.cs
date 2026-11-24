using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace IApi.Checks;

/// <summary>응답 하나</summary>
internal sealed record Reply(int Status, byte[] Body, Dictionary<string, string> Headers)
{
    public string Text => Encoding.UTF8.GetString(Body);
    public JsonNode? Json => Body.Length == 0 ? null : JsonNode.Parse(Body);
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
}

/// <summary>확인 하나에 쓰는 서버: 루프백의 빈 포트, 임시 폴더, 같은 설정 (예전 test_relay 의 setUp)</summary>
internal sealed class Harness : IDisposable
{
    public const string Key = "test-ingest-key", Admin = "test-admin-password";
    public static readonly Hashtable NoEnvironment = new();

    public string Root { get; }
    public Config Config { get; }
    public RelayServer Server { get; }
    public string Base => $"http://127.0.0.1:{Server.Port}";
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public Harness(params string[] extra)
    {
        Root = NewFolder("i-api check");
        Config = Make(Root, [
            "RELAY_PORT=0", $"RELAY_INGEST_KEY={Key}", $"RELAY_ADMIN_PASSWORD={Admin}", $"RELAY_DATA_ROOT={Root}", $"RELAY_DATA_DIR={Root}\\reports",
            $"RELAY_SKILLS_DIR={Root}\\skills", $"RELAY_PLUGINS_DIR={Root}\\plugins", $"RELAY_ANNOUNCEMENTS_DIR={Root}\\announcements", $"RELAY_LOG_FILE={Root}\\relay.log",
            $"RELAY_BACKUP_DIR={Root}\\backups", "RELAY_REPORT_RETENTION_DAYS=90", "RELAY_UPLOADS_PER_HOUR=5", "MASTER_USERS=editor_one, DOMAIN\\editor_two", .. extra], bind: "127.0.0.1");
        Server = new RelayServer(Config);
        Server.Start();
    }

    public static string NewFolder(string prefix)
    {
        string folder = Path.Combine(Path.GetTempPath(), prefix + " " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static Config Make(string root, IEnumerable<string> sets, string? bind = null)
    {
        var options = new Options { SettingsFile = Path.Combine(root, "settings.txt"), Bind = bind };
        options.Sets.AddRange(sets);
        return new Config(options, new Dictionary<string, string>(), NoEnvironment);
    }

    public static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    public void Dispose()
    {
        Server.Dispose();
        _http.Dispose();
        try { Directory.Delete(Root, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // ── HTTP ────────────────────────────────────────────────────────────────────────────────────

    public Reply Call(string method, string path, byte[]? body = null, IEnumerable<(string, string)>? headers = null, bool key = true, string? admin = null, string? contentType = null) =>
        Send(_http, Base, method, path, body, headers, key, admin, contentType);

    public static Reply Send(HttpClient http, string root, string method, string path, byte[]? body = null, IEnumerable<(string, string)>? headers = null, bool key = true,
        string? admin = null, string? contentType = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), root + path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        if (key) request.Headers.TryAddWithoutValidation("X-TerminalNotes-Key", Key);
        if (admin is not null) request.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + admin)));
        foreach (var (name, value) in headers ?? [])
            if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content?.Headers.TryAddWithoutValidation(name, value);
        using var response = http.Send(request);
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers)) all[header.Key] = string.Join(",", header.Value);
        return new Reply((int)response.StatusCode, response.Content.ReadAsByteArrayAsync().Result, all);
    }

    public Reply Json(string method, string path, JsonNode? value = null, IEnumerable<(string, string)>? headers = null, bool key = true, string? admin = null) =>
        Call(method, path, value is null ? null : Encoding.UTF8.GetBytes(value.ToJsonString()), headers, key, admin, value is null ? null : "application/json");

    public Reply AdminJson(string method, string path, JsonNode? value = null) => Json(method, path, value, key: false, admin: Admin);

    public Reply Form(string path, IEnumerable<(string, string)> fields, string? origin = null)
    {
        string body = string.Join("&", fields.Select(field => Uri.EscapeDataString(field.Item1) + "=" + Uri.EscapeDataString(field.Item2)));
        var headers = origin is null ? new List<(string, string)>() : [("Origin", origin)];
        return Call("POST", path, Encoding.UTF8.GetBytes(body), headers, key: false, admin: Admin, contentType: "application/x-www-form-urlencoded");
    }

    public Reply UploadReport(string text, byte[]? logs = null)
    {
        const string boundary = "b3f2e0f2-9a2b-4c3e-8a77-0123456789ab";
        var body = new MemoryStream();
        void Write(string value) => body.Write(Encoding.UTF8.GetBytes(value));
        Write($"--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Disposition: form-data; name=report\r\n\r\n{text}\r\n");
        if (logs is not null)
        {
            Write($"--{boundary}\r\nContent-Type: application/zip\r\nContent-Disposition: form-data; name=logs; filename=logs.zip; filename*=utf-8''logs.zip\r\n\r\n");
            body.Write(logs);
            Write("\r\n");
        }
        Write($"--{boundary}--\r\n");
        return Call("POST", "/api/reports", body.ToArray(), contentType: $"multipart/form-data; boundary=\"{boundary}\"");
    }

    /// <summary>다른 루프백 주소에서 온 요청 (서버에는 다른 PC)</summary>
    public Reply CallFrom(string address, string method, string path, IEnumerable<(string, string)>? headers = null, string? key = Key)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Parse(address), 0));
        socket.Connect(IPAddress.Loopback, Server.Port);
        var text = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{Server.Port}\r\nConnection: close\r\n");
        if (key is not null) text.Append($"X-TerminalNotes-Key: {key}\r\n");
        foreach (var (name, value) in headers ?? []) text.Append($"{name}: {value}\r\n");
        text.Append("\r\n");
        socket.Send(Encoding.UTF8.GetBytes(text.ToString()));
        var data = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = socket.Receive(buffer)) > 0) data.Write(buffer, 0, read);
        byte[] raw = data.ToArray();
        int split = raw.AsSpan().IndexOf("\r\n\r\n"u8);
        string head = Encoding.UTF8.GetString(raw, 0, split);
        var lines = head.Split("\r\n");
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) all[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new Reply(int.Parse(lines[0].Split(' ')[1]), raw[(split + 4)..], all);
    }
}

/// <summary>확인 실패</summary>
internal sealed class CheckFailed(string message) : Exception(message);

internal static class Expect
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new CheckFailed(message);
    }

    public static void Equal<T>(T expected, T actual, string message = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new CheckFailed($"{message} expected <{expected}> got <{actual}>");
    }

    public static void Contains(string part, string text, string message = "")
    {
        if (!text.Contains(part, StringComparison.Ordinal)) throw new CheckFailed($"{message} <{part}> not in <{(text.Length > 400 ? text[..400] + "…" : text)}>");
    }

    public static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new CheckFailed(message);
    }

    public static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : node?.ToJsonString() ?? "null";
    public static long Long(JsonNode? node) => long.Parse(node!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
    public static double Double(JsonNode? node) => double.Parse(node!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>설비 DB 대신 (줄과 받은 쿼리)</summary>
internal sealed class FakeDb(List<string?[]> rows, List<string> queries) : IEquipmentDb
{
    public List<string?[]> Query(string sql)
    {
        queries.Add(sql);
        return rows;
    }

    public void Dispose() { }
}
