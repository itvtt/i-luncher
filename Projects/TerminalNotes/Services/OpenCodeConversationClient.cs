using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerminalNotes.Services;

/// <summary>Typed, directory-scoped HTTP access to one headless OpenCode server.</summary>
public sealed class OpenCodeConversationClient : IDisposable
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumRequestBytes = 1024 * 1024;
    private const int MaximumSessionScopeDescendants = 100;
    private const int MaximumSessionScopeDepth = 16;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SessionScopeTimeout = TimeSpan.FromSeconds(5);
    private readonly Uri _endpoint;
    private readonly string _directory;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly AuthenticationHeaderValue? _authorization;
    private bool _disposed;

    public OpenCodeConversationClient(Uri endpoint, string directory, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("The OpenCode endpoint must be an absolute loopback HTTP URI without credentials, query, or fragment.", nameof(endpoint));
        _endpoint = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + endpoint.AbsolutePath.TrimEnd('/') + "/", UriKind.Absolute);
        _directory = Path.GetFullPath(directory);
        if (Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD") is { Length: > 0 } password)
            _authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes((Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode") + ":" + password)));
        if (httpClient is null)
        {
            _httpClient = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsClient = true;
        }
        else _httpClient = httpClient;
    }

    public Task<JsonElement> ListSessionsAsync(CancellationToken cancellationToken = default) => GetJsonAsync(BuildEndpoint("/session"), cancellationToken);

    public Task<JsonElement> CreateSessionAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return SendJsonAsync(HttpMethod.Post, BuildEndpoint("/session"), new { title }, cancellationToken);
    }

    public async Task<JsonElement> GetSessionAsync(string sessionID, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionID);
        JsonElement session = await GetJsonAsync(BuildSessionEndpoint(sessionID), cancellationToken).ConfigureAwait(false);
        if (GetString(session, "id") != sessionID)
            throw new InvalidDataException("OpenCode returned a different session ID than requested.");
        string? directory = GetString(session, "directory");
        if (string.IsNullOrWhiteSpace(directory) || !DirectoriesEqual(directory, _directory))
            throw new InvalidDataException("OpenCode returned a session outside the selected project directory.");
        if (GetString(session, "parentID") is not null)
            throw new InvalidDataException("The requested session is not a root session.");
        return session;
    }

    /// <summary>Returns the verified root session and its bounded same-project descendants.</summary>
    public async Task<IReadOnlySet<string>> ReadSessionScopeAsync(string rootSessionID, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionID);
        if (!IsValidSessionID(rootSessionID)) throw new ArgumentException("The root session ID is malformed.", nameof(rootSessionID));
        using var scopeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scopeTimeout.CancelAfter(SessionScopeTimeout);
        CancellationToken token = scopeTimeout.Token;

        JsonElement root = await GetSessionAsync(rootSessionID, token).ConfigureAwait(false);
        if (HasNonNullProperty(root, "parentID"))
            throw new InvalidDataException("The requested session is not a root session.");

        var scope = new HashSet<string>(StringComparer.Ordinal) { rootSessionID };
        var pending = new Queue<(string SessionID, int Depth)>();
        pending.Enqueue((rootSessionID, 0));
        int descendantCount = 0;
        while (pending.Count > 0)
        {
            (string parentID, int depth) = pending.Dequeue();
            JsonElement children = await GetJsonAsync(BuildSessionEndpoint(parentID, "/children"), token).ConfigureAwait(false);
            if (children.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("OpenCode returned a malformed session children list.");
            if (children.GetArrayLength() > 0 && depth >= MaximumSessionScopeDepth)
                throw new InvalidDataException("The session tree exceeds the bounded depth.");

            foreach (JsonElement child in children.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("OpenCode returned a malformed child session.");
                string? childID = GetString(child, "id");
                if (!IsValidSessionID(childID))
                    throw new InvalidDataException("OpenCode returned a malformed child session ID.");
                if (!string.Equals(GetString(child, "parentID"), parentID, StringComparison.Ordinal))
                    throw new InvalidDataException("A child session did not match its direct parent.");
                string? childDirectory = GetString(child, "directory");
                if (string.IsNullOrWhiteSpace(childDirectory) || !DirectoriesEqual(childDirectory, _directory))
                    throw new InvalidDataException("OpenCode returned a child session outside the selected project directory.");
                if (!scope.Add(childID!))
                    throw new InvalidDataException("The OpenCode session tree contains a duplicate or cyclic child ID.");
                descendantCount++;
                if (descendantCount > MaximumSessionScopeDescendants)
                    throw new InvalidDataException("The session tree exceeds the bounded session count.");
                pending.Enqueue((childID!, depth + 1));
            }
        }
        return scope;
    }

    public Task<JsonElement> ReadMessagesAsync(string sessionID, CancellationToken cancellationToken = default) =>
        GetJsonAsync(BuildSessionEndpoint(sessionID, "/message"), cancellationToken);

    public Task<JsonElement> ReadStatusAsync(CancellationToken cancellationToken = default) => GetJsonAsync(BuildEndpoint("/session/status"), cancellationToken);
    public Task<JsonElement> ReadPermissionsAsync(CancellationToken cancellationToken = default) => GetJsonAsync(BuildEndpoint("/permission"), cancellationToken);
    public Task<JsonElement> ReadQuestionsAsync(CancellationToken cancellationToken = default) => GetJsonAsync(BuildEndpoint("/question"), cancellationToken);
    public Task<JsonElement> ReadProvidersAsync(CancellationToken cancellationToken = default) => GetJsonAsync(BuildEndpoint("/provider"), cancellationToken);
    public Task<JsonElement> ReadAgentsAsync(CancellationToken cancellationToken = default) => GetJsonAsync(BuildEndpoint("/agent"), cancellationToken);

    public Task SendAsync(string sessionID, string text, string messageID, string? model = null, string? agent = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionID);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageID);
        if (text.Length > 256 * 1024) throw new ArgumentException("The message exceeds the bounded request size.", nameof(text));
        object? modelValue = null;
        if (!string.IsNullOrWhiteSpace(model))
        {
            int separator = model.IndexOf('/');
            if (separator <= 0 || separator == model.Length - 1)
                throw new ArgumentException("A model must use the providerID/modelID form.", nameof(model));
            modelValue = new { providerID = model[..separator], modelID = model[(separator + 1)..] };
        }
        var body = new Dictionary<string, object?>
        {
            ["messageID"] = messageID,
            ["parts"] = new[] { new { type = "text", text } }
        };
        if (modelValue is not null) body["model"] = modelValue;
        if (!string.IsNullOrWhiteSpace(agent)) body["agent"] = agent;
        return SendNoContentAsync(HttpMethod.Post, BuildSessionEndpoint(sessionID, "/prompt_async"), body, cancellationToken);
    }

    public Task AbortAsync(string sessionID, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, BuildSessionEndpoint(sessionID, "/abort"), new { }, cancellationToken);

    public Task AnswerPermissionAsync(string requestID, string reply, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestID);
        if (reply is not ("once" or "always" or "reject")) throw new ArgumentOutOfRangeException(nameof(reply), "Permission reply must be once, always, or reject.");
        return SendNoContentAsync(HttpMethod.Post, BuildEndpoint("/permission/" + Uri.EscapeDataString(requestID) + "/reply"), new { reply }, cancellationToken);
    }

    public Task ReplyQuestionAsync(string requestID, string[][] answers, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestID);
        ArgumentNullException.ThrowIfNull(answers);
        return SendNoContentAsync(HttpMethod.Post, BuildEndpoint("/question/" + Uri.EscapeDataString(requestID) + "/reply"), new { answers }, cancellationToken);
    }

    public Task RejectQuestionAsync(string requestID, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestID);
        return SendNoContentAsync(HttpMethod.Post, BuildEndpoint("/question/" + Uri.EscapeDataString(requestID) + "/reject"), new { }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _httpClient.Dispose();
    }

    private async Task<JsonElement> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using HttpResponseMessage response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        byte[] bytes = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow });
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, Uri uri, object body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using HttpRequestMessage request = CreateJsonRequest(method, uri, body);
        using HttpResponseMessage response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        byte[] bytes = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow });
        return document.RootElement.Clone();
    }

    private async Task SendNoContentAsync(HttpMethod method, Uri uri, object body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using HttpRequestMessage request = CreateJsonRequest(method, uri, body);
        using HttpResponseMessage response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        _ = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri uri, object body)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(body);
        if (payload.Length > MaximumRequestBytes) throw new InvalidDataException("The OpenCode request exceeds the bounded payload size.");
        return new HttpRequestMessage(method, uri)
        {
            Content = new ByteArrayContent(payload) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.RequestUri is null || !IsAllowedUri(request.RequestUri))
            throw new InvalidOperationException("An OpenCode request must stay on the configured loopback server.");
        if (_authorization is not null) request.Headers.Authorization = _authorization;
        HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is not Uri finalUri || !IsAllowedUri(finalUri))
        {
            response.Dispose();
            throw new InvalidOperationException("OpenCode redirected a loopback request to another address.");
        }
        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        _ = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"OpenCode returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > MaximumResponseBytes)
            throw new InvalidDataException("OpenCode returned a response larger than the allowed limit.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("OpenCode returned a response larger than the allowed limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private Uri BuildEndpoint(string path)
    {
        string prefix = _endpoint.GetLeftPart(UriPartial.Authority) + _endpoint.AbsolutePath.TrimEnd('/');
        return new Uri(prefix + path + "?directory=" + Uri.EscapeDataString(_directory), UriKind.Absolute);
    }

    private Uri BuildSessionEndpoint(string sessionID, string suffix = "") =>
        BuildEndpoint("/session/" + Uri.EscapeDataString(sessionID) + suffix);

    private bool IsAllowedUri(Uri uri) => uri.IsAbsoluteUri && uri.IsLoopback && uri.Scheme == _endpoint.Scheme &&
        string.Equals(uri.Host, _endpoint.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == _endpoint.Port;

    private static string? GetString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static bool HasNonNullProperty(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement item) && item.ValueKind != JsonValueKind.Null;

    private static bool IsValidSessionID(string? sessionID) => sessionID is not null && sessionID.Length is >= 4 and <= 128 &&
        sessionID.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool DirectoriesEqual(string first, string second)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }
}
