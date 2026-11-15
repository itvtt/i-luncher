using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiManager;

/// <summary>서버가 돌려준 오류 (메시지는 서버가 쓴 한국어 문장)</summary>
internal sealed class AdminException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// 이 PC 서버의 관리 기능을 부름: /admin/api/* (상태·문제 보고·스킬·공지, relay 2.14+) 와 /launcher/* (i-luncher 앱 목록·런처 공지).
/// 관리자 비밀번호는 서버 설정(relay.py SETTINGS 의 RELAY_ADMIN_PASSWORD)에서 읽으므로 따로 묻지 않음.
/// </summary>
internal sealed class AdminClient : IDisposable
{
    private readonly HttpClient _http;

    public AdminClient(string baseUrl, string password)
    {
        var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        // 기다리는 시간은 요청마다 (러너 등록·멈추기는 몇십 초 걸림)
        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ApiServerManager/" + ManagerHost.LauncherVersion);
        if (password.Length > 0)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + password)));
    }

    public Task<JsonNode> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        using var response = await _http.SendAsync(request, cancel.Token);
        string text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new AdminException(ErrorText(response.StatusCode, text), response.StatusCode);
        return JsonNode.Parse(text.Length == 0 ? "null" : text) ?? new JsonObject();
    }

    /// <summary>파일 그대로 올림 (스크린샷 이미지)</summary>
    public async Task<JsonNode> PostBytesAsync(string path, byte[] data, string contentType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/')) { Content = new ByteArrayContent(data) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var response = await _http.SendAsync(request, cancel.Token);
        string text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new AdminException(ErrorText(response.StatusCode, text), response.StatusCode);
        return JsonNode.Parse(text.Length == 0 ? "null" : text) ?? new JsonObject();
    }

    public async Task<byte[]> GetBytesAsync(string path)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var response = await _http.GetAsync(path.TrimStart('/'), cancel.Token);
        if (!response.IsSuccessStatusCode) throw new AdminException(ErrorText(response.StatusCode, await response.Content.ReadAsStringAsync()), response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static string ErrorText(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.Unauthorized)
            return "관리자 비밀번호가 맞지 않습니다. 설정 파일(relay.py 맨 위 SETTINGS)의 RELAY_ADMIN_PASSWORD 를 확인하세요.";
        if (status == HttpStatusCode.NotFound && body.Contains("찾을 수 없는 주소", StringComparison.Ordinal))
            return "서버가 이 기능을 모릅니다(서버 2.14 이상 필요). [다시 시작]으로 서버를 새 버전으로 바꾸세요.";
        string message = body.Trim();
        try
        {
            if (JsonNode.Parse(message) is JsonValue value && value.TryGetValue(out string? parsed)) message = parsed;
        }
        catch (JsonException) { }
        return message.Length > 0 ? message : $"서버 오류 ({(int)status})";
    }

    public void Dispose() => _http.Dispose();
}
