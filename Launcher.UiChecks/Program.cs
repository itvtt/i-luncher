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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [STAThread]
    private static int Main(string[] args)
    {
        bool launchApps = args.Contains("--launch-apps", StringComparer.OrdinalIgnoreCase);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
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
        int skippedChecks = 0;
        int result = 0;
        bool anyPreviewSaved = false;

        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine($"PASS {++checks}: {message}");
        }

        void Skip(string message)
        {
            skippedChecks++;
            Console.WriteLine($"SKIP: {message}");
        }

        bool? ShowInstallDialog(Action<InstallConfirmationWindow> closeDialog)
        {
            const string appName = "A long synthetic app name that wraps cleanly";
            var owner = new Window
            {
                Width = 520,
                Height = 320,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000
            };
            owner.Show();
            var dialog = new InstallConfirmationWindow(appName, null)
            {
                Owner = owner,
                ShowActivated = false,
                Left = -20000,
                Top = -20000
            };
            Exception? dialogCheckError = null;
            dialog.ContentRendered += (_, _) =>
            {
                try
                {
                    Check(ReferenceEquals(dialog.Owner, owner)
                          && !IsWindowEnabled(new WindowInteropHelper(owner).Handle),
                        "Install confirmation is modal and disables its owner");
                    Check(((Button)dialog.FindName("CancelButton")!).IsKeyboardFocused,
                        "Install confirmation initially focuses Cancel");
                    Check(((TextBlock)dialog.FindName("AppNameText")!).Text == appName
                          && ((TextBlock)dialog.FindName("AppInitialsText")!).Visibility == Visibility.Visible,
                        "Install confirmation shows the app name and a safe icon fallback");
                    closeDialog(dialog);
                }
                catch (Exception ex)
                {
                    dialogCheckError = ex;
                    dialog.Close();
                }
            };
            bool? result = dialog.ShowDialog();
            owner.Close();
            if (dialogCheckError is not null) throw dialogCheckError;
            return result;
        }

        window.Loaded += async (_, _) =>
        {
            string imageDirectory = Path.Combine(sourceRoot, "artifacts");
            try
            {
                await NoticeUiChecks.RunAsync(Check);
                Check(ShowInstallDialog(dialog =>
                        ((Button)dialog.FindName("CancelButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))) == false,
                    "Cancel closes install confirmation without approving installation");
                Check(ShowInstallDialog(dialog =>
                    {
                        var source = PresentationSource.FromVisual(dialog)
                            ?? throw new InvalidOperationException("Install dialog has no presentation source.");
                        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                        {
                            RoutedEvent = Keyboard.PreviewKeyDownEvent
                        };
                        dialog.RaiseEvent(escape);
                    }) == false,
                    "Escape closes install confirmation as a cancellation");
                Check(ShowInstallDialog(dialog =>
                        ((Button)dialog.FindName("InstallButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))) == true,
                    "Install confirms the dialog without launching an app");

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
                Check(runOnlyButton.ActualWidth >= 280 && runOnlyButton.ActualHeight >= 108
                      && !runOnlyButton.IsEnabled
                      && !ActionButtons(responsivenessWindow, "Install_").Any(),
                    $"Unavailable card state: {runOnlyButton.ActualWidth}x{runOnlyButton.ActualHeight}, enabled={runOnlyButton.IsEnabled}, registration controls={ActionButtons(responsivenessWindow, "Install_").Count}");
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
                var activationProbe = new ActivationProbeCatalog(uninstallCatalog);
                int installPromptCount = 0;
                var cancelInstallWindow = new MainWindow(activationProbe, confirmInstall: (entry, prompt) =>
                {
                    installPromptCount++;
                    Check(entry.Name == uninstallEntry.Name && prompt.Contains(entry.Name, StringComparison.Ordinal)
                          && prompt.Contains("\uC124\uCE58\uD558\uC2DC\uACA0\uC2B5\uB2C8\uAE4C", StringComparison.Ordinal),
                        "Install prompt names the app and asks before installing");
                    return false;
                }) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
                cancelInstallWindow.Show();
                await Idle(cancelInstallWindow);
                MainWindow.LauncherCard cancelInstallCard = cancelInstallWindow.LauncherCards.Single(card => card.Entry.Id == "deskcal");
                Button cancelInstallPrimary = ActionButtons(cancelInstallWindow, "Launch_").Single(button => AutomationProperties.GetAutomationId(button) == "Launch_deskcal");
                Check(cancelInstallCard.CanActivate && !cancelInstallCard.CanRun && cancelInstallPrimary.IsEnabled
                      && cancelInstallPrimary.ToolTip?.ToString()?.Contains("\uC124\uCE58 \uC5EC\uBD80", StringComparison.Ordinal) == true,
                    "Uninstalled installable card is an accessible confirmation action");
                typeof(MainWindow).GetMethod("LauncherCard_ContextMenuOpening",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cancelInstallWindow,
                    [cancelInstallPrimary, null]);
                Check(!cancelInstallCard.CanUpdate
                      && cancelInstallPrimary.ContextMenu!.Items.OfType<MenuItem>()
                          .All(item => item.Visibility == Visibility.Collapsed && !item.IsEnabled),
                    "Update and both removal actions stay hidden for uninstalled apps");
                cancelInstallPrimary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle(cancelInstallWindow);
                Check(installPromptCount == 1 && activationProbe.InstallCalls == 0 && activationProbe.LaunchCalls == 0
                      && !cancelInstallCard.IsBusy,
                    "Declining install confirmation performs no install or launch");
                cancelInstallWindow.Close();

                var acceptedInstallProbe = new ActivationProbeCatalog(uninstallCatalog);
                var acceptInstallWindow = new MainWindow(acceptedInstallProbe,
                    confirmInstall: (_, _) => true) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
                acceptInstallWindow.Show();
                await Idle(acceptInstallWindow);
                Button acceptInstallPrimary = ActionButtons(acceptInstallWindow, "Launch_").Single(button => AutomationProperties.GetAutomationId(button) == "Launch_deskcal");
                acceptInstallPrimary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle(acceptInstallWindow);
                await Task.Delay(25);
                MainWindow.LauncherCard acceptedInstallCard = acceptInstallWindow.LauncherCards.Single(card => card.Entry.Id == "deskcal");
                Check(acceptedInstallProbe.InstallCalls == 1 && acceptedInstallProbe.LaunchCalls == 0
                      && !acceptedInstallCard.IsBusy && !acceptedInstallCard.IsInstalled,
                    "Accepting install confirmation installs once without auto-launching");
                acceptedInstallCard.IsBusy = true;
                Check(!acceptedInstallCard.CanActivate && !acceptedInstallCard.CanRun,
                    "Busy cards reject both launch and install activation");
                acceptedInstallCard.IsBusy = false;
                acceptInstallWindow.Close();

                await uninstallCatalog.InstallAsync(uninstallEntry);
                var fakeUpdates = new FakeUpdateLauncherCatalog(uninstallCatalog, "deskcal");
                var updateWindow = new MainWindow(fakeUpdates)
                { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
                updateWindow.Show();
                await Idle(updateWindow);
                MainWindow.LauncherCard updateCard = updateWindow.LauncherCards.Single(card => card.Entry.Id == "deskcal");
                await WithTimeout(WaitForVersion(updateWindow, fakeUpdates, updateCard), TimeSpan.FromSeconds(5),
                    "Fake update catalog did not populate card version metadata.");
                Check(updateCard.VersionText == "1.0" && updateCard.CanUpdate,
                    "Update cards show the compact installed version and retain update availability");
                PropertyInfo onlineInstalledVersion = typeof(ILauncher.Models.LauncherEntry).GetProperty(
                    "OnlineInstalledVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
                PropertyInfo availableVersion = typeof(ILauncher.Models.LauncherEntry).GetProperty(
                    "AvailableVersion", BindingFlags.Instance | BindingFlags.Public)!;
                PropertyInfo updateAvailable = typeof(ILauncher.Models.LauncherEntry).GetProperty(
                    "UpdateAvailable", BindingFlags.Instance | BindingFlags.Public)!;
                string unknownExecutable = Path.Combine(testState, "unknown-installed.exe");
                File.WriteAllBytes(unknownExecutable, Array.Empty<byte>());
                var unknownEntry = new ILauncher.Models.LauncherEntry
                {
                    Id = "unknown-version-fixture",
                    Name = "Unknown Version Fixture",
                    ExecutablePath = unknownExecutable,
                    WorkingDirectory = testState
                };
                typeof(ILauncher.Models.LauncherEntry).GetProperty("OnlineManaged",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unknownEntry, true);
                typeof(ILauncher.Models.LauncherEntry).GetProperty("OnlineInstalled",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unknownEntry, true);
                typeof(ILauncher.Models.LauncherEntry).GetProperty("OnlineExecutablePath",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unknownEntry, unknownExecutable);
                availableVersion.SetValue(unknownEntry, "1.1");
                updateAvailable.SetValue(unknownEntry, false);
                var unknownVersionCard = new MainWindow.LauncherCard(unknownEntry);
                Check(unknownVersionCard.IsInstalled && !unknownEntry.UpdateAvailable
                      && unknownVersionCard.VersionText == "버전 정보 없음"
                      && unknownVersionCard.VersionToolTip.Contains("\uD655\uC778\uD560 \uC218 \uC5C6", StringComparison.Ordinal),
                    "Unknown installed version with a known available version remains unknown when no update is verified");
                availableVersion.SetValue(unknownEntry, null);
                unknownVersionCard.NotifyVersionState();
                Check(unknownVersionCard.VersionText == "버전 정보 없음"
                      && unknownVersionCard.VersionVisibility == Visibility.Visible
                      && !unknownVersionCard.VersionText.Contains("?", StringComparison.Ordinal),
                    "Missing installed and available versions show a clear unknown state without invented numbers");
                var bundledVersionEntry = new ILauncher.Models.LauncherEntry
                {
                    Id = "bundled-version-fixture",
                    Name = "Bundled Version Fixture",
                    ExecutablePath = unknownExecutable,
                    WorkingDirectory = testState,
                    IsIntegrated = true,
                    BundledVersion = "3.2.1"
                };
                var bundledVersionCard = new MainWindow.LauncherCard(bundledVersionEntry);
                Check(!bundledVersionCard.IsInstalled && bundledVersionEntry.InstalledVersion is null
                      && bundledVersionCard.VersionText == "3.2.1"
                      && bundledVersionCard.VersionToolTip.Contains("3.2.1", StringComparison.Ordinal)
                      && !bundledVersionCard.VersionToolTip.Contains("설치된", StringComparison.Ordinal),
                    "Offline bundled version is compact with its launcher provenance in the tooltip");
                availableVersion.SetValue(unknownEntry, "1.1");
                unknownVersionCard.NotifyVersionState();
                Check(!unknownEntry.UpdateAvailable
                      && unknownVersionCard.VersionText == "버전 정보 없음"
                      && unknownVersionCard.VersionToolTip.Contains("\uD655\uC778\uD560 \uC218 \uC5C6", StringComparison.Ordinal),
                    "Known available version is not presented as installed when a synthetic managed install has no version");
                onlineInstalledVersion.SetValue(updateCard.Entry, "v1.0.3.42-beta+build7");
                availableVersion.SetValue(updateCard.Entry, "v2.0.0.8+release");
                updateCard.NotifyVersionState();
                Check(updateCard.VersionText == "1.0.3" && updateCard.CanUpdate
                      && updateCard.VersionToolTip.Contains("설치된 앱: v1.0.3.42-beta+build7", StringComparison.Ordinal)
                      && updateCard.VersionToolTip.Contains("새 버전: v2.0.0.8+release", StringComparison.Ordinal),
                    "Card trims prefix, revision and suffix while tooltip preserves full installed and update versions");
                PropertyInfo bundledVersion = typeof(ILauncher.Models.LauncherEntry).GetProperty(
                    "BundledVersion", BindingFlags.Instance | BindingFlags.Public)!;
                bundledVersion.SetValue(bundledVersionEntry, "V3.2.1.99+local");
                Check(bundledVersionCard.VersionText == "3.2.1"
                      && bundledVersionCard.VersionToolTip.Contains("런처의 앱: V3.2.1.99+local", StringComparison.Ordinal),
                    "Bundled card limits numeric components and retains the full launcher version");
                bundledVersion.SetValue(unknownEntry, "4.5.6.7");
                Check(unknownVersionCard.VersionText == "4.5.6"
                      && unknownVersionCard.VersionToolTip.Contains("설치된 앱의 버전은 확인할 수 없습니다.", StringComparison.Ordinal)
                      && unknownVersionCard.VersionToolTip.Contains("런처의 앱: 4.5.6.7", StringComparison.Ordinal),
                    "Unknown installed version uses bundled display fallback with truthful provenance");
                bundledVersion.SetValue(unknownEntry, null);
                updateAvailable.SetValue(unknownEntry, true);
                Check(unknownVersionCard.VersionText == "1.1"
                      && unknownVersionCard.VersionToolTip.Contains("새 버전: 1.1", StringComparison.Ordinal),
                    "Verified update keeps the existing available-version fallback when installed version is unknown");
                updateAvailable.SetValue(unknownEntry, false);
                bundledVersion.SetValue(bundledVersionEntry, "custom metadata with a long description");
                Check(bundledVersionCard.VersionText == "버전 정보 없음"
                      && bundledVersionCard.VersionToolTip.Contains(bundledVersionEntry.BundledVersion, StringComparison.Ordinal),
                    "Nonnumeric metadata has a bounded truthful fallback and remains available in the tooltip");
                onlineInstalledVersion.SetValue(updateCard.Entry, "1.1");
                availableVersion.SetValue(updateCard.Entry, "1.1");
                updateAvailable.SetValue(updateCard.Entry, false);
                updateCard.NotifyVersionState();
                Check(updateCard.VersionText == "1.1"
                      && updateCard.VersionBrush is SolidColorBrush sameVersionBrush
                      && sameVersionBrush.Color != Color.FromRgb(168, 218, 181)
                      && !updateCard.CanUpdate,
                    "Matching installed and available versions do not advertise an update");
                onlineInstalledVersion.SetValue(updateCard.Entry, "1.0");
                availableVersion.SetValue(updateCard.Entry, "1.1");
                updateAvailable.SetValue(updateCard.Entry, true);
                updateCard.NotifyVersionState();
                Button updatePrimary = ActionButtons(updateWindow, "Launch_").Single(button => AutomationProperties.GetAutomationId(button) == "Launch_deskcal");
                updatePrimary.ApplyTemplate();
                var hoverOverlay = (Border)updatePrimary.Template.FindName("LaunchHoverOverlay", updatePrimary)!;
                var hoverOutline = (Border)updatePrimary.Template.FindName("LaunchHoverOutline", updatePrimary)!;
                var interactionTransform = (ScaleTransform)updatePrimary.RenderTransform;
                Size cardLayoutSize = updatePrimary.RenderSize;
                typeof(MainWindow).GetMethod("LauncherCard_MouseEnter",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(updateWindow,
                    [updatePrimary, new MouseEventArgs(Mouse.PrimaryDevice, 0)]);
                interactionTransform = (ScaleTransform)updatePrimary.RenderTransform;
                Check((SystemParameters.ClientAreaAnimation
                           ? hoverOverlay.HasAnimatedProperties && interactionTransform.HasAnimatedProperties
                           : hoverOverlay.HasAnimatedProperties && !interactionTransform.HasAnimatedProperties
                             && Math.Abs(interactionTransform.ScaleX - 1) < 0.001)
                      && updatePrimary.RenderSize == cardLayoutSize,
                    "Synthetic hover animates the state tint and only adds geometric lift when motion is enabled");
                typeof(MainWindow).GetMethod("LauncherCard_MouseLeave",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(updateWindow,
                    [updatePrimary, new MouseEventArgs(Mouse.PrimaryDevice, 0)]);
                hoverOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                hoverOutline.BeginAnimation(UIElement.OpacityProperty, null);
                interactionTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                interactionTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                interactionTransform.ScaleX = 1;
                interactionTransform.ScaleY = 1;
                updatePrimary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(hoverOverlay.HasAnimatedProperties
                      && (SystemParameters.ClientAreaAnimation
                          ? interactionTransform.HasAnimatedProperties
                          : Math.Abs(interactionTransform.ScaleX - 1) < 0.001
                            && Math.Abs(interactionTransform.ScaleY - 1) < 0.001),
                    "Launch click starts its glow synchronously and limits reduced-motion feedback to opacity");
                await Idle(updateWindow);
                Check(fakeUpdates.LaunchCalls == 1 && fakeUpdates.UpdateCalls == 0,
                    "Primary card click continues to launch without implicit update");
                typeof(MainWindow).GetMethod("LauncherCard_ContextMenuOpening",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(updateWindow,
                    [updatePrimary, null]);
                ContextMenu versionMenu = updatePrimary.ContextMenu!;
                MenuItem updateItem = versionMenu.Items.OfType<MenuItem>().First();
                Check(updateItem.Visibility == Visibility.Visible && updateItem.IsEnabled,
                    "Available update is exposed only as an explicit context action");
                updateCard.IsBusy = true;
                typeof(MainWindow).GetMethod("LauncherCard_ContextMenuOpening",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(updateWindow,
                    [updatePrimary, null]);
                Check(!updateCard.CanUpdate && updateItem.Visibility == Visibility.Collapsed,
                    "Busy cards suppress the update action");
                updateCard.IsBusy = false;
                typeof(MainWindow).GetMethod("LauncherCard_ContextMenuOpening",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(updateWindow,
                    [updatePrimary, null]);
                typeof(MainWindow).GetMethod("UpdateContextMenuItem_Click",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(updateWindow,
                    [updateItem, new RoutedEventArgs(MenuItem.ClickEvent, updateItem)]);
                await Idle(updateWindow);
                Check(fakeUpdates.UpdateCalls == 1 && fakeUpdates.LaunchCalls == 1
                      && !updateCard.Entry.UpdateAvailable && updateCard.VersionText == "1.1",
                    "Explicit update invokes UpdateAsync once without launching the app");
                updateWindow.Close();

                bool confirmRemoval = false;
                int confirmationCount = 0;
                var launchProbe = new ActivationProbeCatalog(uninstallCatalog);
                bool confirmFullDelete = false;
                string fullDeletePrompt = "";
                var installedLaunchWindow = new MainWindow(launchProbe, (_, prompt) =>
                {
                    fullDeletePrompt = prompt;
                    return confirmFullDelete;
                })
                { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
                installedLaunchWindow.Show();
                await Idle(installedLaunchWindow);
                Button installedPrimary = ActionButtons(installedLaunchWindow, "Launch_").Single(button => AutomationProperties.GetAutomationId(button) == "Launch_deskcal");
                installedPrimary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle(installedLaunchWindow);
                await Task.Delay(25);
                Check(launchProbe.LaunchCalls == 1 && launchProbe.InstallCalls == 0,
                    "Installed primary surface launches through the existing launch path");
                typeof(MainWindow).GetMethod("LauncherCard_ContextMenuOpening",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(installedLaunchWindow,
                    [installedPrimary, null]);
                MenuItem fullDeleteItem = installedPrimary.ContextMenu!.Items.OfType<MenuItem>()
                    .Single(item => AutomationProperties.GetName(item) == "앱 완전 삭제");
                Check(fullDeleteItem.Visibility == Visibility.Visible && fullDeleteItem.IsEnabled,
                    "Installed managed app offers full deletion separately from normal removal");
                fullDeleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, fullDeleteItem));
                Check(launchProbe.DeleteFilesCalls == 0 && fullDeletePrompt.Contains(uninstallEntry.Name)
                      && fullDeletePrompt.Contains("설치 파일과 캐시")
                      && fullDeletePrompt.Contains("문서 및 설정은 유지"),
                    "Declining full deletion preserves the installation and explains its boundaries");
                confirmFullDelete = true;
                fullDeleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, fullDeleteItem));
                await Idle(installedLaunchWindow);
                Check(launchProbe.DeleteFilesCalls == 1 && launchProbe.UninstallCalls == 0,
                    "Accepting full deletion routes only to the explicit file deletion overload");
                MainWindow.LauncherCard fullDeleteCard = installedLaunchWindow.LauncherCards
                    .Single(card => card.Entry.Id == "deskcal");
                fullDeleteCard.IsBusy = true;
                typeof(MainWindow).GetMethod("LauncherCard_ContextMenuOpening",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(installedLaunchWindow,
                    [installedPrimary, null]);
                fullDeleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, fullDeleteItem));
                Check(fullDeleteItem.Visibility == Visibility.Collapsed && !fullDeleteItem.IsEnabled
                      && launchProbe.DeleteFilesCalls == 1,
                    "Busy cards hide and block full deletion");
                fullDeleteCard.IsBusy = false;
                installedLaunchWindow.Close();

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
                Button uninstallLaunchButton = ActionButtons(uninstallWindow, "Launch_")
                    .Single(button => AutomationProperties.GetAutomationId(button) == "Launch_deskcal");
                Check(uninstallCard.IsInstalled && uninstallCard.Entry.CanUninstall
                      && uninstallCard.CanRun && uninstallLaunchButton.IsEnabled
                      && uninstallCard.IconOpacity == 1.0
                      && uninstallCard.RunToolTip == $"\uC2E4\uD589 {uninstallCard.Entry.Name}"
                      && uninstallCard.CanRemove && !ActionButtons(uninstallWindow, "Install_").Any(),
                    "Install enables the full-color launch group with removal available from its context menu");
                Check(uninstallLaunchButton.Focus() && ReferenceEquals(Keyboard.FocusedElement, uninstallLaunchButton),
                    "Installed launch group is keyboard accessible");
                await Idle(uninstallWindow);
                Border launchFocusBorder = (Border?)uninstallLaunchButton.Template.FindName("LaunchFocusOverlay", uninstallLaunchButton)
                    ?? throw new InvalidOperationException("Launch focus overlay was not found.");
                Check(launchFocusBorder.BorderBrush is SolidColorBrush launchFocusBrush
                      && launchFocusBrush.Color == Color.FromRgb(124, 156, 255)
                      && launchFocusBorder.BorderThickness.Left >= 2
                      && BoundsInWindow(launchFocusBorder, uninstallWindow) == BoundsInWindow(
                          FindVisualAncestor<Border>(uninstallLaunchButton)!, uninstallWindow),
                    "Installed launch group shows an accent keyboard focus state");
                ((Button)uninstallWindow.FindName("InstalledAppsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(uninstallWindow.LauncherCards.Count == 1 && uninstallWindow.LauncherCards[0].IsInstalled,
                    "Installed-app filter shows installed entries");
                Button checksCategory = Descendants<Button>(uninstallWindow).Single(button => Equals(button.Content, "Checks"));
                checksCategory.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TextBox fixtureSearch = (TextBox)uninstallWindow.FindName("SearchBox");
                fixtureSearch.Text = "DeskCal";
                Check(uninstallWindow.LauncherCards.Count == 1 && uninstallWindow.LauncherCards[0].Entry.Id == "deskcal",
                    "Category and name search combine in the library");
                fixtureSearch.Text = "no matching fixture";
                Check(uninstallWindow.LauncherCards.Count == 0
                      && ((TextBlock)uninstallWindow.FindName("EmptyStateTitle")).Text == "일치하는 앱이 없습니다",
                    "Combined filters display the requested empty state");
                ((Button)uninstallWindow.FindName("ClearFiltersButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)uninstallWindow.FindName("InstalledAppsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(uninstallWindow.LauncherCards.Count == 1, "Clearing filters restores the installed-app result");
                ContextMenu uninstallMenu = uninstallLaunchButton.ContextMenu
                    ?? throw new InvalidOperationException("Card context menu was not created.");
                typeof(MainWindow).GetMethod("LauncherCard_ContextMenuOpening",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(uninstallWindow,
                    [uninstallLaunchButton, null]);
                uninstallMenu.IsOpen = true;
                await Idle(uninstallWindow);
                MenuItem uninstallMenuItem = uninstallMenu.Items.OfType<MenuItem>()
                    .Single(item => AutomationProperties.GetName(item) == "\uC571 \uC81C\uAC70");
                Check(uninstallMenuItem.IsVisible && uninstallMenuItem.IsEnabled
                      && AutomationProperties.GetName(uninstallMenuItem) == "\uC571 \uC81C\uAC70",
                    "Installed managed app exposes accessible removal through the right-click menu");
                uninstallMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, uninstallMenuItem));
                uninstallMenu.IsOpen = false;
                Check(confirmationCount == 1 && uninstallCard.IsInstalled,
                    "Declining uninstall confirmation leaves the managed installation intact");
                confirmRemoval = true;
                uninstallMenu.IsOpen = true;
                await Idle(uninstallWindow);
                uninstallMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, uninstallMenuItem));
                uninstallMenu.IsOpen = false;
                await WithTimeout(WaitForUninstall(uninstallCard), TimeSpan.FromSeconds(15),
                    "Isolated uninstall fixture did not finish removing its registration.");
                Check(!uninstallCard.IsInstalled && !uninstallCard.CanRun
                      && uninstallCard.IconOpacity == 0.85
                      && uninstallCard.RunToolTip == "\uBBF8\uC124\uCE58 \uC571\uC744 \uD074\uB9AD\uD558\uBA74 \uC124\uCE58 \uC5EC\uBD80\uB97C \uD655\uC778\uD569\uB2C8\uB2E4"
                      ,
                    "Removal disables and mutes the launch group");
                Check(Directory.Exists(Path.Combine(uninstallAppsRoot, "_shared-payloads")),
                    "Uninstall retains the shared app payload");
                Check(uninstallWindow.LauncherCards.Count == 0
                      && ((Button)uninstallWindow.FindName("ClearFiltersButton")).IsVisible,
                    "Installed-app membership updates after removal");
                ((Button)uninstallWindow.FindName("ClearFiltersButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(uninstallWindow.LauncherCards.Any(card => ReferenceEquals(card, uninstallCard)) && !uninstallCard.IsInstalled,
                    "Empty-state reset restores the removed app entry");
                await Idle(uninstallWindow);
                Button refreshedLaunchButton = ActionButtons(uninstallWindow, "Launch_")
                    .Single(button => AutomationProperties.GetAutomationId(button) == "Launch_deskcal");
                Check(refreshedLaunchButton.IsEnabled && uninstallCard.CanActivate && !uninstallCard.CanRun
                      && !ActionButtons(uninstallWindow, "Install_").Any(),
                    "After removal the card returns to install-confirm activation without a visible registration button");
                Border removedCardBorder = FindVisualAncestor<Border>(refreshedLaunchButton)!;
                Border removedStateOutline = Descendants<Border>(uninstallWindow).Single(outline =>
                    outline.Name == "CardStateOutline" && ReferenceEquals(outline.DataContext, uninstallCard));
                Check(ReferenceEquals(removedCardBorder.Background, uninstallCard.CardBackgroundBrush)
                      && removedCardBorder.Background is SolidColorBrush removedSurface
                      && removedSurface.Color == Color.FromRgb(35, 45, 59)
                      && removedCardBorder.BorderBrush is SolidColorBrush removedOutline
                      && removedOutline.Color == Color.FromRgb(117, 131, 152)
                      && ReferenceEquals(removedStateOutline.BorderBrush, uninstallCard.CardBorderBrush)
                      && removedStateOutline.BorderThickness.Left == 2,
                    "Removal reactively restores the neutral uninstalled card colors");
                Rect uninstalledBoundsBeforeFocus = BoundsInWindow(removedCardBorder, uninstallWindow);
                Check(refreshedLaunchButton.Focus(), "Uninstalled primary surface accepts keyboard focus");
                await Idle(uninstallWindow);
                Border uninstalledFocusBorder = (Border?)refreshedLaunchButton.Template.FindName(
                    "LaunchFocusOverlay", refreshedLaunchButton)
                    ?? throw new InvalidOperationException("Uninstalled focus overlay was not found.");
                Check(uninstalledFocusBorder.BorderBrush is SolidColorBrush uninstalledFocusBrush
                      && uninstalledFocusBrush.Color == Color.FromRgb(124, 156, 255)
                      && BoundsInWindow(removedCardBorder, uninstallWindow) == uninstalledBoundsBeforeFocus,
                    "Uninstalled keyboard focus keeps the bright blue outline without changing card geometry");
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
                bool integratedVersionsMatchModules = window.LauncherCards.Where(card => card.Entry.IsIntegrated).All(card =>
                {
                    string moduleName = card.Entry.Id switch
                    {
                        "folder-compare" => "FolderCompareWPF",
                        "swift-copy" => "SwiftCopy",
                        "disk-atlas" => "DiskAtlas",
                        "deskcal" => "DeskCal",
                        _ => string.Empty
                    };
                    Assembly? moduleAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                        string.Equals(assembly.GetName().Name, moduleName, StringComparison.Ordinal));
                    if (moduleAssembly is null) return false;
                    var versionInfo = FileVersionInfo.GetVersionInfo(moduleAssembly.Location);
                    string? expectedVersion = !string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
                        ? versionInfo.ProductVersion.Trim() : versionInfo.FileVersion?.Trim();
                    return moduleName.Length > 0 && expectedVersion is not null
                        && card.Entry.BundledVersion == expectedVersion;
                });
                Check(integratedVersionsMatchModules,
                    "Each integrated app shows its matching loaded module version, not the launcher version");
                Check(expectedSupportedAppIds.All(id => window.LauncherCards.Single(card => card.Entry.Id == id).CanInstall),
                    "Integrated apps and TerminalNotes support install before output is built");
                Check(expectedSupportedAppIds.All(id => window.LauncherCards.Single(card => card.Entry.Id == id).CanInstall),
                    "Supported app cards expose enabled install actions");
                Check(window.LauncherCards.Where(card => card.Entry.IsIntegrated).All(card => card.IconSource != null)
                      && window.LauncherCards.Single(card => card.Entry.Id == "deskcal").IconSource
                          is BitmapSource { PixelWidth: 64, PixelHeight: 64 },
                    "Integrated app cards load embedded icons and choose a sharp ICO frame");
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
                bool interactiveDesktopAvailable = GetForegroundWindow() != IntPtr.Zero;
                if (interactiveDesktopAvailable)
                {
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
                }
                else
                {
                    Skip("Custom maximize behavior: no foreground desktop is available");
                    Skip("Custom restore behavior: no foreground desktop is available");
                    Skip("Custom minimize behavior: no foreground desktop is available");
                    Skip("Caption double-click maximize behavior: no foreground desktop is available");
                }

                var search = (TextBox)window.FindName("SearchBox");
                search.Text = "swift";
                Check(window.LauncherCards.Count == 1 && window.LauncherCards[0].Entry.Id == "swift-copy",
                    "Search filters app name");
                Button fileCategory = Descendants<Button>(window).Single(button => Equals(button.Content, "파일 도구"));
                fileCategory.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.LauncherCards.Count == 1 && window.LauncherCards[0].Entry.Id == "swift-copy",
                    "Category selection combines with app-name search");
                search.Text = "차이를 비교";
                Check(window.LauncherCards.Count == 1 && window.LauncherCards[0].Entry.Id == "folder-compare",
                    "Category selection combines with description search");
                search.Text = "없는 검색 결과 39481";
                Check(window.LauncherCards.Count == 0
                      && ((FrameworkElement)window.FindName("EmptyState")).Visibility == Visibility.Visible
                      && ((TextBlock)window.FindName("EmptyStateTitle")).Text == "일치하는 앱이 없습니다"
                      && ((Button)window.FindName("ClearFiltersButton")).IsVisible,
                    "No-match state names the empty result and offers filter reset");
                ((Button)window.FindName("ClearFiltersButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.LauncherCards.Count > 0 && search.Text.Length == 0,
                    "Clear filters resets search and category selection");
                search.Text = "캘린더";
                Check(window.LauncherCards.Count == 1 && window.LauncherCards[0].Entry.Id == "deskcal",
                    "DeskCal is searchable by its Korean category");
                MainWindow.LauncherCard hiddenBusyCard = window.LauncherCards[0];
                hiddenBusyCard.StatusMessage = "필터 밖 작업 진행 중";
                hiddenBusyCard.IsBusy = true;
                search.Text = "swift";
                Check(window.LauncherCards.Count == 1 && window.LauncherCards[0].Entry.Id == "swift-copy"
                      && hiddenBusyCard.IsBusy && hiddenBusyCard.StatusMessage == "필터 밖 작업 진행 중",
                    "Filtering an active card preserves its operation state off-screen");
                search.Text = "캘린더";
                Check(ReferenceEquals(window.LauncherCards.Single(), hiddenBusyCard)
                      && hiddenBusyCard.IsBusy && hiddenBusyCard.StatusMessage == "필터 밖 작업 진행 중",
                    "Restoring the filter reveals the same active card and progress state");
                hiddenBusyCard.IsBusy = false;
                hiddenBusyCard.StatusMessage = string.Empty;
                ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(search.Text == "캘린더" && window.LauncherCards.Count == 1
                      && ((TextBlock)window.FindName("CountText")).Text == "1개 앱", "Refresh preserves search and updates result count");
                search.Clear();
                window.UpdateLayout();
                await Idle(window);

                var launchButtons = ActionButtons(window, "Launch_")
                    .Where(button => expectedSupportedAppIds.Any(id =>
                        AutomationProperties.GetAutomationId(button) == $"Launch_{id}"))
                    .ToList();
                Check(!ActionButtons(window, "Install_").Any(),
                    "Install and remove controls are not shown in the card list");
                Check(launchButtons.All(button => button.Content is Grid
                                                   && AutomationProperties.GetName(button) == window.LauncherCards.Single(card =>
                                                       AutomationProperties.GetAutomationId(button) == card.AutomationId).RunAccessibleName
                                                   && button.ToolTip is string toolTip && toolTip.Length > 0
                                                   && button.Focusable),
                    "Launch groups keep app-specific accessible labels and tooltips");
                Check(!Descendants<TextBlock>(window).Any(text => text.Text.Equals("오른쪽 위 설치 아이콘으로 시작 메뉴에 등록할 수 있습니다.", StringComparison.Ordinal))
                      && window.LauncherCards.All(card => Descendants<Image>(window).Any(image =>
                          ReferenceEquals(image.DataContext, card)
                          && ((Border)((FrameworkElement)image.Parent).Parent).ToolTip?.ToString()?.Contains(card.Entry.Category, StringComparison.CurrentCultureIgnoreCase) == true
                          && ((Border)((FrameworkElement)image.Parent).Parent).ToolTip?.ToString()?.Contains(card.Entry.Description, StringComparison.CurrentCultureIgnoreCase) == true))
                      && window.LauncherCards.All(card => Descendants<TextBlock>(window).Any(text => text.IsVisible
                          && ReferenceEquals(text.DataContext, card) && text.Text == card.Entry.Category)),
                    "Cards show category badges and keep category and description in icon tooltips");
                Check(expectedSupportedAppIds.All(id =>
                {
                    Button run = launchButtons.Single(button => AutomationProperties.GetAutomationId(button) == $"Launch_{id}");
                    MainWindow.LauncherCard card = window.LauncherCards.Single(candidate => candidate.Entry.Id == id);
                    Image icon = Descendants<Image>(run).Single(image => ReferenceEquals(image.DataContext, card));
                    Border iconBorder = (Border)((FrameworkElement)icon.Parent).Parent;
                    Rect iconBounds = BoundsInWindow(iconBorder, window);
                    TextBlock title = Descendants<TextBlock>(run).Single(text =>
                        ReferenceEquals(text.DataContext, card) && text.Text == card.Entry.Name);
                    Rect titleBounds = BoundsInWindow(title, window);
                    TextBlock description = Descendants<TextBlock>(run).Single(text =>
                        ReferenceEquals(text.DataContext, card) && text.Text == card.DescriptionText);
                    Rect descriptionBounds = BoundsInWindow(description, window);
                    Border cardBorder = FindVisualAncestor<Border>(run)!;
                    Rect cardBounds = BoundsInWindow(cardBorder, window);
                    bool expectedAppearance = card.IsInstalled
                        ? card.IconOpacity == 1.0 && ReferenceEquals(title.Foreground, card.CardTitleBrush)
                            && ReferenceEquals(description.Foreground, card.CardDescriptionBrush)
                            && card.CardBackgroundBrush is SolidColorBrush installedBackground
                            && installedBackground.Color == Color.FromRgb(25, 39, 42)
                            && card.CardBorderBrush is SolidColorBrush installedBorder
                            && installedBorder.Color == Color.FromRgb(89, 191, 174)
                        : card.IconOpacity == 0.85 && ReferenceEquals(title.Foreground, card.CardTitleBrush)
                            && ReferenceEquals(description.Foreground, card.CardDescriptionBrush)
                            && card.CardBackgroundBrush is SolidColorBrush uninstalledBackground
                            && uninstalledBackground.Color == Color.FromRgb(35, 45, 59)
                            && card.CardBorderBrush is SolidColorBrush uninstalledBorder
                            && uninstalledBorder.Color == Color.FromRgb(117, 131, 152);
                    return Math.Abs(cardBounds.Width - 280) < 1 && Math.Abs(cardBounds.Height - 108) < 1
                        && Math.Abs(run.ActualWidth - cardBounds.Width) < 1
                        && Math.Abs(run.ActualHeight - cardBounds.Height) < 1
                        && run.IsEnabled == card.CanInteract
                        && Equals(run.ToolTip, card.RunToolTip)
                        && Math.Abs(iconBounds.Width - 64) < 1 && Math.Abs(iconBounds.Height - 64) < 1
                        && Math.Abs(iconBounds.Left - (cardBounds.Left + 12)) < 2
                        && Math.Abs(iconBounds.Top - (cardBounds.Top + 20)) < 2
                        && Math.Abs(titleBounds.Left - (cardBounds.Left + 92)) < 2
                        && Math.Abs(titleBounds.Top - (cardBounds.Top + 10)) < 2
                        && title.ActualWidth == 100 && title.ActualHeight == 22
                        && title.TextTrimming == TextTrimming.CharacterEllipsis
                        && Math.Abs(descriptionBounds.Left - (cardBounds.Left + 92)) < 2
                        && Math.Abs(descriptionBounds.Top - (cardBounds.Top + 34)) < 2
                        && description.ActualWidth == 176 && description.ActualHeight == 32
                        && description.TextWrapping == TextWrapping.Wrap
                        && description.TextTrimming == TextTrimming.CharacterEllipsis
                        && cardBorder.CornerRadius.TopLeft == 12
                        && cardBorder.BorderThickness.Left == 1
                        && ReferenceEquals(cardBorder.Background, card.CardBackgroundBrush)
                        && ReferenceEquals(cardBorder.BorderBrush, card.CardBorderBrush)
                        && Descendants<Border>(window).Any(outline =>
                            outline.Name == "CardStateOutline" && ReferenceEquals(outline.DataContext, card)
                            && ReferenceEquals(outline.BorderBrush, card.CardBorderBrush)
                            && outline.BorderThickness.Left == 2 && outline.CornerRadius.TopLeft == 12
                            && BoundsInWindow(outline, window) == cardBounds)
                        && expectedAppearance && run.Content is Grid
                        && run.ContextMenu?.Items.OfType<MenuItem>().Count() == 3
                        && Descendants<TextBlock>(window).Any(text => ReferenceEquals(text.DataContext, card)
                            && text.Margin.Left == 92 && text.Margin.Top == 72 && text.Width == 176
                            && text.Height == 24 && text.FontSize == 11 && text.Text == card.VersionText
                            && Equals(text.Visibility, card.VersionVisibility));
                }), "Rounded bordered cards group icon, title, description, version, and contextual update/removal on one launch surface");
                Check(window.LauncherCards.All(card => card.StatusMessage.Length == 0
                    && card.DescriptionText == card.Entry.Description
                    && Descendants<TextBlock>(window).Any(text => ReferenceEquals(text.DataContext, card)
                        && text.Text == card.Entry.Name && Equals(text.ToolTip, card.Entry.Name)
                        && text.TextTrimming == TextTrimming.CharacterEllipsis && text.TextWrapping == TextWrapping.NoWrap)
                    && Descendants<TextBlock>(window).Any(text => text.IsVisible
                        && ReferenceEquals(text.DataContext, card)
                        && text.Text == card.DescriptionText && text.TextWrapping == TextWrapping.Wrap))
                    && !Descendants<TextBlock>(window).Any(text => text.IsVisible && text.Text == "설치완료"),
                    "Idle cards show descriptions and preserve full titles with tooltips");

                Check(launchButtons.All(button => button.Focusable
                        && button.IsEnabled == ((MainWindow.LauncherCard)button.Tag).CanInteract),
                    "Primary surfaces remain keyboard-addressable and follow installed/busy state");

                foreach (MainWindow.LauncherCard card in window.LauncherCards)
                {
                    card.IsInstalling = false;
                    card.StatusMessage = "\uC2DC\uC791\uD558\uB294 \uC911\uC785\uB2C8\uB2E4.";
                    card.IsBusy = true;
                }
                await Idle(window);
                Check(window.LauncherCards.All(card =>
                        ReferenceEquals(FindVisualAncestor<Border>(ActionButtons(window, "Launch_").Single(button =>
                            Equals(AutomationProperties.GetAutomationId(button), card.AutomationId)))?.Background,
                            card.CardBackgroundBrush)
                        && ReferenceEquals(FindVisualAncestor<Border>(ActionButtons(window, "Launch_").Single(button =>
                            Equals(AutomationProperties.GetAutomationId(button), card.AutomationId)))?.BorderBrush,
                            card.CardBorderBrush)),
                    "Busy progress preserves each card's installed or uninstalled colors");
                Check(window.LauncherCards.All(card => Descendants<TextBlock>(window).Any(text =>
                          ReferenceEquals(text.DataContext, card) && text.Text == card.StatusMessage
                          && text.IsVisible && text.Width == 176 && text.Height == 32
                          && text.Margin.Left == 92 && text.Margin.Top == 34
                          && AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite)),
                    "Busy progress remains visible through polite live status text");
                Check(launchButtons.All(button => !button.IsEnabled), "Busy state disables every card action");
                Check(launchButtons.All(button => button.Content is Grid)
                    && window.LauncherCards.All(card => card.DescriptionText == card.StatusMessage)
                    && Descendants<ProgressBar>(window).Where(progress => progress.IsVisible).Count() == window.LauncherCards.Count
                    && Descendants<ProgressBar>(window).Where(progress => progress.IsVisible)
                        .All(progress => progress.Height == 3 && progress.Margin.Left == 12 && progress.Margin.Right == 12 && progress.Margin.Bottom == 2),
                    "Busy text replaces descriptions and thin progress remains visible");
                Button firstLaunch = launchButtons[0];
                Check(!firstLaunch.Focus(), "Disabled run action rejects keyboard focus");
                foreach (MainWindow.LauncherCard card in window.LauncherCards)
                {
                    card.IsBusy = false;
                    card.StatusMessage = string.Empty;
                }
                await Idle(window);
                Check(launchButtons.All(button => button.IsEnabled == ((MainWindow.LauncherCard)button.Tag).CanInteract),
                    "Primary surfaces recover according to installed state after busy state");
                Check(window.LauncherCards.All(card => card.DescriptionText == card.Entry.Description)
                    && !Descendants<ProgressBar>(window).Any(progress => progress.IsVisible),
                    "Idle descriptions return and per-card progress bars collapse");
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
                Button installingRunButton = launchButtons.Single(button =>
                    AutomationProperties.GetAutomationId(button) == $"Launch_{installingCard.Entry.Id}");
                Check(!installingRunButton.IsEnabled && !installingCard.CanActivate && !installingCard.CanInstall,
                    "Busy state disables card activation and registration eligibility");
                installingCard.IsBusy = false;
                installingCard.IsInstalling = false;
                await Idle(window);
                Check(launchButtons.All(button => button.IsEnabled == ((MainWindow.LauncherCard)button.Tag).CanInteract),
                    "Card actions recover according to installed state after busy state");

                var gridFixtures = new List<MainWindow.LauncherCard>();
                while (window.LauncherCards.Count < 9)
                {
                    var fixture = new MainWindow.LauncherCard(window.LauncherCards[gridFixtures.Count % expectedAppIds.Length].Entry);
                    gridFixtures.Add(fixture);
                    window.LauncherCards.Add(fixture);
                }
                window.UpdateLayout();
                await Idle(window);
                var nineBounds = ActionButtons(window, "Launch_").Select(button => BoundsInWindow(button, window)).ToList();
                ScrollViewer cardScroll = Descendants<ScrollViewer>(window).Single(scroll =>
                    Descendants<ItemsControl>(scroll).Any(items => ReferenceEquals(items, window.FindName("LauncherItems"))));
                Check(nineBounds.Select(bounds => Math.Round(bounds.Left)).Distinct().Count() == 3
                    && nineBounds.Select(bounds => Math.Round(bounds.Top)).Distinct().Count() == 3
                    && Descendants<Border>(window).Count(border => border.Width == 280 && border.ActualHeight == 108) == 9
                    && cardScroll.ScrollableHeight < 1,
                    "Nine fixture rows fit three columns and three rows at the default window size");
                Directory.CreateDirectory(imageDirectory);
                string nineCardPreview = Path.Combine(imageDirectory, "i-luncher-nine-cards-preview.png");
                bool nineCardCaptured = TrySaveScreenshot(window, nineCardPreview, 500);
                anyPreviewSaved |= nineCardCaptured;
                Check(nineCardCaptured || !File.Exists(nineCardPreview), nineCardCaptured
                    ? "Nine-card software-rendered preview contains visible UI pixels"
                    : "Black nine-card capture is reported and omitted");
                foreach (var fixture in gridFixtures) window.LauncherCards.Remove(fixture);

                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.UpdateLayout();
                await Idle(window);
                launchButtons = ActionButtons(window, "Launch_")
                    .Where(button => expectedSupportedAppIds.Any(id =>
                        AutomationProperties.GetAutomationId(button) == $"Launch_{id}"))
                    .ToList();
                bool usable = expectedSupportedAppIds.All(id =>
                {
                    Button run = launchButtons.Single(button => AutomationProperties.GetAutomationId(button) == $"Launch_{id}");
                    Rect runBounds = BoundsInWindow(run, window);
                    return run.ActualWidth >= 280 && run.ActualHeight >= 108
                           && runBounds.Left >= 0 && runBounds.Right <= window.ActualWidth;
                });
                Check(Math.Abs(window.ActualWidth - window.MinWidth) < 1
                      && Math.Abs(window.ActualHeight - window.MinHeight) < 1 && usable,
                    "Minimum window size preserves the complete launch and install-confirm card");
                var catalogCardsField = typeof(MainWindow).GetField("_allCards", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var catalogCards = (List<MainWindow.LauncherCard>)catalogCardsField.GetValue(window)!;
                string fixtureRoot = Path.GetTempPath();
                var categoryFixtures = Enumerable.Range(0, 12).Select(index => new MainWindow.LauncherCard(new ILauncher.Models.LauncherEntry
                {
                    Id = $"sidebar-fixture-{index}", Name = $"Sidebar fixture {index}", Description = "Category scroll fixture",
                    Category = $"Fixture {index:D2}", ExecutablePath = Path.Combine(fixtureRoot, $"sidebar-fixture-{index}.exe"),
                    WorkingDirectory = fixtureRoot
                })).ToList();
                catalogCards.AddRange(categoryFixtures);
                typeof(MainWindow).GetMethod("UpdateCategoryButtons", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                await Idle(window);
                ScrollViewer categoryScroll = Descendants<ScrollViewer>(window).Single(scroll =>
                    Descendants<StackPanel>(scroll).Any(panel => ReferenceEquals(panel, window.FindName("CategoryButtons"))));
                categoryScroll.ScrollToBottom();
                await Idle(window);
                Button lastCategory = Descendants<Button>((StackPanel)window.FindName("CategoryButtons")).Last();
                Rect lastCategoryBounds = BoundsInWindow(lastCategory, window);
                Rect categoryViewportBounds = BoundsInWindow(categoryScroll, window);
                Check(categoryScroll.ScrollableHeight > 0 &&
                      lastCategoryBounds.Top >= categoryViewportBounds.Top - 1 &&
                      lastCategoryBounds.Bottom <= categoryViewportBounds.Bottom + 1,
                    "Minimum window sidebar scrolls through to its last real-category row");
                catalogCards.RemoveRange(catalogCards.Count - categoryFixtures.Count, categoryFixtures.Count);
                typeof(MainWindow).GetMethod("UpdateCategoryButtons", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                categoryScroll.ScrollToTop();
                await Idle(window);
                cardScroll.ScrollToBottom();
                await Idle(window);
                Check(cardScroll.ScrollableHeight > 0 && Math.Abs(cardScroll.VerticalOffset - cardScroll.ScrollableHeight) < 1,
                    "Minimum window scrolls all cards into reach");
                cardScroll.ScrollToTop();
                await Idle(window);

                Directory.CreateDirectory(imageDirectory);
                string minimumPreview = Path.Combine(imageDirectory, "i-luncher-minimum-preview.png");
                bool minimumCaptured = TrySaveScreenshot(window, minimumPreview, 100);
                anyPreviewSaved |= minimumCaptured;
                Check(minimumCaptured || !File.Exists(minimumPreview), minimumCaptured
                    ? "Minimum-size software-rendered preview contains visible UI pixels"
                    : "Black minimum-size capture is reported and omitted");
                window.Width = 1100;
                window.Height = 600;
                window.UpdateLayout();
                await Idle(window);
                string previewPath = Path.Combine(imageDirectory, "i-luncher-preview.png");
                bool previewCaptured = TrySaveScreenshot(window, previewPath, 500);
                anyPreviewSaved |= previewCaptured;
                Check(previewCaptured || !File.Exists(previewPath), previewCaptured
                    ? "Launcher card software-rendered preview contains visible UI pixels"
                    : "Black default capture is reported and omitted");

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
                Console.WriteLine($"UI checks passed: {checks}; skipped: {skippedChecks}. " +
                    $"Timing report: {timingPath}. " +
                    (anyPreviewSaved ? $"Preview(s) saved under {imageDirectory}." : "WPF previews unavailable; see capture warnings above."));
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

    private static async Task WaitForVersion(Window window, FakeUpdateLauncherCatalog catalog,
        MainWindow.LauncherCard card)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (catalog.CheckCalls == 0 || card.VersionText != "1.0")
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Fake version check did not complete.");
            await Task.Delay(10);
            await Idle(window);
        }
    }

    private static List<Button> ActionButtons(DependencyObject root, string prefix) =>
        Descendants<Button>(root)
            .Where(button => AutomationProperties.GetAutomationId(button).StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

    private static Rect BoundsInWindow(FrameworkElement element, Window window) =>
        element.TransformToAncestor(window).TransformBounds(new Rect(new Point(), element.RenderSize));

    private static T? FindVisualAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(element); current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }
        return null;
    }

    private static bool IsVisualAncestor(DependencyObject possibleAncestor, DependencyObject element)
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(element); current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, possibleAncestor)) return true;
        }
        return false;
    }

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

    private static bool TrySaveScreenshot(Window window, string path, long minimumBrightPixels)
    {
        window.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        if (window.Content is not Visual content)
            throw new InvalidOperationException("Launcher window has no visual content to capture.");
        bitmap.Render(content);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        long brightPixels = 0;
        long nonTransparentPixels = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] > 0) nonTransparentPixels++;
            if (pixels[i + 3] > 0 && pixels[i] + pixels[i + 1] + pixels[i + 2] > 320) brightPixels++;
        }
        if (brightPixels < minimumBrightPixels)
        {
            if (File.Exists(path)) File.Delete(path);
            Console.WriteLine($"WARN Screenshot unavailable for {Path.GetFileName(path)}: software RenderTargetBitmap returned alpha={nonTransparentPixels}, visible={brightPixels}; no preview saved.");
            return false;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
        string contentSize = content is FrameworkElement element ? element.RenderSize.ToString() : "n/a";
        Console.WriteLine($"CAPTURE {Path.GetFileName(path)} content={content.GetType().Name} size={contentSize} window={width}x{height} alpha={nonTransparentPixels} bright={brightPixels}");
        return true;
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

    private sealed class ActivationProbeCatalog(ILauncher.Services.ILauncherCatalog inner)
        : ILauncher.Services.ILauncherCatalog
    {
        private int _installCalls;
        private int _launchCalls;
        private int _uninstallCalls;
        private int _deleteFilesCalls;
        public string RootDirectory => inner.RootDirectory;
        public int InstallCalls => Volatile.Read(ref _installCalls);
        public int LaunchCalls => Volatile.Read(ref _launchCalls);
        public int UninstallCalls => Volatile.Read(ref _uninstallCalls);
        public int DeleteFilesCalls => Volatile.Read(ref _deleteFilesCalls);
        public ILauncher.Services.CatalogResult Load() => inner.Load();
        public Task<string> InstallAsync(ILauncher.Models.LauncherEntry entry)
        {
            Interlocked.Increment(ref _installCalls);
            return Task.FromResult("fixture install");
        }
        public Task<string> UninstallAsync(ILauncher.Models.LauncherEntry entry)
        {
            Interlocked.Increment(ref _uninstallCalls);
            return Task.FromResult("fixture removal");
        }
        public Task<string> UninstallAsync(ILauncher.Models.LauncherEntry entry, bool deleteFiles)
        {
            if (!deleteFiles) return UninstallAsync(entry);
            Interlocked.Increment(ref _deleteFilesCalls);
            return Task.FromResult("fixture full deletion");
        }
        public void Launch(ILauncher.Models.LauncherEntry entry) =>
            Interlocked.Increment(ref _launchCalls);
        public Task LaunchAsync(ILauncher.Models.LauncherEntry entry)
        {
            Interlocked.Increment(ref _launchCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUpdateLauncherCatalog : ILauncher.Services.ILauncherCatalog,
        ILauncher.Services.ILauncherUpdateCatalog
    {
        private readonly ILauncher.Services.ILauncherCatalog _inner;
        private readonly ILauncher.Services.CatalogResult _snapshot;
        private readonly ILauncher.Models.LauncherEntry _entry;
        private int _checkCalls;
        private int _updateCalls;
        private int _launchCalls;
        private string _installedVersion = "1.0";
        private bool _updateAvailable = true;

        public FakeUpdateLauncherCatalog(ILauncher.Services.ILauncherCatalog inner, string appId)
        {
            _inner = inner;
            _snapshot = inner.Load();
            _entry = _snapshot.Entries.Single(entry => entry.Id == appId);
        }

        public string RootDirectory => _inner.RootDirectory;
        public int CheckCalls => Volatile.Read(ref _checkCalls);
        public int UpdateCalls => Volatile.Read(ref _updateCalls);
        public int LaunchCalls => Volatile.Read(ref _launchCalls);
        public ILauncher.Services.CatalogResult Load() => _snapshot;
        public Task<string> InstallAsync(ILauncher.Models.LauncherEntry entry) => _inner.InstallAsync(entry);
        public Task<string> UninstallAsync(ILauncher.Models.LauncherEntry entry) => _inner.UninstallAsync(entry);
        public void Launch(ILauncher.Models.LauncherEntry entry) => Interlocked.Increment(ref _launchCalls);
        public Task LaunchAsync(ILauncher.Models.LauncherEntry entry)
        {
            Interlocked.Increment(ref _launchCalls);
            return Task.CompletedTask;
        }

        public Task<ILauncher.Services.LauncherUpdateCheckResult> CheckForUpdatesAsync(
            System.Threading.CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _checkCalls);
            SetEntryValue("AvailableVersion", "1.1");
            SetEntryValue("OnlineInstalledVersion", _installedVersion);
            SetEntryValue("UpdateAvailable", _updateAvailable);
            var app = new ILauncher.Services.LauncherAppUpdate(_entry.Id, "1.1", null, "fixture.zip",
                new string('a', 64), 1, "DeskCal.exe", _entry.Description, _entry.Category, null);
            return Task.FromResult(new ILauncher.Services.LauncherUpdateCheckResult(
                "fixture-catalog", "v1.1", [app], false));
        }

        public Task<string> UpdateAsync(ILauncher.Models.LauncherEntry entry,
            System.Threading.CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.UpdateAvailable) throw new InvalidOperationException("No fixture update is available.");
            Interlocked.Increment(ref _updateCalls);
            _installedVersion = "1.1";
            _updateAvailable = false;
            SetEntryValue("OnlineInstalledVersion", "1.1");
            SetEntryValue("UpdateAvailable", false);
            return Task.FromResult("Fixture updated");
        }

        private void SetEntryValue(string propertyName, object? value)
        {
            PropertyInfo property = typeof(ILauncher.Models.LauncherEntry).GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(typeof(ILauncher.Models.LauncherEntry).FullName, propertyName);
            MethodInfo setter = property.GetSetMethod(nonPublic: true)
                ?? throw new MissingMethodException(typeof(ILauncher.Models.LauncherEntry).FullName, propertyName);
            setter.Invoke(_entry, [value]);
        }
    }

    private sealed class FixtureIntegratedAppProvider(ILauncher.Models.LauncherEntry entry)
        : ILauncher.Services.IIntegratedAppProvider
    {
        public IReadOnlyList<ILauncher.Models.LauncherEntry> Entries => [entry];
        public Task LaunchAsync(string appId) => throw new NotSupportedException();
    }
}
