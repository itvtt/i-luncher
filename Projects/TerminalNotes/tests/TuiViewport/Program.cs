using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using TerminalNotes;

internal static class Program
{
    private static int _exitCode;

    [STAThread]
    private static int Main()
    {
        // These assertions inspect xterm.js internals; native coverage lives in NativeTerminalSurface.
        Environment.SetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER", "webview");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                await CheckAsync(true);
                await CheckAsync(false);
                await VerifyMultiPaneFocusAsync();
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
                _exitCode = 1;
            }
            finally { app.Shutdown(); }
        };
        app.Run();
        return _exitCode;
    }

    private static async Task VerifyMultiPaneFocusAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes-MultiPaneFocus-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(fixture, "first");
        string second = Path.Combine(fixture, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var window = new MainWindow(Path.Combine(fixture, "settings.json"), autoStartCodex: false);
        var panes = new List<TerminalPane>();
        try
        {
            window.Show();
            var tabs = (TabControl)window.FindName("TerminalTabs");
            var stateType = typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!;
            TabItem AddPane(string project, int sequence)
            {
                Call(window, "EnsureProjectTaskRow", project);
                var pane = new TerminalPane(project);
                panes.Add(pane);
                object state = Activator.CreateInstance(stateType)!;
                stateType.GetProperty("Pane")!.SetValue(state, pane);
                stateType.GetProperty("Title")!.SetValue(state, Path.GetFileName(project));
                stateType.GetProperty("Sequence")!.SetValue(state, sequence);
                stateType.GetProperty("PlanDocumentPath")!.SetValue(state, Path.Combine(project, "terminal-notes", "plan.md"));
                var header = new StackPanel();
                header.Children.Add(new TextBlock { Text = Path.GetFileName(project) });
                var tab = new TabItem { Tag = state, Header = header };
                Call(window, "AttachSingleProjectTerminal", project, tab);
                tabs.Items.Add(tab);
                return tab;
            }

            var firstTab = AddPane(first, 1);
            var secondTab = AddPane(second, 2);
            Call(window, "SetTerminalLayout", 2);
            object layout = typeof(MainWindow).GetField("_terminalSlotLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            layout.GetType().GetMethod("Assign")!.Invoke(layout, [first, 0]);
            layout.GetType().GetMethod("Assign")!.Invoke(layout, [second, 1]);
            Call(window, "ArrangeSingleProjectViews");
            foreach (var pane in panes) await UntilAsync(() => Task.FromResult(pane.IsReady));

            var firstBrowser = (WebView2)typeof(TerminalPane).GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panes[0])!;
            var secondBrowser = (WebView2)typeof(TerminalPane).GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panes[1])!;
            panes[0].FocusTerminal();
            await UntilAsync(async () => await firstBrowser.ExecuteScriptAsync("document.activeElement===terminal.textarea") == "true");
            for (int i = 0; i < 4; i++)
            {
                Call(window, "SelectTerminal", firstTab, false);
                await UntilAsync(async () => await firstBrowser.ExecuteScriptAsync("document.activeElement===terminal.textarea") == "true");
            }
            await panes[0].SendTextAsync("Write-Output ('FOCUS_'+'INPUT_OK')\r");
            await UntilAsync(async () => JsonSerializer.Deserialize<string>(await firstBrowser.ExecuteScriptAsync(
                "Array.from({length:terminal.buffer.active.length},(_,row)=>terminal.buffer.active.getLine(row).translateToString(true)).join('')"))!.Contains("FOCUS_INPUT_OK"));
            Require(tabs.SelectedItem == firstTab && panes[0].IsVisible,
                "The pane receiving input is not the selected, visible terminal.");
            Console.WriteLine("PASS two-pane focus: repeated keyboard activation and selection preserve xterm focus and input.");
        }
        finally
        {
            foreach (var pane in panes) pane.Dispose();
            window.Close();
        }
    }

    private static async Task CheckAsync(bool protect)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes-Viewport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        using var pane = new TerminalPane(fixture, protectTuiViewport: protect);
        var window = new Window { Content = pane, Width = 190, Height = 125 };
        try
        {
            window.Show();
            var browser = (WebView2)typeof(TerminalPane).GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane)!;
            await UntilAsync(() => Task.FromResult(pane.IsReady));
            await UntilAsync(async () => JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,row)=>terminal.buffer.active.getLine(row).translateToString(true)).join('')"))!.Contains("PS"));
            await VerifySizeAsync();
            window.Width = 900;
            window.Height = 600;
            await UntilAsync(async () => int.Parse(await browser.ExecuteScriptAsync("terminal.cols")) > 60);
            window.Width = 190;
            window.Height = 125;
            await Task.Delay(500);
            await VerifySizeAsync();
            string command = "$viewportSize=$Host.UI.RawUI.WindowSize; Write-Output ('NATIVE_'+'SIZE='+$viewportSize.Width+','+$viewportSize.Height)\r";
            await browser.ExecuteScriptAsync("send({type:'input',data:" + JsonSerializer.Serialize(command) + "})");
            await UntilAsync(async () => JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,row)=>terminal.buffer.active.getLine(row).translateToString(true)).join('')"))!.Contains("NATIVE_SIZE="));
            string text = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,row)=>terminal.buffer.active.getLine(row).translateToString(true)).join('')"))!;
            var native = System.Text.RegularExpressions.Regex.Match(text, @"NATIVE_SIZE=(\d+),(\d+)");
            Require(native.Success, "Native window size output missing.");
            Require(int.Parse(native.Groups[1].Value) == int.Parse(await browser.ExecuteScriptAsync("terminal.cols")), "Native/xterm columns differ.");
            Require(int.Parse(native.Groups[2].Value) == int.Parse(await browser.ExecuteScriptAsync("terminal.rows")), "Native/xterm rows differ.");
            (typeof(TerminalPane).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane) as IDisposable)?.Dispose();
            await Task.Delay(150);
            await VerifyScrollingAsync(browser, protect);
            if (protect)
            {
                window.Width = 900;
                window.Height = 600;
                await UntilAsync(async () => int.Parse(await browser.ExecuteScriptAsync("terminal.cols")) > 60);
                await VerifyStateBackgroundAsync(browser);
            }
            Console.WriteLine($"PASS protected={protect}: startup, grow/shrink, native dimensions, input and output.");

            async Task VerifySizeAsync()
            {
                int columns = int.Parse(await browser.ExecuteScriptAsync("terminal.cols"));
                int rows = int.Parse(await browser.ExecuteScriptAsync("terminal.rows"));
                Require(protect ? columns >= 40 && rows >= 24 : columns < 40 && rows < 12, $"Unexpected grid {columns}x{rows}, protected={protect}.");
                if (protect)
                {
                    Require(await browser.ExecuteScriptAsync("viewport.scrollWidth>viewport.clientWidth && viewport.scrollHeight>viewport.clientHeight") == "true", "Overflow must remain accessible.");
                    await browser.ExecuteScriptAsync("viewport.scrollLeft=100; viewport.scrollTop=50");
                    Require(await browser.ExecuteScriptAsync("viewport.scrollLeft>0 && viewport.scrollTop>0") == "true", "Viewport cannot be scrolled.");
                    await browser.ExecuteScriptAsync("viewport.scrollTop=0; viewport.scrollLeft=0; terminal.element.dispatchEvent(new WheelEvent('wheel',{deltaY:25,bubbles:true,cancelable:true})); terminal.element.dispatchEvent(new WheelEvent('wheel',{deltaY:25,shiftKey:true,bubbles:true,cancelable:true}))");
                    Require(await browser.ExecuteScriptAsync("viewport.scrollLeft>0 && viewport.scrollTop>0") == "true", "Wheel and Shift+wheel cannot reach overflow.");
                }
            }
        }
        finally
        {
            pane.Dispose();
            window.Close();
        }
    }

    private static async Task VerifyScrollingAsync(WebView2 browser, bool protect)
    {
        await browser.ExecuteScriptAsync("terminal.reset(); viewport.scrollTop=0; viewport.scrollLeft=0; window.scrollFixtureReady=false; terminal.write(Array.from({length:160},(_,i)=>'ROW_'+i+'\\r\\n').join(''),()=>window.scrollFixtureReady=true)");
        await UntilAsync(async () => await browser.ExecuteScriptAsync("window.scrollFixtureReady && terminal.buffer.active.baseY>100") == "true");
        await Task.Delay(150);
        Require(await browser.ExecuteScriptAsync("(()=>{const r=verticalScrollbar.getBoundingClientRect();return r.right<=innerWidth && r.top>=0 && r.height>0 && verticalScrollbar.scrollHeight>verticalScrollbar.clientHeight})()") == "true",
            "The visible right edge must expose a scrollable history bar even in narrow panes.");
        await browser.ExecuteScriptAsync("verticalScrollbar.scrollTop=0");
        await UntilAsync(async () => await browser.ExecuteScriptAsync("terminal.buffer.active.viewportY===0 && viewport.scrollTop===0") == "true");
        Require(await browser.ExecuteScriptAsync("terminal.buffer.active.getLine(terminal.buffer.active.viewportY).translateToString(true).includes('ROW_0')") == "true",
            "Dragging the scrollbar to the top did not reach old terminal output.");
        await browser.ExecuteScriptAsync("terminal.element.dispatchEvent(new WheelEvent('wheel',{deltaY:120,bubbles:true,cancelable:true}))");
        await UntilAsync(async () => await browser.ExecuteScriptAsync("terminal.buffer.active.viewportY>0") == "true");
        await browser.ExecuteScriptAsync("terminal.textarea.dispatchEvent(new KeyboardEvent('keydown',{key:'End',keyCode:35,shiftKey:true,bubbles:true,cancelable:true}))");
        await UntilAsync(async () => await browser.ExecuteScriptAsync("terminal.buffer.active.viewportY===terminal.buffer.active.baseY") == "true");
        await browser.ExecuteScriptAsync("terminal.textarea.dispatchEvent(new KeyboardEvent('keydown',{key:'Home',keyCode:36,shiftKey:true,bubbles:true,cancelable:true}))");
        await UntilAsync(async () => await browser.ExecuteScriptAsync("terminal.buffer.active.viewportY===0") == "true");
        await browser.ExecuteScriptAsync("window.scrollFixtureReady=false; terminal.write('NEW_OUTPUT\\r\\n',()=>window.scrollFixtureReady=true)");
        await UntilAsync(async () => await browser.ExecuteScriptAsync("window.scrollFixtureReady") == "true");
        await Task.Delay(100);
        Require(await browser.ExecuteScriptAsync("terminal.buffer.active.viewportY===0") == "true",
            "New output pulled a user reading history back to the bottom.");

        // A real TUI enables mouse reporting in the alternate buffer. Its wheel
        // must arrive as a mouse escape sequence, without moving the outer clip.
        await browser.ExecuteScriptAsync("window.scrollFixtureReady=false; window.wheelInput=''; window.wheelSubscription=terminal.onData(data=>window.wheelInput+=data); terminal.write('\\x1b[?1049h\\x1b[?1000h\\x1b[?1006h',()=>window.scrollFixtureReady=true)");
        await UntilAsync(async () => await browser.ExecuteScriptAsync("window.scrollFixtureReady && terminal.modes.mouseTrackingMode!=='none'") == "true");
        await browser.ExecuteScriptAsync("viewport.scrollTop=0; viewport.scrollLeft=0; window.wheelInput=''; (()=>{const r=terminal.element.getBoundingClientRect();terminal.element.dispatchEvent(new WheelEvent('wheel',{deltaY:120,clientX:r.x+15,clientY:r.y+15,bubbles:true,cancelable:true}))})()");
        Require(await browser.ExecuteScriptAsync("wheelInput.includes('\\x1b[<65;') && viewport.scrollTop===0") == "true",
            "OpenCode-style mouse wheel reporting was swallowed by the surrounding viewport.");
        await browser.ExecuteScriptAsync("window.wheelInput=''; (()=>{const r=verticalScrollbar.getBoundingClientRect();const e=new WheelEvent('wheel',{deltaY:120,clientX:r.x+2,clientY:r.y+20,bubbles:true,cancelable:true});window.railWheelAllowed=verticalScrollbar.dispatchEvent(e)})()");
        Require(await browser.ExecuteScriptAsync("wheelInput.includes('\\x1b[<65;') && !railWheelAllowed && viewport.scrollTop===0") == "true",
            "The visible scrollbar rail did not forward its wheel to the mouse-reporting TUI.");
        if (protect)
        {
            await browser.ExecuteScriptAsync("verticalScrollbar.scrollTop=verticalScrollbar.scrollHeight");
            await UntilAsync(async () => await browser.ExecuteScriptAsync("viewport.scrollTop>0") == "true");
            Require(await browser.ExecuteScriptAsync("terminal.buffer.active.type==='alternate'") == "true",
                "Scrolling a clipped TUI must preserve its alternate buffer.");
        }
        await browser.ExecuteScriptAsync("window.scrollFixtureReady=false; terminal.write('\\x1b[?1000l\\x1b[?1006l\\x1b[?1049l',()=>window.scrollFixtureReady=true);wheelSubscription.dispose()");
        await UntilAsync(async () => await browser.ExecuteScriptAsync("window.scrollFixtureReady && terminal.buffer.active.type==='normal'") == "true");
        await browser.ExecuteScriptAsync("verticalScrollbar.scrollTop=0");
        await UntilAsync(async () => await browser.ExecuteScriptAsync("terminal.buffer.active.viewportY===0") == "true");
        Console.WriteLine($"PASS protected={protect}: visible scrollbar, history wheel, keyboard, output anchoring, alternate-buffer mouse delivery and clipped viewport.");
    }

    private static async Task VerifyStateBackgroundAsync(WebView2 browser)
    {
        const string green = "#00853F";
        const string orange = "#FF8A00";
        const string output = "\x1b[0m\x1b[48;2;255;0;255m\x1b[38;2;255;255;255mX" +
            "        \x1b[48;5;201m        \x1b[41m        \x1b[48:2::255:0:255m        \x1b[0m";

        await RenderBackgroundFixtureAsync(browser, green, forceStateBackground: false, useDarkAnsi: false, output);
        var unforced = await CaptureScreenAsync(browser);
        Color unforcedPixel = unforced.ReadCell(4, 0);
        Require(IsColor(unforcedPixel, 255, 0, 255),
            $"The unforced screenshot must show the truecolor SGR background, proving the pixel fixture exercises rendered cells; sampled {unforcedPixel}.");

        await RenderBackgroundFixtureAsync(browser, green, forceStateBackground: true, useDarkAnsi: false, output);
        var forcedGreen = await CaptureScreenAsync(browser);
        foreach (int column in new[] { 4, 12, 20, 28 })
            Require(IsColor(forcedGreen.ReadCell(column, 0), 0, 133, 63),
                $"Protected green background did not reach rendered cell {column}: {forcedGreen.ReadCell(column, 0)}.");
        Require(forcedGreen.CountPixelsInCell(0, 0, pixel => pixel.R > 210 && pixel.G > 210 && pixel.B > 210) > 0,
            "Truecolor foreground text disappeared while masking ANSI backgrounds.");

        await RenderBackgroundFixtureAsync(browser, orange, forceStateBackground: true, useDarkAnsi: true,
            "\x1b[0m\x1b[48;2;255;0;255m\x1b[37mX        \x1b[0m");
        var forcedOrange = await CaptureScreenAsync(browser);
        Require(IsColor(forcedOrange.ReadCell(4, 0), 255, 138, 0),
            $"Protected orange background did not reach rendered cells: {forcedOrange.ReadCell(4, 0)}.");
        Require(forcedOrange.CountPixelsInCell(0, 0, pixel => pixel.R < 100 && pixel.G < 100 && pixel.B < 100) > 0,
            "Dark ANSI foreground text was not visible on the orange state background.");

        await RenderBackgroundFixtureAsync(browser, orange, forceStateBackground: true, useDarkAnsi: true,
            "\x1b[0m\x1b[38;2;48;40;100mX        \x1b[0m");
        var rgbForeground = await CaptureScreenAsync(browser);
        var rgbCell = await ReadCellAsync(browser, 0, 0);
        Require(rgbCell.ForegroundMode == 50331648 && rgbCell.Foreground == 0x302864,
            $"Truecolor foreground RGB channels that resemble SGR background codes were not preserved atomically: {rgbCell}.");
        Require(rgbForeground.CountPixelsInCell(0, 0, pixel => !IsColor(pixel, 255, 138, 0)) > 0,
            "Truecolor foreground text was not visible in the rendered screenshot.");

        await RenderBackgroundFixtureAsync(browser, orange, forceStateBackground: true, useDarkAnsi: true,
            "\x1b[0m\x1b[38:2::48:40:100;2;48;2;255;0;255mX        \x1b[0m");
        var colonForeground = await CaptureScreenAsync(browser);
        Require(IsColor(colonForeground.ReadCell(4, 0), 255, 138, 0),
            "A colon-form foreground followed by style parameters allowed the following background SGR to override state color.");
        var colonCell = await ReadCellAsync(browser, 0, 0);
        Require(colonCell.ForegroundMode == 50331648 && colonCell.Foreground == 0x302864 && colonCell.Dim != 0,
            $"Colon-form foreground or following SGR dim style was not preserved atomically: {colonCell}.");
        Require(colonForeground.CountPixelsInCell(0, 0, pixel => !IsColor(pixel, 255, 138, 0)) > 0,
            "Colon-form truecolor foreground text was not visible in the rendered screenshot.");

        await RenderBackgroundFixtureAsync(browser, "#000000", forceStateBackground: true, useDarkAnsi: false,
            "\x1b[0m\x1b[38;5;41mX        \x1b[0m");
        var indexedForeground = await CaptureScreenAsync(browser);
        var indexedCell = await ReadCellAsync(browser, 0, 0);
        Require(indexedCell.ForegroundMode is 16777216 or 33554432 && indexedCell.Foreground == 41,
            $"Indexed foreground color 41 was mistaken for an SGR palette background: {indexedCell}.");
        Require(indexedForeground.CountPixelsInCell(0, 0, pixel => !IsColor(pixel, 0, 0, 0)) > 0,
            "Indexed foreground color 41 was mistaken for an SGR palette background.");
        Console.WriteLine("PASS protected TUI: screenshot pixels retain state backgrounds across ANSI truecolor, indexed and palette SGR while preserving foregrounds.");
    }

    private static async Task RenderBackgroundFixtureAsync(WebView2 browser, string background,
        bool forceStateBackground, bool useDarkAnsi, string output)
    {
        var theme = new
        {
            type = "theme",
            background,
            foreground = useDarkAnsi ? "#211000" : "#FFFFFF",
            cursor = useDarkAnsi ? "#211000" : "#FFFFFF",
            selectionBackground = "#00572D",
            selectionForeground = "#FFFFFF",
            useDarkAnsi,
            forceStateBackground
        };
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(theme));
        string expectedTheme = JsonSerializer.Serialize(background);
        string expectedForce = JsonSerializer.Serialize(forceStateBackground.ToString().ToLowerInvariant());
        await UntilAsync(async () =>
            await browser.ExecuteScriptAsync("terminal.options.theme.background") == expectedTheme &&
            await browser.ExecuteScriptAsync("terminal.element.dataset.forceStateBackground") == expectedForce);
        await browser.ExecuteScriptAsync("terminal.reset()");
        int colorSequence = output.IndexOf("255;0;255m", StringComparison.Ordinal);
        if (colorSequence >= 0)
        {
            int split = colorSequence + "255;0;".Length;
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "output", data = output[..split] }));
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "output", data = output[split..] }));
        }
        else
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "output", data = output }));
        }
        await UntilAsync(async () => await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,row)=>terminal.buffer.active.getLine(row).translateToString(false)).some(line=>line.includes('X'))") == "true");
        await Task.Delay(150);
    }

    private static async Task<ScreenCapture> CaptureScreenAsync(WebView2 browser)
    {
        string json = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync(
            "JSON.stringify((()=>{const r=terminal.element.querySelector('.xterm-screen').getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,cols:terminal.cols,rows:terminal.rows,scale:window.devicePixelRatio}})())"))!;
        var geometry = JsonSerializer.Deserialize<ScreenGeometry>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        using var stream = new MemoryStream();
        await browser.CoreWebView2.CapturePreviewAsync(
            Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
        stream.Position = 0;
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return new ScreenCapture(pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, geometry);
    }

    private static async Task<CellColors> ReadCellAsync(WebView2 browser, int column, int row)
    {
        string expression = $"JSON.stringify((()=>{{const c=terminal.buffer.active.getLine({row}).getCell({column});return {{foregroundMode:c.getFgColorMode(),foreground:c.getFgColor(),dim:c.isDim()}}}})())";
        string json = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync(expression))!;
        return JsonSerializer.Deserialize<CellColors>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static bool IsColor(Color actual, byte red, byte green, byte blue) =>
        Math.Abs(actual.R - red) <= 3 && Math.Abs(actual.G - green) <= 3 && Math.Abs(actual.B - blue) <= 3;

    private sealed record ScreenGeometry(double X, double Y, double Width, double Height, int Cols, int Rows, double Scale);
    private sealed record CellColors(int ForegroundMode, int Foreground, int Dim);

    private sealed class ScreenCapture(byte[] pixels, int width, int height, int stride, ScreenGeometry geometry)
    {
        public Color ReadCell(int column, int row) => ReadPixel(column, row, .5, .5);

        public int CountPixelsInCell(int column, int row, Func<Color, bool> matches)
        {
            int count = 0;
            for (int y = 2; y < Math.Max(3, CellHeight - 2); y++)
            for (int x = 2; x < Math.Max(3, CellWidth - 2); x++)
            {
                if (matches(ReadPixel(column, row, x / CellWidth, y / CellHeight))) count++;
            }
            return count;
        }

        private double CellWidth => geometry.Width * geometry.Scale / geometry.Cols;
        private double CellHeight => geometry.Height * geometry.Scale / geometry.Rows;

        private Color ReadPixel(int column, int row, double xFraction, double yFraction)
        {
            int x = Math.Clamp((int)Math.Round((geometry.X + geometry.Width / geometry.Cols * (column + xFraction)) * geometry.Scale), 0, width - 1);
            int y = Math.Clamp((int)Math.Round((geometry.Y + geometry.Height / geometry.Rows * (row + yFraction)) * geometry.Scale), 0, height - 1);
            int offset = y * stride + x * 4;
            return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Viewport verification timed out.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object? Call(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
        return method.Invoke(target, arguments);
    }
}
