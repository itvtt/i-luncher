using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private OpenCodeHeadlessHost? _headlessOpenCodeHost;
    private OpenCodeConversationClient? _headlessOpenCodeClient;
    private readonly SemaphoreSlim _nativeOperationGate = new(1, 1);
    private string? _nativePaneKey;
    private DispatcherTimer? _nativeRefreshTimer;
    private string? _headlessSessionId;
    private string _nativeComposerDraft = string.Empty;
    private bool _nativeViewReady;
    private long _nativeViewGeneration;
    private OpenCodeAutomationDispatch? _headlessUnconfirmedDispatch;
    private string? _headlessUnconfirmedRequestId;
    private DateTimeOffset _headlessReceiptLastCheckedAt;
    private bool _headlessReceiptCheckInFlight;
    private DateTimeOffset _startupReceiptLastCheckedAt;
    private bool _startupReceiptCheckInFlight;
    private readonly CancellationTokenSource _headlessLifetimeCancellation = new();
    private readonly CancellationToken _headlessLifetimeToken;
    private CancellationToken HeadlessLifetimeCancellation => _headlessLifetimeToken;
    private CancellationTokenSource? _headlessManualDispatchCancellation;
    private bool _nativeRefreshInFlight;
    private DateTimeOffset _nativeCatalogExpiresAt;
    private JsonElement _nativeModelsCache;
    private JsonElement _nativeAgentsCache;
    private int _openCodeTuiStartAttempted;
    private string? _openCodeTuiManualText;
    private long _openCodeTuiManualGeneration;
    private DateTimeOffset _openCodeTuiManualSubmittedAt;
    private (short Columns, short Rows)? _openCodeTuiInitialDimensions;

    private void InitializeHeadlessOpenCode(Uri endpoint, string? paneKey)
    {
        _nativePaneKey = paneKey;
        _headlessOpenCodeHost = new OpenCodeHeadlessHost(endpoint);
        _headlessOpenCodeClient = new OpenCodeConversationClient(endpoint, WorkingDirectory);
        _nativeRefreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            async (_, _) =>
            {
                _nativeRefreshTimer?.Stop();
                await RefreshNativeStateAsync();
            }, Dispatcher);
        _headlessOpenCodeHost.Exited += code =>
        {
            if (_disposed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() => HandleHeadlessOpenCodeExit(code));
        };
        if (_openCodeStatus is not null)
            _openCodeStatus.RecoverySignalReceived += OnOpenCodeTuiRecoverySignal;
    }

    private void OnNativeViewReady()
    {
        if (_disposed || _failed || !_usesNativeOpenCodeStatus || _nativeViewReady) return;
        _nativeViewReady = true;
        TryStartTerminalWhenLaidOut();
    }

    private void ScheduleNativeStateRefresh()
    {
        if (_disposed || _nativeRefreshTimer is null || _nativeRefreshTimer.IsEnabled) return;
        _nativeRefreshTimer.Start();
    }

    private async Task StartHeadlessOpenCodeAsync()
    {
        if (Interlocked.Exchange(ref _openCodeTuiStartAttempted, 1) != 0) return;
        var host = _headlessOpenCodeHost ?? throw new InvalidOperationException("OpenCode server is unavailable.");
        var client = _headlessOpenCodeClient ?? throw new InvalidOperationException("OpenCode client is unavailable.");
        try
        {
            ReportLifecycleStage("native_server_start_requested");
            await host.StartAsync(WorkingDirectory, _openCodeConfigContent,
                autoMode: StartupCommand == "opencode --auto", cancellationToken: HeadlessLifetimeCancellation);
            if (_disposed) { host.Dispose(); return; }

            _openCodeStatus?.Start();
            _headlessSessionId = await RestoreNativeSessionAsync(client, HeadlessLifetimeCancellation);
            if (_usesNativeOpenCodeTui)
            {
                _headlessSessionId ??= await EnsureHeadlessSessionAsync();
                await SelectOpenCodeTuiSessionAsync(_headlessSessionId, HeadlessLifetimeCancellation);
                var installed = await AutoInstall.CheckAsync(HeadlessLifetimeCancellation);
                if (!installed.IsInstalled || string.IsNullOrWhiteSpace(installed.Path) || !File.Exists(installed.Path))
                    throw new FileNotFoundException("A working OpenCode executable was not found.");
                var dimensions = _openCodeTuiInitialDimensions ?? (Columns: (short)40, Rows: (short)24);
                short columns = (short)Math.Clamp((int)dimensions.Columns, 40, 500);
                short rows = (short)Math.Clamp((int)dimensions.Rows, 24, 500);
                _nativeTerminalEmulator?.Resize(columns, rows);
                string command = BuildOpenCodeAttachCommand(installed.Path, NativeOpenCodeEndpoint!, WorkingDirectory, _headlessSessionId);
                _session.Start(WorkingDirectory, columns, rows, startupCommand: command, openCodeConfigContent: _openCodeConfigContent,
                    exitOnStartupCommandCompletion: true);
                if (!_session.IsRunning) throw new InvalidOperationException("OpenCode attach did not start.");
            }
            if (_disposed) return;
            _ready = true;
            _notice.Visibility = System.Windows.Visibility.Collapsed;
            ApplyActivityVisuals();
            _terminalStartedCompletion.TrySetResult(null);
            ReportLifecycleStage("native_server_started");
            Ready?.Invoke();
            StatusChanged?.Invoke(_usesNativeOpenCodeTui ? "OpenCode TUI 연결 완료 · " + WorkingDirectory : "OpenCode 준비 완료 · " + WorkingDirectory);
            await RefreshNativeStateAsync();
        }
        catch (OperationCanceledException) when (HeadlessLifetimeCancellation.IsCancellationRequested || _disposed) { }
        catch (Exception exception)
        {
            _failed = true;
            _terminalStartedCompletion.TrySetResult(exception);
            if (_usesNativeOpenCodeTui && _headlessOpenCodeHost?.IsRunning == true)
            {
                _ready = false;
                _notice.Text = "OpenCode 서버는 실행 중이지만 TUI 연결을 시작하지 못했습니다.\n" + exception.Message;
                _notice.Visibility = System.Windows.Visibility.Visible;
                StatusChanged?.Invoke("OpenCode TUI 연결 실패 · 서버는 유지 중");
                ApplyActivityChange();
            }
            else
            {
                _openCodeStatus?.Dispose();
                ShowError("OpenCode 서버를 시작하지 못했습니다: " + exception.Message);
            }
        }
    }

    private void HandleHeadlessOpenCodeExit(int code)
    {
        if (_disposed || _failed) return;
        try { _headlessLifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
        _failed = true;
        _ready = false;
        _openCodeStatus?.Dispose();
        _activityTracker.MarkExited();
        FailPendingTextSends(new InvalidOperationException("The OpenCode server exited."));
        ReportLifecycleStage("native_server_exited");
        StatusChanged?.Invoke("OpenCode 서버 종료 · 코드 " + code);
        ProcessExited?.Invoke(code);
        ApplyActivityChange();
    }

    private async Task<string?> RestoreNativeSessionAsync(OpenCodeConversationClient client, CancellationToken cancellationToken)
    {
        string? saved = null;
        string path = NativeSessionMapPath();
        try
        {
            if (File.Exists(path))
            {
                string content = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
                try { using var document = JsonDocument.Parse(content); saved = document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : content; }
                catch (JsonException) { saved = content; }
            }
            if (string.IsNullOrWhiteSpace(saved)) return null;
            _ = await client.GetSessionAsync(saved, cancellationToken);
            BindNativeAutomationSession(saved);
            return saved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            PostNativeNotice("저장된 OpenCode 세션을 확인하지 못했습니다. 세션 목록에서 다시 선택할 수 있습니다.", "info");
            System.Diagnostics.Trace.WriteLine("Saved OpenCode session could not be restored: " + exception.GetType().Name);
            return null;
        }
    }

    private string NativeSessionMapPath()
    {
        string identity = Path.GetFullPath(WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant() + "\n" + (_nativePaneKey ?? "unpersisted");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        string directory = Path.Combine(OpenCodeProjectContext.DataDirectory, "OpenCodeHeadlessSessions");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, hash + ".json");
    }

    private void PersistNativeSession(string sessionId)
    {
        if (_nativePaneKey is null) return;
        try
        {
            string path = NativeSessionMapPath();
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(sessionId), new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PostNativeNotice("세션 연결을 저장하지 못했습니다. 현재 세션은 계속 사용할 수 있습니다.", "info");
        }
    }

    private void HandleNativeWebMessage(JsonElement root)
    {
        if (_disposed || _failed) return;
        string? type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (IsUserInputBlocked && type is ("nativeAbort" or "nativeNew" or "nativeSelect" or "nativePermission" or "nativeQuestion" or "nativeQuestionReject"))
        {
            PostNativeNotice("이 터미널의 사용자 입력이 일시 차단되었습니다.", "info");
            return;
        }
        switch (type)
        {
            case "nativeReady": OnNativeViewReady(); break;
            case "nativeSend":
                if (IsUserInputBlocked)
                {
                    PostNative(new { type = "nativeSendFailed", requestID = root.TryGetProperty("requestID", out var blockedId) ? blockedId.GetString() ?? "" : "", message = "이 터미널의 사용자 입력이 일시 차단되었습니다.", retryable = true });
                    break;
                }
                _ = RunNativeUiOperationAsync(() => SendNativeUiPromptAsync(
                    root.TryGetProperty("requestID", out var requestId) ? requestId.GetString() ?? "" : "",
                    root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
                    root.TryGetProperty("model", out var model) ? model.GetString() : null,
                    root.TryGetProperty("agent", out var agent) ? agent.GetString() : null));
                break;
            case "nativeAbort": _ = RunNativeUiOperationAsync(AbortNativeSessionAsync); break;
            case "nativeNew": _ = RunNativeUiOperationAsync(CreateNativeSessionAsync); break;
            case "nativeSelect":
                _ = RunNativeUiOperationAsync(() => SelectNativeSessionAsync(
                    root.TryGetProperty("sessionID", out var id) ? id.GetString() ?? "" : ""));
                break;
            case "nativePermission":
                _ = RunNativeUiOperationAsync(() => AnswerNativePermissionAsync(
                    root.TryGetProperty("requestID", out var permissionId) ? permissionId.GetString() ?? "" : "",
                    root.TryGetProperty("reply", out var reply) ? reply.GetString() ?? "" : ""));
                break;
            case "nativeQuestion":
                JsonElement questionMessage = root.Clone();
                _ = RunNativeUiOperationAsync(() => ReplyNativeQuestionAsync(questionMessage));
                break;
            case "nativeQuestionReject":
                _ = RunNativeUiOperationAsync(() => RejectNativeQuestionAsync(
                    root.TryGetProperty("requestID", out var questionId) ? questionId.GetString() ?? "" : ""));
                break;
            case "nativeRefresh": _ = ReconcileNativeSendReceiptAsync(); _ = RefreshNativeStateAsync(); break;
            case "nativeSetInput":
                if (root.TryGetProperty("text", out var input) && input.ValueKind == JsonValueKind.String)
                    PostNative(new { type = "nativeSetInput", text = input.GetString() ?? "" });
                break;
            case "nativeDraft":
                _nativeComposerDraft = root.TryGetProperty("text", out var draft) && draft.ValueKind == JsonValueKind.String
                    ? draft.GetString() ?? "" : "";
                ApplyActivityChange();
                break;
            case "shortcut":
                Shortcut?.Invoke(root.TryGetProperty("key", out var key) ? key.GetString() ?? "" : "",
                    root.TryGetProperty("shift", out var shift) && shift.ValueKind == JsonValueKind.True);
                break;
            case "activated": ActivatedByUser?.Invoke(); break;
            case "copy": CopyText(root.TryGetProperty("data", out var copied) ? copied.GetString() : null); break;
            case "contextMenu": ContextMenuRequested?.Invoke(root.TryGetProperty("hasSelection", out var selection) && selection.ValueKind == JsonValueKind.True); break;
            case "paste": PasteFromClipboard(); break;
        }
    }

    private async Task RunNativeUiOperationAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) when (_disposed || HeadlessLifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PostNativeNotice(exception.Message, "error");
            await RefreshNativeStateAsync();
        }
    }

    private async Task CreateNativeSessionAsync()
    {
        await _nativeOperationGate.WaitAsync(HeadlessLifetimeCancellation);
        try
        {
            EnsureNativeSessionSwitchAllowed();
            var client = RequireNativeClient();
            string id = ReadSessionId(await client.CreateSessionAsync("Terminal Notes", HeadlessLifetimeCancellation));
            _ = await client.GetSessionAsync(id, HeadlessLifetimeCancellation);
            if (_usesNativeOpenCodeTui)
                await SelectOpenCodeTuiSessionAsync(id, HeadlessLifetimeCancellation);
            SetHeadlessSession(id);
            PersistNativeSession(id);
        }
        finally { _nativeOperationGate.Release(); }
        await RefreshNativeStateAsync();
    }

    private async Task SelectNativeSessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await _nativeOperationGate.WaitAsync(HeadlessLifetimeCancellation);
        try
        {
            EnsureNativeSessionSwitchAllowed();
            _ = await RequireNativeClient().GetSessionAsync(sessionId, HeadlessLifetimeCancellation);
            if (_usesNativeOpenCodeTui)
                await SelectOpenCodeTuiSessionAsync(sessionId, HeadlessLifetimeCancellation);
            SetHeadlessSession(sessionId);
            PersistNativeSession(sessionId);
        }
        finally { _nativeOperationGate.Release(); }
        await RefreshNativeStateAsync();
    }

    private void EnsureNativeSessionSwitchAllowed()
    {
        if (_headlessUnconfirmedDispatch is not null)
            throw new InvalidOperationException("이전 요청의 접수를 확인 중입니다. 확인이 끝난 뒤 대화를 변경하세요.");
        if (_openCodeStatus?.Snapshot.Kind is not (OpenCodeStatusKind.Idle or OpenCodeStatusKind.Unknown) ||
            _nativeAutomationActive || _startupApiActive || HasPendingStartupSubmission || HasLocalPendingInput())
            throw new InvalidOperationException("먼저 진행 중인 요청을 마치고 입력 초안을 보낸 뒤 세션을 변경하세요.");
    }

    private void SetHeadlessSession(string sessionId)
    {
        BindNativeAutomationSession(sessionId);
    }

    private async Task SelectOpenCodeTuiSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var selector = new OpenCodeAutomationClient(NativeOpenCodeEndpoint!, WorkingDirectory, useTuiSelection: true);
        await selector.SelectStartupSessionAsync(sessionId, cancellationToken);
    }

    private async Task SendNativeUiPromptAsync(string requestId, string prompt, string? model, string? agent)
    {
        using var manualDispatch = BeginHeadlessManualDispatch();
        bool postStarted = false;
        string? sessionId = null;
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            if (prompt.Length > 32 * 1024) throw new InvalidOperationException("요청은 32,768자를 넘을 수 없습니다.");
            if (string.IsNullOrWhiteSpace(requestId)) throw new InvalidOperationException("전송 식별자가 없습니다. 초안을 다시 보내세요.");
            await _nativeOperationGate.WaitAsync(manualDispatch.Token);
            try
            {
                if (_startupApiActive || HasPendingStartupSubmission)
                    throw new InvalidOperationException("플랜 요청의 접수를 확인 중입니다. 확인이 끝난 뒤 보내세요.");
                sessionId = await EnsureHeadlessSessionAsync();
                manualDispatch.Token.ThrowIfCancellationRequested();
                if (_usesNativeOpenCodeTui)
                    await SelectOpenCodeTuiSessionAsync(sessionId, manualDispatch.Token);
                if (_openCodeStatus?.Snapshot.Kind != OpenCodeStatusKind.Idle)
                    throw new InvalidOperationException("OpenCode가 응답 중이거나 세션 상태를 확인할 수 없습니다.");
                if (_headlessUnconfirmedDispatch is { } pending)
                {
                    postStarted = true;
                    if (_headlessUnconfirmedRequestId != requestId || pending.SessionId != sessionId || pending.Prompt != prompt)
                        throw new InvalidOperationException("이전 요청의 접수 결과가 불명확합니다. 같은 초안만 영수증을 다시 확인할 수 있습니다.");
                    if (!await HasHeadlessReceiptAsync(pending))
                        throw new InvalidOperationException("이전 요청의 접수를 확인하지 못했습니다. 중복 전송을 막기 위해 다시 보내지 않았습니다.");
                    _headlessUnconfirmedDispatch = null;
                    _headlessUnconfirmedRequestId = null;
                    if (_nativeComposerDraft == prompt) _nativeComposerDraft = "";
                    postStarted = true;
                    PostNative(new { type = "nativeSent", requestID = requestId, text = prompt, sessionID = sessionId });
                    return;
                }
                string messageId = OpenCodeRecoveryClient.CreateMessageId();
                var dispatch = new OpenCodeAutomationDispatch(sessionId, messageId, prompt, DateTimeOffset.UtcNow);
                _headlessUnconfirmedDispatch = dispatch;
                _headlessUnconfirmedRequestId = requestId;
                postStarted = true;
                await SubmitHeadlessPromptAsync(sessionId, prompt, model, agent, manualDispatch.Token, messageIdOverride: messageId);
                _headlessUnconfirmedDispatch = null;
                _headlessUnconfirmedRequestId = null;
                if (_nativeComposerDraft == prompt) _nativeComposerDraft = "";
            }
            finally { _nativeOperationGate.Release(); }
            PostNative(new { type = "nativeSent", requestID = requestId, text = prompt, sessionID = sessionId });
            await RefreshNativeStateAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !HeadlessLifetimeCancellation.IsCancellationRequested)
        {
            bool knownRejection = exception is HttpRequestException { StatusCode: { } code } && (int)code is >= 400 and < 500;
            bool accepted = false;
            if (postStarted && !knownRejection && _headlessUnconfirmedRequestId == requestId &&
                _headlessUnconfirmedDispatch is { } dispatch)
            {
                try { accepted = await HasHeadlessReceiptAsync(dispatch); }
                catch { }
                if (accepted)
                {
                    _headlessUnconfirmedDispatch = null;
                    _headlessUnconfirmedRequestId = null;
                    if (_nativeComposerDraft == prompt) _nativeComposerDraft = "";
                    PostNative(new { type = "nativeSent", requestID = requestId, text = prompt, sessionID = dispatch.SessionId });
                    await RefreshNativeStateAsync();
                    return;
                }
            }
            if (knownRejection)
            {
                _headlessUnconfirmedDispatch = null;
                _headlessUnconfirmedRequestId = null;
            }
            PostNative(new { type = "nativeSendFailed", requestID = requestId, message = exception.Message, retryable = !postStarted || knownRejection });
            PostNativeNotice(exception.Message, "error");
        }
    }

    private async Task<string> EnsureHeadlessSessionAsync()
    {
        if (_headlessSessionId is { Length: > 0 } current) return current;
        var client = RequireNativeClient();
        string created = ReadSessionId(await client.CreateSessionAsync("Terminal Notes", HeadlessLifetimeCancellation));
        _ = await client.GetSessionAsync(created, HeadlessLifetimeCancellation);
        SetHeadlessSession(created);
        PersistNativeSession(created);
        return created;
    }

    private async Task SubmitHeadlessPromptAsync(string sessionId, string prompt, string? model, string? agent,
        CancellationToken cancellationToken, bool observeForAutoResume = true, string? messageIdOverride = null)
    {
        var client = RequireNativeClient();
        string messageId = messageIdOverride ?? OpenCodeRecoveryClient.CreateMessageId();
        OpenCodeSubmissionObservation observation = _openCodeStatus?.ObserveSubmission() ?? default;
        if (observeForAutoResume) _autoResume?.ObserveSubmittedPrompt(prompt, sessionId, messageId);
        else _autoResume?.Suspend("질문 입력 · 자동 진행 대기");
        try
        {
            await client.SendAsync(sessionId, prompt, messageId, model, agent, cancellationToken);
        }
        catch
        {
            _autoResume?.Suspend("요청 전달 결과 확인 필요 · 자동 진행 중지");
            throw;
        }
        _openCodeStatus?.ConfirmApiSubmission(sessionId, messageId, observation.Generation);
        if (observeForAutoResume) _autoResume?.ConfirmApiSubmission(sessionId, messageId);
        ClearWorkFailed();
    }

    private async Task<bool> HasHeadlessReceiptAsync(OpenCodeAutomationDispatch dispatch)
    {
        using var client = new OpenCodeAutomationClient(NativeOpenCodeEndpoint!, WorkingDirectory, useTuiSelection: false);
        return await client.HasStartupMessageAsync(dispatch, HeadlessLifetimeCancellation);
    }

    private async Task SubmitHeadlessStartupPromptAsync(string prompt, bool isQuestion, Func<bool>? canSubmit,
        CancellationToken cancellationToken)
    {
        await _nativeOperationGate.WaitAsync(cancellationToken);
        try
        {
            if (canSubmit?.Invoke() == false) throw new OperationCanceledException(cancellationToken);
            string sessionId = await EnsureHeadlessSessionAsync();
            if (_openCodeStatus?.Snapshot.Kind is OpenCodeStatusKind.Busy or OpenCodeStatusKind.NeedsInput)
                throw new InvalidOperationException("OpenCode가 먼저 진행 중인 요청이나 질문을 처리해야 합니다.");
            await SubmitHeadlessPromptAsync(sessionId, prompt, null, null, cancellationToken, observeForAutoResume: !isQuestion);
            if (canSubmit?.Invoke() == false) throw new OperationCanceledException(cancellationToken);
        }
        finally { _nativeOperationGate.Release(); }
    }

    private async Task AbortNativeSessionAsync()
    {
        CancelNativeAutomation();
        try { _headlessManualDispatchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _autoResume?.Suspend("사용자 중단 · 자동 진행 중지");
        _inputCapture.Observe("\x03");
        if (HasPendingStartupSubmission)
            PostNativeNotice("중단 요청을 보냈습니다. 기존 입력의 접수 여부를 확인 중이며, 확인될 때까지 새 입력은 보내지 않습니다.", "info");
        if (_headlessSessionId is not { Length: > 0 } sessionId) return;
        await RequireNativeClient().AbortAsync(sessionId, HeadlessLifetimeCancellation);
        await RefreshNativeStateAsync();
    }

    private async Task AnswerNativePermissionAsync(string requestId, string reply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (reply is not ("once" or "always" or "reject")) throw new ArgumentException("지원하지 않는 승인 응답입니다.");
        string sessionId = await EnsurePendingRequestBelongsToSelectedSessionAsync(requestId, permission: true);
        await RequireNativeClient().AnswerPermissionAsync(requestId, reply, HeadlessLifetimeCancellation);
        if (_headlessSessionId != sessionId) return;
        await RefreshNativeStateAsync();
    }

    private async Task ReplyNativeQuestionAsync(JsonElement root)
    {
        string requestId = root.TryGetProperty("requestID", out var id) ? id.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(requestId) || !root.TryGetProperty("answers", out var answersElement) ||
            answersElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("질문 답변 형식이 올바르지 않습니다.");
        string sessionId = await EnsurePendingRequestBelongsToSelectedSessionAsync(requestId, permission: false);
        string[][] answers = answersElement.EnumerateArray().Select(question => question.ValueKind == JsonValueKind.Array
            ? question.EnumerateArray().Select(answer => answer.GetString() ?? "").ToArray()
            : Array.Empty<string>()).ToArray();
        await RequireNativeClient().ReplyQuestionAsync(requestId, answers, HeadlessLifetimeCancellation);
        if (_headlessSessionId != sessionId) return;
        await RefreshNativeStateAsync();
    }

    private async Task RejectNativeQuestionAsync(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        string sessionId = await EnsurePendingRequestBelongsToSelectedSessionAsync(requestId, permission: false);
        await RequireNativeClient().RejectQuestionAsync(requestId, HeadlessLifetimeCancellation);
        if (_headlessSessionId != sessionId) return;
        await RefreshNativeStateAsync();
    }

    private async Task RefreshNativeStateAsync()
    {
        if (_disposed || _headlessOpenCodeClient is null || !_browser.Dispatcher.CheckAccess())
        {
            if (!_disposed && !_browser.Dispatcher.HasShutdownStarted)
            await _browser.Dispatcher.InvokeAsync(RefreshNativeStateAsync).Task.Unwrap();
            return;
        }
        if (_nativeRefreshInFlight) return;
        _nativeRefreshInFlight = true;
        try
        {
            var client = _headlessOpenCodeClient;
            long generation = _nativeViewGeneration;
            string? sessionIdSnapshot = _headlessSessionId;
            JsonElement sessions = FilterNativeRootSessions(await client.ListSessionsAsync(HeadlessLifetimeCancellation));
            JsonElement permissions = await client.ReadPermissionsAsync(HeadlessLifetimeCancellation);
            JsonElement questions = await client.ReadQuestionsAsync(HeadlessLifetimeCancellation);
            if (DateTimeOffset.UtcNow >= _nativeCatalogExpiresAt)
            {
                _nativeModelsCache = await ReadOptionalAsync(client.ReadProvidersAsync);
                _nativeAgentsCache = await ReadOptionalAsync(client.ReadAgentsAsync);
                _nativeCatalogExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60);
            }
            IReadOnlySet<string> sessionScope = sessionIdSnapshot is { Length: > 0 } rootSession
                ? await client.ReadSessionScopeAsync(rootSession, HeadlessLifetimeCancellation)
                : new HashSet<string>(StringComparer.Ordinal);
            JsonElement messages = sessionIdSnapshot is { Length: > 0 } sessionId
                ? await client.ReadMessagesAsync(sessionId, HeadlessLifetimeCancellation)
                : EmptyJsonArray();
            if (_nativeViewGeneration != generation || _headlessSessionId != sessionIdSnapshot) return;
            PostNative(new
            {
                type = "nativeState",
                ready = IsReady,
                sessionID = sessionIdSnapshot,
                sessions,
                messages,
                permissions = FilterRequests(permissions, "permissions", sessionScope),
                questions = FilterRequests(questions, "questions", sessionScope),
                status = (_openCodeStatus?.Snapshot.Kind ?? OpenCodeStatusKind.Unknown).ToString().ToLowerInvariant(),
                models = NormalizeModels(_nativeModelsCache),
                agents = NormalizeAgents(_nativeAgentsCache),
                error = (string?)null
            });
        }
        catch (OperationCanceledException) when (_disposed || HeadlessLifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PostNative(new { type = "nativeState", ready = false, error = exception.Message });
        }
        finally
        {
            _nativeRefreshInFlight = false;
            _ = ReconcileNativeSendReceiptAsync();
            if (!_disposed && !_failed && _ready && _nativeRefreshTimer is not null && !_nativeRefreshTimer.IsEnabled)
                _nativeRefreshTimer.Start();
        }
    }

    private async Task<JsonElement> ReadOptionalAsync(Func<CancellationToken, Task<JsonElement>> read)
    {
        try { return await read(HeadlessLifetimeCancellation); }
        catch (OperationCanceledException) when (HeadlessLifetimeCancellation.IsCancellationRequested) { throw; }
        catch { return EmptyJsonArray(); }
    }

    private JsonElement FilterNativeRootSessions(JsonElement sessions)
    {
        if (sessions.ValueKind != JsonValueKind.Array) return EmptyJsonArray();
        var verified = new List<string>();
        foreach (JsonElement session in sessions.EnumerateArray().Take(100))
        {
            string? id = GetJsonString(session, "id");
            string? directory = GetJsonString(session, "directory");
            if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("ses", StringComparison.Ordinal) ||
                GetJsonString(session, "parentID") is not null || string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkingDirectory));
                string actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) verified.Add(session.GetRawText());
            }
            catch { }
        }
        return JsonDocument.Parse("[" + string.Join(',', verified) + "]").RootElement.Clone();
    }

    private async Task ReconcileNativeSendReceiptAsync()
    {
        await ReconcileInterruptedStartupReceiptAsync();
        if (_disposed || _headlessReceiptCheckInFlight || _headlessUnconfirmedDispatch is not { } dispatch ||
            _headlessUnconfirmedRequestId is not { } requestId ||
            DateTimeOffset.UtcNow - _headlessReceiptLastCheckedAt < TimeSpan.FromSeconds(2)) return;
        _headlessReceiptCheckInFlight = true;
        _headlessReceiptLastCheckedAt = DateTimeOffset.UtcNow;
        try
        {
            if (!await HasHeadlessReceiptAsync(dispatch) || _disposed ||
                !ReferenceEquals(_headlessUnconfirmedDispatch, dispatch) || _headlessUnconfirmedRequestId != requestId) return;
            _headlessUnconfirmedDispatch = null;
            _headlessUnconfirmedRequestId = null;
            if (_nativeComposerDraft == dispatch.Prompt) _nativeComposerDraft = "";
            PostNative(new { type = "nativeSent", requestID = requestId, text = dispatch.Prompt, sessionID = dispatch.SessionId });
            await RefreshNativeStateAsync();
        }
        catch (OperationCanceledException) when (HeadlessLifetimeCancellation.IsCancellationRequested) { }
        catch { }
        finally { _headlessReceiptCheckInFlight = false; }
    }

    private async Task ReconcileInterruptedStartupReceiptAsync()
    {
        if (_disposed || _startupApiActive || _startupReceiptCheckInFlight ||
            _startupApiUnconfirmed is not { } dispatch ||
            DateTimeOffset.UtcNow - _startupReceiptLastCheckedAt < TimeSpan.FromSeconds(2)) return;
        _startupReceiptCheckInFlight = true;
        _startupReceiptLastCheckedAt = DateTimeOffset.UtcNow;
        try
        {
            using var client = new OpenCodeAutomationClient(NativeOpenCodeEndpoint!, WorkingDirectory, useTuiSelection: false);
            if (!await client.HasStartupMessageAsync(dispatch, HeadlessLifetimeCancellation) || _disposed || _startupApiActive ||
                !ReferenceEquals(_startupApiUnconfirmed, dispatch)) return;
            _openCodeStatus?.ConfirmApiSubmission(dispatch.SessionId, dispatch.MessageId, _startupApiGeneration);
            _startupApiUnconfirmed = null;
            _openCodeStatus?.SetSessionScope(_boundAutomationSession);
            PostNativeNotice("The interrupted input was confirmed as accepted. No second request was sent.", "info");
        }
        catch (OperationCanceledException) when (HeadlessLifetimeCancellation.IsCancellationRequested) { }
        catch { }
        finally { _startupReceiptCheckInFlight = false; }
    }

    private static JsonElement EmptyJsonArray() => JsonDocument.Parse("[]").RootElement.Clone();

    private static string ReadSessionId(JsonElement response)
    {
        string? id = GetJsonString(response, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("OpenCode did not return a session ID.");
        return id;
    }

    private async Task<string> EnsurePendingRequestBelongsToSelectedSessionAsync(string requestId, bool permission)
    {
        string? sessionId = _headlessSessionId;
        if (sessionId is null) throw new InvalidOperationException("먼저 요청 대상 세션을 선택하세요.");
        JsonElement requests = permission
            ? await RequireNativeClient().ReadPermissionsAsync(HeadlessLifetimeCancellation)
            : await RequireNativeClient().ReadQuestionsAsync(HeadlessLifetimeCancellation);
        IReadOnlySet<string> scope = await RequireNativeClient().ReadSessionScopeAsync(sessionId, HeadlessLifetimeCancellation);
        string property = permission ? "permissions" : "questions";
        JsonElement list = requests.ValueKind == JsonValueKind.Object && requests.TryGetProperty(property, out var nested)
            ? nested : requests;
        bool exact = list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any(request =>
            GetJsonString(request, "id") == requestId && GetJsonString(request, "sessionID") is { } requestedSession && scope.Contains(requestedSession));
        if (!exact || _headlessSessionId != sessionId) throw new InvalidOperationException("이 승인 또는 질문은 현재 선택한 세션에 속하지 않습니다.");
        return sessionId;
    }

    private static JsonElement FilterRequests(JsonElement requests, string collection, IReadOnlySet<string> sessionScope)
    {
        if (sessionScope.Count == 0) return EmptyJsonArray();
        JsonElement list = requests.ValueKind == JsonValueKind.Object && requests.TryGetProperty(collection, out var nested)
            ? nested : requests;
        if (list.ValueKind != JsonValueKind.Array) return EmptyJsonArray();
        string[] filtered = list.EnumerateArray()
            .Where(request => GetJsonString(request, "sessionID") is { } session && sessionScope.Contains(session))
            .Select(request => request.GetRawText()).ToArray();
        return JsonDocument.Parse("[" + string.Join(',', filtered) + "]").RootElement.Clone();
    }

    private static object[] NormalizeModels(JsonElement providers)
    {
        JsonElement values = providers;
        if (providers.ValueKind == JsonValueKind.Object && providers.TryGetProperty("providers", out var nested)) values = nested;
        JsonElement connected = default;
        if (providers.ValueKind == JsonValueKind.Object && providers.TryGetProperty("all", out var all)) values = all;
        if (providers.ValueKind == JsonValueKind.Object && providers.TryGetProperty("connected", out var connectedValue)) connected = connectedValue;
        HashSet<string>? connectedIds = connected.ValueKind == JsonValueKind.Array
            ? connected.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : null;
        var result = new List<object>();
        IEnumerable<(string Key, JsonElement Value)> providerList = values.ValueKind switch
        {
            JsonValueKind.Array => values.EnumerateArray().Select((value, index) => (index.ToString(), value)).ToArray(),
            JsonValueKind.Object => values.EnumerateObject().Select(property => (property.Name, property.Value)).ToArray(),
            _ => Array.Empty<(string, JsonElement)>()
        };
        foreach (var (providerKey, provider) in providerList)
        {
            string providerId = GetJsonString(provider, "id") ?? GetJsonString(provider, "providerID") ?? providerKey;
            if (connectedIds is not null && !connectedIds.Contains(providerId)) continue;
            string providerName = GetJsonString(provider, "name") ?? providerId;
            if (!provider.TryGetProperty("models", out var modelValues)) continue;
            IEnumerable<(string Id, JsonElement Model)> list = modelValues.ValueKind switch
            {
                JsonValueKind.Array => modelValues.EnumerateArray().Select((value, index) => (index.ToString(), value)).ToArray(),
                JsonValueKind.Object => modelValues.EnumerateObject().Select(value => (value.Name, value.Value)).ToArray(),
                _ => Array.Empty<(string, JsonElement)>()
            };
            foreach (var (modelKey, model) in list)
            {
                string modelId = GetJsonString(model, "id") ?? modelKey;
                string modelName = GetJsonString(model, "name") ?? modelId;
                if (providerId.Length > 0 && modelId.Length > 0)
                    result.Add(new { id = providerId + "/" + modelId, name = providerName + " · " + modelName });
            }
        }
        return result.ToArray();
    }

    private static object[] NormalizeAgents(JsonElement agents)
    {
        JsonElement values = agents;
        if (agents.ValueKind == JsonValueKind.Object && agents.TryGetProperty("agents", out var nested)) values = nested;
        if (values.ValueKind != JsonValueKind.Array) return Array.Empty<object>();
        return values.EnumerateArray().Select(agent => new
        {
            name = GetJsonString(agent, "name") ?? GetJsonString(agent, "id") ?? "",
            description = GetJsonString(agent, "description")
        }).Where(agent => agent.name.Length > 0).Cast<object>().ToArray();
    }

    private static string? GetJsonString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private OpenCodeConversationClient RequireNativeClient() => _headlessOpenCodeClient ??
        throw new InvalidOperationException("OpenCode API 연결이 준비되지 않았습니다.");

    private async Task StopOwnedOpenCodeProcessAsync()
    {
        if (!_usesNativeOpenCodeStatus)
        {
            await _session.StopAndWaitAsync();
            return;
        }

        try { _headlessLifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
        try { _headlessManualDispatchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        // Stop the owned attach child before its server so it cannot leave a client
        // connected to a backend that is already being torn down.
        await _session.CloseAsync();
        _headlessOpenCodeHost?.Dispose();
    }

    private CancellationTokenSource BeginHeadlessManualDispatch()
    {
        if (_headlessManualDispatchCancellation is null || _headlessManualDispatchCancellation.IsCancellationRequested)
        {
            _headlessManualDispatchCancellation?.Dispose();
            _headlessManualDispatchCancellation = new CancellationTokenSource();
        }
        return CancellationTokenSource.CreateLinkedTokenSource(HeadlessLifetimeCancellation, _headlessManualDispatchCancellation.Token);
    }

    private void PostNativeNotice(string message, string level) => PostNative(new { type = "nativeNotice", message, level });

    private bool PostNative(object message)
    {
        if (_disposed || _browser.CoreWebView2 is null) return false;
        try { _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message)); return true; }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine("Native OpenCode view post failed: " + exception.GetType().Name);
            return false;
        }
    }
}



