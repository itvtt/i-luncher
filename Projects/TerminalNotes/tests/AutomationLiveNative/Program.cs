using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _exitCode;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync();
                Console.WriteLine("Native OpenCode retried failed phases, delivered the fixture, completed final verification, and kept all role terminals alive.");
            }
            catch (Exception exception)
            {
                _exitCode = 1;
                Console.Error.WriteLine("FAIL: " + exception);
            }
            finally
            {
                app.Shutdown();
            }
        };
        app.Run();
        return _exitCode;
    }

    private static async Task RunAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes AutomationLiveNative " + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(fixture, "project");
        string configDirectory = Path.Combine(fixture, "opencode-config");
        string settings = Path.Combine(fixture, "settings.json");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(configDirectory);

        string originalDirectory = Environment.CurrentDirectory;
        var environment = new EnvironmentScope(
            "OPENCODE_CONFIG_CONTENT",
            "OPENCODE_CONFIG_DIR",
            "OPENCODE_DISABLE_AUTOUPDATE", "OPENCODE_CONFIG", "OPENCODE_TUI_CONFIG", "OPENCODE_WORKSPACE_ID", "OPENCODE_DB",
            "OPENCODE_DISABLE_PROJECT_CONFIG", "OPENCODE_DISABLE_MODELS_FETCH", "OPENCODE_PURE",
            "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "PATH");
        MainWindow? window = null;
        var provider = new FakeOpenAiCompatibleProvider();
        try
        {
            Environment.CurrentDirectory = project;
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_CONTENT", CreateOpenCodeConfig(provider.BaseUrl));
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", configDirectory);
            Environment.SetEnvironmentVariable("OPENCODE_DISABLE_AUTOUPDATE", "1");
            foreach (string key in new[] { "OPENCODE_CONFIG", "OPENCODE_TUI_CONFIG", "OPENCODE_WORKSPACE_ID", "OPENCODE_DB" })
                Environment.SetEnvironmentVariable(key, null);
            foreach (string key in new[] { "OPENCODE_DISABLE_PROJECT_CONFIG", "OPENCODE_DISABLE_MODELS_FETCH", "OPENCODE_PURE" })
                Environment.SetEnvironmentVariable(key, "1");
            foreach (string key in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" })
                Environment.SetEnvironmentVariable(key, Path.Combine(fixture, key));
            // Explicit local payload only: never install, discover a fallback, or use
            // the real user's provider/configuration in this integration test.
            string payload = Environment.GetEnvironmentVariable("TERMINALNOTES_TEST_OPENCODE") ??
                throw new InvalidOperationException("Set TERMINALNOTES_TEST_OPENCODE to the native OpenCode executable.");
            Require(File.Exists(payload) && payload.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), "Invalid native payload.");
            string shim = Path.Combine(fixture, "bin");
            Directory.CreateDirectory(shim);
            await File.WriteAllTextAsync(Path.Combine(shim, "opencode.ps1"), "& " + AutomationCommandBuilder.QuotePowerShell(payload) + " @args\nexit $LASTEXITCODE\n");
            Environment.SetEnvironmentVariable("PATH", shim + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            await provider.ProbeFixtureLaunchAsync();
            Require(provider.ModelProbeCount > 0,
                "The fixture launch proof was not based on an observed request to the local loopback provider.");

            window = new MainWindow(settings, autoStartCodex: false, initialWorkspace: project,
                startupHooks: new OpenCodeStartupHooks(_ => Task.FromResult(true),
                    () => throw new InvalidOperationException("No installation permitted in fixture."),
                    _ => throw new InvalidOperationException("No fallback discovery permitted in fixture.")));
            window.Show();
            await UntilAsync(() => GetField(window, "_automationOwnership") is not null,
                "the automation scheduler to acquire its local ownership lock", TimeSpan.FromSeconds(30));

            var timer = (System.Windows.Threading.DispatcherTimer)GetField(window, "_automationTimer")!;
            timer.Stop();
            var projects = (HashSet<string>)GetField(window, "_automationProjects")!;
            projects.Add(project);

            var tabs = (TabControl)window.FindName("TerminalTabs")!;
            var team = (Dictionary<AutomationRole, TabItem>)Invoke(window, "EnsureAutomationTeam", project)!;
            Require(team.Count == 3, "The scheduler did not create the three real OpenCode role panes.");
            var rolePanes = Enum.GetValues<AutomationRole>()
                .Select(role => (Role: role, Tab: team[role], Pane: Pane(team[role])))
                .ToArray();

            await UntilAsync(
                () => rolePanes.All(item => CanStartNativeAutomation(item.Pane)),
                "ordinary native idle readiness in all three role panes",
                TimeSpan.FromSeconds(90));
            Invoke(window, "ShowProjectTask", project);

            var store = new AutomationStore(project);
            AutomationRun draft = await store.CreateDraftAsync(
                "# Local OpenCode role pipeline\n\nRun the three automation roles without editing files.",
                maxIterations: 1,
                options: new AutomationExecutionOptions(
                    TokenBudget: 100_000,
                    MaxStageMinutes: 4));
            Require(draft.Options.ModelFor(AutomationRole.Coordinator) is null &&
                    draft.Options.ModelFor(AutomationRole.Implementer) is null &&
                    draft.Options.ModelFor(AutomationRole.Reviewer) is null,
                "The test run unexpectedly overrode OpenCode's configured default model.");
            await store.ScheduleAsync(draft.Id, DateTimeOffset.UtcNow.AddMilliseconds(-50), draft.Revision);

            DateTime deadline = DateTime.UtcNow.AddMinutes(6);
            AutomationRun? run = null;
            while (DateTime.UtcNow < deadline)
            {
                await (Task)Invoke(window, "PollAutomationAsync")!;
                run = await store.GetAsync(draft.Id);
                if (run?.State is AutomationState.Completed or AutomationState.Stopped)
                    break;
                await Task.Delay(250);
            }

            AutomationRun completedRun = run is { State: AutomationState.Completed }
                ? run
                : throw new InvalidOperationException("The real MainWindow automation poller did not complete its scheduled run. Last state: " + run?.State + "; " + run?.StatusDetail);
            Require(completedRun.WorkflowPhase == AutomationWorkflowPhase.FinalVerification &&
                    completedRun.PlanApproved && !string.IsNullOrWhiteSpace(completedRun.DeliveryEvidence),
                "The run did not retain approved planning and delivery evidence at final verification.");
            Require(completedRun.CurrentRoleRetryCount == 0,
                "The completed workflow retained a stale same-role retry counter.");

            var roleRequests = provider.RoleRequests.ToArray();
            (AutomationRole Role, string Phase)[] expectedOrder =
            [
                (AutomationRole.Coordinator, "Planning"),
                (AutomationRole.Coordinator, "Planning"),
                (AutomationRole.Reviewer, "PlanReview"),
                (AutomationRole.Reviewer, "PlanReview"),
                (AutomationRole.Coordinator, "Implementation"),
                (AutomationRole.Implementer, "Implementation"),
                (AutomationRole.Reviewer, "Verification"),
                (AutomationRole.Coordinator, "Delivery"),
                (AutomationRole.Implementer, "Delivery"),
                (AutomationRole.Reviewer, "FinalVerification")
            ];
            Require(roleRequests.Select(request => (request.Role, request.Phase)).SequenceEqual(expectedOrder),
                "The native workflow did not retry failed phases and then complete the continuous commander sequence. Actual order: " +
                string.Join(", ", roleRequests.Select(request => request.Role + "/" + request.Phase)));
            Require(roleRequests.All(request => request.Model == "test-model"),
                "OpenCode did not use the isolated default model configured for the loopback provider.");

            string[] stageDirectories = Directory.GetDirectories(Path.Combine(project, "terminal-notes", "runs", draft.Id.ToString("N")));
            Require(stageDirectories.Length == 10, $"Expected ten persisted native phase dispatches including retries, received {stageDirectories.Length}.");
            int failedStages = 0;
            int successfulStages = 0;
            foreach (string stageDirectory in stageDirectories)
            {
                using JsonDocument result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stageDirectory, "result.json")));
                if (result.RootElement.GetProperty("Success").GetBoolean()) successfulStages++;
                else failedStages++;
            }
            Require(failedStages == 2 && successfulStages == 8,
                $"Expected two rejected native phase responses and eight accepted responses; got {failedStages} rejected and {successfulStages} accepted.");

            foreach (var item in rolePanes)
            {
                tabs.SelectedItem = item.Tab;
                await UntilAsync(async () =>
                {
                    string screen = await ScreenTextAsync(item.Pane);
                    return screen.Contains(SummaryFor(item.Role), StringComparison.Ordinal);
                }, $"{item.Role} response text to appear in its visible OpenCode terminal", TimeSpan.FromSeconds(45));
                var browser = (WebView2)GetField(item.Pane, "_browser")!;
                await using (var capture = File.Create(Path.Combine(fixture, item.Role + ".png")))
                    await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, capture);
                Console.WriteLine($"PASS: {item.Role} native session returned and visibly rendered {SummaryFor(item.Role)}.");
            }

            await Task.Delay(1500);
            await (Task)Invoke(window, "PollAutomationAsync")!;
            var finalTeam = team;
            foreach (AutomationRole role in Enum.GetValues<AutomationRole>())
            {
                TabItem tab = finalTeam[role];
                TerminalPane pane = Pane(tab);
                Require(tabs.Items.Contains(tab), $"The {role} tab disappeared after workflow completion.");
                Require(pane.IsReady && pane.IsRunning,
                    $"The {role} terminal process was not alive after workflow completion.");
            }

            Console.WriteLine($"PASS: actual OpenCode processed {string.Join(" -> ", expectedOrder.Select(item => item.Role + "/" + item.Phase))} and all role terminals stayed alive.");
        }
        finally
        {
            try
            {
                if (window is not null)
                {
                    Invoke(window, "DisposeResources");
                    window.Close();
                }
            }
            finally
            {
                provider.Dispose();
                environment.Restore();
                Environment.CurrentDirectory = originalDirectory;
                Console.WriteLine("Native fixture evidence: " + fixture);
            }
        }
    }

    private static string CreateOpenCodeConfig(string baseUrl) => JsonSerializer.Serialize(new
    {
        model = "test/test-model",
        small_model = "test/test-model",
        enabled_providers = new[] { "test" },
        provider = new Dictionary<string, object>
        {
            ["test"] = new
            {
                name = "Loopback test provider",
                npm = "@ai-sdk/openai-compatible",
                env = Array.Empty<string>(),
                options = new { apiKey = "test-key", baseURL = baseUrl },
                models = new Dictionary<string, object>
                {
                    ["test-model"] = new
                    {
                        id = "test-model",
                        name = "Loopback Test Model",
                        attachment = false,
                        reasoning = false,
                        temperature = false,
                        tool_call = true,
                        release_date = "2025-01-01",
                        limit = new { context = 100_000, output = 10_000 },
                        cost = new { input = 0, output = 0 },
                        options = new { }
                    }
                }
            }
        }
    });

    private static TerminalPane Pane(TabItem tab) => (TerminalPane)GetProperty(tab.Tag!, "Pane")!;

    private static bool CanStartNativeAutomation(TerminalPane pane) =>
        (bool)(GetProperty(pane, "CanStartNativeAutomation") ?? false);

    private static string SummaryFor(AutomationRole role) => role switch
    {
        AutomationRole.Coordinator => "LIVE_COORDINATOR",
        AutomationRole.Implementer => "LIVE_IMPLEMENTER",
        AutomationRole.Reviewer => "LIVE_REVIEWER",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static async Task<string> ScreenTextAsync(TerminalPane pane)
    {
        var browser = (WebView2)GetField(pane, "_browser")!;
        if (browser.CoreWebView2 is null) return string.Empty;
        string json = await browser.ExecuteScriptAsync(
            "Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')");
        return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
    }

    private static async Task UntilAsync(Func<bool> condition, string description, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string description, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    private static object? Invoke(object target, string name, params object?[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null) { throw exception.InnerException; }
    }

    private static object? GetProperty(object target, string name) =>
        (target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().FullName, name)).GetValue(target);

    private static object? GetField(object target, string name) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name)).GetValue(target);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class EnvironmentScope(params string[] names)
    {
        private readonly Dictionary<string, string?> _values = names.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        public void Restore()
        {
            foreach (var (name, value) in _values) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class FakeOpenAiCompatibleProvider : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly Task _acceptLoop;
        private readonly ConcurrentQueue<(AutomationRole Role, string Phase, string Body, string? Model)> _roleRequests = new();
        private readonly ConcurrentDictionary<string, int> _phaseAttempts = new(StringComparer.Ordinal);
        private int _modelProbeCount;
        private string? _fixtureLaunchProof;

        public string BaseUrl { get; }
        public IReadOnlyCollection<(AutomationRole Role, string Phase, string Body, string? Model)> RoleRequests => _roleRequests.ToArray();
        public int ModelProbeCount => Volatile.Read(ref _modelProbeCount);

        public async Task ProbeFixtureLaunchAsync()
        {
            using var client = new HttpClient();
            using HttpResponseMessage response = await client.GetAsync(BaseUrl + "/models");
            response.EnsureSuccessStatusCode();
            using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string[] models = payload.RootElement.GetProperty("data").EnumerateArray()
                .Select(model => model.GetProperty("id").GetString() ?? "")
                .ToArray();
            if (!models.Contains("test-model", StringComparer.Ordinal))
                throw new InvalidOperationException("The local fixture provider did not return its test model during the launch probe.");
            _fixtureLaunchProof = $"Local fixture launch proof: GET {BaseUrl}/models returned test-model with HTTP {(int)response.StatusCode}; the loopback provider is listening for {Environment.CurrentDirectory}.";
        }

        public FakeOpenAiCompatibleProvider()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _acceptLoop = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) when (_stop.IsCancellationRequested) { break; }
                _handlers.Add(Task.Run(() => HandleAsync(context)));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url?.AbsolutePath ?? "";
                if (context.Request.HttpMethod == "GET" && path == "/v1/models")
                {
                    Interlocked.Increment(ref _modelProbeCount);
                    await WriteJsonAsync(context.Response, new
                    {
                        @object = "list",
                        data = new[] { new { id = "test-model", @object = "model", created = 0, owned_by = "local-test" } }
                    });
                    return;
                }

                if (context.Request.HttpMethod != "POST" || path != "/v1/chat/completions")
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteJsonAsync(context.Response, new { error = new { message = "unsupported local fake-provider route" } });
                    return;
                }

                string body;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                    body = await reader.ReadToEndAsync();
                using JsonDocument request = JsonDocument.Parse(body);
                string requestText = ReadMessageText(request.RootElement.GetProperty("messages"));
                var identity = ClassifyRequest(requestText);
                if (identity.Role is not AutomationRole role || identity.Phase is null)
                {
                    await WriteCompletionAsync(context.Response, "{\"summary\":\"LOCAL_TITLE\",\"instruction\":\"\",\"complete\":false}", streaming: IsStreaming(request.RootElement));
                    return;
                }

                string? model = request.RootElement.TryGetProperty("model", out JsonElement modelElement)
                    ? modelElement.GetString()
                    : null;
                _roleRequests.Enqueue((role, identity.Phase, body, model));
                int attempt = _phaseAttempts.AddOrUpdate(identity.Phase, 1, static (_, current) => current + 1);
                string content;
                if ((identity.Phase == "Planning" || identity.Phase == "PlanReview") && attempt == 1)
                {
                    content = $"INVALID_{identity.Phase.ToUpperInvariant()}_RESPONSE";
                }
                else
                {
                    content = PhaseResponse(identity.Phase, role);
                }
                await WriteCompletionAsync(context.Response, content, IsStreaming(request.RootElement));
            }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
            catch (IOException) { }
            catch (JsonException exception)
            {
                try
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = new { message = exception.Message } });
                }
                catch (Exception) { }
            }
            finally
            {
                try { context.Response.Close(); }
                catch (HttpListenerException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private static string ReadMessageText(JsonElement messages)
        {
            var parts = new List<string>();
            foreach (JsonElement message in messages.EnumerateArray())
            {
                if (!message.TryGetProperty("content", out JsonElement content)) continue;
                if (content.ValueKind == JsonValueKind.String)
                {
                    parts.Add(content.GetString() ?? "");
                    continue;
                }
                if (content.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement part in content.EnumerateArray())
                    if (part.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                        parts.Add(text.GetString() ?? "");
            }
            return string.Join("\n", parts);
        }

        private static (AutomationRole? Role, string? Phase) ClassifyRequest(string messages)
        {
            AutomationRole? role = messages.Contains("· Coordinator ·", StringComparison.Ordinal) ? AutomationRole.Coordinator
                : messages.Contains("· Implementer ·", StringComparison.Ordinal) ? AutomationRole.Implementer
                : messages.Contains("· Reviewer ·", StringComparison.Ordinal) ? AutomationRole.Reviewer
                : null;
            const string marker = "Workflow phase: ";
            int start = messages.LastIndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return (role, null);
            string phase = messages[(start + marker.Length)..].Split('\n')[0].TrimEnd('\r').Trim();
            return (role, string.IsNullOrWhiteSpace(phase) ? null : phase);
        }

        private string PhaseResponse(string phase, AutomationRole role)
        {
            string? deliveryEvidence = null;
            string summary;
            string instruction;
            bool complete = false;
            bool planApproved = false;
            object? planning = null;
            switch (phase, role)
            {
                case ("Planning", AutomationRole.Coordinator):
                    summary = "LIVE_COORDINATOR\nAcceptance checks:\n1. Implement and verify the approved local no-op criteria.\n2. Run the local test fixture and capture live readiness evidence.";
                    instruction = "Review the acceptance checks and approve a complete plan.";
                    planning = new
                    {
                        goalsAndScope = "검증 가능한 로컬 fixture 자동화 범위와 실제 서비스 확인까지 수행합니다.",
                        ideasAndChoices = new[] { "승인된 plan.md의 요구사항을 역할별 단계로 분리합니다." },
                        designFilesTasks = new[] { "자동화 상태 저장소와 역할별 native 세션을 사용합니다." },
                        orderedImplementation = new[] { "계획 검토 후 구현 검증, 서비스 실행, 최종 확인 순서로 진행합니다." },
                        risksAndEdgeCases = new[] { "native 연결 지연과 서비스 실행 실패를 진단 로그에 보존합니다." },
                        acceptanceTests = new[] { "각 단계의 역할·phase 순서, fixture 응답, 최종 실행 근거를 확인합니다." },
                        serviceDemo = "fixture provider를 로컬 작업 폴더에서 실행하고 loopback readiness 응답과 표시 결과를 확인합니다."
                    };
                    break;
                case ("PlanReview", AutomationRole.Reviewer):
                    summary = "LIVE_REVIEWER: Reviewed the plan, scope, acceptance checks, and local fixture delivery method.";
                    instruction = "Plan approved. Coordinator can direct the planned implementation.";
                    planApproved = true;
                    break;
                case ("Implementation", AutomationRole.Coordinator):
                    summary = "LIVE_COORDINATOR: The approved local no-op implementation is ready for its assigned implementation step.";
                    instruction = "Implement the approved local no-op criteria and report actual verification.";
                    break;
                case ("Implementation", AutomationRole.Implementer):
                    summary = "LIVE_IMPLEMENTER: The approved local no-op criteria are implemented and the fixture checks passed.";
                    instruction = "Independently verify the approved criteria and fixture checks.";
                    break;
                case ("Verification", AutomationRole.Reviewer):
                    summary = "LIVE_REVIEWER. Verified criteria: approved no-op criteria. Verification evidence: the local fixture checks passed.";
                    instruction = "Implementation verification passed. Coordinator can direct fixture delivery.";
                    complete = true;
                    break;
                case ("Delivery", AutomationRole.Coordinator):
                    summary = "LIVE_COORDINATOR: The local fixture service must be launched and its live readiness checked.";
                    instruction = "Run the local fixture provider and capture its actual readiness proof.";
                    break;
                case ("Delivery", AutomationRole.Implementer):
                    summary = "LIVE_IMPLEMENTER: Launched the local fixture provider and observed its model-list readiness response.";
                    instruction = "Independently verify the fixture service launch proof and keep it available.";
                    deliveryEvidence = FixtureLaunchEvidence();
                    break;
                case ("FinalVerification", AutomationRole.Reviewer):
                    summary = "LIVE_REVIEWER. Verified criteria: implementation checks and live fixture launch. Verification evidence: the loopback provider returned the test model and remained available.";
                    instruction = "";
                    complete = true;
                    deliveryEvidence = FixtureLaunchEvidence();
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected local provider role/phase: {role}/{phase}.");
            }
            return JsonSerializer.Serialize(new { summary, instruction, complete, planApproved, deliveryEvidence, planning });
        }

        private string FixtureLaunchEvidence()
        {
            return Volatile.Read(ref _fixtureLaunchProof)
                ?? throw new InvalidOperationException("The test driver has not recorded the local fixture provider launch proof.");
        }

        private static bool IsStreaming(JsonElement root) =>
            root.TryGetProperty("stream", out JsonElement stream) && stream.ValueKind == JsonValueKind.True;

        private static async Task WriteCompletionAsync(HttpListenerResponse response, string content, bool streaming)
        {
            if (!streaming)
            {
                await WriteJsonAsync(response, new
                {
                    id = "chatcmpl-terminalnotes-live",
                    @object = "chat.completion",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "test-model",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 128, completion_tokens = 32, total_tokens = 160 }
                });
                return;
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.SendChunked = true;
            response.Headers[HttpResponseHeader.CacheControl] = "no-cache";
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await WriteSseEventAsync(response, new
            {
                id = "chatcmpl-terminalnotes-live",
                @object = "chat.completion.chunk",
                created = now,
                model = "test-model",
                choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } }
            });
            await WriteSseEventAsync(response, new
            {
                id = "chatcmpl-terminalnotes-live",
                @object = "chat.completion.chunk",
                created = now,
                model = "test-model",
                choices = new[] { new { index = 0, delta = new { content }, finish_reason = (string?)null } }
            });
            await WriteSseEventAsync(response, new
            {
                id = "chatcmpl-terminalnotes-live",
                @object = "chat.completion.chunk",
                created = now,
                model = "test-model",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 128, completion_tokens = 32, total_tokens = 160 }
            });
            byte[] done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            await response.OutputStream.WriteAsync(done);
            await response.OutputStream.FlushAsync();
        }

        private static async Task WriteSseEventAsync(HttpListenerResponse response, object chunk)
        {
            byte[] bytes = Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(chunk) + "\n\n");
            await response.OutputStream.WriteAsync(bytes);
            await response.OutputStream.FlushAsync();
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, object payload)
        {
            response.StatusCode = response.StatusCode == 0 ? (int)HttpStatusCode.OK : response.StatusCode;
            response.ContentType = "application/json; charset=utf-8";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            await response.OutputStream.FlushAsync();
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); }
            catch (ObjectDisposedException) { }
            try { _listener.Close(); }
            catch (ObjectDisposedException) { }
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            try { Task.WhenAll(_handlers.ToArray()).Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
