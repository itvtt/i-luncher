using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

await DirectoryQueryAndPromptShapeAsync();
await AutomationStartupUsesNativeSessionAndExactReceiptAsync();
await ExactRootDirectoryValidationAsync();
await RedirectAndPostFailureAreNotRetriedAsync();
await SessionScopeVerifiesTreeAndFailsClosedAsync();
ConfigEnvironmentIsPreservedAndAutoPermissionMerges();
Console.WriteLine("Headless conversation client tests passed.");

static async Task DirectoryQueryAndPromptShapeAsync()
{
    string directory = Path.Combine(Path.GetTempPath(), "Headless Project & One");
    var requests = new List<(HttpMethod Method, Uri Uri, string Body)>();
    using var client = new HttpClient(new DelegateHandler(async (request, token) =>
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
        requests.Add((request.Method, request.RequestUri!, body));
        string json = request.RequestUri!.AbsolutePath.EndsWith("/session/abc/prompt_async", StringComparison.Ordinal)
            ? "" : "[]";
        return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }));
    using var conversation = new OpenCodeConversationClient(new Uri("http://127.0.0.1:43123/"), directory, client);

    await conversation.ListSessionsAsync();
    await conversation.SendAsync("ses_1", "hello", "msg_1", "provider/model", "build");
    await conversation.AnswerPermissionAsync("per_1", "once");
    await conversation.ReplyQuestionAsync("que_1", [["A"], ["B", "C"]]);
    Assert(requests.Count == 4, "expected exactly one list and one each of the prompt/permission/question requests");
    foreach (var request in requests)
        Assert(request.Uri.Query.Contains("directory=" + Uri.EscapeDataString(Path.GetFullPath(directory)), StringComparison.Ordinal), "every API route must include an encoded directory query");
    using JsonDocument payload = JsonDocument.Parse(requests[1].Body);
    JsonElement root = payload.RootElement;
    Assert(root.GetProperty("messageID").GetString() == "msg_1", "message ID was not preserved");
    Assert(root.GetProperty("parts")[0].GetProperty("text").GetString() == "hello", "text part shape is wrong");
    Assert(root.GetProperty("model").GetProperty("providerID").GetString() == "provider", "provider ID was not split");
    Assert(requests[2].Uri.AbsolutePath.EndsWith("/permission/per_1/reply", StringComparison.Ordinal), "permission route is wrong");
    using JsonDocument permission = JsonDocument.Parse(requests[2].Body);
    Assert(permission.RootElement.GetProperty("reply").GetString() == "once", "permission reply payload is wrong");
    Assert(requests[3].Uri.AbsolutePath.EndsWith("/question/que_1/reply", StringComparison.Ordinal), "question route is wrong");
}

