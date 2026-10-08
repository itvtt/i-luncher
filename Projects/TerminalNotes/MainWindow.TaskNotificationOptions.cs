using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private TaskNotificationPreferences _taskNotificationPreferences = new();
    private readonly List<WeakReference<TaskNotificationMenuSet>> _taskNotificationMenus = [];
    private static readonly int[] TaskNotificationSnoozePresets = [5, 10, 30, 60];
    private readonly System.Windows.Threading.DispatcherTimer _taskNotificationOptionsRefreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(30)
    };
    private bool _taskNotificationOptionsRefreshTimerAttached;

    private void InitializeTaskNotificationOptions(bool enabled, DateTimeOffset? snoozedUntilUtc)
    {
        _taskNotificationPreferences = new(enabled, snoozedUntilUtc?.ToUniversalTime());
        if (!_taskNotificationOptionsRefreshTimerAttached)
        {
            _taskNotificationOptionsRefreshTimer.Tick += TaskNotificationOptionsRefreshTimer_Tick;
            _taskNotificationOptionsRefreshTimerAttached = true;
        }
        _tray?.ConfigureTaskNotifications(
            () => _taskNotificationPreferences,
            SetTaskNotificationsEnabled,
            SnoozeTaskNotifications,
            ShowCustomTaskNotificationSnooze);
        RefreshTaskNotificationOptions();
    }

    private void AddTaskNotificationOptions(ContextMenu menu)
    {
        var options = new MenuItem { Header = "\uC791\uC5C5 \uC54C\uB9BC" };
        var enabledItem = new MenuItem { Header = "\uC54C\uB9BC \uC0AC\uC6A9", IsCheckable = true };
        var offItem = new MenuItem { Header = "\uC601\uAD6C \uB044\uAE30", IsCheckable = true };
        var snoozeItems = new Dictionary<int, MenuItem>();
        var customItem = new MenuItem { Header = "\uC0AC\uC6A9\uC790 \uC9C0\uC815 \uC2DC\uAC04\u2026" };

        enabledItem.Click += EnabledNotificationMenuItem_Click;
        offItem.Click += DisabledNotificationMenuItem_Click;
        foreach (int minutes in TaskNotificationSnoozePresets)
        {
            var item = new MenuItem
            {
                Header = $"{minutes}\uBD84 \uB3D9\uC548 \uC54C\uB9BC \uBBF8\uB8E8\uAE30",
                CommandParameter = minutes
            };
            item.Click += SnoozeNotificationMenuItem_Click;
            snoozeItems.Add(minutes, item);
            options.Items.Add(item);
        }
        customItem.Click += CustomNotificationMenuItem_Click;
        options.Items.Insert(0, enabledItem);
        options.Items.Insert(1, offItem);
        options.Items.Insert(2, new Separator());
        options.Items.Add(new Separator());
        options.Items.Add(customItem);
        var menuSet = new TaskNotificationMenuSet(new WeakReference<MainWindow>(this), options, enabledItem, offItem, snoozeItems, customItem);
        options.Tag = enabledItem.Tag = offItem.Tag = customItem.Tag = menuSet;
        foreach (var item in snoozeItems.Values) item.Tag = menuSet;
        options.SubmenuOpened += TaskNotificationOptionsSubmenuOpened;
        menu.Items.Add(new Separator());
        menu.Items.Add(options);
        _taskNotificationMenus.Add(new(menuSet));
        RefreshTaskNotificationOptions();
    }

    private void RefreshTaskNotificationOptions()
    {
        bool enabled = _taskNotificationPreferences.Enabled;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string header = TaskNotificationOptionsHeader(_taskNotificationPreferences, now);
        for (int index = _taskNotificationMenus.Count - 1; index >= 0; index--)
        {
            if (!_taskNotificationMenus[index].TryGetTarget(out var menu))
            {
                _taskNotificationMenus.RemoveAt(index);
                continue;
            }
            menu.Parent.Header = header;
            menu.EnabledItem.IsChecked = enabled;
            menu.OffItem.IsChecked = !enabled;
            foreach (var item in menu.SnoozeItems.Values)
            {
                item.IsEnabled = enabled;
                item.IsCheckable = true;
                item.IsChecked = false;
            }
            menu.CustomItem.IsEnabled = enabled;
        }
        _tray?.RefreshTaskNotificationMenu();

        if (_closing || _taskNotificationPreferences.SnoozedUntilUtc is not { } until || until <= now)
            _taskNotificationOptionsRefreshTimer.Stop();
        else
            _taskNotificationOptionsRefreshTimer.Start();
    }

    private static void TaskNotificationOptionsSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: TaskNotificationMenuSet menu } && menu.Owner.TryGetTarget(out var owner))
            owner.RefreshTaskNotificationOptions();
    }

    private static void EnabledNotificationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetTaskNotificationMenuSet(sender)?.Owner.TryGetTarget(out var owner) == true)
            owner.SetTaskNotificationsEnabled(true);
    }

    private static void DisabledNotificationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetTaskNotificationMenuSet(sender)?.Owner.TryGetTarget(out var owner) == true)
            owner.SetTaskNotificationsEnabled(false);
    }

    private static void SnoozeNotificationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: int minutes } item &&
            GetTaskNotificationMenuSet(sender)?.Owner.TryGetTarget(out var owner) == true)
            owner.SnoozeTaskNotifications(minutes);
    }

    private static void CustomNotificationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetTaskNotificationMenuSet(sender)?.Owner.TryGetTarget(out var owner) == true)
            owner.ShowCustomTaskNotificationSnooze();
    }

    private static TaskNotificationMenuSet? GetTaskNotificationMenuSet(object? sender) =>
        (sender as MenuItem)?.Tag as TaskNotificationMenuSet;

    private void DisposeTaskNotificationOptions()
    {
        _taskNotificationOptionsRefreshTimer.Stop();
        if (_taskNotificationOptionsRefreshTimerAttached)
        {
            _taskNotificationOptionsRefreshTimer.Tick -= TaskNotificationOptionsRefreshTimer_Tick;
            _taskNotificationOptionsRefreshTimerAttached = false;
        }
        foreach (var reference in _taskNotificationMenus)
            if (reference.TryGetTarget(out var menu)) menu.Parent.SubmenuOpened -= TaskNotificationOptionsSubmenuOpened;
        _taskNotificationMenus.Clear();
    }

    private static string TaskNotificationOptionsHeader(TaskNotificationPreferences preferences, DateTimeOffset now)
    {
        if (!preferences.Enabled) return "\uC791\uC5C5 \uC54C\uB9BC \u00B7 \uAEBC\uC9D0";
        if (preferences.SnoozedUntilUtc is not { } until || until <= now) return "\uC791\uC5C5 \uC54C\uB9BC";
        int remainingMinutes = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));
        return $"\uC791\uC5C5 \uC54C\uB9BC \u00B7 {remainingMinutes}\uBD84 \uC911\uC9C0";
    }

    private void TaskNotificationOptionsRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing)
        {
            _taskNotificationOptionsRefreshTimer.Stop();
            return;
        }
        RefreshTaskNotificationOptions();
    }

    private void SetTaskNotificationsEnabled(bool enabled)
    {
        _taskNotificationPreferences = new(enabled, null);
        if (!enabled) _tray?.CancelTaskNotifications();
        SaveSettings();
        RefreshTaskNotificationOptions();
    }

    private void SnoozeTaskNotifications(int minutes)
    {
        if (minutes <= 0 || !TaskNotificationPreferences.TryGetSnoozeUntil(minutes.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow, out var untilUtc)) return;
        _taskNotificationPreferences = new(true, untilUtc);
        _tray?.CancelTaskNotifications();
        SaveSettings();
        RefreshTaskNotificationOptions();
    }

    private void ShowCustomTaskNotificationSnooze()
    {
        RestoreFromTray();
        var dialog = new TaskNotificationMinutesDialog(this);
        if (dialog.ShowDialog() == true) SnoozeTaskNotifications(dialog.Minutes);
    }

    private sealed record TaskNotificationMenuSet(WeakReference<MainWindow> Owner, MenuItem Parent,
        MenuItem EnabledItem, MenuItem OffItem, Dictionary<int, MenuItem> SnoozeItems, MenuItem CustomItem);
}
