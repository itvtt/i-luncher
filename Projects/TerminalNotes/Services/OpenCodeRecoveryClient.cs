using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerminalNotes.Services;

public enum OpenCodeRecoveryStatus
{
    Unknown,
    Idle,
    Busy,
    Retry
}

public enum OpenCodeRecoveryOutcome
{
    Accepted,
    Rejected,
    Ambiguous
}

public sealed record RecoverySessionEvidence(
    string SessionId,
    OpenCodeRecoveryStatus Status,
    string? ParentSessionId,
    string SessionDirectory,
    bool HasPendingPermission,
    bool HasPendingQuestion,
    bool HasPendingPermissionInDescendants,
    bool HasPendingQuestionInDescendants,
    int IncompleteTodoCount,
    int TotalTodoCount,
    string TodoFingerprint,
    string ProgressFingerprint,
    bool HasRunningTools,
    bool HasBusyDescendants,
    string? LatestUserMessageId,
    DateTimeOffset? LatestUserMessageCreated,
    string? LatestAssistantMessageId,
    string? LatestMessageId,
    bool HasErrorMarker,
    string? LatestErrorType,
    int? RetryAttempt,
    string? RetryMessage,
    long? RetryNext)
{
    public int CompletedTodoCount { get; init; }
    public bool AllTodosCompleted { get; init; }
    public bool HasSuccessfulFinalResponse { get; init; }
    public string ActivityFingerprint { get; init; } = string.Empty;
    public string? StalledLaunchToolFingerprint { get; init; }
    public string? AbortCancellationToolFingerprint { get; init; }
    public IReadOnlyList<RecoveryErrorEvidence> CurrentErrors { get; init; } = Array.Empty<RecoveryErrorEvidence>();
}

public sealed record RecoveryErrorEvidence(string Type, string? ToolFingerprint);

public sealed record OpenCodeRecoveryReadResult(
    bool IsAvailable,
    RecoverySessionEvidence? Evidence,
    string? ErrorCode = null,
    string? Error = null);

public sealed record OpenCodeRecoveryContinueResult(
    OpenCodeRecoveryOutcome Outcome,
    string MessageId,
    string? ErrorCode = null,
    string? Error = null)
{
    public bool IsAccepted => Outcome == OpenCodeRecoveryOutcome.Accepted;
    public bool IsAmbiguous => Outcome == OpenCodeRecoveryOutcome.Ambiguous;
}

public sealed record OpenCodeRecoveryActionResult(
    OpenCodeRecoveryOutcome Outcome,
    string? ErrorCode = null,
    string? Error = null)
{
    public bool IsAccepted => Outcome == OpenCodeRecoveryOutcome.Accepted;
}

/// <summary>
/// Reads and controls one exact OpenCode root session through its loopback HTTP API.
/// This client never sends keystrokes to the terminal or invokes TUI-control routes.
/// </summary>
public sealed class OpenCodeRecoveryClient : IDisposable
{
    private const int MaximumJsonPayloadBytes = 2 * 1024 * 1024;
    private const int MaximumSessionListBytes = 2 * 1024 * 1024;
    private const int MaximumSessions = 200;
    private const int MaximumDescendants = 100;
    private const int MaximumDescendantDepth = 16;
    private const int MaximumMessages = 50;
    private const int MaximumPromptCharacters = 16 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(12);
    private static readonly object MessageIdGate = new();
    private static long _lastMessageTimestamp;
    private static long _messageCounter;

    private readonly Uri _serverUri;
    private readonly string _directory;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string? _authorization;
    private bool _disposed;