static async Task AutomationStartupUsesNativeSessionAndExactReceiptAsync()
{
    string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Headless Automation Project"));
    const string sessionID = "ses_headless123";
    const string messageID = "msg_stable123";
    const string prompt = "continue the approved task";
    bool wrongReceipt = false;
    var requests = new List<(HttpMethod Method, string Path, string Body)>();
    using var transport = new HttpClient(new DelegateHandler(async (request, token) =>
    {
        string path = request.RequestUri!.AbsolutePath;
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
        requests.Add((request.Method, path, body));
        if (path == "/session/status") return JsonResponse("{}");
        if (path == "/permission" || path == "/question") return JsonResponse("[]");
        if (path == "/session" && request.Method == HttpMethod.Post)
            return JsonResponse(SessionJson(sessionID, directory, "Approved task"));
        if (path == "/session/" + sessionID && request.Method == HttpMethod.Get)
            return JsonResponse(SessionJson(sessionID, directory, "Approved task"));
        if (path == "/session/" + sessionID + "/prompt_async" && request.Method == HttpMethod.Post)
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (path == "/session/" + sessionID + "/message/" + messageID && request.Method == HttpMethod.Get)
            return JsonResponse(MessageJson(messageID, sessionID, wrongReceipt ? "different prompt" : prompt));
        if (path.StartsWith("/tui/", StringComparison.Ordinal))
            throw new InvalidOperationException("Headless startup must not call TUI endpoints.");
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
    }));
    using var client = new OpenCodeAutomationClient(new Uri("http://127.0.0.1:43129/"), directory,
        transport, useTuiSelection: false);

    string createdID = await client.CreateStartupSessionAsync("Approved task");
    Assert(createdID == sessionID, "startup must retain the created session ID");
    await client.SelectStartupSessionAsync(sessionID);
    var dispatch = new OpenCodeAutomationDispatch(sessionID, messageID, prompt, DateTimeOffset.UtcNow);
    await client.SubmitStartupPromptAsync(dispatch, () => { });
    Assert(await client.HasStartupMessageAsync(dispatch), "the exact startup message receipt was not accepted");

    var promptPosts = requests.Where(request => request.Method == HttpMethod.Post && request.Path == "/session/" + sessionID + "/prompt_async").ToArray();
    Assert(promptPosts.Length == 1, "startup must post one prompt with the retained dispatch");
    using (JsonDocument sent = JsonDocument.Parse(promptPosts[0].Body))
        Assert(sent.RootElement.GetProperty("messageID").GetString() == messageID, "startup must preserve its stable message ID");
    Assert(requests.All(request => !request.Path.StartsWith("/tui/", StringComparison.Ordinal)), "headless startup must never call a TUI route");

    wrongReceipt = true;
    await ThrowsAsync<OpenCodeAutomationException>(() => client.HasStartupMessageAsync(dispatch));
}

static string SessionJson(string sessionID, string directory, string title) => JsonSerializer.Serialize(new { id = sessionID, directory, title });
static string MessageJson(string messageID, string sessionID, string text) => JsonSerializer.Serialize(new
{
    info = new { id = messageID, sessionID, role = "user" },
    parts = new[] { new { type = "text", text } }
});

static async Task ExactRootDirectoryValidationAsync()
{
    string directory = Path.Combine(Path.GetTempPath(), "Headless Root");
    using var client = new HttpClient(new DelegateHandler((request, _) => Task.FromResult(JsonResponse(
        "{\"id\":\"ses_other\",\"directory\":" + JsonSerializer.Serialize(directory) + "}"))));
    using var conversation = new OpenCodeConversationClient(new Uri("http://127.0.0.1:43124/"), directory, client);
    await ThrowsAsync<InvalidDataException>(() => conversation.GetSessionAsync("ses_expected"));

    using var outsideClient = new HttpClient(new DelegateHandler((request, _) => Task.FromResult(JsonResponse(
        "{\"id\":\"ses_expected\",\"directory\":\"C:\\\\outside\"}"))));
    using var outsideConversation = new OpenCodeConversationClient(new Uri("http://127.0.0.1:43124/"), directory, outsideClient);
    await ThrowsAsync<InvalidDataException>(() => outsideConversation.GetSessionAsync("ses_expected"));
}

static async Task RedirectAndPostFailureAreNotRetriedAsync()
{
    int calls = 0;
    using var redirectClient = new HttpClient(new DelegateHandler((request, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://example.com/redirected");
        return Task.FromResult(response);
    }));
    using var redirected = new OpenCodeConversationClient(new Uri("http://127.0.0.1:43125/"), Path.GetTempPath(), redirectClient);
    await ThrowsAsync<InvalidOperationException>(() => redirected.ListSessionsAsync());

    using var failingClient = new HttpClient(new DelegateHandler((request, _) =>
    {
        Interlocked.Increment(ref calls);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("failure") });
    }));
    using var failing = new OpenCodeConversationClient(new Uri("http://127.0.0.1:43126/"), Path.GetTempPath(), failingClient);
    await ThrowsAsync<HttpRequestException>(() => failing.SendAsync("ses_1", "hello", "msg_once"));
    Assert(calls == 1, "ambiguous/rejected prompt POST must not be retried");
}

