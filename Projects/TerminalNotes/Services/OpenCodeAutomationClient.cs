using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerminalNotes.Services;

public sealed record OpenCodeAutomationDispatch(
    string SessionId,
    string MessageId,
    string Prompt = "",
    DateTimeOffset? SubmittedAt = null,
    AutomationRole? Role = null)
{
    public AutomationWorkflowPhase? WorkflowPhase { get; init; }
}

/// <summary>
/// A failure reported by the native OpenCode automation API. POST requests are
/// never retried because their effects may have reached the visible session.
/// </summary>
public sealed class OpenCodeAutomationException : Exception
{
    public OpenCodeAutomationException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}

/// <summary>
/// Creates and selects one visible OpenCode root session, submits its role
/// prompt through the native API, and reads completion from native session data.
/// </summary>
public sealed partial class OpenCodeAutomationClient : IDisposable
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumPromptCharacters = 256 * 1024;
    private const int MaximumMessageCount = 200;
    private const int MaximumSessions = 100;
    private const int MaximumDescendantDepth = 16;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SubmissionAcknowledgementGrace = TimeSpan.FromSeconds(15);

    private readonly Uri _serverUri;
    private readonly string _directory;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string? _authorization;
    private readonly bool _useTuiSelection;
    private bool _disposed;

    // A null completion can mean a healthy busy session OR a failed read. Expose
    // the latter without changing the safe same-session retry behavior.
    public string? LastReadWarning { get; private set; }
    public IReadOnlyCollection<string> ObservedSessionIds { get; private set; } = [];

    public OpenCodeAutomationClient(Uri serverUri, string directory, HttpClient? httpClient = null, TimeProvider? timeProvider = null, bool useTuiSelection = true)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!serverUri.IsAbsoluteUri || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The OpenCode server URI must be an absolute HTTP URI.", nameof(serverUri));
        if (!serverUri.IsLoopback)
            throw new ArgumentException("The OpenCode automation server must use a loopback address.", nameof(serverUri));
        if (!string.IsNullOrEmpty(serverUri.UserInfo))
            throw new ArgumentException("Credentials must be supplied through OpenCode server environment settings.", nameof(serverUri));
        if (!string.IsNullOrEmpty(serverUri.Query) || !string.IsNullOrEmpty(serverUri.Fragment))
            throw new ArgumentException("The OpenCode server URI cannot include a query or fragment.", nameof(serverUri));

        _serverUri = serverUri;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _useTuiSelection = useTuiSelection;
        _directory = Path.GetFullPath(directory);
        _authorization = CreateAuthorizationHeader();
        if (httpClient is null)
        {
            _httpClient = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false
            });
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    /// <summary>
    /// Creates a dedicated root session, selects it in the visible TUI, and
    /// submits one exact user message. The callback runs at the final submit
    /// boundary, after session creation and selection, so callers can recheck
    /// ownership and cancellation immediately before the non-repeatable POST.
    /// </summary>
    public async Task<OpenCodeAutomationDispatch> StartAsync(
        string title,
        string prompt,
        string? model,
        CancellationToken cancellationToken = default,
        Action? beforeSubmit = null,
        Action<string>? sessionCreated = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (title.Length > 512)
            throw new ArgumentException("The automation session title exceeds the bounded title size.", nameof(title));
        if (prompt.Length > MaximumPromptCharacters)
            throw new ArgumentException("The role prompt exceeds the bounded prompt size.", nameof(prompt));
        ModelReference? modelReference = ParseModel(model);

        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(OperationTimeout);
        CancellationToken token = operationTimeout.Token;
        try
        {
            // A successful, well-formed status read proves the loopback endpoint
            // is ready before the first state-changing request is attempted.
            using (JsonDocument status = await GetJsonAsync(BuildEndpoint("/session/status"), token).ConfigureAwait(false))
                _ = RequireObject(status.RootElement, "session status map");

            using JsonDocument created = await PostJsonAsync(
                BuildEndpoint("/session"),
                new { title },
                token,
                "session_create").ConfigureAwait(false);
            JsonElement createdSession = RequireObject(created.RootElement, "created session");
            string sessionId = GetString(createdSession, "id") ?? "";
            ValidateSessionId(sessionId);
            VerifySessionIdentity(createdSession, sessionId, title, requireTitle: true);
            if (GetString(createdSession, "parentID") is not null)
                throw Failure("not_root_session", "OpenCode created a child session where a dedicated root session was required.");

            using (JsonDocument detail = await GetJsonAsync(BuildSessionEndpoint(sessionId), token).ConfigureAwait(false))
            {
                JsonElement session = RequireObject(detail.RootElement, "session detail");
                VerifySessionIdentity(session, sessionId, title, requireTitle: true);
                if (GetString(session, "parentID") is not null)
                    throw Failure("not_root_session", "OpenCode returned a child session where a dedicated root session was required.");
            }

            if (_useTuiSelection)
            using (JsonDocument selected = await PostJsonAsync(
                       BuildEndpoint("/tui/select-session"),
                       new { sessionID = sessionId },
                       token,
                       "tui_select").ConfigureAwait(false))
            {
                if (selected.RootElement.ValueKind != JsonValueKind.True)
                    throw Failure("tui_select_not_confirmed", "OpenCode did not acknowledge the exact session selection broadcast.");
            }

            string messageId = OpenCodeRecoveryClient.CreateMessageId();
            sessionCreated?.Invoke(sessionId);
            object promptRequest = modelReference is null
                ? new
                {
                    messageID = messageId,
                    parts = new[] { new { type = "text", text = prompt } }
                }
                : new
                {
                    messageID = messageId,
                    parts = new[] { new { type = "text", text = prompt } },
                    model = new { providerID = modelReference.ProviderID, modelID = modelReference.ModelID }
                };
            byte[] promptPayload = JsonSerializer.SerializeToUtf8Bytes(promptRequest);
            if (promptPayload.Length > MaximumResponseBytes)
                throw Failure("payload_too_large", "The role prompt request exceeds the bounded JSON payload size.");

            token.ThrowIfCancellationRequested();
            beforeSubmit?.Invoke();
            token.ThrowIfCancellationRequested();
            var dispatch = new OpenCodeAutomationDispatch(
                sessionId,
                messageId,
                prompt,
                _timeProvider.GetUtcNow(),
                AutomationStageOutputContract.RoleFromPrompt(prompt))
            {
                WorkflowPhase = AutomationStageOutputContract.PhaseFromPrompt(prompt)
            };
            try
            {
                await PostPromptAsync(sessionId, promptPayload, token).ConfigureAwait(false);
            }
            catch (OpenCodeAutomationException exception) when (exception.Code == "prompt_response_ambiguous")
            {
                // The POST may already have reached OpenCode. Preserve its exact
                // session/message identity and let ReadCompletionAsync reconcile it;
                // resubmitting here could execute a mutating role twice.
            }
            return dispatch;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw Failure("request_timeout", "OpenCode did not finish the bounded session setup and prompt submission.", exception);
        }
        catch (OpenCodeAutomationException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Failure("disconnected", "The OpenCode loopback server could not complete session setup or prompt submission.", exception);
        }
        catch (IOException exception)
        {
            throw Failure("invalid_response", "OpenCode returned a response that could not be read safely.", exception);
        }
        catch (JsonException exception)
        {
            throw Failure("invalid_response", "OpenCode returned malformed JSON during session setup or prompt submission.", exception);
        }
    }

    /// <summary>
    /// Reads one bounded native snapshot. Pending work, blocked input, uncertain
    /// reads, and recovery awaiting confirmed quiescence all retain this dispatch.
    /// </summary>
    public async Task<AutomationStageOutput?> ReadCompletionAsync(
        OpenCodeAutomationDispatch dispatch,
        CancellationToken cancellationToken = default)
    {
        LastReadWarning = null;
        ArgumentNullException.ThrowIfNull(dispatch);
        ValidateSessionId(dispatch.SessionId);
        ValidateMessageId(dispatch.MessageId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(OperationTimeout);
        CancellationToken token = operationTimeout.Token;
        try
        {
            AutomationSnapshot snapshot = await ReadSnapshotAsync(dispatch, token).ConfigureAwait(false);
            ObserveProgress(dispatch, snapshot);
            if (_recoveryReason is not null)
                return RecoveredOutputIfIdle(snapshot);
            // A tool error can be followed by corrective work in the same turn.
            // Never release ownership while that turn or a descendant is active.
            if (snapshot.Status is NativeRunStatus.Busy or NativeRunStatus.Retry ||
                snapshot.HasBusyDescendants || snapshot.HasPendingInput || snapshot.HasRunningTools)
                return null;
            foreach (AssistantMessage assistant in snapshot.AssistantMessages)
            {
                if (assistant.Error is not null)
                    return FailureOutput("OpenCode reported an assistant error: " + BoundText(assistant.Error), snapshot.Tokens);
                if (assistant.HasToolError)
                    return FailureOutput("OpenCode reported a tool error in the exact role response.", snapshot.Tokens);
            }
            if (!snapshot.HasTargetUserMessage)
            {
                if (IsWithinAcknowledgementGrace(dispatch)) return null;
                return FailureOutput("The exact role user message is missing from its native OpenCode session.");
            }
            if (!snapshot.LatestUserMessageIsTarget)
                return FailureOutput("A later user message replaced this role dispatch in its dedicated session.");
            if (!snapshot.TargetUserTextMatches)
                return FailureOutput("The exact role user message does not contain the submitted prompt.");

            if (snapshot.AssistantMessages.Count == 0)
            {
                if (IsWithinAcknowledgementGrace(dispatch)) return null;
                return FailureOutput("OpenCode became idle without creating an assistant response for the exact role message.");
            }

            foreach (AssistantMessage assistant in snapshot.AssistantMessages)
            {
                if (assistant.HasRunningTool)
                    return null;
                if (assistant.Finish is null || assistant.CompletedAt is null)
                {
                    if (IsWithinAcknowledgementGrace(dispatch)) return null;
                    return FailureOutput("OpenCode became idle before the exact role assistant message had a terminal finish and completion time.");
                }
            }

            AssistantMessage finalAssistant = snapshot.AssistantMessages
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .First();
            if (!string.Equals(finalAssistant.Finish, "stop", StringComparison.Ordinal))
                return FailureOutput("The exact role turn ended without a successful terminal assistant finish.");
            if (finalAssistant.CompletedAt < finalAssistant.CreatedAt)
                return FailureOutput("OpenCode returned an invalid completion time for the exact role response.");

            string responseText = string.Concat(finalAssistant.TextParts);
            if (!AutomationStageOutputContract.TryParse(
                responseText,
                dispatch.Role ?? AutomationStageOutputContract.RoleFromPrompt(dispatch.Prompt),
                dispatch.WorkflowPhase ?? AutomationStageOutputContract.PhaseFromPrompt(dispatch.Prompt),
                out AutomationStageOutput? output,
                out _,
                out string? error))
                return FailureOutput(error ?? "OpenCode's final assistant response did not match the required JSON contract.", snapshot.Tokens);

            return output! with { Tokens = snapshot.Tokens };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            ObserveTransportFailure();
            LastReadWarning = "OpenCode 결과 조회 시간 초과 · 동일 세션 재확인";
            return null;
        }
        catch (OpenCodeAutomationException exception) when (exception.Code == "transient_http_error")
        {
            ObserveTransportFailure();
            LastReadWarning = exception.Message;
            return null;
        }
        catch (OpenCodeAutomationException exception)
        {
            ResetTransportFailures();
            // An invalid snapshot does not prove that a mutating session stopped.
            LastReadWarning = exception.Message;
            return null;
        }
        catch (HttpRequestException)
        {
            ObserveTransportFailure();
            LastReadWarning = "OpenCode 결과 조회 연결 실패 · 동일 세션 재확인";
            return null;
        }
        catch (IOException)
        {
            ObserveTransportFailure();
            LastReadWarning = "OpenCode 결과 응답 읽기 실패 · 동일 세션 재확인";
            return null;
        }
        catch (JsonException exception)
        {
            ResetTransportFailures();
            LastReadWarning = "OpenCode returned malformed JSON in the native completion read. " + exception.GetType().Name + ".";
            return null;
        }
        finally
        {
            if (LastReadWarning is not null) ResetProgressObservation();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private async Task<AutomationSnapshot> ReadSnapshotAsync(OpenCodeAutomationDispatch dispatch, CancellationToken cancellationToken)
    {
        using JsonDocument detailDocument = await GetJsonAsync(BuildSessionEndpoint(dispatch.SessionId), cancellationToken).ConfigureAwait(false);
        JsonElement detail = RequireObject(detailDocument.RootElement, "session detail");
        VerifySessionIdentity(detail, dispatch.SessionId, expectedTitle: null, requireTitle: false);
        if (GetString(detail, "parentID") is not null)
            throw Failure("not_root_session", "The target automation session is no longer a root session.");

        Task<JsonDocument> statusTask = GetJsonAsync(BuildEndpoint("/session/status"), cancellationToken);
        Task<JsonDocument> childTask = GetJsonAsync(BuildSessionEndpoint(dispatch.SessionId, "/children"), cancellationToken);
        Task<JsonDocument> permissionTask = GetJsonAsync(BuildEndpoint("/permission"), cancellationToken);
        Task<JsonDocument> questionTask = GetJsonAsync(BuildEndpoint("/question"), cancellationToken);
        Task<JsonDocument> messagesTask = GetJsonAsync(
            BuildSessionEndpoint(dispatch.SessionId, "/message", "limit=" + MaximumMessageCount.ToString(CultureInfo.InvariantCulture)),
            cancellationToken);
        try { await Task.WhenAll(statusTask, childTask, permissionTask, questionTask, messagesTask).ConfigureAwait(false); }
        catch
        {
            foreach (var task in new[] { statusTask, childTask, permissionTask, questionTask, messagesTask })
                if (task.IsCompletedSuccessfully) task.Result.Dispose();
            throw;
        }

        using JsonDocument statusDocument = await statusTask.ConfigureAwait(false);
        using JsonDocument childDocument = await childTask.ConfigureAwait(false);
        using JsonDocument permissionDocument = await permissionTask.ConfigureAwait(false);
        using JsonDocument questionDocument = await questionTask.ConfigureAwait(false);
        using JsonDocument messagesDocument = await messagesTask.ConfigureAwait(false);

        JsonElement statuses = RequireObject(statusDocument.RootElement, "session status map");
        NativeRunStatus rootStatus = ReadStatus(statuses, dispatch.SessionId);
        List<SessionIdentity> descendants = await ReadDescendantsAsync(
            dispatch.SessionId,
            RequireArray(childDocument.RootElement, "session children"),
            cancellationToken).ConfigureAwait(false);
        HashSet<string> sessionIds = new(StringComparer.Ordinal) { dispatch.SessionId };
        foreach (SessionIdentity descendant in descendants)
            sessionIds.Add(descendant.Id);

        bool busyDescendants = false;
        bool retrying = rootStatus == NativeRunStatus.Retry;
        bool descendantTools = false;
        var progress = new StringBuilder();
        var activeSessionIds = new List<string>();
        if (rootStatus != NativeRunStatus.Idle) activeSessionIds.Add(dispatch.SessionId);
        foreach (SessionIdentity descendant in descendants.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            NativeRunStatus status = ReadStatus(statuses, descendant.Id);
            if (status is NativeRunStatus.Busy or NativeRunStatus.Retry)
            {
                busyDescendants = true;
                activeSessionIds.Add(descendant.Id);
            }
            retrying |= status == NativeRunStatus.Retry;
            using JsonDocument childMessages = await GetJsonAsync(
                BuildSessionEndpoint(descendant.Id, "/message", "limit=" + MaximumMessageCount), cancellationToken).ConfigureAwait(false);
            bool childTools = AppendMessageProgress(progress, childMessages.RootElement, descendant.Id);
            descendantTools |= childTools;
            if (childTools && !activeSessionIds.Contains(descendant.Id)) activeSessionIds.Add(descendant.Id);
        }

        bool hasPendingPermission = HasPendingInput(permissionDocument.RootElement, "permissions", sessionIds);
        bool hasPendingQuestion = HasPendingInput(questionDocument.RootElement, "questions", sessionIds);
        bool pendingInput = hasPendingPermission || hasPendingQuestion;
        MessageEvidence messages = ParseMessages(
            RequireArray(messagesDocument.RootElement, "session messages"),
            dispatch.SessionId,
            dispatch.MessageId,
            dispatch.Prompt);
        bool runningTools = AppendMessageProgress(progress, messagesDocument.RootElement, dispatch.SessionId, dispatch.MessageId);
        if (runningTools && !activeSessionIds.Contains(dispatch.SessionId)) activeSessionIds.Add(dispatch.SessionId);
        ObservedSessionIds = sessionIds.ToArray();
        return new AutomationSnapshot(
            rootStatus,
            busyDescendants,
            pendingInput,
            messages.HasTargetUserMessage,
            messages.LatestUserMessageIsTarget,
            messages.TargetUserTextMatches,
            messages.AssistantMessages,
            messages.Tokens,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(progress.ToString()))),
            progress.Length > 0,
            progress.ToString(Math.Max(0, progress.Length - 10000), Math.Min(progress.Length, 10000)),
            runningTools || descendantTools,
            retrying,
            activeSessionIds,
            messagesDocument.RootElement.EnumerateArray().Any(row =>
                GetString(GetProperty(row, "info"), "role") == "user" &&
                GetString(GetProperty(row, "info"), "id") != dispatch.MessageId));
    }

    private async Task<List<SessionIdentity>> ReadDescendantsAsync(
        string rootSessionId,
        JsonElement initialChildren,
        CancellationToken cancellationToken)
    {
        var result = new List<SessionIdentity>();
        var pending = new Queue<(string ParentId, JsonElement Children, int Depth)>();
        pending.Enqueue((rootSessionId, initialChildren, 1));
        var seen = new HashSet<string>(StringComparer.Ordinal) { rootSessionId };
        while (pending.Count > 0)
        {
            (string parentId, JsonElement children, int depth) = pending.Dequeue();
            if (depth > MaximumDescendantDepth)
                throw Failure("descendant_depth_exceeded", "The automation session descendant tree exceeded the supported depth.");
            foreach (JsonElement childElement in children.EnumerateArray())
            {
                JsonElement child = RequireObject(childElement, "child session");
                string id = GetString(child, "id") ?? "";
                ValidateSessionId(id);
                if (!seen.Add(id))
                    throw Failure("ambiguous_descendant", "OpenCode returned a duplicate session in the automation descendant tree.");
                if (seen.Count - 1 > MaximumSessions)
                    throw Failure("too_many_descendants", "The automation session exceeded the supported descendant count.");
                string? actualParentId = GetString(child, "parentID");
                if (!string.Equals(actualParentId, parentId, StringComparison.Ordinal))
                    throw Failure("descendant_identity_mismatch", "OpenCode returned a child session with an unexpected parent ID.");
                string childDirectory = GetString(child, "directory") ?? "";
                if (!DirectoriesEqual(childDirectory, _directory))
                    throw Failure("descendant_directory_mismatch", "OpenCode returned a descendant session from a different directory.");

                var identity = new SessionIdentity(id, parentId, childDirectory);
                result.Add(identity);
                using JsonDocument nested = await GetJsonAsync(
                    BuildSessionEndpoint(id, "/children"),
                    cancellationToken).ConfigureAwait(false);
                pending.Enqueue((id, RequireArray(nested.RootElement, "descendant children").Clone(), depth + 1));
            }
        }
        return result;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        return await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostJsonAsync(Uri endpoint, object payload, CancellationToken cancellationToken, string operation)
    {
        using var request = CreateJsonRequest(HttpMethod.Post, endpoint, JsonSerializer.SerializeToUtf8Bytes(payload));
        try
        {
            return await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenCodeAutomationException exception) when (exception.Code is "request_timeout" or "disconnected" or "invalid_response")
        {
            throw Failure(operation + "_ambiguous", "The " + operation + " response was lost or invalid; OpenCode was not sent a retry.", exception);
        }
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(RequestTimeout);
        using HttpResponseMessage response = await SendCoreAsync(request, requestTimeout.Token).ConfigureAwait(false);
        EnsureSuccess(response);
        byte[] bytes = await ReadBoundedAsync(
            await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false),
            MaximumResponseBytes,
            requestTimeout.Token).ConfigureAwait(false);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
    }

    private async Task PostPromptAsync(string sessionId, byte[] payload, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest(HttpMethod.Post, BuildSessionEndpoint(sessionId, "/prompt_async"), payload);
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(RequestTimeout);
        try
        {
            using HttpResponseMessage response = await SendCoreAsync(request, requestTimeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                if ((int)response.StatusCode is >= 400 and < 500)
                    throw Failure("prompt_rejected", $"OpenCode rejected the exact role prompt with HTTP {(int)response.StatusCode}.");
                throw Failure("prompt_response_ambiguous", $"OpenCode returned HTTP {(int)response.StatusCode} for the role prompt; it was not retried.");
            }
        }
        catch (OperationCanceledException exception)
        {
            throw Failure("prompt_response_ambiguous", "The role prompt response timed out after dispatch may have begun; it was not retried.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw Failure("prompt_response_ambiguous", "The role prompt response was lost after dispatch may have begun; it was not retried.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_authorization is not null)
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(_authorization);
        HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        Uri requested = request.RequestUri ?? throw Failure("invalid_request", "The OpenCode request did not have an endpoint URI.");
        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || !IsAllowedFinalUri(finalUri) || !UriEquals(requested, finalUri))
        {
            response.Dispose();
            throw Failure("redirect_rejected", "OpenCode redirected a loopback request to a different endpoint.");
        }
        return response;
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        bool transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                         (int)response.StatusCode >= 500;
        throw Failure(transient ? "transient_http_error" : "http_error",
            $"OpenCode returned HTTP {(int)response.StatusCode} for {response.RequestMessage?.RequestUri?.AbsolutePath ?? "a native API request"}.");
    }

    private HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri endpoint, byte[] payload)
    {
        var request = new HttpRequestMessage(method, endpoint)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    private Uri BuildEndpoint(string path, string? query = null)
    {
        string root = _serverUri.GetLeftPart(UriPartial.Authority) + _serverUri.AbsolutePath.TrimEnd('/');
        string endpoint = root + path;
        var queryParts = new List<string>();
        if (query is not null) queryParts.Add(query);
        queryParts.Add("directory=" + Uri.EscapeDataString(_directory));
        return new Uri(endpoint + "?" + string.Join("&", queryParts), UriKind.Absolute);
    }

    private Uri BuildSessionEndpoint(string sessionId, string suffix = "", string? query = null) =>
        BuildEndpoint("/session/" + Uri.EscapeDataString(sessionId) + suffix, query);

    private static NativeRunStatus ReadStatus(JsonElement statuses, string sessionId)
    {
        if (!statuses.TryGetProperty(sessionId, out JsonElement status))
            return NativeRunStatus.Idle; // OpenCode omits idle sessions from its status map.
        if (status.ValueKind != JsonValueKind.Object)
            throw Failure("invalid_status", "OpenCode returned a malformed session status entry.");
        return GetString(status, "type") switch
        {
            "idle" => NativeRunStatus.Idle,
            "busy" => NativeRunStatus.Busy,
            "retry" => NativeRunStatus.Retry,
            string value => throw Failure("invalid_status", "OpenCode returned an unknown session status: " + BoundText(value) + "."),
            null => throw Failure("invalid_status", "OpenCode returned a session status without its native type.")
        };
    }

    private static bool HasPendingInput(JsonElement entries, string resource, HashSet<string> targetSessionIds)
    {
        JsonElement array = RequireArray(entries, resource);
        bool found = false;
        foreach (JsonElement entryElement in array.EnumerateArray())
        {
            JsonElement entry = RequireObject(entryElement, resource + " entry");
            string? sessionId = GetString(entry, "sessionID");
            if (string.IsNullOrWhiteSpace(sessionId))
                throw Failure("invalid_pending_input", "OpenCode returned a " + resource + " entry without a session ID.");
            if (targetSessionIds.Contains(sessionId)) found = true;
        }
        return found;
    }

    private static MessageEvidence ParseMessages(JsonElement messages, string sessionId, string targetMessageId, string expectedPrompt)
    {
        JsonElement[] rows = messages.EnumerateArray().ToArray();
        if (rows.Length > MaximumMessageCount)
            throw Failure("too_many_messages", "OpenCode returned more messages than the bounded role session read permits.");

        var parsed = new List<ParsedMessage>(rows.Length);
        foreach (JsonElement rowElement in rows)
        {
            JsonElement row = RequireObject(rowElement, "message row");
            JsonElement info = RequireObject(GetProperty(row, "info"), "message info");
            string id = GetString(info, "id") ?? "";
            string? role = GetString(info, "role");
            string? actualSessionId = GetString(info, "sessionID");
            if (string.IsNullOrWhiteSpace(id) || role is not ("user" or "assistant"))
                throw Failure("invalid_message", "OpenCode returned a message without a recognized ID or role.");
            if (!string.Equals(actualSessionId, sessionId, StringComparison.Ordinal))
                throw Failure("message_session_mismatch", "OpenCode returned a message from a different session.");
            JsonElement parts = RequireArray(GetProperty(row, "parts"), "message parts");
            long created = ReadRequiredTime(GetProperty(GetProperty(info, "time"), "created"), "message creation");
            long? completed = ReadOptionalTime(GetProperty(GetProperty(info, "time"), "completed"), "message completion");
            string? parentId = GetString(info, "parentID");
            string? finish = GetString(info, "finish");
            string? messageError = ParseError(GetProperty(info, "error"));
            var textParts = new List<string>();
            bool hasToolError = false;
            bool hasRunningTool = false;
            foreach (JsonElement partElement in parts.EnumerateArray())
            {
                JsonElement part = RequireObject(partElement, "message part");
                string? type = GetString(part, "type");
                if (type == "text")
                {
                    string? text = GetString(part, "text");
                    if (text is null) throw Failure("invalid_message", "OpenCode returned a text part without text.");
                    textParts.Add(text);
                }
                else if (type == "tool")
                {
                    JsonElement state = GetProperty(part, "state");
                    if (state.ValueKind != JsonValueKind.Object)
                        throw Failure("invalid_message", "OpenCode returned a tool part without a state object.");
                    string? toolStatus = GetString(state, "status");
                    if (toolStatus is "pending" or "running") hasRunningTool = true;
                    if (toolStatus == "error") hasToolError = true;
                    if (toolStatus is not ("pending" or "running" or "completed" or "error"))
                        throw Failure("invalid_message", "OpenCode returned a tool part with an unknown state.");
                }
            }

            long messageTokens = ReadTokenCount(GetProperty(info, "tokens"));
            parsed.Add(new ParsedMessage(id, role, parentId, created, completed, finish, messageError, textParts, hasToolError, hasRunningTool, messageTokens));
        }

        ParsedMessage? targetUser = parsed.FirstOrDefault(message =>
            message.Role == "user" && string.Equals(message.Id, targetMessageId, StringComparison.Ordinal));
        if (parsed.Count(message => message.Role == "user" && string.Equals(message.Id, targetMessageId, StringComparison.Ordinal)) > 1)
            throw Failure("duplicate_message_id", "OpenCode returned the target user message more than once.");
        ParsedMessage? latestUser = parsed.Where(message => message.Role == "user")
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (targetUser is null)
            return new MessageEvidence(false, false, false, new List<AssistantMessage>(), 0);

        List<ParsedMessage> assistantsAfterTarget = parsed
            .Where(message => message.Role == "assistant" && IsAfter(message, targetUser))
            .ToList();
        foreach (ParsedMessage assistant in assistantsAfterTarget)
        {
            if (!string.Equals(assistant.ParentId, targetMessageId, StringComparison.Ordinal))
                throw Failure("assistant_parent_mismatch", "A later assistant message in the dedicated session is not parented to the exact role message.");
        }
        var assistantMessages = assistantsAfterTarget
            .Select(message => new AssistantMessage(
                message.Id,
                message.CreatedAt,
                message.CompletedAt,
                message.Finish,
                message.Error,
                message.TextParts,
                message.HasToolError,
                message.HasRunningTool))
            .ToList();

        string targetText = string.Concat(targetUser.TextParts);
        long tokenTotal = assistantsAfterTarget
            .Where(message => string.Equals(message.ParentId, targetMessageId, StringComparison.Ordinal))
            .Aggregate(0L, (total, message) => SaturatingAdd(total, message.Tokens));
        return new MessageEvidence(
            true,
            latestUser is not null && string.Equals(latestUser.Id, targetMessageId, StringComparison.Ordinal),
            string.Equals(targetText, expectedPrompt, StringComparison.Ordinal),
            assistantMessages,
            tokenTotal);
    }

    private void VerifySessionIdentity(JsonElement session, string expectedSessionId, string? expectedTitle, bool requireTitle)
    {
        string? actualId = GetString(session, "id");
        string? actualDirectory = GetString(session, "directory");
        if (!string.Equals(actualId, expectedSessionId, StringComparison.Ordinal))
            throw Failure("session_identity_mismatch", "OpenCode returned a different session ID than the one created for this role.");
        if (string.IsNullOrWhiteSpace(actualDirectory) || !DirectoriesEqual(actualDirectory, _directory))
            throw Failure("session_directory_mismatch", "OpenCode returned a role session from a different directory.");
        if (requireTitle && !string.Equals(GetString(session, "title"), expectedTitle, StringComparison.Ordinal))
            throw Failure("session_title_mismatch", "OpenCode returned a role session with a different title.");
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (sessionId.Length is < 4 or > 128 || !sessionId.StartsWith("ses", StringComparison.Ordinal) ||
            sessionId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw Failure("invalid_session_id", "OpenCode returned an invalid session ID.");
    }

    private static void ValidateMessageId(string messageId)
    {
        if (messageId.Length is < 5 or > 128 || !messageId.StartsWith("msg_", StringComparison.Ordinal) ||
            messageId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw Failure("invalid_message_id", "The automation dispatch contains an invalid message ID.");
    }

    private static ModelReference? ParseModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        int slash = model.IndexOf('/');
        if (slash <= 0 || slash == model.Length - 1 || model.AsSpan(slash + 1).Contains('/'))
            throw new ArgumentException("The OpenCode model must use provider/model format.", nameof(model));
        string providerId = model[..slash].Trim();
        string modelId = model[(slash + 1)..].Trim();
        if (providerId.Length == 0 || modelId.Length == 0 || providerId.Length > 128 || modelId.Length > 256)
            throw new ArgumentException("The OpenCode model must use bounded provider/model identifiers.", nameof(model));
        return new ModelReference(providerId, modelId);
    }

    private bool IsAllowedFinalUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.IsLoopback &&
        string.Equals(uri.Scheme, _serverUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, _serverUri.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == _serverUri.Port;

    private static bool UriEquals(Uri left, Uri right) =>
        string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private static bool DirectoriesEqual(string left, string right)
    {
        try
        {
            string leftFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            string rightFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(leftFull, rightFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
    }

    private static string? CreateAuthorizationHeader()
    {
        string? password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        if (string.IsNullOrEmpty(password)) return null;
        string username = Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw Failure("response_too_large", "OpenCode returned a response larger than the native API safety limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static JsonElement RequireObject(JsonElement element, string resource) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw Failure("invalid_response", "OpenCode returned an invalid " + resource + " response.");

    private static JsonElement RequireArray(JsonElement element, string resource) =>
        element.ValueKind == JsonValueKind.Array
            ? element
            : throw Failure("invalid_response", "OpenCode returned an invalid " + resource + " response.");

    private static JsonElement GetProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement value) ? value : default;

    private static string? GetString(JsonElement element, string propertyName)
    {
        JsonElement value = GetProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long ReadRequiredTime(JsonElement element, string description) =>
        ReadOptionalTime(element, description) ?? throw Failure("invalid_message_time", "OpenCode returned a message without a valid " + description + " time.");

    private static long? ReadOptionalTime(JsonElement element, string description)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number) && number >= 0) return number;
        if (element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            return parsed.ToUnixTimeMilliseconds();
        throw Failure("invalid_message_time", "OpenCode returned an invalid " + description + " time.");
    }

    private static string? ParseError(JsonElement error)
    {
        if (error.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (error.ValueKind != JsonValueKind.Object)
            return "OpenCode returned a malformed assistant error marker.";
        string? name = GetString(error, "name") ?? GetString(error, "type");
        JsonElement data = GetProperty(error, "data");
        string? message = GetString(data, "message");
        return string.IsNullOrWhiteSpace(name) ? message ?? "OpenCode reported an assistant error." :
            string.IsNullOrWhiteSpace(message) ? name : name + ": " + message;
    }

    private static long ReadTokenCount(JsonElement tokens)
    {
        if (tokens.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return 0;
        if (tokens.ValueKind != JsonValueKind.Object)
            throw Failure("invalid_tokens", "OpenCode returned malformed assistant token usage.");
        JsonElement total = GetProperty(tokens, "total");
        if (total.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (total.ValueKind != JsonValueKind.Number || !total.TryGetInt64(out long totalTokens) || totalTokens < 0)
                throw Failure("invalid_tokens", "OpenCode returned invalid total token usage.");
            return totalTokens;
        }
        long result = 0;
        foreach (string field in new[] { "input", "output", "reasoning" })
            result = SaturatingAdd(result, ReadNonNegativeInt64(tokens, field));
        JsonElement cache = GetProperty(tokens, "cache");
        if (cache.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (cache.ValueKind != JsonValueKind.Object)
                throw Failure("invalid_tokens", "OpenCode returned malformed cache token usage.");
            result = SaturatingAdd(result, ReadNonNegativeInt64(cache, "read"));
            result = SaturatingAdd(result, ReadNonNegativeInt64(cache, "write"));
        }
        return result;
    }

    private static long ReadNonNegativeInt64(JsonElement element, string propertyName)
    {
        JsonElement value = GetProperty(element, propertyName);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number) || number < 0)
            throw Failure("invalid_tokens", "OpenCode returned invalid token usage for " + propertyName + ".");
        return number;
    }

    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static bool IsAfter(ParsedMessage message, ParsedMessage other) =>
        message.CreatedAt > other.CreatedAt ||
        (message.CreatedAt == other.CreatedAt && string.CompareOrdinal(message.Id, other.Id) > 0);

    private static string BoundText(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 300));
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                if (character is '\r' or '\n' or '\t') builder.Append(' ');
                continue;
            }
            builder.Append(character);
            if (builder.Length >= 300) break;
        }
        return builder.ToString().Trim();
    }

    private static AutomationStageOutput FailureOutput(string summary, long tokens = 0) => new(false, summary, "", false, tokens);

    private bool IsWithinAcknowledgementGrace(OpenCodeAutomationDispatch dispatch) =>
        dispatch.SubmittedAt is not null && _timeProvider.GetUtcNow() - dispatch.SubmittedAt.Value < SubmissionAcknowledgementGrace;

    private static OpenCodeAutomationException Failure(string code, string message, Exception? innerException = null) =>
        new(code, message, innerException);

    private enum NativeRunStatus { Idle, Busy, Retry }
    private sealed record ModelReference(string ProviderID, string ModelID);
    private sealed record SessionIdentity(string Id, string ParentId, string Directory);
    private sealed record ParsedMessage(
        string Id,
        string Role,
        string? ParentId,
        long CreatedAt,
        long? CompletedAt,
        string? Finish,
        string? Error,
        List<string> TextParts,
        bool HasToolError,
        bool HasRunningTool,
        long Tokens);
    private sealed record AssistantMessage(
        string Id,
        long CreatedAt,
        long? CompletedAt,
        string? Finish,
        string? Error,
        List<string> TextParts,
        bool HasToolError,
        bool HasRunningTool);
    private sealed record MessageEvidence(
        bool HasTargetUserMessage,
        bool LatestUserMessageIsTarget,
        bool TargetUserTextMatches,
        List<AssistantMessage> AssistantMessages,
        long Tokens);
    private sealed record AutomationSnapshot(
        NativeRunStatus Status,
        bool HasBusyDescendants,
        bool HasPendingInput,
        bool HasTargetUserMessage,
        bool LatestUserMessageIsTarget,
        bool TargetUserTextMatches,
        List<AssistantMessage> AssistantMessages,
        long Tokens,
        string ProgressFingerprint,
        bool HasProgressEvidence,
        string ProgressExcerpt,
        bool HasRunningTools,
        bool IsRetrying,
        List<string> ActiveSessionIds,
        bool HasUnexpectedUserMessage);
}
