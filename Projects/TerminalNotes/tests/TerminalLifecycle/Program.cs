using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;
using TerminalNotes.TerminalEngine;

internal static class Program
{
    private static int _exitCode;
    private static bool _runCompleted;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, args) =>
        {
            _exitCode = 1;
            Console.Error.WriteLine("FAIL WPF dispatcher: " + args.Exception);
            args.Handled = true;
            app.Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _exitCode = 1;
            Console.Error.WriteLine("FAIL unhandled exception: " + args.ExceptionObject);
        };
        app.Startup += async (_, _) =>
        {
            try
            {
                await VerifyAsync().WaitAsync(TimeSpan.FromMinutes(2));
                Console.WriteLine("PASS: selected native terminal stop and restart preserve pane and task state.");
            }
            catch (Exception exception)
            {
                _exitCode = 1;
                Console.Error.WriteLine("FAIL terminal lifecycle regression: " + exception);
            }
            finally
            {
                _runCompleted = true;
                app.Shutdown(_exitCode);
            }
        };

        app.Run();
        if (!_runCompleted)
        {
            Console.Error.WriteLine("FAIL WPF dispatcher exited before the lifecycle run completed.");
            return 1;
        }
        return _exitCode;
    }

    private static async Task VerifyAsync()
    {
        string? oldRenderer = Environment.GetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER");
        object? oldDataDirectory = AppContext.GetData("TerminalNotes.DataDirectory");
        string oldCurrentDirectory = Environment.CurrentDirectory;
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes lifecycle " + Guid.NewGuid().ToString("N"));
        string dataDirectory = Path.Combine(fixture, "app-data");
        string workspace = Path.Combine(fixture, "workspace");
        string project = Path.Combine(workspace, "Lifecycle project");
        string settingsPath = Path.Combine(fixture, "settings.json");
        MainWindow? window = null;
        var panes = new List<TerminalPane>();
        try
        {
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(dataDirectory);
            AppContext.SetData("TerminalNotes.DataDirectory", dataDirectory);
            Environment.CurrentDirectory = fixture;
            Environment.SetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER", null);
            await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new
            {
                Workspace = workspace,
                CodexWorkspace = workspace,
                SetupCompleted = true,
                WorkspaceDefaultsVersion = 1
            }));

            window = new MainWindow(settingsPath, autoStartCodex: false);
            // Suppress normal Loaded startup; the fixture creates only explicit shell panes below.
            Field(window, "_windowInitialized", true);
            Field(window, "_workspace", workspace);
            window.Show();

            var tabs = (TabControl)window.FindName("TerminalTabs");
            var layout = Field<TerminalSlotLayout>(window, "_terminalSlotLayout");
            layout.SetCapacity(2);
            layout.Assign(project, 0);

            Type launchType = typeof(MainWindow).GetNestedType("ResolvedTerminalLaunch", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ResolvedTerminalLaunch was not found.");
            object Launch(string title, string marker)
            {
                var constructor = launchType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Single(candidate => candidate.GetParameters().Length == 5);
                return constructor.Invoke(new object?[]
                {
                    false, $"Write-Output '{marker}'", title, project, TerminalTool.PowerShell
                });
            }

            TabItem CreateTab(int slot, string title, string marker)
            {
                var tab = (TabItem)(Call(window, "CreateTerminalTabWithoutPlanDocument", slot, Launch(title, marker), title)
                    ?? throw new InvalidOperationException("Could not create an explicit shell pane."));
                panes.Add(Pane(tab));
                return tab;
            }

            var ownerTab = CreateTab(1, "Managed owner", "LIFECYCLE_OWNER_READY");
            Call(window, "AttachSingleProjectTerminal", project, ownerTab);
            tabs.Items.Add(ownerTab);

            var selectedTab = CreateTab(2, "Manual shell", "LIFECYCLE_MANUAL_READY");
            tabs.Items.Add(selectedTab);
            tabs.SelectedItem = selectedTab;
            Field(window, "_selectedProjectPath", (string?)project);
            Call(window, "SetTaskWorkspaceVisible", false);
            Call(window, "ChangeTerminalLayout", 2, false);

            var ownerPane = Pane(ownerTab);
            var selectedPane = Pane(selectedTab);
            Require(Field<bool>(ownerPane, "_useNativeTerminalSurface") &&
                Field<bool>(selectedPane, "_useNativeTerminalSurface"),
                "The fixture did not select the native terminal renderer.");
            await Until(() => ownerPane.IsReady && selectedPane.IsReady, "both native PowerShell panes to become ready");
            Console.WriteLine("PASS both native shell panes are ready");
            uint ownerPid = ProcessId(ownerPane);
            uint originalSelectedPid = ProcessId(selectedPane);
            Require(ownerPid != originalSelectedPid, "The explicit panes unexpectedly share a process.");
            Require(ProcessExists(ownerPid) && ProcessExists(originalSelectedPid), "A fixture shell process exited before lifecycle operations.");

            object ownerRuntime = Call(window, "RuntimeFor", project)
                ?? throw new InvalidOperationException("Could not get the project task runtime.");
            Require(ReferenceEquals(Member<TabItem?>(ownerRuntime, "SingleTerminal"), ownerTab),
                "The control pane is not registered as the managed task owner.");
            Require(!Member<bool>(ownerRuntime, "SingleStopped"), "The managed owner began in stopped state.");

            var selectedState = selectedTab.Tag;
            string planPath = Property<string>(selectedState, "PlanDocumentPath");
            Require(!File.Exists(planPath), "A shell tab without a plan document created plan.md.");
            Property(selectedState, "InputMode", TerminalInputMode.Build);
            Property(selectedState, "InputDraft", "unsent lifecycle draft");
            var originalSlots = layout.Slots.ToArray();

            var processControl = (Button)window.FindName("StopTerminalProcessButton");
            Call(window, "UpdateTerminalLifecycleButtons");
            Require(processControl.IsEnabled && processControl.Content as string == "■",
                "The running terminal does not offer Stop on the shared process control.");
            Task stop = (Task)(Call(window, "ToggleSelectedTerminalProcessAsync")
                ?? throw new InvalidOperationException("The shared process toggle did not return a Task."));
            Require(Field<HashSet<TabItem>>(window, "_terminalLifecycleOperations").Count == 1 &&
                Field<HashSet<string>>(window, "_terminalLifecycleProjects").Count == 1,
                "The first lifecycle operation did not reserve its pane and project.");
            Task duplicateStop = (Task)(Call(window, "StopSelectedTerminalProcessAsync")
                ?? throw new InvalidOperationException("Duplicate stop did not return a Task."));
            await Task.WhenAll(stop, duplicateStop);
            await Until(() => !selectedPane.IsRunning && !ProcessExists(originalSelectedPid), "selected shell process to exit");
            Require(ReferenceEquals(tabs.SelectedItem, selectedTab), "Stopping replaced or deselected the selected tab.");
            Require(ownerPane.IsRunning && ProcessId(ownerPane) == ownerPid && ProcessExists(ownerPid),
                "Stopping the manual pane affected the managed owner pane.");
            Require(!Member<bool>(ownerRuntime, "SingleStopped"), "Stopping an unowned pane set the managed task stop latch.");
            Require(layout.Slots.SequenceEqual(originalSlots), "Stopping changed the terminal slot assignment.");
            Require(!File.Exists(planPath), "Stopping created the missing plan document.");
            Call(window, "UpdateTerminalLifecycleButtons");
            Require(processControl.IsEnabled && processControl.Content as string == "▶",
                "The stopped terminal does not offer Start on the same process control.");

            Task restart = (Task)(Call(window, "ToggleSelectedTerminalProcessAsync")
                ?? throw new InvalidOperationException("The shared Start action did not return a Task."));
            await restart;
            var replacement = tabs.SelectedItem as TabItem
                ?? throw new InvalidOperationException("Restart did not select the replacement tab.");
            Require(!ReferenceEquals(replacement, selectedTab) && Property<string>(replacement.Tag, "Title") == "Manual shell",
                "Restart did not replace the selected manual shell tab.");
            var replacementState = replacement.Tag;
            var replacementPane = Pane(replacement);
            panes.Add(replacementPane);
            await Until(() => replacementPane.IsReady, "replacement native PowerShell pane to become ready");
            uint replacementPid = ProcessId(replacementPane);
            Call(window, "UpdateTerminalLifecycleButtons");
            Require(processControl.IsEnabled && processControl.Content as string == "■",
                "The started terminal did not restore Stop on the same process control.");
            Require(replacementPid != originalSelectedPid && ProcessExists(replacementPid),
                "Restart did not produce a new live process.");
            await Until(() => !ProcessExists(originalSelectedPid), "old selected process to remain exited after restart");
            Require(ReferenceEquals(tabs.SelectedItem, replacement), "Restart did not select the replacement tab.");
            Require(ReferenceEquals(Pane(ownerTab), ownerPane) && ownerPane.IsRunning && ProcessId(ownerPane) == ownerPid,
                "Restart changed or stopped the managed owner pane.");
            Require(!Member<bool>(ownerRuntime, "SingleStopped"), "Restart changed the unrelated managed task stop latch.");
            Require(layout.Slots.SequenceEqual(originalSlots) && layout.IndexOf(project) == 0,
                "Restart changed the project terminal slot.");
            Require(string.Equals(Field<string?>(window, "_selectedProjectPath"), project, StringComparison.OrdinalIgnoreCase),
                "Restart changed the selected project.");
            Require(Property<string>(replacementState, "PlanDocumentPath") == planPath && !File.Exists(planPath),
                "Restart changed or created the missing plan document.");
            Require(Property<TerminalInputMode>(replacementState, "InputMode") == TerminalInputMode.Build &&
                Property<string>(replacementState, "InputDraft") == "unsent lifecycle draft",
                "Restart did not preserve the manual pane input mode and draft.");
            Require(!Member<bool>(ownerRuntime, "SingleStopped"), "The managed owner stop latch changed during the lifecycle test.");
            Console.WriteLine("PASS stopping and restarting a manual pane preserves the same-project task owner");

            tabs.SelectedItem = ownerTab;
            using var pendingStartup = new CancellationTokenSource();
            void SetRuntime(string name, object? value) => ownerRuntime.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(ownerRuntime, value);
            SetRuntime("StartingSingle", true);
            SetRuntime("SingleStartupCancellation", pendingStartup);
            Field<HashSet<string>>(window, "_startingProjectTasks").Add(project);
            async Task FinishCanceledStartup()
            {
                await Until(() => pendingStartup.IsCancellationRequested, "managed startup cancellation");
                await Task.Delay(250);
                SetRuntime("StartingSingle", false);
                SetRuntime("SingleStartupCancellation", null);
                Field<HashSet<string>>(window, "_startingProjectTasks").Remove(project);
            }
            Task startupUnwind = FinishCanceledStartup();
            await (Task)Call(window, "RestartSelectedTerminalProcessAsync")!;
            await startupUnwind;
            var restartedOwner = tabs.SelectedItem as TabItem
                ?? throw new InvalidOperationException("Managed restart did not select a replacement.");
            var restartedOwnerPane = Pane(restartedOwner);
            panes.Add(restartedOwnerPane);
            await Until(() => restartedOwnerPane.IsReady, "restarted managed shell pane to become ready");
            Require(!ReferenceEquals(restartedOwner, ownerTab) && !ProcessExists(ownerPid) &&
                ReferenceEquals(Member<TabItem?>(ownerRuntime, "SingleTerminal"), restartedOwner),
                "Managed restart did not replace and rebind the stopped owner.");
            Require(Member<bool>(ownerRuntime, "SingleStopped"),
                "Managed restart unexpectedly resumed the stopped task.");
            Require(replacementPane.IsRunning && ProcessId(replacementPane) == replacementPid &&
                layout.Slots.SequenceEqual(originalSlots) && !File.Exists(planPath),
                "Managed restart changed the other pane, layout, or missing plan.");
            Console.WriteLine("PASS managed restart waits for startup cancellation, rebinds its pane, and keeps automation stopped");
        }
        finally
        {
            foreach (var pane in panes.Distinct())
            {
                try { pane.Dispose(); } catch { }
            }
            if (window is not null)
            {
                try { window.Content = null; window.Close(); } catch { }
            }
            try { Directory.Delete(fixture, recursive: true); } catch { }
            Environment.CurrentDirectory = oldCurrentDirectory;
            Environment.SetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER", oldRenderer);
            AppContext.SetData("TerminalNotes.DataDirectory", oldDataDirectory);
        }
    }

    private static T Field<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        return (T)field.GetValue(instance)!;
    }

    private static T Field<T>(object instance, string name, T value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        field.SetValue(instance, value);
        return value;
    }

    private static T Property<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(instance.GetType().FullName, name);
        return (T)property.GetValue(instance)!;
    }

    private static T Property<T>(object instance, string name, T value)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(instance.GetType().FullName, name);
        property.SetValue(instance, value);
        return value;
    }

    private static T Member<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null) return (T)field.GetValue(instance)!;
        return Property<T>(instance, name);
    }

    private static object? Call(object instance, string name, params object?[] arguments)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, name);
        return method.Invoke(instance, arguments);
    }

    private static TerminalPane Pane(TabItem tab) => Property<TerminalPane>(tab.Tag, "Pane");
    private static uint ProcessId(TerminalPane pane) => Field<ConPtySession>(pane, "_session").ProcessId
        ?? throw new InvalidOperationException("The native pane has no owned process ID.");

    private static bool ProcessExists(uint processId)
    {
        try { using var process = Process.GetProcessById(checked((int)processId)); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task Until(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
