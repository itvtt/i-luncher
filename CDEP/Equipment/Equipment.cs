using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IApi;

internal sealed class MySqlError(string message) : Exception(message);

/// <summary>설비 DB 연결 하나 (확인 코드가 가짜로 바꿈)</summary>
internal interface IEquipmentDb : IDisposable
{
    List<string?[]> Query(string sql);
}

/// <summary>
/// 쿼리 하나에 필요한 만큼의 MySQL: 서버가 TLS 를 주면 TLS, caching_sha2_password (MySQL 8 기본, 전체 인증에는 그 TLS 가 필요) 또는 mysql_native_password, 글자 결과
/// </summary>
internal sealed class MySqlClient : IEquipmentDb
{
    private const uint ClientFlags = 0x1 | 0x200 | 0x2000 | 0x8000 | 0x80000;  // long password, protocol 41, transactions, secure connection, plugin auth
    private const uint ConnectWithDb = 0x8, ClientSsl = 0x800;

    private readonly TcpClient _socket;
    private Stream _stream;
    private byte _sequence;

    public MySqlClient(string host, int port, string user, string password, string database, int timeoutSeconds = 8)
    {
        _socket = new TcpClient();
        try
        {
            var connect = _socket.ConnectAsync(host, port);
            if (!connect.Wait(TimeSpan.FromSeconds(timeoutSeconds))) throw new MySqlError("timed out");
            _socket.ReceiveTimeout = _socket.SendTimeout = timeoutSeconds * 1000;
            _stream = _socket.GetStream();
            Login(user, password ?? "", database);
        }
        catch (AggregateException error) when (error.InnerException is SocketException inner)
        {
            _socket.Dispose();
            throw new MySqlError(inner.Message);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private byte[] Exact(int size)
    {
        var data = new byte[size];
        int done = 0;
        while (done < size)
        {
            int read = _stream.Read(data, done, size - done);
            if (read <= 0) throw new MySqlError("DB 연결이 끊겼습니다.");
            done += read;
        }
        return data;
    }

    private byte[] ReadPacket()
    {
        using var payload = new MemoryStream();
        while (true)
        {
            byte[] header = Exact(4);
            int size = header[0] | header[1] << 8 | header[2] << 16;
            _sequence = (byte)(header[3] + 1);
            payload.Write(Exact(size));
            if (size < 0xFFFFFF) return payload.ToArray();
        }
    }

    private void WritePacket(byte[] payload)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        header[3] = _sequence;
        _stream.Write(header);
        _stream.Write(payload);
        _stream.Flush();
        _sequence++;
    }

    private static MySqlError Failure(byte[] packet)
    {
        int code = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1, 2));
        int start = packet.Length > 3 && packet[3] == '#' ? 9 : 3;
        return new MySqlError($"MySQL {code}: {Encoding.UTF8.GetString(packet, Math.Min(start, packet.Length), Math.Max(0, packet.Length - start))}");
    }

    private static byte[] Scramble(string plugin, string password, byte[] nonce)
    {
        if (password.Length == 0) return [];
        byte[] first, second;
        if (plugin == "caching_sha2_password")
        {
            first = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            second = SHA256.HashData([.. SHA256.HashData(first), .. nonce]);
        }
        else
        {
            first = SHA1.HashData(Encoding.UTF8.GetBytes(password));
            second = SHA1.HashData([.. nonce, .. SHA1.HashData(first)]);
        }
        return first.Zip(second, (a, b) => (byte)(a ^ b)).ToArray();
    }

    private static byte[] HandshakeHead(uint flags)
    {
        var head = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(head, flags);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), 1 << 24);
        head[8] = 45;
        return head;
    }

    private void Login(string user, string password, string database)
    {
        byte[] packet = ReadPacket();
        if (packet[0] == 0xFF) throw Failure(packet);
        int position = Array.IndexOf(packet, (byte)0, 1) + 1 + 4;
        byte[] nonce = packet[position..(position + 8)];
        position += 9;
        uint serverFlags = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(position, 2));
        position += 5;
        serverFlags |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(position, 2)) << 16;
        position += 2;
        int authLength = packet[position];
        position += 11;
        int rest = Math.Max(13, authLength - 8);
        nonce = [.. nonce, .. packet[position..Math.Min(packet.Length, position + rest - 1)]];
        int pluginStart = Math.Min(packet.Length, position + rest);
        int pluginEnd = Array.IndexOf(packet, (byte)0, pluginStart);
        string plugin = Encoding.ASCII.GetString(packet, pluginStart, (pluginEnd < 0 ? packet.Length : pluginEnd) - pluginStart);
        if (plugin.Length == 0) plugin = "mysql_native_password";
        uint flags = ClientFlags | (database.Length > 0 ? ConnectWithDb : 0);
        bool secure = false;
        if ((serverFlags & ClientSsl) != 0)
        {
            flags |= ClientSsl;
            WritePacket(HandshakeHead(flags));
            // DB 의 인증서는 보통 자체 서명: 여기서 TLS 는 비밀번호가 그대로 지나가지 않게 함
            var tls = new SslStream(_stream, false, (_, _, _, _) => true);
            tls.AuthenticateAsClient(new SslClientAuthenticationOptions { TargetHost = "", RemoteCertificateValidationCallback = (_, _, _, _) => true });
            _stream = tls;
            secure = true;
        }
        byte[] auth = Scramble(plugin, password, nonce);
        using var body = new MemoryStream();
        body.Write(HandshakeHead(flags));
        body.Write(Encoding.UTF8.GetBytes(user));
        body.WriteByte(0);
        body.WriteByte((byte)auth.Length);
        body.Write(auth);
        if (database.Length > 0)
        {
            body.Write(Encoding.UTF8.GetBytes(database));
            body.WriteByte(0);
        }
        body.Write(Encoding.ASCII.GetBytes(plugin));
        body.WriteByte(0);
        WritePacket(body.ToArray());
        while (true)
        {
            packet = ReadPacket();
            if (packet[0] == 0x00) return;
            if (packet[0] == 0xFF) throw Failure(packet);
            if (packet[0] == 0xFE)
            {
                // 서버가 다른 방식을 물음
                int end = Array.IndexOf(packet, (byte)0, 1);
                plugin = Encoding.ASCII.GetString(packet, 1, (end < 0 ? packet.Length : end) - 1);
                byte[] data = end < 0 ? [] : packet[(end + 1)..];
                if (plugin == "mysql_clear_password" && secure) WritePacket([.. Encoding.UTF8.GetBytes(password), 0]);
                else if (plugin is "caching_sha2_password" or "mysql_native_password")
                {
                    int length = data.Length;
                    while (length > 0 && data[length - 1] == 0) length--;
                    WritePacket(Scramble(plugin, password, data[..length]));
                }
                else throw new MySqlError($"지원하지 않는 DB 로그인 방식입니다: {plugin}");
                continue;
            }
            if (packet[0] == 0x01 && packet.Length > 1 && packet[1] == 0x03) continue;  // caching_sha2 빠른 길: OK 가 뒤따름
            if (packet[0] == 0x01 && packet.Length > 1 && packet[1] == 0x04)
            {
                if (!secure) throw new MySqlError("이 DB 계정은 TLS 연결에서만 로그인할 수 있습니다.");
                WritePacket([.. Encoding.UTF8.GetBytes(password), 0]);
                continue;
            }
            throw new MySqlError("DB 로그인 응답을 이해하지 못했습니다.");
        }
    }

    private static (long? Value, int Position) Length(byte[] data, int position)
    {
        byte first = data[position];
        return first switch
        {
            < 0xFB => (first, position + 1),
            0xFB => (null, position + 1),
            0xFC => (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 1, 2)), position + 3),
            0xFD => (data[position + 1] | data[position + 2] << 8 | data[position + 3] << 16, position + 4),
            _ => ((long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(position + 1, 8)), position + 9),
        };
    }

    /// <summary>SELECT 의 줄 (칸마다 글자 또는 null)</summary>
    public List<string?[]> Query(string sql)
    {
        _sequence = 0;
        WritePacket([0x03, .. Encoding.UTF8.GetBytes(sql)]);
        byte[] packet = ReadPacket();
        if (packet[0] == 0xFF) throw Failure(packet);
        if (packet[0] == 0x00) return [];
        int count = (int)(Length(packet, 0).Value ?? 0);
        for (int index = 0; index < count; index++) ReadPacket();  // 열 정의
        ReadPacket();  // 그 끝
        var rows = new List<string?[]>();
        while (true)
        {
            packet = ReadPacket();
            if (packet[0] == 0xFE && packet.Length < 9) return rows;
            if (packet[0] == 0xFF) throw Failure(packet);
            int position = 0;
            var row = new string?[count];
            for (int index = 0; index < count; index++)
            {
                (long? size, position) = Length(packet, position);
                row[index] = size is null ? null : Encoding.UTF8.GetString(packet, position, (int)size);
                position += (int)(size ?? 0);
            }
            rows.Add(row);
        }
    }

    public void Dispose()
    {
        try
        {
            _sequence = 0;
            if (_stream is not null) WritePacket([0x01]);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        try { _stream?.Dispose(); } catch (IOException) { }
        _socket.Dispose();
    }
}

