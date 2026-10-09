using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Text;
using System.Windows.Threading;
using System.ComponentModel;
using System.Windows.Shell;
using SwiftCopy.Core;

namespace SwiftCopy.UiChecks;
internal static class Program
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendText(IntPtr window, uint message, IntPtr wParam, string text);
    private delegate bool EnumWindow(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumWindow callback, IntPtr data);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hwnd);

    private static void FillNativeDialog(Action open, string titlePrefix, string input, bool cancel = false)
    {
        Exception? error = null;
        bool handled = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        int tries = 0;
        timer.Tick += (_, _) =>
        {
            IntPtr dialog = IntPtr.Zero;
            EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
            {
                var title = new StringBuilder(512); GetWindowText(hwnd, title, title.Capacity);
                if (title.ToString().StartsWith(titlePrefix, StringComparison.Ordinal)) { dialog = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            if (dialog == IntPtr.Zero) { if (++tries > 50) { timer.Stop(); error = new Exception("Native picker not found"); } return; }
            timer.Stop();
            try
            {
                if (!cancel)
                {
                    IntPtr edit = IntPtr.Zero;
                    EnumChildWindows(dialog, (hwnd, _) =>
                    {
                        var name = new StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
                        if (name.ToString() == "Edit" && GetDlgCtrlID(hwnd) is 1148 or 1152) edit = hwnd;
                        return true;
                    }, IntPtr.Zero);
                    if (edit == IntPtr.Zero) throw new Exception("Native picker path edit not found");
                    SendText(edit, 12, IntPtr.Zero, input);
                }
                handled = true;
                if (cancel) SendMessage(dialog, 0x10, IntPtr.Zero, IntPtr.Zero);
                else
                {
                    IntPtr acceptButton = IntPtr.Zero;
                    EnumChildWindows(dialog, (hwnd, _) =>
                    {
                        var name = new StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
                        if (name.ToString() == "Button" && GetDlgCtrlID(hwnd) == 1) acceptButton = hwnd;
                        return true;
                    }, IntPtr.Zero);
                    if (acceptButton == IntPtr.Zero) throw new Exception("Native accept button not found");
                    SendMessage(acceptButton, 245, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception ex) { error = ex; SendMessage(dialog, 0x111, (IntPtr)2, IntPtr.Zero); }
        };
        timer.Start();
        try { open(); }
        finally { timer.Stop(); }
        if (error != null) throw error;
        if (!handled) throw new Exception("Native dialog was not exercised");
    }

    [STAThread]
    private static int Main()
    {
        int exitCode = 0;
        var app = new Application();
        app.Resources = (ResourceDictionary)Application.LoadComponent(new Uri("/SwiftCopy;component/AppResources.xaml", UriKind.Relative));
        string root = Path.Combine(Path.GetTempPath(), "SwiftCopy-UiChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new SwiftCopy.App.MainWindow { ShowActivated = false };
        window.Loaded += async (_, _) =>
        {
            try
            {
                T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                void CheckLayout()
                {
                    window.UpdateLayout();
                    foreach (string name in new[] { "SourceCard", "DestinationCard", "WorkStateCard", "CopyButton", "MoveButton", "WorkStateTitle", "WorkStateDescription", "CurrentText", "TransferProgress", "PercentText", "WorkerLaneScroll" })
                    {
                        var element = Control<FrameworkElement>(name);
                        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                        if (!element.IsVisible || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Left < 0 || bounds.Top < 0 || bounds.Right > window.ActualWidth + 1 || bounds.Bottom > window.ActualHeight + 1)
                            throw new Exception("Status/action clipped: " + name);
                    }
                    var status = Control<Border>("WorkStateCard");
                    foreach (string name in new[] { "SpeedText", "FileRateText", "BytesText", "FilesText", "EtaText", "CurrentText", "DetailText", "WorkerLaneScroll" })
                    {
                        var element = Control<FrameworkElement>(name);
                        var bounds = element.TransformToAncestor(status).TransformBounds(new Rect(element.RenderSize));
                        if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > status.ActualWidth || bounds.Bottom > status.ActualHeight)
                            throw new Exception($"Status metric clipped: {name}, bounds={bounds}, card={status.RenderSize}");
                    }
                    var setup = Control<Grid>("SetupPanel");
                    if (status.TranslatePoint(new Point(), window).Y < setup.TranslatePoint(new Point(0, setup.ActualHeight), window).Y - 1
                        || status.TranslatePoint(new Point(0, status.ActualHeight), window).Y > Control<Button>("CopyButton").TranslatePoint(new Point(), window).Y)
                        throw new Exception("Setup/status/action sections overlap");
                }
                CheckLayout();
                SavePreview(window, "SwiftCopy-preview.png");
                double defaultWidth = window.Width, defaultHeight = window.Height;
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                SavePreview(window, "SwiftCopy-minimum.png");
                CheckLayout();
                window.Width = defaultWidth; window.Height = defaultHeight;
                window.UpdateLayout();
                Console.WriteLine("PASS Setup, prominent status and actions remain visible at default/minimum sizes");
                var workerSelector = Control<ComboBox>("WorkerCountBox");
                if (workerSelector.SelectedIndex != 2 || ((ComboBoxItem)workerSelector.SelectedItem).Content?.ToString() != "4")
                    throw new Exception("Default worker count is not 4");
                string[] workerChoices = workerSelector.Items.Cast<ComboBoxItem>().Select(item => item.Content?.ToString() ?? "").ToArray();
                if (!workerChoices.SequenceEqual(["1", "2", "4", "8", "16"]))
                    throw new Exception("Worker count choices are incomplete: " + string.Join(",", workerChoices));
                if (string.IsNullOrWhiteSpace(workerSelector.ToolTip?.ToString()) && string.IsNullOrWhiteSpace(((FrameworkElement)workerSelector.Parent).ToolTip?.ToString()))
                    throw new Exception("Worker selector needs the storage-performance guidance tooltip");

                var laneSnapshots = new TransferWorkerSnapshot[]
                {
                    new(0, @"C:\fixtures\alpha.bin", @"D:\copy\alpha.bin", 100, 40, 3, TransferWorkerState.Copying),
                    new(1, @"C:\fixtures\beta.bin", @"D:\copy\beta.bin", 200, 200, 2, TransferWorkerState.Verifying),
                    new(2, @"C:\fixtures\failed.bin", @"D:\copy\failed.bin", 50, 12, 0, TransferWorkerState.Failed),
                    new(3, "", "", 0, 0, 0, TransferWorkerState.Waiting)
                };
                var updateLanes = typeof(SwiftCopy.App.MainWindow).GetMethod("UpdateWorkerLanes", BindingFlags.Instance | BindingFlags.NonPublic)!;
                updateLanes.Invoke(window, [laneSnapshots, false]);
                var lanes = Control<ItemsControl>("WorkerLaneList").Items.Cast<SwiftCopy.App.WorkerLaneViewModel>().ToArray();
                if (lanes.Length != 4 || lanes[0].FileName != "alpha.bin" || lanes[0].Stage != "복사 중" || Math.Abs(lanes[0].Progress - 40) > 0.01
                    || lanes[0].CountText != "완료 3개" || lanes[1].Stage != "검증 중" || !lanes[1].IsIndeterminate || lanes[2].Stage != "오류")
                    throw new Exception("Parallel worker lane snapshot states are not represented accurately");
                window.UpdateLayout();
                var laneList = Control<ItemsControl>("WorkerLaneList");
                var fourthLane = (FrameworkElement)laneList.ItemContainerGenerator.ContainerFromIndex(3);
                var laneViewport = Control<ScrollViewer>("WorkerLaneScroll");
                var fourthBounds = fourthLane.TransformToAncestor(laneViewport).TransformBounds(new Rect(fourthLane.RenderSize));
                if (fourthBounds.Bottom > laneViewport.ActualHeight + 1)
                    throw new Exception($"Default four worker lanes do not fit without scrolling: fourth={fourthBounds}, viewport={laneViewport.RenderSize}");
                SavePreview(window, "SwiftCopy-workers.png");
                updateLanes.Invoke(window, [laneSnapshots, true]);
                if (!lanes[0].Stage.StartsWith("일시정지", StringComparison.Ordinal) || lanes[0].IsIndeterminate || lanes[1].ProgressText != "검증 일시정지")
                    throw new Exception("Paused worker lane state is not represented accurately");
                Console.WriteLine("PASS Worker selector defaults to 4 with bounded choices; simultaneous copy/verify/error/pause lane states render accurately");
                if (!window.FontFamily.Source.Contains("SwiftCopy Sans") || window.FontSize < 14) throw new Exception("Bundled readable font not configured");
                foreach (var weight in new[] { FontWeights.Medium, FontWeights.SemiBold })
                {
                    var face = new Typeface(window.FontFamily, FontStyles.Normal, weight, FontStretches.Normal);
                    if (!face.TryGetGlyphTypeface(out var glyph) || !glyph.FamilyNames.Values.Contains("SwiftCopy Sans") || glyph.Weight != weight
                        || glyph.StyleSimulations != StyleSimulations.None || !glyph.CharacterToGlyphMap.ContainsKey('한'))
                        throw new Exception("Bundled Korean font fallback or synthetic weight detected");
                }
                if (TextOptions.GetTextFormattingMode(window) != TextFormattingMode.Display || TextOptions.GetTextRenderingMode(window) != TextRenderingMode.ClearType)
                    throw new Exception("Pixel-aligned ClearType rendering missing");
                if (window.Icon == null || SendMessage(new WindowInteropHelper(window).Handle, 0x7F, (IntPtr)1, IntPtr.Zero) == IntPtr.Zero)
                    throw new Exception("Window/taskbar icon missing");
                var icon = new IconBitmapDecoder(new Uri("pack://application:,,,/SwiftCopy;component/Assets/SwiftCopy.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (!new[] { 16, 24, 32, 48, 256 }.All(size => icon.Frames.Any(frame => frame.PixelWidth == size)))
                    throw new Exception("Multi-resolution icon frames missing");
                Console.WriteLine("PASS Readable font, ClearType, native window icon and multi-resolution ICO");
                if (window.WindowStyle != WindowStyle.None) throw new Exception("Native title bar still enabled");
                int Hit(Point local)
                {
                    Point screen = window.PointToScreen(local);
                    int packed = ((int)screen.X & 0xffff) | (((int)screen.Y & 0xffff) << 16);
                    return (int)SendMessage(new WindowInteropHelper(window).Handle, 0x84, IntPtr.Zero, (IntPtr)packed);
                }
                if (Hit(new Point(300, 36)) != 2) throw new Exception("Header is not a native draggable caption");
                if (Hit(new Point(2, 150)) != 10) throw new Exception("Left resize border is not active");
                var closeCenter = Control<Button>("CloseButton").TranslatePoint(new Point(19, 17), window);
                if (Hit(closeCenter) != 1) throw new Exception("Caption button does not receive client clicks");
                Console.WriteLine("PASS Native title bar removed; header drag, resize border and caption-button hit testing");
                Control<Button>("MaximizeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                if (window.WindowState != WindowState.Maximized) throw new Exception("Maximize failed");
                if ((string)Control<Button>("MaximizeButton").ToolTip != "이전 크기로 복원") throw new Exception("Restore tooltip missing");
                Control<Button>("MaximizeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                if (window.WindowState != WindowState.Normal) throw new Exception("Restore failed");
                Control<Button>("MinimizeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                if (window.WindowState != WindowState.Minimized) throw new Exception("Minimize failed");
                SystemCommands.RestoreWindow(window);
                await Task.Delay(100);
                Console.WriteLine("PASS Custom maximize, restore and minimize buttons");
                string mixedFile = Path.Combine(root, "mixed.txt"); File.WriteAllText(mixedFile, "mixed picker");
                string mixedFolder = Path.Combine(root, "mixed folder"); Directory.CreateDirectory(mixedFolder);
                FillNativeDialog(() => Control<Button>("AddItemsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                    "파일·폴더 선택", $"\"{mixedFile}\" \"{mixedFolder}\"");
                if (Control<ListBox>("SourceList").Items.Count != 2) throw new Exception("Native mixed file/folder selection failed");
                Console.WriteLine("PASS Single source button accepts file and folder together through real Explorer dialog");
                string folderOnly = Path.Combine(root, "folder only"); Directory.CreateDirectory(folderOnly);
                FillNativeDialog(() => Control<Button>("AddItemsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), "파일·폴더 선택", folderOnly);
                if (Control<ListBox>("SourceList").Items.Count != 3) throw new Exception("Folder-only selection navigated instead of adding");
                FillNativeDialog(() => Control<Button>("AddItemsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), "파일·폴더 선택", "", cancel: true);
                if (Control<ListBox>("SourceList").Items.Count != 3) throw new Exception("Cancel changed source selection");
                string browsePath = Path.Combine(root, "browse destination"); Directory.CreateDirectory(browsePath);
                FillNativeDialog(() => Control<Button>("BrowseDestinationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), "대상 폴더 선택", browsePath);
                if (Control<TextBox>("DestinationBox").Text != browsePath) throw new Exception("Destination browse did not set editable path");
                Console.WriteLine("PASS Folder-only selection, picker cancellation, and native destination folder browsing");
                Control<ListBox>("SourceList").SelectAll();
                typeof(SwiftCopy.App.MainWindow).GetMethod("Remove_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
                string file = Path.Combine(root, "UI 테스트.txt");
                File.WriteAllText(file, "UI button transfer fixture");
                // Seed the same source collection used by the OS picker and drag/drop.
                typeof(SwiftCopy.App.MainWindow).GetMethod("AddSources", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new[] { file }]);
                var destination = Control<TextBox>("DestinationBox");
                destination.Text = "\"" + Path.Combine(root, "copy") + "\"";
                async Task Click(string name)
                {
                    bool sawValidation = false, sawRunning = false, sawWorkerSelectorDisabled = false;
                    var property = DependencyPropertyDescriptor.FromProperty(Window.TitleProperty, typeof(Window));
                    EventHandler changed = (_, _) =>
                    {
                        string status = Control<TextBlock>("StatusBadge").Text;
                        if (status == "●  확인 중") sawValidation = Control<ProgressBar>("TransferProgress").IsIndeterminate
                            && window.TaskbarItemInfo.ProgressState == TaskbarItemProgressState.Indeterminate;
                        if (status is "●  확인 중" or "●  탐색 + 전송 중" or "●  전송 중")
                            sawWorkerSelectorDisabled |= !Control<ComboBox>("WorkerCountBox").IsEnabled;
                        if (status == "●  탐색 + 전송 중")
                        {
                            sawRunning = Control<TextBlock>("WorkStateTitle").Text.Contains("하고 있습니다");
                            if (name == "CopyButton") SavePreview(window, "SwiftCopy-running.png");
                        }
                    };
                    property.AddValueChanged(window, changed);
                    try
                    {
                        Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var deadline = DateTime.UtcNow.AddSeconds(20);
                        while (!Control<Button>("CopyButton").IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(30);
                        if (!Control<Button>("CopyButton").IsEnabled) throw new Exception("UI transfer timeout");
                        if (Control<TextBlock>("StatusBadge").Text != "●  완료") throw new Exception("UI status: " + Control<TextBlock>("StatusBadge").Text);
                        if (!sawValidation || !sawRunning || !sawWorkerSelectorDisabled || !Control<ComboBox>("WorkerCountBox").IsEnabled
                            || !Control<TextBlock>("WorkStateTitle").Text.Contains("완료")
                            || !Control<TextBlock>("WorkStateDescription").Text.Contains("파일 1개")
                            || Control<ProgressBar>("TransferProgress").IsIndeterminate
                            || window.TaskbarItemInfo.ProgressState != TaskbarItemProgressState.None)
                            throw new Exception("Immediate feedback, active state or completion summary missing");
                        CheckLayout();
                        if (name == "CopyButton") SavePreview(window, "SwiftCopy-complete.png");
                    }
                    finally { property.RemoveValueChanged(window, changed); }
                }
                await Click("CopyButton");
                if (!File.Exists(file) || File.ReadAllText(Path.Combine(root, "copy", Path.GetFileName(file))) != File.ReadAllText(file))
                    throw new Exception("Copy button failed source/output check");
                Console.WriteLine("PASS Copy button starts immediately and preserves source; output content matches");
                destination.Text = Path.Combine(root, "move");
                await Click("MoveButton");
                if (File.Exists(file) || File.ReadAllText(Path.Combine(destination.Text, Path.GetFileName(file))) != "UI button transfer fixture")
                    throw new Exception("Move button failed source/output check");
                Console.WriteLine("PASS Move button starts without confirmation; source relocated and output content matches");
                if (!Control<Button>("LogButton").IsEnabled) throw new Exception("Log button disabled after completion");
                Console.WriteLine("PASS Action buttons restored and log available after completion");
                Console.WriteLine("PASS Immediate validation, visible running status, taskbar progress and completion summary");

                var preview = new SwiftCopy.App.MainWindow { ShowActivated = false };
                preview.Show(); preview.UpdateLayout();
                SavePreview(preview, "SwiftCopy-preview.png");
                ((Button)preview.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (preview.IsVisible) throw new Exception("Close button failed");
                Console.WriteLine("PASS Custom close button");
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); exitCode = 1; }
            finally { window.Close(); app.Shutdown(); }
        };
        app.Run(window);
        return exitCode;
    }

    private static void SavePreview(Window window, string fileName)
    {
        window.UpdateLayout();
        var visual = (Grid)window.Content;
        var render = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, new Rect(visual.RenderSize));
        render.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(render));
        string screenshot = Path.Combine(AppContext.BaseDirectory, fileName);
        using (var output = File.Create(screenshot)) png.Save(output);
        Console.WriteLine("Preview: " + screenshot);
    }
}
