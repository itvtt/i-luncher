using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ILauncher.Models;
using ILauncher.Services;

internal static class NoticeChecks
{
    private static readonly Uri FeedUri = new("https://example.test/notices.json");

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ILauncher-NoticeChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ValidateAndPreserveAsync(Path.Combine(root, "validation"));
            await CacheAndRevisionAsync(Path.Combine(root, "cache"));
            await TransportAsync(Path.Combine(root, "transport"));
            await ConcurrentAsync(Path.Combine(root, "concurrent"));
            await PersistenceFailureAsync(Path.Combine(root, "file-instead-of-directory"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Feed(params object[] notices) => JsonSerializer.Serialize(new { schemaVersion = 1, notices });
    private static object Item(string id = "notice-1", int revision = 1, string title = "공지", string body = "본문",
        string publishedAt = "2026-01-01T00:00:00Z", bool important = false) => new { id, revision, title, body, publishedAt, important };
    private static HttpResponseMessage Ok(string json, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or ArgumentException or HttpRequestException)
        { return; }
        throw new Exception(message);
    }

    private static async Task ValidateAndPreserveAsync(string directory)
    {
        var payload = Feed(Item());
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Ok(payload))));
        var service = new NoticeService(FeedUri, directory, client);
        await service.RefreshAsync();
        var invalid = new[]
        {
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"notices\":[]}",
            "{\"schemaVersion\":1,\"notices\":[],\"unknown\":true}",
            "{\"schemaVersion\":2,\"notices\":[]}",
            "{\"schemaVersion\":1}",
            Feed(Item("has space")), Feed(Item("_leading")), Feed(Item("a" + new string('a', 80))),
            Feed(Item(revision: 0)), Feed(Item(title: " \t\n")), Feed(Item(body: "")), Feed(Item(body: " \t\n")), Feed(Item(title: new string('a', 121))),
            Feed(Item(body: new string('a', 12001))), Feed(Item(publishedAt: "2026-01-01T00:00:00+00:00")),
            Feed(Item(publishedAt: "2026-02-30T00:00:00Z")), Feed(Item(publishedAt: "2026-01-01T00:00:00.12345678Z")),
            Feed(Item(), Item()), Feed(Enumerable.Range(0, 101).Select(i => Item("id" + i)).ToArray()),
            Feed(Item()).Replace("\"revision\":1", "\"revision\":1,\"revision\":2", StringComparison.Ordinal),
            Feed(Item()).Replace("\"important\":false", "\"important\":\"false\"", StringComparison.Ordinal),
            Feed(Item()).Replace("\"revision\":1", "\"revision\":2147483648", StringComparison.Ordinal),
            "{\"schemaVersion\":1,\"notices\":[{\"id\":\"a\",\"revision\":1,\"title\":\"\\ud800\",\"body\":\"\",\"publishedAt\":\"2026-01-01T00:00:00Z\",\"important\":false}]}"
        };
        foreach (var candidate in invalid)
        {
            payload = candidate;
            await RejectAsync(async () => { await service.RefreshAsync(); }, "Invalid feed was accepted: " + candidate[..Math.Min(candidate.Length, 100)]);
            Assert(service.CachedNotices.Count == 1 && service.CachedNotices[0].Id == "notice-1", "Invalid feed replaced the last valid feed.");
        }
        payload = Feed(Item("older", publishedAt: "2025-01-01T00:00:00Z"), Item("future", publishedAt: "9999-01-01T00:00:00Z"),
            Item("newer", title: string.Concat(Enumerable.Repeat("😀", 120)), body: string.Concat(Enumerable.Repeat("😀", 12000)), publishedAt: "2026-01-01T00:00:00.1234567Z"));
        var visible = await service.RefreshAsync();
        Assert(visible.Select(n => n.Id).SequenceEqual(new[] { "newer", "older" }), "Future filtering, ordering, or Unicode scalar limits failed.");
        // All-English JSON fits even when the body is exactly at the scalar limit.
        payload = Feed(Item(title: new string('a', 120), body: new string('a', 12000), revision: int.MaxValue));
        Assert((await service.RefreshAsync()).Single().Revision == int.MaxValue, "Boundary values were rejected.");
    }

    private static async Task CacheAndRevisionAsync(string directory)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            if (calls == 1) return Task.FromResult(Ok(Feed(Item()), "\"v1\""));
            Assert(request.Headers.IfNoneMatch.Single().ToString() == "\"v1\"", "ETag not sent.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }));
        var service = new NoticeService(FeedUri, directory, client);
        Assert(calls == 0, "Constructor performed network IO.");
        var notice = (await service.RefreshAsync()).Single();
        Assert(!service.IsRead(notice), "New notice starts read.");
        service.MarkRead(notice);
        Assert(service.IsRead(notice) && !service.IsRead(notice with { Revision = 2 }), "Revision changes did not become unread.");
        service.MarkRead(notice with { Revision = 3 });
        service.MarkRead(notice);
        Assert(service.IsRead(notice with { Revision = 2 }), "Revision rollback lowered read state.");
        Assert((await service.RefreshAsync()).Single() == notice, "304 discarded cache.");
        using var offline = new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline")));
        var restarted = new NoticeService(FeedUri, directory, offline);
        Assert(restarted.CachedNotices.Single() == notice && restarted.IsRead(notice with { Revision = 3 }), "Cache/read state failed across restart.");
        await RejectAsync(async () => { await restarted.RefreshAsync(); }, "Offline refresh unexpectedly succeeded.");
        Assert(restarted.CachedNotices.Single() == notice, "Offline refresh discarded cache.");
        using var invalid = new HttpClient(new Handler((_, _) => Task.FromResult(Ok("{}"))));
        await RejectAsync(async () => { await new NoticeService(FeedUri, directory, invalid).RefreshAsync(); }, "Invalid feed accepted.");
        Assert(new NoticeService(FeedUri, directory, offline).CachedNotices.Single() == notice, "Invalid remote overwrote disk cache.");
        Assert(new NoticeService(new Uri("https://other.test/notices.json"), directory, offline).CachedNotices.Count == 0, "Cache leaked across feeds.");
        File.WriteAllText(Path.Combine(directory, "notices-cache.json"), new string('x', 280000));
        File.WriteAllText(Path.Combine(directory, "notices-read.json"), "{broken}");
        var corrupted = new NoticeService(FeedUri, directory, offline);
        Assert(corrupted.CachedNotices.Count == 0 && !corrupted.IsRead(notice), "Invalid local state was trusted.");
        File.WriteAllText(Path.Combine(directory, "notices-cache.json"), JsonSerializer.Serialize(new
        {
            feedUri = FeedUri.AbsoluteUri, etag = (string?)null, feed = new { schemaVersion = 1, notices = "wrong-type" }
        }));
        File.WriteAllText(Path.Combine(directory, "notices-read.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, feedUri = FeedUri.AbsoluteUri, readRevisions = new[] { new { id = "notice-1", revision = 0 } }
        }));
        corrupted = new NoticeService(FeedUri, directory, offline);
        Assert(corrupted.CachedNotices.Count == 0 && !corrupted.IsRead(notice), "Schema-invalid local JSON prevented safe startup.");
        File.WriteAllText(Path.Combine(directory, "notices-read.json"), new string('x', 256 * 1024 + 1));
        Assert(!new NoticeService(FeedUri, directory, offline).IsRead(notice), "Oversized read state prevented safe startup.");
    }

    private static async Task TransportAsync(string directory)
    {
        var insecureRejected = false;
        try { _ = new NoticeService(new Uri("http://example.test/feed"), directory); }
        catch (ArgumentException) { insecureRejected = true; }
        Assert(insecureRejected, "HTTP feed allowed.");
        using var noCache304 = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified))));
        await RejectAsync(async () => { await new NoticeService(FeedUri, directory, noCache304).RefreshAsync(); }, "304 without cache accepted.");
        var redirectCalls = 0;
        using var redirect = new HttpClient(new Handler((_, _) =>
        {
            redirectCalls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://example.test/insecure");
            return Task.FromResult(response);
        }));
        await RejectAsync(async () => { await new NoticeService(FeedUri, directory, redirect).RefreshAsync(); }, "Insecure redirect accepted.");
        Assert(redirectCalls == 1, "Insecure redirect was requested.");
        using var oversized = new HttpClient(new Handler((_, _) =>
        {
            var content = new UnboundedContent(new byte[256 * 1024 + 1]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await RejectAsync(async () => { await new NoticeService(FeedUri, directory, oversized).RefreshAsync(); }, "Unbounded chunked response accepted.");
        using var misleadingLength = new HttpClient(new Handler((_, _) =>
        {
            var content = new UnboundedContent(new byte[256 * 1024 + 1]);
            content.Headers.ContentLength = 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await RejectAsync(async () => { await new NoticeService(FeedUri, directory, misleadingLength).RefreshAsync(); }, "Misleading Content-Length bypassed streamed limit.");
        using var cancellation = new CancellationTokenSource();
        using var delayed = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Ok(Feed()); }));
        var pending = new NoticeService(FeedUri, directory, delayed).RefreshAsync(cancellation.Token);
        cancellation.Cancel();
        try { await pending; throw new Exception("Refresh ignored cancellation."); }
        catch (OperationCanceledException) { }
    }

    private static async Task ConcurrentAsync(string directory)
    {
        var calls = 0;
        var active = 0;
        var maximum = 0;
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            var revision = Interlocked.Increment(ref calls);
            await Task.Delay(20, token);
            Interlocked.Decrement(ref active);
            return Ok(Feed(Item(revision: revision)));
        }));
        var service = new NoticeService(FeedUri, directory, client);
        var fetches = Enumerable.Range(0, 4).Select(_ => service.RefreshAsync()).ToArray();
        await Task.WhenAll(fetches);
        Assert(maximum == 1 && service.CachedNotices.Single().Revision == 4, "Refreshes overlapped or stale result won.");
        await Task.WhenAll(Enumerable.Range(1, 20).Select(revision => Task.Run(() => service.MarkRead(service.CachedNotices.Single() with { Revision = revision }))));
        var restarted = new NoticeService(FeedUri, directory, client);
        Assert(restarted.CachedNotices.Single().Revision == 4 && restarted.IsRead(service.CachedNotices.Single() with { Revision = 20 }), "Concurrent persistence lost state.");
        Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Atomic writes left temporary files.");
    }

    private static async Task PersistenceFailureAsync(string path)
    {
        File.WriteAllText(path, "file");
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Ok(Feed(Item())))));
        var service = new NoticeService(FeedUri, path, client);
        var notice = (await service.RefreshAsync()).Single();
        service.MarkRead(notice);
        Assert(service.CachedNotices.Single() == notice && service.IsRead(notice), "Persistence failure prevented fetched display/read state.");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class UnboundedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }
}
