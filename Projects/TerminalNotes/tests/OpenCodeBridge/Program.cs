using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
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
                await VerifyLiveOpenCodeBridgeAsync();
                Console.WriteLine("PASS: installed OpenCode TUI exposes native loopback status and pane visuals ignore activity-tail prose.");
            }
            catch (Exception exception)
            {
                _exitCode = 1;
                Console.Error.WriteLine("FAIL: " + exception);
            }
            finally
            {
                app.Shutdown(_exitCode);
            }
        };

        app.Run();
        return _exitCode;
    }

    private static async Task VerifyNativeRecoveryReadsAsync(Uri endpoint, string directory)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = endpoint };
        string? password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        if (!string.IsNullOrEmpty(password))
        {
            string username = Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode";
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(username + ":" + password)));
        }
        string query = "?directory=" + Uri.EscapeDataString(directory);
        using var response = await http.PostAsync("session" + query,
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string id = json.RootElement.GetProperty("id").GetString()!;
        try
        {
            using var recovery = new OpenCodeRecoveryClient(endpoint, directory);
            var read = await recovery.ReadAsync(id);
            Require(read.IsAvailable && read.Evidence is { Status: OpenCodeRecoveryStatus.Idle, TotalTodoCount: 0 },
                "Native recovery endpoints/schema failed: " + read.ErrorCode + " / " + read.Error);
            Require(read.Evidence!.LatestUserMessageId is null && !read.Evidence.HasRunningTools,
                "Empty test session was misclassified as active work.");
            Console.WriteLine("PASS: actual OpenCode recovery reads validate exact empty session without a model request.");
        }
        finally
        {
            using var deletion = await http.DeleteAsync("session/" + Uri.EscapeDataString(id) + query);
            deletion.EnsureSuccessStatusCode();
        }
    }

    private static async Task VerifyLiveOpenCodeBridgeAsync()
    {
        string fixtureRoot = Path.Combine(Path.GetTempPath(), "TerminalNotes OpenCode Bridge " + Guid.NewGuid().ToString("N"));
        string projectDirectory = Path.Combine(fixtureRoot, "project");
        Directory.CreateDirectory(projectDirectory);
        using var isolatedEnvironment = new IsolatedOpenCodeEnvironment(fixtureRoot);
        string planPath = Path.Combine(projectDirectory, "plan 한글.md");
        File.WriteAllText(planPath, "# Project plan\nBuild the requested feature.");
        File.WriteAllText(Path.Combine(projectDirectory, "project-rules.md"), "Follow the project rules.");
        File.WriteAllText(Path.Combine(projectDirectory, "opencode.json"), "{\"instructions\":[\"project-rules.md\"]}");

        TerminalPane? pane = null;
        TerminalPane? unstartedPane = null;
        Window? window = null;
        try
        {
            pane = new TerminalPane(projectDirectory, startupCommand: "opencode", protectTuiViewport: true,
                planDocumentPath: planPath, projectName: "rraad");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pane.Ready += () => ready.TrySetResult();
            window = new Window
            {
                Content = pane,
                Width = 960,
                Height = 640,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Show();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(45));

            var browser = GetField<WebView2>(pane, "_browser");
            var monitor = GetField<OpenCodeStatusMonitor>(pane, "_openCodeStatus");
            Uri endpoint = GetField<Uri>(monitor, "_serverUri");
            try
            {
                await UntilAsync(
                    () => monitor.Snapshot.Kind == OpenCodeStatusKind.Idle && pane.Activity == TerminalActivity.Idle,
                    TimeSpan.FromSeconds(45),
                    "read-only OpenCode startup idle baseline");
            }
            catch (TimeoutException exception)
            {
                await DumpNativeEndpointDiagnosticsAsync(endpoint, projectDirectory, monitor);
                await InspectReadOnlyOpenApiAsync(endpoint, projectDirectory);
                string screen = await ScreenAsync(browser);
                throw new TimeoutException(
                    $"Native startup baseline timed out. Monitor={monitor.Snapshot}; pane={pane.Activity}/{pane.ActivityLabel}; " +
                    $"reader={GetOptionalField<Task>(monitor, "_readerTask")?.Status}; failure={GetOptionalField<string>(monitor, "_lastConnectionFailure")}; connected={GetOptionalField<bool>(monitor, "_connected")}; " +
                    $"hasBaseline={GetOptionalField<bool>(monitor, "_hasBaseline")}; command={GetField<string>(pane, "_effectiveStartupCommand")}; " +
                    $"screen tail={screen[^Math.Min(screen.Length, 1800)..]}",
                    exception);
            }

            await DumpNativeEndpointDiagnosticsAsync(endpoint, projectDirectory, monitor);
            await InspectReadOnlyOpenApiAsync(endpoint, projectDirectory);
            await VerifyProjectContextConfigAsync(pane, endpoint, projectDirectory, planPath);
            await VerifyNativeRecoveryReadsAsync(endpoint, projectDirectory);
            string effectiveCommand = GetField<string>(pane, "_effectiveStartupCommand");
            Match portMatch = Regex.Match(effectiveCommand, @"\Aopencode --hostname 127\.0\.0\.1 --port (?<port>\d+)\z");
            Require(portMatch.Success, "The TUI did not start OpenCode with an explicit 127.0.0.1 port.");
            int port = int.Parse(portMatch.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture);
            Require(endpoint.Host == "127.0.0.1" && endpoint.Port == port && endpoint.IsLoopback,
                "The monitor endpoint did not match the actual loopback port passed to OpenCode.");

            unstartedPane = new TerminalPane(projectDirectory, startupCommand: "opencode", protectTuiViewport: true);
            string secondCommand = GetField<string>(unstartedPane, "_effectiveStartupCommand");
            Match secondPortMatch = Regex.Match(secondCommand, @"\Aopencode --hostname 127\.0\.0\.1 --port (?<port>\d+)\z");
            Require(secondPortMatch.Success && int.Parse(secondPortMatch.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture) != port,
                "A second pane did not reserve a distinct loopback port.");
            unstartedPane.Dispose();
            unstartedPane = null;

            Require(!pane.HasCompletedWork, "An empty initial status baseline was reported as completed work.");
            await VerifyNativeVisualAndTextIndependenceAsync(pane, browser);
        }
        finally
        {
            try { unstartedPane?.Dispose(); } catch { }
            try { pane?.Dispose(); } catch { }
            try { window?.Close(); } catch { }
            isolatedEnvironment.Dispose();
            DeleteFixtureTree(fixtureRoot);
        }
    }

    private static async Task VerifyProjectContextConfigAsync(TerminalPane pane, Uri endpoint, string project, string planPath)
    {
        var context = GetField<OpenCodeProjectContext>(pane, "_openCodeProjectContext");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        string? password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        if (!string.IsNullOrEmpty(password))
        {
            string username = Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode";
            string token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(username + ":" + password));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
        }
        using var response = await client.GetAsync(new Uri(endpoint, "config?directory=" + Uri.EscapeDataString(project)));
        response.EnsureSuccessStatusCode();
        using var config = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string?[] instructions = config.RootElement.GetProperty("instructions").EnumerateArray().Select(item => item.GetString()).ToArray();
        Require(instructions.Contains(context.InstructionFilePath) && instructions.Contains("project-rules.md"),
            "Live OpenCode did not combine the pane MD context with the project's existing instructions.");
        string before = File.ReadAllText(context.InstructionFilePath);
        Require(before.Contains(planPath, StringComparison.Ordinal) && before.Contains("rraad", StringComparison.Ordinal),
            "The pane context did not identify its project name and MD.");
        string nextPlan = Path.Combine(project, "next.md");
        pane.UpdatePlanDocumentContext(nextPlan, "rraad renamed");
        string after = File.ReadAllText(context.InstructionFilePath);
        Require(after.Contains(nextPlan, StringComparison.Ordinal) && after.Contains("rraad renamed", StringComparison.Ordinal) &&
                !after.Contains(planPath, StringComparison.Ordinal), "Live pane rebinding retained stale plan context.");
        Require(File.ReadAllText(planPath).Contains("Build the requested feature.", StringComparison.Ordinal) && !File.Exists(nextPlan),
            "Context rebinding modified a plan file.");
        Console.WriteLine("PASS: live OpenCode config preserves project rules and receives the stable project/MD context; rebinding preserves MD files.");
    }

    private static async Task VerifyNativeVisualAndTextIndependenceAsync(TerminalPane pane, WebView2 browser)
    {
        await ApplyNativeSnapshotAsync(
            pane,
            browser,
            new OpenCodeStatusSnapshot(OpenCodeStatusKind.Busy, false, "session.test"),
            TerminalActivity.Active,
            "#00853F",
            "OpenCode busy state");
        Require(!pane.HasCompletedWork, "Native busy status was reported as completed work.");
        string activeLabel = pane.ActivityLabel;
        await SendActivityTailAsync(browser, "hi?!", "hi?!", promptAtCursor: true);
        Require(pane.Activity == TerminalActivity.Active && pane.ActivityLabel == activeLabel && !pane.HasCompletedWork,
            "Greeting punctuation in activityTail changed the native busy snapshot.");
        await UntilAsync(() => ThemeBackgroundAsync(browser, "#00853F"), TimeSpan.FromSeconds(5), "busy color retained after greeting punctuation");

        await ApplyNativeSnapshotAsync(
            pane,
            browser,
            new OpenCodeStatusSnapshot(OpenCodeStatusKind.Idle, true, "session.test"),
            TerminalActivity.Idle,
            "#252D3A",
            "OpenCode idle completion");
        Require(pane.HasCompletedWork, "Explicit native idle completion was not exposed by the pane.");
        string idleLabel = pane.ActivityLabel;
        await SendActivityTailAsync(browser, "Hi How can I help you today?", "Hi How can I help you today?", promptAtCursor: false);
        Require(pane.Activity == TerminalActivity.Idle && pane.ActivityLabel == idleLabel && pane.HasCompletedWork,
            "Greeting or answer text in activityTail changed the native idle snapshot.");
        await UntilAsync(() => ThemeBackgroundAsync(browser, "#252D3A"), TimeSpan.FromSeconds(5), "idle gray color retained after answer text");

        await ApplyNativeSnapshotAsync(
            pane,
            browser,
            new OpenCodeStatusSnapshot(OpenCodeStatusKind.NeedsInput, false, "session.test"),
            TerminalActivity.NeedsInput,
            "#FF8A00",
            "OpenCode permission request");
        Require(!pane.HasCompletedWork, "A native permission request was reported as completed work.");
        await SendActivityTailAsync(browser, "Proceed? [y/N]", "Proceed? [y/N]", promptAtCursor: true);
        Require(pane.Activity == TerminalActivity.NeedsInput,
            "Terminal text changed the native permission snapshot.");
        await UntilAsync(() => ThemeBackgroundAsync(browser, "#FF8A00"), TimeSpan.FromSeconds(5), "permission orange color retained after activityTail");

        await ApplyNativeSnapshotAsync(
            pane,
            browser,
            new OpenCodeStatusSnapshot(OpenCodeStatusKind.Pending, false),
            TerminalActivity.Starting,
            "#252D3A",
            "local pending submission");
        Require(!pane.HasCompletedWork, "A local pending submission was reported as completed work.");

        await ApplyNativeSnapshotAsync(
            pane,
            browser,
            new OpenCodeStatusSnapshot(OpenCodeStatusKind.Unknown, false),
            TerminalActivity.Quiet,
            "#252D3A",
            "native status unavailable");
    }

    private static async Task ApplyNativeSnapshotAsync(
        TerminalPane pane,
        WebView2 browser,
        OpenCodeStatusSnapshot snapshot,
        TerminalActivity expectedActivity,
        string expectedColor,
        string context)
    {
        SetField(pane, "_openCodeSnapshot", snapshot);
        Call(pane, "ApplyActivityChange");
        await UntilAsync(
            async () => pane.Activity == expectedActivity && await ThemeBackgroundAsync(browser, expectedColor),
            TimeSpan.FromSeconds(5),
            context);
    }

    private static async Task SendActivityTailAsync(WebView2 browser, string data, string screen, bool promptAtCursor)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2WebMessageReceivedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            try
            {
                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                if (message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "activityTail")
                    received.TrySetResult();
            }
            catch (JsonException) { }
        };

        browser.CoreWebView2.WebMessageReceived += handler;
        try
        {
            string payload = JsonSerializer.Serialize(new
            {
                type = "activityTail",
                data,
                screen,
                layout = "80x24",
                promptAtCursor
            });
            await browser.ExecuteScriptAsync("window.chrome.webview.postMessage(" + payload + ");true");
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            browser.CoreWebView2.WebMessageReceived -= handler;
        }
    }

    private static async Task<bool> ThemeBackgroundAsync(WebView2 browser, string expected) =>
        string.Equals(
            JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("terminal.options.theme.background")),
            expected,
            StringComparison.OrdinalIgnoreCase);

    private static async Task InspectReadOnlyOpenApiAsync(Uri endpoint, string directory)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        string? password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        if (!string.IsNullOrEmpty(password))
        {
            string username = Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode";
            string token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(username + ":" + password));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
        }

        string query = "?directory=" + Uri.EscapeDataString(directory);
        using var docResponse = await client.GetAsync(new Uri(endpoint, "doc" + query));
        string doc = await docResponse.Content.ReadAsStringAsync();
        bool permissionDocumented = doc.Contains("/permission", StringComparison.OrdinalIgnoreCase);
        bool questionDocumented = doc.Contains("/question", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"OpenCode /doc: HTTP {(int)docResponse.StatusCode}; permission route text={permissionDocumented}; question route text={questionDocumented}.");
        PrintOpenApiItemShape(doc, "PermissionRequest");
        PrintOpenApiItemShape(doc, "QuestionRequest");

        foreach (string path in new[] { "permission/", "question/" })
        {
            using var response = await client.GetAsync(new Uri(endpoint, path + query));
            string body = await response.Content.ReadAsStringAsync();
            string shape = "body unavailable";
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var json = JsonDocument.Parse(body);
                    shape = json.RootElement.ValueKind == JsonValueKind.Array
                        ? $"array with {json.RootElement.GetArrayLength()} pending item(s)"
                        : $"JSON {json.RootElement.ValueKind}";
                }
                catch (JsonException)
                {
                    shape = "non-JSON response";
                }
            }
            Console.WriteLine($"OpenCode GET /{path.TrimEnd('/')}: HTTP {(int)response.StatusCode}; {shape}.");
        }
    }

    private static void PrintOpenApiItemShape(string document, string schemaName)
    {
        try
        {
            using var json = JsonDocument.Parse(document);
            JsonElement schema = json.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty(schemaName);
            var required = schema.TryGetProperty("required", out JsonElement requiredElement)
                ? requiredElement.EnumerateArray().Select(value => value.GetString()).Where(value => value is not null).ToArray()
                : Array.Empty<string?>();
            string[] properties = schema.TryGetProperty("properties", out JsonElement propertiesElement)
                ? propertiesElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            Console.WriteLine($"OpenCode /doc schema {schemaName}: required=[{string.Join(",", required)}]; properties=[{string.Join(",", properties)}].");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"OpenCode /doc schema {schemaName}: unavailable ({exception.GetType().Name}).");
        }
    }

    private static async Task DumpNativeEndpointDiagnosticsAsync(Uri endpoint, string directory, OpenCodeStatusMonitor monitor)
    {
        string taskState = (GetOptionalField<Task>(monitor, "_readerTask")?.Status.ToString()) ?? "not started";
        Console.WriteLine($"OpenCode monitor before baseline wait: snapshot={monitor.Snapshot}; reader={taskState}; " +
            $"connected={GetOptionalField<bool>(monitor, "_connected")}; hasBaseline={GetOptionalField<bool>(monitor, "_hasBaseline")}; " +
            $"establishedBaseline={GetOptionalField<bool>(monitor, "_hasEstablishedBaseline")}; pending={GetOptionalField<bool>(monitor, "_hasPendingSubmission")}.");

        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
        string? password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        if (!string.IsNullOrEmpty(password))
        {
            string username = Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode";
            string token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(username + ":" + password));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
        }

        string query = "?directory=" + Uri.EscapeDataString(directory);
        foreach (string path in new[] { "session/status", "permission/", "question/" })
        {
            try
            {
                using var response = await client.GetAsync(new Uri(endpoint, path + query), HttpCompletionOption.ResponseHeadersRead);
                string body = await response.Content.ReadAsStringAsync();
                if (body.Length > 400)
                    body = body[..400];
                Console.WriteLine($"OpenCode diagnostic GET /{path}: HTTP {(int)response.StatusCode}, content-type={response.Content.Headers.ContentType}; body={body}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"OpenCode diagnostic GET /{path}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        using var eventTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "event" + query));
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, eventTimeout.Token);
            Console.WriteLine($"OpenCode diagnostic GET /event: HTTP {(int)response.StatusCode}, content-type={response.Content.Headers.ContentType}.");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"OpenCode diagnostic GET /event: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task<string> ScreenAsync(WebView2 browser) =>
        JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync(
            "Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')")) ?? string.Empty;

    private static T GetField<T>(object instance, string name) where T : class =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().Name, name));

    private static T? GetOptionalField<T>(object instance, string name)
    {
        object? value = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
        return value is T typed ? typed : default;
    }

    private static void SetField(object instance, string name, object value) =>
        (instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().Name, name)).SetValue(instance, value);

    private static void Call(object instance, string name) =>
        (instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().Name, name)).Invoke(instance, null);

    private static async Task UntilAsync(Func<bool> predicate, TimeSpan timeout, string context) =>
        await UntilAsync(() => Task.FromResult(predicate()), timeout, context);

    private static async Task UntilAsync(Func<Task<bool>> predicate, TimeSpan timeout, string context)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
                return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for " + context + ".");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void DeleteFixtureTree(string fixtureRoot)
    {
        string target = Path.GetFullPath(fixtureRoot);
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a fixture outside the temporary directory.");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private sealed class IsolatedOpenCodeEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);
        private bool _disposed;

        public IsolatedOpenCodeEnvironment(string fixtureRoot)
        {
            string config = Path.Combine(fixtureRoot, "config");
            string data = Path.Combine(fixtureRoot, "data");
            string cache = Path.Combine(fixtureRoot, "cache");
            string state = Path.Combine(fixtureRoot, "state");
            string appData = Path.Combine(fixtureRoot, "appdata");
            string localAppData = Path.Combine(fixtureRoot, "localappdata");
            foreach (string directory in new[] { config, data, cache, state, appData, localAppData })
                Directory.CreateDirectory(directory);

            Set("XDG_CONFIG_HOME", config);
            Set("XDG_DATA_HOME", data);
            Set("XDG_CACHE_HOME", cache);
            Set("XDG_STATE_HOME", state);
            Set("APPDATA", appData);
            Set("LOCALAPPDATA", localAppData);
            Set("OPENCODE_CONFIG_DIR", config);
            Set("OPENCODE_DATA_DIR", data);
            Set("OPENCODE_CONFIG", null);
            Set("OPENCODE_CONFIG_CONTENT", null);
            Set("OPENCODE_TUI_CONFIG", null);
            Set("OPENCODE_PERMISSION", null);
            Set("OPENCODE_DISABLE_AUTOUPDATE", "1");
            Set("OPENCODE_DISABLE_MODELS_FETCH", "1");
            Set("OPENCODE_DISABLE_LSP_DOWNLOAD", "1");
            Set("OPENCODE_DISABLE_DEFAULT_PLUGINS", "1");
            Set("OPENCODE_OFFLINE", "true");
        }

        private void Set(string name, string? value)
        {
            _originalValues.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var pair in _originalValues)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
