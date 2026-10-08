using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
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
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync();
                Console.WriteLine("PASS: all automation roles launch native OpenCode panes with the selected permission mode.");
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
        string originalDirectory = Environment.CurrentDirectory;
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Native Automation " + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(fixture, "workspace");
        string project = Path.Combine(workspace, "empty-project");
        Directory.CreateDirectory(project);
        Environment.CurrentDirectory = fixture;

        MainWindow? window = null;
        try
        {
            window = new MainWindow(Path.Combine(fixture, "settings.json"), autoStartCodex: false, initialWorkspace: workspace);
            VerifyRoleLaunchDescriptors(window, project);

            window.Show();
            var tabs = (System.Windows.Controls.TabControl)(window.FindName("TerminalTabs")
                ?? throw new InvalidOperationException("The terminal tab control was not created."));
            await UntilAsync(() => tabs.Items.Count == 1, "initial terminal tab", 15);
            TerminalPane initialPane = Pane((TabItem)tabs.Items[0]);
            await UntilAsync(() => initialPane.IsReady && initialPane.IsRunning, "isolated starter shell", 45);

            var team = (Dictionary<AutomationRole, TabItem>)Invoke(window, "EnsureAutomationTeam", project)!;
            Require(team.Count == Enum.GetValues<AutomationRole>().Length, "The automation team did not create exactly one pane per role.");
            Require(team.Values.All(tabs.Items.Contains), "A role pane was not added to the visible terminal collection.");

            var rolePanes = Enum.GetValues<AutomationRole>()
                .Select(role => (Role: role, Tab: team[role], Pane: Pane(team[role])))
                .ToArray();
            await UntilAsync(
                () => rolePanes.All(item => CanStartNativeAutomation(item.Pane) && NativeStatusKind(item.Pane) == "Idle"),
                "all native OpenCode role panes to reach idle", 60);
            await UntilAsync(
                async () => (await Task.WhenAll(rolePanes.Select(item => ScreenTextAsync(item.Pane))))
                    .All(screen => screen.Contains("Ask anything", StringComparison.OrdinalIgnoreCase)),
                "all three visible OpenCode TUIs to render their composer", 60);

            var endpoints = new HashSet<int>();
            foreach (var item in rolePanes)
            {
                object state = item.Tab.Tag ?? throw new InvalidOperationException($"{item.Role} has no terminal state.");
                var tool = (TerminalTool)(GetProperty(state, "Tool")
                    ?? throw new InvalidOperationException($"{item.Role} has no launch tool."));
                var endpoint = (Uri)(GetProperty(item.Pane, "NativeOpenCodeEndpoint")
                    ?? throw new InvalidOperationException($"{item.Role} has no native OpenCode endpoint."));

                Require(tool == TerminalTool.OpenCode, $"{item.Role} was created as {tool} instead of OpenCode.");
                Require(item.Pane.StartupCommand == "opencode", $"{item.Role} did not use the Standard OpenCode launch command.");
                Require(item.Pane.IsReady && item.Pane.IsRunning, $"{item.Role} OpenCode process is not running in its ready pane.");
                Require(endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.Port > 0,
                    $"{item.Role} did not receive a dedicated loopback HTTP endpoint.");
                Require(endpoints.Add(endpoint.Port), "Two automation roles shared one native OpenCode endpoint.");
                Require(CanStartNativeAutomation(item.Pane), $"{item.Role} was not ready for native automation at idle.");
                Require(NativeStatusKind(item.Pane) == "Idle", $"{item.Role} native OpenCode status was not idle.");
                Console.WriteLine($"PASS: {item.Role} is an idle native OpenCode pane at {endpoint}.");
            }

            string screenshotPath = Path.Combine(FindRepositoryRoot(), "artifacts", "AutomationNativeLaunch.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            await Task.Delay(600);
            CaptureVisual(window, screenshotPath);
            Console.WriteLine("Screenshot: " + screenshotPath);
        }
        finally
        {
            if (window is not null)
            {
                Invoke(window, "DisposeResources");
                window.Close();
            }

            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        }
    }

    private static void VerifyRoleLaunchDescriptors(MainWindow window, string project)
    {
        foreach (bool autoMode in new[] { false, true })
        {
            SetField(window, "_useAutoMode", autoMode);
            TerminalPermissionMode mode = autoMode ? TerminalPermissionMode.Auto : TerminalPermissionMode.Standard;
            string expectedCommand = TerminalLaunchModes.Command(TerminalTool.OpenCode, mode)
                ?? throw new InvalidOperationException($"No OpenCode command was defined for {mode}.");

            foreach (AutomationRole role in Enum.GetValues<AutomationRole>())
            {
                object descriptor = Invoke(window, "ResolveAutomationLaunch", project, RoleTitle(role))
                    ?? throw new InvalidOperationException($"{role} returned no launch descriptor.");
                Require((bool)(GetProperty(descriptor, "LaunchCodex") ?? true) == false,
                    $"{role} unexpectedly selected the legacy Codex launch path.");
                Require((TerminalTool)(GetProperty(descriptor, "Tool")
                    ?? throw new InvalidOperationException($"{role} has no launch tool.")) == TerminalTool.OpenCode,
                    $"{role} launch descriptor is not OpenCode.");
                Require((string?)GetProperty(descriptor, "Command") == expectedCommand,
                    $"{role} did not map {mode} to '{expectedCommand}'.");
                Require(string.Equals((string?)GetProperty(descriptor, "WorkingDirectory"), project, StringComparison.OrdinalIgnoreCase),
                    $"{role} launch descriptor does not target the isolated project.");
            }
        }

        SetField(window, "_useAutoMode", false);
    }

    private static string RoleTitle(AutomationRole role) => role switch
    {
        AutomationRole.Coordinator => "지휘",
        AutomationRole.Implementer => "구현",
        AutomationRole.Reviewer => "검토",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static TerminalPane Pane(TabItem tab) => (TerminalPane)(GetProperty(tab.Tag!, "Pane")
        ?? throw new InvalidOperationException("The tab has no terminal pane."));

    private static bool CanStartNativeAutomation(TerminalPane pane) =>
        (bool)(GetProperty(pane, "CanStartNativeAutomation") ?? false);

    private static string? NativeStatusKind(TerminalPane pane)
    {
        object? monitor = GetField(pane, "_openCodeStatus");
        object? snapshot = monitor is null ? null : GetProperty(monitor, "Snapshot");
        return snapshot is null ? null : GetProperty(snapshot, "Kind")?.ToString();
    }

    private static async Task<string> ScreenTextAsync(TerminalPane pane)
    {
        var browser = (WebView2)(GetField(pane, "_browser")
            ?? throw new InvalidOperationException("The terminal browser was not created."));
        if (browser.CoreWebView2 is null) return string.Empty;
        string json = await browser.ExecuteScriptAsync(
            "Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')");
        return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string description, int timeoutSeconds)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < end)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    private static async Task UntilAsync(Func<bool> condition, string description, int timeoutSeconds)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TerminalNotes.csproj"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the TerminalNotes repository root for the screenshot.");
    }

    private static void CaptureVisual(Window window, string path)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out NativeRect rectangle))
            throw new InvalidOperationException("Could not capture the native OpenCode window.");
        int width = rectangle.Right - rectangle.Left;
        int height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("The native OpenCode window has no visible area.");
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rectangle.Left, rectangle.Top, 0, 0, new System.Drawing.Size(width, height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static object? Invoke(object target, string name, params object?[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static object? GetProperty(object target, string name) =>
        (target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().FullName, name)).GetValue(target);

    private static object? GetField(object target, string name) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name)).GetValue(target);

    private static void SetField(object target, string name, object value) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name)).SetValue(target, value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
