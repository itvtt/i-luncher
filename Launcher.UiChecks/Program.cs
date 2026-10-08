using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ILauncher;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static int Main(string[] args)
    {
        bool launchApps = args.Contains("--launch-apps", StringComparer.OrdinalIgnoreCase);
        string sourceRoot = new ILauncher.Services.LauncherCatalog().RootDirectory;
        string testState = Path.Combine(Path.GetTempPath(), "Launcher-UiChecks-" + Guid.NewGuid().ToString("N"));
        string? previousDeskCalDataDirectory = Environment.GetEnvironmentVariable("DESKCAL_DATA_DIR");
        Directory.CreateDirectory(testState);
        Environment.SetEnvironmentVariable("DESKCAL_DATA_DIR", Path.Combine(testState, "DeskCal"));
        SetFolderCompareSettingsPath(Path.Combine(testState, "FolderCompare", "lastpaths.json"));
        IReadOnlyDictionary<string, Type> liveWindowTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["folder-compare"] = typeof(FolderCompareWPF.MainWindow),
            ["swift-copy"] = typeof(SwiftCopy.App.MainWindow),
            ["deskcal"] = typeof(DeskCal.MainWindow)
        };

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow()
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000
        };
        var launched = new Dictionary<string, Window>(StringComparer.OrdinalIgnoreCase);
        var timings = new List<LaunchTiming>();
        int checks = 0;
        int result = 0;

        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine($"PASS {++checks}: {message}");
        }

        window.Loaded += async (_, _) =>
        {
            string imageDirectory = Path.Combine(sourceRoot, "artifacts");
            try
            {
                var delayedCatalog = new DelayedLauncherCatalog(450);
                var responsivenessWindow = new MainWindow(delayedCatalog)
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000
                };
                responsivenessWindow.Show();
                await Task.Delay(100);
                Check(delayedCatalog.LoadStarted && !delayedCatalog.LoadCompleted,
                    "Window dispatcher stays responsive while catalog discovery is delayed");
                await WithTimeout(WaitForCatalog(responsivenessWindow, delayedCatalog), TimeSpan.FromSeconds(10),
                    "Delayed catalog load did not populate launcher cards.");
                responsivenessWindow.UpdateLayout();
                Button runOnlyButton = ActionButtons(responsivenessWindow, "Launch_").Single();
                Check(runOnlyButton.ActualWidth is >= 68 and <= 80
                      && ActionButtons(responsivenessWindow, "Install_").All(button =>
                          !button.IsVisible || !KeyboardNavigation.GetIsTabStop(button)),
                    "Run-only app keeps the compact right-side action and no install tab stop");
                responsivenessWindow.Close();

                string uninstallAppsRoot = Path.Combine(testState, "UninstallFixtureApps");
                string uninstallMenuRoot = Path.Combine(testState, "UninstallFixtureStartMenu");
                ILauncher.Models.LauncherEntry sourceDeskCal = new()
                {
                    Id = "deskcal", Name = "DeskCal", Description = "Isolated uninstall UI fixture",
                    Category = "Checks", IsIntegrated = true,
                    ExecutablePath = Path.Combine(AppContext.BaseDirectory, "i-luncher.exe"),
                    WorkingDirectory = AppContext.BaseDirectory
                };
                var uninstallCatalog = new ILauncher.Services.LauncherCatalog(sourceRoot, uninstallAppsRoot,
                    uninstallMenuRoot, new FixtureIntegratedAppProvider(sourceDeskCal));
                ILauncher.Models.LauncherEntry uninstallEntry = uninstallCatalog.Load().Entries
                    .Single(entry => entry.Id == "deskcal");
                await uninstallCatalog.InstallAsync(uninstallEntry);
                bool confirmRemoval = false;
                int confirmationCount = 0;
                var uninstallWindow = new MainWindow(uninstallCatalog, (_, prompt) =>
                {
                    confirmationCount++;
                    Check(prompt.Contains(uninstallEntry.Name, StringComparison.Ordinal)
                          && prompt.Contains("\uB2E4\uC6B4\uB85C\uB4DC\uD55C \uD30C\uC77C", StringComparison.Ordinal)
                          && prompt.Contains("\uC0AC\uC6A9\uC790 \uB370\uC774\uD130\uB294 \uC720\uC9C0", StringComparison.Ordinal),
                        "Uninstall confirmation names the app and explains retained data");
                    return confirmRemoval;
                }) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
                uninstallWindow.Show();
                await Idle(uninstallWindow);
                MainWindow.LauncherCard uninstallCard = uninstallWindow.LauncherCards.Single(card => card.Entry.Id == "deskcal");
                Button uninstallButton = ActionButtons(uninstallWindow, "Install_")
                    .Single(button => AutomationProperties.GetAutomationId(button) == "Install_deskcal");
                Check(uninstallCard.IsInstalled && uninstallCard.Entry.CanUninstall
                      && AutomationProperties.GetName(uninstallButton).EndsWith(" \uC81C\uAC70", StringComparison.Ordinal),
                    "Managed installed app exposes an accessible Remove icon");
                var removeBorder = (Border?)uninstallButton.Template.FindName("ButtonBorder", uninstallButton);
                Check(ReferenceEquals(uninstallButton.Style, uninstallWindow.FindResource("RegistrationActionButtonStyle"))
                      && removeBorder?.Background is SolidColorBrush removeBrush
                      && removeBrush.Color == Color.FromRgb(56, 35, 42),
                    "Managed Remove uses the red registration icon style and state triggers");
                uninstallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(confirmationCount == 1 && uninstallCard.IsInstalled,
                    "Declining uninstall confirmation leaves the managed installation intact");
                confirmRemoval = true;
                uninstallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WithTimeout(WaitForUninstall(uninstallCard), TimeSpan.FromSeconds(15),
                    "Isolated uninstall fixture did not finish removing its registration.");
                Check(!uninstallCard.IsInstalled
                      && AutomationProperties.GetName(uninstallButton).EndsWith(" \uC124\uCE58", StringComparison.Ordinal)
                      && Directory.Exists(Path.Combine(uninstallAppsRoot, "_shared-payloads")),
                    "Confirmed removal refreshes the action and retains the shared app payload");
                uninstallWindow.Close();
                await Idle(window);
                string[] expectedAppIds = ["folder-compare", "swift-copy", "disk-atlas", "deskcal"];
                string[] expectedSupportedAppIds = expectedAppIds
                    .Concat(window.LauncherCards.Any(card => card.Entry.Id == "terminalnotes")
                        ? new[] { "terminalnotes" } : Array.Empty<string>())
                    .ToArray();
                Check(window.LauncherCards.Where(card => card.Entry.IsIntegrated)
                        .Select(card => card.Entry.Id).SequenceEqual(expectedAppIds)
                      && expectedAppIds.All(id => window.LauncherCards.Any(card => card.Entry.Id == id)),
                    "All four integrated apps appear in the launcher");
                Check(expectedSupportedAppIds.All(id => window.LauncherCards.Any(card => card.Entry.Id == id)),
                    "TerminalNotes is included in launcher UI checks when configured");
                Check(window.LauncherCards.Where(card => card.Entry.IsIntegrated).All(card => card.IsAvailable),
                    "Integrated apps are ready without source projects or executables");
                Check(expectedSupportedAppIds.All(id => window.LauncherCards.Single(card => card.Entry.Id == id).CanInstall),
                    "Integrated apps and TerminalNotes support install before output is built");
                Check(expectedSupportedAppIds.All(id => window.LauncherCards.Single(card => card.Entry.Id == id).CanInstall),
                    "Supported app cards expose enabled install actions");
                Check(window.LauncherCards.Where(card => card.Entry.IsIntegrated).All(card => card.IconSource != null),
                    "Integrated app cards load their embedded icons");
                Check(application.Resources.Count == 0 && window.TryFindResource("WindowBrush") is SolidColorBrush,
                    "Launcher resources are scoped to MainWindow");
                Check(window.Content is Grid { Background: SolidColorBrush background }
                      && background.Color == Color.FromRgb(11, 13, 18),
                    "Dark launcher surface applied to MainWindow");

                var handle = new WindowInteropHelper(window).Handle;
                Check(window.Icon != null && SendMessage(handle, 0x7F, (IntPtr)1, IntPtr.Zero) != IntPtr.Zero,
                    "Custom window and taskbar icon installed");
                var icon = new IconBitmapDecoder(new Uri("pack://application:,,,/i-luncher;component/Assets/ILauncher.ico"),
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check(new[] { 16, 24, 32, 48, 256 }.All(size => icon.Frames.Any(frame => frame.PixelWidth == size)),
                    "Dedicated icon includes small and high DPI resolutions");
                Check(window.WindowStyle == WindowStyle.None, "Native title bar replaced");

                int Hit(Point local)
                {
                    Point screen = window.PointToScreen(local);
                    int packed = ((int)screen.X & 0xffff) | (((int)screen.Y & 0xffff) << 16);
                    return (int)SendMessage(handle, 0x84, IntPtr.Zero, (IntPtr)packed);
                }

                Check(Hit(new Point(window.ActualWidth / 2, 25)) == 2, "Header supports native window dragging");
                Check(Hit(new Point(2, 150)) == 10, "Window edge supports resizing");
                foreach (string name in new[] { "MinimizeButton", "MaximizeButton", "CloseButton" })
                {
                    var control = (Button)window.FindName(name);
                    Point center = control.TranslatePoint(new Point(control.ActualWidth / 2, control.ActualHeight / 2), window);
                    Check(Hit(center) == 1, $"{name} receives client clicks");
                }

                var maximize = (Button)window.FindName("MaximizeButton");
                maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(150);
                Check(window.WindowState == WindowState.Maximized && AutomationProperties.GetName(maximize).Contains("복원"),
                    "Custom maximize button maximizes and exposes restore action");
                maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(150);
                Check(window.WindowState == WindowState.Normal, "Custom restore button restores original size");
                ((Button)window.FindName("MinimizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(150);
                Check(window.WindowState == WindowState.Minimized, "Custom minimize button minimizes window");
                SystemCommands.RestoreWindow(window);
                await Task.Delay(150);
                SendMessage(handle, 0xA3, (IntPtr)2, IntPtr.Zero);
                await Task.Delay(150);
                Check(window.WindowState == WindowState.Maximized, "Native caption double-click maximizes window");
                SystemCommands.RestoreWindow(window);
                await Task.Delay(150);

                var search = (TextBox)window.FindName("SearchBox");
                search.Text = "swift";
                Check(window.LauncherCards.Count == 1 && window.LauncherCards[0].Entry.Id == "swift-copy",
                    "Search filters app name");
                search.Text = "없는 검색 결과 39481";
                Check(window.LauncherCards.Count == 0 && ((FrameworkElement)window.FindName("EmptyState")).Visibility == Visibility.Visible,
                    "No-match state displayed");
                search.Text = "파일 도구";
                Check(window.LauncherCards.Count == 2, "Korean category search finds both file apps");
                search.Text = "일정";
                Check(window.LauncherCards.Count == 1 && window.LauncherCards[0].Entry.Id == "deskcal",
                    "DeskCal is searchable by its Korean category");
                ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(search.Text == "일정" && window.LauncherCards.Count == 1, "Refresh preserves search");
                search.Clear();
                window.UpdateLayout();
                await Idle(window);

                var launchButtons = ActionButtons(window, "Launch_")
                    .Where(button => expectedSupportedAppIds.Any(id =>
                        AutomationProperties.GetAutomationId(button) == $"Launch_{id}"))
                    .ToList();
                var installButtons = ActionButtons(window, "Install_")
                    .Where(button => expectedSupportedAppIds.Any(id =>
                        AutomationProperties.GetAutomationId(button) == $"Install_{id}"))
                    .ToList();
                Check(launchButtons.Count == expectedSupportedAppIds.Length
                      && expectedSupportedAppIds.All(id => launchButtons.Any(button =>
                          AutomationProperties.GetAutomationId(button) == $"Launch_{id}")),
                    "Every supported app exposes a stable run action");
                Check(installButtons.Count == expectedSupportedAppIds.Length
                      && expectedSupportedAppIds.All(id => installButtons.Any(button =>
                          AutomationProperties.GetAutomationId(button) == $"Install_{id}"))
                      && installButtons.All(button => button.Visibility == Visibility.Visible && button.IsVisible
                          && button.IsEnabled && button.Focusable && KeyboardNavigation.GetIsTabStop(button)
                          && ExpectedInstalledAwareInstallLabel(button, window.LauncherCards)),
                    "Every supported app exposes an enabled, accessible install action");
                Check(launchButtons.All(button => Equals(button.Content, "실행")
                                                   && AutomationProperties.GetName(button).EndsWith(" 실행", StringComparison.Ordinal)
                                                   && Equals(button.ToolTip, "앱 실행")
                                                   && button.Focusable && KeyboardNavigation.GetIsTabStop(button)),
                    "Run actions keep concise accessible labels and tooltips");
                Check(!Descendants<TextBlock>(window).Any(text => text.Text.Equals("오른쪽 위 설치 아이콘으로 시작 메뉴에 등록할 수 있습니다.", StringComparison.Ordinal))
                      && window.LauncherCards.All(card => Descendants<TextBlock>(window).Any(text =>
                          ReferenceEquals(text.DataContext, card) && text.Text == card.Entry.Category)),
                    "Cards show app categories without persistent install guidance");
                Check(expectedSupportedAppIds.All(id =>
                {
                    Button install = installButtons.Single(button => AutomationProperties.GetAutomationId(button) == $"Install_{id}");
                    Button run = launchButtons.Single(button => AutomationProperties.GetAutomationId(button) == $"Launch_{id}");
                    Rect installBounds = BoundsInWindow(install, window);
                    Rect runBounds = BoundsInWindow(run, window);
                    MainWindow.LauncherCard card = window.LauncherCards.Single(candidate => candidate.Entry.Id == id);
                    TextBlock description = Descendants<TextBlock>(window).Single(text =>
                        ReferenceEquals(text.DataContext, card) && text.Text == card.Entry.Description);
                    Rect descriptionBounds = BoundsInWindow(description, window);
                    return installBounds.Top < runBounds.Top && install.ActualWidth <= 44
                        && runBounds.Left >= descriptionBounds.Right - 2
                        && run.ActualWidth is >= 68 and <= 80
                        && run.ActualHeight >= 32 && runBounds.Top < descriptionBounds.Bottom;
                }), "Install icons stay above the compact Run action beside each description");

                Button firstLaunch = launchButtons[0];
                window.ShowActivated = true;
                window.Activate();
                Check(firstLaunch.Focus() && ReferenceEquals(Keyboard.FocusedElement, firstLaunch),
                    "Keyboard focus reaches a run action without invoking it");
                await Idle(window);
                var focusBorder = (Border?)firstLaunch.Template.FindName("ButtonBorder", firstLaunch);
                Check(focusBorder?.BorderBrush is SolidColorBrush focusBrush
                      && focusBrush.Color == Color.FromRgb(124, 156, 255)
                      && focusBorder.BorderThickness.Left >= 2,
                    "Keyboard focus displays the accent focus state");

                foreach (MainWindow.LauncherCard card in window.LauncherCards)
                {
                    card.IsInstalling = false;
                    card.StatusMessage = "\uC2DC\uC791\uD558\uB294 \uC911\uC785\uB2C8\uB2E4.";
                    card.IsBusy = true;
                }
                await Idle(window);
                Check(window.LauncherCards.All(card => Descendants<TextBlock>(window).Any(text =>
                          ReferenceEquals(text.DataContext, card) && text.Text == card.StatusMessage
                          && AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite)),
                    "Busy progress remains visible through polite live status text");
                Check(launchButtons.All(button => !button.IsEnabled), "Busy state disables every run action");
                Check(installButtons.All(button => button.IsVisible && !button.IsEnabled
                    && !AutomationProperties.GetName(button).EndsWith(" \uC124\uCE58 \uC911\u2026", StringComparison.Ordinal)),
                    "Run activity disables install actions without showing an install-in-progress label");
                Check(launchButtons.All(button => Equals(button.Content, "\uC2E4\uD589 \uC911\u2026")),
                    "Run activity shows the run busy label");
                Check(!firstLaunch.Focus(), "Disabled run action rejects keyboard focus");
                foreach (MainWindow.LauncherCard card in window.LauncherCards)
                {
                    card.IsBusy = false;
                    card.StatusMessage = string.Empty;
                }
                await Idle(window);
                Check(launchButtons.All(button => button.IsEnabled), "Run actions recover after busy state");
                typeof(MainWindow).GetMethod("ShowError", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, new object?[] { "Fixture error status" });
                await Idle(window);
                Border errorPanel = (Border)window.FindName("ErrorPanel");
                TextBlock errorText = (TextBlock)window.FindName("ErrorText");
                Check(errorPanel.IsVisible && errorText.Text == "Fixture error status"
                      && AutomationProperties.GetLiveSetting(errorPanel) == AutomationLiveSetting.Assertive,
                    "Error status stays visible and assertive to assistive technology");
                ActionButtons(window, "").Single(button =>
                    AutomationProperties.GetAutomationId(button) == "DismissErrorButton")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!errorPanel.IsVisible && errorText.Text.Length == 0,
                    "Error status recovers after dismissal");

                MainWindow.LauncherCard installingCard = window.LauncherCards.Single(card => card.Entry.Id == expectedAppIds[0]);
                installingCard.IsInstalling = true;
                installingCard.IsBusy = true;
                await Idle(window);
                Button installingButton = installButtons.Single(button =>
                    AutomationProperties.GetAutomationId(button) == $"Install_{installingCard.Entry.Id}");
                Button installingRunButton = launchButtons.Single(button =>
                    AutomationProperties.GetAutomationId(button) == $"Launch_{installingCard.Entry.Id}");
                Check(!installingButton.IsEnabled && !installingRunButton.IsEnabled
                      && installButtons.Where(button => !ReferenceEquals(button, installingButton)).All(button => button.IsEnabled)
                      && AutomationProperties.GetName(installingButton).EndsWith(" \uC124\uCE58 \uC911\u2026", StringComparison.Ordinal)
                      && Equals(installingRunButton.Content, "\uC2E4\uD589"),
                    "Install activity disables both actions for that card and shows the install-specific label");
                installingCard.IsBusy = false;
                installingCard.IsInstalling = false;
                await Idle(window);
                Check(installButtons.All(button => button.IsEnabled
                    && ExpectedInstalledAwareInstallLabel(button, window.LauncherCards)),
                    "Install actions recover with their accessible labels after busy state");

                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.UpdateLayout();
                await Idle(window);
                launchButtons = ActionButtons(window, "Launch_")
                    .Where(button => expectedSupportedAppIds.Any(id =>
                        AutomationProperties.GetAutomationId(button) == $"Launch_{id}"))
                    .ToList();
                installButtons = ActionButtons(window, "Install_")
                    .Where(button => expectedSupportedAppIds.Any(id =>
                        AutomationProperties.GetAutomationId(button) == $"Install_{id}"))
                    .ToList();
                bool usable = expectedSupportedAppIds.All(id =>
                {
                    Button run = launchButtons.Single(button => AutomationProperties.GetAutomationId(button) == $"Launch_{id}");
                    Button install = installButtons.Single(button => AutomationProperties.GetAutomationId(button) == $"Install_{id}");
                    Rect runBounds = BoundsInWindow(run, window);
                    Rect installBounds = BoundsInWindow(install, window);
                    return run.ActualWidth is >= 68 and <= 80 && run.ActualHeight >= 32
                           && install.ActualWidth >= 32 && install.ActualHeight >= 32
                           && runBounds.Left >= 0 && runBounds.Right <= window.ActualWidth
                           && installBounds.Left >= 0 && installBounds.Right <= window.ActualWidth;
                });
                Check(Math.Abs(window.ActualWidth - window.MinWidth) < 1
                      && Math.Abs(window.ActualHeight - window.MinHeight) < 1 && usable,
                    "Minimum window size preserves usable Run and Install actions");

                Directory.CreateDirectory(imageDirectory);
                string minimumPreview = Path.Combine(imageDirectory, "i-luncher-minimum-preview.png");
                SaveScreenshot(window, minimumPreview);
                Check(File.Exists(minimumPreview), "Minimum-size RenderTargetBitmap screenshot saved");
                window.Width = 860;
                window.Height = 640;
                window.UpdateLayout();
                await Idle(window);
                string previewPath = Path.Combine(imageDirectory, "i-luncher-preview.png");
                SaveScreenshot(window, previewPath);
                Check(File.Exists(previewPath) && new FileInfo(previewPath).Length > 0,
                    "Launcher card RenderTargetBitmap preview updated");

                LaunchTiming diskTiming = await SmokeDiskAtlas();
                timings.Add(diskTiming);
                Check(diskTiming.ContentRenderedMilliseconds > 0,
                    "DiskAtlas renders in-process for a non-elevated UI smoke without starting a scan");

                if (launchApps)
                {
                    foreach ((string appId, Type expectedType) in liveWindowTypes)
                    {
                        Button button = ActionButtons(window, "Launch_").Single(candidate =>
                            AutomationProperties.GetAutomationId(candidate) == $"Launch_{appId}");
                        (Window appWindow, LaunchTiming timing) = await LaunchAndMeasure(window, button, appId, expectedType);
                        launched.Add(appId, appWindow);
                        timings.Add(timing);
                        Check(appWindow.IsVisible && appWindow.GetType() == expectedType,
                            $"{appId} opens as an in-process Window");

                        appWindow.WindowState = WindowState.Minimized;
                        int beforeCount = Application.Current.Windows.Cast<Window>().Count(candidate => candidate.GetType() == expectedType);
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal);
                        await Idle(window);
                        Window[] after = Application.Current.Windows.Cast<Window>()
                            .Where(candidate => candidate.GetType() == expectedType).ToArray();
                        Check(beforeCount == 1 && after.Length == 1 && ReferenceEquals(after[0], appWindow)
                              && appWindow.WindowState != WindowState.Minimized,
                            $"Repeated {appId} activation reuses and restores its Window");
                    }

                    launched["swift-copy"].Close();
                    await Idle(window);
                    Check(!launched["swift-copy"].IsVisible
                          && launched["folder-compare"].IsVisible && launched["deskcal"].IsVisible,
                        "Closing one integrated app leaves the others open");

                    ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(!window.IsVisible && launched["folder-compare"].IsVisible && launched["deskcal"].IsVisible,
                        "Closing the launcher leaves integrated app windows open");
                    launched["deskcal"].Close();
                    await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    using (File.Open(Path.Combine(testState, "DeskCal", "deskcal.db"), FileMode.Open,
                               FileAccess.ReadWrite, FileShare.None))
                    {
                        Check(launched["folder-compare"].IsVisible,
                            "Closing DeskCal releases its database while another tool remains open");
                    }
                }
                else
                {
                    Check(!Application.Current.Windows.Cast<Window>().Any(candidate =>
                            liveWindowTypes.Values.Contains(candidate.GetType())),
                        "Default UI checks do not invoke integrated launcher actions");
                    ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(!window.IsVisible, "Custom close button closes launcher");
                }

                string timingPath = Path.Combine(imageDirectory, "i-launcher-launch-timings.json");
                File.WriteAllText(timingPath, JsonSerializer.Serialize(new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    mode = launchApps ? "launch-apps" : "default",
                    timings
                }, new JsonSerializerOptions { WriteIndented = true }));
                Check(File.Exists(timingPath) && new FileInfo(timingPath).Length > 0,
                    "Launch-to-ContentRendered timings saved as JSON");
                Console.WriteLine($"UI checks passed: {checks}. Preview and timings: {imageDirectory}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                result = 1;
            }
            finally
            {
                foreach (Window appWindow in launched.Values)
                    if (appWindow.IsLoaded) appWindow.Close();
                if (window.IsLoaded) window.Close();
                SetFolderCompareSettingsPath(null);
                Environment.SetEnvironmentVariable("DESKCAL_DATA_DIR", previousDeskCalDataDirectory);
                application.Shutdown();
                try
                {
                    if (Directory.Exists(testState)) Directory.Delete(testState, recursive: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARN Could not remove UI check state '{testState}': {ex.Message}");
                }
            }
        };
        window.Show();
        application.Run();
        return result;
    }

    private static async Task<(Window Window, LaunchTiming Timing)> LaunchAndMeasure(
        MainWindow launcher, Button button, string appId, Type expectedType)
    {
        var existing = Application.Current.Windows.Cast<Window>()
            .Where(window => window.GetType() == expectedType).ToHashSet();
        var rendered = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Window? launched = null;
        await launcher.Dispatcher.InvokeAsync(() =>
        {
            launched = Application.Current.Windows.Cast<Window>()
                .SingleOrDefault(window => window.GetType() == expectedType && !existing.Contains(window));
            if (launched is null)
                throw new InvalidOperationException($"{appId} did not create its expected in-process Window.");
            Window target = launched;
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                target.ContentRendered -= handler;
                rendered.TrySetResult(stopwatch.Elapsed.TotalMilliseconds);
            };
            target.ContentRendered += handler;
        }, DispatcherPriority.Normal);

        double elapsed = await WithTimeout(rendered.Task, TimeSpan.FromSeconds(20),
            $"{appId} did not raise ContentRendered.");
        await Idle(launcher);
        return (launched!, new LaunchTiming(appId, elapsed, "launcher-click"));
    }

    private static async Task<LaunchTiming> SmokeDiskAtlas()
    {
        var rendered = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        var window = new DiskAtlas.App.MainWindow
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000
        };
        window.ContentRendered += (_, _) => rendered.TrySetResult(stopwatch.Elapsed.TotalMilliseconds);
        window.Show();
        try
        {
            double elapsed = await WithTimeout(rendered.Task, TimeSpan.FromSeconds(20),
                "DiskAtlas smoke window did not raise ContentRendered.");
            if (!window.IsVisible || window.WindowStyle != WindowStyle.None || window.Icon is null
                || window.TryFindResource("CanvasBrush") is null)
                throw new InvalidOperationException("DiskAtlas smoke window did not apply its embedded styles and chrome.");
            return new LaunchTiming("disk-atlas", elapsed, "direct-ui-smoke");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, string message)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (!ReferenceEquals(completed, task)) throw new TimeoutException(message);
        return await task;
    }

    private static async Task WithTimeout(Task task, TimeSpan timeout, string message)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (!ReferenceEquals(completed, task)) throw new TimeoutException(message);
        await task;
    }

    private static void SetFolderCompareSettingsPath(string? path)
    {
        PropertyInfo property = typeof(FolderCompareWPF.PathSettings).GetProperty(
            "SettingsPathOverride", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(FolderCompareWPF.PathSettings).FullName, "SettingsPathOverride");
        property.SetValue(null, path);
    }

    private static async Task Idle(DispatcherObject element) =>
        await element.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static async Task WaitForCatalog(MainWindow window, DelayedLauncherCatalog catalog)
    {
        while (!catalog.LoadCompleted || window.LauncherCards.Count == 0)
            await Task.Delay(20);
    }

    private static async Task WaitForUninstall(MainWindow.LauncherCard card)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (card.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(25);
        if (card.IsBusy) throw new TimeoutException("Uninstall action remained busy.");
    }

    private static List<Button> ActionButtons(DependencyObject root, string prefix) =>
        Descendants<Button>(root)
            .Where(button => AutomationProperties.GetAutomationId(button).StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

    private static Rect BoundsInWindow(FrameworkElement element, Window window) =>
        element.TransformToAncestor(window).TransformBounds(new Rect(new Point(), element.RenderSize));

    private static bool ExpectedInstalledAwareInstallLabel(
        Button button, IEnumerable<MainWindow.LauncherCard> cards)
    {
        string automationId = AutomationProperties.GetAutomationId(button);
        if (!automationId.StartsWith("Install_", StringComparison.Ordinal)) return false;
        string appId = automationId["Install_".Length..];
        MainWindow.LauncherCard card = cards.Single(candidate => candidate.Entry.Id == appId);
        return AutomationProperties.GetName(button) == card.InstallAccessibleName
            && Equals(card.InstallLabel, card.IsInstalled
                ? card.Entry.CanUninstall ? "\uC81C\uAC70" : "\uC124\uCE58\uC644\uB8CC"
                : "\uC124\uCE58");
    }

    private static void SaveScreenshot(Window window, string path)
    {
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed record LaunchTiming(string AppId, double ContentRenderedMilliseconds, string Trigger);

    private sealed class DelayedLauncherCatalog(int delayMilliseconds)
        : ILauncher.Services.ILauncherCatalog
    {
        private int _loadStarted;
        private int _loadCompleted;

        public string RootDirectory => Path.GetTempPath();
        public bool LoadStarted => Volatile.Read(ref _loadStarted) != 0;
        public bool LoadCompleted => Volatile.Read(ref _loadCompleted) != 0;

        public ILauncher.Services.CatalogResult Load()
        {
            Interlocked.Exchange(ref _loadStarted, 1);
            Thread.Sleep(delayMilliseconds);
            string root = Path.GetTempPath();
            ILauncher.Services.CatalogResult result = new(
            [new ILauncher.Models.LauncherEntry
            {
                Id = "delayed-test",
                Name = "Delayed test",
                Description = "Catalog response used by the UI responsiveness check",
                Category = "Checks",
                ExecutablePath = Path.Combine(root, "missing.exe"),
                WorkingDirectory = root
            }], []);
            Interlocked.Exchange(ref _loadCompleted, 1);
            return result;
        }

        public Task<string> InstallAsync(ILauncher.Models.LauncherEntry entry) =>
            throw new NotSupportedException();
        public Task<string> UninstallAsync(ILauncher.Models.LauncherEntry entry) =>
            throw new NotSupportedException();
        public void Launch(ILauncher.Models.LauncherEntry entry) => throw new NotSupportedException();
        public Task LaunchAsync(ILauncher.Models.LauncherEntry entry) => throw new NotSupportedException();
    }

    private sealed class FixtureIntegratedAppProvider(ILauncher.Models.LauncherEntry entry)
        : ILauncher.Services.IIntegratedAppProvider
    {
        public IReadOnlyList<ILauncher.Models.LauncherEntry> Entries => [entry];
        public Task LaunchAsync(string appId) => throw new NotSupportedException();
    }
}
