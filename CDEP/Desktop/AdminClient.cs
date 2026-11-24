using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IApi.Manager;

/// <summary>서버가 돌려준 오류 (메시지는 서버가 쓴 한국어 문장)</summary>
internal sealed class AdminException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// 이 서버의 관리 기능을 부름: /admin/api/* (상태 · API · 스킬 · 공지 · 릴리스 · 러너) 와 /launcher/* (i-luncher 서버 앱 목록 · 스크린샷).
/// 관리자 비밀번호는 서버 설정(settings.txt 의 RELAY_ADMIN_PASSWORD)에서 읽으므로 따로 묻지 않음.
/// </summary>
internal sealed class AdminClient : IDisposable
{
    private readonly HttpClient _http;

    public AdminClient(string baseUrl, string password)
    {
        var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        // 기다리는 시간은 요청마다 (러너 등록·멈추기는 몇십 초 걸림)
        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("i-api-manager/" + Program.Version);
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

    /// <summary>
    /// 사용자 API 시험 호출: 그 방법 · 주소로 그대로 보내고 (관리자 비밀번호와 앱 키를 함께) 상태 · 받은 글 · 걸린 시간을 돌려줌. 실패도 그대로 돌려줌
    /// </summary>
    public async Task<(int Status, string Text, long Milliseconds)> TryAsync(string method, string path, string body, string appKey)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path.TrimStart('/'));
        if (appKey.Length > 0) request.Headers.TryAddWithoutValidation("X-TerminalNotes-Key", appKey);
        if (body.Length > 0) request.Content = new StringContent(body, Encoding.UTF8, body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[') ? "application/json" : "text/plain");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(130));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, cancel.Token);
        string text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text, watch.ElapsedMilliseconds);
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
            return "관리자 비밀번호가 맞지 않습니다. 설정 파일(settings.txt)의 RELAY_ADMIN_PASSWORD 를 확인하고 [다시 시작]하세요.";
        if (status == HttpStatusCode.NotFound && body.Contains("찾을 수 없는 주소", StringComparison.Ordinal))
            return "서버가 이 기능을 모릅니다. [다시 시작]으로 이 버전의 서버를 켜세요.";
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
