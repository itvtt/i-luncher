using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace IApi;

/// <summary>
/// 작은 HTTP/1.1 서버 (요청마다 스레드 하나, 응답 뒤 연결을 닫음). .NET 기본 실행 환경만 씀:
/// http.sys(HttpListener)는 다른 PC 의 주소로 받으려면 관리자 권한(URL 예약)이 필요해서 쓰지 않음
/// </summary>
internal sealed class HttpServer : IDisposable
{
    private readonly Socket _socket;
    private readonly Action<Request> _handle;
    private readonly X509Certificate2? _certificate;
    private volatile bool _stopped;

    public int Port { get; }

    public HttpServer(string bind, int port, X509Certificate2? certificate, Action<Request> handle)
    {
        _handle = handle;
        _certificate = certificate;
        IPAddress address = bind is "" or "0.0.0.0" ? IPAddress.Any : bind == "::" ? IPAddress.IPv6Any
            : IPAddress.TryParse(bind, out var parsed) ? parsed
            : Dns.GetHostAddresses(bind).FirstOrDefault(found => found.AddressFamily == AddressFamily.InterNetwork) ?? throw new SocketException((int)SocketError.AddressNotAvailable);
        _socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        // Windows 에서는 다른 서버가 같은 포트를 받지 못하게 (예전 서버와 같음), 다른 OS 는 TIME_WAIT 만 건너뜀
        if (OperatingSystem.IsWindows()) _socket.ExclusiveAddressUse = true;
        else _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            _socket.Bind(new IPEndPoint(address, port));
            _socket.Listen(256);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    public void Start() => new Thread(AcceptLoop) { IsBackground = true, Name = "http-" + Port }.Start();

    private void AcceptLoop()
    {
        while (!_stopped)
        {
            Socket client;
            try { client = _socket.Accept(); }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                if (_stopped) return;
                Thread.Sleep(50);
                continue;
            }
            new Thread(() => Serve(client)) { IsBackground = true, Name = "request" }.Start();
        }
    }

    private void Serve(Socket client)
    {
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = client.SendTimeout = 30_000;
            Stream stream = new NetworkStream(client, ownsSocket: false);
            if (_certificate is not null)
            {
                var secure = new SslStream(stream, leaveInnerStreamOpen: false);
                secure.AuthenticateAsServer(_certificate, clientCertificateRequired: false, SslProtocols.None, checkCertificateRevocation: false);
                stream = secure;
            }
            using (stream)
            {
                var remote = ((IPEndPoint)client.RemoteEndPoint!).Address;
                string address = (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
                var request = Request.Read(stream, address, _certificate is not null);
                if (request is null) return;
                _handle(request);
                request.Finish();
            }
            try { client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
        }
        catch (Exception error) when (error is IOException or SocketException or AuthenticationException or ObjectDisposedException) { }
        finally { client.Dispose(); }
    }

    public void Dispose()
    {
        _stopped = true;
        _socket.Dispose();
    }
}

/// <summary>요청 하나와 그 응답</summary>
internal sealed class Request
{
    private const int HeaderLimit = 65_536;
    public const int MaxBody = 25 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[16_384];
    private int _start, _end;
    private readonly List<(string Name, string Value)> _headers = [];

    public string Method { get; private init; } = "";
    /// <summary>요청 줄의 주소 그대로 (경로 + ?쿼리)</summary>
    public string Target { get; private init; } = "";
    public string Path { get; private init; } = "/";
    public List<string> Parts { get; private init; } = [];
    public Dictionary<string, List<string>> Query { get; private init; } = [];
    public string RemoteAddress { get; }
    public bool Secure { get; }
    /// <summary>응답을 시작한 상태 코드 (0 = 아직)</summary>
    public int Status { get; private set; }
    public bool BodyRead { get; private set; }
    /// <summary>요청에 맞은 API (관리 창의 API 화면이 세는 것)</summary>
    public ApiFunction? Function { get; set; }
    public Dictionary<string, string> Values { get; set; } = [];
    public string? FailureMessage { get; private set; }

    private Request(Stream stream, string remote, bool secure)
    {
        _stream = stream;
        RemoteAddress = remote;
        Secure = secure;
    }