/// <summary>
/// EQ Status 의 설비 표: DB 에서 EQUIPMENT_REFRESH_SECONDS 마다 많아야 한 번 읽어 둠 (앱이 몇 대가 물어도 DB 에는 그 간격마다 쿼리 하나).
/// 읽기가 실패하면 마지막 표를 잠시 쓰다가 503
/// </summary>
internal sealed partial class EquipmentStatus
{
    [GeneratedRegex(@"^[A-Za-z0-9_$]+(\.[A-Za-z0-9_$]+)?$")] private static partial Regex TableName();

    private readonly Config _config;
    private readonly Lock _lock = new();
    private (JsonArray Items, string ETag, DateTimeOffset At)? _current;
    private long _tried;
    private bool _everTried;
    private string _error = "";

    /// <summary>확인 코드가 가짜 DB 로 바꿈 (host, port, user, password, database)</summary>
    internal Func<string, int, string, string, string, IEquipmentDb> Connect = (host, port, user, password, database) => new MySqlClient(host, port, user, password, database);

    public EquipmentStatus(Config config) => _config = config;

    public bool Configured => _config.EquipmentDbHost.Length > 0;

    public (JsonArray Items, string ETag, DateTimeOffset At) Snapshot()
    {
        if (!Configured) throw new RelayError(404, "설비 상태 DB가 설정되지 않았습니다(API 서버 설정 파일의 EQUIPMENT_DB_*).");
        int interval = _config.EquipmentRefreshSeconds;
        lock (_lock)
        {
            if (Environment.TickCount64 - _tried >= interval * 1000L || _current is null && !_everTried)
            {
                _tried = Environment.TickCount64;
                _everTried = true;
                try
                {
                    var items = ReadTable();
                    byte[] body = Util.Utf8.GetBytes(Sorted(items).ToJsonString(Util.Compact));
                    _current = (items, "\"" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()[..32] + "\"", Util.Now());
                    _error = "";
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (_error.Length == 0) Log.Warning("equipment status read failed: " + error.Message);
                    _error = error.Message.Length > 0 ? error.Message : error.GetType().Name;
                }
            }
            bool stale = _current is null || _error.Length > 0 && (Util.Now() - _current.Value.At).TotalSeconds > Math.Max(60, interval * 6);
            if (stale) throw new RelayError(503, "설비 상태 DB를 읽지 못했습니다: " + (_error.Length > 0 ? _error : "아직 읽은 적이 없습니다."));
            return _current!.Value;
        }
    }

