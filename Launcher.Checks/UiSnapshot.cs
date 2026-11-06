using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ILauncher;
using ILauncher.Services;

/// <summary>
/// Launcher.Checks --ui-snapshot &lt;데이터 폴더&gt; &lt;png&gt; [초]: 실제 MainWindow 를 띄워 몇 초 뒤 화면을 PNG 로 저장하고
/// 카드 상태를 출력. 같은 폴더에 앱 추가 창(-editor.png)과 설정 창(-settings.png)도 저장.
/// 원격 세션이 끊겨 있으면 WPF 가 아무것도 그리지 못해 그림은 비고, 출력한 값으로만 확인할 수 있음.
/// </summary>
static class UiSnapshot
{
    public static int Run(string dataDirectory, string output, int seconds)
    {
        int result = 1;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var settings = LauncherSettings.Load(dataDirectory);
            var window = new MainWindow(settings) { ShowActivated = false, Left = -4000, Top = 0 };
            window.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    // ILUNCHER_SNAPSHOT_DEMO=1: 처음 세 카드를 업데이트 있음 / 설치됨 / 받는 중 상태로 바꿔 그림
                    if (Environment.GetEnvironmentVariable("ILUNCHER_SNAPSHOT_DEMO") == "1")
                    {
                        var demo = ((IEnumerable<AppCard>)window.LauncherItems.ItemsSource).ToList();
                        InstalledApp Installed(AppCard card, string version) => new(card.App.EffectiveInstallName, @"C:\demo\" + card.App.EffectiveInstallName, null,
                            ReleaseVersion.Parse(version), "설치한 릴리스", null, true, null, null);
                        if (demo.Count > 0) demo[0].Installed = Installed(demo[0], "1.0.9");
                        if (demo.Count > 1 && demo[1].Latest?.Version is { } latest) demo[1].Installed = Installed(demo[1], latest.ToString());
                        if (demo.Count > 2) { demo[2].BeginWork("받는 중…"); demo[2].ReportWork(new AppProgress("FolderCompareWPF-win-x64.zip (3.1 MB) 받는 중… 42%", 0.42)); }
                        window.UpdateLayout();
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    }
                    Save((FrameworkElement)window.Content, output);
                    Console.WriteLine($"title: {window.SectionTitle.Text} · {window.CountText.Text} · status: {window.StatusText.Text}");
                    Console.WriteLine($"catalog: {window.CatalogSourceText.Text}");
                    if (window.ErrorPanel.Visibility == Visibility.Visible) Console.WriteLine($"error: {window.ErrorText.Text}");
                    if (window.EmptyState.Visibility == Visibility.Visible) Console.WriteLine($"empty: {window.EmptyStateTitle.Text} / {window.EmptyStateText.Text}");
                    foreach (AppCard card in (IEnumerable<AppCard>)window.LauncherItems.ItemsSource)
                        Console.WriteLine($"card [{card.Category}] {card.Name}: {card.VersionText} | {card.DescriptionText} | tooltip: {card.VersionToolTip.Replace(Environment.NewLine, " / ")}");
                    Console.WriteLine($"counts: all {window.AllCountText.Text}, installed {window.InstalledCountText.Text}, updates '{window.UpdatesCountText.Text}'");

                    using var github = new GitHubClient(settings.GetToken, "checks");
                    var manager = new AppManager(github, new AppInstaller(), Path.Combine(dataDirectory, "downloads"), () => settings.DefaultHost);
                    SaveWindow(new AppEditorWindow(null, manager, settings.DefaultHost, ["도구", "일정", "파일 공유"]), Suffix(output, "-editor"));
                    SaveWindow(new SettingsWindow(settings, "1.0.0"), Suffix(output, "-settings"));
                    result = 0;
                }
                catch (Exception error)
                {
                    Console.WriteLine("snapshot failed: " + error);
                }
                finally
                {
                    window.Close();
                    app.Shutdown();
                }
            };
            timer.Start();
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static string Suffix(string path, string suffix) =>
        Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + suffix + Path.GetExtension(path));

    private static void SaveWindow(Window dialog, string path)
    {
        dialog.ShowActivated = false;
        dialog.Left = -4000;
        dialog.Top = 0;
        dialog.Show();
        dialog.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Save((FrameworkElement)dialog.Content, path);
        dialog.Close();
    }

    private static void Save(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // 창 배경은 Window 에 있으므로 내용 아래에 같은 색을 깔아 그림
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = (Window.GetWindow(root)?.Background as SolidColorBrush) ?? Brushes.Black;
            context.DrawRectangle(background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        Console.WriteLine($"saved {path} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
    }
}
