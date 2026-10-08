using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using TerminalNotes.TerminalEngine;
using TerminalNotes.TerminalTransport;

namespace TerminalNotes.TerminalDesktop;

internal sealed class TerminalWindow : Window
{
    private readonly LaunchSettings _settings;
    private readonly TerminalEmulator _engine = new(100, 30);
    private readonly object _engineGate = new();
    private readonly ITerminalProcess _process = TerminalProcessFactory.Create();
    private readonly TerminalSurface _surface;
    private readonly TextBlock _status = new() { Text = "시작 중…", Margin = new Thickness(8, 3), FontSize = 12 };
    private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _inputPump;
    private bool _started, _closing, _closed;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MemoryStream _smokeOutput = new();

    public TerminalWindow(LaunchSettings settings)
    {
        _settings = settings;
        Title = "TerminalNotes Terminal";
        Width = 1000; Height = 650; MinWidth = 320; MinHeight = 200;
        _surface = new TerminalSurface(_engine, _engineGate);
        var add = new Button { Content = "+ 새 터미널", Margin = new Thickness(4), Padding = new Thickness(10, 4) };
        add.Click += (_, _) => OpenNewTerminal();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Children = { add,
            new TextBlock { Text = "Ctrl+Shift+T 새 창 · Ctrl+Shift+C/V 복사/붙여넣기 · Shift+드래그 선택",
                VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(8, 0) } } };
        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(_status, Dock.Bottom);
        layout.Children.Add(top); layout.Children.Add(_status); layout.Children.Add(_surface); Content = layout;
        _surface.Input += QueueInput;
        _surface.GridResized += ResizeProcess;
        _surface.TitleChanged += title => Title = string.IsNullOrWhiteSpace(title) ? "TerminalNotes Terminal" : title + " — TerminalNotes";
        _surface.Error += SetStatus;
        _engine.Response += response => QueueInput(Encoding.UTF8.GetBytes(response));
        _process.OutputReceived += bytes =>
        {
            if (_settings.Smoke) lock (_smokeOutput) _smokeOutput.Write(bytes);
            lock (_engineGate) _engine.Feed(bytes.AsSpan());
        };
        _process.Exited += result => Dispatcher.UIThread.Post(() => SetStatus(result.Error is null
            ? $"프로세스 종료 ({result.ExitCode?.ToString() ?? "?"})" : "연결 오류: " + result.Error));
        _process.InputFailed += error => Dispatcher.UIThread.Post(() => SetStatus("입력 전송 실패: " + error.Message));
        _inputPump = PumpInputAsync();
        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OpenNewTerminal() => new TerminalWindow(_settings with { Smoke = false }).Show();
    private void ResizeProcess(int columns, int rows)
    {
        if (!_started || _closing || !_process.IsRunning) return;
        try { _process.Resize(columns, rows); }
        catch (Exception error) { SetStatus("크기 변경 실패: " + error.Message); }
    }
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control | KeyModifiers.Shift))
        { e.Handled = true; OpenNewTerminal(); }
    }

    private async Task StartAsync()
    {
        try
        {
            var snapshot = Snapshot();
            await _process.StartAsync(new TerminalProcessOptions
            {
                FileName = _settings.Executable, Arguments = _settings.Arguments,
                WorkingDirectory = _settings.Directory, Columns = snapshot.Columns, Rows = snapshot.Rows,
                Environment = new Dictionary<string, string?> { ["TERM"] = "xterm-256color", ["COLORTERM"] = "truecolor" }
            }, _lifetime.Token);
            if (_closing) return;
            _started = true; _ready.TrySetResult();
            snapshot = Snapshot();
            ResizeProcess(snapshot.Columns, snapshot.Rows);
            SetStatus(_settings.Directory);
            _surface.Focus();
            if (_settings.Smoke) _ = RunSmokeAsync();
        }
        catch (Exception error)
        {
            _ready.TrySetCanceled();
            SetStatus("시작 실패: " + error.Message);
            if (_settings.Smoke) await FinishSmokeAsync(error);
        }
    }

    private void QueueInput(byte[] bytes)
    {
        if (_closing || bytes.Length == 0) return;
        if (!_input.Writer.TryWrite(bytes))
            Dispatcher.UIThread.Post(() => SetStatus("입력 대기열이 가득 찼습니다. 방금 입력은 전송되지 않았습니다."));
    }

    private async Task PumpInputAsync()
    {
        try
        {
            await _ready.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            await foreach (byte[] bytes in _input.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                await _process.WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Dispatcher.UIThread.Post(() => SetStatus("입력 전송 실패: " + error.Message)); }
    }

    private void SetStatus(string text) => _status.Text = text;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        await CloseSessionAsync();
        Close();
    }

    private async Task CloseSessionAsync()
    {
        if (_closing) return;
        _closing = true; _surface.Stop(); _input.Writer.TryComplete(); _lifetime.Cancel(); _ready.TrySetCanceled();
        try { await _process.CloseAsync(); await _process.DisposeAsync(); await _inputPump; }
        catch (Exception error) { SetStatus("종료 오류: " + error.Message); }
        finally { _closed = true; _lifetime.Dispose(); }
    }

    // Explicit, isolated acceptance mode. It writes only to this window's own shell.
    private async Task RunSmokeAsync()
    {
        try
        {
            await Task.Delay(1000);
            string command = OperatingSystem.IsWindows()
                ? "[Console]::WriteLine('NATIVE_' + 'READY 한글✓')\r"
                : "printf '\\033[32m%s\\033[0m\\n' 'NATIVE_'\"READY 한글✓\"\r";
            _surface.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = command.TrimEnd('\r') });
            _surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await UntilAsync(() => ScreenText().Contains("NATIVE_READY 한글✓", StringComparison.Ordinal), "Unicode output");
            for (int i = 0; i < 20; i++) { Width = 700 + i * 11; Height = 400 + i * 7; await Task.Delay(30); }
            if (!_process.IsRunning) throw new InvalidOperationException("Child exited during resize");
            QueueInput(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows()
                ? "[Console]::WriteLine('AFTER_' + 'RESIZE')\r" : "printf '%s\\n' 'AFTER_'RESIZE\r"));
            await UntilAsync(() => ScreenText().Contains("AFTER_RESIZE", StringComparison.Ordinal), "Output after resize");
            var state = Snapshot();
            if (state.Columns < 10 || state.Rows < 5) throw new InvalidOperationException("Invalid viewport");
            var second = new TerminalWindow(_settings with { Smoke = false });
            try
            {
                second.Show();
                await second._ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (!second._process.IsRunning || ReferenceEquals(second._engine, _engine)) throw new InvalidOperationException("New terminal is not independent");
                await second.CloseSessionAsync();
                if (!_process.IsRunning) throw new InvalidOperationException("Closing another terminal terminated the original");
            }
            finally { await second.CloseSessionAsync(); second.Close(); }
            string? screenshot = Environment.GetEnvironmentVariable("TERMINALNOTES_DESKTOP_SMOKE_SCREENSHOT");
            if (!string.IsNullOrEmpty(screenshot))
            {
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
                bitmap.Render(this); bitmap.Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            await FinishSmokeAsync(null);
        }
        catch (Exception error) { await FinishSmokeAsync(error); }
    }

    private TerminalSnapshot Snapshot() { lock (_engineGate) return _engine.GetSnapshot(); }
    private string ScreenText() => string.Join("\n", Snapshot().Lines.Select(line => string.Concat(line.Cells.Select(cell => cell.Text))));
    private static async Task UntilAsync(Func<bool> condition, string name)
    {
        var end = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < end) { if (condition()) return; await Task.Delay(100); }
        throw new TimeoutException(name);
    }
    private async Task FinishSmokeAsync(Exception? error)
    {
        string? log = Environment.GetEnvironmentVariable("TERMINALNOTES_DESKTOP_SMOKE_LOG");
        if (!string.IsNullOrEmpty(log))
        {
            string raw;
            lock (_smokeOutput) raw = Encoding.UTF8.GetString(_smokeOutput.ToArray());
            await File.WriteAllTextAsync(log, error is null
                ? "PASS portable desktop: real PTY, TextInput/Enter, Unicode, repeated resize, independent windows and close ownership\n"
                : "FAIL " + error + "\nSTATUS: " + _status.Text + "\nSCREEN:\n" + ScreenText() + "\nRAW:\n" + raw);
        }
        await CloseSessionAsync();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(error is null ? 0 : 1);
    }
}
