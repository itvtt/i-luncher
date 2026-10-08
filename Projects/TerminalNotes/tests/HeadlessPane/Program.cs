using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _exitCode;

    [STAThread]
    private static int Main()
    {
        // This suite covers the retained API conversation view explicitly.
        Environment.SetEnvironmentVariable("TERMINALNOTES_OPENCODE_RENDERER", "headless");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            try { await VerifyAsync(); }
            catch (Exception exception)
            {
                _exitCode = 1;
                Console.Error.WriteLine("FAIL: " + exception);
            }
            finally { application.Shutdown(_exitCode); }
        };
        application.Run();
        return _exitCode;
    }

    private static async Task VerifyAsync()
    {
        var installation = await new OpenCodeInstallationService().CheckAsync();
        Require(installation.IsInstalled, "A working OpenCode binary is required: " + installation.Message);
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes Headless " + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project 한글");
        Directory.CreateDirectory(project);
        Console.WriteLine("Fixture: " + root);
        using var environment = new IsolatedEnvironment(root);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(root, "terminalnotes"));
        string plan = Path.Combine(project, "plan.md");
        const string planContent = "# Headless regression\nPreserve this plan unchanged.\n";
        File.WriteAllText(plan, planContent);
        TerminalPane? pane = null;
        TerminalPane? other = null;
        var window = new Window { Width = 980, Height = 720, ShowInTaskbar = false };
        uint? firstProcess = null;
        try
        {
            pane = CreatePane(project, plan, "smoke-1");
            window.Content = pane;
            window.Show();
            await AwaitReady(pane);
            firstProcess = Field<OpenCodeHeadlessHost>(pane, "_headlessOpenCodeHost").ProcessId;
            Require(!Field<ConPtySession>(pane, "_session").IsRunning, "OpenCode unexpectedly started ConPTY.");
            var browser = Field<WebView2>(pane, "_browser");
            Require(browser.Source.AbsolutePath.EndsWith("opencode.html", StringComparison.Ordinal), "The native page was not loaded.");
            if (string.IsNullOrWhiteSpace(await SessionId(browser)))
                await browser.CoreWebView2.ExecuteScriptAsync("window.chrome.webview.postMessage({type:'nativeNew'});");
            await Until(async () => !string.IsNullOrWhiteSpace(await SessionId(browser)), "initial native session");
            string firstSession = await SessionId(browser);
            Console.WriteLine("PASS: real headless server and native page ready without ConPTY or model requests.");

            using (var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }))
            {
                string? password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
                if (!string.IsNullOrEmpty(password))
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes((Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode") + ":" + password)));
                Uri endpoint = Field<OpenCodeHeadlessHost>(pane, "_headlessOpenCodeHost").Endpoint;
                using var changed = await http.PatchAsync(new Uri(endpoint, "session/" + Uri.EscapeDataString(firstSession) + "?directory=" + Uri.EscapeDataString(project)),
                    new StringContent("{\"title\":\"자동 갱신 확인\"}", System.Text.Encoding.UTF8, "application/json"));
                changed.EnsureSuccessStatusCode();
                await Until(async () => (await ScriptString(browser, "document.querySelector('#session-select').selectedOptions[0]?.textContent || ''")).Contains("자동 갱신 확인", StringComparison.Ordinal), "automatic native state refresh", 8);
            }
            Console.WriteLine("PASS: native session changes refresh without user interaction or a model request.");

            await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('textarea').value='크기 변경 중 보존할 입력'; document.querySelector('textarea').dispatchEvent(new Event('input',{bubbles:true}));");
            foreach (var size in new[] { (520d, 460d), (1260d, 820d), (680d, 540d), (980d, 720d) })
            {
                window.Width = size.Item1;
                window.Height = size.Item2;
                await Task.Delay(180);
            }
            Require(pane.IsReady && pane.IsRunning, "Native session exited during window resize.");
            Require(await ScriptString(browser, "document.querySelector('textarea').value") == "크기 변경 중 보존할 입력", "Resize discarded the draft.");
            Require(await SessionId(browser) == firstSession, "Resize changed the session.");
            Require(await browser.ExecuteScriptAsync("Math.abs(document.querySelector('.composer-area').getBoundingClientRect().bottom-innerHeight)<2") == "true",
                "Hiding the notice left unused space below the composer.");
            await Until(() => Task.FromResult(Field<string>(pane, "_nativeComposerDraft") == "크기 변경 중 보존할 입력"), "native draft reaches the host");
            Require((bool)Call(pane, "HasLocalPendingInput")!, "The host ignored the unsent native draft.");
            bool newPaneShortcut = false;
            pane.Shortcut += (key, shift) => newPaneShortcut |= key == "t" && shift;
            await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('textarea').dispatchEvent(new KeyboardEvent('keydown',{key:'t',code:'KeyT',ctrlKey:true,shiftKey:true,bubbles:true,cancelable:true}));");
            await Until(() => Task.FromResult(newPaneShortcut), "native Ctrl+Shift+T reaches the host");
            Console.WriteLine("PASS: repeated window resizing preserves server, exact session and Korean draft.");
            Console.WriteLine("PASS: native drafts block background work and Ctrl+Shift+T requests a new pane.");
            using (var capture = JsonDocument.Parse(await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}")))
            {
                string preview = Path.Combine(root, "native-view.png");
                await File.WriteAllBytesAsync(preview, Convert.FromBase64String(capture.RootElement.GetProperty("data").GetString()!));
                Console.WriteLine("Preview: " + preview);
            }

            await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('textarea').value=''; document.querySelector('textarea').dispatchEvent(new Event('input',{bubbles:true})); window.chrome.webview.postMessage({type:'nativeNew'});");
            await Until(async () => (await SessionId(browser)) is { Length: > 0 } current && current != firstSession, "new native session");
            string secondSession = await SessionId(browser);
            Require(File.ReadAllText(plan) == planContent, "Native session navigation altered the project plan.");
            Console.WriteLine("PASS: native new-session action creates a distinct session and preserves the linked MD.");

            CancellationToken paneLifetime = Field<CancellationToken>(pane, "_headlessLifetimeToken");
            using var firstManual = (CancellationTokenSource)Call(pane, "BeginHeadlessManualDispatch")!;
            pane.Interrupt();
            Require(firstManual.IsCancellationRequested && !paneLifetime.IsCancellationRequested,
                "Interrupt must cancel the current manual dispatch and preserve the live client.");
            using var nextManual = (CancellationTokenSource)Call(pane, "BeginHeadlessManualDispatch")!;
            Require(!nextManual.IsCancellationRequested, "Interrupt permanently disabled subsequent explicit requests.");
            await browser.CoreWebView2.ExecuteScriptAsync("window.chrome.webview.postMessage({type:'nativeAbort'});");
            await Until(() => Task.FromResult(nextManual.IsCancellationRequested), "native stop cancels the active manual request");
            await Task.Delay(400);
            await browser.CoreWebView2.ExecuteScriptAsync("window.chrome.webview.postMessage({type:'nativeNew'});");
            await Until(async () => (await SessionId(browser)) is { Length: > 0 } current && current != secondSession, "new session after stop");
            secondSession = await SessionId(browser);
            Require(pane.IsReady, "Stopping a task disabled the native conversation client.");
            Console.WriteLine("PASS: stop leaves the native client connected and able to open a new session.");

            pane.Dispose();
            Require(paneLifetime.IsCancellationRequested, "Disposal did not cancel pending native operations.");
            window.Content = null;
            await Until(() => Task.FromResult(!ProcessExists(firstProcess)), "owned headless process disposal");
            pane = CreatePane(project, plan, "smoke-1");
            window.Content = pane;
            await AwaitReady(pane);
            browser = Field<WebView2>(pane, "_browser");
            await Until(async () => await SessionId(browser) == secondSession, "exact saved session restoration");
            Console.WriteLine("PASS: reopening restores the exact saved native session after owned process cleanup.");

            // A different pane key must not silently take over the first pane's session.
            other = CreatePane(project, plan, "smoke-2");
            window.Content = other;
            await AwaitReady(other);
            var otherBrowser = Field<WebView2>(other, "_browser");
            if (string.IsNullOrWhiteSpace(await SessionId(otherBrowser)))
                await otherBrowser.CoreWebView2.ExecuteScriptAsync("window.chrome.webview.postMessage({type:'nativeNew'});");
            await Until(async () => !string.IsNullOrWhiteSpace(await SessionId(otherBrowser)), "second pane session");
            Require(await SessionId(otherBrowser) != secondSession, "Two independent panes resumed the same session.");
            Require(pane.IsRunning, "Starting another pane terminated the original server.");
            Console.WriteLine("PASS: a second pane owns a distinct session while the original stays alive.");
        }
        finally
        {
            other?.Dispose();
            pane?.Dispose();
            window.Content = null;
            window.Close();
            AppContext.SetData("TerminalNotes.DataDirectory", null);
        }
    }

    private static TerminalPane CreatePane(string project, string plan, string paneKey) =>
        new(project, startupCommand: "opencode", protectTuiViewport: true,
            planDocumentPath: plan, projectName: "Headless regression", paneKey: paneKey);

    private static async Task AwaitReady(TerminalPane pane) =>
        await Until(() => Task.FromResult(pane.IsReady), "native pane readiness: " + pane.WorkingDirectory, 60);

    private static Task<string> SessionId(WebView2 browser) =>
        ScriptString(browser, "document.querySelector('#session-select')?.value || ''");

    private static async Task<string> ScriptString(WebView2 browser, string expression) =>
        JsonSerializer.Deserialize<string>(await browser.CoreWebView2.ExecuteScriptAsync(expression)) ?? "";

    private static async Task Until(Func<Task<bool>> predicate, string operation, int seconds = 30)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (await predicate()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(operation);
    }

    private static bool ProcessExists(uint? id)
    {
        if (id is null) return false;
        try { using var process = Process.GetProcessById(checked((int)id.Value)); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static T Field<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException("Missing field " + name));

    private static object? Call(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class IsolatedEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _original = new();
        public IsolatedEnvironment(string root)
        {
            foreach (string kind in new[] { "CONFIG", "DATA", "CACHE", "STATE" })
            {
                string path = Path.Combine(root, kind.ToLowerInvariant());
                Directory.CreateDirectory(path);
                Set("XDG_" + kind + "_HOME", path);
            }
            foreach (string name in new[] { "APPDATA", "LOCALAPPDATA" })
            {
                string path = Path.Combine(root, name.ToLowerInvariant());
                Directory.CreateDirectory(path);
                Set(name, path);
            }
            Set("OPENCODE_CONFIG_DIR", Path.Combine(root, "config"));
            Set("OPENCODE_DATA_DIR", Path.Combine(root, "data"));
            foreach (string name in new[] { "OPENCODE_CONFIG", "OPENCODE_CONFIG_CONTENT", "OPENCODE_TUI_CONFIG", "OPENCODE_PERMISSION" }) Set(name, null);
            foreach (string name in new[] { "OPENCODE_DISABLE_AUTOUPDATE", "OPENCODE_DISABLE_MODELS_FETCH", "OPENCODE_DISABLE_LSP_DOWNLOAD", "OPENCODE_DISABLE_DEFAULT_PLUGINS" }) Set(name, "1");
            Set("OPENCODE_OFFLINE", "true");
        }
        private void Set(string name, string? value)
        {
            _original.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose()
        {
            foreach (var (name, value) in _original) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
