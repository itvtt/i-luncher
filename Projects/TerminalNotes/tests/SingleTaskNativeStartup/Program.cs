using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _exitCode;

    [STAThread]
    private static int Main()
    {
        bool submit = string.Equals(Environment.GetEnvironmentVariable("TERMINALNOTES_TEST_SUBMIT"), "1", StringComparison.Ordinal);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync();
                Console.WriteLine(submit
                    ? "PASS: native single TASK pane confirmed empty-plan question submission."
                    : "PASS: native single TASK pane reached idle and accepted-ready state without submitting a prompt.");
            }
            catch (Exception exception)
            {
                _exitCode = 1;
                Console.Error.WriteLine("FAIL: " + exception);
            }
            finally { app.Shutdown(); }
        };
        app.Run();
        return _exitCode;
    }

    private static async Task RunAsync()
    {
        bool submit = string.Equals(Environment.GetEnvironmentVariable("TERMINALNOTES_TEST_SUBMIT"), "1", StringComparison.Ordinal);
        string payload = Environment.GetEnvironmentVariable("TERMINALNOTES_TEST_OPENCODE") ??
            throw new InvalidOperationException("Set TERMINALNOTES_TEST_OPENCODE to the installed native OpenCode executable.");
        payload = Path.GetFullPath(payload);
        if (!File.Exists(payload) || !payload.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The explicit native OpenCode payload is missing or invalid.", payload);

        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Single TASK Native " + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(fixture, "기존 프로젝트 with spaces");
        string data = Path.Combine(fixture, "terminal-notes-data");
        string settings = Path.Combine(fixture, "settings.json");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(data);
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        await File.WriteAllTextAsync(plan, "# Native startup fixture\nDo not submit or execute this plan.\n");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        {
            Workspace = fixture,
            CodexWorkspace = fixture,
            SetupCompleted = true,
            OpenCodeOnly = "Y",
            Width = 1100,
            Height = 760
        }));

        string shimDirectory = Path.Combine(fixture, "bin");
        Directory.CreateDirectory(shimDirectory);
        string shim = Path.Combine(shimDirectory, "opencode.ps1");
        await File.WriteAllTextAsync(shim,
            "& '" + payload.Replace("'", "''", StringComparison.Ordinal) + "' @args\nexit $LASTEXITCODE\n");

        string[] isolatedNames =
        [
            "PATH", "OPENCODE_CONFIG_CONTENT", "OPENCODE_CONFIG_DIR", "OPENCODE_DISABLE_AUTOUPDATE",
            "OPENCODE_CONFIG", "OPENCODE_TUI_CONFIG", "OPENCODE_WORKSPACE_ID", "OPENCODE_DB",
            "OPENCODE_DISABLE_PROJECT_CONFIG", "OPENCODE_DISABLE_MODELS_FETCH", "OPENCODE_PURE",
            "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME",
            "TERMINALNOTES_TEST_OPENCODE"
        ];
        var previousEnvironment = isolatedNames.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        string originalDirectory = Environment.CurrentDirectory;
        object? originalAppData = AppContext.GetData("TerminalNotes.DataDirectory");
        MainWindow? window = null;
        bool passed = false;
        try
        {
            Environment.CurrentDirectory = fixture;
            Environment.SetEnvironmentVariable("PATH", shimDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", Path.Combine(fixture, "opencode-config"));
            Environment.SetEnvironmentVariable("OPENCODE_DISABLE_AUTOUPDATE", "1");
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_CONTENT", CreateIsolatedConfig());
            foreach (string name in new[] { "OPENCODE_CONFIG", "OPENCODE_TUI_CONFIG", "OPENCODE_WORKSPACE_ID", "OPENCODE_DB" })
                Environment.SetEnvironmentVariable(name, null);
            foreach (string name in new[] { "OPENCODE_DISABLE_PROJECT_CONFIG", "OPENCODE_DISABLE_MODELS_FETCH", "OPENCODE_PURE" })
                Environment.SetEnvironmentVariable(name, "1");
            foreach (string name in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" })
                Environment.SetEnvironmentVariable(name, Path.Combine(fixture, name));
            Environment.SetEnvironmentVariable("TERMINALNOTES_TEST_OPENCODE", null);
            AppContext.SetData("TerminalNotes.DataDirectory", data);

            var hooks = new OpenCodeStartupHooks(
                token => CheckExplicitPayloadAsync(payload, token),
                ConfirmInstallation: () => false,
                InstallAsync: _ => throw new InvalidOperationException("The harness must never invoke an installer."));
            window = new MainWindow(settings, autoStartCodex: true, startupHooks: hooks);
            window.Show();

            await UntilAsync(() => FieldValue(window, "_openCodeReady") is true, "startup hook readiness", TimeSpan.FromSeconds(25));
            Call(window, "EnsureProjectTaskRow", project);
            var tab = (TabItem)(Call(window, "EnsureSingleProjectTerminal", project)
                ?? throw new InvalidOperationException("TASK did not create its single terminal."));
            var pane = (TerminalPane)(Property(tab.Tag!, "Pane")
                ?? throw new InvalidOperationException("The TASK tab has no terminal pane."));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (!pane.CanExecuteSavedPlan && DateTime.UtcNow < deadline)
            {
                var currentDiagnostic = Property(pane, "NativeAutomationDiagnostic");
                if (Equals(Property(currentDiagnostic!, "Failed"), true) || Equals(Property(currentDiagnostic!, "Disposed"), true)) break;
                await Task.Delay(100);
            }

            if (!pane.CanExecuteSavedPlan)
            {
                var diagnostic = Property(pane, "NativeAutomationDiagnostic");
                string rendered = $"TASK native startup failed or did not become executable before the deadline. " +
                    $"Ready={pane.IsReady}; running={pane.IsRunning}; status={Property(pane, "NativeOpenCodeStatus")}; " +
                    $"block={pane.PlanExecutionBlockReason}; ActivityLabel={pane.ActivityLabel}; monitor={Property(pane, "NativeStatusMonitorPhase")}; " +
                    $"lastFailure={Property(pane, "LastPlanExecutionFailure")}; notice={Property(FieldValue(pane, "_notice")!, "Text")}; supervision={FieldValue(pane, "_nativeAutomationSupervisionNotice")}; " +
                    $"endpoint={Property(pane, "NativeOpenCodeEndpoint")}; " +
                    $"diag={JsonSerializer.Serialize(diagnostic)}; fixture={fixture}";
                if (Equals(Property(diagnostic!, "Failed"), true) || Equals(Property(diagnostic!, "Disposed"), true))
                    throw new InvalidOperationException(rendered);
                throw new TimeoutException(rendered);
            }

            if (!pane.IsReady || !pane.IsRunning || !Equals(Property(pane, "NativeOpenCodeStatus"), OpenCodeStatusKind.Idle))
                throw new InvalidOperationException($"Invalid ready state: ready={pane.IsReady}, running={pane.IsRunning}, status={Property(pane, "NativeOpenCodeStatus")}, fixture={fixture}");
            if (!string.Equals(Path.GetFullPath(pane.WorkingDirectory), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The native TASK pane did not retain the fixture project working directory.");

            Console.WriteLine($"Native TASK pane ready: project={project}; status={Property(pane, "NativeOpenCodeStatus")}; endpoint={Property(pane, "NativeOpenCodeEndpoint")}");
            if (submit)
            {
                const string question = "\uc774\u0020\ud504\ub85c\uc81d\ud2b8\uc5d0\ub294\u0020\uc544\uc9c1\u0020\uc2e4\ud589\ud560\u0020\ud50c\ub79c\uc774\u0020\uc5c6\uc2b5\ub2c8\ub2e4\u002e\u0020\uc0ac\uc6a9\uc790\uc5d0\uac8c\u0020\uc791\uc5c5\u0020\ubaa9\ud45c\ub098\u0020\uad6c\ud604\u0020\ud50c\ub79c\uc744\u0020\uc785\ub825\ud574\u0020\ub2ec\ub77c\uace0\u0020\ud55c\uad6d\uc5b4\ub85c\u0020\uc9e7\uac8c\u0020\uc694\uccad\ud55c\u0020\ub4a4\u0020\uae30\ub2e4\ub9ac\uc138\uc694\u002e\u0020\uc785\ub825\uc744\u0020\ubc1b\uae30\u0020\uc804\uc5d0\ub294\u0020\uc791\uc5c5\uc744\u0020\uc2dc\uc791\ud558\uc9c0\u0020\ub9c8\uc138\uc694\u002e";
                object monitor = FieldValue(pane, "_openCodeStatus") ?? throw new InvalidOperationException("Native status monitor is unavailable.");
                long confirmationBaseline = Convert.ToInt64(Property(monitor, "SubmissionConfirmationVersion"));
                bool submitted = await pane.SubmitProjectInputAsync(question, isQuestion: true);
                long confirmationVersion = Convert.ToInt64(Property(monitor, "SubmissionConfirmationVersion"));
                if (!submitted)
                {
                    Console.Error.WriteLine($"Submission failure diagnostics: ActivityLabel={pane.ActivityLabel}; status={Property(pane, "NativeOpenCodeStatus")}; monitor={Property(pane, "NativeStatusMonitorPhase")}; block={pane.PlanExecutionBlockReason}; lastFailure={Property(pane, "LastPlanExecutionFailure")}; confirmationVersion={confirmationBaseline}->{confirmationVersion}; native={JsonSerializer.Serialize(Property(pane, "NativeAutomationDiagnostic"))}");
                    throw new InvalidOperationException("Native OpenCode did not confirm submission of the empty-plan question.");
                }
                if (confirmationVersion <= confirmationBaseline)
                    throw new InvalidOperationException($"SubmitProjectInputAsync returned true without a newer native confirmation version: baseline={confirmationBaseline}; current={confirmationVersion}.");
                Console.WriteLine($"Native empty-plan question submission confirmed by OpenCode message.updated: version {confirmationBaseline} -> {confirmationVersion}.");
            }
            passed = true;
        }
        finally
        {
            if (window is not null)
            {
                Call(window, "DisposeResources");
                window.Close();
            }
            Environment.CurrentDirectory = originalDirectory;
            foreach (var (name, value) in previousEnvironment)
                Environment.SetEnvironmentVariable(name, value);
            AppContext.SetData("TerminalNotes.DataDirectory", originalAppData);
            if (passed)
            {
                string fixtureRoot = Path.GetFullPath(fixture);
                string allowedRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fixtureRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Refusing to clean a fixture outside the temporary directory.");
                // ConPTY and WebView release their file handles after disposal.
                for (int attempt = 0; Directory.Exists(fixtureRoot); attempt++)
                {
                    try { Directory.Delete(fixtureRoot, recursive: true); }
                    catch (IOException) when (attempt < 20) { await Task.Delay(250); }
                    catch (UnauthorizedAccessException) when (attempt < 20) { await Task.Delay(250); }
                }
            }
            else Console.Error.WriteLine("Preserved native startup fixture for diagnosis: " + fixture);
        }
    }

    private static async Task<bool> CheckExplicitPayloadAsync(string payload, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(payload)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            return false;
        }
        return process.ExitCode == 0;
    }

    private static string CreateIsolatedConfig() => JsonSerializer.Serialize(new
    {
        model = "fixture/test-model",
        small_model = "fixture/test-model",
        enabled_providers = new[] { "fixture" },
        provider = new Dictionary<string, object>
        {
            ["fixture"] = new
            {
                name = "Isolated startup fixture",
                npm = "@ai-sdk/openai-compatible",
                env = Array.Empty<string>(),
                options = new { apiKey = "fixture-only", baseURL = "http://127.0.0.1:9/v1" },
                models = new Dictionary<string, object>
                {
                    ["test-model"] = new
                    {
                        id = "test-model", name = "Startup fixture", attachment = false,
                        reasoning = false, temperature = false, tool_call = true,
                        release_date = "2025-01-01", limit = new { context = 32_000, output = 4_000 },
                        cost = new { input = 0, output = 0 }, options = new { }
                    }
                }
            }
        }
    });

    private static async Task UntilAsync(Func<bool> predicate, string description, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for " + description + ".");
            await Task.Delay(50);
        }
    }

    private static object? Call(object instance, string name, params object?[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(instance, args);

    private static object? FieldValue(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance);

    private static object? Property(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance);
}
