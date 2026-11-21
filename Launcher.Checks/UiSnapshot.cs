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
                    // 두 탭을 따로: 스토어(한 줄씩, -store) · 설치됨(큰 아이콘, -installed). 처음 탭은 위 그림
                    string firstTab = window.CurrentTab;
                    Console.WriteLine($"first tab: {firstTab}");
                    foreach (string tab in new[] { "store", "installed" })
                    {
                        window.ShowTab(tab);
                        Pump(TimeSpan.FromMilliseconds(700));
                        Save((FrameworkElement)window.Content, Suffix(output, "-" + tab));
                        Console.WriteLine($"{tab}: {window.ViewTitle} · {window.VisibleCards.Count}개 · chips: " + string.Join(", ", window.CategoryButtons.Children
                            .OfType<System.Windows.Controls.Button>().Select(chip => System.Windows.Automation.AutomationProperties.GetName(chip))));
                        if (window.EmptyState.Visibility == Visibility.Visible) Console.WriteLine($"  empty: {window.EmptyStateTitle.Text} / {window.EmptyStateText.Text}");
                        // 스토어 줄을 누른 앱 페이지 (-detail): 상세 설명이 있는 첫 앱
                        if (tab == "store" && window.VisibleCards.OfType<AppCard>().OrderByDescending(card => card.App.Details is { Length: > 0 }).FirstOrDefault() is { } shown)
                        {
                            window.ShowDetail(shown);
                            Pump(TimeSpan.FromMilliseconds(1500));
                            Save((FrameworkElement)window.Content, Suffix(output, "-detail"));
                            Console.WriteLine($"detail: {shown.Name} · {shown.App.Screenshots?.Count ?? 0} screenshots · details {shown.App.Details?.Length ?? 0} chars");
                        }
                    }
                    window.ShowTab(firstTab);
                    Pump(TimeSpan.FromMilliseconds(300));
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
                    // 웹사이트 추가: ILUNCHER_SNAPSHOT_SITES=<주소,주소> 마다 열자마자 사이트에서 이름 · 아이콘을 가져온 모습 (못 가져오면 기본 아이콘이 펼쳐짐)
                    int siteIndex = 0;
                    foreach (string site in (Environment.GetEnvironmentVariable("ILUNCHER_SNAPSHOT_SITES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var siteEditor = new FavoriteEditorWindow(null, ["개발", "설비", "일정", "파일"], web: true) { ShowActivated = false, Left = -4000, Top = 0, InitialAddress = site, MaxHeight = 4000 };
                        siteEditor.Show();
                        for (int i = 0; i < 60 && (siteEditor.PathButton.IsEnabled == false || siteEditor.PathNote.Visibility != Visibility.Visible); i++) Pump(TimeSpan.FromMilliseconds(250));
                        Pump(TimeSpan.FromMilliseconds(400));
                        Save((FrameworkElement)siteEditor.Content, Suffix(output, "-site" + ++siteIndex));
                        Console.WriteLine($"site {site}: name {siteEditor.NameBox.Text} · {siteEditor.PathNote.Text}");
                        siteEditor.Close();
                    }
                    // GitHub 프로젝트 추가 창: 확인한 저장소(이 PC 의 API 서버로 실제 확인) · 열 수 없는 저장소 오류. 내 앱 삭제 창
                    var github = new GitHubClient(repository => settings.GetToken(repository), MainWindow.LauncherVersion);
                    var manager = new AppManager(github, new AppInstaller(), Path.Combine(Path.GetTempPath(), "i-luncher-snapshot-downloads"), () => settings.DefaultHost);
                    foreach (var (repository, suffix) in new[] { ("itvtt/Capture", "-github"), ("torvalds/linux", "-github-error") })
                    {
                        var projectWindow = new GitHubProjectWindow(manager, settings.DefaultHost, ["개발", "도구", "설비", "일정", "파일"], _ => null)
                        { ShowActivated = false, Left = -4000, Top = 0, MaxHeight = 4000 };
                        projectWindow.Show();
                        projectWindow.RepoBox.Text = repository;
                        projectWindow.CheckButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        for (int i = 0; i < 60 && projectWindow.Details.Visibility != Visibility.Visible && projectWindow.ErrorText.Visibility != Visibility.Visible; i++)
                            Pump(TimeSpan.FromMilliseconds(250));
                        Pump(TimeSpan.FromMilliseconds(300));
                        Save((FrameworkElement)projectWindow.Content, Suffix(output, suffix));
                        Console.WriteLine($"github {repository}: details {projectWindow.Details.Visibility} · add {projectWindow.AddButton.IsEnabled} · " +
                                          (projectWindow.ErrorText.Visibility == Visibility.Visible ? "error: " + projectWindow.ErrorText.Text : projectWindow.PreviewVersion.Text + " " + projectWindow.PreviewFile.Text));
                        projectWindow.Close();
                    }
                    // 비공개라 읽지 못했을 때의 모습: 이 프로젝트의 토큰 칸
                    {
                        var tokenProject = new GitHubProjectWindow(manager, settings.DefaultHost, ["개발", "도구"], _ => null)
                        { ShowActivated = false, Left = -4000, Top = 0, MaxHeight = 4000 };
                        tokenProject.Show();
                        tokenProject.RepoBox.Text = "owner/private-tool";
                        tokenProject.ErrorText.Text = "owner/private-tool 을(를) 읽지 못했습니다. 비공개 저장소면 이 프로젝트의 토큰을 넣고 다시 확인하세요.";
                        tokenProject.ErrorText.Visibility = Visibility.Visible;
                        tokenProject.TokenRow.Visibility = Visibility.Visible;
                        Pump(TimeSpan.FromMilliseconds(400));
                        Save((FrameworkElement)tokenProject.Content, Suffix(output, "-github-token"));
                        tokenProject.Close();
                    }
                    window.SelectFilter("all");
                    Pump(TimeSpan.FromMilliseconds(300));
                    // 내 앱 편집: 기본 아이콘을 고른 모습 (이름 · 분류 · 설명 · 아이콘만)
                    if (window.VisibleCards.FirstOrDefault() is { } myAppCard)
                    {
                        var editing = myAppCard.App.Clone();
                        editing.Icon = "builtin:chart";
                        editing.Personal = true;
                        var editWindow = new GitHubProjectWindow(manager, settings.DefaultHost, ["개발", "도구", "설비", "일정", "파일"], _ => null, editing)
                        { ShowActivated = false, Left = -4000, Top = 0, MaxHeight = 4000, RepositoryIcon = myAppCard.Icon };
                        editWindow.Show();
                        Pump(TimeSpan.FromSeconds(3));
                        Save((FrameworkElement)editWindow.Content, Suffix(output, "-myapp-edit"));
                        editWindow.Body.ScrollToEnd();
                        Pump(TimeSpan.FromMilliseconds(400));
                        Save((FrameworkElement)editWindow.Content, Suffix(output, "-myapp-edit-end"));
                        Console.WriteLine($"my app edit: {editWindow.NameBox.Text} · {editWindow.PreviewVersion.Text}");
                        editWindow.Close();
                    }
                    if (window.VisibleCards.FirstOrDefault(card => card.IsInstalled) is { } installedCard)
                    {
                        var deleteWindow = new DeleteProjectWindow(installedCard) { ShowActivated = false, Left = -4000, Top = 0 };
                        deleteWindow.Show();
                        Pump(TimeSpan.FromMilliseconds(500));
                        Save((FrameworkElement)deleteWindow.Content, Suffix(output, "-delete"));
                        deleteWindow.Close();
                        // 프로젝트 토큰 (앱 우클릭 → 토큰…)
                        if (GitHubRepository.Parse(installedCard.App.Repository) is { } tokenRepository)
                        {
                            var tokenWindow = new TokenWindow(installedCard, tokenRepository, hasToken: true, _ => Task.CompletedTask) { ShowActivated = false, Left = -4000, Top = 0 };
                            tokenWindow.Show();
                            Pump(TimeSpan.FromMilliseconds(500));
                            Save((FrameworkElement)tokenWindow.Content, Suffix(output, "-token"));
                            tokenWindow.Close();
                        }
                    }
                    // 설정 (오른쪽 화면)
                    window.SettingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Pump(TimeSpan.FromSeconds(1));
                    Save((FrameworkElement)window.Content, Suffix(output, "-settings"));
                    // 제목줄의 로고·이름 = 홈: 설정 화면에서도, 앱 페이지에서도 전체 앱으로
                    window.HomeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Pump(TimeSpan.FromMilliseconds(500));
                    Console.WriteLine($"home from settings: {window.ViewTitle} · list {window.StorePane.Visibility} · page {window.PagePane.Visibility}");
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

    /// <summary>스크린샷 자리 그림 (색 판 + 줄 몇 개)</summary>
    private static string ShotPng(string path, Color color)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 640, 400));
            for (int i = 0; i < 5; i++) context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), null, new Rect(40, 60 + i * 60, 360 - i * 40, 22), 6, 6);
        }
        var bitmap = new RenderTargetBitmap(640, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
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
