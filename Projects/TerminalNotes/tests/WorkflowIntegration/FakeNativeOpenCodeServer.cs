using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

internal static class FakeNativeOpenCodeServer
{
    public static int Run(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        // OpenCodeHeadlessHost resolves PATH candidates by probing `opencode --version`.
        // Accept that probe so tests never fall through to a real per-user OpenCode install.
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine("OpenCode 1.18.34");
            return 0;
        }
        if (args.Contains("run", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("The workflow must use the visible native OpenCode HTTP session, not `opencode run`.");
            return 90;
        }

        string? probe = Environment.GetEnvironmentVariable("TN_DEFAULT_PROBE");
        if (!string.IsNullOrWhiteSpace(probe))
            File.WriteAllText(probe, Environment.CurrentDirectory + "\n" + string.Join('|', args), new UTF8Encoding(false));

        int portIndex = Array.IndexOf(args, "--port");
        if (portIndex < 0 || portIndex + 1 >= args.Length || !int.TryParse(args[portIndex + 1], out int port) || port is <= 0 or > 65535)
        {
            Console.Error.WriteLine("The fake interactive OpenCode process needs its dedicated loopback port.");
            return 91;
        }

        string host = "127.0.0.1";
        int hostnameIndex = Array.IndexOf(args, "--hostname");
        if (hostnameIndex >= 0 && hostnameIndex + 1 < args.Length) host = args[hostnameIndex + 1];
        if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal)) return 92;

        string? startupTrace = Environment.GetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_TRACE");
        if (!string.IsNullOrWhiteSpace(startupTrace))
        {
            Directory.CreateDirectory(startupTrace);
            File.WriteAllText(Path.Combine(startupTrace, Environment.ProcessId + "-" + Guid.NewGuid().ToString("N") + ".started"),
                Environment.CurrentDirectory, new UTF8Encoding(false));
        }

        string? exitOnceMarker = Environment.GetEnvironmentVariable("TN_WORKFLOW_EXIT_STARTUP_ONCE");
        if (!string.IsNullOrWhiteSpace(exitOnceMarker))
        {
            try
            {
                using var marker = new FileStream(exitOnceMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] processId = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                marker.Write(processId);
                marker.Flush(flushToDisk: true);
                return 94;
            }
            catch (IOException) when (File.Exists(exitOnceMarker)) { }
        }

        string? startupHoldFile = Environment.GetEnvironmentVariable("TN_WORKFLOW_NATIVE_STARTUP_HOLD_FILE");
        while (!string.IsNullOrWhiteSpace(startupHoldFile) && File.Exists(startupHoldFile))
            Thread.Sleep(25);

        string? tuiInputTrace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TUI_INPUT_TRACE");
        if (!string.IsNullOrWhiteSpace(tuiInputTrace)) _ = Task.Run(() => CaptureTuiPlanSubmissionsAsync(tuiInputTrace));

        using var server = new FakeServer(host, port);
        server.Run();
        return 0;
    }

    private static async Task CaptureTuiPlanSubmissionsAsync(string traceDirectory)
    {
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                string input = line.Replace("\x1b[200~", "", StringComparison.Ordinal)
                    .Replace("\x1b[201~", "", StringComparison.Ordinal);
                string trimmedInput = input.Trim();
                const string initialPlanRequest = "이 프로젝트에는 아직 실행할 플랜이 없습니다. 사용자에게 작업 목표나 구현 플랜을 입력해 달라고 한국어로 짧게 요청한 뒤 기다리세요. 입력을 받기 전에는 작업을 시작하지 마세요.";
                if (trimmedInput != "hi" && trimmedInput != initialPlanRequest &&
                    !input.Contains("Read and follow the saved implementation plan at ", StringComparison.Ordinal) &&
                    !input.Contains("Read the current project plan at ", StringComparison.Ordinal)) continue;
                Directory.CreateDirectory(traceDirectory);
                string path = Path.Combine(traceDirectory, "tui-input-" + Guid.NewGuid().ToString("N") + ".json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                {
                    AcceptedAt = DateTimeOffset.UtcNow,
                    Input = trimmedInput is "hi" or initialPlanRequest ? trimmedInput : input
                }), new UTF8Encoding(false));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly object _gate = new();
        private readonly Dictionary<string, FakeSession> _sessions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, EventStream> _eventStreams = new();
        private readonly int _port;
        private long _nextEventStream;
        private int _coordinatorPromptCount;
        private int _planReviewAttempts;
        private int _repeatedFeedbackReviewAttempts;
        private int _nativeArtifactBlocked;

        private const long FirstCoordinatorTokenUsage = 180_867;

        public FakeServer(string host, int port)
        {
            _port = port;
            _listener.Prefixes.Add($"http://{host}:{port}/");
        }

        public void Run()
        {
            _listener.Start();
            string? deliveryProof = Environment.GetEnvironmentVariable("TN_WORKFLOW_DELIVERY_PROOF");
            if (!string.IsNullOrWhiteSpace(deliveryProof))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(deliveryProof))!);
                File.WriteAllText(deliveryProof,
                    $"FAKE_NATIVE_OPENCODE_READY pid={Environment.ProcessId} port={_port} directory={Environment.CurrentDirectory}",
                    new UTF8Encoding(false));
            }
            Console.WriteLine("FAKE_NATIVE_OPENCODE_READY");
            Console.CancelKeyPress += OnCancelKeyPress;
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = _listener.GetContextAsync().GetAwaiter().GetResult(); }
                    catch (HttpListenerException) when (_shutdown.IsCancellationRequested) { break; }
                    catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { break; }
                    _ = Task.Run(() => HandleRequestAsync(context));
                }
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
            }
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _listener.Close();
            foreach (EventStream stream in _eventStreams.Values) stream.Dispose();
            _shutdown.Dispose();
        }

        private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            _shutdown.Cancel();
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
                if (path.Length == 0) path = "/";
                // The production host probes this directory-independent loopback endpoint
                // before its API client adds the project directory to normal requests.
                if (path == "/global/health" && context.Request.HttpMethod == "GET")
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                        new { healthy = true, version = "1.18.34" });
                    return;
                }
                if (!DirectoryMatches(context.Request.QueryString["directory"]))
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "unexpected directory" });
                    return;
                }
                if (path == "/event" && context.Request.HttpMethod == "GET")
                {
                    await StreamEventsAsync(context);
                    return;
                }

                if (path == "/session/status" && context.Request.HttpMethod == "GET")
                {
                    if (File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-api-outage")))
                    {
                        await WriteJsonAsync(context.Response, HttpStatusCode.ServiceUnavailable, new { error = "isolated outage fixture" });
                        return;
                    }
                    bool slowStartupFixture = File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-slow-startup"));
                    Dictionary<string, object> statuses;
                    lock (_gate)
                        statuses = _sessions.ToDictionary(pair => pair.Key,
                            pair => (object)new { type = slowStartupFixture ? "busy" : pair.Value.Status }, StringComparer.Ordinal);
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK, statuses);
                    return;
                }
                if (path is "/permission" or "/question")
                {
                    object[] pending = File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-historical-approval"))
                        ? [new { id = "old-unrelated-request", sessionID = "ses_historical_unowned" }] : [];
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK, pending);
                    return;
                }
                if (path == "/session" && context.Request.HttpMethod == "POST")
                {
                    string traceDirectory = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")!;
                    Directory.CreateDirectory(traceDirectory);
                    File.WriteAllText(Path.Combine(traceDirectory, "startup-session-attempt-" + Guid.NewGuid().ToString("N") + ".json"),
                        JsonSerializer.Serialize(new { Directory = Environment.CurrentDirectory }), new UTF8Encoding(false));
                    string failCreateOnce = Path.Combine(Environment.CurrentDirectory, ".fixture-session-create-failure-once");
                    if (File.Exists(failCreateOnce))
                    {
                        File.Delete(failCreateOnce);
                        await WriteJsonAsync(context.Response, HttpStatusCode.ServiceUnavailable, new { error = "one-shot startup session failure" });
                        return;
                    }
                    using JsonDocument body = await ReadBodyAsync(context.Request);
                    string title = body.RootElement.TryGetProperty("title", out JsonElement titleValue)
                        ? titleValue.GetString() ?? string.Empty
                        : string.Empty;
                    if (title.Length == 0)
                    {
                        await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "title required" });
                        return;
                    }
                    string id = "ses_" + Guid.NewGuid().ToString("N");
                    var session = new FakeSession(id, Environment.CurrentDirectory, title);
                    lock (_gate) _sessions.Add(id, session);
                    File.WriteAllText(Path.Combine(traceDirectory, "startup-session-" + id + ".json"),
                        JsonSerializer.Serialize(new { Directory = Environment.CurrentDirectory, SessionId = id, Title = title }), new UTF8Encoding(false));
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK, session.ToInfo());
                    return;
                }
                if (path == "/tui/select-session" && context.Request.HttpMethod == "POST")
                {
                    using JsonDocument body = await ReadBodyAsync(context.Request);
                    string id = body.RootElement.GetProperty("sessionID").GetString() ?? string.Empty;
                    bool exists;
                    lock (_gate)
                    {
                        exists = _sessions.ContainsKey(id);
                    }
                    if (!exists)
                    {
                        await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "session not found" });
                        return;
                    }
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK, true);
                    return;
                }

                string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2 && segments[0] == "session")
                {
                    FakeSession? session;
                    lock (_gate) _sessions.TryGetValue(segments[1], out session);
                    if (session is null)
                    {
                        await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "session not found" });
                        return;
                    }

                    if (segments.Length == 2 && context.Request.HttpMethod == "GET")
                    {
                        await WriteJsonAsync(context.Response, HttpStatusCode.OK, session.ToInfo());
                        return;
                    }
                    if (segments.Length == 3 && segments[2] == "children" && context.Request.HttpMethod == "GET")
                    {
                        await WriteJsonAsync(context.Response, HttpStatusCode.OK, Array.Empty<object>());
                        return;
                    }
                    if (segments.Length == 3 && segments[2] == "message" && context.Request.HttpMethod == "GET")
                    {
                        object[] messages;
                        lock (_gate) messages = session.Messages.Select(message => message.ToJson()).ToArray();
                        await WriteJsonAsync(context.Response, HttpStatusCode.OK, messages);
                        return;
                    }
                    if (segments.Length == 4 && segments[2] == "message" && context.Request.HttpMethod == "GET")
                    {
                        FakeMessage? message;
                        lock (_gate) message = session.Messages.SingleOrDefault(item =>
                            string.Equals(item.Id, segments[3], StringComparison.Ordinal));
                        if (message is null)
                        {
                            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "message not found" });
                            return;
                        }
                        await WriteJsonAsync(context.Response, HttpStatusCode.OK, message.ToJson());
                        return;
                    }
                    if (segments.Length == 3 && segments[2] == "abort" && context.Request.HttpMethod == "POST")
                    {
                        lock (_gate)
                        {
                            if (!session.IgnoreAbort)
                            {
                                session.Aborted = true;
                                session.Status = "idle";
                            }
                        }
                        if (!session.IgnoreAbort)
                            Broadcast("session.status", new { sessionID = session.Id, status = new { type = "idle" } });
                        await WriteJsonAsync(context.Response, HttpStatusCode.OK, true);
                        return;
                    }
                    if (segments.Length == 3 && segments[2] == "prompt_async" && context.Request.HttpMethod == "POST")
                    {
                        using JsonDocument body = await ReadBodyAsync(context.Request);
                        string messageId = body.RootElement.GetProperty("messageID").GetString() ?? string.Empty;
                        JsonElement textPart = body.RootElement.GetProperty("parts").EnumerateArray().Single();
                        string prompt = textPart.GetProperty("text").GetString() ?? string.Empty;
                        string? model = null;
                        if (body.RootElement.TryGetProperty("model", out JsonElement modelValue))
                            model = modelValue.GetProperty("providerID").GetString() + "/" + modelValue.GetProperty("modelID").GetString();
                        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(prompt))
                        {
                            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "message and prompt required" });
                            return;
                        }

                        bool initialNativePrompt;
                        lock (_gate) initialNativePrompt = model is null && session.Messages.Count == 0;
                        if (model is null)
                        {
                            // Record every default-model API attempt, including repeats in a nonempty session,
                            // so workflow tests can verify exact raw prompts and detect duplicate POSTs.
                            string startupTraceDirectory = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")!;
                            Directory.CreateDirectory(startupTraceDirectory);
                            File.WriteAllText(Path.Combine(startupTraceDirectory, "startup-prompt-" + Guid.NewGuid().ToString("N") + ".json"),
                                JsonSerializer.Serialize(new { Directory = Environment.CurrentDirectory, SessionId = session.Id, MessageId = messageId, Prompt = prompt }),
                                new UTF8Encoding(false));
                        }
                        if (initialNativePrompt)
                        {
                            long startupCreated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var startupMessage = new FakeMessage(messageId, new
                            {
                                id = messageId,
                                sessionID = session.Id,
                                role = "user",
                                parentID = (string?)null,
                                time = new { created = startupCreated, completed = (long?)null },
                                tokens = new { input = 0, output = 0, reasoning = 0, cache = new { read = 0, write = 0 } }
                            }, new[] { new { type = "text", text = prompt } });
                            lock (_gate)
                            {
                                if (!session.Messages.Any(item => string.Equals(item.Id, messageId, StringComparison.Ordinal)))
                                    session.Messages.Add(startupMessage);
                                session.Status = "busy";
                            }
                            if (!File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-suppress-startup-events")))
                            {
                                Broadcast("message.updated", new { info = startupMessage.Info, parts = startupMessage.Parts });
                                Broadcast("session.status", new { sessionID = session.Id, status = new { type = "busy" } });
                            }
                            if (prompt == "hi") _ = CompleteStartupHiAsync(session, messageId, startupCreated);
                            string dropResponse = Path.Combine(Environment.CurrentDirectory, ".fixture-drop-startup-prompt-response-once");
                            if (File.Exists(dropResponse))
                            {
                                File.Delete(dropResponse);
                                context.Response.Abort();
                                return;
                            }
                            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                            context.Response.Close();
                            return;
                        }

                        RoleResult role = ResolveRole(prompt);
                        session.Stall = role.Name == "implementer" && role.WorkflowPhase == "Implementation" &&
                            (prompt.Contains("WF_SUPERVISION", StringComparison.Ordinal) || prompt.Contains("WF_MICRO", StringComparison.Ordinal));
                        session.IgnoreAbort = prompt.Contains("WF_SUPERVISION_IGNORE_ABORT", StringComparison.Ordinal);
                        if (model is not null && model != role.ExpectedModel)
                        {
                            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "unexpected model" });
                            return;
                        }
                        if (!TryReadCurrentPlan(prompt, out string? sourcePath, out string? sourcePlan))
                        {
                            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "persisted plan was missing or empty" });
                            return;
                        }

                        long created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var user = new FakeMessage(messageId, new
                        {
                            id = messageId,
                            sessionID = session.Id,
                            role = "user",
                            parentID = (string?)null,
                            time = new { created, completed = (long?)null },
                            tokens = new { input = 0, output = 0, reasoning = 0, cache = new { read = 0, write = 0 } }
                        }, new[] { new { type = "text", text = prompt } });
                        lock (_gate)
                        {
                            session.Messages.Add(user);
                            session.Status = "busy";
                        }
                        Broadcast("message.updated", new { info = user.Info, parts = user.Parts });
                        Broadcast("session.status", new { sessionID = session.Id, status = new { type = "busy" } });

                        string traceDirectory = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")!;
                        Directory.CreateDirectory(traceDirectory);
                        File.WriteAllText(Path.Combine(traceDirectory, role.Name + "-" + Guid.NewGuid().ToString("N") + ".trace"),
                            JsonSerializer.Serialize(new
                            {
                                Directory = Environment.CurrentDirectory,
                                SourcePath = sourcePath,
                                SourcePlan = sourcePlan,
                                Prompt = prompt,
                            Role = role.Name,
                            WorkflowPhase = role.WorkflowPhase,
                            RecordedAt = DateTimeOffset.UtcNow,
                                SessionId = session.Id,
                                MessageId = messageId,
                                Model = model
                            }),
                            new UTF8Encoding(false));

                        TryBlockNativeSessionArtifact(prompt);

                        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                        context.Response.Close();
                        _ = Task.Run(() => CompletePromptAsync(session, messageId, role, created));
                        return;
                    }
                }

                await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "route not found" });
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAKE_NATIVE_OPENCODE_REQUEST_FAILED " + exception.GetType().Name + ": " + exception.Message);
                try { await WriteJsonAsync(context.Response, HttpStatusCode.InternalServerError, new { error = "fake server error" }); }
                catch { }
            }
        }

        private async Task CompletePromptAsync(FakeSession session, string messageId, RoleResult role, long userCreated)
        {
            try
            {
                await Task.Delay(250, _shutdown.Token);
                long created = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), userCreated + 1);
                int coordinatorPrompt = role.Name == "coordinator"
                    ? Interlocked.Increment(ref _coordinatorPromptCount)
                    : 0;
                int inputTokens = coordinatorPrompt == 1 ? checked((int)(FirstCoordinatorTokenUsage - 45)) : 123;
                if (coordinatorPrompt == 1) await HoldFirstCoordinatorResponseAsync();
                if (session.Stall)
                {
                    while (!session.Aborted) await Task.Delay(50, _shutdown.Token);
                    return;
                }
                string assistantId = "msg_" + Guid.NewGuid().ToString("N");
                var assistant = new FakeMessage(assistantId, new
                {
                    id = assistantId,
                    sessionID = session.Id,
                    role = "assistant",
                    parentID = messageId,
                    time = new { created, completed = (long?)(created + 1) },
                    finish = "stop",
                    error = (object?)null,
                    tokens = new { input = inputTokens, output = 45, reasoning = 0, cache = new { read = 0, write = 0 } }
                }, new[] { new { type = "text", text = role.Response } });
                lock (_gate)
                {
                    if (session.Aborted) return;
                    session.Messages.Add(assistant);
                    session.Status = "idle";
                }
                Broadcast("message.updated", new { info = assistant.Info, parts = assistant.Parts });
                // The visible TUI status and API status both settle only on this native idle event.
                Broadcast("session.status", new { sessionID = session.Id, status = new { type = "idle" } });
                Broadcast("session.idle", new { sessionID = session.Id });
            }
            catch (OperationCanceledException) { }
        }

        private async Task CompleteStartupHiAsync(FakeSession session, string messageId, long userCreated)
        {
            try
            {
                await Task.Delay(1000, _shutdown.Token);
                long created = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), userCreated + 1);
                string assistantId = "msg_" + Guid.NewGuid().ToString("N");
                var assistant = new FakeMessage(assistantId, new
                {
                    id = assistantId,
                    sessionID = session.Id,
                    role = "assistant",
                    parentID = messageId,
                    time = new { created, completed = (long?)(created + 1) },
                    finish = "stop",
                    error = (object?)null,
                    tokens = new { input = 0, output = 0, reasoning = 0, cache = new { read = 0, write = 0 } }
                }, new[] { new { type = "text", text = "Please provide a saved plan before I begin implementation." } });
                lock (_gate)
                {
                    if (session.Aborted) return;
                    session.Messages.Add(assistant);
                    session.Status = "idle";
                }
                if (!File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-suppress-startup-events")))
                {
                    Broadcast("message.updated", new { info = assistant.Info, parts = assistant.Parts });
                    Broadcast("session.status", new { sessionID = session.Id, status = new { type = "idle" } });
                    Broadcast("session.idle", new { sessionID = session.Id });
                }
            }
            catch (OperationCanceledException) { }
        }

        private void TryBlockNativeSessionArtifact(string prompt)
        {
            string? signal = Environment.GetEnvironmentVariable("TN_WORKFLOW_BLOCK_NATIVE_ARTIFACT_ONCE");
            if (string.IsNullOrWhiteSpace(signal) || Volatile.Read(ref _nativeArtifactBlocked) != 0)
                return;

            string runsRoot = Path.Combine(Environment.CurrentDirectory, "terminal-notes", "runs");
            string? artifactPath = Directory.Exists(runsRoot)
                ? Directory.EnumerateDirectories(runsRoot)
                    .SelectMany(run => Directory.EnumerateDirectories(run))
                    .Select(dispatch => Path.Combine(dispatch, "native-session.json"))
                    .FirstOrDefault(path => !File.Exists(path) && !Directory.Exists(path))
                : null;
            if (artifactPath is null) return;
            Directory.CreateDirectory(artifactPath);
            if (Interlocked.Exchange(ref _nativeArtifactBlocked, 1) == 0)
                File.WriteAllText(signal, artifactPath, new UTF8Encoding(false));
        }

        private static bool TryReadGuidAfter(string text, string marker, out Guid value)
        {
            int start = text.IndexOf(marker, StringComparison.Ordinal);
            string? token = start < 0 ? null : text[(start + marker.Length)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            return Guid.TryParse(token, out value);
        }

        private async Task HoldFirstCoordinatorResponseAsync()
        {
            string? holdFile = Environment.GetEnvironmentVariable("TN_WORKFLOW_HOLD_FILE");
            if (string.IsNullOrWhiteSpace(holdFile) || !File.Exists(holdFile)) return;

            string? signal = Environment.GetEnvironmentVariable("TN_WORKFLOW_HELD_SIGNAL");
            if (!string.IsNullOrWhiteSpace(signal))
                await File.WriteAllTextAsync(signal, "Coordinator is busy; its response is intentionally pending.", _shutdown.Token);

            while (File.Exists(holdFile)) await Task.Delay(50, _shutdown.Token);
        }

        private async Task StreamEventsAsync(HttpListenerContext context)
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;
            context.Response.KeepAlive = true;
            long id = Interlocked.Increment(ref _nextEventStream);
            var stream = new EventStream(context.Response);
            _eventStreams[id] = stream;
            try
            {
                await stream.FlushAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, _shutdown.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (HttpListenerException) { }
            finally
            {
                _eventStreams.TryRemove(id, out _);
                stream.Dispose();
            }
        }

        private void Broadcast(string type, object properties)
        {
            string data = "data: " + JsonSerializer.Serialize(new { type, properties }) + "\n\n";
            foreach ((long id, EventStream stream) in _eventStreams)
            {
                if (!stream.TryWrite(data)) _eventStreams.TryRemove(id, out _);
            }
        }

        private static async Task<JsonDocument> ReadBodyAsync(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            string body = await reader.ReadToEndAsync();
            return JsonDocument.Parse(body);
        }

        private static bool TryReadCurrentPlan(string prompt, out string? path, out string? content)
        {
            path = null;
            content = null;
            const string marker = "Absolute file path: ";
            int start = prompt.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return false;
            path = prompt[(start + marker.Length)..].Split('\n')[0].TrimEnd('\r').Trim();
            if (!File.Exists(path)) return false;
            content = File.ReadAllText(path, Encoding.UTF8);
            return !string.IsNullOrWhiteSpace(content);
        }

        private RoleResult ResolveRole(string prompt)
        {
            if (prompt.StartsWith("Terminal Notes 진행 중 점검", StringComparison.Ordinal))
            {
                bool coordinator = prompt.Split('\n')[0].Contains("Coordinator", StringComparison.Ordinal);
                return new(coordinator ? "checkpoint-coordinator" : "checkpoint-reviewer", "Checkpoint", "fixture/default",
                    Result("진행 중 변경을 읽고 잘못된 접근을 확인했습니다.",
                        prompt.Contains("WF_MICRO", StringComparison.Ordinal) ? "INTERVENE: 부분 구현을 검토한 뒤 더 작은 수정 단위로 진행하세요." : "CONTINUE: 현재 변경과 다음 검증을 확인하세요.", false));
            }
            const string phaseMarker = "Workflow phase: ";
            int phaseStart = prompt.IndexOf(phaseMarker, StringComparison.Ordinal);
            if (phaseStart < 0)
                throw new InvalidOperationException("The native prompt did not include a machine-readable workflow phase.");
            string phase = prompt[(phaseStart + phaseMarker.Length)..].Split('\n')[0].TrimEnd('\r').Trim();

            if (prompt.Contains("지휘자입니다.", StringComparison.Ordinal))
            {
                return phase switch
                {
                    "Planning" => new("coordinator", phase, "fake/coordinator", Result(
                        "Acceptance checks:\n1. 승인된 요구사항을 구현하고 실제 결과로 검증한다.\n2. 로컬 fixture 서비스를 실행해 준비 상태를 확인한다.",
                        "인수 기준을 기준으로 플랜을 검토하세요.", complete: false, planning: PlanningOutput() with
                        { GoalsAndScope = PlanningOutput().GoalsAndScope + " " + prompt.Split('\n').FirstOrDefault(line => line.Contains("지속 개선 모드 · 개선 주기", StringComparison.Ordinal)) })),
                    "Implementation" => new("coordinator", phase, "fake/coordinator", Result(
                        "승인된 플랜의 구현 작업을 배정했습니다.", "승인된 인수 기준을 구현하고 필요한 검증을 실행하세요.", complete: false)),
                    "Delivery" => new("coordinator", phase, "fake/coordinator", Result(
                        "로컬 fixture 서비스를 시작하고 준비 상태 증거를 수집하도록 배정했습니다.",
                        "로컬 fixture 서비스를 시작하고 실제 준비 상태 증거를 deliveryEvidence에 기록하세요.", complete: false)),
                    _ => throw new InvalidOperationException($"Coordinator received unexpected workflow phase '{phase}'.")
                };
            }
            if (prompt.Contains("구현자입니다.", StringComparison.Ordinal))
            {
                return phase switch
                {
                    "Implementation" => new("implementer", phase, "fake/implementer", Result(
                        "승인된 기준의 구현과 필요한 검증을 완료했습니다.", "실제 검증 근거로 승인된 기준을 확인하세요.", complete: false)),
                    "Delivery" => new("implementer", phase, "fake/implementer", Result(
                        "로컬 fixture OpenCode 서비스가 준비된 것을 확인했습니다.", "fixture launch proof를 최종 검토하세요.", complete: false,
                        deliveryEvidence: ReadDeliveryEvidence())),
                    _ => throw new InvalidOperationException($"Implementer received unexpected workflow phase '{phase}'.")
                };
            }
            if (prompt.Contains("검토자입니다.", StringComparison.Ordinal))
            {
                return phase switch
                {
                    "PlanReview" when prompt.Contains("WF_PLAN_REVIEW_REJECT_ONCE", StringComparison.Ordinal) &&
                                          Interlocked.Increment(ref _planReviewAttempts) == 1 =>
                        new("reviewer", phase, "fake/reviewer", Result(
                            "Unmet planning criterion: the fixture launch check needs an explicit readiness result.",
                            "Add an explicit local readiness check to the acceptance plan, then review the revised plan.",
                            complete: false, planApproved: false)),
                    "PlanReview" => new("reviewer", phase, "fake/reviewer", Result(
                        "플랜의 인수 기준과 범위를 검토하고 승인했습니다.",
                        "계획 검토를 통과했습니다. Coordinator는 승인된 계획의 구현을 지시하세요.",
                        complete: false, planApproved: true)),
                    "Verification" when prompt.Contains("WF_REPEATED_REVIEW_FEEDBACK", StringComparison.Ordinal) &&
                                             Interlocked.Increment(ref _repeatedFeedbackReviewAttempts) <= 2 =>
                        new("reviewer", phase, "fake/reviewer", Result(
                            "Unmet criterion: fixture readiness check. Failing evidence: the repeated fixture assertion is not yet recorded.",
                            "Repeatable fixture issue: record the same fixture readiness assertion before delivery.",
                            complete: false)),
                    "Verification" => new("reviewer", phase, "fake/reviewer", Result(
                        "승인된 인수 기준과 구현 검증 근거를 확인했습니다.",
                        "구현 검증을 통과했습니다. Coordinator는 서비스 전달을 지시하세요.", complete: true)),
                    "FinalVerification" => new("reviewer", phase, "fake/reviewer", Result(
                        "최종 검증에서 구현과 fixture launch proof가 모두 충족되었습니다.", "", complete: true,
                        deliveryEvidence: ReadDeliveryEvidence())),
                    _ => throw new InvalidOperationException($"Reviewer received unexpected workflow phase '{phase}'.")
                };
            }
            throw new InvalidOperationException("The native prompt did not identify an automation role.");
        }

        private static string Result(
            string summary,
            string instruction,
            bool complete,
            bool planApproved = false,
            string? deliveryEvidence = null,
            AutomationPlanningOutput? planning = null) => JsonSerializer.Serialize(new
            {
                summary,
                instruction,
                complete,
                planApproved,
                deliveryEvidence,
                planning
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        private static AutomationPlanningOutput PlanningOutput() => new(
            "검증 가능한 로컬 fixture 자동화 범위와 실제 서비스 확인까지 수행합니다.",
            ["승인된 plan.md의 요구사항을 역할별 단계로 분리합니다."],
            ["자동화 상태 저장소와 역할별 native 세션을 사용합니다."],
            ["계획 검토 후 구현 검증, 서비스 실행, 최종 확인 순서로 진행합니다."],
            ["native 연결 지연과 서비스 실행 실패를 진단 로그에 보존합니다."],
            ["각 단계의 역할·phase 순서, fixture 응답, 최종 실행 근거를 확인합니다."],
            "fixture provider를 로컬 작업 폴더에서 실행하고 loopback readiness 응답과 표시 결과를 확인합니다.");

        private static string ReadDeliveryEvidence()
        {
            string? proofPath = Environment.GetEnvironmentVariable("TN_WORKFLOW_DELIVERY_PROOF");
            if (string.IsNullOrWhiteSpace(proofPath) || !File.Exists(proofPath))
                throw new InvalidOperationException("Fixture delivery proof is missing; the fake native service was not launched.");
            return $"Fixture launch proof ({Path.GetFullPath(proofPath)}): {File.ReadAllText(proofPath, Encoding.UTF8)}";
        }

        private bool DirectoryMatches(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            try
            {
                string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.CurrentDirectory));
                string actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode status, object value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private sealed class FakeSession(string id, string directory, string title)
        {
            public string Id { get; } = id;
            public string Directory { get; } = directory;
            public string Title { get; } = title;
            public string Status { get; set; } = "idle";
            public bool Stall { get; set; }
            public bool IgnoreAbort { get; set; }
            public volatile bool Aborted;
            public List<FakeMessage> Messages { get; } = [];

            public object ToInfo() => new { id = Id, directory = Directory, title = Title, parentID = (string?)null, testProcessId = Environment.ProcessId };
        }

        private sealed record FakeMessage(string Id, object Info, object Parts)
        {
            public object ToJson() => new { info = Info, parts = Parts };
        }
        private sealed record RoleResult(string Name, string WorkflowPhase, string ExpectedModel, string Response);

        private sealed class EventStream(HttpListenerResponse response) : IDisposable
        {
            private readonly object _gate = new();
            private readonly StreamWriter _writer = new(response.OutputStream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            private bool _disposed;

            public Task FlushAsync()
            {
                lock (_gate)
                {
                    if (_disposed) return Task.CompletedTask;
                    _writer.Write(": connected\r\n\r\n");
                    return _writer.FlushAsync();
                }
            }

            public bool TryWrite(string value)
            {
                lock (_gate)
                {
                    if (_disposed) return false;
                    try
                    {
                        _writer.Write(value);
                        _writer.Flush();
                        return true;
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException or HttpListenerException)
                    {
                        DisposeLocked();
                        return false;
                    }
                }
            }

            public void Dispose()
            {
                lock (_gate) DisposeLocked();
            }

            private void DisposeLocked()
            {
                if (_disposed) return;
                _disposed = true;
                try { _writer.Dispose(); } catch { }
                try { response.Close(); } catch { }
            }
        }
    }
}
