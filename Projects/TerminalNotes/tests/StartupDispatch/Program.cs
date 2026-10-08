using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

internal static class Program
{
    private const string SessionId = "ses_startup123";
    private const string MessageId = "msg_startup123";
    private const string Prompt = "hi";
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "TerminalNotes StartupDispatch Workspace");

    private static async Task<int> Main()
    {
        try
        {
            await CreateAndSelectStartupSessionAsync();
            await StartupMessageReceiptAsync();
            await StartupMessageIdentityRejectionAsync();
            await BusyAndPermissionBlockSubmissionAsync();
            await AmbiguousPromptPostReconcilesAsync();
            await CancellationAtSubmitBoundaryAsync();
            await PromptPayloadHasNoModelOverrideAsync();
            Console.WriteLine("PASS: startup session, dispatch, and exact receipt checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static async Task CreateAndSelectStartupSessionAsync()
    {
        var handler = new FakeHandler((request, _) => JsonResponse(request.Method, request.RequestUri!,
            methodPath => methodPath switch
            {
                ("GET", "/session/status") => Ok("{}"),
                ("POST", "/session") => Ok(SessionJson(SessionId, "Startup task")),
                ("GET", "/session/" + SessionId) => Ok(SessionJson(SessionId, "Startup task")),
                ("POST", "/tui/select-session") => Ok("true"),
                _ => NotFound()
            }));
        using var client = NewClient(handler);

        string created = await client.CreateStartupSessionAsync("Startup task");
        Require(created == SessionId, "The startup session ID did not match the native response.");
        await client.SelectStartupSessionAsync(created);
        Require(handler.Requests.Count(request => request.Method == HttpMethod.Post && request.Path == "/tui/select-session") == 1,
            "The exact startup session was not selected once.");
        using JsonDocument selectBody = JsonDocument.Parse(handler.Requests.Single(request => request.Path == "/tui/select-session").Body!);
        Require(selectBody.RootElement.GetProperty("sessionID").GetString() == SessionId,
            "The TUI selection did not name the exact startup session.");
    }

    private static async Task StartupMessageReceiptAsync()
    {
        var missingHandler = MessageHandler(HttpStatusCode.NotFound, null);
        using (var client = NewClient(missingHandler))
            Require(!await client.HasStartupMessageAsync(Dispatch()), "A native 404 must mean the exact message is not present.");

        var exactHandler = MessageHandler(HttpStatusCode.OK, MessageJson(MessageId, SessionId, "user", Prompt));
        using (var client = NewClient(exactHandler))
            Require(await client.HasStartupMessageAsync(Dispatch()), "The exact native startup message was not accepted as present.");
    }

    private static async Task StartupMessageIdentityRejectionAsync()
    {
        foreach (var mismatch in new[]
        {
            (Id: "msg_other123", Session: SessionId, Role: "user", Text: Prompt),
            (Id: MessageId, Session: "ses_other123", Role: "user", Text: Prompt),
            (Id: MessageId, Session: SessionId, Role: "user", Text: "different")
        })
        {
            using var client = NewClient(MessageHandler(HttpStatusCode.OK,
                MessageJson(mismatch.Id, mismatch.Session, mismatch.Role, mismatch.Text)));
            OpenCodeAutomationException exception = await ThrowsAsync<OpenCodeAutomationException>(
                () => client.HasStartupMessageAsync(Dispatch()));
            Require(exception.Code is "startup_message_identity_mismatch" or "startup_message_mismatch",
                "A mismatched startup receipt returned an unrelated failure.");
        }
    }

    private static async Task BusyAndPermissionBlockSubmissionAsync()
    {
        foreach (var scenario in new[] { "busy", "permission" })
        {
            int handlerPromptPosts = 0;
            var handler = new FakeHandler((request, _) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path == "/session/" + SessionId)
                    return Task.FromResult(Ok(SessionJson(SessionId, "Startup task")));
                if (request.Method == HttpMethod.Get && path == "/session/status")
                    return Task.FromResult(Ok(scenario == "busy" ? StatusJson("busy") : "{}"));
                if (request.Method == HttpMethod.Get && path == "/permission")
                    return Task.FromResult(Ok(scenario == "permission" ? PendingJson(SessionId) : "[]"));
                if (request.Method == HttpMethod.Get && path == "/question") return Task.FromResult(Ok("[]"));
                if (request.Method == HttpMethod.Post && path == "/session/" + SessionId + "/prompt_async")
                { handlerPromptPosts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
                return Task.FromResult(NotFound());
            });
            using var client = NewClient(handler);
            bool callbackRan = false;
            OpenCodeAutomationException exception = await ThrowsAsync<OpenCodeAutomationException>(() =>
                client.SubmitStartupPromptAsync(Dispatch(), () => callbackRan = true));
            Require(exception.Code == (scenario == "busy" ? "session_not_idle" : "session_input_pending"),
                $"The {scenario} startup session returned the wrong submission block.");
            Require(!callbackRan && handlerPromptPosts == 0,
                $"The {scenario} startup session reached the final boundary or sent a prompt.");
        }
    }

    private static async Task AmbiguousPromptPostReconcilesAsync()
    {
        int postCount = 0;
        var handler = ReadyHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session/" + SessionId + "/prompt_async")
            {
                postCount++;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/session/" + SessionId + "/message/" + MessageId)
                return Ok(MessageJson(MessageId, SessionId, "user", Prompt));
            return NotFound();
        });
        using var client = NewClient(handler);

        OpenCodeAutomationException exception = await ThrowsAsync<OpenCodeAutomationException>(
            () => client.SubmitStartupPromptAsync(Dispatch(), () => { }));
        Require(exception.Code == "prompt_response_ambiguous", "The lost prompt POST response was not kept ambiguous.");
        Require(await client.HasStartupMessageAsync(Dispatch()), "The exact GET did not reconcile the accepted message.");
        Require(postCount == 1, "The ambiguous prompt POST was retried.");
    }

    private static async Task CancellationAtSubmitBoundaryAsync()
    {
        int promptPosts = 0;
        var handler = ReadyHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session/" + SessionId + "/prompt_async")
            { promptPosts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            return Task.FromResult(NotFound());
        });
        using var client = NewClient(handler);
        using var cancellation = new CancellationTokenSource();
        await ThrowsAsync<OperationCanceledException>(() => client.SubmitStartupPromptAsync(
            Dispatch(), () => cancellation.Cancel(), cancellation.Token));
        Require(promptPosts == 0, "Cancellation at the final submit boundary still sent the prompt.");
    }

    private static async Task PromptPayloadHasNoModelOverrideAsync()
    {
        string? promptBody = null;
        var handler = ReadyHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session/" + SessionId + "/prompt_async")
            {
                promptBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return NotFound();
        });
        using var client = NewClient(handler);
        await client.SubmitStartupPromptAsync(Dispatch(), () => { });
        using JsonDocument payload = JsonDocument.Parse(promptBody ?? throw new InvalidOperationException("No prompt payload was captured."));
        Require(!payload.RootElement.TryGetProperty("model", out _), "The startup prompt overrode the configured model.");
        Require(payload.RootElement.GetProperty("messageID").GetString() == MessageId, "The startup prompt changed its fixed message ID.");
        Require(payload.RootElement.GetProperty("parts")[0].GetProperty("text").GetString() == Prompt,
            "The startup prompt text did not match the dispatch.");
    }

    private static FakeHandler MessageHandler(HttpStatusCode status, string? json) => new((request, _) =>
        Task.FromResult(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/session/" + SessionId + "/message/" + MessageId
            ? status == HttpStatusCode.NotFound ? NotFound() : Ok(json!)
            : NotFound()));

    private static FakeHandler ReadyHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> tail) =>
        new(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/session/" + SessionId) return Ok(SessionJson(SessionId, "Startup task"));
            if (request.Method == HttpMethod.Get && path == "/session/status") return Ok("{}");
            if (request.Method == HttpMethod.Get && path == "/permission") return Ok("[]");
            if (request.Method == HttpMethod.Get && path == "/question") return Ok("[]");
            return await tail(request, token);
        });

    private static OpenCodeAutomationClient NewClient(FakeHandler handler) =>
        new(new Uri("http://127.0.0.1:43127/"), Workspace, new HttpClient(handler, disposeHandler: false));

    private static OpenCodeAutomationDispatch Dispatch() => new(SessionId, MessageId, Prompt);

    private static string SessionJson(string id, string title) => JsonSerializer.Serialize(new
    {
        id,
        directory = Path.GetFullPath(Workspace),
        title,
        parentID = (string?)null
    });

    private static string StatusJson(string status) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        [SessionId] = new { type = status }
    });

    private static string PendingJson(string sessionId) => JsonSerializer.Serialize(new[] { new { sessionID = sessionId } });

    private static string MessageJson(string id, string sessionId, string role, string text) => JsonSerializer.Serialize(new
    {
        info = new { id, sessionID = sessionId, role },
        parts = new[] { new { type = "text", text } }
    });

    private static Task<HttpResponseMessage> JsonResponse(HttpMethod method, Uri uri,
        Func<(string Method, string Path), HttpResponseMessage> response) =>
        Task.FromResult(response((method.Method, uri.AbsolutePath)));

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!.AbsolutePath, body));
            HttpResponseMessage response = await responder(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);
}
