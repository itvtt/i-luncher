using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILauncher.Models;

namespace ILauncher.Services;

public sealed class NoticeService : INoticeService
{
    public static readonly Uri DefaultFeedUri = new("https://itvtt.github.io/i-luncher-notices/notices.json");
    private const int MaxFeedBytes = 256 * 1024;
    private const int MaxReadEntries = 1000;
    private static readonly Regex IdPattern = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z", RegexOptions.CultureInvariant);
    private static readonly Regex DatePattern = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?Z\z", RegexOptions.CultureInvariant);
    private readonly Uri _feedUri;
    private readonly string _stateDirectory;
    private readonly HttpClient _client;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, int> _readRevisions = new(StringComparer.Ordinal);
    private IReadOnlyList<Notice> _notices = Array.Empty<Notice>();
    private string? _etag;
    private bool _hasCache;

    public NoticeService(Uri feedUri, string stateDirectory, HttpClient? client = null)
    {
        ValidateUri(feedUri);
        _feedUri = feedUri;
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        LoadCache();
        LoadReadState();
    }

    public IReadOnlyList<Notice> CachedNotices
    {
        get
        {
            lock (_stateGate)
                return Array.AsReadOnly(_notices.Where(n => n.PublishedAt <= DateTimeOffset.UtcNow)
                    .OrderByDescending(n => n.PublishedAt).ThenBy(n => n.Id, StringComparer.Ordinal).ToArray());
        }
    }

