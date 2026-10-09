using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher;

public partial class MainWindow
{
    private INoticeService? _noticeService;
    private IReadOnlyList<Notice> _notices = [];
    private readonly CancellationTokenSource _noticeCancellation = new();
    private DispatcherTimer? _noticeTimer;
    private Task? _noticeRefreshTask;
    private NoticeWindow? _noticeWindow;
    private bool _noticeLoadFailed;
    private bool _noticesClosed;

    private void InitializeNotices(INoticeService? service)
    {
        _noticeService = service;
        _notices = service?.CachedNotices ?? [];
        NoticeButton.Visibility = service is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateNoticeIndicators();
    }

    private void StartNotices()
    {
        if (_noticeService is null || _noticesClosed) return;
        if (_noticeTimer is null)
        {
            _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _noticeTimer.Tick += NoticeTimer_Tick;
            _noticeTimer.Start();
        }
        _ = RefreshNoticesAsync();
    }

    private void NoticeTimer_Tick(object? sender, EventArgs e) => _ = RefreshNoticesAsync();

    private Task RefreshNoticesAsync()
    {
        if (_noticeService is null || _noticesClosed) return Task.CompletedTask;
        if (_noticeRefreshTask is { IsCompleted: false }) return _noticeRefreshTask;
        return _noticeRefreshTask = RefreshNoticesCoreAsync();
    }

    private async Task RefreshNoticesCoreAsync()
    {
        try
        {
            var notices = await _noticeService!.RefreshAsync(_noticeCancellation.Token);
            if (_noticesClosed) return;
            _notices = notices;
            _noticeLoadFailed = false;
        }
        catch (OperationCanceledException) when (_noticeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (_noticesClosed) return;
            _noticeLoadFailed = true;
            _notices = _noticeService!.CachedNotices;
        }
        UpdateNoticeIndicators();
        _noticeWindow?.SetNotices(_notices, _noticeLoadFailed);
    }

    private void UpdateNoticeIndicators()
    {
        int unread = _noticeService is null ? 0 : _notices.Count(notice => !_noticeService.IsRead(notice));
        NoticeUnreadDot.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(NoticeButton, unread > 0 ? $"공지, 새 공지 {unread}개" : "공지");
        NoticeButton.ToolTip = unread > 0 ? $"새 공지 {unread}개" : "공지";
        Notice? important = LatestUnreadImportant();
        NoticeBanner.Visibility = important is null ? Visibility.Collapsed : Visibility.Visible;
        NoticeBannerTitle.Text = important?.Title ?? string.Empty;
        NoticeBannerTitle.ToolTip = important?.Title;
    }

    private Notice? LatestUnreadImportant() => _noticeService is null ? null :
        _notices.FirstOrDefault(notice => notice.Important && !_noticeService.IsRead(notice));

    private void NoticeButton_Click(object sender, RoutedEventArgs e) => ShowNotices(null);

    private void NoticeBanner_Click(object sender, RoutedEventArgs e) => ShowNotices(LatestUnreadImportant());

    private void ShowNotices(Notice? selected)
    {
        if (_noticeService is null) return;
        if (_noticeWindow is not null)
        {
            if (selected is not null) _noticeWindow.SelectNotice(selected.Id);
            _noticeWindow.Activate();
            return;
        }
        _noticeWindow = new NoticeWindow(_notices, _noticeLoadFailed, notice =>
        {
            _noticeService.MarkRead(notice);
            UpdateNoticeIndicators();
        }, RefreshNoticesAsync) { Owner = this };
        _noticeWindow.Closed += (_, _) => _noticeWindow = null;
        selected ??= _notices.FirstOrDefault(notice => !_noticeService.IsRead(notice));
        if (selected is not null) _noticeWindow.SelectNotice(selected.Id);
        _noticeWindow.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        _noticesClosed = true;
        _noticeTimer?.Stop();
        if (_noticeTimer is not null) _noticeTimer.Tick -= NoticeTimer_Tick;
        _noticeCancellation.Cancel();
        _noticeCancellation.Dispose();
        base.OnClosed(e);
    }
}