    /// <summary>ETag 를 위해 키 순서를 정렬한 사본</summary>
    private static JsonArray Sorted(JsonArray items) => new(items.OfType<JsonObject>()
        .Select(item => (JsonNode)new JsonObject(item.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())))).ToArray());

    private JsonArray ReadTable()
    {
        if (!TableName().IsMatch(_config.EquipmentTable)) throw new MySqlError("EQUIPMENT_STATUS_TABLE은 표 이름(영문·숫자·_)이어야 합니다.");
        string table = string.Join(".", _config.EquipmentTable.Split('.').Select(part => $"`{part}`"));
        List<string?[]> rows;
        using (var client = Connect(_config.EquipmentDbHost, _config.EquipmentDbPort, _config.EquipmentDbUser, _config.EquipmentDbPassword, _config.EquipmentDbName))
            rows = client.Query("SELECT `line`, `sdwt`, `prc`, `eqpid`, `status`, `Hr`, `Down_time`, `comment` FROM " + table +
                                " WHERE LOWER(COALESCE(`view`, '')) <> 'hide' ORDER BY `line`, `prc`, `eqpid`");
        var items = new JsonArray();
        foreach (var row in rows)
        {
            string? Cell(int index) => index < row.Length ? row[index] : null;
            double hours = double.TryParse(Cell(5) ?? "0", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0.0;
            items.Add(new JsonObject
            {
                ["line"] = Cell(0) ?? "", ["sdwt"] = Cell(1) ?? "", ["process"] = Cell(2) ?? "", ["equipmentId"] = Cell(3) ?? "",
                ["status"] = (Cell(4) ?? "").Trim().ToUpperInvariant(), ["hours"] = hours, ["downTime"] = Cell(6) ?? "", ["comment"] = Cell(7) ?? "",
            });
        }
        return items;
    }

    public string State()
    {
        if (!Configured) return "설비 상태: 꺼짐";
        lock (_lock)
        {
            if (_current is null) return "설비 상태: " + (_error.Length > 0 ? $"DB 읽기 실패 — {_error}" : "아직 요청 없음");
            string text = $"설비 상태: {_current.Value.Items.Count}대 · {Util.LocalText(_current.Value.At)} 읽음";
            return text + (_error.Length > 0 ? $" · 마지막 읽기 실패 — {_error}" : "");
        }
    }
}