static async Task SessionScopeVerifiesTreeAndFailsClosedAsync()
{
    string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Headless Scope Project"));
    string root = "ses_root";
    string child = "ses_child";
    string grandchild = "ses_grandchild";
    var validHandlers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/session/" + root + "/children"] = JsonSerializer.Serialize(new[] { new { id = child, parentID = root, directory } }),
        ["/session/" + child + "/children"] = JsonSerializer.Serialize(new[] { new { id = grandchild, parentID = child, directory } }),
        ["/session/" + grandchild + "/children"] = "[]"
    };
    using (var transport = CreateScopeTransport(root, directory, validHandlers))
    using (var conversation = new OpenCodeConversationClient(new Uri("http://127.0.0.1:43127/"), directory, transport))
    {
        IReadOnlySet<string> scope = await conversation.ReadSessionScopeAsync(root);
        Assert(scope.SetEquals([root, child, grandchild]), "valid two-level scope did not include exactly the verified sessions");
    }

    string otherDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Other Scope Project"));
    await ScopeFailsAsync(root, directory, new Dictionary<string, string>
    {
        ["/session/" + root + "/children"] = JsonSerializer.Serialize(new[] { new { id = child, parentID = root, directory = otherDirectory } })
    });
    await ScopeFailsAsync(root, directory, new Dictionary<string, string>
    {
        ["/session/" + root + "/children"] = JsonSerializer.Serialize(new[] { new { id = child, parentID = "ses_wrong", directory } })
    });
    await ScopeFailsAsync(root, directory, new Dictionary<string, string>
    {
        ["/session/" + root + "/children"] = JsonSerializer.Serialize(new[]
        {
            new { id = child, parentID = root, directory },
            new { id = child, parentID = root, directory }
        })
    });
}

static HttpClient CreateScopeTransport(string rootID, string directory, IReadOnlyDictionary<string, string> children)
{
    return new HttpClient(new DelegateHandler((request, _) =>
    {
        string path = request.RequestUri!.AbsolutePath;
        string json = path == "/session/" + rootID
            ? JsonSerializer.Serialize(new { id = rootID, directory })
            : children.TryGetValue(path, out string? payload) ? payload : "[]";
        return Task.FromResult(JsonResponse(json));
    }));
}

static async Task ScopeFailsAsync(string rootID, string directory, IReadOnlyDictionary<string, string> children)
{
    using var transport = CreateScopeTransport(rootID, directory, children);
    using var conversation = new OpenCodeConversationClient(new Uri("http://127.0.0.1:43128/"), directory, transport);
    await ThrowsAsync<InvalidDataException>(() => conversation.ReadSessionScopeAsync(rootID));
}

static void ConfigEnvironmentIsPreservedAndAutoPermissionMerges()
{
    Type hostType = typeof(OpenCodeHeadlessHost);
    MethodInfo startInfoFactory = hostType.GetMethod("CreateStartInfo", BindingFlags.NonPublic | BindingFlags.Static)!;
    var startInfo = (System.Diagnostics.ProcessStartInfo)startInfoFactory.Invoke(null,
        ["C:\\OpenCode\\opencode.exe", Path.GetTempPath(), 43127, null])!;
    string? inheritedFile = Environment.GetEnvironmentVariable("OPENCODE_CONFIG");
    string? inheritedInline = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT");
    Assert((startInfo.Environment.TryGetValue("OPENCODE_CONFIG", out string? actualFile) ? actualFile : null) == inheritedFile, "the inherited OPENCODE_CONFIG file setting must be preserved");
    Assert((startInfo.Environment.TryGetValue("OPENCODE_CONFIG_CONTENT", out string? actualInline) ? actualInline : null) == inheritedInline, "default launch must preserve inherited inline config");

    MethodInfo merger = hostType.GetMethod("MergeAutoPermission", BindingFlags.NonPublic | BindingFlags.Static)!;
    string merged = (string)merger.Invoke(null, ["{\"model\":\"provider/model\",\"permission\":{\"read\":\"ask\"}}"] )!;
    using JsonDocument config = JsonDocument.Parse(merged);
    Assert(config.RootElement.GetProperty("model").GetString() == "provider/model", "auto mode must retain supplied configuration");
    Assert(config.RootElement.GetProperty("permission").GetString() == "allow", "explicit auto mode must set allow permissions");
}

static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
}

sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await send(request, cancellationToken);
        response.RequestMessage ??= request;
        return response;
    }
}
