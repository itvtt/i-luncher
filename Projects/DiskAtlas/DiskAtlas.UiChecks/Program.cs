using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Data;
using System.Text.Json;
using DiskAtlas.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var fixture = Path.Combine(Path.GetTempPath(), "DiskAtlas-UiChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        void Create(string relative, int size)
        {
            var path = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            file.SetLength(size);
        }
        Create("Downloads/release-backup.zip", 40 * 1024 * 1024);
        Create("Media/project-preview.mp4", 24 * 1024 * 1024);
        Create("Media/design-assets.psd", 12 * 1024 * 1024);
        Create("Projects/build/output.dll", 5 * 1024 * 1024);
        Create("Projects/data/export.csv", 1024 * 1024);
        Directory.CreateDirectory(Path.Combine(fixture, "Duplicates", "First"));
        Directory.CreateDirectory(Path.Combine(fixture, "Duplicates", "Second"));
        File.WriteAllText(Path.Combine(fixture, "Duplicates", "First", "same-a.txt"), "DiskAtlas duplicate fixture A");
        File.WriteAllText(Path.Combine(fixture, "Duplicates", "Second", "same-a-copy.txt"), "DiskAtlas duplicate fixture A");
        File.WriteAllText(Path.Combine(fixture, "Duplicates", "First", "same-b.txt"), "DiskAtlas duplicate fixture B with another size");
        File.WriteAllText(Path.Combine(fixture, "Duplicates", "Second", "same-b-copy.txt"), "DiskAtlas duplicate fixture B with another size");
        var alternateDuplicateFixture = fixture + "-alternate";
        Directory.CreateDirectory(alternateDuplicateFixture);
        File.WriteAllText(Path.Combine(alternateDuplicateFixture, "only-file.txt"), "A different selected root");
        File.WriteAllText(Path.Combine(fixture, "Projects", "design-notes.md"), "# 검증 문서\n내용검색 고유마커 AtlasNeedle42\n검색되는 사용자 문서입니다.");
        var system = Path.Combine(fixture, "system-note.txt");
        File.WriteAllText(system, "AtlasNeedle42");
        File.SetAttributes(system, FileAttributes.System);
        var recycleFixture = fixture + "-recycle";
        var recycler = new FixtureRecycler(recycleFixture);
        var confirmationCount = 0;
        var window = new DiskAtlas.App.MainWindow(recycler, (_, _) => { confirmationCount++; return true; }) { ShowActivated = false, ShowInTaskbar = false };
        int checks = 0;
        int exitCode = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine($"PASS {++checks}: {message}");
        }
        void Capture(string name)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.Combine(sourceRoot, "artifacts"));
            using var output = File.Create(Path.Combine(sourceRoot, "artifacts", name));
            encoder.Save(output);
        }
        window.Loaded += async (_, _) =>
        {
            try
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.WindowStyle == WindowStyle.None && window.Icon != null, "Custom chrome and dedicated icon initialized");
                Check(window.CurrentScan == null, "Startup does not scan disks without request");
                Check(((Button)window.FindName("FindDuplicatesButton")).IsEnabled,
                    "Duplicate finding is available before any scan has been loaded");
                var directWindow = new DiskAtlas.App.MainWindow(new FixtureRecycler(fixture + "-direct-recycle"), (_, _) => true)
                    { ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    ((TextBox)directWindow.FindName("PathBox")).Text = fixture;
                    await directWindow.FindDuplicatesAsync();
                    Check(directWindow.CurrentScan?.Root.FullPath.TrimEnd('\\') == fixture.TrimEnd('\\')
                        && directWindow.DuplicateRows.Count == 4
                        && directWindow.DuplicateRows.Select(row => row.GroupId).Distinct().Count() == 2,
                        "Direct duplicate finding scans a fresh selected fixture and returns its two duplicate groups");
                    ((TextBox)directWindow.FindName("PathBox")).Text = alternateDuplicateFixture;
                    await directWindow.FindDuplicatesAsync();
                    var alternateScan = directWindow.CurrentScan;
                    Check(alternateScan?.Root.FullPath.TrimEnd('\\') == alternateDuplicateFixture.TrimEnd('\\')
                        && directWindow.DuplicateRows.Count == 0,
                        "Direct duplicate finding rescans when the selected root changes");
                    ((TextBox)directWindow.FindName("PathBox")).Text = fixture;
                    var cancelledDuplicatePipeline = directWindow.FindDuplicatesAsync();
                    var cancelDuplicatesButton = (Button)directWindow.FindName("CancelDuplicatesButton");
                    Check(cancelDuplicatesButton.IsEnabled,
                        "Direct duplicate pipeline exposes cancellation during its initial scan");
                    cancelDuplicatesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await cancelledDuplicatePipeline;
                    Check(ReferenceEquals(alternateScan, directWindow.CurrentScan)
                        && directWindow.DuplicateRows.Count == 0
                        && ((TextBlock)directWindow.FindName("DuplicateStatusText")).Text.Contains("취소")
                        && !cancelDuplicatesButton.IsEnabled
                        && ((Button)directWindow.FindName("FindDuplicatesButton")).IsEnabled,
                        "Cancelling the initial scan skips duplicate analysis, preserves the old scan, and restores Find");
                    var priorDuplicateSummary = ((TextBlock)directWindow.FindName("DuplicateSummaryText")).Text;
                    ((TextBox)directWindow.FindName("PathBox")).Text = Path.Combine(fixture, "missing-selected-root");
                    await directWindow.FindDuplicatesAsync();
                    Check(ReferenceEquals(alternateScan, directWindow.CurrentScan)
                        && ((TextBlock)directWindow.FindName("DuplicateStatusText")).Text.Contains("선택한 폴더가 없거나 접근할 수 없습니다")
                        && ((TextBlock)directWindow.FindName("DuplicateSummaryText")).Text == priorDuplicateSummary,
                        "An invalid selected root reports failure and leaves the previous scan and duplicate results untouched");
                }
                finally { directWindow.Close(); }
                var pathInput = (TextBox)window.FindName("PathBox");
                window.UpdateLayout();
                var pathHost = (FrameworkElement)pathInput.Template.FindName("PART_ContentHost", pathInput);
                Check(pathHost.ActualHeight >= pathInput.FontSize + pathInput.Padding.Top + pathInput.Padding.Bottom,
                    "Path input has enough vertical space to show text without clipping");
                Capture("DiskAtlas-startup.png");
                var searchInput = (TextBox)window.FindName("SearchBox");
                Check(searchInput.IsVisible && ((Button)window.FindName("SearchButton")).IsVisible,
                    "Search input and action are visible on startup outside result tabs");
                window.UpdateLayout();
                var toolbar = (Grid)window.FindName("SearchToolbar");
                var toolbarControls = new FrameworkElement[]
                {
                    (FrameworkElement)window.FindName("PathBox"),
                    (FrameworkElement)window.FindName("DriveBox"),
                    (FrameworkElement)window.FindName("ScanButton"),
                    searchInput,
                    (FrameworkElement)window.FindName("SearchButton")
                };
                var toolbarY = toolbarControls.Select(control => control.TranslatePoint(new Point(), window).Y).ToArray();
                Check(toolbarY.Max() - toolbarY.Min() <= 2
                    && Math.Abs(toolbar.Height - 36) <= 0.5
                    && Math.Abs(toolbar.ActualHeight - 36) <= 0.5,
                    "Path and search controls share one aligned 36-pixel toolbar row");
                var optionsToggle = (ToggleButton)window.FindName("OptionsToggle");
                var optionsPopup = (Popup)window.FindName("OptionsPopup");
                optionsToggle.IsChecked = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(optionsPopup.IsOpen
                    && ((FrameworkElement)window.FindName("SearchModeBox")).IsVisible
                    && ((FrameworkElement)window.FindName("MatchCaseBox")).IsVisible
                    && ((FrameworkElement)window.FindName("MetricBox")).IsVisible,
                    "Search options popup opens with mode, case, and metric controls accessible");
                optionsToggle.IsChecked = false;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(!optionsPopup.IsOpen, "Search options popup closes from its toggle");
                optionsToggle.IsChecked = true;
                window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Check(optionsToggle.IsChecked == false, "Escape dismisses options without changing the current scope");
                ((TextBox)window.FindName("PathBox")).Text = fixture;
                await window.SearchAsync("AtlasNeedle42", SearchMode.Contents);
                Check(window.SearchResults.Count == 1, "First search prepares selected folder and excludes system files without a prior scan");
                ((TabControl)window.FindName("MainTabs")).SelectedIndex = 0;
                window.UpdateLayout();
                Check(searchInput.IsVisible, "Search remains visible while browsing folders");
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.UpdateLayout();
                var searchAction = (Button)window.FindName("SearchButton");
                var actionPosition = searchAction.TranslatePoint(new Point(), window);
                Check(searchInput.ActualWidth > 220 && actionPosition.X + searchAction.ActualWidth < window.ActualWidth,
                    "Search input and action fit the minimum window size");
                window.Width = 1280;
                window.Height = 850;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.CurrentScan?.Files.Count == 11, "Scan button flow publishes all fixture files including duplicate fixtures");
                Check(((FrameworkElement)window.FindName("MessageBar")).Visibility == Visibility.Collapsed
                    && window.FindName("WarningsButton") == null,
                    "Completed scans do not display skipped-item advisories or an analysis-details button");
                long total = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
                Check(window.CurrentScan?.Root.Size == total, "UI scan totals include system-file space");
                Check(((DataGrid)window.FindName("FolderGrid")).Items.Count > 0, "Folder usage rows rendered");
                var map = (DiskAtlas.App.TreemapControl)window.FindName("Treemap");
                window.UpdateLayout();
                Check(map.VisibleCount > window.CurrentScan!.Root.Children.Count && map.VisibleCount <= 1500,
                    "Nested treemap includes descendant files within render budget");
                var metricBox = (ComboBox)window.FindName("MetricBox");
                var originalScan = window.CurrentScan;
                metricBox.SelectedIndex = 1;
                for (int attempt = 0; attempt < 100 && !metricBox.IsEnabled; attempt++) await Task.Delay(10);
                Check(map.UseAllocatedSize && ReferenceEquals(originalScan, window.CurrentScan), "Allocated metric updates map without rescanning");
                metricBox.SelectedIndex = 0;
                for (int attempt = 0; attempt < 100 && !metricBox.IsEnabled; attempt++) await Task.Delay(10);
                Check(!map.UseAllocatedSize, "Logical metric restores file-size view");
                CheckTreemapProportions();
                Check(true, "Treemap preserves omitted area and excludes zero-allocation sparse files in allocated view");
                Capture("DiskAtlas-preview.png");
                ((TextBox)window.FindName("SearchBox")).Text = "AtlasNeedle42";
                ((ComboBox)window.FindName("SearchModeBox")).SelectedIndex = 1;
                await window.SearchAsync("AtlasNeedle42", SearchMode.Contents);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var results = (DataGrid)window.FindName("SearchResultsGrid");
                Check(results.Items.Count == 1, "Content search shows user document and excludes system file");
                Capture("DiskAtlas-search.png");
                await window.SearchAsync("검증 AND AtlasNeedle42", SearchMode.Contents);
                Check(results.Items.Count == 1, "AND search finds both terms within the document");
                await window.SearchAsync("design-notes AND AtlasNeedle42", SearchMode.NameOrContents);
                Check(results.Items.Count == 1, "Combined name/content search supports AND across both fields");
                await window.SearchAsync("absent-name OR export.csv", SearchMode.FileName);
                Check(results.Items.Count == 1, "OR filename search finds either alternative");
                var beforeInvalidQuery = window.CurrentScan;
                ((TextBox)window.FindName("PathBox")).Text = Path.Combine(fixture, "Media");
                await window.SearchAsync("코드 AND", SearchMode.Contents);
                Check(ReferenceEquals(beforeInvalidQuery, window.CurrentScan), "Malformed expression is rejected before scanning a new path");
                ((TextBox)window.FindName("PathBox")).Text = fixture;
                var extensionGrid = (DataGrid)window.FindName("ExtensionGrid");
                extensionGrid.SelectedItem = window.Extensions.First(row => row.Extension == ".zip");
                Check(map.HighlightedExtension == ".zip", "Extension legend highlights matching treemap tiles");
                ((Button)window.FindName("ClearTypeHighlightButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(map.HighlightedExtension is null, "Treemap extension highlight clears");
                var commonExtensions = new[] { ".zip", ".mp4", ".psd", ".dll", ".csv", ".md", ".txt" };
                Check(commonExtensions.Select(DiskAtlas.App.FileTypeColors.GetColor).Distinct().Count() == commonExtensions.Length
                    && DiskAtlas.App.FileTypeColors.GetColor(".ZIP") == DiskAtlas.App.FileTypeColors.GetColor(".zip"),
                    "Common file types have distinct stable case-insensitive map colors");
                await window.SearchAsync("no-matching-query-839171", SearchMode.NameOrContents);
                Check(results.Items.Count == 0, "No-match search clears stale results");
                ((TextBox)window.FindName("PathBox")).Text = Path.Combine(fixture, "Media");
                await window.SearchAsync("design-assets", SearchMode.FileName);
                Check(window.SearchResults.Count == 1 && window.CurrentScan!.Root.FullPath.TrimEnd('\\') == Path.Combine(fixture, "Media"),
                    "Changing the path searches the newly selected scope rather than stale scan results");
                await window.ScanPathAsync(fixture);

                var filesGrid = (DataGrid)window.FindName("LargeFilesGrid");
                var sizeColumn = filesGrid.Columns.First(column => column.SortMemberPath == "Node.Size");
                typeof(DataGrid).GetMethod("OnSorting", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(filesGrid, [new DataGridSortingEventArgs(sizeColumn)]);
                var sortedSizes = filesGrid.Items.Cast<DiskAtlas.App.FileRow>().Select(row => row.Node.Size).ToArray();
                Check(sortedSizes.SequenceEqual(sortedSizes.Order()), "Column sorting uses numeric bytes rather than formatted text");
                CollectionViewSource.GetDefaultView(filesGrid.ItemsSource).SortDescriptions.Clear();

                var duplicateTab = (TabItem)window.FindName("DuplicatesTab");
                Check(duplicateTab.Header?.ToString() == "중복 찾기"
                    && window.FindName("FindDuplicatesButton") is Button
                    && window.FindName("DeleteDuplicatesButton") is Button
                    && window.FindName("ClearDuplicateSelectionButton") is Button,
                    "Duplicate tab exposes find, clear-selection, and Recycle Bin actions");
                var duplicateFind = window.FindDuplicatesAsync();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                var duplicateProgress = (ProgressBar)window.FindName("DuplicateProgress");
                Check(duplicateFind.IsCompleted || (duplicateProgress.Visibility == Visibility.Visible
                    && ((Button)window.FindName("CancelDuplicatesButton")).IsEnabled),
                    "Duplicate scan exposes cancellable progress while hashing");
                await duplicateFind;
                window.UpdateLayout();
                var duplicateGrid = (DataGrid)window.FindName("DuplicateGrid");
                Check(window.DuplicateRows.Count == 4 && window.DuplicateRows.Select(row => row.GroupId).Distinct().Count() == 2,
                    "Duplicate scan renders fixture files in two content groups");
                Check(window.DuplicateRows.All(row => !row.IsSelected)
                    && ((TextBlock)window.FindName("DuplicateSummaryText")).Text.Contains("그룹 2개")
                    && ((TextBlock)window.FindName("DuplicateSummaryText")).Text.Contains("파일 4개")
                    && ((TextBlock)window.FindName("DuplicateSummaryText")).Text.Contains("확보 가능"),
                    "Duplicate results show group, file, and reclaimable totals with no destructive default selection");
                Check(VirtualizingPanel.GetIsVirtualizing(duplicateGrid)
                    && VirtualizingPanel.GetVirtualizationMode(duplicateGrid) == VirtualizationMode.Recycling
                    && duplicateGrid.EnableRowVirtualization,
                    "Duplicate rows use recycling virtualization for large result sets");
                duplicateGrid.SelectedItem = window.DuplicateRows[0];
                duplicateGrid.ScrollIntoView(window.DuplicateRows[0]);
                window.UpdateLayout();
                var duplicateCheckBox = FindVisualChild<CheckBox>(duplicateGrid);
                Check(duplicateCheckBox is { IsEnabled: true }, "Read-only duplicate grid keeps row selection checkboxes interactive");
                var toggleProvider = (IToggleProvider)new CheckBoxAutomationPeer(duplicateCheckBox!).GetPattern(PatternInterface.Toggle)!;
                toggleProvider.Toggle();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Check(window.DuplicateRows.Count(row => row.IsSelected) == 1
                    && ((TextBlock)window.FindName("DuplicateSelectionText")).Text.Contains("선택 1개")
                    && ((Button)window.FindName("DeleteDuplicatesButton")).IsEnabled
                    && ((Button)window.FindName("OpenDuplicateFolderButton")).IsEnabled,
                    "Duplicate checkbox updates selected count and enables explicit file actions");
                ((Button)window.FindName("ClearDuplicateSelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.DuplicateRows.All(row => !row.IsSelected), "Duplicate selection can be cleared without changing results");
                foreach (var row in window.DuplicateRows) row.IsSelected = true;
                await window.DeleteSelectedDuplicatesAsync();
                Check(confirmationCount == 0
                    && ((TextBlock)window.FindName("DuplicateStatusText")).Text.Contains("모든 파일이 선택"),
                    "Selecting every file in a duplicate group is blocked before confirmation or deletion");
                foreach (var row in window.DuplicateRows) row.IsSelected = false;
                window.DuplicateRows[0].IsSelected = true;
                var selectionStatus = ((TextBlock)window.FindName("DuplicateStatusText")).Text;
                var selectionDeleteEnabled = ((Button)window.FindName("DeleteDuplicatesButton")).IsEnabled;
                var selectionWarningFlag = (bool)typeof(DiskAtlas.App.MainWindow)
                    .GetField("_duplicateSelectionWarning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var selectedDuplicateCount = window.DuplicateRows.Count(row => row.IsSelected);
                Check(!selectionStatus.Contains("모든 파일이 선택") && selectionDeleteEnabled,
                    $"Reducing an unsafe all-file selection clears its validation warning (status='{selectionStatus}', selected={selectedDuplicateCount}, warningFlag={selectionWarningFlag}, deleteEnabled={selectionDeleteEnabled})");
                Capture("DiskAtlas-duplicates.png");
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.UpdateLayout();
                var duplicateDelete = (FrameworkElement)window.FindName("DeleteDuplicatesButton");
                var duplicateDeletePosition = duplicateDelete.TranslatePoint(new Point(), window);
                Check(FindVisualChildren<DataGridRow>(duplicateGrid).Count >= 4
                    && ((FrameworkElement)window.FindName("DiskMapPanel")).Visibility == Visibility.Collapsed
                    && ((FrameworkElement)window.FindName("ExtensionPanel")).Visibility == Visibility.Collapsed
                    && duplicateDeletePosition.X + duplicateDelete.ActualWidth <= window.ActualWidth,
                    "Duplicate tab shows at least four fixture rows and all actions at the minimum window size");
                Capture("DiskAtlas-duplicates-minimum.png");
                window.Width = 1280;
                window.Height = 850;
                var filesBeforeRecycle = window.CurrentScan!.Files.Count;
                var deletionTargets = window.DuplicateRows.GroupBy(row => row.GroupId).Select(group => group.First()).ToArray();
                deletionTargets[0].IsSelected = true;
                deletionTargets[1].IsSelected = true;
                recycler.FailPath = deletionTargets[1].FullPath;
                await window.DeleteSelectedDuplicatesAsync();
                Check(confirmationCount == 1 && recycler.RecycledPaths.Count == 1
                    && !File.Exists(recycler.RecycledPaths[0])
                    && window.CurrentScan!.Files.Count == filesBeforeRecycle - 1,
                    "Confirmed duplicate deletion uses the fake recycler and refreshes totals after partial success");
                Check(window.DuplicateRows.Count == 0
                    && ((TextBlock)window.FindName("DuplicateStatusText")).Text.Contains("휴지통으로 이동 1개")
                    && ((TextBlock)window.FindName("DuplicateStatusText")).Text.Contains("실패 1개")
                    && window.DuplicateFailures.Count == 1
                    && window.DuplicateFailures[0].Contains(deletionTargets[1].FullPath)
                    && ((ToggleButton)window.FindName("DuplicateFailuresToggle")).Visibility == Visibility.Visible,
                    "Partial deletion clears stale results and preserves a readable per-file failure list");
                ((TabControl)window.FindName("MainTabs")).SelectedIndex = 0;

                var commandArgs = Environment.GetCommandLineArgs();
                int largeIndex = Array.IndexOf(commandArgs, "--large-fixture");
                if (largeIndex >= 0 && largeIndex + 1 < commandArgs.Length)
                {
                    var prior = window.CurrentScan;
                    var priorSize = window.LogicalSizeText;
                    var interrupted = window.ScanPathAsync(commandArgs[largeIndex + 1]);
                    ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await interrupted;
                    Check(ReferenceEquals(window.CurrentScan, prior) && window.LogicalSizeText == priorSize,
                        "Cancelled replacement scan preserves previous result and size metrics");
                    var watch = Stopwatch.StartNew();
                    long lastTick = 0, maxTickGap = 0, ticks = 0;
                    var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                    heartbeat.Tick += (_, _) => { var now = watch.ElapsedMilliseconds; maxTickGap = Math.Max(maxTickGap, now - lastTick); lastTick = now; ticks++; };
                    heartbeat.Start();
                    await window.ScanPathAsync(commandArgs[largeIndex + 1]);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    heartbeat.Stop();
                    Check(window.CurrentScan!.Files.Count == 100000 && window.LargeFiles.Count == 10000,
                        "100k-file result integrates with bounded large-file grid");
                    Check(ticks > 0 && map.VisibleCount <= 1500, "Large scan keeps dispatcher active and treemap bounded");
                    var report = JsonSerializer.Serialize(new { files = 100000, milliseconds = watch.Elapsed.TotalMilliseconds,
                        dispatcherTicks = ticks, largestDispatcherGapMilliseconds = maxTickGap }, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(Path.Combine(sourceRoot, "artifacts", "ui-scale-check.json"), report);
                    Console.WriteLine(report);
                }
                ((Button)window.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Check(window.WindowState == WindowState.Maximized, "Custom maximize button works");
                window.UpdateLayout();
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                Check(GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(window).Handle, 2), ref monitor), "Current monitor work area is available");
                var footer = (FrameworkElement)window.FindName("StatusText");
                var footerBottom = footer.PointToScreen(new Point(0, footer.ActualHeight));
                var mapBottom = map.PointToScreen(new Point(map.ActualWidth, map.ActualHeight));
                Check(footerBottom.Y <= monitor.Work.Bottom + 1 && mapBottom.Y <= monitor.Work.Bottom + 1
                    && footerBottom.Y > monitor.Work.Top && mapBottom.X <= monitor.Work.Right + 1,
                    "Maximized footer and disk map remain inside the monitor work area");
                Capture("DiskAtlas-maximized.png");
                ((Button)window.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Check(window.WindowState == WindowState.Normal, "Custom restore button works");
                var originalMinHeight = window.MinHeight;
                var oversizedMinimum = (monitor.Work.Bottom - monitor.Work.Top) / VisualTreeHelper.GetDpi(window).DpiScaleY + 120;
                window.MinHeight = oversizedMinimum;
                window.WindowState = WindowState.Maximized;
                await Task.Delay(100);
                window.UpdateLayout();
                Check(footer.PointToScreen(new Point(0, footer.ActualHeight)).Y <= monitor.Work.Bottom + 1,
                    "Maximized content fits even when the configured minimum exceeds the work area");
                window.WindowState = WindowState.Normal;
                await Task.Delay(100);
                Check(Math.Abs(window.MinHeight - oversizedMinimum) < 0.1, "Restore preserves the original minimum height");
                window.MinHeight = originalMinHeight;
                ((Button)window.FindName("MinimizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Check(window.WindowState == WindowState.Minimized, "Custom minimize button works");
                SystemCommands.RestoreWindow(window);
                ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Check(!window.IsVisible, "Custom close button works");
                Console.WriteLine($"DiskAtlas UI checks passed: {checks}. Fixture: {fixture}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                exitCode = 1;
            }
            finally { application.Shutdown(); }
        };
        window.Show();
        application.Run();
        return exitCode;
    }

    private static void CheckTreemapProportions()
    {
        var map = new DiskAtlas.App.TreemapControl();
        var root = new ScanNode { Name = "root", IsDirectory = true };
        for (int i = 0; i < 3000; i++) root.Children.Add(new ScanNode { Name = $"file-{i}.txt", Size = 1, AllocatedSize = 1, Parent = root });
        void Render(bool allocated)
        {
            map.SetRoot(root, allocated);
            map.Measure(new Size(1000, 600));
            map.Arrange(new Rect(0, 0, 1000, 600));
            map.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1000, 600, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(map);
        }
        object[] Tiles() => ((System.Collections.IEnumerable)typeof(DiskAtlas.App.TreemapControl)
            .GetField("_tiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(map)!).Cast<object>().ToArray();
        Render(false);
        var tiles = Tiles();
        double area = tiles.Sum(tile => { var bounds = (Rect)tile.GetType().GetProperty("Bounds")!.GetValue(tile)!; return bounds.Width * bounds.Height; });
        if (tiles.Length != 1500 || Math.Abs(area / 600000 - .5) > .00001)
            throw new Exception("Truncated map falsely inflates retained files to full area");
        root.Children.Clear();
        root.Children.Add(new ScanNode { Name = "sparse.dat", Size = 1000000000, AllocatedSize = 0 });
        root.Children.Add(new ScanNode { Name = "real.txt", Size = 1, AllocatedSize = 4096 });
        Render(true);
        tiles = Tiles();
        if (tiles.Length != 1 || ((ScanNode)tiles[0].GetType().GetProperty("Node")!.GetValue(tiles[0])!).Name != "real.txt")
            throw new Exception("Sparse logical size dominates allocated map");
        root.Children.Clear();
        string[] types = [".zip", ".mp4", ".psd", ".dll", ".csv", ".md", ".txt", ".png", ".mp3", ".cs", ".json", ".pdf"];
        for (int folderIndex = 0; folderIndex < 24; folderIndex++)
        {
            var folder = new ScanNode { Name = $"Folder-{folderIndex:00}", IsDirectory = true, Parent = root };
            for (int fileIndex = 0; fileIndex < 40; fileIndex++)
            {
                var size = 10 + (fileIndex * 7 % 31);
                folder.Children.Add(new ScanNode { Name = $"file-{fileIndex}{types[(folderIndex + fileIndex) % types.Length]}", Size = size, AllocatedSize = size, Parent = folder });
                folder.Size += size;
                folder.AllocatedSize += size;
            }
            root.Children.Add(folder);
        }
        Render(false);
        var fileTiles = Tiles().Select(tile => (ScanNode)tile.GetType().GetProperty("Node")!.GetValue(tile)!).Where(node => !node.IsDirectory).ToArray();
        if (fileTiles.Length < 400 || fileTiles.Select(node => node.Extension).Distinct().Count() != types.Length || map.VisibleCount > 1500)
            throw new Exception("Nested map fails to expose a bounded, varied set of file types");
        var denseBitmap = new RenderTargetBitmap(1000, 600, 96, 96, PixelFormats.Pbgra32);
        denseBitmap.Render(map);
        var denseEncoder = new PngBitmapEncoder();
        denseEncoder.Frames.Add(BitmapFrame.Create(denseBitmap));
        var artifactDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts"));
        Directory.CreateDirectory(artifactDirectory);
        using var denseOutput = File.Create(Path.Combine(artifactDirectory, "DiskAtlas-dense-map.png"));
        denseEncoder.Save(denseOutput);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private static List<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var matches = new List<T>();
        void Visit(DependencyObject current)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                var child = VisualTreeHelper.GetChild(current, index);
                if (child is T match) matches.Add(match);
                Visit(child);
            }
        }
        Visit(parent);
        return matches;
    }

    private sealed class FixtureRecycler(string recycleDirectory) : IFileRecycler
    {
        public List<string> RecycledPaths { get; } = [];
        public string? FailPath { get; set; }

        public Task RecycleAsync(string path, Action validateImmediatelyBeforeRecycle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            validateImmediatelyBeforeRecycle();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(path, FailPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Disposable fixture recycler failure");
            Directory.CreateDirectory(recycleDirectory);
            var destination = Path.Combine(recycleDirectory, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path));
            File.Move(path, destination);
            RecycledPaths.Add(path);
            return Task.CompletedTask;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
