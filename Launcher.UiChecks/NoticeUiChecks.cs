using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using ILauncher;
using ILauncher.Models;
using ILauncher.Services;

internal static class NoticeUiChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var important = new Notice("maintenance", 1, "서비스 안내", "첫 번째 공지 본문", DateTimeOffset.UtcNow.AddHours(-1), true);
        var other = new Notice("update", 1, "업데이트 안내", "두 번째 공지 본문", DateTimeOffset.UtcNow.AddHours(-2), false);
        var service = new FakeNoticeService { CachedNotices = new[] { important, other } };
        var window = new MainWindow(new EmptyCatalog(), noticeService: service)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000
        };
        try
        {
            check(Control<Border>(window, "NoticeBanner").Visibility == Visibility.Visible
                  && Control<TextBlock>(window, "NoticeBannerTitle").Text == important.Title,
                "Cached important notice is visible before the first network refresh");
            window.Show();
            await Idle(window);
            check(service.RefreshCount == 1 && Control<Ellipse>(window, "NoticeUnreadDot").Visibility == Visibility.Visible,
                "Startup fetches notices independently and shows unread state");

            Click(window, "NoticeButton");
            await Idle(window);
            var history = window.OwnedWindows.OfType<NoticeWindow>().Single();
            check(Control<TextBox>(history, "NoticeBody").Text == important.Body
                  && service.IsRead(important) && !service.IsRead(other),
                "Opening history displays and marks only the selected notice read");
            check(Control<Border>(window, "NoticeBanner").Visibility == Visibility.Collapsed
                  && Control<Ellipse>(window, "NoticeUnreadDot").Visibility == Visibility.Visible,
                "Reading the important notice removes its banner while other unread notices remain");

            history.SelectNotice(other.Id);
            check(service.IsRead(other) && Control<Ellipse>(window, "NoticeUnreadDot").Visibility == Visibility.Collapsed
                  && Control<TextBox>(history, "NoticeBody").Text == other.Body,
                "Selecting another notice marks it read and retains its full body");
            history.Close();

            var revised = important with { Revision = 2, Body = "수정된 공지 본문" };
            service.CachedNotices = new[] { revised, other };
            Click(window, "RefreshButton");
            await Idle(window);
            check(!service.IsRead(revised) && Control<Border>(window, "NoticeBanner").Visibility == Visibility.Visible,
                "A higher notice revision becomes unread after refresh");

            service.Handler = _ => Task.FromException<IReadOnlyList<Notice>>(new HttpRequestException("Offline fixture"));
            Click(window, "RefreshButton");
            await Idle(window);
            check(Control<Border>(window, "NoticeBanner").Visibility == Visibility.Visible,
                "An offline notice refresh preserves the last cached banner");
            Click(window, "NoticeButton");
            await Idle(window);
            history = window.OwnedWindows.OfType<NoticeWindow>().Single();
            check(Control<TextBox>(history, "NoticeBody").Text == revised.Body
                  && Control<TextBlock>(history, "StatusText").Text == "최근에 저장된 공지입니다.",
                "Offline history retains cached content and explains its status");
            history.Close();

            CancellationToken pendingToken = default;
            service.Handler = async cancellationToken =>
            {
                pendingToken = cancellationToken;
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return service.CachedNotices;
            };
            Click(window, "RefreshButton");
            await Idle(window);
            check(pendingToken.CanBeCanceled && !pendingToken.IsCancellationRequested,
                "Notice refresh receives the launcher lifetime cancellation token");
            window.Close();
            await Idle(window);
            check(pendingToken.IsCancellationRequested,
                "Closing the launcher cancels a pending notice request");
        }
        finally
        {
            foreach (Window owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            window.Close();
        }

        var emptyHistory = new NoticeWindow([], true, _ => throw new InvalidOperationException(), () => Task.CompletedTask)
        { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            emptyHistory.Show();
            await Idle(emptyHistory);
            check(Control<TextBlock>(emptyHistory, "StatusText").Text.Contains("불러오지 못했습니다", StringComparison.Ordinal),
                "A first-run feed failure shows a clear retry message");
            emptyHistory.SetNotices([], false);
            check(Control<TextBlock>(emptyHistory, "StatusText").Text == "아직 공지가 없습니다.",
                "An empty published feed has a simple empty state");
        }
        finally { emptyHistory.Close(); }
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Click(Window window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Idle(DispatcherObject element) => await element.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class FakeNoticeService : INoticeService
    {
        private readonly Dictionary<string, int> _read = new();
        public IReadOnlyList<Notice> CachedNotices { get; set; } = [];
        public Func<CancellationToken, Task<IReadOnlyList<Notice>>>? Handler { get; set; }
        public int RefreshCount { get; private set; }
        public Task<IReadOnlyList<Notice>> RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return Handler?.Invoke(cancellationToken) ?? Task.FromResult(CachedNotices);
        }
        public bool IsRead(Notice notice) => _read.GetValueOrDefault(notice.Id) >= notice.Revision;
        public void MarkRead(Notice notice) => _read[notice.Id] = Math.Max(_read.GetValueOrDefault(notice.Id), notice.Revision);
    }

    private sealed class EmptyCatalog : ILauncherCatalog
    {
        public string RootDirectory => Path.GetTempPath();
        public CatalogResult Load() => new([], []);
        public Task<string> InstallAsync(LauncherEntry entry) => throw new NotSupportedException();
        public Task<string> UninstallAsync(LauncherEntry entry) => throw new NotSupportedException();
        public void Launch(LauncherEntry entry) => throw new NotSupportedException();
        public Task LaunchAsync(LauncherEntry entry) => throw new NotSupportedException();
    }
}
