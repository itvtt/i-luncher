using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TerminalNotes.Services;

/// <summary>
/// Owns the notification-area icon and forwards its commands to the WPF UI thread.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const string IconResourceUri = "pack://application:,,,/TerminalNotes;component/Assets/AppIcon.ico";

    private readonly Dispatcher _dispatcher;
    private readonly Action _restoreWindow;
    private readonly Action _closeAllTerminals;
    private readonly Action _exitApplication;
    private readonly System.Drawing.Icon _icon;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly System.Windows.Forms.ToolStripMenuItem _openItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _closeAllItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _exitItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _taskNotificationsItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _notificationsEnabledItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _notificationsOffItem;
    private readonly Dictionary<int, System.Windows.Forms.ToolStripMenuItem> _snoozeItems = [];
    private readonly System.Windows.Forms.ToolStripMenuItem _customSnoozeItem;
    private readonly Queue<QueuedBalloon> _balloonQueue = new();
    private readonly DispatcherTimer _balloonFallbackTimer;

    private Func<TaskNotificationPreferences>? _getNotificationPreferences;
    private Action<bool>? _setNotificationsEnabled;
    private Action<int>? _snoozeNotifications;
    private Action? _customSnooze;
    private bool _balloonIsShowing;
    private bool _hiddenNoticeShown;
    private bool _disposed;

    public TrayIconService(
        Action restoreWindow,
        Action closeAllTerminals,
        Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(restoreWindow);
        ArgumentNullException.ThrowIfNull(closeAllTerminals);
        ArgumentNullException.ThrowIfNull(exitApplication);

        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _restoreWindow = restoreWindow;
        _closeAllTerminals = closeAllTerminals;
        _exitApplication = exitApplication;

        _icon = LoadIcon();
        try
        {
            _openItem = new System.Windows.Forms.ToolStripMenuItem("열기");
            _closeAllItem = new System.Windows.Forms.ToolStripMenuItem("터미널 모두 닫기")
            {
                Enabled = false
            };
            _exitItem = new System.Windows.Forms.ToolStripMenuItem("프로그램 종료");

            _openItem.Click += OpenItem_Click;
            _closeAllItem.Click += CloseAllItem_Click;
            _exitItem.Click += ExitItem_Click;

            _taskNotificationsItem = new System.Windows.Forms.ToolStripMenuItem("\uC791\uC5C5 \uC54C\uB9BC");
            _notificationsEnabledItem = new System.Windows.Forms.ToolStripMenuItem("\uC54C\uB9BC \uC0AC\uC6A9") { CheckOnClick = true };
            _notificationsOffItem = new System.Windows.Forms.ToolStripMenuItem("\uC601\uAD6C \uB044\uAE30");
            _customSnoozeItem = new System.Windows.Forms.ToolStripMenuItem("\uC0AC\uC6A9\uC790 \uC9C0\uC815 \uC2DC\uAC04\u2026");
            _balloonFallbackTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _balloonFallbackTimer.Tick += BalloonFallbackTimer_Tick;
            _notificationsEnabledItem.Click += NotificationsEnabledItem_Click;
            _notificationsOffItem.Click += NotificationsOffItem_Click;
            _customSnoozeItem.Click += CustomSnoozeItem_Click;
            _taskNotificationsItem.DropDownItems.Add(_notificationsEnabledItem);
            _taskNotificationsItem.DropDownItems.Add(_notificationsOffItem);
            _taskNotificationsItem.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
            foreach (int minutes in new[] { 5, 10, 30, 60 })
            {
                var item = new System.Windows.Forms.ToolStripMenuItem($"{minutes}\uBD84 \uB3D9\uC548 \uC54C\uB9BC \uBBF8\uB8E8\uAE30");
                item.Tag = minutes;
                item.Click += SnoozeItem_Click;
                _snoozeItems.Add(minutes, item);
                _taskNotificationsItem.DropDownItems.Add(item);
            }
            _taskNotificationsItem.DropDownItems.Add(_customSnoozeItem);
            _taskNotificationsItem.DropDownOpening += TaskNotificationsItem_DropDownOpening;

            _menu = new System.Windows.Forms.ContextMenuStrip();
            _menu.Items.AddRange(
            [
                _openItem,
                _closeAllItem,
                _taskNotificationsItem,
                new System.Windows.Forms.ToolStripSeparator(),
                _exitItem
            ]);
            _menu.Opening += Menu_Opening;

            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                ContextMenuStrip = _menu,
                Icon = _icon,
                Text = "Terminal Notes · 열린 터미널 없음"
            };
            _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
            _notifyIcon.BalloonTipClicked += NotifyIcon_BalloonTipClicked;
            _notifyIcon.BalloonTipClosed += NotifyIcon_BalloonTipClosed;
            _notifyIcon.Visible = true;
        }
        catch
        {
            _openItem?.Dispose();
            _closeAllItem?.Dispose();
            _exitItem?.Dispose();
            _taskNotificationsItem?.Dispose();
            _notificationsEnabledItem?.Dispose();
            _notificationsOffItem?.Dispose();
            _customSnoozeItem?.Dispose();
            foreach (var item in _snoozeItems.Values) item.Dispose();
            _balloonFallbackTimer?.Stop();
            _menu?.Dispose();
            _notifyIcon?.Dispose();
            _icon.Dispose();
            throw;
        }
    }

    public void UpdateSessionCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        RunOnDispatcher(() =>
        {
            _closeAllItem.Enabled = count > 0;
            _notifyIcon.Text = count == 0
                ? "Terminal Notes · 열린 터미널 없음"
                : $"Terminal Notes · 열린 터미널 {count}개";
        });
    }

    public void ShowHiddenNotice()
    {
        RunOnDispatcher(() =>
        {
            if (_hiddenNoticeShown)
            {
                return;
            }

            _hiddenNoticeShown = true;
            EnqueueBalloon("Terminal Notes", "\uCC3D\uC744 \uB2EB\uC544\uB3C4 \uD2B8\uB808\uC774\uC5D0\uC11C \uACC4\uC18D \uC2E4\uD589\uB429\uB2C8\uB2E4.", System.Windows.Forms.ToolTipIcon.Info, 4000, () => true);
        });
    }

    internal void ConfigureTaskNotifications(
        Func<TaskNotificationPreferences> getPreferences,
        Action<bool> setEnabled,
        Action<int> snooze,
        Action customMinutes)
    {
        ArgumentNullException.ThrowIfNull(getPreferences);
        ArgumentNullException.ThrowIfNull(setEnabled);
        ArgumentNullException.ThrowIfNull(snooze);
        ArgumentNullException.ThrowIfNull(customMinutes);
        RunOnDispatcher(() =>
        {
            _getNotificationPreferences = getPreferences;
            _setNotificationsEnabled = setEnabled;
            _snoozeNotifications = snooze;
            _customSnooze = customMinutes;
            RefreshTaskNotificationMenu();
        });
    }

    public void ShowTaskNotification(string title, string body, bool warning, Func<bool> stillRelevant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ArgumentNullException.ThrowIfNull(stillRelevant);
        RunOnDispatcher(() => EnqueueBalloon(title, body,
            warning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info,
            5000, stillRelevant, isTaskNotification: true));
    }

    public void CancelTaskNotifications()
    {
        RunOnDispatcher(() =>
        {
            var retained = _balloonQueue.Where(item => !item.IsTaskNotification).ToArray();
            _balloonQueue.Clear();
            foreach (var item in retained) _balloonQueue.Enqueue(item);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.DoubleClick -= NotifyIcon_DoubleClick;
        _notifyIcon.BalloonTipClicked -= NotifyIcon_BalloonTipClicked;
        _notifyIcon.BalloonTipClosed -= NotifyIcon_BalloonTipClosed;
        _menu.Opening -= Menu_Opening;
        _taskNotificationsItem.DropDownOpening -= TaskNotificationsItem_DropDownOpening;
        _openItem.Click -= OpenItem_Click;
        _closeAllItem.Click -= CloseAllItem_Click;
        _exitItem.Click -= ExitItem_Click;
        _notificationsEnabledItem.Click -= NotificationsEnabledItem_Click;
        _notificationsOffItem.Click -= NotificationsOffItem_Click;
        _customSnoozeItem.Click -= CustomSnoozeItem_Click;
        foreach (var item in _snoozeItems.Values) item.Click -= SnoozeItem_Click;
        _balloonFallbackTimer.Stop();
        _balloonQueue.Clear();

        // Hiding first removes the shell icon immediately instead of waiting for finalization.
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        GC.SuppressFinalize(this);
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri(IconResourceUri, UriKind.Absolute))
            ?? throw new InvalidOperationException($"Tray icon resource was not found: {IconResourceUri}");

        using Stream stream = resource.Stream;
        using var sourceIcon = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)sourceIcon.Clone();
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e) => Dispatch(_restoreWindow);

    private void NotifyIcon_BalloonTipClicked(object? sender, EventArgs e) => Dispatch(_restoreWindow);

    private void NotifyIcon_BalloonTipClosed(object? sender, EventArgs e) => RunOnDispatcher(CompleteCurrentBalloon);

    private void EnqueueBalloon(string title, string body, System.Windows.Forms.ToolTipIcon icon, int timeout,
        Func<bool> stillRelevant, bool isTaskNotification = false)
    {
        _balloonQueue.Enqueue(new(title, body, icon, timeout, stillRelevant, isTaskNotification));
        DrainBalloonQueue();
    }

    private void DrainBalloonQueue()
    {
        if (_disposed || _balloonIsShowing) return;
        while (_balloonQueue.TryDequeue(out var next))
        {
            bool relevant;
            try { relevant = next.StillRelevant(); }
            catch { relevant = false; }
            if (!relevant) continue;

            _balloonIsShowing = true;
            try
            {
                _notifyIcon.BalloonTipTitle = next.Title;
                _notifyIcon.BalloonTipText = next.Body;
                _notifyIcon.BalloonTipIcon = next.Icon;
                _notifyIcon.ShowBalloonTip(next.Timeout);
                // Windows controls shell balloon duration; this fallback prevents a missing
                // close callback from blocking queued notices for the rest of the app lifetime.
                _balloonFallbackTimer.Interval = TimeSpan.FromSeconds(35);
                _balloonFallbackTimer.Start();
            }
            catch
            {
                _balloonIsShowing = false;
                continue;
            }
            return;
        }
    }

    private void CompleteCurrentBalloon()
    {
        if (!_balloonIsShowing) return;
        _balloonFallbackTimer.Stop();
        _balloonIsShowing = false;
        DrainBalloonQueue();
    }

    private void BalloonFallbackTimer_Tick(object? sender, EventArgs e) => CompleteCurrentBalloon();

    private void Menu_Opening(object? sender, System.ComponentModel.CancelEventArgs e) => RefreshTaskNotificationMenu();

    private void TaskNotificationsItem_DropDownOpening(object? sender, EventArgs e) => RefreshTaskNotificationMenu();

    internal void RefreshTaskNotificationMenu()
    {
        TaskNotificationPreferences? preferences = null;
        try { preferences = _getNotificationPreferences?.Invoke(); }
        catch { }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _taskNotificationsItem.Text = NotificationMenuHeader(preferences, now);
        _notificationsEnabledItem.Checked = preferences?.Enabled ?? false;
        _notificationsOffItem.Checked = !(preferences?.Enabled ?? false);
        foreach (var item in _snoozeItems.Values)
        {
            item.Checked = false;
            item.Enabled = preferences?.Enabled ?? false;
        }
        _customSnoozeItem.Enabled = preferences?.Enabled ?? false;
    }

    private static string NotificationMenuHeader(TaskNotificationPreferences? preferences, DateTimeOffset now)
    {
        if (preferences is null) return "\uC791\uC5C5 \uC54C\uB9BC";
        if (!preferences.Enabled) return "\uC791\uC5C5 \uC54C\uB9BC \u00B7 \uAEBC\uC9D0";
        if (preferences.SnoozedUntilUtc is not { } until || until <= now) return "\uC791\uC5C5 \uC54C\uB9BC";
        int remainingMinutes = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));
        return $"\uC791\uC5C5 \uC54C\uB9BC \u00B7 {remainingMinutes}\uBD84 \uC911\uC9C0";
    }

    private void NotificationsEnabledItem_Click(object? sender, EventArgs e) =>
        Dispatch(() => _setNotificationsEnabled?.Invoke(true));

    private void NotificationsOffItem_Click(object? sender, EventArgs e) =>
        Dispatch(() => _setNotificationsEnabled?.Invoke(false));

    private void SnoozeItem_Click(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Forms.ToolStripMenuItem { Tag: int minutes })
            Dispatch(() => _snoozeNotifications?.Invoke(minutes));
    }

    private void CustomSnoozeItem_Click(object? sender, EventArgs e) => Dispatch(() => _customSnooze?.Invoke());

    private void OpenItem_Click(object? sender, EventArgs e) => Dispatch(_restoreWindow);

    private void CloseAllItem_Click(object? sender, EventArgs e)
    {
        if (_closeAllItem.Enabled)
        {
            Dispatch(_closeAllTerminals);
        }
    }

    private void ExitItem_Click(object? sender, EventArgs e) => Dispatch(_exitApplication);

    private void Dispatch(Action callback)
    {
        RunOnDispatcher(() =>
        {
            if (!_disposed)
            {
                callback();
            }
        });
    }

    private void RunOnDispatcher(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    action();
                }
            });
        }
        catch (InvalidOperationException) when (
            _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            // The application began shutting down between the checks and BeginInvoke.
        }
     }

    private sealed record QueuedBalloon(string Title, string Body, System.Windows.Forms.ToolTipIcon Icon,
        int Timeout, Func<bool> StillRelevant, bool IsTaskNotification);
}
