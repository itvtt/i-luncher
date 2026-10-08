using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        // Keep this browser context-menu fixture on its explicit legacy renderer.
        Environment.SetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER", "webview");
        var app = new Application();
        app.Startup += async (_, _) => await RunAsync(app);
        app.Run();
        return _failures;
    }

    private static async Task RunAsync(Application app)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "Terminal Notes Context " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "AGENTS.md"), "# Terminal context fixture\n");
        IDataObject? originalClipboard = null;
        bool capturedClipboard = false;
        try
        {
            originalClipboard = CloneClipboard();
            capturedClipboard = true;
        }
        catch { }
        var window = new MainWindow(Path.Combine(fixture, "settings.json"), autoStartCodex: false, initialWorkspace: fixture);
        try
        {
            window.Show();
            var tabs = (TabControl)window.FindName("TerminalTabs");
            await UntilAsync(() => tabs.Items.Count == 1 && Pane((TabItem)tabs.Items[0]).IsReady, "terminal startup");
            var tab = (TabItem)tabs.Items[0];
            var pane = Pane(tab);
            var browser = Field<WebView2>(pane, "_browser");

            await pane.SendTextAsync("Write-Output 'CONTEXT_SELECTION_MARKER'");
            await UntilAsync(async () => (await ScreenAsync(browser)).Contains("CONTEXT_SELECTION_MARKER", StringComparison.Ordinal), "selection marker output");
            await OpenContextMenuAsync(browser, pane, selectAll: true);

            string[] expected = ["복사", "붙여넣기", "전체 선택", "선택 해제", "화면 지우기"];
            string[] labels = Items(pane.ContextMenu).Select(ItemHeader).ToArray();
            Check(labels.SequenceEqual(expected), "terminal body menu contains only xterm buffer and clipboard actions");
            Check(labels.All(label => !label.Contains("터미널 닫기", StringComparison.Ordinal) && !label.Contains("이름 바꾸기", StringComparison.Ordinal)),
                "terminal body menu excludes application session actions");
            Check(!ReferenceEquals(pane.ContextMenu, tab.ContextMenu) && Items(tab.ContextMenu).Any(item => ItemHeader(item).Contains("터미널 닫기", StringComparison.Ordinal)),
                "tab title keeps its separate session menu");

            var copy = Item(pane.ContextMenu, "복사");
            var paste = Item(pane.ContextMenu, "붙여넣기");
            var selectAll = Item(pane.ContextMenu, "전체 선택");
            var clearSelection = Item(pane.ContextMenu, "선택 해제");
            var clearScreen = Item(pane.ContextMenu, "화면 지우기");
            Check(copy.IsEnabled && clearSelection.IsEnabled, "selection actions are enabled for an xterm selection");

            Click(copy);
            await UntilAsync(() => Clipboard.ContainsText() && Clipboard.GetText().Contains("CONTEXT_SELECTION_MARKER", StringComparison.Ordinal), "copy selection to clipboard");
            Check(true, "copy reads the actual xterm selection into the Windows clipboard");

            Click(clearSelection);
            await UntilAsync(async () => !await BoolAsync(browser, "terminal.hasSelection()"), "clear selection");
            await OpenContextMenuAsync(browser, pane, selectAll: false);
            Check(!copy.IsEnabled && !clearSelection.IsEnabled, "copy and clear-selection disable when xterm has no selection");
            pane.ContextMenu.IsOpen = false;

            Click(selectAll);
            await UntilAsync(async () => await BoolAsync(browser, "terminal.hasSelection()"), "select all");
            Check(true, "select all selects the actual xterm buffer");
            pane.ClearSelection();
            await UntilAsync(async () => !await BoolAsync(browser, "terminal.hasSelection()"), "selection reset before paste");

            const string declinedMarker = "MULTILINE_CONTEXT_SHOULD_NOT_PASTE";
            SetClipboard("Set-Variable first 1\r\nWrite-Output '" + declinedMarker + "'");
            await OpenContextMenuAsync(browser, pane, selectAll: false);
            Check(paste.IsEnabled, "paste enables when the clipboard contains text");
            DeclineNextMultilinePaste(window.Dispatcher);
            Click(paste);
            await Task.Delay(150);
            Check(!(await ScreenAsync(browser)).Contains(declinedMarker, StringComparison.Ordinal),
                "declining the multiline warning leaves terminal input unchanged");

            const string pasteText = "Set-Variable -Name contextMenuValue -Value 73";
            SetClipboard(pasteText);
            await OpenContextMenuAsync(browser, pane, selectAll: false);
            Click(paste);
            await UntilAsync(async () => (await CurrentLineAsync(browser)).Contains("MenuValue -Value 73", StringComparison.Ordinal), "context menu paste into xterm");
            await Task.Delay(250);
            Check((await CurrentLineAsync(browser)).Contains("MenuValue -Value 73", StringComparison.Ordinal),
                "context menu paste does not submit the command on its own");
            await pane.SendTextAsync(string.Empty);
            await UntilAsync(async () => !(await CurrentLineAsync(browser)).Contains("MenuValue -Value 73", StringComparison.Ordinal), "pasted command submission");
            await pane.SendTextAsync("Write-Output ('CONTEXT_VALUE=' + $contextMenuValue)");
            await UntilAsync(async () => (await ScreenAsync(browser)).Contains("CONTEXT_VALUE=73", StringComparison.Ordinal), "pasted command effect");
            Check(true, "paste writes through the same live terminal pane");

            string? pasteStatus = null;
            pane.StatusChanged += value => pasteStatus = value;
            SetClipboard(new string('x', 200_001));
            await OpenContextMenuAsync(browser, pane, selectAll: false);
            Click(paste);
            Check(pasteStatus?.Contains("200,000자 이하", StringComparison.Ordinal) == true,
                "context menu paste reuses the clipboard length limit");

            Click(clearScreen);
            await UntilAsync(async () => !(await ScreenAsync(browser)).Contains("CONTEXT_SELECTION_MARKER", StringComparison.Ordinal), "clear screen");
            Check(true, "clear screen operates on the actual xterm buffer");

            SetProperty(pane, "IsUserInputBlocked", true);
            SetClipboard("BLOCKED_CONTEXT_PASTE");
            pane.PasteFromClipboard();
            await browser.ExecuteScriptAsync("terminal.input(" + JsonSerializer.Serialize("Set-Variable blockedManual 1\r") + ")");
            await Task.Delay(250);
            Check(!pane.CanPasteFromClipboard && !(await ScreenAsync(browser)).Contains("BLOCKED_CONTEXT_PASTE", StringComparison.Ordinal),
                "exit input block rejects context paste and manual xterm input");
            await pane.SendTextAsync("Write-Output ('BLOCKED_MANUAL=' + [bool](Get-Variable blockedManual -ErrorAction SilentlyContinue))");
            await UntilAsync(async () => (await ScreenAsync(browser)).Contains("BLOCKED_MANUAL=False", StringComparison.Ordinal), "programmatic input while user input blocked");
            Check(true, "exit input block still permits acknowledged programmatic terminal input");
            SetProperty(pane, "IsUserInputBlocked", false);

            Call(pane, "SetAutomationBusy", true);
            await UntilAsync(async () => string.Equals((await ThemeAsync(browser)).Background,
                TerminalActivityPalette.Background(TerminalActivity.Active), StringComparison.OrdinalIgnoreCase), "known automation busy xterm theme");
            Check(pane.Activity == TerminalActivity.Active && pane.ActivityLabel == "진행 중" && !pane.HasCompletedWork &&
                  string.Equals((await ThemeAsync(browser)).Background, "#00853F", StringComparison.OrdinalIgnoreCase),
                "known automation work overlays quiet tracker state as active");

            pane.MarkNeedsInput();
            await UntilAsync(async () => string.Equals((await ThemeAsync(browser)).Background,
                TerminalActivityPalette.Background(TerminalActivity.NeedsInput), StringComparison.OrdinalIgnoreCase), "needs-input xterm theme");
            var needsInputTheme = await ThemeAsync(browser);
            Check(string.Equals(needsInputTheme.Background, "#FF8A00", StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(needsInputTheme.Foreground, "#211000", StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(needsInputTheme.Cursor, needsInputTheme.Foreground, StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(needsInputTheme.SelectionBackground, needsInputTheme.Background, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(needsInputTheme.Red, "#7b1027", StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(needsInputTheme.Green, "#084f25", StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(needsInputTheme.Blue, "#073e8a", StringComparison.OrdinalIgnoreCase),
                "orange terminal theme uses dark foreground, cursor, selection, and hue-preserving ANSI colors");
            Check(await BoolAsync(browser, "terminal.options.minimumContrastRatio>=4.5"),
                "xterm enforces at least a 4.5 minimum contrast ratio on saturated status backgrounds");
            Check(pane.Activity == TerminalActivity.NeedsInput && pane.ActivityLabel.Contains("확인 필요", StringComparison.Ordinal),
                "input-needed state takes priority over known automation busy");

            pane.ClearActivityOverride();
            await UntilAsync(async () => string.Equals((await ThemeAsync(browser)).Red, "#f18188", StringComparison.OrdinalIgnoreCase), "default ANSI theme restore");
            pane.MarkWorkCompleted();
            Check(!pane.HasCompletedWork, "known automation busy hides a tracker completion");
            Call(pane, "SetAutomationBusy", false);
            await UntilAsync(async () => string.Equals((await ThemeAsync(browser)).Background,
                TerminalActivityPalette.Background(pane.Activity, hasCompletedWork: true), StringComparison.OrdinalIgnoreCase), "completed xterm theme");
            var completedTheme = await ThemeAsync(browser);
            Check(pane.ActivityLabel == "응답 완료" &&
                  string.Equals(completedTheme.Background, "#252D3A", StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(completedTheme.Foreground, "#FFFFFF", StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(completedTheme.Cursor, completedTheme.Foreground, StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(completedTheme.SelectionBackground, completedTheme.Background, StringComparison.OrdinalIgnoreCase),
                "completed work applies the inactive gray terminal theme and completed label");

            Call(pane, "SetAutomationBusy", true);
            await UntilAsync(() => pane.Activity == TerminalActivity.Active && !pane.HasCompletedWork, "busy overlay after completion");
            Call(pane, "MarkWorkFailed");
            await UntilAsync(async () => string.Equals((await ThemeAsync(browser)).Background,
                TerminalActivityPalette.Background(TerminalActivity.Error), StringComparison.OrdinalIgnoreCase), "error xterm theme");
            var errorTheme = await ThemeAsync(browser);
            Check(pane.Activity == TerminalActivity.Error && !pane.HasCompletedWork &&
                  string.Equals(errorTheme.Background, "#252D3A", StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(errorTheme.Foreground, "#FFFFFF", StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(errorTheme.Cursor, errorTheme.Foreground, StringComparison.OrdinalIgnoreCase),
                "work failure takes priority over known busy and completion with readable gray xterm colors");

            Call(pane, "SetAutomationBusy", true);
            await UntilAsync(() => pane.Activity == TerminalActivity.Active && pane.ActivityLabel == "진행 중", "next run clears failed overlay");
            await pane.SendTextAsync("Write-Output 'RECOVERED_AUTOMATION_RUN'");
            Call(pane, "SetAutomationBusy", false);
            pane.MarkWorkCompleted();
            await UntilAsync(async () => string.Equals((await ThemeAsync(browser)).Background,
                TerminalActivityPalette.Background(pane.Activity, hasCompletedWork: true), StringComparison.OrdinalIgnoreCase), "recovered completion theme");
            Check(pane.HasCompletedWork && pane.ActivityLabel == "응답 완료",
                "a new busy run can recover from work failure and complete");

            Call(pane, "SetAutomationBusy", true);
            Field<TerminalActivityTracker>(pane, "_activityTracker").MarkError();
            await UntilAsync(async () => string.Equals((await ThemeAsync(browser)).Background,
                TerminalActivityPalette.Background(TerminalActivity.Error), StringComparison.OrdinalIgnoreCase), "real terminal error priority");
            Check(pane.Activity == TerminalActivity.Error && !pane.HasCompletedWork,
                "real terminal error remains higher priority than known automation busy");

            Console.WriteLine("TERMINAL CONTEXT TESTS PASSED");
        }
        catch (Exception exception)
        {
            _failures++;
            Console.Error.WriteLine("TERMINAL CONTEXT TESTS FAILED\n" + exception);
        }
        finally
        {
            if (capturedClipboard)
            {
                try
                {
                    if (originalClipboard is null) Clipboard.Clear();
                    else Clipboard.SetDataObject(originalClipboard, true);
                }
                catch { }
            }
            try { window.Close(); } catch { }
            app.Shutdown(_failures);
        }
    }

    private static async Task OpenContextMenuAsync(WebView2 browser, TerminalPane pane, bool selectAll)
    {
        pane.ContextMenu.IsOpen = false;
        string selection = selectAll ? "terminal.selectAll();" : string.Empty;
        await browser.ExecuteScriptAsync(selection + "document.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,button:2,clientX:20,clientY:20}));true");
        await UntilAsync(() => pane.ContextMenu.IsOpen, "terminal context menu open");
    }

    private static IEnumerable<MenuItem> Items(ContextMenu menu) => menu.Items.OfType<MenuItem>();
    private static MenuItem Item(ContextMenu menu, string header) => Items(menu).Single(item => ItemHeader(item) == header);
    private static string ItemHeader(MenuItem item) => item.Header?.ToString() ?? string.Empty;
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static TerminalPane Pane(TabItem tab) =>
        (TerminalPane)(tab.Tag.GetType().GetProperty("Pane", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(tab.Tag)
            ?? throw new MissingMemberException(tab.Tag.GetType().Name, "Pane"));

    private static T Field<T>(object instance, string name) where T : class =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().Name, name));

    private static void Call(object instance, string name, params object[] arguments) =>
        (instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().Name, name)).Invoke(instance, arguments);

    private static void SetProperty(object instance, string name, object value) =>
        (instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(instance.GetType().Name, name)).SetValue(instance, value);

    private static async Task<string> ScreenAsync(WebView2 browser) =>
        JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')")) ?? string.Empty;

    private static async Task<string> CurrentLineAsync(WebView2 browser) =>
        JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync(
            "(()=>{const b=terminal.buffer.active;let y=b.baseY+b.cursorY;let text='';while(y>=0){const line=b.getLine(y);if(!line)break;text=line.translateToString(false)+text;if(!line.isWrapped)break;y--;}return text})()")) ?? string.Empty;

    private static async Task<bool> BoolAsync(WebView2 browser, string script) =>
        JsonSerializer.Deserialize<bool>(await browser.ExecuteScriptAsync(script));

    private static async Task<XtermTheme> ThemeAsync(WebView2 browser) =>
        JsonSerializer.Deserialize<XtermTheme>(await browser.ExecuteScriptAsync(
            "(()=>{const t=terminal.options.theme;return {background:t.background,foreground:t.foreground,cursor:t.cursor,selectionBackground:t.selectionBackground,red:t.red,green:t.green,blue:t.blue}})()"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("xterm theme was unavailable.");

    private static void SetClipboard(string text)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch (Exception exception) { last = exception; Thread.Sleep(40); }
        }
        throw new InvalidOperationException("Could not set the test clipboard.", last);
    }

    private static IDataObject? CloneClipboard()
    {
        IDataObject? source = Clipboard.GetDataObject();
        if (source is null) return null;
        var copy = new DataObject();
        foreach (string format in source.GetFormats(autoConvert: false))
        {
            try
            {
                object? data = source.GetData(format, autoConvert: false);
                if (data is not null) copy.SetData(format, data);
            }
            catch { }
        }
        return copy;
    }

    private static void DeclineNextMultilinePaste(Dispatcher dispatcher)
    {
        var started = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Send, (sender, _) =>
        {
            nint dialog = FindWindow(null, "여러 줄 붙여넣기");
            if (dialog != 0)
            {
                ((DispatcherTimer)sender!).Stop();
                SendMessage(dialog, 0x0111, 7, 0);
            }
            else if (started.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("The multiline paste confirmation did not appear.");
        }, dispatcher);
        timer.Start();
    }

    private static async Task UntilAsync(Func<bool> condition, string name)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(name);
            await Task.Delay(40);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string name)
    {
        var timer = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(name);
            await Task.Delay(40);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private sealed record XtermTheme(
        string Background,
        string Foreground,
        string Cursor,
        string SelectionBackground,
        string Red,
        string Green,
        string Blue);
}
