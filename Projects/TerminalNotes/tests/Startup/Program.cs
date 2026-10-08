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
    private static int _failure;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--startup-fake-opencode") return RunFakeOpenCode(args[1..]);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => await RunAsync(app);
        app.Run();
        return _failure;
    }

    private static async Task RunAsync(Application app)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Startup " + Guid.NewGuid().ToString("N"));
        string fakeBin = Path.Combine(fixture, "fake bin");
        string trace = Path.Combine(fixture, "fake cli trace.txt");
        string originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string? originalTrace = Environment.GetEnvironmentVariable("TN_STARTUP_TRACE");
        string originalDirectory = Environment.CurrentDirectory;
        Directory.CreateDirectory(fakeBin);
        WriteFakeOpenCode(fakeBin);
        Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + originalPath);
        Environment.SetEnvironmentVariable("TN_STARTUP_TRACE", trace);
        Environment.CurrentDirectory = fixture;
        bool succeeded = false;

        try
        {
            await VerifyInstalledPathCreatesWorkspaceAndStartsTerminalAsync(fixture, trace);
            await VerifyDeclineDoesNotInstallOrStartAsync(fixture);
            await VerifyInstallIsRecheckedAndStillMissingDoesNotStartAsync(fixture);
            await VerifyInstallThenReadyStartsTerminalAsync(fixture, trace);
            await VerifyWorkspaceSetupRunsAfterReadinessAndPersistsAsync(fixture, trace);
            await VerifyInitialWorkspacePlanIsCreatedBeforeLoadAndPreservesLegacyAsync(fixture, trace);
            await VerifyWorkspaceSetupCancellationDoesNotCreateFolderAsync(fixture);
            await VerifySetupCompletedSkipsWizardOnRestartAsync(fixture, trace);
            await VerifyAutoStartDisabledSkipsWorkspaceWizardAsync(fixture);
            await VerifyRepeatedClicksDuringProbeStaySingleFlightAsync(fixture);
            await VerifyDisposalCancelsPendingStartupAsync(fixture);
            Console.WriteLine("Startup regression tests passed.");
            succeeded = true;
        }
        catch (Exception exception)
        {
            _failure = 1;
            Console.Error.WriteLine(exception);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("TN_STARTUP_TRACE", originalTrace);
            Environment.CurrentDirectory = originalDirectory;
            app.Shutdown();
            if (succeeded)
            {
                try { Directory.Delete(fixture, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else Console.Error.WriteLine("Startup fixture retained: " + fixture);
        }
    }

    private static async Task VerifyInstalledPathCreatesWorkspaceAndStartsTerminalAsync(string fixture, string trace)
    {
        string workspace = Path.Combine(fixture, "installed path workspace");
        Require(!Directory.Exists(workspace), "The installed-path workspace must begin missing.");
        int checks = 0, confirmations = 0, installs = 0;
        var hooks = new OpenCodeStartupHooks(
            _ => { Interlocked.Increment(ref checks); return Task.FromResult(true); },
            () => { Interlocked.Increment(ref confirmations); return false; },
            _ => { Interlocked.Increment(ref installs); return Task.FromResult(NotConnected()); });

        MainWindow window = ShowStartupWindow(fixture, "installed", workspace, hooks);
        try
        {
            await UntilAsync(() => Tabs(window).Items.Count == 1 && File.Exists(trace), "installed startup terminal and fake CLI launch");
            Require(Directory.Exists(workspace), "Startup creates a missing configured workspace.");
            Require(checks == 1 && confirmations == 0 && installs == 0,
                "A ready OpenCode installation skips the consent prompt and installation hook.");
            Require(Field<bool>(window, "_openCodeReady"), "A successful startup probe marks OpenCode ready.");
        }
        finally { CloseForTest(window); }
        File.Delete(trace);
    }

    private static async Task VerifyDeclineDoesNotInstallOrStartAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "declined workspace");
        int checks = 0, confirmations = 0, installs = 0;
        var hooks = new OpenCodeStartupHooks(
            _ => { Interlocked.Increment(ref checks); return Task.FromResult(false); },
            () => { Interlocked.Increment(ref confirmations); return false; },
            _ => { Interlocked.Increment(ref installs); return Task.FromResult(NotConnected()); });

        MainWindow window = ShowStartupWindow(fixture, "declined", workspace, hooks);
        try
        {
            await UntilStartupSettlesAsync(window, () => checks == 1 && confirmations == 1);
            Require(Tabs(window).Items.Count == 0 && installs == 0,
                "Declining installation creates no terminal and does not invoke the installer.");
            Require(!Directory.Exists(workspace), "Declining installation must not create the workspace before setup is accepted.");
        }
        finally { CloseForTest(window); }
    }

    private static async Task VerifyInstallIsRecheckedAndStillMissingDoesNotStartAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "still missing workspace");
        int checks = 0, confirmations = 0, installs = 0;
        var hooks = new OpenCodeStartupHooks(
            _ => { Interlocked.Increment(ref checks); return Task.FromResult(false); },
            () => { Interlocked.Increment(ref confirmations); return true; },
            _ => { Interlocked.Increment(ref installs); return Task.FromResult(NotConnected()); });

        MainWindow window = ShowStartupWindow(fixture, "still-missing", workspace, hooks);
        try
        {
            await UntilStartupSettlesAsync(window, () => checks == 2 && confirmations == 1 && installs == 1);
            Require(Tabs(window).Items.Count == 0,
                "A successful installer hook is not treated as proof that OpenCode is runnable.");
            Require(checks == 2 && confirmations == 1 && installs == 1,
                "Consented installation runs once and triggers exactly one readiness recheck.");
            Require(!Directory.Exists(workspace), "A failed readiness recheck does not create the workspace.");
        }
        finally { CloseForTest(window); }
    }

    private static async Task VerifyInstallThenReadyStartsTerminalAsync(string fixture, string trace)
    {
        string workspace = Path.Combine(fixture, "installed by hook workspace");
        int checks = 0, confirmations = 0, installs = 0;
        var hooks = new OpenCodeStartupHooks(
            _ => Task.FromResult(Interlocked.Increment(ref checks) == 2),
            () => { Interlocked.Increment(ref confirmations); return true; },
            _ => { Interlocked.Increment(ref installs); return Task.FromResult(NotConnected()); });

        MainWindow window = ShowStartupWindow(fixture, "installed-by-hook", workspace, hooks);
        try
        {
            await UntilAsync(() => Tabs(window).Items.Count == 1 && File.Exists(trace), "terminal starts after readiness recheck");
            Require(checks == 2 && confirmations == 1 && installs == 1,
                "The install path prompts once, invokes the hook once, and confirms readiness again.");
        }
        finally { CloseForTest(window); }
        File.Delete(trace);
    }

    private static async Task VerifyWorkspaceSetupRunsAfterReadinessAndPersistsAsync(string fixture, string trace)
    {
        string startingWorkspace = Path.Combine(fixture, "wizard initial workspace");
        string selectedWorkspace = Path.Combine(fixture, "wizard selected workspace");
        string settingsPath = Path.Combine(fixture, "settings", "wizard-success.json");
        Require(!Directory.Exists(startingWorkspace) && !Directory.Exists(selectedWorkspace),
            "The first-run workspace test begins without creating either candidate folder.");
        if (File.Exists(trace)) File.Delete(trace);

        var calls = new List<string>();
        int checks = 0;
        var hooks = new OpenCodeStartupHooks(
            _ =>
            {
                calls.Add("check");
                return Task.FromResult(Interlocked.Increment(ref checks) == 2);
            },
            () => { calls.Add("consent"); return true; },
            _ => { calls.Add("install"); return Task.FromResult(NotConnected()); },
            (currentWorkspace, currentAutoMode) =>
            {
                calls.Add("setup");
                Require(currentWorkspace == startingWorkspace && !currentAutoMode,
                    "The workspace wizard receives the current global workspace and mode.");
                Require(!Directory.Exists(selectedWorkspace), "The workspace is not created until setup is accepted.");
                return Task.FromResult<WorkspaceSetupResult?>(new(selectedWorkspace, UseAutoMode: true));
            });

        MainWindow window = ShowStartupWindow(fixture, "wizard-success", startingWorkspace, hooks);
        try
        {
            await UntilAsync(() => Tabs(window).Items.Count == 1 && File.Exists(trace),
                "terminal starts after install readiness and first-run setup");
            Require(calls.SequenceEqual(new[] { "check", "consent", "install", "check", "setup" }),
                "Workspace and mode selection follows consent, installation, and readiness recheck in order.");
            Require(Directory.Exists(selectedWorkspace) && !Directory.Exists(startingWorkspace),
                "Only the accepted workspace is created.");
            var pane = (TerminalPane)GetProperty(((TabItem)Tabs(window).Items[0]).Tag, "Pane");
            Require(pane.WorkingDirectory == Path.GetFullPath(selectedWorkspace) && pane.StartupCommand == "opencode --auto",
                "The accepted global workspace and Auto Mode configure the first OpenCode terminal.");
            using JsonDocument settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            JsonElement root = settings.RootElement;
            Require(root.GetProperty("SetupCompleted").GetBoolean() &&
                    root.GetProperty("CodexWorkspace").GetString() == Path.GetFullPath(selectedWorkspace) &&
                    root.GetProperty("UseAutoMode").GetBoolean() && root.GetProperty("OpenCodeOnly").GetString() == "Y",
                "The selected workspace, Auto Mode, completed-setup marker, and OpenCode-only setting persist.");
        }
        finally { CloseForTest(window); }
        File.Delete(trace);
    }

    private static async Task VerifyWorkspaceSetupCancellationDoesNotCreateFolderAsync(string fixture)
    {
        string initialWorkspace = Path.Combine(fixture, "cancelled setup initial workspace");
        string settingsPath = Path.Combine(fixture, "settings", "wizard-cancelled.json");
        int checks = 0, confirmations = 0, installs = 0, setupCalls = 0;
        var hooks = new OpenCodeStartupHooks(
            _ => { Interlocked.Increment(ref checks); return Task.FromResult(true); },
            () => { Interlocked.Increment(ref confirmations); return false; },
            _ => { Interlocked.Increment(ref installs); return Task.FromResult(NotConnected()); },
            (workspace, autoMode) =>
            {
                Interlocked.Increment(ref setupCalls);
                Require(workspace == initialWorkspace && !autoMode,
                    "Cancelled setup starts from the selected current workspace and standard mode.");
                return Task.FromResult<WorkspaceSetupResult?>(null);
            });

        MainWindow window = ShowStartupWindow(fixture, "wizard-cancelled", initialWorkspace, hooks);
        try
        {
            await UntilStartupSettlesAsync(window, () => checks == 1 && setupCalls == 1);
            Require(Tabs(window).Items.Count == 0 && !Directory.Exists(initialWorkspace),
                "Cancelling first-run setup creates neither a terminal nor a workspace folder.");
            Require(confirmations == 0 && installs == 0 && !Field<bool>(window, "_setupCompleted"),
                "A ready installation skips consent, and cancelled workspace setup is not marked complete.");
        }
        finally { CloseForTest(window); }
    }

    private static async Task VerifyInitialWorkspacePlanIsCreatedBeforeLoadAndPreservesLegacyAsync(
        string fixture, string trace)
    {
        string workspace = Path.Combine(fixture, "fresh initial workspace");
        string legacyPath = Path.Combine(workspace, "terminal-notes", "tabs", "legacy-session", "plan.md");
        string canonicalPath = TerminalTabDocumentStore.GetProjectPlanPath(workspace);
        const string legacyText = "# Legacy plan\n\nKeep this source unchanged.\n";
        const string editedText = "# Fresh workspace plan\n\nSaved through the startup UI.\n";
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllTextAsync(legacyPath, legacyText, new UTF8Encoding(false));
        Require(!File.Exists(canonicalPath), "The fresh initial workspace must not have a canonical plan before startup.");
        if (File.Exists(trace)) File.Delete(trace);

        int checks = 0;
        var hooks = new OpenCodeStartupHooks(
            _ => { Interlocked.Increment(ref checks); return Task.FromResult(true); },
            () => throw new InvalidOperationException("A ready startup must not request install consent."),
            _ => throw new InvalidOperationException("A ready startup must not invoke the installer."));

        MainWindow window = ShowStartupWindow(fixture, "fresh-initial-plan", workspace, hooks);
        try
        {
            try
            {
                await UntilAsync(() =>
                {
                    return Tabs(window).Items.Count == 1 &&
                           TryGetLoadedPlanSnapshot(window, canonicalPath, out _);
                }, "fresh initial workspace plan load");
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(exception.Message + " " + DescribePlanState(window, canonicalPath), exception);
            }

            Require(checks == 1, "The fresh initial workspace used more than one startup readiness check.");
            Require(File.Exists(canonicalPath), "Startup did not create the canonical project plan before the first load.");
            Require(TryGetLoadedPlanSnapshot(window, canonicalPath, out TabDocumentSnapshot initialSnapshot) &&
                    initialSnapshot.Exists && initialSnapshot.Text == legacyText,
                "The first UI load did not observe an existing canonical snapshot copied from the legacy plan.");
            Require(File.ReadAllText(legacyPath) == legacyText,
                "Startup changed the legacy plan while creating the canonical project plan.");

            TextBox editor = Field<TextBox>(window, "_tabDocumentEditor");
            Require(editor.IsEnabled && editor.Text == legacyText,
                "The loaded project plan was not available for editing in the UI.");
            editor.Text = editedText;
            Field<Button>(window, "_tabDocumentSave").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            await UntilAsync(() =>
            {
                if (!File.Exists(canonicalPath) || File.ReadAllText(canonicalPath) != editedText) return false;
                return TryGetLoadedPlanSnapshot(window, canonicalPath, out TabDocumentSnapshot savedSnapshot) &&
                       savedSnapshot.Exists && savedSnapshot.Text == editedText;
            }, "fresh initial workspace plan save");

            Require(File.ReadAllText(legacyPath) == legacyText,
                "Saving the canonical project plan overwrote the legacy source document.");
        }
        finally
        {
            CloseForTest(window);
        }
        if (File.Exists(trace)) File.Delete(trace);
    }

    private static async Task VerifySetupCompletedSkipsWizardOnRestartAsync(string fixture, string trace)
    {
        string selectedWorkspace = Path.Combine(fixture, "wizard selected workspace");
        string settingsPath = Path.Combine(fixture, "settings", "wizard-success.json");
        int setupCalls = 0, confirmations = 0, installs = 0;
        var hooks = new OpenCodeStartupHooks(
            _ => Task.FromResult(true),
            () => { Interlocked.Increment(ref confirmations); return false; },
            _ => { Interlocked.Increment(ref installs); return Task.FromResult(NotConnected()); },
            (_, _) =>
            {
                Interlocked.Increment(ref setupCalls);
                return Task.FromResult<WorkspaceSetupResult?>(null);
            });
        if (File.Exists(trace)) File.Delete(trace);
        var window = new MainWindow(settingsPath, autoStartCodex: true, startupHooks: hooks);
        try
        {
            window.Show();
            await UntilAsync(() => Field<bool>(window, "_openCodeReady") && !Field<bool>(window, "_openCodeStartupBusy"),
                "restarted app completes readiness without reopening completed workspace setup");
            Require(Tabs(window).Items.Count == 0 && !File.Exists(trace),
                "A normal restart leaves terminals closed after readiness succeeds.");
            Require(Field<string>(window, "_workspace") == Path.GetFullPath(selectedWorkspace) && Field<bool>(window, "_useAutoMode"),
                "Restart retains the configured workspace and Auto Mode preference without opening a session.");
            Require(setupCalls == 0 && confirmations == 0 && installs == 0,
                "A completed setup skips the wizard and install hooks on normal restart.");
        }
        finally { CloseForTest(window); }
        File.Delete(trace);
    }

    private static async Task VerifyAutoStartDisabledSkipsWorkspaceWizardAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "wizard skipped without auto start");
        int setupCalls = 0;
        string settingsPath = Path.Combine(fixture, "settings", "wizard-skipped.json");
        var hooks = new OpenCodeStartupHooks(
            _ => Task.FromResult(false),
            () => throw new InvalidOperationException("The non-OpenCode startup path must not request install consent."),
            _ => throw new InvalidOperationException("The non-OpenCode startup path must not invoke the installer."),
            (_, _) =>
            {
                Interlocked.Increment(ref setupCalls);
                return Task.FromResult<WorkspaceSetupResult?>(null);
            });
        var window = new MainWindow(settingsPath, autoStartCodex: false, initialWorkspace: workspace, startupHooks: hooks);
        try
        {
            window.Show();
            await UntilAsync(() => Tabs(window).Items.Count == 1 &&
                    ((TerminalPane)GetProperty(((TabItem)Tabs(window).Items[0]).Tag, "Pane")).IsReady,
                "non-OpenCode terminal starts without workspace setup");
            Require(setupCalls == 0 && !Field<bool>(window, "_setupCompleted"),
                "Disabling automatic OpenCode startup skips the first-run workspace wizard.");
        }
        finally { CloseForTest(window); }
    }

    private static async Task VerifyRepeatedClicksDuringProbeStaySingleFlightAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "single flight workspace");
        int checks = 0, confirmations = 0, installs = 0;
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new OpenCodeStartupHooks(
            async token =>
            {
                Interlocked.Increment(ref checks);
                probeEntered.TrySetResult();
                return await releaseProbe.Task.WaitAsync(token);
            },
            () => { Interlocked.Increment(ref confirmations); return false; },
            _ => { Interlocked.Increment(ref installs); return Task.FromResult(NotConnected()); });

        MainWindow window = ShowStartupWindow(fixture, "single-flight", workspace, hooks);
        try
        {
            await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var newTerminal = (Button)window.FindName("NewTerminalButton");
            newTerminal.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            newTerminal.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            releaseProbe.TrySetResult(false);
            await UntilStartupSettlesAsync(window, () => checks == 1 && confirmations == 1);
            Require(Tabs(window).Items.Count == 0 && installs == 0,
                "Clicks during a pending probe do not create duplicate prompts or terminals.");
            Require(checks == 1 && confirmations == 1,
                "Concurrent startup requests share one probe and one consent decision.");
        }
        finally
        {
            releaseProbe.TrySetResult(false);
            CloseForTest(window);
        }
    }

    private static async Task VerifyDisposalCancelsPendingStartupAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "cancelled workspace");
        int confirmations = 0, installs = 0;
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new OpenCodeStartupHooks(
            async token =>
            {
                probeEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { cancellationObserved.TrySetResult(); throw; }
                return false;
            },
            () => { Interlocked.Increment(ref confirmations); return true; },
            _ => { Interlocked.Increment(ref installs); return Task.FromResult(NotConnected()); });

        MainWindow window = ShowStartupWindow(fixture, "cancelled", workspace, hooks);
        try
        {
            await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            CloseForTest(window);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await DispatcherIdleAsync();
            Require(Tabs(window).Items.Count == 0 && confirmations == 0 && installs == 0,
                "Closing and disposing during the probe cancels startup before consent, installation, or terminal creation.");
        }
        finally { CloseForTest(window); }
    }

    private static MainWindow ShowStartupWindow(string fixture, string name, string workspace, OpenCodeStartupHooks hooks)
    {
        string settings = Path.Combine(fixture, "settings", name + ".json");
        var window = new MainWindow(settings, autoStartCodex: true, initialWorkspace: workspace, startupHooks: hooks);
        window.Show();
        return window;
    }

    private static void CloseForTest(MainWindow window)
    {
        Call(window, "DisposeResources");
        if (window.IsVisible) window.Close();
    }

    private static TabControl Tabs(MainWindow window) => (TabControl)window.FindName("TerminalTabs");

    private static async Task UntilStartupSettlesAsync(MainWindow window, Func<bool> completed)
    {
        await UntilAsync(() => completed() && !Field<bool>(window, "_openCodeStartupBusy"), "startup flow to settle");
    }

    private static async Task UntilAsync(Func<bool> condition, string name, int seconds = 20)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Timed out waiting for " + name + ".");
    }

    private static async Task DispatcherIdleAsync() => await Task.Delay(100);

    private static int RunFakeOpenCode(string[] args)
    {
        string? trace = Environment.GetEnvironmentVariable("TN_STARTUP_TRACE");
        if (!string.IsNullOrWhiteSpace(trace))
            File.AppendAllText(trace, Environment.CurrentDirectory + "\t" + string.Join(' ', args) + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine("FAKE_OPENCODE_STARTUP");
        return 0;
    }

    private static void WriteFakeOpenCode(string directory)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test executable path is unavailable.");
        File.WriteAllText(Path.Combine(directory, "opencode.cmd"),
            "@echo off\r\n\"" + executable + "\" --startup-fake-opencode %*\r\nexit /b %ERRORLEVEL%\r\n", Encoding.ASCII);
    }

    private static AutoInstallResult NotConnected() => new(AutoInstallStatus.NotConnected, "test installer result");

    private static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().FullName, name));

    private static object GetProperty(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)
        ?? throw new MissingMemberException(target.GetType().FullName, name);

    private static object? Call(object target, string name, params object?[] args)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        return method.Invoke(target, args);
    }

    private static bool TryGetLoadedPlanSnapshot(MainWindow window, string path, out TabDocumentSnapshot snapshot)
    {
        snapshot = default!;
        var states = (IDictionary)(window.GetType().GetField("_tabDocumentStates",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new MissingFieldException(window.GetType().FullName, "_tabDocumentStates"));
        object? state = states[path];
        if (state is null) return false;
        object? value = state.GetType().GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(state);
        if (value is not TabDocumentSnapshot candidate || !candidate.Exists) return false;
        snapshot = candidate;
        return true;
    }

    private static string DescribePlanState(MainWindow window, string path)
    {
        var states = (IDictionary)(window.GetType().GetField("_tabDocumentStates",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new MissingFieldException(window.GetType().FullName, "_tabDocumentStates"));
        string keys = string.Join(" | ", states.Keys.Cast<object>().Select(value => value?.ToString()));
        string? active = Field<string?>(window, "_activeTabDocumentPath");
        TextBox editor = Field<TextBox>(window, "_tabDocumentEditor");
        return $"tabs={Tabs(window).Items.Count}; canonicalExists={File.Exists(path)}; active={active}; " +
               $"stateCount={states.Count}; keys={keys}; editorEnabled={editor.IsEnabled}; editorLength={editor.Text.Length}";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
