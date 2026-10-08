using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private const string Plan = "깔끔한 한국어 웹 계산기를 만들어줘. 사칙연산과 키보드 입력이 가능해야 하고, 완성되면 로컬 서비스를 실제로 실행해서 브라우저로 보여줘.";
    private static bool _diagnosticNativePayload;
    private static bool _continuous;
    private static string? _resumeProject;

    [STAThread]
    private static int Main(string[] args)
    {
        _diagnosticNativePayload = args.Contains("--native-payload", StringComparer.Ordinal);
        _continuous = args.Contains("--continuous", StringComparer.Ordinal);
        int resumeIndex = Array.IndexOf(args, "--resume-project");
        if (resumeIndex >= 0) _resumeProject = Path.GetFullPath(args[resumeIndex + 1]);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = entry.Key?.ToString() ?? "";
            if ((name.StartsWith("TN_WORKFLOW", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("TN_LIVE", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(entry.Value?.ToString()))
            {
                Console.Error.WriteLine($"REFUSED: provider fixture environment variable is set: {name}");
                return 2;
            }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exitCode = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync();
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("REAL_PROJECT_FAILURE: " + exception);
            }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exitCode;
    }

    private static async Task RunAsync()
    {
        string repo = FindRepositoryRoot();
        string runRoot = Path.Combine(repo, "artifacts", "real-project-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(runRoot, "calculator-project");
        if (_resumeProject is not null)
        {
            string artifacts = Path.GetFullPath(Path.Combine(repo, "artifacts")) + Path.DirectorySeparatorChar;
            if (!_resumeProject.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(_resumeProject, "terminal-notes", "automation-runs.json")))
                throw new InvalidOperationException("Resume requires an existing real-test project inside this repository's artifacts.");
            project = _resumeProject;
            runRoot = Path.GetDirectoryName(project)!;
        }
        string controlRoot = Path.Combine(Path.GetTempPath(), "TerminalNotes Real Project " + Guid.NewGuid().ToString("N"));
        string stopFile = Path.Combine(controlRoot, "STOP");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(controlRoot);
        Environment.CurrentDirectory = repo;
        string realOpenCodeBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "opencode-ai", "node_modules", "opencode-windows-x64", "bin");
        if (_diagnosticNativePayload && File.Exists(Path.Combine(realOpenCodeBin, "opencode.exe")))
            Environment.SetEnvironmentVariable("PATH", realOpenCodeBin + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""));

        bool useAutoMode = ReadGlobalUseAutoMode();
        string settingsPath = Path.Combine(controlRoot, "settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new
        {
            Workspace = project,
            CodexWorkspace = project,
            UseAutoMode = useAutoMode,
            SetupCompleted = true,
            OpenCodeOnly = "Y",
            AutoResumeEnabled = true,
            TerminalProfiles = Array.Empty<object>()
        }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        if (_resumeProject is not null)
            await File.WriteAllTextAsync(settingsPath + ".automation-projects.json", JsonSerializer.Serialize(new[] { project }));

        Console.WriteLine("REAL_PROJECT_READY");
        Console.WriteLine("project=" + project);
        Console.WriteLine("settings=" + settingsPath);
        Console.WriteLine("controlStop=" + stopFile);
        Console.WriteLine("cliDiscovery=" + (_diagnosticNativePayload
            ? "explicit --native-payload diagnostic override (test-process PATH only)"
            : "normal application discovery; no test PATH override"));
        Console.WriteLine("provider=model inherited from installed OpenCode configuration; no provider/model override");

        MainWindow? window = null;
        try
        {
            window = new MainWindow(settingsPath, autoStartCodex: true, initialWorkspace: project);
            window.Show();
            Call(window, "RememberProjectName", project, "실제 한국어 계산기");
            if (_resumeProject is null)
            {
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            await UntilCondition(() => editor.IsEnabled, "real project Plan editor", 180);
            string planPath = Path.Combine(project, "terminal-notes", "plan.md");
            await UntilCondition(() => File.Exists(planPath), "app-created canonical Plan file", 30);
            await (Task)Call(window, "ReloadTabDocumentAsync")!;
            await UntilCondition(() => editor.IsEnabled && editor.Text.Length == 0, "canonical Plan snapshot reload", 30);
            editor.Text = Plan;
            var modes = Field<System.Collections.IDictionary>(window, "_planExecutionModes");
            object conductor = modes.Cast<object>()
                .Single(pair => GetProperty(pair, "Key")?.ToString() == "Conductor") is { } entry
                ? GetProperty(entry, "Value")!
                : throw new InvalidOperationException("The Conductor execution mode was not created.");
            ((RadioButton)conductor).IsChecked = true;
            if (_continuous)
            {
                Call(window, "EnsureProjectTaskRow", project);
                var rows = Field<IDictionary>(window, "_projectTaskRows");
                ((CheckBox)GetProperty(rows[project]!, "Loop")!).IsChecked = true;
            }
            var execute = Field<Button>(window, "_tabDocumentExecute");
            await UntilCondition(() => execute.IsEnabled, "Conductor Execute button", 60);
            execute.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(2000);
            Console.WriteLine($"REAL_PROJECT_SUBMIT editorLength={editor.Text.Length} executeEnabled={execute.IsEnabled} planPath={Path.Combine(project, "terminal-notes", "plan.md")} planExists={File.Exists(Path.Combine(project, "terminal-notes", "plan.md"))} planLength={(File.Exists(Path.Combine(project, "terminal-notes", "plan.md")) ? new FileInfo(Path.Combine(project, "terminal-notes", "plan.md")).Length : 0)} mode={FieldValue(window, "_planExecutionMode")} status={Field<TextBlock>(window, "_tabDocumentStatus").Text}");

            }

            string storePath = Path.Combine(project, "terminal-notes", "automation-runs.json");
            AutomationRun? run = null;
            await UntilAsync(async () =>
            {
                if (!File.Exists(storePath)) return false;
                run = (await new AutomationStore(project).LoadAsync()).OrderByDescending(item => item.CreatedAt).FirstOrDefault();
                return run is not null;
            }, "real automation run registration", 120);
            Console.WriteLine($"REAL_PROJECT_RUN id={run!.Id} state={run.State} project={project}");
            if (_resumeProject is not null)
            {
                await UntilCondition(() => FieldValue(window, "_automationOwnership") is not null, "scheduler recovery", 60);
                run = await new AutomationStore(project).GetAsync(run.Id) ?? run;
                bool manualResume = run.State is AutomationState.Paused or AutomationState.Stopped or AutomationState.Failed;
                if (manualResume)
                    await (Task)Call(window, "ResumeProjectTaskAsync", project)!;
                Call(window, "ShowProjectTask", project);
                Console.WriteLine(manualResume ? "REAL_PROJECT_MANUALLY_RESUMED: same saved run, project files, and plan."
                    : "REAL_PROJECT_AUTOMATICALLY_RECOVERED: startup resumed the existing run without a resume command.");
            }

            DateTime nextReport = DateTime.MinValue;
            while (true)
            {
                if (File.Exists(stopFile))
                {
                    Console.WriteLine("REAL_PROJECT_STOP requested=" + stopFile);
                    await StopRunAsync(window, run.Id);
                    break;
                }
                run = await new AutomationStore(project).GetAsync(run.Id) ?? run;
                if (DateTime.UtcNow >= nextReport)
                {
                    nextReport = DateTime.UtcNow.AddSeconds(15);
                    string diagnosticPath = AutomationDiagnostics.LogPath(project, run.Id);
                    Console.WriteLine($"REAL_PROJECT_PROGRESS state={run.State} role={run.NextRole} phase={run.WorkflowPhase} iteration={run.Iteration} diagnostics={diagnosticPath}");
                    if (File.Exists(diagnosticPath)) Console.WriteLine("REAL_PROJECT_DIAGNOSTIC " + LastDiagnostic(diagnosticPath));
                    try { await CaptureRoleTerminalsAsync(window, project, runRoot); }
                    catch (Exception captureError) { Console.WriteLine("REAL_CAPTURE_RETRY " + captureError.GetType().Name); }
                }
                if (_continuous && run.ImprovementCycle >= 2 && run.WorkflowPhase == AutomationWorkflowPhase.PlanReview)
                {
                    Console.WriteLine("REAL_CONTINUOUS_NEXT_PLAN: previous cycle independently verified; distinct next plan passed the structural gate.");
                    await StopRunAsync(window, run.Id);
                    break;
                }
                if (run.State is AutomationState.Completed or AutomationState.Failed or AutomationState.Stopped)
                {
                    if (run.State == AutomationState.Completed)
                    {
                        await (Task)Call(window, "RefreshAutomationRunsAsync")!;
                        Call(window, "ShowProjectTaskList");
                        Call(window, "UpdateProjectTaskActivity");
                        var rows = Field<IDictionary>(window, "_projectTaskRows");
                        string displayed = ((TextBlock)GetProperty(rows[project]!, "State")!).Text;
                        if (displayed != "✓ 완료" || string.IsNullOrWhiteSpace(run.FinalVerificationEvidence))
                            throw new InvalidOperationException("Completed real work lacks its task-row completion or final evidence.");
                        await File.WriteAllTextAsync(Path.Combine(runRoot, "completion-verification.json"), JsonSerializer.Serialize(new
                        {
                            run.Id, run.State, run.WorkflowPhase, TaskRow = displayed, run.FinalVerificationEvidence,
                            run.DeliveryEvidence, run.TotalTokenUsage, Plan = await File.ReadAllTextAsync(Path.Combine(project, "terminal-notes", "plan.md"))
                        }, new JsonSerializerOptions { WriteIndented = true }));
                        window.UpdateLayout();
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var capture = File.Create(Path.Combine(runRoot, "task-completed.png"));
                        encoder.Save(capture);
                        Console.WriteLine("REAL_PROJECT_VERIFIED: final evidence persisted and task row displays ✓ 완료.");
                    }
                    Console.WriteLine($"REAL_PROJECT_TERMINAL state={run.State} project={project} plan={Path.Combine(project, "terminal-notes", "plan.md")}");
                    break;
                }
                await Task.Delay(1000);
            }

            // Keep the completed process and any real local service alive for independent inspection.
            Console.WriteLine("REAL_PROJECT_HOLDING_WINDOW; create control STOP file to stop and exit.");
            while (!File.Exists(stopFile)) await Task.Delay(1000);
        }
        finally
        {
            if (window is not null)
            {
                Call(window, "CloseAllTerminals", false);
                await (Task)Call(window, "RequestExitAsync")!;
            }
            Console.WriteLine("REAL_PROJECT_EXIT project=" + project);
        }
    }

    private static async Task CaptureRoleTerminalsAsync(MainWindow window, string project, string runRoot)
    {
        object runtime = Call(window, "RuntimeFor", project)!;
        var team = (Dictionary<AutomationRole, TabItem>)GetProperty(runtime, "Team")!;
        string directory = Path.Combine(runRoot, "terminal-evidence");
        Directory.CreateDirectory(directory);
        foreach (var (role, tab) in team.ToArray())
        {
            var pane = (TerminalPane)GetProperty(tab.Tag, "Pane")!;
            var browser = Field<Microsoft.Web.WebView2.Wpf.WebView2>(pane, "_browser");
            if (browser.CoreWebView2 is null) continue;
            await browser.ExecuteScriptAsync("if(!window.realInputKinds){window.realInputKinds={mouse:0,control:0,text:0};terminal.onData(d=>{const k=/^\\x1b\\[<[0-9]+;[0-9]+;[0-9]+[Mm]$/.test(d)?'mouse':/^[\\x00-\\x1f]/.test(d)?'control':'text';window.realInputKinds[k]++;});}");
            var capture = Field<TerminalInputCapture>(pane, "_inputCapture");
            await File.WriteAllTextAsync(Path.Combine(directory, role + ".input-state.json"), JsonSerializer.Serialize(new
            {
                capture.HasPendingInput,
                pane.IsReady, pane.IsRunning, pane.IsLoaded, pane.IsVisible, pane.ActualWidth, pane.ActualHeight,
                ready = FieldValue(pane, "_ready"), webReady = FieldValue(pane, "_pendingWebReadyDimensions"), failed = FieldValue(pane, "_failed"),
                ambiguous = FieldValue(capture, "_ambiguous"),
                textElements = ((ICollection)FieldValue(capture, "_text")!).Count,
                pendingBytes = ((string)FieldValue(capture, "_pending")!).Length,
                inputKinds = await browser.ExecuteScriptAsync("JSON.stringify(window.realInputKinds)")
            }));
            string screen = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync(
                "Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')")) ?? "";
            await File.WriteAllTextAsync(Path.Combine(directory, role + ".txt"), screen);
            await using var image = File.Create(Path.Combine(directory, role + ".png"));
            await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, image);
        }
    }

    private static bool ReadGlobalUseAutoMode()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotes", "settings.json");
        if (!File.Exists(path)) return false;
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.TryGetProperty("UseAutoMode", out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TerminalNotes.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate TerminalNotes.csproj above the test executable.");
    }

    private static Task StopRunAsync(MainWindow window, Guid runId) => ControlRunAsync(window, runId, "stop");

    private static async Task ControlRunAsync(MainWindow window, Guid runId, string action)
    {
        Call(window, "ShowAutomationWindow");
        await UntilCondition(() => FieldValue(window, "_automationRunsList") is ListBox, "automation run list for graceful stop", 30);
        await (Task)Call(window, "RefreshAutomationRunsAsync")!;
        var list = Field<ListBox>(window, "_automationRunsList");
        object? item = list.Items.Cast<object>().FirstOrDefault(candidate =>
            GetProperty(GetProperty(candidate, "Run")!, "Id") is Guid id && id == runId);
        if (item is null) throw new InvalidOperationException("The real run was not present in the automation control list.");
        list.SelectedItem = item;
        await (Task)Call(window, "ControlAutomationAsync", action, window)!;
    }

    private static string LastDiagnostic(string path) => File.ReadLines(path).LastOrDefault() ?? "";
    private static async Task UntilCondition(Func<bool> condition, string name, int seconds) => await UntilAsync(() => Task.FromResult(condition()), name, seconds);
    private static async Task UntilAsync(Func<Task<bool>> condition, string name, int seconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(250); }
        throw new TimeoutException("Timed out waiting for " + name);
    }
    private static T Field<T>(object target, string name) => (T)(FieldValue(target, name) ?? throw new InvalidOperationException("Null field: " + name));
    private static object? FieldValue(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static object? GetProperty(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
