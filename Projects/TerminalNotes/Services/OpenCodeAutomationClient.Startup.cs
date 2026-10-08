using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace TerminalNotes.Services;

public sealed partial class OpenCodeAutomationClient
{
    private static readonly TimeSpan StartupRequestTimeout = TimeSpan.FromSeconds(30);

    public async Task<string> CreateStartupSessionAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Length > 256) throw new ArgumentOutOfRangeException(nameof(title));
        ObjectDisposedException.ThrowIf(_disposed, this);

        using (JsonDocument status = (await SendStartupJsonAsync(
                   HttpMethod.Get, BuildEndpoint("/session/status"), payload: null, operation: "startup_status", cancellationToken)
               .ConfigureAwait(false))!)
            _ = RequireObject(status.RootElement, "session status map");

        JsonDocument created;
        try
        {
            created = (await SendStartupJsonAsync(
                HttpMethod.Post, BuildEndpoint("/session"), new { title }, "startup_session_create", cancellationToken)
                .ConfigureAwait(false))!;
        }
        catch (OpenCodeAutomationException exception) when (exception.Code is "request_timeout" or "disconnected" or "invalid_response" or "transient_http_error" or "response_too_large")
        {
            throw Failure("session_create_ambiguous", "The startup session creation response was uncertain; no prompt was submitted.", exception);
        }

        string sessionId;
        using (created)
        {
            JsonElement session = RequireObject(created.RootElement, "created startup session");
            sessionId = GetString(session, "id") ?? "";
            ValidateSessionId(sessionId);
            VerifySessionIdentity(session, sessionId, title, requireTitle: true);
            EnsureStartupRootSession(session);
        }

        using JsonDocument detail = (await SendStartupJsonAsync(
                   HttpMethod.Get, BuildSessionEndpoint(sessionId), payload: null, operation: "startup_session_detail", cancellationToken)
               .ConfigureAwait(false))!;
        VerifySessionIdentity(RequireObject(detail.RootElement, "startup session detail"), sessionId, title, requireTitle: true);
        EnsureStartupRootSession(detail.RootElement);
        return sessionId;
    }

    public async Task SelectStartupSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using JsonDocument detail = (await SendStartupJsonAsync(
                   HttpMethod.Get, BuildSessionEndpoint(sessionId), payload: null, operation: "startup_session_detail", cancellationToken)
               .ConfigureAwait(false))!;
        JsonElement session = RequireObject(detail.RootElement, "startup session detail");
        VerifySessionIdentity(session, sessionId, expectedTitle: null, requireTitle: false);
        EnsureStartupRootSession(session);

        if (!_useTuiSelection) return;

        using JsonDocument selected = (await SendStartupJsonAsync(
                   HttpMethod.Post, BuildEndpoint("/tui/select-session"), new { sessionID = sessionId }, "startup_tui_select", cancellationToken)
               .ConfigureAwait(false))!;
        if (selected.RootElement.ValueKind != JsonValueKind.True)
            throw Failure("tui_select_not_confirmed", "OpenCode did not acknowledge the exact startup session selection.");
    }

    public async Task SubmitStartupPromptAsync(
        OpenCodeAutomationDispatch dispatch,
        Action beforeSubmit,
        CancellationToken cancellationToken = default,
        Action? submissionNotSent = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(beforeSubmit);
        ValidateSessionId(dispatch.SessionId);
        ValidateMessageId(dispatch.MessageId);
        if (string.IsNullOrWhiteSpace(dispatch.Prompt) || dispatch.Prompt.Length > MaximumPromptCharacters)
            throw Failure("invalid_prompt", "The startup prompt is empty or exceeds the native request limit.");
        ObjectDisposedException.ThrowIf(_disposed, this);

        using JsonDocument detail = (await SendStartupJsonAsync(
                   HttpMethod.Get, BuildSessionEndpoint(dispatch.SessionId), payload: null, operation: "startup_session_detail", cancellationToken)
               .ConfigureAwait(false))!;
        JsonElement session = RequireObject(detail.RootElement, "startup session detail");
        VerifySessionIdentity(session, dispatch.SessionId, expectedTitle: null, requireTitle: false);
        EnsureStartupRootSession(session);

        using JsonDocument status = (await SendStartupJsonAsync(
                   HttpMethod.Get, BuildEndpoint("/session/status"), payload: null, operation: "startup_status", cancellationToken)
               .ConfigureAwait(false))!;
        JsonElement statuses = RequireObject(status.RootElement, "session status map");
        if (ReadStatus(statuses, dispatch.SessionId) != NativeRunStatus.Idle)
            throw Failure("session_not_idle", "OpenCode has not confirmed the startup session is idle.");

        using JsonDocument permissions = (await SendStartupJsonAsync(
                   HttpMethod.Get, BuildEndpoint("/permission"), payload: null, operation: "startup_permissions", cancellationToken)
               .ConfigureAwait(false))!;
        using JsonDocument questions = (await SendStartupJsonAsync(
                   HttpMethod.Get, BuildEndpoint("/question"), payload: null, operation: "startup_questions", cancellationToken)
               .ConfigureAwait(false))!;
        var targetSession = new HashSet<string>(StringComparer.Ordinal) { dispatch.SessionId };
        if (HasPendingInput(permissions.RootElement, "permissions", targetSession) ||
            HasPendingInput(questions.RootElement, "questions", targetSession))
            throw Failure("session_input_pending", "The startup session has an unanswered permission or question request.");

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageID = dispatch.MessageId,
            parts = new[] { new { type = "text", text = dispatch.Prompt } }
        });
        if (payload.Length > MaximumResponseBytes)
            throw Failure("payload_too_large", "The startup prompt request exceeds the bounded JSON payload size.");

        cancellationToken.ThrowIfCancellationRequested();
        beforeSubmit();
        if (cancellationToken.IsCancellationRequested)
        {
            submissionNotSent?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
        await PostPromptAsync(dispatch.SessionId, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasStartupMessageAsync(OpenCodeAutomationDispatch dispatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ValidateSessionId(dispatch.SessionId);
        ValidateMessageId(dispatch.MessageId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Uri endpoint = BuildSessionEndpoint(
            dispatch.SessionId, "/message/" + Uri.EscapeDataString(dispatch.MessageId));
        using JsonDocument? document = await SendStartupJsonAsync(
            HttpMethod.Get, endpoint, payload: null, operation: "startup_message_read", cancellationToken, missingIsFalse: true)
            .ConfigureAwait(false);
        if (document is null) return false;

        JsonElement message = RequireObject(document.RootElement, "startup message");
        JsonElement info = RequireObject(GetProperty(message, "info"), "startup message info");
        if (!string.Equals(GetString(info, "id"), dispatch.MessageId, StringComparison.Ordinal) ||
            !string.Equals(GetString(info, "sessionID"), dispatch.SessionId, StringComparison.Ordinal) ||
            !string.Equals(GetString(info, "role"), "user", StringComparison.Ordinal))
            throw Failure("startup_message_identity_mismatch", "OpenCode returned a different native message identity for the startup request.");

        var text = new System.Text.StringBuilder();
        foreach (JsonElement partElement in RequireArray(GetProperty(message, "parts"), "startup message parts").EnumerateArray())
        {
            JsonElement part = RequireObject(partElement, "startup message part");
            if (!string.Equals(GetString(part, "type"), "text", StringComparison.Ordinal))
                throw Failure("startup_message_mismatch", "OpenCode returned non-text content for the startup prompt.");
            text.Append(GetString(part, "text") ?? throw Failure("invalid_message", "OpenCode returned a text part without text."));
        }
        if (!string.Equals(text.ToString(), dispatch.Prompt, StringComparison.Ordinal))
            throw Failure("startup_message_mismatch", "OpenCode returned different text for the startup prompt message.");
        return true;
    }

    private async Task<JsonDocument?> SendStartupJsonAsync(
        HttpMethod method,
        Uri endpoint,
        object? payload,
        string operation,
        CancellationToken cancellationToken,
        bool missingIsFalse = false)
    {
        using var request = payload is null
            ? new HttpRequestMessage(method, endpoint)
            : CreateJsonRequest(method, endpoint, JsonSerializer.SerializeToUtf8Bytes(payload));
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(StartupRequestTimeout);
        try
        {
            using HttpResponseMessage response = await SendCoreAsync(request, requestTimeout.Token).ConfigureAwait(false);
            if (missingIsFalse && response.StatusCode == HttpStatusCode.NotFound) return null;
            EnsureSuccess(response);
            byte[] bytes = await ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false),
                MaximumResponseBytes,
                requestTimeout.Token).ConfigureAwait(false);
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("request_timeout", "OpenCode did not finish the bounded " + operation + " request.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw Failure("disconnected", "The OpenCode loopback server could not complete the " + operation + " request.", exception);
        }
        catch (IOException exception)
        {
            throw Failure("invalid_response", "OpenCode returned a response that could not be read safely for " + operation + ".", exception);
        }
        catch (JsonException exception)
        {
            throw Failure("invalid_response", "OpenCode returned malformed JSON for " + operation + ".", exception);
        }
    }

    private static void EnsureStartupRootSession(JsonElement session)
    {
        if (GetString(session, "parentID") is not null)
            throw Failure("not_root_session", "OpenCode returned a child session where a dedicated startup root session was required.");
    }
}