    /// <summary>확인 코드가 서버 없이 요청을 만듦</summary>
    internal static Request ForTest(string method, string target, string remote = "127.0.0.1", Stream? body = null, params (string, string)[] headers)
    {
        var request = Parse(body ?? new MemoryStream(), remote, false, method, target);
        foreach (var header in headers) request._headers.Add(header);
        return request;
    }

    public static Request? Read(Stream stream, string remote, bool secure)
    {
        var reader = new Request(stream, remote, secure);
        string? line;
        do
        {
            line = reader.ReadLine(8192);
            if (line is null) return null;
        }
        while (line.Length == 0);  // 앞의 빈 줄은 건너뜀
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 3 || !words[2].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            reader.WriteRaw(400, "Bad Request", "잘못된 요청입니다.");
            return null;
        }
        var request = Parse(stream, remote, secure, words[0], words[1]);
        reader._buffer.AsSpan(reader._start, reader._end - reader._start).CopyTo(request._buffer);
        request._end = reader._end - reader._start;
        int total = 0;
        while (true)
        {
            string? header = request.ReadLine(8192);
            if (header is null) return null;
            if (header.Length == 0) break;
            total += header.Length;
            if (total > HeaderLimit)
            {
                request.WriteRaw(431, "Request Header Fields Too Large", "헤더가 너무 깁니다.");
                return null;
            }
            int colon = header.IndexOf(':');
            if (colon > 0) request._headers.Add((header[..colon].Trim(), header[(colon + 1)..].Trim()));
        }
        // HTTP/1.1 클라이언트가 본문 전에 기다리면 바로 보내라고 알림
        if (string.Equals(request.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase))
        {
            byte[] proceed = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();
            stream.Write(proceed);
        }
        return request;
    }

