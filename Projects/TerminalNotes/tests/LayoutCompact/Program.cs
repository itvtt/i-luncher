using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        var app = new Application();
        app.Startup += async (_, _) => await RunAsync(app);
        app.Run();
        return _failures;
    }

    private static async Task RunAsync(Application app)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Layout " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "AGENTS.md"), "# Layout fixture\n");
        var window = new MainWindow(Path.Combine(fixture, "settings.json"), autoStartCodex: false, initialWorkspace: fixture);
        try
        {
            window.Show();
            var tabs = (TabControl)window.FindName("TerminalTabs");
            await UntilAsync(() => tabs.Items.Count == 1 && Pane((TabItem)tabs.Items[0]).IsRunning, "first shell startup");
            var first = Pane((TabItem)tabs.Items[0]);
            await UntilAsync(() => Field<TextBox>(window, "_tabDocumentEditor").IsEnabled, "MD editor load");
            var planEditor = Field<TextBox>(window, "_tabDocumentEditor");
            const string sharedPlanDraft = "# 구현 요구사항\n\n같은 프로젝트의 세션은 같은 플랜 MD를 사용합니다.";
            planEditor.Text = sharedPlanDraft;
            await Task.Delay(200);
            CaptureWindow(window, "design-desktop.png");

            Layout(window, 3);
            await UntilAsync(() => tabs.Items.Count == 3 && tabs.Items.Cast<TabItem>().All(tab => Pane(tab).IsRunning), "three shell startup");
            var firstThree = tabs.Items.Cast<TabItem>().Select(Pane).ToArray();
            var split = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("SplitTerminals");
            Check(ReferenceEquals(first, firstThree[0]) && split.Columns == 3 && split.Rows == 1 && split.Children.Count == 3,
                "3x1 layout preserves existing pane identity");
            Check(Descendants<TextBlock>(split).Any(text => text.FontSize == 20 && text.Text == "PowerShell"),
                "split title stays readable without taking over the terminal");

            Layout(window, 6);
            await UntilAsync(() => tabs.Items.Count == 6 && tabs.Items.Cast<TabItem>().All(tab => Pane(tab).IsRunning), "six shell startup");
            var allPanes = tabs.Items.Cast<TabItem>().Select(Pane).ToArray();
            Check(firstThree.SequenceEqual(allPanes.Take(3)) && split.Columns == 3 && split.Rows == 2 && split.Children.Count == 6,
                "3x2 layout preserves existing sessions");
            var picker = (WrapPanel)window.FindName("DispatcherSessions");
            var pickerTexts = ((StackPanel)((Button)picker.Children[0]).Content).Children.OfType<TextBlock>().ToArray();
            Check(pickerTexts.Any(text => text.FontSize == 16 && text.Text == "PowerShell") &&
                  pickerTexts.Any(text => text.FontSize >= 13 && text.Text.Contains("01", StringComparison.Ordinal)),
                "session picker uses readable titles and status text");

            window.Width = 820;
            window.Height = 480;
            window.UpdateLayout();
            Console.WriteLine($"Minimum layout: split={split.ActualHeight:F0}, workbench={((FrameworkElement)window.FindName("WorkbenchHost")).ActualHeight:F0}, panes={string.Join(',', allPanes.Where(pane => pane.Parent is not null).Select(pane => pane.ActualHeight.ToString("F0")))}");
            Check(allPanes.Where(pane => pane.Parent is not null).All(pane => pane.ActualHeight >= 34),
                "minimum window keeps at least two terminal rows visible in 3x2 layout");
            Check(window.FindName("CompactButton") is null &&
                  IsInsideWindowWidth(window, (FrameworkElement)window.FindName("NewTerminalButton")) &&
                  IsInsideWindowWidth(window, (FrameworkElement)window.FindName("FocusButton")),
                "monitor button is removed and terminal and plan actions remain reachable at minimum size");
            Check(IsInsideWindow(window, Field<TextBox>(window, "_tabDocumentEditor")) &&
                  IsInsideWindow(window, Field<Button>(window, "_tabDocumentSave")) &&
                  IsInsideWindow(window, Field<Button>(window, "_tabDocumentExecute")),
                "MD editor and save actions remain fully visible at minimum size");
            CaptureWindow(window, "design-desktop-minimum.png");
            Layout(window, 8);
            await UntilAsync(() => tabs.Items.Count == 8 && tabs.Items.Cast<TabItem>().All(tab => Pane(tab).IsRunning), "eight shell startup");
            allPanes = tabs.Items.Cast<TabItem>().Select(Pane).ToArray();
            string defaultPlanPath = Path.GetFullPath(Path.Combine(fixture, "terminal-notes", "plan.md"));
            string[] planPaths = tabs.Items.Cast<TabItem>()
                .Select(tab => Path.GetFullPath(Property<string>(tab.Tag, "PlanDocumentPath")))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Check(planPaths.Length == 1 && string.Equals(planPaths[0], defaultPlanPath, StringComparison.OrdinalIgnoreCase),
                "every same-project session uses the shared default plan path");
            tabs.SelectedIndex = 7;
            await UntilAsync(() => planEditor.IsEnabled && planEditor.Text == sharedPlanDraft,
                "shared project plan draft after selecting another terminal");
            Check(planEditor.Text == sharedPlanDraft,
                "same-project terminals share one unsaved plan draft");
            var planPathsBeforeRename = tabs.Items.Cast<TabItem>().Select(tab => Property<string>(tab.Tag, "PlanDocumentPath")).ToArray();
            Call(window, "RememberProjectName", fixture, "Compact Project");
            var splitProjectStatuses = Descendants<TextBlock>(split)
                .Where(text => text.Text.Contains("Compact Project", StringComparison.Ordinal)).ToArray();
            Check(splitProjectStatuses.Length == 8 &&
                  Descendants<TextBlock>(split).Count(text => text.FontSize == 20 && text.Text == "PowerShell") == 8,
                "project name appears in all small terminal statuses while role titles keep their size");
            var projectPickerButtons = Field<Dictionary<TabItem, Button>>(window, "_sessionButtons");
            var projectGroupHeading = ((StackPanel)projectPickerButtons[(TabItem)tabs.Items[0]].Content).Children.OfType<TextBlock>()
                .First(text => text.Text.Contains("Compact Project", StringComparison.Ordinal));
            Check(projectGroupHeading.Text.Contains("8\uAC1C \uC138\uC158", StringComparison.Ordinal) &&
                  (projectGroupHeading.ToolTip as string)?.Contains(fixture, StringComparison.OrdinalIgnoreCase) == true &&
                  tabs.Items.Cast<TabItem>().Select(tab => Pane(tab)).SequenceEqual(allPanes) &&
                  tabs.Items.Cast<TabItem>().Select(tab => Property<string>(tab.Tag, "PlanDocumentPath")).SequenceEqual(planPathsBeforeRename),
                "renaming the project updates its group label without changing sessions or plans");
            tabs.SelectedIndex = 0;
            Call(window, "ShowCompactDashboard");
            var dashboard = Application.Current.Windows.Cast<Window>().Single(candidate => candidate != window && candidate.Title.Contains("상태 모니터"));
            Check(!window.IsVisible && dashboard.IsVisible && dashboard.Topmost && allPanes.All(pane => pane.IsRunning),
                "floating mini dashboard hides main window and preserves sessions");
            dashboard.UpdateLayout();
            var miniGrid = Descendants<Grid>(dashboard).Single(grid => System.Windows.Automation.AutomationProperties.GetName(grid) == "미니 터미널 그리드");
            Check(miniGrid.ColumnDefinitions.Count == 4 && miniGrid.RowDefinitions.Count == 2,
                "mini dashboard mirrors the active 4x2 layout");
            Check(dashboard.ActualWidth == 440 && dashboard.ActualHeight == 200,
                "eight-tile floating window doubles its previous width and height");
            Check(Descendants<TextBlock>(miniGrid).Count(text => text.FontSize == 14 && text.Text == "PowerShell" && text.TextWrapping == TextWrapping.Wrap) == 8,
                "terminal titles are readable inside every tile");
            var titledTab = (TabItem)tabs.Items[1];
            const string fullTitle = "프로젝트 A · 구현 담당";
            titledTab.Tag.GetType().GetProperty("Title")!.SetValue(titledTab.Tag, fullTitle);
            Call(window, "UpdateTerminalTitle", titledTab);
            Call(window, "UpdateCompactDashboard");
            dashboard.UpdateLayout();
            Check(Descendants<TextBlock>(miniGrid).Any(text => text.Text == fullTitle && text.ActualHeight > 18 && text.ActualHeight <= 36),
                "renamed project title retains its prefix and wraps within the shape");
            CaptureWindow(dashboard, "layout-compact-dashboard-default.png");
            var dashboardButtons = Descendants<Button>(dashboard).ToArray();
            var fullButton = dashboardButtons.Single(button => (button.ToolTip as string)?.Contains("전체 화면", StringComparison.Ordinal) == true);
            var sessionButton = dashboardButtons.First(button => (button.ToolTip as string)?.Contains("대화형 터미널") == true);
            Check(IsInsideWindowWidth(dashboard, fullButton) && IsInsideWindowWidth(dashboard, sessionButton),
                "mini dashboard controls fit at the enlarged default size");
            Call(window, "UpdateCompactDashboard");
            var sameSessionButton = Descendants<Button>(dashboard).First(button =>
                (button.ToolTip as string)?.Contains("대화형 터미널") == true && Descendants<TextBlock>(button).Any(text => text.Text == "01"));
            Check(ReferenceEquals(sessionButton, sameSessionButton), "mini tile identity survives status redraw");

            allPanes[0].MarkWorkCompleted();
            await UntilAsync(() => (sessionButton.ToolTip as string)?.Contains("응답 완료", StringComparison.Ordinal) == true, "completed tile state");
            Check((sessionButton.Background as System.Windows.Media.SolidColorBrush)?.Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(TerminalActivityPalette.Background(TerminalActivity.Idle, true)),
                "completed response uses inactive gray tile fill");
            Check((sessionButton.BorderBrush as System.Windows.Media.SolidColorBrush)?.Color == System.Windows.Media.Colors.White,
                "selected inactive tile has a neutral outline instead of a misleading green state");
            CaptureWindow(dashboard, "layout-compact-dashboard.png");

            var modalChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = dashboard.Dispatcher.BeginInvoke(new Action(() =>
            {
                Window? modal = null;
                try
                {
                    modal = Application.Current.Windows.Cast<Window>().Single(candidate => candidate != window && candidate != dashboard && candidate.IsVisible);
                    Check(ReferenceEquals(modal.Owner, dashboard) && ReferenceEquals(Window.GetWindow(allPanes[0]), modal) && allPanes[0].IsRunning,
                        "modal owns and reparents the same live terminal pane");
                    Check(Descendants<TextBlock>(modal).Any(text => text.FontSize == 24 && text.Text == "PowerShell"),
                        "monitor terminal uses a compact readable title");
                    var modeButtons = Descendants<RadioButton>(modal).Where(button => button.GroupName == "CompactInputMode").ToArray();
                    Check(modeButtons.Length == 1 && modeButtons[0].Content as string == "플랜", "compact terminal exposes only the plan document");
                    modeButtons[0].IsChecked = true;
                    object state = ((TabItem)tabs.Items[0]).Tag;
                    Check(state.GetType().GetProperty("InputMode")!.GetValue(state)!.ToString() == "Plan", "compact mode selection keeps the plan document");
                    CaptureWindow(modal, "layout-compact-terminal.png");
                    modal.Close();
                    modalChecked.SetResult();
                }
                catch (Exception exception)
                {
                    modal?.Close();
                    modalChecked.SetException(exception);
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            sessionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await modalChecked.Task;
            Check(ReferenceEquals(Window.GetWindow(allPanes[0]), window) && allPanes[0].IsRunning,
                "completed tile modal closes back to the same grid without disposal");

            allPanes[0].MarkNeedsInput();
            await UntilAsync(() => (sessionButton.ToolTip as string)?.Contains("입력 필요", StringComparison.Ordinal) == true, "input-needed tile state");
            Check(ReferenceEquals(sessionButton, Descendants<Button>(dashboard).First(button =>
                      (button.ToolTip as string)?.Contains("대화형 터미널") == true && Descendants<TextBlock>(button).Any(text => text.Text == "01"))) &&
                  (sessionButton.Background as System.Windows.Media.SolidColorBrush)?.Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(TerminalActivityPalette.Background(TerminalActivity.NeedsInput)),
                "input-needed state keeps tile identity and uses vivid orange fill");
            var inputModalChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = dashboard.Dispatcher.BeginInvoke(new Action(() =>
            {
                Window? modal = null;
                try
                {
                    modal = Application.Current.Windows.Cast<Window>().Single(candidate => candidate != window && candidate != dashboard && candidate.IsVisible);
                    Check(ReferenceEquals(Window.GetWindow(allPanes[0]), modal), "input-needed tile opens its same terminal pane");
                    modal.Close();
                    inputModalChecked.SetResult();
                }
                catch (Exception exception)
                {
                    modal?.Close();
                    inputModalChecked.SetException(exception);
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            sessionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await inputModalChecked.Task;

            allPanes[1].MarkWorkCompleted();
            Field<TerminalActivityTracker>(allPanes[2], "_activityTracker").MarkError();
            await allPanes[3].SendTextAsync("1..30 | ForEach-Object { Write-Output $_; Start-Sleep -Milliseconds 300 }");
            await UntilAsync(() => Descendants<Button>(dashboard).Any(button => (button.ToolTip as string)?.Contains("진행 중", StringComparison.Ordinal) == true) &&
                                  Descendants<Button>(dashboard).Any(button => (button.ToolTip as string)?.Contains("응답 완료", StringComparison.Ordinal) == true), "multi-state mini grid");
            var stateTiles = Descendants<Button>(dashboard).Where(button => (button.ToolTip as string)?.Contains("대화형 터미널") == true).ToArray();
            Check(stateTiles.Select(button => (button.Background as System.Windows.Media.SolidColorBrush)?.Color).Distinct().Count() == 3,
                "mini grid uses exactly green, orange, and inactive gray fills");
            await Task.Delay(200); // Allow template bindings and the current frame to render.
            Check(stateTiles.All(button =>
                ((button.Template.FindName("Chrome", button) as Border)?.Background as System.Windows.Media.SolidColorBrush)?.Color ==
                (button.Background as System.Windows.Media.SolidColorBrush)?.Color),
                "rendered tile surfaces keep their state fill");
            foreach (var tile in stateTiles)
            {
                Check(tile.Focus(), "state tile receives keyboard focus");
                await dashboard.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(HasSolidStateInterior(tile), "focused tile interior is filled with its state color");
            }
            CaptureWindow(dashboard, "layout-compact-dashboard-states.png");
            allPanes[3].Interrupt();

            Layout(window, 3);
            dashboard.UpdateLayout();
            Check(miniGrid.ColumnDefinitions.Count == 3 && miniGrid.RowDefinitions.Count == 1,
                "mini dashboard follows a live switch to 3x1 layout");
            var nextPage = Descendants<Button>(dashboard).Single(button => button.Content as string == "›");
            Check(nextPage.IsEnabled, "hidden terminal page exposes next navigation");
            nextPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dashboard.UpdateLayout();
            Check(Field<int>(dashboard, "_page") == 1 &&
                  Field<TabItem[]>(dashboard, "_renderedTabs").SequenceEqual(tabs.Items.Cast<TabItem>().Skip(3).Take(3)),
                "next page shows the hidden session group in matching slots");
            var previousPage = Descendants<Button>(dashboard).Single(button => button.Content as string == "‹");
            previousPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dashboard.UpdateLayout();
            Check(Field<int>(dashboard, "_page") == 0 &&
                  Field<TabItem[]>(dashboard, "_renderedTabs").SequenceEqual(tabs.Items.Cast<TabItem>().Take(3)),
                "previous page returns to the original mini grid");

            Layout(window, 1);
            dashboard.UpdateLayout();
            Check(miniGrid.ColumnDefinitions.Count == 1 && miniGrid.RowDefinitions.Count == 1 && dashboard.ActualWidth == 240 && dashboard.ActualHeight == 128,
                "single-layout floating window also doubles its previous width and height");
            var tinyHeaderButtons = Descendants<Button>(dashboard).Where(button => button.Content as string is "‹" or "›" or "↗" or "×").ToArray();
            Check(tinyHeaderButtons.Length == 4 && tinyHeaderButtons.All(button => IsInsideWindowWidth(dashboard, button)),
                "single-layout multi-session header keeps enlarged pager, restore, and close icons unclipped");

            dashboard.Close();
            await UntilAsync(() => window.IsVisible, "main window restore");
            Check(allPanes.All(pane => pane.IsRunning), "dashboard close preserves every session");
            Console.WriteLine("LAYOUT COMPACT TESTS PASSED");
        }
        catch (Exception exception)
        {
            _failures++;
            Console.Error.WriteLine("FAIL " + exception);
        }
        finally
        {
            Call(window, "DisposeResources");
            window.Close();
            app.Shutdown();
            Console.WriteLine("Fixture: " + fixture);
        }
    }

    private static TerminalPane Pane(TabItem tab) =>
        (TerminalPane)tab.Tag.GetType().GetProperty("Pane")!.GetValue(tab.Tag)!;

    private static void Layout(MainWindow window, int count) => Call(window, "SetTerminalLayout", count);

    private static object? Call(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static T Property<T>(object target, string name) =>
        (T)target.GetType().GetProperty(name)!.GetValue(target)!;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }

    private static async Task UntilAsync(Func<bool> condition, string name, int seconds = 25)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(name);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static bool IsInsideWindowWidth(Window window, FrameworkElement element)
    {
        Point point = element.TranslatePoint(new Point(0, 0), window);
        return point.X >= 0 && point.X + element.ActualWidth <= window.ActualWidth;
    }

    private static bool IsInsideWindow(Window window, FrameworkElement element)
    {
        Point point = element.TranslatePoint(new Point(0, 0), window);
        return element.ActualHeight > 0 && element.ActualWidth > 0 && point.X >= 0 && point.Y >= 0 &&
               point.X + element.ActualWidth <= window.ActualWidth && point.Y + element.ActualHeight <= window.ActualHeight;
    }

    private static bool HasSolidStateInterior(Button tile)
    {
        var expected = ((System.Windows.Media.SolidColorBrush)tile.Background).Color;
        int width = (int)Math.Ceiling(tile.ActualWidth);
        int height = (int)Math.Ceiling(tile.ActualHeight);
        var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var surface = new System.Windows.Media.DrawingVisual();
        using (var drawing = surface.RenderOpen())
            drawing.DrawRectangle(new System.Windows.Media.VisualBrush(tile), null, new System.Windows.Rect(0, 0, width, height));
        rendered.Render(surface);
        byte[] pixels = new byte[width * height * 4];
        rendered.CopyPixels(pixels, width * 4, 0);
        int matching = 0, interior = 0;
        for (int y = 4; y < height - 4; y++)
        for (int x = 4; x < width - 4; x++)
        {
            int index = (y * width + x) * 4;
            interior++;
            if (pixels[index] == expected.B && pixels[index + 1] == expected.G &&
                pixels[index + 2] == expected.R && pixels[index + 3] == 255) matching++;
        }
        // Leave room for title glyphs while rejecting border-only state coloring.
        return interior > 0 && matching > interior * 0.75;
    }

    private static void CaptureWindow(Window window, string filename)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        GetWindowRect(handle, out var rectangle);
        using var bitmap = new System.Drawing.Bitmap(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            IntPtr deviceContext = graphics.GetHdc();
            try { _ = PrintWindow(handle, deviceContext, 2); }
            finally { graphics.ReleaseHdc(deviceContext); }
        }
        string output = Path.Combine(AppContext.BaseDirectory, filename);
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("Screenshot: " + output);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rectangle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);
}
