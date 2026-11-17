using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ILauncher;
using ILauncher.Services;

/// <summary>
/// Launcher.Checks --ui-snapshot &lt;데이터 폴더&gt; &lt;png&gt; [초]: 실제 MainWindow 를 띄워 몇 초 뒤 화면을 PNG 로 저장하고
/// 카드 상태를 출력. 같은 폴더에 즐겨찾기(-favorites.png), 트레이 창(-tray.png), 설정 화면(-settings.png)도 저장.
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
            // ILUNCHER_SNAPSHOT_SIZE=1920x1040: 넓은 창(최대화)으로 그림
            if (Environment.GetEnvironmentVariable("ILUNCHER_SNAPSHOT_SIZE")?.Split('x') is [var width, var height]
                && double.TryParse(width, out double w) && double.TryParse(height, out double h))
                (window.Width, window.Height) = (w, h);
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
                        var demo = window.VisibleCards.ToList();
                        InstalledApp Installed(AppCard card, string version) => new(card.App.EffectiveInstallName, @"C:\demo\" + card.App.EffectiveInstallName, null,
                            ReleaseVersion.Parse(version), "설치한 릴리스", null, true, null, null);
                        if (demo.Count > 0) demo[0].Installed = Installed(demo[0], "1.0.9");
                        if (demo.Count > 1 && demo[1].Latest?.Version is { } latest) demo[1].Installed = Installed(demo[1], latest.ToString());
                        if (demo.Count > 2) { demo[2].BeginWork("받는 중…"); demo[2].ReportWork(new AppProgress("Compare-win-x64.zip (3.1 MB) 받는 중… 42%", 0.42)); }
                        window.UpdateLayout();
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    }
                    Save((FrameworkElement)window.Content, output);
                    // 아래로 내린 화면 (앱 카드 줄)
                    window.CardScrollViewer.ScrollToEnd();
                    Pump(TimeSpan.FromMilliseconds(500));
                    Save((FrameworkElement)window.Content, Suffix(output, "-scrolled"));
                    window.CardScrollViewer.ScrollToHome();
                    // 앱 페이지 (카드를 누르면): ILUNCHER_SNAPSHOT_DETAIL=<앱 id> 이면 그 앱, 없으면 처음 카드. 스크린샷을 받을 때까지 잠시 기다림
                    string? detailId = Environment.GetEnvironmentVariable("ILUNCHER_SNAPSHOT_DETAIL");
                    if (window.VisibleCards.FirstOrDefault(card => detailId is null || card.App.Id == detailId) is { } detailCard)
                    {
                        // ILUNCHER_SNAPSHOT_SHOTS=<이름,이름>: 앱 목록은 그대로 두고 이 화면에서만 스크린샷을 넣어 봄
                        if (Environment.GetEnvironmentVariable("ILUNCHER_SNAPSHOT_SHOTS") is { Length: > 0 } shots)
                        {
                            var app = detailCard.App.Clone();
                            app.Screenshots = [.. shots.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                            detailCard.App = app;
                        }
                        window.ShowDetail(detailCard);
                        Pump(TimeSpan.FromSeconds(4));
                        Save((FrameworkElement)window.Content, Suffix(output, "-detail"));
                        Console.WriteLine($"detail: {detailCard.Name} · shots {window.DetailShots.Items.Count}");
                        // 더 보기 메뉴 (따로 뜨는 창이라 메뉴만 그림)
                        var menu = window.DetailMoreButton.ContextMenu;
                        menu.PlacementTarget = window.DetailMoreButton;
                        menu.IsOpen = true;
                        Pump(TimeSpan.FromSeconds(1));
                        Save(menu, Suffix(output, "-detail-menu"));
                        menu.IsOpen = false;
                        window.DetailBackButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    }
                    Console.WriteLine($"title: {window.ViewTitle} · {window.VisibleCards.Count}개 · status: {window.StatusText.Text}");
                    Console.WriteLine($"catalog: {window.CatalogSourceText.Text}");
                    if (window.ErrorPanel.Visibility == Visibility.Visible) Console.WriteLine($"error: {window.ErrorText.Text}");
                    if (window.EmptyState.Visibility == Visibility.Visible) Console.WriteLine($"empty: {window.EmptyStateTitle.Text} / {window.EmptyStateText.Text}");
                    foreach (AppCard card in window.VisibleCards)
                        Console.WriteLine($"card [{card.Category}] {card.Name}: {card.VersionText} | {card.DescriptionText} | tooltip: {card.VersionToolTip.Replace(Environment.NewLine, " / ")}");
                    Console.WriteLine("chips: " + string.Join(", ", window.CategoryButtons.Children.OfType<System.Windows.Controls.Button>()
                        .Select(chip => System.Windows.Automation.AutomationProperties.GetName(chip))));
                    Console.WriteLine($"dock: {window.DockFavorites.Items.Count} favorites, {window.DockRunning.Items.Count} running");

                    // 트레이 창 (트레이 아이콘을 한 번 누르면 뜨는 작은 창)
                    var quick = new TrayPopup(window) { ShowActivated = false, Left = -4000, Top = 0 };
                    quick.Refresh();
                    quick.Show();
                    Pump(TimeSpan.FromSeconds(1));
                    Save((FrameworkElement)quick.Content, Suffix(output, "-tray"));
                    Console.WriteLine($"tray: {quick.Items.Items.Count} cells, {quick.Updates.Items.Count} updates");
                    quick.CloseForGood();

                    // 즐겨찾기 화면과 추가 창 (설치된 프로그램 목록은 늦게 채워지므로 잠시 기다림)
                    window.SelectFilter("favorites");
                    Pump(TimeSpan.FromSeconds(1));
                    Save((FrameworkElement)window.Content, Suffix(output, "-favorites"));
                    Console.WriteLine($"favorites: {window.ViewTitle} · {window.VisibleCards.Count}개 앱");
                    var favoriteEditor = new FavoriteEditorWindow(null, ["개발", "설비", "일정", "파일"]) { ShowActivated = false, Left = -4000, Top = 0 };
                    favoriteEditor.Show();
                    Pump(TimeSpan.FromSeconds(3));
                    Save((FrameworkElement)favoriteEditor.Content, Suffix(output, "-favorite-editor"));
                    favoriteEditor.Close();
                    // 설정 (오른쪽 화면)
                    window.SettingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Pump(TimeSpan.FromSeconds(1));
                    Save((FrameworkElement)window.Content, Suffix(output, "-settings"));
                    // 제목줄의 로고·이름 = 홈: 설정 화면에서도, 앱 페이지에서도 전체 앱으로
                    window.HomeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Pump(TimeSpan.FromMilliseconds(500));
                    Console.WriteLine($"home from settings: {window.ViewTitle} · list {window.StorePane.Visibility} · page {window.PagePane.Visibility}");
                    if (window.VisibleCards.FirstOrDefault() is { } homeCard)
                    {
                        window.ShowDetail(homeCard);
                        window.HomeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Pump(TimeSpan.FromMilliseconds(500));
                        Console.WriteLine($"home from app page: {window.ViewTitle} · list {window.StorePane.Visibility} · app page {window.DetailPane.Visibility}");
                    }
                    // 관리자 메뉴 (마스터 계정만 제목 표시줄에 보임, 창 전체). ILUNCHER_SNAPSHOT_MANAGER=1 이면 그 화면도 그림 (이 PC 서버에 연결함)
                    Console.WriteLine($"api manager: {window.ApiManagerButton.Visibility}");
                    if (Environment.GetEnvironmentVariable("ILUNCHER_SNAPSHOT_MANAGER") == "1" && window.ApiManagerButton.Visibility == Visibility.Visible)
                    {
                        window.ApiManagerButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Pump(TimeSpan.FromSeconds(6));
                        Save((FrameworkElement)window.Content, Suffix(output, "-manager"));
                        if (window.PageContent.Content is ApiManager.ManagerView view)
                        {
                            foreach (var (tab, name) in new[] { (2, "-manager-releases"), (3, "-manager-runners"), (1, "-manager-apps") })
                            {
                                view.Nav.SelectedIndex = tab;
                                Pump(TimeSpan.FromSeconds(tab == 3 ? 8 : 3));
                                Save((FrameworkElement)window.Content, Suffix(output, name));
                                if (tab != 3) continue;
                                // 러너 추가 칸 (저장소를 고르면 서버가 폴더·이름·.env 를 채움)
                                view.AddRunnerButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                view.NewRunnerRepositoryBox.Text = Environment.GetEnvironmentVariable("ILUNCHER_SNAPSHOT_RUNNER_REPOSITORY") ?? "";
                                view.NewRunnerRepositoryBox.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                                    PresentationSource.FromVisual(view)!, 0, System.Windows.Input.Key.Enter) { RoutedEvent = UIElement.KeyDownEvent });
                                Pump(TimeSpan.FromSeconds(4));
                                Save((FrameworkElement)window.Content, Suffix(output, "-manager-runner-add"));
                            }
                            // 카드의 "관리자 메뉴에서 편집": 앱 목록에서 그 앱 (분류 칸과 고를 수 있는 분류)
                            if (window.VisibleCards.LastOrDefault() is { } editCard)
                            {
                                view.ShowApp(editCard.App.Id);
                                Pump(TimeSpan.FromSeconds(2));
                                Save((FrameworkElement)window.Content, Suffix(output, "-manager-app"));
                                Console.WriteLine($"admin menu app: {(view.AppsList.SelectedItem as ApiManager.AppRow)?.Title} · chips {view.CategoryChips.Children.Count}");
                            }
                        }
                    }
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

    /// <summary>화면 스레드를 막지 않고 잠시 기다림 (비동기로 채우는 목록·아이콘)</summary>
    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
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
        // 창 배경은 Window 에 있으므로 내용 아래에 같은 색을 먼저 깔고, 내용은 직접 그림
        // (VisualBrush 로 그리면 화면이 잠겼거나 원격 세션이 끊겼을 때 비어 나옴)
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = (Window.GetWindow(root)?.Background as SolidColorBrush) ?? Brushes.Black;
            context.DrawRectangle(background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(visual);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        Console.WriteLine($"saved {path} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
    }
}
