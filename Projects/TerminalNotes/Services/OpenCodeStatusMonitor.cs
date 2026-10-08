using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerminalNotes.Services;

public enum OpenCodeStatusKind
{
    Unknown,
    Pending,
    Idle,
    Busy,
    NeedsInput
}

public enum OpenCodeStatusMonitorPhase
{
    NotMonitoring,
    Connecting,
    Checking,
    Monitoring,
    ReconnectWaiting
}

public sealed record OpenCodeStatusSnapshot(
    OpenCodeStatusKind Kind,
    bool HasCompletedTurn,
    string? SessionId = null,
    string? Detail = null);

public sealed record OpenCodeRecoverySignal(string Type, string SessionId, string? MessageId, DateTimeOffset? MessageCreatedAt, long Version);
public readonly record struct OpenCodeSubmissionObservation(long ConfirmationVersion, long Generation);

/// <summary>
/// Reads OpenCode's native status events from its loopback HTTP server. It never
/// infers a running or completed turn from terminal text or silence.
/// </summary>
public sealed class OpenCodeStatusMonitor : IDisposable
{
    private const int MaximumSseLineLength = 64 * 1024;
    private const int MaximumSseFrameLength = 256 * 1024;
    private static readonly TimeSpan BaselineTimeout = TerminalExecutionTiming.MetadataRequestTimeout;
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Uri _serverUri;
    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    internal TimeSpan? NativeStatusHandshakeTimeoutOverride { get; set; }
    internal TimeSpan? MetadataRequestTimeoutOverride { get; set; }
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string? _authorization;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, SessionExecutionStatus> _sessionStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sessionEventVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingInput> _pendingInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _pendingInputEventVersions = new(StringComparer.Ordinal);

    private OpenCodeStatusSnapshot _snapshot = new(OpenCodeStatusKind.Unknown, false, Detail: "Waiting for OpenCode status.");
    private Task? _readerTask;
    private long _eventVersion;
    private long _inputEventVersion;
    private int _connectionGeneration;
    private bool _started;
    private bool _disposed;
    private bool _connected;
    private OpenCodeStatusMonitorPhase _monitorPhase;
    private bool _hasBaseline;
    private bool _hasPendingSubmission;
    private bool _hasConfirmedApiBaseline;
    private bool _hasCompletedTurn;
    private bool _hasExplicitIdleEvidence;
    private string? _lastSessionId;
    private long _recoveryEventVersion;
    private long _submissionConfirmationVersion;
    private long _submissionGeneration;
    private string? _lastConfirmedSubmissionMessageKey;
    private string? _lastConnectionFailure;
    private HashSet<string>? _ownedSessions;
    private readonly Dictionary<string, string> _sessionParents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentRequestAssistantMessages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentRequestPartFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentRequestDeltaText = new(StringComparer.Ordinal);
    private string? _currentRequestSessionId;
    private string? _currentRequestMessageId;
    private DateTimeOffset? _currentSubmissionAt;
    private DateTimeOffset? _lastActivityAt;

    // Dedicated role panes must not inherit old approvals from the project's
    // shared OpenCode database. An empty scope is a newly opened role pane.
    public void SetSessionScope(string? rootSessionId)
    {
        OpenCodeStatusSnapshot? changed;
        lock (_gate)
        {
            _ownedSessions = new(StringComparer.Ordinal);
            if (rootSessionId is not null) _ownedSessions.Add(rootSessionId);
            _lastSessionId = rootSessionId;
            _hasPendingSubmission = _hasConfirmedApiBaseline = _hasCompletedTurn = _hasExplicitIdleEvidence = false;
            ResetCurrentRequestActivityLocked();
            changed = RecomputeSnapshotLocked();
        }
        RaiseStatusChanged(changed);
    }

    private bool IsInScope(string? sessionId)
    {
        if (_ownedSessions is null) return true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (sessionId is not null && seen.Add(sessionId))
        {
            if (_ownedSessions.Contains(sessionId)) return true;
            sessionId = _sessionParents.GetValueOrDefault(sessionId);
        }
        return false;
    }

    public void ObserveOwnedSessionTree(IEnumerable<string> sessionIds)
    {
        OpenCodeStatusSnapshot? changed;
        lock (_gate)
        {
            if (_ownedSessions is null || _ownedSessions.Count == 0) return;
            _ownedSessions.UnionWith(sessionIds);
            changed = RecomputeSnapshotLocked();
        }
        RaiseStatusChanged(changed);
    }

    public void ConfirmOwnedTurnCompleted()
    {
        OpenCodeStatusSnapshot? changed;
        lock (_gate)
        {
            if (_ownedSessions is null) return;
            _hasPendingSubmission = false;
            _hasConfirmedApiBaseline = false;
            _hasCompletedTurn = true;
            ResetCurrentRequestActivityLocked();
            changed = RecomputeSnapshotLocked();
        }
        RaiseStatusChanged(changed);
    }