    public async Task<IReadOnlyList<Notice>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var uri = _feedUri;
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                lock (_stateGate)
                {
                    if (_hasCache && _etag is not null)
                        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(_etag));
                }
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } finalUri)
                    ValidateUri(finalUri);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects >= 5 || response.Headers.Location is not { } location)
                        throw new HttpRequestException("The notice feed redirected too many times or without a location.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    ValidateUri(uri);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    lock (_stateGate)
                        if (!_hasCache) throw new HttpRequestException("The notice feed returned 304 without a cached feed.");
                    return CachedNotices;
                }
                response.EnsureSuccessStatusCode();
                var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                var notices = ParseFeed(bytes);
                timeout.Token.ThrowIfCancellationRequested();
                var etag = response.Headers.ETag?.ToString();
                if (etag?.Length > 1024) etag = null;
                lock (_stateGate)
                {
                    _notices = notices;
                    _etag = etag;
                    _hasCache = true;
                    TryPersist(() => SaveCache(bytes, etag));
                }
                return CachedNotices;
            }
        }
        finally { _refreshGate.Release(); }
    }

    public bool IsRead(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        lock (_stateGate)
            return _readRevisions.TryGetValue(notice.Id, out var revision) && revision >= notice.Revision;
    }

    public void MarkRead(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (!IdPattern.IsMatch(notice.Id) || notice.Revision < 1)
            throw new ArgumentException("The notice ID or revision is invalid.", nameof(notice));
        lock (_stateGate)
        {
            var revision = _readRevisions.TryGetValue(notice.Id, out var previous) ? Math.Max(previous, notice.Revision) : notice.Revision;
            _readRevisions.Remove(notice.Id);
            _readRevisions.Add(notice.Id, revision);
            while (_readRevisions.Count > MaxReadEntries) _readRevisions.Remove(_readRevisions.First().Key);
            TryPersist(SaveReadState);
        }
    }

    private static void ValidateUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.AbsoluteUri.Length > 2048)
            throw new ArgumentException("Notice feeds and redirects must use absolute HTTPS URLs without credentials.", nameof(uri));
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxFeedBytes) throw new InvalidDataException("The notice feed exceeds 256 KiB.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaxFeedBytes) throw new InvalidDataException("The notice feed exceeds 256 KiB.");
            output.Write(buffer, 0, count);
        }
    }

    private static IReadOnlyList<Notice> ParseFeed(byte[] bytes)
    {
        if (bytes.Length > MaxFeedBytes) throw new InvalidDataException("The notice feed exceeds 256 KiB.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        RequireFields(root, "schemaVersion", "notices");
        if (!root.GetProperty("schemaVersion").TryGetInt32(out var schema) || schema != 1)
            throw new InvalidDataException("Unsupported notice feed schema.");
        var items = root.GetProperty("notices");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100)
            throw new InvalidDataException("The notice feed must contain at most 100 notices.");
        var notices = new List<Notice>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            RequireFields(item, "id", "revision", "title", "body", "publishedAt", "important");
            var id = GetText(item, "id");
            var title = GetText(item, "title");
            var body = GetText(item, "body");
            var dateText = GetText(item, "publishedAt");
            if (!IdPattern.IsMatch(id) || !ids.Add(id)) throw new InvalidDataException("Notice IDs must be valid and unique.");
            if (!item.GetProperty("revision").TryGetInt32(out var revision) || revision < 1)
                throw new InvalidDataException("Notice revisions must be positive integers.");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)
                || title.EnumerateRunes().Count() > 120 || body.EnumerateRunes().Count() > 12000)
                throw new InvalidDataException("The notice title or body exceeds its limit or is empty.");
            if (!DatePattern.IsMatch(dateText) || !DateTimeOffset.TryParseExact(dateText,
                new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" }, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var publishedAt))
                throw new InvalidDataException("Notice publication dates must be valid UTC timestamps ending in Z.");
            var important = item.GetProperty("important");
            if (important.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Notice importance must be a boolean.");
            notices.Add(new Notice(id, revision, title, body, publishedAt, important.GetBoolean()));
        }
        return notices.AsReadOnly();
    }

    private static void RequireFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate JSON field.");
        if (seen.Count != fields.Length) throw new InvalidDataException("Required JSON fields are missing.");
    }

    private static string GetText(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"'{name}' must be a string.");
        var text = value.GetString()!;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) throw new InvalidDataException("Invalid Unicode surrogate.");
            }
            else if (char.IsLowSurrogate(text[index])) throw new InvalidDataException("Invalid Unicode surrogate.");
        }
        return text;
    }

    private void LoadCache()
    {
        try
        {
            var bytes = ReadLocal("notices-cache.json", MaxFeedBytes + 16 * 1024);
            if (bytes is null) return;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 10 });
            var root = document.RootElement;
            RequireFields(root, "feedUri", "etag", "feed");
            if (GetText(root, "feedUri") != _feedUri.AbsoluteUri) return;
            var etag = root.GetProperty("etag");
            if (etag.ValueKind != JsonValueKind.Null)
            {
                var value = GetText(root, "etag");
                if (value.Length > 1024 || !EntityTagHeaderValue.TryParse(value, out _)) throw new InvalidDataException("Invalid cached ETag.");
                _etag = value;
            }
            _notices = ParseFeed(Encoding.UTF8.GetBytes(root.GetProperty("feed").GetRawText()));
            _hasCache = true;
        }
        catch (Exception ex) when (IsLocalFailure(ex)) { _notices = Array.Empty<Notice>(); _etag = null; _hasCache = false; }
    }

    private void LoadReadState()
    {
        try
        {
            var bytes = ReadLocal("notices-read.json", MaxFeedBytes);
            if (bytes is null) return;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            RequireFields(root, "schemaVersion", "feedUri", "readRevisions");
            if (!root.GetProperty("schemaVersion").TryGetInt32(out var schema) || schema != 1
                || GetText(root, "feedUri") != _feedUri.AbsoluteUri) return;
            var items = root.GetProperty("readRevisions");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaxReadEntries) throw new InvalidDataException("Invalid read state.");
            foreach (var item in items.EnumerateArray())
            {
                RequireFields(item, "id", "revision");
                var id = GetText(item, "id");
                if (!IdPattern.IsMatch(id) || !item.GetProperty("revision").TryGetInt32(out var revision)
                    || revision < 1 || !_readRevisions.TryAdd(id, revision)) throw new InvalidDataException("Invalid read revision.");
            }
        }
        catch (Exception ex) when (IsLocalFailure(ex)) { _readRevisions.Clear(); }
    }

    private byte[]? ReadLocal(string name, int limit)
    {
        var path = Path.Combine(_stateDirectory, name);
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new InvalidDataException("Notice state exceeds its limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private void SaveCache(byte[] bytes, string? etag)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("feedUri", _feedUri.AbsoluteUri);
            writer.WriteString("etag", etag);
            writer.WritePropertyName("feed");
            writer.WriteRawValue(bytes);
            writer.WriteEndObject();
        }
        SaveAtomic("notices-cache.json", output.ToArray());
    }

    private void SaveReadState() => SaveAtomic("notices-read.json", JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, feedUri = _feedUri.AbsoluteUri,
        readRevisions = _readRevisions.Select(pair => new { id = pair.Key, revision = pair.Value }).ToArray()
    }));

    private void SaveAtomic(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_stateDirectory);
        var destination = Path.Combine(_stateDirectory, name);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { TryPersist(() => File.Delete(temporary)); }
    }

    private static bool IsLocalFailure(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or JsonException or InvalidOperationException or ArgumentException;
    private static void TryPersist(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