    public OpenCodeRecoveryClient(Uri serverUri, string directory, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!serverUri.IsAbsoluteUri || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The OpenCode server URI must be an absolute HTTP URI.", nameof(serverUri));
        if (!serverUri.IsLoopback)
            throw new ArgumentException("The OpenCode recovery server must use a loopback address.", nameof(serverUri));
        if (!string.IsNullOrEmpty(serverUri.UserInfo))
            throw new ArgumentException("Credentials must be supplied through OpenCode server environment settings.", nameof(serverUri));
        if (!string.IsNullOrEmpty(serverUri.Query) || !string.IsNullOrEmpty(serverUri.Fragment))
            throw new ArgumentException("The OpenCode server URI cannot include a query or fragment.", nameof(serverUri));

        _serverUri = serverUri;
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
    /// Creates the same ascending, time-sortable message ID shape used by
    /// OpenCode 1.18.31 (`Identifier.ascending("message")`).
    /// </summary>
    public static string CreateMessageId()
    {
        long timestamp;
        long counter;
        lock (MessageIdGate)
        {
            long current = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            timestamp = Math.Max(current, _lastMessageTimestamp);
            if (timestamp == _lastMessageTimestamp)
                _messageCounter++;
            else
                _messageCounter = 1;
            _lastMessageTimestamp = timestamp;
            counter = _messageCounter;
        }

        ulong encoded = checked((ulong)timestamp * 0x1000UL + (ulong)counter);
        Span<byte> timeBytes = stackalloc byte[6];
        for (int index = 5; index >= 0; index--)
        {
            timeBytes[index] = (byte)(encoded & 0xff);
            encoded >>= 8;
        }

        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        byte[] randomBytes = RandomNumberGenerator.GetBytes(14);
        Span<char> randomSuffix = stackalloc char[14];
        for (int index = 0; index < randomSuffix.Length; index++)
            randomSuffix[index] = alphabet[randomBytes[index] % alphabet.Length];
        return "msg_" + Convert.ToHexStringLower(timeBytes) + randomSuffix.ToString();
    }

    /// <summary>Reads fresh evidence for one listed, exact, root session.</summary>
    public async Task<OpenCodeRecoveryReadResult> ReadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(ReadTimeout);
        CancellationToken token = operationTimeout.Token;

        try
        {
            JsonElement listedSession;
            JsonElement detailedSession;
            using (JsonDocument listDocument = await GetJsonAsync(
                       BuildEndpoint("/session", query: $"limit={MaximumSessions.ToString(CultureInfo.InvariantCulture)}"),
                       token,
                       MaximumSessionListBytes).ConfigureAwait(false))
            {
                JsonElement list = RequireArray(listDocument.RootElement, "session list");
                JsonElement[] matching = list.EnumerateArray()
                    .Where(item => StringEquals(GetString(item, "id"), sessionId))
                    .ToArray();
                if (matching.Length == 0)
                    return Unavailable("session_missing", "The exact session ID is not present in the directory-scoped session list.");
                if (matching.Length != 1)
                    return Unavailable("session_ambiguous", "The directory-scoped session list returned the exact session ID more than once.");
                listedSession = matching[0].Clone();
            }

            using (JsonDocument detailDocument = await GetJsonAsync(BuildSessionEndpoint(sessionId), token).ConfigureAwait(false))
                detailedSession = detailDocument.RootElement.Clone();

            if (!ValidateSessionIdentity(listedSession, sessionId, out string? identityError) ||
                !ValidateSessionIdentity(detailedSession, sessionId, out identityError))
                return Unavailable("identity_mismatch", identityError ?? "The session identity does not match the requested root session.");

            if (GetString(listedSession, "parentID") is not null || GetString(detailedSession, "parentID") is not null)
                return Unavailable("not_root_session", "Recovery is restricted to an exact root session.");

            string? parentSessionId = GetString(detailedSession, "parentID");
            string sessionDirectory = GetString(detailedSession, "directory")!;

            List<(string Id, int Depth)> descendants = await ReadDescendantsAsync(sessionId, token).ConfigureAwait(false);
            HashSet<string> sessionIds = new(StringComparer.Ordinal) { sessionId };
            foreach ((string descendantId, _) in descendants)
                sessionIds.Add(descendantId);

            Task<JsonDocument> statusTask = GetJsonAsync(BuildEndpoint("/session/status"), token);
            Task<JsonDocument> todoTask = GetJsonAsync(BuildSessionEndpoint(sessionId, "/todo"), token);
            Task<JsonDocument> messagesTask = GetJsonAsync(
                BuildSessionEndpoint(sessionId, "/message", $"limit={MaximumMessages.ToString(CultureInfo.InvariantCulture)}"),
                token,
                MaximumJsonPayloadBytes);
            Task<JsonDocument> permissionTask = GetJsonAsync(BuildEndpoint("/permission"), token);
            Task<JsonDocument> questionTask = GetJsonAsync(BuildEndpoint("/question"), token);

            await Task.WhenAll(statusTask, todoTask, messagesTask, permissionTask, questionTask).ConfigureAwait(false);
            using JsonDocument statusDocument = await statusTask.ConfigureAwait(false);
            using JsonDocument todoDocument = await todoTask.ConfigureAwait(false);
            using JsonDocument messagesDocument = await messagesTask.ConfigureAwait(false);
            using JsonDocument permissionDocument = await permissionTask.ConfigureAwait(false);
            using JsonDocument questionDocument = await questionTask.ConfigureAwait(false);

            JsonElement statuses = RequireObject(statusDocument.RootElement, "session status map");
            JsonElement rootStatus = statuses.TryGetProperty(sessionId, out JsonElement directStatus)
                ? directStatus
                : default;
            OpenCodeRecoveryStatus status = rootStatus.ValueKind == JsonValueKind.Undefined
                ? OpenCodeRecoveryStatus.Idle
                : ParseStatus(rootStatus);
            if (status == OpenCodeRecoveryStatus.Unknown)
                return Unavailable("invalid_status", "OpenCode returned an unknown status for the exact session.");

            JsonElement todosElement = RequireArray(todoDocument.RootElement, "session todos");
            (int incompleteTodos, int totalTodos, int completedTodos, string todoFingerprint) = ParseTodos(todosElement);

            JsonElement messagesElement = RequireArray(messagesDocument.RootElement, "session messages");
            JsonElement[] messageRows = messagesElement.EnumerateArray().ToArray();
            if (!messageRows.Any(row => GetString(GetProperty(row, "info"), "role") == "user") &&
                messageRows.Length == MaximumMessages)
            {
                string? parentUserId = GetTruncatedAssistantParentId(messageRows, sessionId);
                if (parentUserId is null)
                    return Unavailable("current_user_unavailable", "The capped assistant-message window does not identify one exact current user parent.");
                JsonElement parentUser = await ReadExactUserParentAsync(sessionId, parentUserId, token).ConfigureAwait(false);
                messageRows = [parentUser, .. messageRows];
            }
            MessageEvidence messages = ParseMessages(messageRows, sessionId, _directory);

            HashSet<string> pendingPermissionIds = GetPendingSessionIds(permissionDocument.RootElement, "permissions");
            HashSet<string> pendingQuestionIds = GetPendingSessionIds(questionDocument.RootElement, "questions");
            bool hasPendingPermission = pendingPermissionIds.Contains(sessionId);
            bool hasPendingQuestion = pendingQuestionIds.Contains(sessionId);
            bool hasPendingPermissionInDescendants = pendingPermissionIds.Any(id => sessionIds.Contains(id) && id != sessionId);
            bool hasPendingQuestionInDescendants = pendingQuestionIds.Any(id => sessionIds.Contains(id) && id != sessionId);

            bool hasBusyDescendants = false;
            foreach ((string descendantId, _) in descendants)
            {
                if (!statuses.TryGetProperty(descendantId, out JsonElement descendantStatus))
                    continue; // The v1 status map omits idle sessions.
                if (ParseStatus(descendantStatus) is OpenCodeRecoveryStatus.Busy or OpenCodeRecoveryStatus.Retry)
                    hasBusyDescendants = true;
                else if (ParseStatus(descendantStatus) == OpenCodeRecoveryStatus.Unknown)
                    return Unavailable("invalid_descendant_status", "OpenCode returned an unknown descendant status.");
            }

            string progressFingerprint = CreateProgressFingerprint(messages.CompletedToolEvidence, completedTodos);
            var evidence = new RecoverySessionEvidence(
                sessionId,
                status,
                parentSessionId,
                sessionDirectory,
                hasPendingPermission,
                hasPendingQuestion,
                hasPendingPermissionInDescendants,
                hasPendingQuestionInDescendants,
                incompleteTodos,
                totalTodos,
                todoFingerprint,
                progressFingerprint,
                messages.HasRunningTools,
                hasBusyDescendants,
                messages.LatestUserMessageId,
                messages.LatestUserMessageCreated,
                messages.LatestAssistantMessageId,
                messages.LatestMessageId,
                messages.HasErrorMarker,
                messages.LatestErrorType,
                status == OpenCodeRecoveryStatus.Retry ? GetInt32(rootStatus, "attempt") : null,
                status == OpenCodeRecoveryStatus.Retry ? GetString(rootStatus, "message") : null,
                status == OpenCodeRecoveryStatus.Retry ? GetInt64(rootStatus, "next") : null)
            {
                AllTodosCompleted = totalTodos > 0 && todosElement.EnumerateArray().All(todo => GetString(todo, "status") == "completed"),
                HasSuccessfulFinalResponse = messages.HasSuccessfulFinalResponse,
                CompletedTodoCount = completedTodos,
                ActivityFingerprint = messages.ActivityFingerprint,
                StalledLaunchToolFingerprint = messages.StalledLaunchToolFingerprint,
                AbortCancellationToolFingerprint = messages.AbortCancellationToolFingerprint,
                CurrentErrors = messages.CurrentErrors
            };
            return new OpenCodeRecoveryReadResult(true, evidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Unavailable("timeout", "The OpenCode recovery read exceeded its bounded timeout.");
        }
        catch (RecoveryProtocolException exception)
        {
            return Unavailable(exception.Code, exception.Message);
        }
        catch (HttpRequestException exception)
        {
            return Unavailable("disconnected", "The OpenCode loopback server could not complete the recovery read.", exception);
        }
        catch (IOException exception)
        {
            return Unavailable("invalid_response", "The OpenCode response could not be read safely.", exception);
        }
        catch (JsonException exception)
        {
            return Unavailable("invalid_response", "OpenCode returned malformed recovery JSON.", exception);
        }
    }

    /// <summary>
    /// Sends a continuation into the same session. The supplied user message ID is
    /// checked before POST and reconciled after a lost response; the request is never retried.
    /// </summary>
    public async Task<OpenCodeRecoveryContinueResult> ContinueAsync(
        string sessionId,
        string prompt,
        string messageId,
        CancellationToken cancellationToken = default,
        string? expectedUserMessageId = null,
        bool allowAbortedTurn = false,
        Func<bool>? canSend = null,
        string? expectedProgressFingerprint = null,
        string? expectedTodoFingerprint = null,
        bool allowUntrackedWork = false,
        string? expectedActivityFingerprint = null,
        string? expectedStalledLaunchToolFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ValidateMessageId(messageId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (prompt.Length > MaximumPromptCharacters)
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "payload_too_large", "The continuation exceeds the bounded prompt size.");

        OpenCodeRecoveryReadResult read = await ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!read.IsAvailable || read.Evidence is null)
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, read.ErrorCode ?? "identity_unavailable", read.Error ?? "The exact session could not be verified.");
        RecoverySessionEvidence evidence = read.Evidence;
        if (expectedUserMessageId is not null && !StringEquals(evidence.LatestUserMessageId, expectedUserMessageId))
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "user_message_changed", "The latest user message no longer matches the recovery target.");
        if (expectedProgressFingerprint is not null &&
            !StringEquals(evidence.ProgressFingerprint, expectedProgressFingerprint))
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "progress_changed", "Substantive native progress changed after the recovery decision.");
        if (expectedTodoFingerprint is not null && !StringEquals(evidence.TodoFingerprint, expectedTodoFingerprint))
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "progress_changed", "Native todos changed after the recovery decision.");
        if (expectedActivityFingerprint is not null && !StringEquals(evidence.ActivityFingerprint, expectedActivityFingerprint))
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "progress_changed", "Current-turn native activity changed after the recovery decision.");

        bool expectedAbort = allowAbortedTurn && expectedUserMessageId is not null &&
            StringEquals(evidence.LatestUserMessageId, expectedUserMessageId) &&
            IsExpectedAbortError(evidence, expectedStalledLaunchToolFingerprint);
        if (evidence.Status != OpenCodeRecoveryStatus.Idle ||
            evidence.HasPendingPermission || evidence.HasPendingQuestion ||
            evidence.HasPendingPermissionInDescendants || evidence.HasPendingQuestionInDescendants ||
            evidence.HasBusyDescendants || evidence.HasRunningTools ||
            (evidence.IncompleteTodoCount == 0 && !CanAllowUntrackedWork(evidence, allowUntrackedWork, expectedUserMessageId)) ||
            (evidence.HasErrorMarker && !expectedAbort))
        {
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "session_not_ready", "The exact session or one of its descendants is active or waiting for input.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var preflightTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preflightTimeout.CancelAfter(RequestTimeout);
            ExistingMessage existing = await ReadMessageByIdAsync(sessionId, messageId, preflightTimeout.Token).ConfigureAwait(false);
            if (existing.Exists)
            {
                if (existing.IsExactUserMessage)
                    return ContinueResult(OpenCodeRecoveryOutcome.Accepted, messageId, "message_already_present", "The exact user message ID is already present; no POST was sent.");
                return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "message_id_conflict", "The supplied message ID already belongs to another message.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or RecoveryProtocolException or IOException or JsonException or OperationCanceledException)
        {
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "message_preflight_failed", "The user message ID could not be checked; no prompt was sent.", exception);
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageID = messageId,
            parts = new[] { new { type = "text", text = prompt } }
        });
        if (payload.Length > MaximumJsonPayloadBytes)
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "payload_too_large", "The continuation request exceeds the bounded payload size.");

        // Message-ID lookup performs another network round trip, so refresh the
        // approved evidence after it and immediately before the POST.
        OpenCodeRecoveryReadResult finalRead = await ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!finalRead.IsAvailable || finalRead.Evidence is null)
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, finalRead.ErrorCode ?? "identity_unavailable", finalRead.Error ?? "The exact session could not be verified.");
        RecoverySessionEvidence finalEvidence = finalRead.Evidence;
        if (expectedUserMessageId is not null && !StringEquals(finalEvidence.LatestUserMessageId, expectedUserMessageId))
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "user_message_changed", "The latest user message changed before dispatch.");
        if (expectedProgressFingerprint is not null &&
            !StringEquals(finalEvidence.ProgressFingerprint, expectedProgressFingerprint))
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "progress_changed", "Substantive native progress changed before dispatch.");
        if (expectedTodoFingerprint is not null && !StringEquals(finalEvidence.TodoFingerprint, expectedTodoFingerprint))
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "progress_changed", "Native todos changed before dispatch.");
        if (expectedActivityFingerprint is not null && !StringEquals(finalEvidence.ActivityFingerprint, expectedActivityFingerprint))
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "progress_changed", "Current-turn native activity changed before dispatch.");
        bool finalExpectedAbort = allowAbortedTurn && expectedUserMessageId is not null &&
            StringEquals(finalEvidence.LatestUserMessageId, expectedUserMessageId) &&
            IsExpectedAbortError(finalEvidence, expectedStalledLaunchToolFingerprint);
        bool allowFinalUntrackedWork = CanAllowUntrackedWork(finalEvidence, allowUntrackedWork, expectedUserMessageId);
        if (!IsContinuationReady(finalEvidence, finalExpectedAbort, allowFinalUntrackedWork))
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "session_not_ready", "The exact session or one of its descendants became active or blocked before dispatch.");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildSessionEndpoint(sessionId, "/prompt_async"));
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (canSend is not null && !canSend())
            return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "recovery_guard_changed", "Recovery ownership, project context, or the target turn changed before dispatch.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return ContinueResult(OpenCodeRecoveryOutcome.Accepted, messageId);
            if ((int)response.StatusCode is >= 400 and < 500)
                return ContinueResult(OpenCodeRecoveryOutcome.Rejected, messageId, "http_rejected", $"OpenCode rejected the continuation with HTTP {(int)response.StatusCode}.");

            bool confirmed = await TryConfirmMessageAsync(sessionId, messageId, cancellationToken).ConfigureAwait(false);
            return confirmed
                ? ContinueResult(OpenCodeRecoveryOutcome.Accepted, messageId, "message_confirmed", "The exact user message is present after an uncertain HTTP response.")
                : ContinueResult(OpenCodeRecoveryOutcome.Ambiguous, messageId, "response_ambiguous", "The continuation response was uncertain and the message could not be confirmed; it was not resent.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ContinueResult(OpenCodeRecoveryOutcome.Ambiguous, messageId, "cancelled_after_send", "The request was cancelled after dispatch may have begun; verify this message ID before taking further action.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            bool confirmed = await TryConfirmMessageAsync(sessionId, messageId, cancellationToken).ConfigureAwait(false);
            return confirmed
                ? ContinueResult(OpenCodeRecoveryOutcome.Accepted, messageId, "message_confirmed", "The exact user message is present after the HTTP response was lost.")
                : ContinueResult(OpenCodeRecoveryOutcome.Ambiguous, messageId, "response_ambiguous", "The continuation response was lost and the message could not be confirmed; it was not resent.", exception);
        }
    }

    /// <summary>Aborts the verified exact root session using OpenCode's native abort endpoint.</summary>
    public async Task<OpenCodeRecoveryActionResult> AbortAsync(
        string sessionId,
        CancellationToken cancellationToken = default,
        string? expectedUserMessageId = null,
        Func<bool>? canSend = null,
        string? expectedProgressFingerprint = null,
        string? expectedTodoFingerprint = null,
        bool allowUntrackedWork = false,
        string? expectedActivityFingerprint = null,
        string? expectedStalledLaunchToolFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        OpenCodeRecoveryReadResult read = await ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!read.IsAvailable || read.Evidence is null)
            return ActionResult(OpenCodeRecoveryOutcome.Rejected, read.ErrorCode ?? "identity_unavailable", read.Error ?? "The exact session could not be verified.");
        RecoverySessionEvidence evidence = read.Evidence;
        if (expectedUserMessageId is not null && !StringEquals(evidence.LatestUserMessageId, expectedUserMessageId))
            return ActionResult(OpenCodeRecoveryOutcome.Rejected, "user_message_changed", "The latest user message no longer matches the abort target.");
        if (expectedProgressFingerprint is not null && !StringEquals(evidence.ProgressFingerprint, expectedProgressFingerprint) ||
            expectedTodoFingerprint is not null && !StringEquals(evidence.TodoFingerprint, expectedTodoFingerprint) ||
            expectedActivityFingerprint is not null && !StringEquals(evidence.ActivityFingerprint, expectedActivityFingerprint))
            return ActionResult(OpenCodeRecoveryOutcome.Rejected, "progress_changed", "Native progress or current-turn activity changed after the recovery decision.");
        if (!IsAbortable(evidence, CanAllowUntrackedWork(evidence, allowUntrackedWork, expectedUserMessageId), expectedStalledLaunchToolFingerprint))
            return ActionResult(OpenCodeRecoveryOutcome.Rejected, "session_not_abortable", "The exact session is not a stalled busy turn with incomplete work and no active tool, descendant, error, or input blocker.");

        // Re-read immediately before abort so newly observed activity disarms it.
        OpenCodeRecoveryReadResult finalRead = await ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!finalRead.IsAvailable || finalRead.Evidence is null)
            return ActionResult(OpenCodeRecoveryOutcome.Rejected, finalRead.ErrorCode ?? "identity_unavailable", finalRead.Error ?? "The exact session could not be verified.");
        RecoverySessionEvidence finalEvidence = finalRead.Evidence;
        if (expectedUserMessageId is not null && !StringEquals(finalEvidence.LatestUserMessageId, expectedUserMessageId))
            return ActionResult(OpenCodeRecoveryOutcome.Rejected, "user_message_changed", "The latest user message changed before abort.");
        if (expectedProgressFingerprint is not null && !StringEquals(finalEvidence.ProgressFingerprint, expectedProgressFingerprint) ||
            expectedTodoFingerprint is not null && !StringEquals(finalEvidence.TodoFingerprint, expectedTodoFingerprint) ||
            expectedActivityFingerprint is not null && !StringEquals(finalEvidence.ActivityFingerprint, expectedActivityFingerprint))
            return ActionResult(OpenCodeRecoveryOutcome.Rejected, "progress_changed", "Native progress or current-turn activity changed before abort.");
        if (!IsAbortable(finalEvidence, CanAllowUntrackedWork(finalEvidence, allowUntrackedWork, expectedUserMessageId), expectedStalledLaunchToolFingerprint))
            return ActionResult(OpenCodeRecoveryOutcome.Rejected, "session_not_abortable", "The exact session is no longer a stalled busy turn with no active blocker.");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildSessionEndpoint(sessionId, "/abort"));
        request.Content = new ByteArrayContent("{}"u8.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (canSend is not null && !canSend())
            return ActionResult(OpenCodeRecoveryOutcome.Rejected, "recovery_guard_changed", "Recovery ownership or project context changed before abort dispatch.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var actionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            actionTimeout.CancelAfter(RequestTimeout);
            using HttpResponseMessage response = await SendAsync(request, actionTimeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                byte[] responseBody = await ReadBoundedAsync(
                    await response.Content.ReadAsStreamAsync(actionTimeout.Token).ConfigureAwait(false),
                    1024,
                    actionTimeout.Token).ConfigureAwait(false);
                if (responseBody.Length == 0 || !bool.TryParse(Encoding.UTF8.GetString(responseBody), out bool aborted) || !aborted)
                    return ActionResult(OpenCodeRecoveryOutcome.Rejected, "abort_not_confirmed", "OpenCode did not confirm that the exact session was aborted.");
                return ActionResult(OpenCodeRecoveryOutcome.Accepted);
            }
            if ((int)response.StatusCode is >= 400 and < 500)
                return ActionResult(OpenCodeRecoveryOutcome.Rejected, "http_rejected", $"OpenCode rejected the abort with HTTP {(int)response.StatusCode}.");
            return ActionResult(OpenCodeRecoveryOutcome.Ambiguous, "response_ambiguous", "The abort response was uncertain.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult(OpenCodeRecoveryOutcome.Ambiguous, "cancelled_after_send", "The abort request may have reached OpenCode before cancellation.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return ActionResult(OpenCodeRecoveryOutcome.Ambiguous, "response_ambiguous", "The abort response was lost.", exception);
        }
    }

    private static bool CanAllowUntrackedWork(RecoverySessionEvidence evidence, bool allowUntrackedWork, string? expectedUserMessageId) =>
        allowUntrackedWork && !string.IsNullOrWhiteSpace(expectedUserMessageId) &&
        StringEquals(evidence.LatestUserMessageId, expectedUserMessageId) && !evidence.HasSuccessfulFinalResponse;

    private static bool IsAbortable(RecoverySessionEvidence evidence, bool allowUntrackedWork, string? expectedLaunchToolFingerprint) =>
        evidence.Status == OpenCodeRecoveryStatus.Busy && (evidence.IncompleteTodoCount > 0 || allowUntrackedWork) &&
        !evidence.HasPendingPermission && !evidence.HasPendingQuestion &&
        !evidence.HasPendingPermissionInDescendants && !evidence.HasPendingQuestionInDescendants &&
        (!evidence.HasRunningTools || expectedLaunchToolFingerprint is not null &&
            StringEquals(evidence.StalledLaunchToolFingerprint, expectedLaunchToolFingerprint)) &&
        !evidence.HasBusyDescendants && !evidence.HasErrorMarker;

    internal static bool IsExpectedAbortError(RecoverySessionEvidence evidence, string? expectedLaunchFingerprint) =>
        evidence.CurrentErrors.Count > 0 && evidence.CurrentErrors.All(error =>
            StringEquals(error.Type, "MessageAbortedError") ||
            expectedLaunchFingerprint is not null && StringEquals(error.Type, "ToolExecutionAborted") &&
            StringEquals(error.ToolFingerprint, expectedLaunchFingerprint));
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private static bool IsContinuationReady(RecoverySessionEvidence evidence, bool allowExpectedAbort, bool allowUntrackedWork) =>
        evidence.Status == OpenCodeRecoveryStatus.Idle &&
        !evidence.HasPendingPermission && !evidence.HasPendingQuestion &&
        !evidence.HasPendingPermissionInDescendants && !evidence.HasPendingQuestionInDescendants &&
        !evidence.HasBusyDescendants && !evidence.HasRunningTools && (evidence.IncompleteTodoCount > 0 || allowUntrackedWork) &&
        (!evidence.HasErrorMarker || allowExpectedAbort);

    private async Task<List<(string Id, int Depth)>> ReadDescendantsAsync(string rootSessionId, CancellationToken cancellationToken)
    {
        var result = new List<(string Id, int Depth)>();
        var pending = new Queue<(string Id, int Depth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { rootSessionId };
        pending.Enqueue((rootSessionId, 0));

        while (pending.Count > 0)
        {
            (string parentId, int depth) = pending.Dequeue();
            if (depth >= MaximumDescendantDepth)
                throw new RecoveryProtocolException("descendant_limit", "The session tree exceeds the bounded recovery depth.");

            using JsonDocument childDocument = await GetJsonAsync(BuildSessionEndpoint(parentId, "/children"), cancellationToken).ConfigureAwait(false);
            JsonElement children = RequireArray(childDocument.RootElement, "session children");
            foreach (JsonElement child in children.EnumerateArray())
            {
                string? childId = GetString(child, "id");
                string? actualParentId = GetString(child, "parentID");
                if (string.IsNullOrWhiteSpace(childId) || !StringEquals(actualParentId, parentId))
                    throw new RecoveryProtocolException("invalid_child_identity", "A child session did not match its requested parent.");
                if (!ValidateSessionIdentity(child, childId, out string? error))
                    throw new RecoveryProtocolException("invalid_child_identity", error ?? "A child session did not match the configured directory.");
                if (!seen.Add(childId))
                    throw new RecoveryProtocolException("ambiguous_child_tree", "The OpenCode session tree contains a duplicate or cyclic child ID.");
                if (result.Count >= MaximumDescendants)
                    throw new RecoveryProtocolException("descendant_limit", "The session tree exceeds the bounded recovery session count.");

                int childDepth = depth + 1;
                result.Add((childId, childDepth));
                pending.Enqueue((childId, childDepth));
            }
        }

        return result;
    }

    private bool ValidateSessionIdentity(JsonElement session, string expectedSessionId, out string? error)
    {
        string? actualId = GetString(session, "id");
        string? actualDirectory = GetString(session, "directory");
        if (!StringEquals(actualId, expectedSessionId))
        {
            error = "OpenCode returned a different session ID than the one requested.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(actualDirectory) || !DirectoriesEqual(actualDirectory, _directory))
        {
            error = "OpenCode returned a session outside the configured project directory.";
            return false;
        }
        error = null;
        return true;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri endpoint, CancellationToken cancellationToken, int maximumBytes = MaximumJsonPayloadBytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new RecoveryProtocolException("not_found", "An exact OpenCode recovery resource was not found.");
        if (!response.IsSuccessStatusCode)
            throw new RecoveryProtocolException("http_error", $"OpenCode returned HTTP {(int)response.StatusCode} for a recovery read.");
        if (response.RequestMessage?.RequestUri is Uri finalUri && !IsAllowedFinalUri(finalUri))
            throw new RecoveryProtocolException("redirect_rejected", "OpenCode redirected a loopback recovery request to another address.");

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = await ReadBoundedAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException exception)
        {
            throw new RecoveryProtocolException("invalid_json", "OpenCode returned malformed recovery JSON.", exception);
        }
    }

    private async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > maximumBytes)
                throw new RecoveryProtocolException("payload_too_large", "OpenCode returned a response that exceeds the bounded payload size.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.RequestUri is null || !IsAllowedFinalUri(request.RequestUri))
            throw new RecoveryProtocolException("non_loopback_rejected", "A recovery request must stay on the configured loopback server.");
        if (_authorization is not null)
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(_authorization);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(RequestTimeout);
        HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is Uri finalUri && !IsAllowedFinalUri(finalUri))
        {
            response.Dispose();
            throw new RecoveryProtocolException("redirect_rejected", "OpenCode redirected a loopback recovery request to another address.");
        }
        return response;
    }

    private static string? GetTruncatedAssistantParentId(IEnumerable<JsonElement> messages, string sessionId)
    {
        string? parentId = null;
        foreach (JsonElement row in messages)
        {
            JsonElement info = GetProperty(row, "info");
            string? rowParent = GetString(info, "parentID");
            if (GetString(info, "role") != "assistant" ||
                !StringEquals(GetString(info, "sessionID"), sessionId) ||
                string.IsNullOrWhiteSpace(rowParent) ||
                parentId is not null && !StringEquals(parentId, rowParent))
                return null;
            parentId ??= rowParent;
        }
        return parentId;
    }

    private async Task<JsonElement> ReadExactUserParentAsync(string sessionId, string parentId, CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetJsonAsync(
            BuildSessionEndpoint(sessionId, "/message/" + Uri.EscapeDataString(parentId)),
            cancellationToken,
            MaximumJsonPayloadBytes).ConfigureAwait(false);
        JsonElement message = RequireObject(document.RootElement, "exact parent user message");
        JsonElement info = RequireObject(GetProperty(message, "info"), "exact parent user metadata");
        if (!StringEquals(GetString(info, "id"), parentId) ||
            !StringEquals(GetString(info, "sessionID"), sessionId) ||
            GetString(info, "role") != "user")
            throw new RecoveryProtocolException("invalid_current_user_parent", "The exact assistant parent was not a user message in the requested session.");
        _ = RequireArray(GetProperty(message, "parts"), "exact parent user message parts");
        return message.Clone();
    }
    private async Task<ExistingMessage> ReadMessageByIdAsync(string sessionId, string messageId, CancellationToken cancellationToken)
    {
        Uri endpoint = BuildSessionEndpoint(sessionId, "/message/" + Uri.EscapeDataString(messageId));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ExistingMessage(false, false);
        if (!response.IsSuccessStatusCode)
            throw new RecoveryProtocolException("message_lookup_failed", $"OpenCode returned HTTP {(int)response.StatusCode} while checking a message ID.");

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = await ReadBoundedAsync(stream, MaximumJsonPayloadBytes, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
        JsonElement info = GetProperty(document.RootElement, "info");
        return new ExistingMessage(
            true,
            StringEquals(GetString(info, "id"), messageId) &&
            StringEquals(GetString(info, "sessionID"), sessionId) &&
            StringEquals(GetString(info, "role"), "user"));
    }

    private async Task<bool> TryConfirmMessageAsync(string sessionId, string messageId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;
        using var confirmTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        confirmTimeout.CancelAfter(RequestTimeout);
        try
        {
            ExistingMessage existing = await ReadMessageByIdAsync(sessionId, messageId, confirmTimeout.Token).ConfigureAwait(false);
            return existing.IsExactUserMessage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or RecoveryProtocolException or IOException or JsonException or OperationCanceledException)
        {
            return false;
        }
    }

    private Uri BuildEndpoint(string path, string? query = null)
    {
        string root = _serverUri.GetLeftPart(UriPartial.Authority) + _serverUri.AbsolutePath.TrimEnd('/');
        string uri = root + path;
        List<string> queryParts = new();
        if (query is not null)
            queryParts.Add(query);
        queryParts.Add("directory=" + Uri.EscapeDataString(_directory));
        return new Uri(uri + "?" + string.Join("&", queryParts), UriKind.Absolute);
    }

    private Uri BuildSessionEndpoint(string sessionId, string suffix = "", string? query = null) =>
        BuildEndpoint("/session/" + Uri.EscapeDataString(sessionId) + suffix, query);

    private bool IsAllowedFinalUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.IsLoopback &&
        string.Equals(uri.Scheme, _serverUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, _serverUri.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == _serverUri.Port;

    private static string? CreateAuthorizationHeader()
    {
        string? password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        if (string.IsNullOrEmpty(password))
            return null;
        string username = Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
    }

    private static bool DirectoriesEqual(string left, string right)
    {
        try
        {
            string leftFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            string rightFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(leftFull, rightFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static JsonElement RequireArray(JsonElement element, string resource) =>
        element.ValueKind == JsonValueKind.Array
            ? element
            : throw new RecoveryProtocolException("invalid_response", $"OpenCode returned an invalid {resource} response.");

    private static JsonElement RequireObject(JsonElement element, string resource) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw new RecoveryProtocolException("invalid_response", $"OpenCode returned an invalid {resource} response.");

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out JsonElement value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static JsonElement GetProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement value)
            ? value
            : default;

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        JsonElement value = GetProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        JsonElement value = GetProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) ? result : null;
    }

    private static OpenCodeRecoveryStatus ParseStatus(JsonElement status)
    {
        string? type = GetString(status, "type");
        return type switch
        {
            "idle" => OpenCodeRecoveryStatus.Idle,
            "busy" => OpenCodeRecoveryStatus.Busy,
            "retry" => OpenCodeRecoveryStatus.Retry,
            _ => OpenCodeRecoveryStatus.Unknown
        };
    }

    private static (int Incomplete, int Total, int Completed, string Fingerprint) ParseTodos(JsonElement todos)
    {
        int incomplete = 0;
        int total = 0;
        int completed = 0;
        StringBuilder material = new();
        foreach (JsonElement todo in todos.EnumerateArray())
        {
            string? content = GetString(todo, "content");
            string? status = GetString(todo, "status");
            string? priority = GetString(todo, "priority");
            if (content is null || status is null || priority is null)
                throw new RecoveryProtocolException("invalid_todos", "OpenCode returned a todo without its required content, status, or priority.");
            if (status is not ("pending" or "in_progress" or "completed" or "cancelled"))
                throw new RecoveryProtocolException("invalid_todos", "OpenCode returned a todo with an unknown status.");
            total++;
            if (status is "pending" or "in_progress")
                incomplete++;
            if (status == "completed") completed++;
            AppendFingerprintField(material, content);
            AppendFingerprintField(material, status);
            AppendFingerprintField(material, priority);
        }
        return (incomplete, total, completed, Hash(material.ToString()));
    }

    private static MessageEvidence ParseMessages(IEnumerable<JsonElement> messages, string sessionId, string projectDirectory)
    {
        var parsed = new List<ParsedMessage>();
        var completedToolEvidence = new SortedSet<string>(StringComparer.Ordinal);
        var activeToolEvidence = new List<(string Name, string Status, JsonElement Input)>();
        int index = 0;

        foreach (JsonElement row in messages)
        {
            JsonElement info = GetProperty(row, "info");
            if (info.ValueKind != JsonValueKind.Object)
                throw new RecoveryProtocolException("invalid_messages", "OpenCode returned a message without message metadata.");
            string? id = GetString(info, "id");
            string? role = GetString(info, "role");
            string? actualSessionId = GetString(info, "sessionID");
            if (string.IsNullOrWhiteSpace(id) || (role != "user" && role != "assistant"))
                throw new RecoveryProtocolException("invalid_messages", "OpenCode returned a message without a recognized ID or role.");
            if (actualSessionId is not null && !StringEquals(actualSessionId, sessionId))
                throw new RecoveryProtocolException("message_session_mismatch", "OpenCode returned a message from a different session.");

            DateTimeOffset? created = ParseCreatedTime(GetProperty(GetProperty(info, "time"), "created"));
            int messageIndex = index++;
            bool messageHasError = false;
            bool messageHasRunningTools = false;
            string? messageErrorType = null;
            var rowErrors = new List<RecoveryErrorEvidence>();

            if (GetProperty(info, "error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                messageHasError = true;
                messageErrorType = GetString(GetProperty(info, "error"), "name") ?? GetString(GetProperty(info, "error"), "type") ?? "message_error";
                rowErrors.Add(new RecoveryErrorEvidence(messageErrorType, null));
            }

            JsonElement parts = GetProperty(row, "parts");
            if (parts.ValueKind != JsonValueKind.Array)
                throw new RecoveryProtocolException("invalid_messages", "OpenCode returned a message without a parts array.");
            parsed.Add(new ParsedMessage(id, role, created, messageIndex, messageHasError, messageErrorType, false)
            {
                Errors = rowErrors
            });
            if (role != "assistant")
                continue;

            StringBuilder activityEvidence = new();
            foreach (JsonElement part in parts.EnumerateArray())
            {
                string? partType = GetString(part, "type");
                if (partType is "text" or "reasoning")
                {
                    AppendFingerprintField(activityEvidence, partType);
                    AppendFingerprintField(activityEvidence, GetString(part, "text") ?? string.Empty);
                }
                else if (partType == "tool")
                {
                    JsonElement activityState = GetProperty(part, "state");
                    AppendFingerprintField(activityEvidence, "tool");
                    AppendFingerprintField(activityEvidence, GetString(part, "tool") ?? string.Empty);
                    AppendFingerprintField(activityEvidence, GetString(activityState, "status") ?? string.Empty);
                    JsonElement activityInput = GetProperty(activityState, "input");
                    AppendFingerprintField(activityEvidence, activityInput.ValueKind == JsonValueKind.Undefined ? string.Empty : activityInput.GetRawText());
                    JsonElement activityOutput = GetProperty(activityState, "output");
                    AppendFingerprintField(activityEvidence, activityOutput.ValueKind == JsonValueKind.Undefined ? string.Empty : activityOutput.GetRawText());
                }
                if (partType == "tool")
                {
                    JsonElement state = GetProperty(part, "state");
                    string? toolStatus = GetString(state, "status");
                    // Only structured tool evidence can show work between polls. Text,
                    // reasoning, timestamps, and repeated assistant snapshots do not
                    // reset the no-progress limit.
                    JsonElement input = GetProperty(state, "input");
                    if (toolStatus == "completed")
                        completedToolEvidence.Add((GetString(part, "tool") ?? string.Empty) + "\n" +
                            (input.ValueKind == JsonValueKind.Undefined ? string.Empty : input.GetRawText()));
                    if (toolStatus is "pending" or "running")
                        messageHasRunningTools = true;
                    if (toolStatus is "pending" or "running")
                        activeToolEvidence.Add((GetString(part, "tool") ?? string.Empty, toolStatus, input));
                    if (toolStatus == "error")
                    {
                        messageHasError = true;
                        bool isAbortCancellation = IsExactToolAbortOutput(state);
                        string errorType = isAbortCancellation ? "ToolExecutionAborted" : "tool_error";
                        messageErrorType ??= errorType;
                        string toolName = GetString(part, "tool") ?? string.Empty;
                        string? toolFingerprint = isAbortCancellation && input.ValueKind == JsonValueKind.Object
                            ? Hash(toolName + "\n" + input.GetRawText())
                            : null;
                        rowErrors.Add(new RecoveryErrorEvidence(errorType, toolFingerprint));
                    }
                }
            }
            parsed[^1] = parsed[^1] with
            {
                HasErrorMarker = messageHasError,
                ErrorType = messageErrorType,
                HasRunningTools = messageHasRunningTools,
                Errors = rowErrors,
                ParentId = GetString(info, "parentID"),
                ActivityEvidence = activityEvidence.ToString(),
                SuccessfulFinish = GetString(info, "finish") == "stop" && created is not null &&
                    ParseCreatedTime(GetProperty(GetProperty(info, "time"), "completed")) is { } completed && completed >= created &&
                    parts.EnumerateArray().Any(part => GetString(part, "type") == "text" && !string.IsNullOrWhiteSpace(GetString(part, "text")))
            };
        }

        ParsedMessage? latestUser = FindLatest(parsed, "user");
        ParsedMessage? latestAssistant = FindLatest(parsed, "assistant");
        ParsedMessage? latest = parsed.OrderBy(item => item.Created ?? DateTimeOffset.MinValue).ThenBy(item => item.Index).LastOrDefault();
        int activeTurnStart = latestUser?.Index ?? latestAssistant?.Index ?? int.MaxValue;
        ParsedMessage[] activeTurnAssistants = parsed
            .Where(item => item.Role == "assistant" && item.Index >= activeTurnStart)
            .ToArray();
        RecoveryErrorEvidence[] currentErrors = activeTurnAssistants.SelectMany(item => item.Errors).ToArray();
        bool hasRunningTools = activeTurnAssistants.Any(item => item.HasRunningTools);
        StringBuilder activityMaterial = new();
        foreach (ParsedMessage assistant in activeTurnAssistants)
            AppendFingerprintField(activityMaterial, assistant.ActivityEvidence);
        string activityFingerprint = Hash(activityMaterial.ToString());
        bool hasErrorMarker = currentErrors.Length > 0;
        string? latestErrorType = currentErrors.LastOrDefault()?.Type;
        RecoveryErrorEvidence[] abortErrors = currentErrors.Where(error => StringEquals(error.Type, "ToolExecutionAborted")).ToArray();
        string? stalledLaunchToolFingerprint = activeToolEvidence.Count == 1 && activeToolEvidence[0].Status == "running" &&
            IsGeneratedAppLaunch(activeToolEvidence[0].Name, activeToolEvidence[0].Input, projectDirectory)
                ? Hash(activeToolEvidence[0].Name + "\n" + activeToolEvidence[0].Input.GetRawText())
                : null;
        return new MessageEvidence(
            latestUser?.Id,
            latestUser?.Created,
            latestAssistant?.Id,
            latest?.Id,
            hasRunningTools,
            hasErrorMarker,
            latestErrorType,
            string.Join("\n", completedToolEvidence))
        {
            HasSuccessfulFinalResponse = latestAssistant is { SuccessfulFinish: true, HasErrorMarker: false } &&
                latestAssistant.ParentId == latestUser?.Id && latest?.Id == latestAssistant.Id && !hasRunningTools,
            ActivityFingerprint = activityFingerprint,
            StalledLaunchToolFingerprint = stalledLaunchToolFingerprint,
            AbortCancellationToolFingerprint = abortErrors.Length == 1 ? abortErrors[0].ToolFingerprint : null,
            CurrentErrors = currentErrors
        };
    }

    private static bool IsGeneratedAppLaunch(string toolName, JsonElement input, string projectDirectory)
    {
        if (toolName is not ("bash" or "shell" or "powershell")) return false;
        string? command = GetString(input, "command");
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4096 || command.Contains('\n') || command.Contains('\r')) return false;
        foreach (char forbidden in new[] { ';', '|', '&', '>', '<', '`', '$' })
            if (command.Contains(forbidden)) return false;

        string workingDirectory = projectDirectory;
        string? suppliedWorkdir = GetString(input, "workdir");
        if (suppliedWorkdir is not null)
        {
            if (!TryResolveContainedPath(projectDirectory, projectDirectory, suppliedWorkdir, out string resolvedWorkdir) ||
                !Directory.Exists(resolvedWorkdir)) return false;
            workingDirectory = resolvedWorkdir;
        }

        List<string> args = TokenizeSimpleCommand(command);
        if (args.Count == 0) return false;
        if (args.Count == 1 && args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            TryResolveContainedPath(projectDirectory, workingDirectory, args[0], out string resolvedExecutable) &&
            File.Exists(resolvedExecutable) && IsAppPath(projectDirectory, resolvedExecutable) &&
            IsProjectApplicationOutput(projectDirectory, resolvedExecutable))
            return true;

        if (args[0].IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || Path.IsPathRooted(args[0]))
            return false;
        string executable = Path.GetFileName(args[0]);
        if (executable.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 2 || !args[1].Equals("run", StringComparison.OrdinalIgnoreCase)) return false;
            string? projectFile = null;
            bool configurationSeen = false;
            for (int i = 2; i < args.Count; i++)
            {
                if (args[i].Equals("--project", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-p", StringComparison.OrdinalIgnoreCase))
                {
                    if (projectFile is not null || ++i >= args.Count) return false;
                    projectFile = args[i];
                }
                else if (args[i].Equals("--configuration", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-c", StringComparison.OrdinalIgnoreCase))
                {
                    if (configurationSeen || ++i >= args.Count || !IsSupportedAppConfiguration(args[i])) return false;
                    configurationSeen = true;
                }
                else if (args[i].StartsWith("--project=", StringComparison.OrdinalIgnoreCase))
                {
                    if (projectFile is not null || args[i].Length == "--project=".Length) return false;
                    projectFile = args[i]["--project=".Length..];
                }
                else if (args[i].StartsWith("--configuration=", StringComparison.OrdinalIgnoreCase))
                {
                    if (configurationSeen || !IsSupportedAppConfiguration(args[i]["--configuration=".Length..])) return false;
                    configurationSeen = true;
                }
                else if (args[i] is not ("--no-build" or "--no-restore")) return false;
            }
            if (projectFile is not null)
                return TryResolveContainedPath(projectDirectory, workingDirectory, projectFile, out string resolvedProject) &&
                    File.Exists(resolvedProject) && resolvedProject.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                    IsAppPath(projectDirectory, resolvedProject) && !IsTestOrBuildName(Path.GetFileNameWithoutExtension(resolvedProject));
            string[] projects = Directory.Exists(workingDirectory)
                ? Directory.GetFiles(workingDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
                : [];
            return projects.Length == 1 && IsAppPath(projectDirectory, projects[0]) &&
                !IsTestOrBuildName(Path.GetFileNameWithoutExtension(projects[0]));
        }

        if (executable.Equals("npm", StringComparison.OrdinalIgnoreCase) && args.Count == 3 &&
            args[1].Equals("run", StringComparison.OrdinalIgnoreCase) &&
            args[2] is "dev" or "start" or "preview")
        {
            string packagePath = Path.Combine(workingDirectory, "package.json");
            try
            {
                using JsonDocument package = JsonDocument.Parse(File.ReadAllText(packagePath));
                JsonElement scripts = GetProperty(package.RootElement, "scripts");
                return GetProperty(scripts, args[2]).ValueKind == JsonValueKind.String &&
                    !IsTestOrBuildName(GetString(scripts, args[2]) ?? string.Empty) && IsAppPath(projectDirectory, packagePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }

        if (args.Count != 2 || !(executable.Equals("node", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("node.exe", StringComparison.OrdinalIgnoreCase) || executable.Equals("python", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("python3", StringComparison.OrdinalIgnoreCase) || executable.Equals("python.exe", StringComparison.OrdinalIgnoreCase))) return false;
        string entry = args[1];
        string entryName = Path.GetFileNameWithoutExtension(entry);
        if (!entry.EndsWith(executable.StartsWith("python", StringComparison.OrdinalIgnoreCase) ? ".py" : ".js", StringComparison.OrdinalIgnoreCase) ||
            !entryName.Equals("app", StringComparison.OrdinalIgnoreCase) && !entryName.Equals("server", StringComparison.OrdinalIgnoreCase) &&
            !entryName.Equals("main", StringComparison.OrdinalIgnoreCase) && !entryName.Equals("index", StringComparison.OrdinalIgnoreCase) &&
            !entryName.Equals("web", StringComparison.OrdinalIgnoreCase)) return false;
        return TryResolveContainedPath(projectDirectory, workingDirectory, entry, out string resolvedEntry) &&
            File.Exists(resolvedEntry) && IsAppPath(projectDirectory, resolvedEntry);
    }

    private static bool IsSupportedAppConfiguration(string value) =>
        value.Equals("Debug", StringComparison.OrdinalIgnoreCase) || value.Equals("Release", StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectApplicationOutput(string projectDirectory, string executablePath)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(projectDirectory), Path.GetFullPath(executablePath));
        string[] parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        int outputIndex = Array.FindIndex(parts, part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("dist", StringComparison.OrdinalIgnoreCase) || part.Equals("out", StringComparison.OrdinalIgnoreCase));
        if (outputIndex < 0 || outputIndex >= parts.Length - 1)
            return false;
        string name = Path.GetFileNameWithoutExtension(executablePath);
        if (IsTestOrBuildName(name)) return false;
        return !new[] { "msbuild", "csc", "vbc", "testhost", "vstest", "vstest.console", "installer", "setup", "uninstall", "nuget" }
            .Any(excluded => name.Equals(excluded, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> TokenizeSimpleCommand(string command)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        bool started = false;
        foreach (char character in command)
        {
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else token.Append(character);
            }
            else if (character is '\'' or '"') { quote = character; started = true; }
            else if (char.IsWhiteSpace(character))
            {
                if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
            }
            else { token.Append(character); started = true; }
        }
        if (quote != '\0') return [];
        if (started) result.Add(token.ToString());
        return result;
    }

    private static bool TryResolveContainedPath(string projectDirectory, string workingDirectory, string candidate, out string resolved)
    {
        try
        {
            resolved = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(workingDirectory, candidate));
            return IsAppPath(projectDirectory, resolved);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            resolved = string.Empty;
            return false;
        }
    }

    private static bool IsAppPath(string projectDirectory, string candidate)
    {
        string root = Path.GetFullPath(projectDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(candidate);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, comparison) && !string.Equals(full, root.TrimEnd(Path.DirectorySeparatorChar), comparison)) return false;
        string relative = Path.GetRelativePath(root, full);
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IsTestOrBuildName)) return false;
        return HasNoReparsePointInPath(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), relative);
    }

    private static bool HasNoReparsePointInPath(string root, string relative)
    {
        try
        {
            string current = root;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            if (relative == ".") return true;
            foreach (string part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsTestOrBuildName(string value) =>
        value.Contains("test", StringComparison.OrdinalIgnoreCase) || value.Contains("spec", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("build", StringComparison.OrdinalIgnoreCase) || value.Contains("migration", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("training", StringComparison.OrdinalIgnoreCase);

    private static bool IsExactToolAbortOutput(JsonElement state)
    {
        string? output = GetString(state, "output");
        string? error = GetString(state, "error");
        return string.Equals(output?.Trim(), "Tool execution aborted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(error?.Trim(), "Tool execution aborted", StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedMessage? FindLatest(IEnumerable<ParsedMessage> messages, string role) =>
        messages.Where(item => item.Role == role)
            .OrderBy(item => item.Created ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Index)
            .LastOrDefault();

    private static DateTimeOffset? ParseCreatedTime(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long epochMilliseconds))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            return parsed;
        return null;
    }

    private static HashSet<string> GetPendingSessionIds(JsonElement requests, string resource)
    {
        JsonElement array = RequireArray(requests, resource);
        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (JsonElement item in array.EnumerateArray())
        {
            string? sessionId = GetString(item, "sessionID");
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new RecoveryProtocolException("invalid_pending_input", $"OpenCode returned a {resource} entry without a session ID.");
            result.Add(sessionId);
        }
        return result;
    }

    private static string CreateProgressFingerprint(string completedToolEvidence, int completedTodoCount)
    {
        StringBuilder material = new();
        AppendFingerprintField(material, completedTodoCount.ToString(CultureInfo.InvariantCulture));
        AppendFingerprintField(material, completedToolEvidence);
        return Hash(material.ToString());
    }

    private static void AppendFingerprintField(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');

    private static string Hash(string material) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    private static bool StringEquals(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    private static void ValidateMessageId(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (messageId.Length > 128 || !messageId.StartsWith("msg_", StringComparison.Ordinal) ||
            messageId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new ArgumentException("The OpenCode user message ID must be a bounded msg_ ID.", nameof(messageId));
    }

    private static OpenCodeRecoveryReadResult Unavailable(string code, string message, Exception? exception = null) =>
        new(false, null, code, exception is null ? message : message + " " + exception.GetType().Name + ".");

    private static OpenCodeRecoveryContinueResult ContinueResult(
        OpenCodeRecoveryOutcome outcome,
        string messageId,
        string? code = null,
        string? message = null,
        Exception? exception = null) =>
        new(outcome, messageId, code, exception is null ? message : message + " " + exception.GetType().Name + ".");

    private static OpenCodeRecoveryActionResult ActionResult(
        OpenCodeRecoveryOutcome outcome,
        string? code = null,
        string? message = null,
        Exception? exception = null) =>
        new(outcome, code, exception is null ? message : message + " " + exception.GetType().Name + ".");

    private sealed record ParsedMessage(
        string Id,
        string Role,
        DateTimeOffset? Created,
        int Index,
        bool HasErrorMarker,
        string? ErrorType,
        bool HasRunningTools)
    {
        public string? ParentId { get; init; }
        public string ActivityEvidence { get; init; } = string.Empty;
        public bool SuccessfulFinish { get; init; }
        public IReadOnlyList<RecoveryErrorEvidence> Errors { get; init; } = Array.Empty<RecoveryErrorEvidence>();
    }

    private sealed record MessageEvidence(
        string? LatestUserMessageId,
        DateTimeOffset? LatestUserMessageCreated,
        string? LatestAssistantMessageId,
        string? LatestMessageId,
        bool HasRunningTools,
        bool HasErrorMarker,
        string? LatestErrorType,
        string CompletedToolEvidence)
    {
        public bool HasSuccessfulFinalResponse { get; init; }
        public string ActivityFingerprint { get; init; } = string.Empty;
        public string? StalledLaunchToolFingerprint { get; init; }
        public string? AbortCancellationToolFingerprint { get; init; }
        public IReadOnlyList<RecoveryErrorEvidence> CurrentErrors { get; init; } = Array.Empty<RecoveryErrorEvidence>();
    }

    private sealed record ExistingMessage(bool Exists, bool IsExactUserMessage);

    private sealed class RecoveryProtocolException : Exception
    {
        public RecoveryProtocolException(string code, string message, Exception? innerException = null)
            : base(message, innerException) => Code = code;

        public string Code { get; }
    }
}