    private static Request Parse(Stream stream, string remote, bool secure, string method, string target)
    {
        string path = target, query = "";
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            path = absolute.AbsolutePath;
            query = absolute.Query.TrimStart('?');
        }
        else
        {
            int mark = target.IndexOf('?');
            if (mark >= 0)
            {
                path = target[..mark];
                query = target[(mark + 1)..];
            }
            int hash = path.IndexOf('#');
            if (hash >= 0) path = path[..hash];
        }
        return new Request(stream, remote, secure)
        {
            Method = method.ToUpperInvariant(),
            Target = target,
            Path = path,
            Parts = path.Split('/').Where(part => part.Length > 0).Select(Util.Unquote).ToList(),
            Query = Util.ParseQuery(query),
        };
    }

    public string? Header(string name)
    {
        foreach (var (key, value) in _headers)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    public string HeaderOr(string name, string fallback = "") => Header(name) ?? fallback;

    public string? QueryValue(string name) => Query.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    public string Value(string name) => Values.TryGetValue(name, out var value) ? value : "";

    // ── 읽기 ─────────────────────────────────────────────────────────────────────────────────────

    private bool Fill()
    {
        if (_start > 0)
        {
            _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length) return false;
        int read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read <= 0) return false;
        _end += read;
        return true;
    }

    /// <summary>한 줄 (줄바꿈 없이, Latin-1 → 헤더는 그대로 바이트), 연결이 끊기면 null</summary>
    private string? ReadLine(int limit)
    {
        while (true)
        {
            int newline = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)'\n');
            if (newline >= 0)
            {
                int length = newline > 0 && _buffer[_start + newline - 1] == '\r' ? newline - 1 : newline;
                string line = Encoding.UTF8.GetString(_buffer, _start, length);
                _start += newline + 1;
                return line;
            }
            if (_end - _start >= limit) return null;
            try
            {
                if (!Fill()) return null;
            }
            catch (IOException) { return null; }
        }
    }

    private int ReadSome(Span<byte> target)
    {
        if (_end > _start)
        {
            int count = Math.Min(target.Length, _end - _start);
            _buffer.AsSpan(_start, count).CopyTo(target);
            _start += count;
            return count;
        }
        return _stream.Read(target);
    }

    private byte[] ReadExactly(int size)
    {
        var data = new byte[size];
        int done = 0;
        while (done < size)
        {
            int read;
            try { read = ReadSome(data.AsSpan(done)); }
            catch (IOException) { read = 0; }
            if (read <= 0) throw new RelayError(400, "요청이 중간에 끊겼습니다.");
            done += read;
        }
        return data;
    }

    /// <summary>요청 본문 (Content-Length 또는 chunked: .NET HttpClient + JsonContent 는 길이 없이 chunked 로 보냄)</summary>
    public byte[] Body()
    {
        if ((Header("Transfer-Encoding") ?? "").Contains("chunked", StringComparison.OrdinalIgnoreCase)) return ChunkedBody();
        string? length = Header("Content-Length");
        if (length is null) throw new RelayError(411, "Content-Length가 필요합니다.");
        if (!long.TryParse(length.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long size)) throw new RelayError(400, "Content-Length가 올바르지 않습니다.");
        if (size < 0 || size > MaxBody) throw new RelayError(413, "한 번에 25MB까지 받습니다.");
        BodyRead = true;
        return ReadExactly((int)size);
    }

    private byte[] ChunkedBody()
    {
        BodyRead = true;
        using var data = new MemoryStream();
        while (true)
        {
            string? line = ReadLine(1024) ?? throw new RelayError(400, "요청이 중간에 끊겼습니다.");
            string sizeText = line.Split(';', 2)[0].Trim();
            if (!long.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long size)) throw new RelayError(400, "요청 형식이 올바르지 않습니다.");
            if (size < 0 || data.Length + size > MaxBody) throw new RelayError(413, "한 번에 25MB까지 받습니다.");
            if (size == 0)
            {
                while (ReadLine(1024) is { Length: > 0 }) { }  // 끝 머리글 (trailer)
                return data.ToArray();
            }
            data.Write(ReadExactly((int)size));
            ReadLine(8);  // 조각마다 붙는 CRLF
        }
    }

    public JsonNode? JsonBody() => Util.ParseJson(Body());

    /// <summary>JSON 객체 본문 (아니면 400)</summary>
    public JsonObject JsonObjectBody(string message = "JSON 객체가 필요합니다.") => JsonBody() as JsonObject ?? throw new RelayError(400, message);

    public Dictionary<string, string> Form()
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(Body()); }
        catch (DecoderFallbackException) { throw new RelayError(400, "형식이 올바르지 않습니다."); }
        return Util.ParseQuery(text, keepBlank: true).ToDictionary(pair => pair.Key, pair => pair.Value[^1]);
    }

    /// <summary>일찍 거절하기 전에 읽지 않은 본문을 읽어 둠: 클라이언트가 연결 끊김 대신 상태를 받게</summary>
    public void Drain()
    {
        if (BodyRead) return;
        BodyRead = true;
        if (Header("Transfer-Encoding") is not null) return;
        if (!long.TryParse(Header("Content-Length") ?? "0", out long remaining) || remaining <= 0 || remaining > MaxBody) return;
        var sink = new byte[65_536];
        try
        {
            while (remaining > 0)
            {
                int read = ReadSome(sink.AsSpan(0, (int)Math.Min(sink.Length, remaining)));
                if (read <= 0) break;
                remaining -= read;
            }
        }
        catch (IOException) { }
    }

    // ── 쓰기 ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<int, string> Reasons = new()
    {
        [100] = "Continue", [200] = "OK", [201] = "Created", [202] = "Accepted", [204] = "No Content", [206] = "Partial Content",
        [301] = "Moved Permanently", [302] = "Found", [303] = "See Other", [304] = "Not Modified", [307] = "Temporary Redirect", [308] = "Permanent Redirect",
        [400] = "Bad Request", [401] = "Unauthorized", [403] = "Forbidden", [404] = "Not Found", [405] = "Method Not Allowed", [409] = "Conflict",
        [410] = "Gone", [411] = "Length Required", [413] = "Payload Too Large", [415] = "Unsupported Media Type", [422] = "Unprocessable Entity",
        [429] = "Too Many Requests", [431] = "Request Header Fields Too Large", [500] = "Internal Server Error", [501] = "Not Implemented",
        [502] = "Bad Gateway", [503] = "Service Unavailable",
    };

    private void WriteHead(int status, IEnumerable<(string, string)> headers)
    {
        Status = status;
        var text = new StringBuilder();
        text.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reasons.GetValueOrDefault(status, "Status")).Append("\r\n");
        text.Append("Server: i-api/").Append(Program.Version).Append("\r\n");
        text.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        text.Append("Connection: close\r\n");
        foreach (var (name, value) in headers) text.Append(name).Append(": ").Append(value).Append("\r\n");
        text.Append("\r\n");
        _stream.Write(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private void WriteRaw(int status, string reason, string message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message);
        try
        {
            _stream.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nConnection: close\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\n\r\n"));
            _stream.Write(body);
        }
        catch (IOException) { }
    }

    public void Send(int status, byte[] body, string contentType, IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        var headers = new List<(string, string)>
        {
            ("Content-Type", contentType), ("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture)),
            ("X-Content-Type-Options", "nosniff"), ("Cache-Control", "no-store"),
        };
        if (extra is not null) headers.AddRange(extra.Select(pair => (pair.Key, pair.Value)));
        WriteHead(status, headers);
        if (Method != "HEAD") _stream.Write(body);
    }

    public void Json(int status, JsonNode? value, IEnumerable<KeyValuePair<string, string>>? extra = null) =>
        Send(status, Util.Utf8.GetBytes(Util.ToJson(value)), "application/json; charset=utf-8", extra);

    public void Text(int status, string text) => Send(status, Util.Utf8.GetBytes(text), "text/plain; charset=utf-8");

    public void Html(int status, string title, string body) => Send(status, Util.Utf8.GetBytes(AdminPages.Page(title, body)), "text/html; charset=utf-8",
    [
        new("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'"),
        new("X-Frame-Options", "DENY"), new("Referrer-Policy", "same-origin"),
    ]);

    public void File(string path, string contentType, string downloadName) =>
        Send(200, Util.ReadBytes(path), contentType, [new("Content-Disposition", "attachment; filename*=UTF-8''" + Util.Quote(downloadName, ""))]);

    public void Redirect(string location) => WriteHead(303, [("Location", location), ("Content-Length", "0")]);

    /// <summary>바뀌지 않음 (ETag 가 같음)</summary>
    public void NotModified(string etag, params (string, string)[] extra) => WriteHead(304, new[] { ("ETag", etag) }.Concat(extra));

    /// <summary>길이를 모르는 파일을 그대로 흘려보냄 (GitHub 중계의 릴리스 파일). 돌려준 스트림에 씀</summary>
    public Stream StartStream(int status, string contentType, long? length)
    {
        var headers = new List<(string, string)> { ("Content-Type", contentType) };
        if (length is { } size) headers.Add(("Content-Length", size.ToString(CultureInfo.InvariantCulture)));
        headers.Add(("Cache-Control", "no-store"));
        WriteHead(status, headers);
        return _stream;
    }

    /// <summary>
    /// 오류 응답: 앱과 관리 창은 400·409 등에서 JSON 글자를 읽음 (예전 서버와 같음), 관리 페이지는 글자 그대로.
    /// 응답을 이미 시작했으면 아무것도 하지 않음
    /// </summary>
    public void Error(int status, string message)
    {
        FailureMessage ??= message;
        if (Status != 0) return;
        Drain();
        if (Path.StartsWith("/api/", StringComparison.Ordinal) || Path.StartsWith("/admin/api/", StringComparison.Ordinal))
            Send(status, Util.Utf8.GetBytes(Util.ToJson(JsonValue.Create(message))), "application/json; charset=utf-8");
        else Send(status, Util.Utf8.GetBytes(message), "text/plain; charset=utf-8");
    }

    /// <summary>관리자 비밀번호를 물음 (브라우저가 로그인 창을 띄움)</summary>
    public void Challenge()
    {
        Drain();
        FailureMessage ??= "관리자 비밀번호가 필요합니다.";
        byte[] body = Util.Utf8.GetBytes("관리자 비밀번호가 필요합니다.");
        WriteHead(401, [("WWW-Authenticate", "Basic realm=\"Terminal Notes relay\", charset=\"UTF-8\""), ("Content-Type", "text/plain; charset=utf-8"),
            ("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture))]);
        _stream.Write(body);
    }

    public void Finish()
    {
        try { _stream.Flush(); } catch (IOException) { }
    }
}

/// <summary>응답을 이미 썼음 (관리자 비밀번호 묻기 등)</summary>
internal sealed class HandledException : Exception;
