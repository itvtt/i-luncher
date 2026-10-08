using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;
using System.Runtime.InteropServices;
using TerminalNotes.Services;

namespace TerminalNotes;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private AppInstanceCoordinator? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string settingsPath = TerminalNotes.MainWindow.DefaultSettingsPath;
        string? workspace = null;
        int workspaceIndex = Array.IndexOf(e.Args, "--workspace");
        if (workspaceIndex >= 0 && workspaceIndex + 1 < e.Args.Length)
            try { workspace = Path.GetFullPath(e.Args[workspaceIndex + 1]); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        try
        {
            // Retry acquisition too: the previous process might be exiting.
            var waiting = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                _instance = AppInstanceCoordinator.TryAcquire(settingsPath);
                if (_instance is not null) break;
                if (AppInstanceCoordinator.ReadOwnerProcessId(settingsPath) is int processId)
                    AllowSetForegroundWindow(processId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (await AppInstanceCoordinator.TryActivateAsync(settingsPath, workspace, timeout.Token))
                {
                    Shutdown();
                    return;
                }
                await Task.Delay(100);
            } while (waiting.Elapsed < TimeSpan.FromSeconds(10));
            if (_instance is null)
            {
                MessageBox.Show("이미 실행 중인 Terminal Notes 창이 응답을 기다리고 있습니다. 트레이의 기존 창을 확인하세요. 기존 작업은 그대로 유지됩니다.", "Terminal Notes");
                Shutdown();
                return;
            }
            try { GlobalAgentInstructions.EnsureFile(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A busy instruction store can be retried when the editor or a terminal opens.
            }
            var window = new TerminalNotes.MainWindow();
            MainWindow = window;
            _instance.StartListening(async requestedProject => await Dispatcher.InvokeAsync(() => window.ActivateFromLauncher(requestedProject)));
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show("Terminal Notes 시작 실패 · " + exception.Message, "Terminal Notes");
            Shutdown(1);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is TerminalNotes.MainWindow window && !window.PrepareForSystemShutdown()) e.Cancel = true;
        base.OnSessionEnding(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as TerminalNotes.MainWindow)?.DisposeResources();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