    public OpenCodeStatusMonitor(Uri serverUri, string directory, HttpClient? httpClient = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!serverUri.IsAbsoluteUri || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The OpenCode server URI must be an absolute HTTP URI.", nameof(serverUri));
        if (!serverUri.IsLoopback)
            throw new ArgumentException("The OpenCode status server must use a loopback address.", nameof(serverUri));
        if (!string.IsNullOrEmpty(serverUri.UserInfo))
            throw new ArgumentException("Credentials must be supplied through OpenCode server environment settings.", nameof(serverUri));

        _serverUri = serverUri;
        _directory = directory;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    /// <summary>Raised when the published native status snapshot changes.</summary>
    public event Action<OpenCodeStatusSnapshot>? StatusChanged;
    public event Action<OpenCodeRecoverySignal>? RecoverySignalReceived;
    public event Action<OpenCodeSubmissionObservation>? SubmissionConfirmed;
    public event Action? ActivityChanged;

    /// <summary>Recent current-request assistant or tool changes, excluding native busy alone.</summary>
    public bool HasRecentActivity
    {
        get
        {
            lock (_gate)
                return _lastActivityAt is { } at && _timeProvider.GetUtcNow() - at <= TimeSpan.FromSeconds(8);
        }
    }

    public DateTimeOffset? LastActivityAt
    {
        get { lock (_gate) return _lastActivityAt; }
    }

    internal string? LastConnectionFailure => _lastConnectionFailure;

    /// <summary>Monotonic version of current, in-scope native user-message confirmations.</summary>
    public long SubmissionConfirmationVersion
    {
        get { lock (_gate) return _submissionConfirmationVersion; }
    }

    public long SubmissionGeneration
    {
        get { lock (_gate) return _submissionGeneration; }
    }

    public OpenCodeStatusSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public OpenCodeStatusMonitorPhase MonitorPhase
    {
        get { lock (_gate) return _disposed ? OpenCodeStatusMonitorPhase.NotMonitoring : _monitorPhase; }
    }

    /// <summary>Starts the SSE reader and returns immediately.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;

            _started = true;
            CancellationToken cancellationToken = _lifetime.Token;
            _readerTask = Task.Run(() => RunAsync(cancellationToken));
        }
    }

    /// <summary>
    /// Records that the local TUI received Enter. This is only an optimistic
    /// gray pending state; native busy/idle events decide the real status.
    /// </summary>
    public OpenCodeSubmissionObservation ObserveSubmission()
    {
        OpenCodeStatusSnapshot? changed;
        OpenCodeSubmissionObservation observation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _hasPendingSubmission = true;
            _hasConfirmedApiBaseline = false;
            _hasCompletedTurn = false;
            _hasExplicitIdleEvidence = false;
            ResetCurrentRequestActivityLocked();
            _currentSubmissionAt = _timeProvider.GetUtcNow();
            _submissionGeneration++;
            observation = new OpenCodeSubmissionObservation(_submissionConfirmationVersion, _submissionGeneration);
            changed = RecomputeSnapshotLocked(forcePending: true);
        }

        RaiseStatusChanged(changed);
        return observation;
    }

    /// <summary>Reconciles an exact API receipt without inferring model activity or completion.</summary>
    internal void ConfirmApiSubmission(string sessionId, string messageId, long generation)
    {
        OpenCodeSubmissionObservation observation;
        lock (_gate)
        {
            if (_disposed || generation != _submissionGeneration || !IsInScope(sessionId)) return;
            string key = sessionId + "/" + messageId;
            if (_lastConfirmedSubmissionMessageKey != key)
            {
                _lastConfirmedSubmissionMessageKey = key;
                _submissionConfirmationVersion++;
            }
            _currentRequestSessionId = sessionId;
            _currentRequestMessageId = messageId;
            if (!_hasCompletedTurn)
            {
                // An exact receipt can restore request tracking after reconnect
                // cleared transient state, but cannot reopen an explicit completion.
                _hasPendingSubmission = true;
                _hasConfirmedApiBaseline = false;
            }
            observation = new(_submissionConfirmationVersion, generation);
        }
        SubmissionConfirmed?.Invoke(observation);
    }

    internal async Task RefreshConfirmedApiStatusAsync(string sessionId, string messageId, long submissionGeneration,
        CancellationToken cancellationToken)
    {
        int connectionGeneration;
        lock (_gate)
        {
            if (_disposed || !_connected || submissionGeneration != _submissionGeneration ||
                _lastConfirmedSubmissionMessageKey != sessionId + "/" + messageId) return;
            connectionGeneration = _connectionGeneration;
        }
        await RefreshBaselineCoreAsync(connectionGeneration, cancellationToken).ConfigureAwait(false);
        OpenCodeStatusSnapshot? changed;
        lock (_gate)
        {
            if (_disposed || !_connected || connectionGeneration != _connectionGeneration ||
                submissionGeneration != _submissionGeneration ||
                _lastConfirmedSubmissionMessageKey != sessionId + "/" + messageId) return;
            // A confirmed HTTP baseline can settle display-only transport pending,
            // while the pending request remains active for native activity/completion.
            if (!_hasCompletedTurn)
            {
                // Re-establish exact request tracking if an SSE disconnect cleared
                // its transient activity state while the API receipt remained valid.
                _hasPendingSubmission = true;
                _currentRequestSessionId = sessionId;
                _currentRequestMessageId = messageId;
            }
            _hasConfirmedApiBaseline = true;
            changed = RecomputeSnapshotLocked();
        }
        RaiseStatusChanged(changed);
    }

    /// <summary>
    /// Cancels background work without waiting, so pane disposal is safe on a
    /// UI thread. An injected HttpClient remains owned by its caller.
    /// </summary>
    public void Dispose()
    {
        bool phaseChanged;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            phaseChanged = _monitorPhase != OpenCodeStatusMonitorPhase.NotMonitoring;
            _monitorPhase = OpenCodeStatusMonitorPhase.NotMonitoring;
        }

        if (phaseChanged) RaiseActivityChanged();
        _lifetime.Cancel();
        if (_ownsHttpClient)
            _httpClient.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan reconnectDelay = InitialReconnectDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            int generation = 0;
            using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? baselineTask = null;
            Task? streamTask = null;

            try
            {
                SetMonitorPhase(OpenCodeStatusMonitorPhase.Connecting);
                using var request = CreateRequest(BuildEndpointUri("/event"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation.Token);
                handshake.CancelAfter(NativeStatusHandshakeTimeoutOverride ?? TerminalExecutionTiming.NativeStatusHandshakeTimeout);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    handshake.Token).ConfigureAwait(false);
                handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                response.EnsureSuccessStatusCode();

                OpenCodeStatusSnapshot? changed;
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    generation = ++_connectionGeneration;
                    _connected = true;
                    _hasBaseline = false;
                    changed = RecomputeSnapshotLocked();
                }
                RaiseStatusChanged(changed);

                baselineTask = RefreshBaselineAsync(generation, connectionCancellation.Token);
                streamTask = ReadSseStreamAsync(
                    await response.Content.ReadAsStreamAsync(connectionCancellation.Token).ConfigureAwait(false),
                    connectionCancellation.Token);
                Task firstCompleted = await Task.WhenAny(baselineTask, streamTask).ConfigureAwait(false);
                if (ReferenceEquals(firstCompleted, baselineTask))
                    await baselineTask.ConfigureAwait(false); // A failed baseline tears down this stream and reconnects.
                while (!streamTask.IsCompleted)
                {
                    await Task.WhenAny(streamTask, Task.Delay(TimeSpan.FromSeconds(2), connectionCancellation.Token)).ConfigureAwait(false);
                    if (!streamTask.IsCompleted)
                        await RefreshBaselineAsync(generation, connectionCancellation.Token).ConfigureAwait(false);
                }
                await streamTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A failed request or stream is represented as Unknown below.
                // Exception messages can contain request details, so none are surfaced.
                _lastConnectionFailure = exception.GetType().Name + "/" + exception.InnerException?.GetType().Name;
            }
            finally
            {
                connectionCancellation.Cancel();
                if (baselineTask is not null)
                {
                    try { await baselineTask.ConfigureAwait(false); }
                    catch (Exception) { }
                }
                if (streamTask is not null)
                {
                    try { await streamTask.ConfigureAwait(false); }
                    catch (Exception) { }
                }

                SetDisconnected(generation);
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            SetMonitorPhase(OpenCodeStatusMonitorPhase.ReconnectWaiting);

            try
            {
                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            reconnectDelay = TimeSpan.FromMilliseconds(Math.Min(
                MaximumReconnectDelay.TotalMilliseconds,
                reconnectDelay.TotalMilliseconds * 2));
        }
    }

    private async Task RefreshBaselineAsync(int generation, CancellationToken cancellationToken)
    {
        SetMonitorPhase(OpenCodeStatusMonitorPhase.Checking, generation);
        try
        {
            await RefreshBaselineCoreAsync(generation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (MonitorPhase == OpenCodeStatusMonitorPhase.Checking)
                SetMonitorPhase(OpenCodeStatusMonitorPhase.NotMonitoring, generation);
        }
    }

    private async Task RefreshBaselineCoreAsync(int generation, CancellationToken cancellationToken)
    {
        long versionAtRequestStart;
        long inputVersionAtRequestStart;
        lock (_gate)
        {
            versionAtRequestStart = _eventVersion;
            inputVersionAtRequestStart = _inputEventVersion;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataRequestTimeoutOverride ?? BaselineTimeout);
        Task<Dictionary<string, SessionExecutionStatus>> statusTask = FetchSessionBaselineAsync(timeout.Token);
        Task<Dictionary<string, PendingInput>> permissionTask = FetchPendingInputBaselineAsync("permission", "/permission/", timeout.Token);
        Task<Dictionary<string, PendingInput>> questionTask = FetchPendingInputBaselineAsync("question", "/question/", timeout.Token);
        await Task.WhenAll(statusTask, permissionTask, questionTask).ConfigureAwait(false);
        var baseline = await statusTask.ConfigureAwait(false);
        var pendingBaseline = await permissionTask.ConfigureAwait(false);
        foreach (var item in await questionTask.ConfigureAwait(false))
            pendingBaseline[item.Key] = item.Value;

        OpenCodeStatusSnapshot? changed;
        lock (_gate)
        {
            if (_disposed || !_connected || generation != _connectionGeneration)
                return;

            var eventsAfterRequest = _sessionStatuses
                .Where(pair => _sessionEventVersions.TryGetValue(pair.Key, out long version) && version > versionAtRequestStart)
                .ToArray();

            _sessionStatuses.Clear();
            foreach (var item in baseline)
                _sessionStatuses[item.Key] = item.Value;
            foreach (var item in eventsAfterRequest)
                _sessionStatuses[item.Key] = item.Value;

            var pendingEventsAfterRequest = _pendingInputs
                .Where(pair => _pendingInputEventVersions.TryGetValue(pair.Key, out long version) && version > inputVersionAtRequestStart)
                .ToArray();
            var inputKeysChangedAfterRequest = _pendingInputEventVersions
                .Where(pair => pair.Value > inputVersionAtRequestStart)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string key in inputKeysChangedAfterRequest)
                pendingBaseline.Remove(key);
            foreach (var item in pendingEventsAfterRequest)
                pendingBaseline[item.Key] = item.Value;

            _pendingInputs.Clear();
            foreach (var item in pendingBaseline)
                _pendingInputs[item.Key] = item.Value;

            _hasBaseline = true;
            _lastConnectionFailure = null;
            changed = RecomputeSnapshotLocked();
        }

        RaiseStatusChanged(changed);
        SetMonitorPhase(OpenCodeStatusMonitorPhase.Monitoring, generation);
    }

    private void SetMonitorPhase(OpenCodeStatusMonitorPhase phase, int? generation = null)
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed || generation is { } current && (!_connected || current != _connectionGeneration))
                return;
            changed = _monitorPhase != phase;
            _monitorPhase = phase;
        }

        if (changed) RaiseActivityChanged();
    }

    private void RaiseActivityChanged()
    {
        Delegate[] handlers = ActivityChanged?.GetInvocationList() ?? [];
        foreach (Delegate handler in handlers)
        {
            try { ((Action)handler)(); }
            catch { /* A subscriber must not terminate the status monitor. */ }
        }
    }

    private async Task ReadSseStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var characters = new char[4096];
        var line = new StringBuilder();
        var frameData = new StringBuilder();
        bool lineTooLong = false;
        bool frameInvalid = false;
        bool hasData = false;

        while (true)
        {
            int count = await reader.ReadAsync(characters.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return;

            for (int index = 0; index < count; index++)
            {
                char character = characters[index];
                if (character != '\n')
                {
                    if (line.Length < MaximumSseLineLength)
                        line.Append(character);
                    else
                        lineTooLong = true;
                    continue;
                }

                string completedLine = line.ToString();
                if (completedLine.EndsWith('\r'))
                    completedLine = completedLine[..^1];

                if (lineTooLong)
                {
                    frameInvalid = true;
                }
                else if (completedLine.Length == 0)
                {
                    if (hasData && !frameInvalid)
                        HandleSseData(frameData.ToString());

                    frameData.Clear();
                    frameInvalid = false;
                    hasData = false;
                }
                else if (completedLine[0] != ':')
                {
                    int colon = completedLine.IndexOf(':');
                    string field = colon < 0 ? completedLine : completedLine[..colon];
                    string value = colon < 0 ? string.Empty : completedLine[(colon + 1)..];
                    if (value.StartsWith(' '))
                        value = value[1..];

                    if (string.Equals(field, "data", StringComparison.Ordinal))
                    {
                        int appendedLength = value.Length + (hasData ? 1 : 0);
                        if (frameData.Length + appendedLength > MaximumSseFrameLength)
                        {
                            frameInvalid = true;
                            frameData.Clear();
                        }
                        else if (!frameInvalid)
                        {
                            if (hasData)
                                frameData.Append('\n');
                            frameData.Append(value);
                            hasData = true;
                        }
                    }
                }

                line.Clear();
                lineTooLong = false;
            }
        }
    }

    private void HandleSseData(string data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            if (root.TryGetProperty("directory", out JsonElement eventDirectory) &&
                eventDirectory.ValueKind == JsonValueKind.String &&
                !DirectoriesMatch(eventDirectory.GetString(), _directory))
            {
                return;
            }

            JsonElement payload = root.TryGetProperty("payload", out JsonElement wrappedPayload) &&
                                  wrappedPayload.ValueKind == JsonValueKind.Object
                ? wrappedPayload
                : root;
            if (!TryGetString(payload, "type", out string eventType))
                return;

            JsonElement properties = payload;
            if (payload.TryGetProperty("properties", out JsonElement eventProperties) && eventProperties.ValueKind == JsonValueKind.Object)
                properties = eventProperties;
            else if (payload.TryGetProperty("data", out JsonElement eventData) && eventData.ValueKind == JsonValueKind.Object)
                properties = eventData;

            HandleOpenCodeEvent(eventType, properties);
        }
    }

    private void HandleOpenCodeEvent(string eventType, JsonElement properties)
    {
        // Recovery identity comes from native user messages, never the aggregate last-session hint.
        OpenCodeRecoverySignal? recoverySignal = null;
        if (eventType == "message.updated" && properties.TryGetProperty("info", out var info) &&
            TryGetString(info, "role", out var role) && role == "user" &&
            TryGetString(info, "sessionID", out var userSession) && TryGetString(info, "id", out var messageId))
        {
            DateTimeOffset? created = null;
            if (info.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object &&
                time.TryGetProperty("created", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number &&
                timestamp.TryGetInt64(out long milliseconds))
            {
                try { created = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
                catch (ArgumentOutOfRangeException) { }
            }
            recoverySignal = new(eventType, userSession, messageId, created, Interlocked.Increment(ref _recoveryEventVersion));
        }
        else if (eventType is "todo.updated" or "session.error" && TryGetString(properties, "sessionID", out var taskSession))
            recoverySignal = new(eventType, taskSession, null, null, Interlocked.Increment(ref _recoveryEventVersion));
        OpenCodeStatusSnapshot? changed = null;
        bool activityChanged = false;
        long submissionConfirmationVersion = 0;
        long submissionConfirmationGeneration = 0;
        lock (_gate)
        {
            if (_disposed)
                return;

            if (eventType is "session.created" or "session.updated" &&
                properties.TryGetProperty("info", out JsonElement sessionInfo) &&
                TryGetString(sessionInfo, "id", out string childId) && TryGetString(sessionInfo, "parentID", out string parentId))
                _sessionParents[childId] = parentId;

            long priorConfirmationVersion = _submissionConfirmationVersion;
            activityChanged = ObserveCurrentRequestActivityLocked(eventType, properties);
            if (_submissionConfirmationVersion > priorConfirmationVersion)
            {
                submissionConfirmationVersion = _submissionConfirmationVersion;
                submissionConfirmationGeneration = _submissionGeneration;
            }

            if (IsStatusEvent(eventType) &&
                TryGetString(properties, "sessionID", out string sessionId) &&
                properties.TryGetProperty("status", out JsonElement status) &&
                TryGetString(status, "type", out string statusType))
            {
                if (IsInScope(sessionId)) _lastSessionId = sessionId;
                if (TryParseExecutionStatus(statusType, out SessionExecutionStatus executionStatus))
                {
                    ObserveSessionStatusLocked(sessionId!, executionStatus);
                    if (executionStatus == SessionExecutionStatus.Idle && IsInScope(sessionId))
                        MarkTurnCompletedIfReadyLocked();
                    else if (IsInScope(sessionId))
                        _hasExplicitIdleEvidence = false;
                }
            }
            else if (IsIdleEvent(eventType) && TryGetString(properties, "sessionID", out string idleSessionId))
            {
                if (IsInScope(idleSessionId)) _lastSessionId = idleSessionId;
                ObserveSessionStatusLocked(idleSessionId!, SessionExecutionStatus.Idle);
                if (IsInScope(idleSessionId)) MarkTurnCompletedIfReadyLocked();
            }
            else if (IsInputRequestEvent(eventType))
            {
                string kind = IsPermissionEvent(eventType) ? "permission" : "question";
                string id = TryGetString(properties, "requestID", out string requestId)
                    ? requestId
                    : TryGetString(properties, "id", out string alternateId) ? alternateId : string.Empty;
                string? inputSessionId = TryGetString(properties, "sessionID", out string foundSessionId) ? foundSessionId : null;
                string key = kind + ":" + (id.Length > 0 ? id : inputSessionId ?? "unknown");
                _pendingInputs[key] = new PendingInput(kind, id, inputSessionId);
                _pendingInputEventVersions[key] = ++_inputEventVersion;
                if (inputSessionId is not null && IsInScope(inputSessionId))
                    _lastSessionId = inputSessionId;
            }
            else if (IsInputResponseEvent(eventType))
            {
                string kind = IsPermissionEvent(eventType) ? "permission" : "question";
                string id = TryGetString(properties, "requestID", out string requestId)
                    ? requestId
                    : TryGetString(properties, "permissionID", out string permissionId) ? permissionId
                    : TryGetString(properties, "id", out string alternateId) ? alternateId : string.Empty;
                string? inputSessionId = TryGetString(properties, "sessionID", out string foundSessionId) ? foundSessionId : null;

                string[] resolvedKeys = _pendingInputs
                    .Where(pair => pair.Value.Kind == kind &&
                                   ((id.Length > 0 && pair.Value.Id == id) ||
                                    (id.Length == 0 && (inputSessionId is null || pair.Value.SessionId == inputSessionId))))
                    .Select(pair => pair.Key)
                    .ToArray();
                if (id.Length > 0)
                {
                    string canonicalKey = kind + ":" + id;
                    if (!resolvedKeys.Contains(canonicalKey, StringComparer.Ordinal))
                        resolvedKeys = resolvedKeys.Append(canonicalKey).ToArray();
                }
                if (resolvedKeys.Length == 0 && inputSessionId is not null)
                {
                    string sessionKey = kind + ":" + inputSessionId;
                    resolvedKeys = [sessionKey];
                }
                foreach (string key in resolvedKeys)
                {
                    _pendingInputs.Remove(key);
                    _pendingInputEventVersions[key] = ++_inputEventVersion;
                }
            }

            changed = RecomputeSnapshotLocked();
        }

        RaiseStatusChanged(changed);
        if (activityChanged) RaiseActivityChanged();
        if (submissionConfirmationVersion != 0) SubmissionConfirmed?.Invoke(new OpenCodeSubmissionObservation(submissionConfirmationVersion, submissionConfirmationGeneration));
        if (recoverySignal is not null) RecoverySignalReceived?.Invoke(recoverySignal);
    }

    private bool ObserveCurrentRequestActivityLocked(string eventType, JsonElement properties)
    {
        if (!_hasPendingSubmission)
            return false;

        bool recordedActivity = false;
        if (eventType == "message.updated" && properties.TryGetProperty("info", out JsonElement info) &&
            TryGetString(info, "sessionID", out string sessionId) && IsInScope(sessionId) &&
            TryGetString(info, "id", out string messageId) && TryGetString(info, "role", out string role))
        {
            if (role == "user")
            {
                // Bind activity to the user message observed after the current submission.
                DateTimeOffset? createdAt = ReadMessageCreatedAt(info);
                if (createdAt is null || _currentSubmissionAt is not { } submittedAt ||
                    createdAt.Value.ToUnixTimeMilliseconds() < submittedAt.ToUnixTimeMilliseconds())
                    return false;
                string submissionMessageKey = sessionId + "/" + messageId;
                if (!string.Equals(_lastConfirmedSubmissionMessageKey, submissionMessageKey, StringComparison.Ordinal))
                {
                    _lastConfirmedSubmissionMessageKey = submissionMessageKey;
                    _submissionConfirmationVersion++;
                }

                if (_currentRequestSessionId != sessionId || _currentRequestMessageId != messageId)
                {
                    _currentRequestSessionId = sessionId;
                    _currentRequestMessageId = messageId;
                    _currentRequestAssistantMessages.Clear();
                    _currentRequestPartFingerprints.Clear();
                    _currentRequestDeltaText.Clear();
                }
            }
            else if (role == "assistant" &&
                     sessionId == _currentRequestSessionId &&
                     TryGetString(info, "parentID", out string parentId) && parentId == _currentRequestMessageId)
            {
                _currentRequestAssistantMessages[sessionId + "/" + messageId] = messageId;
            }
        }

        if (eventType == "message.part.delta")
            return ObserveCurrentRequestTextDeltaLocked(properties);

        if (eventType != "message.part.updated" ||
            !properties.TryGetProperty("part", out JsonElement part) ||
            !TryGetString(part, "sessionID", out string partSessionId) ||
            !TryGetString(part, "messageID", out string assistantMessageId) ||
            partSessionId != _currentRequestSessionId || !IsInScope(partSessionId) ||
            !_currentRequestAssistantMessages.ContainsKey(partSessionId + "/" + assistantMessageId) ||
            !TryGetString(part, "id", out string partId) || !TryGetString(part, "type", out string partType))
            return recordedActivity;

        string fingerprint;
        if (partType is "text" or "reasoning")
        {
            string? text = TryGetString(part, "text", out string partText) ? partText : null;
            string partKey = partSessionId + "/" + assistantMessageId + "/" + partId;
            if (string.IsNullOrWhiteSpace(text) && TryGetString(properties, "delta", out string delta))
            {
                if (string.IsNullOrWhiteSpace(delta)) return recordedActivity;
                text = _currentRequestDeltaText.GetValueOrDefault(partKey, string.Empty) + delta;
            }
            if (string.IsNullOrWhiteSpace(text))
                return recordedActivity;
            fingerprint = partType + ":" + text;
            _currentRequestDeltaText[partKey] = text;
        }
        else if (partType == "tool")
        {
            if (!part.TryGetProperty("state", out JsonElement state) || state.ValueKind != JsonValueKind.Object ||
                !TryGetString(state, "status", out string toolStatus) ||
                toolStatus is not ("pending" or "running" or "completed" or "error"))
                return recordedActivity;
            // Empty allocated tool parts and status spinner events carry no work evidence.
            string? tool = TryGetString(part, "tool", out string toolName) ? toolName : null;
            string stateEvidence = string.Join("|", new[] { "title", "input", "output", "error", "metadata" }
                .Select(name => state.TryGetProperty(name, out JsonElement value) ? value.GetRawText() : string.Empty));
            if (toolStatus is "pending" or "running" && string.IsNullOrWhiteSpace(tool) && stateEvidence.Trim('|').Length == 0)
                return recordedActivity;
            fingerprint = "tool:" + toolStatus + ":" + tool + ":" + stateEvidence;
        }
        else
        {
            return recordedActivity;
        }

        string key = partSessionId + "/" + assistantMessageId + "/" + partId;
        if (_currentRequestPartFingerprints.TryGetValue(key, out string? previous) && previous == fingerprint)
            return recordedActivity;
        _currentRequestPartFingerprints[key] = fingerprint;
        _lastActivityAt = _timeProvider.GetUtcNow();
        return true;
    }

    private void ResetCurrentRequestActivityLocked()
    {
        _currentRequestSessionId = null;
        _currentRequestMessageId = null;
        _currentSubmissionAt = null;
        _currentRequestAssistantMessages.Clear();
        _currentRequestPartFingerprints.Clear();
        _currentRequestDeltaText.Clear();
        _lastActivityAt = null;
    }

    private bool ObserveCurrentRequestTextDeltaLocked(JsonElement properties)
    {
        if (!TryGetString(properties, "sessionID", out string sessionId) ||
            !TryGetString(properties, "messageID", out string messageId) ||
            !TryGetString(properties, "partID", out string partId) ||
            !TryGetString(properties, "field", out string field) || field is not ("text" or "reasoning") ||
            !TryGetString(properties, "delta", out string delta) || string.IsNullOrWhiteSpace(delta) ||
            sessionId != _currentRequestSessionId || !IsInScope(sessionId) ||
            !_currentRequestAssistantMessages.ContainsKey(sessionId + "/" + messageId))
            return false;

        string key = sessionId + "/" + messageId + "/" + partId;
        string text = _currentRequestDeltaText.GetValueOrDefault(key, string.Empty) + delta;
        _currentRequestDeltaText[key] = text;
        _currentRequestPartFingerprints[key] = field + ":" + text;
        _lastActivityAt = _timeProvider.GetUtcNow();
        return true;
    }

    private static DateTimeOffset? ReadMessageCreatedAt(JsonElement info)
    {
        if (!info.TryGetProperty("time", out JsonElement time) || time.ValueKind != JsonValueKind.Object ||
            !time.TryGetProperty("created", out JsonElement created) || !created.TryGetInt64(out long milliseconds))
            return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private void ObserveSessionStatusLocked(string sessionId, SessionExecutionStatus status)
    {
        _eventVersion++;
        _sessionEventVersions[sessionId] = _eventVersion;
        _sessionStatuses[sessionId] = status;
    }

    private void MarkTurnCompletedIfReadyLocked()
    {
        if (_hasPendingSubmission &&
            !_pendingInputs.Values.Any(input => IsInScope(input.SessionId)) &&
            !_sessionStatuses.Any(pair => IsInScope(pair.Key) && pair.Value is SessionExecutionStatus.Busy or SessionExecutionStatus.Retry))
        {
            _hasPendingSubmission = false;
            _hasConfirmedApiBaseline = false;
            _hasCompletedTurn = true;
            _hasExplicitIdleEvidence = true;
        }
    }

    private OpenCodeStatusSnapshot? RecomputeSnapshotLocked(bool forcePending = false)
    {
        OpenCodeStatusKind kind;
        string detail;

        if (!_connected)
        {
            kind = OpenCodeStatusKind.Unknown;
            detail = "OpenCode status unavailable.";
        }
        else if (_pendingInputs.Values.Any(input => IsInScope(input.SessionId)))
        {
            kind = OpenCodeStatusKind.NeedsInput;
            detail = "OpenCode is waiting for input.";
        }
        else if (_sessionStatuses.Any(pair => IsInScope(pair.Key) && pair.Value is SessionExecutionStatus.Busy or SessionExecutionStatus.Retry))
        {
            kind = OpenCodeStatusKind.Busy;
            detail = "OpenCode is working.";
        }
        else if (forcePending || (_hasPendingSubmission && !_hasConfirmedApiBaseline))
        {
            kind = OpenCodeStatusKind.Pending;
            detail = "Request sent; waiting for OpenCode status.";
        }
        else if (_hasBaseline || _hasExplicitIdleEvidence || _sessionStatuses.Count > 0)
        {
            kind = OpenCodeStatusKind.Idle;
            detail = "OpenCode is idle.";
        }
        else
        {
            kind = OpenCodeStatusKind.Unknown;
            detail = "Waiting for OpenCode status.";
        }

        var next = new OpenCodeStatusSnapshot(kind, _hasCompletedTurn, _lastSessionId, detail);
        if (next == _snapshot)
            return null;

        _snapshot = next;
        return next;
    }

    private void SetDisconnected(int generation)
    {
        OpenCodeStatusSnapshot? changed;
        bool phaseChanged;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (generation != 0 && generation != _connectionGeneration)
                return;

            bool lostConnection = _connected || generation != 0;
            _connected = false;
            _hasBaseline = false;
            phaseChanged = _monitorPhase != OpenCodeStatusMonitorPhase.NotMonitoring;
            _monitorPhase = OpenCodeStatusMonitorPhase.NotMonitoring;
            if (lostConnection)
            {
                _sessionStatuses.Clear();
                _sessionEventVersions.Clear();
                _pendingInputs.Clear();
                _pendingInputEventVersions.Clear();
                _inputEventVersion = 0;
                _hasPendingSubmission = false;
                _hasConfirmedApiBaseline = false;
                _hasCompletedTurn = false;
                _hasExplicitIdleEvidence = false;
                _lastSessionId = null;
                ResetCurrentRequestActivityLocked();
            }
            changed = RecomputeSnapshotLocked();
        }

        RaiseStatusChanged(changed);
        if (phaseChanged) RaiseActivityChanged();
    }

    private HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (_authorization is not null)
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(_authorization);
        return request;
    }

    private async Task<Dictionary<string, SessionExecutionStatus>> FetchSessionBaselineAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(BuildEndpointUri("/session/status"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseBaseline(document.RootElement);
    }

    private async Task<Dictionary<string, PendingInput>> FetchPendingInputBaselineAsync(
        string kind,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(BuildEndpointUri(path));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new Dictionary<string, PendingInput>(StringComparer.Ordinal);

        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParsePendingInputs(document.RootElement, kind);
    }

    private Uri BuildEndpointUri(string path)
    {
        var builder = new UriBuilder(_serverUri)
        {
            Path = path,
            Query = "directory=" + Uri.EscapeDataString(_directory)
        };
        return builder.Uri;
    }

    private static string? CreateAuthorizationHeader()
    {
        string? password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        if (string.IsNullOrEmpty(password))
            return null;

        string username = Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode";
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        return "Basic " + token;
    }

    private static Dictionary<string, SessionExecutionStatus> ParseBaseline(JsonElement root)
    {
        var result = new Dictionary<string, SessionExecutionStatus>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The OpenCode status baseline must be an object.");

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                TryGetString(property.Value, "type", out string statusType) &&
                TryParseExecutionStatus(statusType, out SessionExecutionStatus status))
            {
                result[property.Name] = status;
            }
            else
            {
                throw new InvalidDataException("The OpenCode status baseline contains an invalid session status.");
            }
        }

        return result;
    }

    private static Dictionary<string, PendingInput> ParsePendingInputs(JsonElement root, string kind)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The OpenCode pending input baseline must be an array.");

        var result = new Dictionary<string, PendingInput>(StringComparer.Ordinal);
        foreach (JsonElement item in root.EnumerateArray())
        {
            if (!TryGetString(item, "id", out string id) ||
                !TryGetString(item, "sessionID", out string sessionId))
                throw new InvalidDataException("The OpenCode pending input baseline contains an invalid request.");

            result[kind + ":" + id] = new PendingInput(kind, id, sessionId);
        }

        return result;
    }

    private static bool TryParseExecutionStatus(string statusType, out SessionExecutionStatus status)
    {
        switch (statusType)
        {
            case "idle":
                status = SessionExecutionStatus.Idle;
                return true;
            case "busy":
                status = SessionExecutionStatus.Busy;
                return true;
            case "retry":
                status = SessionExecutionStatus.Retry;
                return true;
            default:
                status = default;
                return false;
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    private static bool IsStatusEvent(string type) => type == "session.status";
    private static bool IsIdleEvent(string type) => type == "session.idle";
    private static bool IsPermissionEvent(string type) => type.StartsWith("permission.", StringComparison.Ordinal);
    private static bool IsInputRequestEvent(string type) =>
        type is "permission.asked" or "permission.v2.asked" or "permission.updated" or "question.asked" or "question.v2.asked";
    private static bool IsInputResponseEvent(string type) =>
        type is "permission.replied" or "permission.v2.replied" or "question.replied" or "question.rejected" or
            "question.v2.replied" or "question.v2.rejected";

    private static bool DirectoriesMatch(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return false;

        static string Normalize(string value) => value.Trim().Replace('\\', '/').TrimEnd('/');
        return string.Equals(Normalize(first), Normalize(second), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private void RaiseStatusChanged(OpenCodeStatusSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        Delegate[] handlers = StatusChanged?.GetInvocationList() ?? [];
        foreach (Delegate handler in handlers)
        {
            try { ((Action<OpenCodeStatusSnapshot>)handler)(snapshot); }
            catch { /* A subscriber must not terminate the SSE reader. */ }
        }
    }

    private enum SessionExecutionStatus
    {
        Idle,
        Busy,
        Retry
    }

    private sealed record PendingInput(string Kind, string Id, string? SessionId);
}
