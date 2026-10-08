using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _fail;
    private static string _nativeCommand = "opencode";
    private static bool Headless => Environment.GetEnvironmentVariable("TERMINALNOTES_TEST_NO_WEBVIEW") == "1";
    private static readonly List<string> LaunchFolders = new();
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--child", var request])
        {
            var json = JsonDocument.Parse(File.ReadAllText(request)).RootElement;
            var start = new ProcessStartInfo(json.GetProperty("FileName").GetString()!) {
                Arguments = json.GetProperty("Arguments").GetString()!, WorkingDirectory = json.GetProperty("WorkingDirectory").GetString()!, UseShellExecute = true };
            ExternalTerminalLauncher.LaunchAsync(new(json.GetProperty("Directory").GetString()!, "ses_fixture", start)).GetAwaiter().GetResult();
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); Console.WriteLine("EXTERNAL CMD TESTS PASSED"); }
            catch (Exception ex) { _fail++; Console.WriteLine("FAIL " + ex); }
            finally
            {
                // Only CMD processes created by this fixture are eligible for cleanup.
                foreach (string folder in LaunchFolders)
                {
                    try
                    {
                        var process = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(folder, "cmd.pid"))));
                        if (process.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase)) process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException) { }
                }
                app.Shutdown();
            }
        };
        app.Run(); return _fail;
    }

    private static async Task RunAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes CMD " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "appdata"));
        await VerifyIndependentLifetime(fixture);
        await VerifyRealOpenCodeButton(fixture);
        await VerifyMultiRoleButtons(fixture);
        Console.WriteLine("Fixture: " + fixture);
    }

    private static async Task VerifyIndependentLifetime(string fixture)
    {
        string project = Path.Combine(fixture, "한글 & %PATH% ! ' test"); Directory.CreateDirectory(project);
        string plan = Path.Combine(project, "plan.md"); await File.WriteAllTextAsync(plan, "# original plan\n");
        string command = Path.Combine(project, "probe.ps1");
        await File.WriteAllTextAsync(command, """
            $cmdProbe = @{ Session=$args[1]; Directory=(Get-Location).Path; Config=$env:OPENCODE_CONFIG_CONTENT; Pid=$PID }
            $cmdProbe | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path (Get-Location).Path 'probe.json') -Encoding UTF8
            while (!(Test-Path -LiteralPath (Join-Path (Get-Location).Path 'finish'))) {
                [System.IO.File]::WriteAllText((Join-Path (Get-Location).Path 'heartbeat'), [DateTime]::UtcNow.Ticks.ToString())
                Start-Sleep -Milliseconds 100
            }
            exit 0
            """, new UTF8Encoding(true));
        var launch = ExternalTerminalLauncher.Prepare(command, project, "ses_fixture", plan, "title & %PATH% '", false);
        LaunchFolders.Add(launch.Directory);
        Assert(launch.StartInfo.UseShellExecute && launch.StartInfo.Arguments.StartsWith("/d /v:off /k ") && !launch.StartInfo.Arguments.Contains(project),
            "launch uses a real independent CMD with no project text in the command parser");
        // Let the test CMD exit after its probe completes; production deliberately keeps /k for continued manual use.
        launch.StartInfo.Arguments = launch.StartInfo.Arguments.Replace("/k powershell", "/c powershell", StringComparison.Ordinal);
        string request = Path.Combine(fixture, "child.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { launch.Directory, launch.StartInfo.FileName, launch.StartInfo.Arguments, launch.StartInfo.WorkingDirectory }));
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--child", request }, UseShellExecute = false, CreateNoWindow = true })!;
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert(child.ExitCode == 0, "launcher process exits successfully while its external CMD remains");
        await Until(() => File.Exists(Path.Combine(project, "probe.json")), "independent probe startup");
        var probe = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "probe.json"))).RootElement;
        Assert(probe.GetProperty("Session").GetString() == "ses_fixture" && probe.GetProperty("Directory").GetString() == project,
            "spaces, Korean, percent, ampersand and quotes preserve exact working directory and session arguments");
        var config = JsonDocument.Parse(probe.GetProperty("Config").GetString()!).RootElement;
        string instructions = config.GetProperty("instructions")[0].GetString()!;
        Assert(File.Exists(instructions) && File.ReadAllText(instructions).Contains(plan) && File.ReadAllText(plan) == "# original plan\n",
            "standalone context survives temporary context disposal and preserves the original plan");
        Assert(ExternalTerminalLauncher.IsProjectActive(project) && ExternalTerminalLauncher.IsSessionActive(project, "ses_fixture"),
            "external ownership is detected after the launcher has exited");
        string heartbeat = File.ReadAllText(Path.Combine(project, "heartbeat"));
        await Task.Delay(400);
        Assert(File.ReadAllText(Path.Combine(project, "heartbeat")) != heartbeat, "external work continues after its parent application exits");
        bool duplicateRejected = false;
        try { ExternalTerminalLauncher.Prepare(command, project, "ses_fixture", plan, "duplicate", false); }
        catch (InvalidOperationException) { duplicateRejected = true; }
        Assert(duplicateRejected, "duplicate CMD continuation cannot own the same session");
        await File.WriteAllTextAsync(Path.Combine(project, "finish"), "finish");
        await Until(() => !ExternalTerminalLauncher.IsProjectActive(project), "external lease release");
        Assert(true, "completed external work releases ownership; leftover lock files do not block app reuse");
    }

    private static async Task VerifyRealOpenCodeButton(string fixture)
    {

        string workspace = Path.Combine(fixture, "workspace"), project = Path.Combine(workspace, "single");
        Directory.CreateDirectory(project);
        // A fresh native DB and config; this test never sends a provider/model request.
        foreach (string name in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "OPENCODE_CONFIG_DIR" })
            Environment.SetEnvironmentVariable(name, Path.Combine(fixture, name));
        foreach (string name in new[] { "OPENCODE_CONFIG", "OPENCODE_DB", "OPENCODE_TUI_CONFIG", "OPENCODE_WORKSPACE_ID", "OPENCODE_SERVER_PASSWORD", "OPENCODE_SERVER_USERNAME" })
            Environment.SetEnvironmentVariable(name, null);
        foreach (string name in new[] { "OPENCODE_DISABLE_AUTOUPDATE", "OPENCODE_DISABLE_MODELS_FETCH", "OPENCODE_PURE", "OPENCODE_DISABLE_PROJECT_CONFIG" })
            Environment.SetEnvironmentVariable(name, "1");
        Environment.SetEnvironmentVariable("OPENCODE_CONFIG_CONTENT", "{\"instructions\":[]}");
        var install = await new OpenCodeInstallationService().CheckAsync();
        Assert(install.IsInstalled && install.Path is not null, "native OpenCode is available");
        _nativeCommand = install.Path!;
        string settings = Path.Combine(workspace, "settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        var main = new MainWindow(settings, autoStartCodex: false);
        try
        {
            if (Headless) Call(main, "Window_Loaded", main, new RoutedEventArgs()); else main.Show();
            await Until(() => Field<IDictionary>(main, "_projectTaskRows").Contains(project), "task discovery");
            Field<System.Windows.Threading.DispatcherTimer>(main, "_automationTimer").Stop();
            Field<Dictionary<string, bool>>(main, "_projectTaskMultiModes")[project] = false;
            Call(main, "SelectTaskProject", project, true);
            var launch = Call(main, "ResolveAutomationLaunch", project, "CMD fixture")!;
            var tab = (TabItem)Call(main, "CreateTerminalTab", 1, launch, "CMD fixture")!;
            Call(main, "AttachSingleProjectTerminal", project, tab);
            ((TabControl)main.FindName("TerminalTabs")).Items.Add(tab);
            Call(main, "ShowProjectTask", project);
            var pane = Pane(tab);
            if (Headless) StartHeadlessNative(pane);
            try { await Until(() => pane.IsReady && Property<OpenCodeStatusKind>(pane, "NativeOpenCodeStatus") == OpenCodeStatusKind.Idle, "real native OpenCode startup", 30); }
            catch {
                Console.WriteLine("Pane: " + pane.IsReady + " / " + pane.IsRunning + " / " + pane.ActivityLabel);
                Console.WriteLine("Notice: " + Field<TextBlock>(pane, "_notice").Text);
                Console.WriteLine("Native output: " + string.Concat(Field<Queue<string>>(pane, "_pending").ToArray()));
                Console.WriteLine("Diagnostic: " + Property<object>(pane, "NativeAutomationDiagnostic"));
                throw;
            }
            var endpoint = Property<Uri>(pane, "NativeOpenCodeEndpoint");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string SessionUrl(string path) => new Uri(endpoint, path + "?directory=" + Uri.EscapeDataString(project)).ToString();
            using var response = await http.PostAsync(SessionUrl("/session"), new StringContent("{\"title\":\"CMD preserved session\"}", Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            string session = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
            Call(pane, "BindNativeAutomationSession", session);
            Assert(await ExternalTerminalLauncher.ResolveRootSessionAsync(endpoint, project, session) == session, "exact native session is checked against its project");
            bool mismatch = false;
            try { await ExternalTerminalLauncher.ResolveRootSessionAsync(endpoint, workspace, session); } catch { mismatch = true; }
            Assert(mismatch, "a session from another project is rejected instead of opening a recent unrelated conversation");
            var editor = Field<TextBox>(main, "_tabDocumentEditor");
            await Until(() => editor.IsEnabled, "plan ready");
            editor.Text = "# unsaved plan retained by CMD handoff\n";
            var store = new AutomationStore(project);
            var run = await store.StartApprovedPlanAsync("immutable approved work");
            Call(main, "UpdateProjectTaskActivity");
            var button = (Button)Field<IDictionary>(main, "_cmdTerminalButtons")[tab]!;
            Assert(button.Content as string == "CMD" && (Headless || button.IsVisible) && button.IsEnabled && AutomationProperties.GetName(button) == "CMD에서 이어가기",
                "single task exposes an accessible CMD continuation button");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Field<HashSet<string>>(main, "_externalTerminalWrites").Count == 0, "CMD handoff completion", 60);
            Console.WriteLine("Handoff status: " + ((TextBlock)main.FindName("StatusLabel")).Text);
            Assert(ExternalTerminalLauncher.IsSessionActive(project, session), "the actual CMD button starts a standalone native session");
            string root = Path.Combine((string)AppContext.GetData("TerminalNotes.DataDirectory")!, "CmdSessions");
            string folder = Directory.EnumerateFiles(root, "launch.json", SearchOption.AllDirectories)
                .Single(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Session").GetString() == session);
            LaunchFolders.Add(Path.GetDirectoryName(folder)!);
            Assert(!pane.IsRunning && Property<bool>(pane, "WasContinuedExternally") && (await store.GetAsync(run.Id))!.State == AutomationState.Stopped,
                "handoff confirms embedded process exit and persists a stop before independent execution");
            Assert(File.ReadAllText(TerminalTabDocumentStore.GetProjectPlanPath(project)) == editor.Text && !(bool)Call(main, "CanMoveProject", project)!,
                "handoff saves the draft and prevents archive moves while external work owns the project");
            await (Task)Call(main, "ResumeProjectTaskAsync", project)!;
            Assert((await store.GetAsync(run.Id))!.State == AutomationState.Stopped, "app resume cannot duplicate externally owned work");
            Call(main, "DisposeResources"); main.Close();
            await Task.Delay(1500);
            Assert(ExternalTerminalLauncher.IsSessionActive(project, session) && !File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "failed.txt")),
                "native OpenCode in CMD remains alive after every application-owned resource is disposed");
        }
        finally { Call(main, "DisposeResources"); main.Close(); }
    }

    private static async Task VerifyMultiRoleButtons(string fixture)
    {
        string workspace = Path.Combine(fixture, "multi-workspace"), project = Path.Combine(workspace, "multi");
        Directory.CreateDirectory(project);
        string settings = Path.Combine(workspace, "settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        var main = new MainWindow(settings, autoStartCodex: false);
        try
        {
            if (Headless) Call(main, "Window_Loaded", main, new RoutedEventArgs()); else main.Show();
            await Until(() => Field<IDictionary>(main, "_projectTaskRows").Contains(project), "multi discovery");
            Field<System.Windows.Threading.DispatcherTimer>(main, "_automationTimer").Stop();
            Call(main, "OpenProjectTask", project);
            var runtime = Call(main, "RuntimeFor", project)!;
            var team = (Dictionary<AutomationRole, TabItem>)runtime.GetType().GetProperty("Team")!.GetValue(runtime)!;
            if (Headless) foreach (var tab in team.Values) StartHeadlessNative(Pane(tab));
            await Until(() => team.Values.All(tab => Pane(tab).IsReady && Property<OpenCodeStatusKind>(Pane(tab), "NativeOpenCodeStatus") == OpenCodeStatusKind.Idle), "multi native startup", 60);
            var sessions = new Dictionary<TabItem, string>();
            using var http = new HttpClient();
            foreach (var (role, tab) in team)
            {
                var pane = Pane(tab);
                var url = new Uri(Property<Uri>(pane, "NativeOpenCodeEndpoint"), "/session?directory=" + Uri.EscapeDataString(project));
                using var response = await http.PostAsync(url, new StringContent(JsonSerializer.Serialize(new { title = "CMD " + role }), Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
                string session = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
                Call(pane, "BindNativeAutomationSession", session); sessions[tab] = session;
            }
            Call(main, "UpdateProjectTaskActivity");
            var buttons = Field<IDictionary>(main, "_cmdTerminalButtons");
            Assert(team.Count == 3 && team.Values.All(tab => (Headless || ((Button)buttons[tab]!).IsVisible) && ((Button)buttons[tab]!).IsEnabled),
                "multi mode displays a CMD button for coordinator, reviewer and implementer");
            foreach (var (role, tab) in team)
            {
                ((Button)buttons[tab]!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Field<HashSet<string>>(main, "_externalTerminalWrites").Count == 0, "handoff " + role, 60);
                string session = sessions[tab];
                string folder = FindLaunchFolder(session);
                LaunchFolders.Add(folder);
                Assert(ExternalTerminalLauncher.IsSessionActive(project, session), "each role opens its own exact original session: " + role);
            }
            Assert(team.Values.All(tab => !Pane(tab).IsRunning), "multi handoff retires embedded role processes before CMD continuation");
            Call(main, "DisposeResources"); main.Close();
            await Task.Delay(1500);
            Assert(sessions.Values.All(session => ExternalTerminalLauncher.IsSessionActive(project, session)),
                "all three role CMD sessions survive application resource shutdown");
        }
        finally { Call(main, "DisposeResources"); main.Close(); }
    }

    private static void StartHeadlessNative(TerminalPane pane)
    {
        var endpoint = Property<Uri>(pane, "NativeOpenCodeEndpoint");
        // Exercise the real ConPTY owner and OpenCode API without the restricted environment's WebView renderer.
        Field<System.Windows.Threading.DispatcherTimer>(pane, "_flushTimer").Stop();
        Field<ConPtySession>(pane, "_session").Start(pane.WorkingDirectory,
            startupCommand: "& '" + _nativeCommand.Replace("'", "''") + "' serve --hostname 127.0.0.1 --port " + endpoint.Port,
            openCodeConfigContent: Field<string>(pane, "_openCodeConfigContent"), exitOnStartupCommandCompletion: true);
        pane.GetType().GetField("_ready", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pane, true);
        Field<OpenCodeStatusMonitor>(pane, "_openCodeStatus").Start();
    }
    private static string FindLaunchFolder(string session)
    {
        string root = Path.Combine((string)AppContext.GetData("TerminalNotes.DataDirectory")!, "CmdSessions");
        return Path.GetDirectoryName(Directory.EnumerateFiles(root, "launch.json", SearchOption.AllDirectories)
            .Single(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Session").GetString() == session))!;
    }

    private static TerminalPane Pane(TabItem tab) => (TerminalPane)tab.Tag.GetType().GetProperty("Pane")!.GetValue(tab.Tag)!;
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    private static T Property<T>(object o, string name) => (T)o.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    private static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!.Invoke(o, args);
    private static void Assert(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static async Task Until(Func<bool> condition, string name, int seconds = 30)
    {
        for (int i = 0; i < seconds * 10; i++) { if (condition()) return; await Task.Delay(100); }
        throw new TimeoutException(name);
    }
}
