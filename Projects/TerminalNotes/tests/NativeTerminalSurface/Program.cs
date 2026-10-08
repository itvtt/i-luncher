using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerminalNotes;
using TerminalNotes.Controls;
using TerminalNotes.Services;
using TerminalNotes.TerminalEngine;

internal static class Program
{
    private static int _exitCode;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await VerifyAsync(); Console.WriteLine("PASS: native WPF terminal surface with real ConPTY."); }
            catch (Exception exception) { _exitCode = 1; Console.Error.WriteLine("FAIL: " + exception); }
            finally { app.Shutdown(_exitCode); }
        };
        app.Run();
        return _exitCode;
    }

    private static async Task VerifyAsync()
    {
        VerifyStateBackgroundRendering();
        await VerifyAdaptiveViewportAsync();
        string? oldRenderer = Environment.GetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER");
        Environment.SetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER", null);
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes native surface " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string onceFile = Path.Combine(root, "send-once.txt");
        string escapedPath = onceFile.Replace("'", "''");
        var first = new TerminalPane(root, startupCommand: "Write-Output 'NATIVE_SURFACE_START'; [Console]::WriteLine('한글✓ 日本語😀');");
        var second = new TerminalPane(root, startupCommand: "Write-Output 'SECOND_SURFACE_START';");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(second, 1);
        grid.Children.Add(first);
        grid.Children.Add(second);
        var window = new Window { Width = 1050, Height = 820, Content = grid, ShowInTaskbar = false };
        try
        {
            window.Show();
            await WaitReady(first);
            await WaitReady(second);
            var firstSession = Field<ConPtySession>(first, "_session");
            var secondSession = Field<ConPtySession>(second, "_session");
            var firstSurface = Field<NativeTerminalSurface>(first, "_nativeTerminalSurface");
            var firstEmulator = Field<TerminalEmulator>(first, "_nativeTerminalEmulator");
            var capturedOutput = new System.Text.StringBuilder();
            firstSession.OutputReceived += text => { lock (capturedOutput) capturedOutput.Append(text); };
            Require(Field<bool>(first, "_useNativeTerminalSurface"), "The generic shell did not select the native WPF surface by default.");
            Require(!firstSession.ProcessId.Equals(secondSession.ProcessId), "Two panes share a ConPTY process.");
            try { await Until(() => Buffer(firstSurface).Contains("NATIVE_SURFACE_START", StringComparison.Ordinal) && Buffer(firstSurface).Contains("한글✓ 日本語😀", StringComparison.Ordinal), "Unicode startup output"); }
            catch
            {
                lock (capturedOutput) Console.WriteLine("Captured PTY output: " + capturedOutput.ToString());
                Console.WriteLine("Rendered buffer: " + Buffer(firstSurface));
                Console.WriteLine($"State: ready={first.IsReady}, running={first.IsRunning}, process={firstSession.ProcessId}");
                throw;
            }
            Console.WriteLine("PASS: generic shell output is parsed and rendered by the native terminal engine.");

            var previous = firstEmulator.GetSnapshot();
            foreach (var size in new[] { (760d, 620d), (1280d, 900d), (890d, 720d), (1120d, 860d) })
            {
                window.Width = size.Item1;
                window.Height = size.Item2;
                await Task.Delay(180);
            }
            await Until(() => firstEmulator.GetSnapshot().Columns != previous.Columns || firstEmulator.GetSnapshot().Rows != previous.Rows, "native resize");
            Require(first.IsReady && first.IsRunning && second.IsReady && second.IsRunning, "A pane stopped during resize.");
            Console.WriteLine("PASS: WPF layout changes update the native viewport while both shell sessions remain live.");

            first.SelectAll();
            first.CopySelection();
            Require(Clipboard.ContainsText() && Clipboard.GetText().Contains("NATIVE_SURFACE_START", StringComparison.Ordinal), "Select All/Copy did not preserve the rendered transcript.");
            first.ClearSelection();

            firstSurface.FocusSurface();
            firstSurface.PasteText($"Add-Content -LiteralPath '{escapedPath}' -Value 'sent-once'");
            var source = PresentationSource.FromVisual(firstSurface) ?? throw new InvalidOperationException("The native surface has no presentation source.");
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            firstSurface.RaiseEvent(enter);
            await Until(() => File.Exists(onceFile) && File.ReadAllLines(onceFile).Length == 1, "exactly-once native terminal send");
            await Task.Delay(500);
            Require(File.ReadAllLines(onceFile).Length == 1, "A single user submission executed more than once.");
            await Until(() => Buffer(firstSurface).Contains("sent-once", StringComparison.Ordinal), "native input echo");
            Console.WriteLine("PASS: clipboard commands and acknowledged shell submission work through the native surface.");

            uint? secondProcess = secondSession.ProcessId;
            first.Dispose();
            await Until(() => !ProcessExists(firstSession.ProcessId), "first pane process close");
            Require(second.IsRunning && secondSession.ProcessId == secondProcess, "Closing one pane affected the other pane.");
            second.Dispose();
            await Until(() => !ProcessExists(secondProcess), "second pane process close");
            Console.WriteLine("PASS: pane close reaps its own ConPTY process and keeps the other pane isolated.");
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            window.Content = null;
            window.Close();
            try { Directory.Delete(root, recursive: true); } catch { }
            Environment.SetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER", oldRenderer);
        }
    }

    private static void VerifyStateBackgroundRendering()
    {
        var emulator = new TerminalEmulator(20, 6);
        emulator.Feed("\x1b[48;5;196;97mX \x1b[0m\r\n");
        emulator.Feed("\x1b[48;2;20;40;180;97mY \x1b[0m\r\n");
        emulator.Feed("\x1b[44;7mZ \x1b[0m\r\n");
        var surface = new NativeTerminalSurface(emulator);
        var window = new Window { Width = 480, Height = 240, Content = surface, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            surface.SetTheme("#FF8A00", "#211000", "#FFC47A", forceStateBackground: true);
            var forced = CaptureSurface(surface);
            Color orange = ParseTestColor("#FF8A00");
            RequireNear(orange, CellPixel(forced, surface, row: 0, column: 1), 3,
                "Indexed ANSI background escaped the forced orange state fill.");
            RequireNear(orange, CellPixel(forced, surface, row: 1, column: 1), 3,
                "Truecolor ANSI background escaped the forced orange state fill.");
            RequireNear(orange, CellPixel(forced, surface, row: 2, column: 1), 3,
                "Inverse ANSI background escaped the forced orange state fill.");
            Require(CellHasDarkGlyph(forced, surface, row: 0),
                "Bright ANSI white text was not corrected for contrast against the orange state fill.");
            Require(CellHasDarkGlyphNearBottom(forced, surface, row: 2, column: 1),
                "Forced inverse video lost its visible underline/menu cue.");

            surface.SelectAllText();
            window.UpdateLayout();
            var selected = CaptureSurface(surface);
            Color selectedPixel = CellPixel(selected, surface, row: 4, column: 4);
            Require(selectedPixel != orange && selectedPixel.R >= 245 && selectedPixel.G > orange.G && selectedPixel.B > orange.B,
                "Selection did not remain visible as a translucent tint of the orange state color.");
            surface.ClearSelectionText();

            surface.SetTheme("#00853F", "#FFFFFF", "#00572D", forceStateBackground: true);
            RequireNear(ParseTestColor("#00853F"), CellPixel(CaptureSurface(surface), surface, row: 1, column: 1), 3,
                "The forced state background did not update to active green.");
            surface.SetTheme("#252D3A", "#FFFFFF", "#43536B", forceStateBackground: true);
            RequireNear(ParseTestColor("#252D3A"), CellPixel(CaptureSurface(surface), surface, row: 1, column: 1), 3,
                "The forced state background did not update to inactive gray.");

            surface.SetTheme("#FF8A00", "#211000", "#FFC47A", forceStateBackground: false);
            var ordinaryShell = CaptureSurface(surface);
            RequireNear(ParseTestColor("#FF0000"), CellPixel(ordinaryShell, surface, row: 0, column: 1), 3,
                "Opt-out shell mode changed an indexed ANSI background.");
            RequireNear(ParseTestColor("#1428B4"), CellPixel(ordinaryShell, surface, row: 1, column: 1), 3,
                "Opt-out shell mode changed an ANSI truecolor background.");
            RequireNear(ParseTestColor("#211000"), CellPixel(ordinaryShell, surface, row: 2, column: 1), 3,
                "Opt-out shell mode changed the background of an inverse ANSI run.");
            VerifyViewportThemeFill();
            Console.WriteLine("PASS: native ANSI backgrounds follow status color only when forced, with contrast, inverse, and selection cues preserved.");
        }
        finally
        {
            window.Content = null;
            window.Close();
            surface.DisposeSurface();
        }
    }

    private static void VerifyViewportThemeFill()
    {
        var surface = new NativeTerminalSurface(new TerminalEmulator(40, 24));
        surface.SetTheme("#00853F", "#FFFFFF", "#00572D", forceStateBackground: true);
        var viewport = NativeTerminalSurface.CreateScrollableViewport(surface);
        viewport.Padding = new Thickness(14);
        var window = new Window { Width = 480, Height = 520, Content = viewport, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var active = CaptureViewport(viewport);
            RequireNear(ParseTestColor("#00853F"), Pixel(active, 3, 3), 3,
                "Uncovered active-state viewport space did not use the terminal theme background.");

            surface.SetTheme("#252D3A", "#FFFFFF", "#43536B", forceStateBackground: true);
            window.UpdateLayout();
            var idle = CaptureViewport(viewport);
            RequireNear(ParseTestColor("#252D3A"), Pixel(idle, 3, 3), 3,
                "Uncovered viewport space did not update with the terminal state background.");

            surface.SetTheme("#FF8A00", "#211000", "#FFC47A", forceStateBackground: false);
            window.UpdateLayout();
            var shell = CaptureViewport(viewport);
            RequireNear(ParseTestColor("#FF8A00"), Pixel(shell, 3, 3), 3,
                "Ordinary shell viewport space did not follow the current theme background.");
        }
        finally
        {
            window.Content = null;
            window.Close();
            surface.DisposeSurface();
        }
    }

    private static async Task VerifyAdaptiveViewportAsync()
    {
        var emulator = new TerminalEmulator(40, 24);
        var surface = new NativeTerminalSurface(emulator);
        surface.DimensionsChanged += (columns, rows) => emulator.Resize(columns, rows);
        surface.SetTheme("#101010", "#FFFFFF", "#43536B");
        var viewport = NativeTerminalSurface.CreateScrollableViewport(surface);
        var host = new Grid();
        host.Children.Add(viewport);
        viewport.HorizontalAlignment = HorizontalAlignment.Left;
        viewport.VerticalAlignment = VerticalAlignment.Top;
        var window = new Window { Width = 1060, Height = 760, Content = host, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            foreach (var size in new[] { (240d, 160d), (120d, 500d), (640d, 200d), (960d, 600d), (240d, 160d), (960d, 600d) })
            {
                viewport.Width = size.Item1;
                viewport.Height = size.Item2;
                window.UpdateLayout();
                await Task.Delay(100);
                window.UpdateLayout();
                Rect bounds = surface.TransformToAncestor(viewport).TransformBounds(new Rect(surface.RenderSize));
                Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1,
                    $"The complete native TUI surface is clipped at {size}: surface={bounds}, viewport={viewport.RenderSize}.");
                Require(viewport.ScrollableWidth < 1 && viewport.ScrollableHeight < 1, "Fitted terminal requires outer scrolling.");
                var snapshot = emulator.GetSnapshot();
                Require(snapshot.Columns >= 40 && snapshot.Rows >= 24, "Fitting reduced the safe logical VT grid.");
                double expectedScale = Math.Min(1, Math.Min(viewport.ViewportWidth / surface.MinimumViewportWidth, viewport.ViewportHeight / surface.MinimumViewportHeight));
                Require(Math.Abs(bounds.Width / surface.ActualWidth - expectedScale) < 0.001,
                    "Terminal scale did not follow the viewport or return to normal at a larger size.");

                // Render colored cells at both ends of the terminal, including the bottom prompt row.
                surface.Feed($"\x1b[2J\x1b[1;1H\x1b[48;2;255;0;0m \x1b[{snapshot.Rows};{snapshot.Columns}H\x1b[48;2;0;255;0m \x1b[0m");
                await Task.Delay(60);
                var bitmap = CaptureViewport(viewport);
                double cellWidth = Field<double>(surface, "_cellWidth");
                double cellHeight = Field<double>(surface, "_cellHeight");
                var dpi = VisualTreeHelper.GetDpi(viewport);
                foreach (var marker in new[] { (0, 0, Colors.Red), (snapshot.Columns - 1, snapshot.Rows - 1, Colors.Lime) })
                {
                    var local = new Point((marker.Item1 + 0.5) * cellWidth, (marker.Item2 + 0.5) * cellHeight);
                    Point visible = surface.TranslatePoint(local, viewport);
                    Require(visible.X >= 0 && visible.Y >= 0 && visible.X < viewport.ActualWidth && visible.Y < viewport.ActualHeight,
                        "A terminal corner is outside the visible pane.");
                    RequireNear(marker.Item3, Pixel(bitmap, (int)(visible.X * dpi.DpiScaleX), (int)(visible.Y * dpi.DpiScaleY)), 8,
                        "A terminal corner was not rendered inside the small pane.");
                    Point input = viewport.TranslatePoint(visible, surface);
                    Require((input - local).Length < 0.01, "Scaled terminal pointer coordinates no longer map to the original cell.");
                }
            }
            Console.WriteLine("PASS: small native TUI panes fit both terminal corners, retain the minimum grid, and restore normal scale.");
        }
        finally
        {
            window.Content = null;
            window.Close();
            surface.DisposeSurface();
        }
    }

    private static BitmapSource CaptureViewport(FrameworkElement viewport)
    {
        viewport.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(viewport);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(viewport.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(viewport.ActualHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(viewport);
        return bitmap;
    }

    private static BitmapSource CaptureSurface(NativeTerminalSurface surface)
    {
        surface.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(surface);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(surface);
        return bitmap;
    }

    private static Color CellPixel(BitmapSource bitmap, NativeTerminalSurface surface, int row, int column)
    {
        double cellWidth = Field<double>(surface, "_cellWidth");
        double cellHeight = Field<double>(surface, "_cellHeight");
        var dpi = VisualTreeHelper.GetDpi(surface);
        int x = Math.Clamp((int)Math.Round((column * cellWidth + cellWidth * 0.65) * dpi.DpiScaleX), 0, bitmap.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round((row * cellHeight + cellHeight * 0.55) * dpi.DpiScaleY), 0, bitmap.PixelHeight - 1);
        return Pixel(bitmap, x, y);
    }

    private static bool CellHasDarkGlyph(BitmapSource bitmap, NativeTerminalSurface surface, int row)
    {
        double cellWidth = Field<double>(surface, "_cellWidth");
        double cellHeight = Field<double>(surface, "_cellHeight");
        var dpi = VisualTreeHelper.GetDpi(surface);
        int left = 0;
        int top = Math.Clamp((int)Math.Floor(row * cellHeight * dpi.DpiScaleY), 0, bitmap.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(cellWidth * dpi.DpiScaleX), left + 1, bitmap.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling((row + 1) * cellHeight * dpi.DpiScaleY), top + 1, bitmap.PixelHeight);
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
            if (RelativeLuminance(Pixel(bitmap, x, y)) < 0.10) return true;
        return false;
    }

    private static bool CellHasDarkGlyphNearBottom(BitmapSource bitmap, NativeTerminalSurface surface, int row, int column)
    {
        double cellWidth = Field<double>(surface, "_cellWidth");
        double cellHeight = Field<double>(surface, "_cellHeight");
        var dpi = VisualTreeHelper.GetDpi(surface);
        int left = Math.Clamp((int)Math.Floor(column * cellWidth * dpi.DpiScaleX), 0, bitmap.PixelWidth - 1);
        int right = Math.Clamp((int)Math.Ceiling((column + 1) * cellWidth * dpi.DpiScaleX), left + 1, bitmap.PixelWidth);
        int top = Math.Clamp((int)Math.Floor((row + 1) * cellHeight * dpi.DpiScaleY) - 3, 0, bitmap.PixelHeight - 1);
        int bottom = Math.Clamp(top + 4, top + 1, bitmap.PixelHeight);
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
            if (RelativeLuminance(Pixel(bitmap, x, y)) < 0.10) return true;
        return false;
    }

    private static Color Pixel(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        byte alpha = pixel[3];
        if (alpha == 0) return Colors.Transparent;
        byte Unpremultiply(byte value) => alpha == 255 ? value : (byte)Math.Clamp((int)Math.Round(value * 255.0 / alpha), 0, 255);
        return Color.FromArgb(alpha, Unpremultiply(pixel[2]), Unpremultiply(pixel[1]), Unpremultiply(pixel[0]));
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte component)
        {
            double value = component / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Color ParseTestColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value)!;

    private static void RequireNear(Color expected, Color actual, int tolerance, string message)
    {
        if (Math.Abs(expected.R - actual.R) > tolerance || Math.Abs(expected.G - actual.G) > tolerance || Math.Abs(expected.B - actual.B) > tolerance)
            throw new InvalidOperationException($"{message} Expected #{expected.R:X2}{expected.G:X2}{expected.B:X2}; got #{actual.R:X2}{actual.G:X2}{actual.B:X2}.");
    }

    private static string Buffer(NativeTerminalSurface surface) => (string)Call(surface, "GetEntireBufferText")!;

    private static async Task WaitReady(TerminalPane pane)
    {
        var method = typeof(TerminalPane).GetMethod("EnsureReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("EnsureReadyAsync");
        await (Task)method.Invoke(pane, new object[] { CancellationToken.None })!;
    }

    private static async Task Until(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token);
        }
        Console.WriteLine("PASS: " + label);
    }

    private static bool ProcessExists(uint? pid)
    {
        if (pid is null) return false;
        try { _ = System.Diagnostics.Process.GetProcessById((int)pid.Value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static T Field<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().Name, name));

    private static object? Call(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, null);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
