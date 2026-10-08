using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;

namespace TerminalNotes;

public partial class MainWindow
{
    private const double TaskSidebarCollapsedWidth = 0;
    private const double TaskSidebarMinimumWidth = 200;
    private const double TaskSidebarDefaultWidth = 240;
    private const double TaskSidebarMaximumWidth = 380;
    private static readonly TimeSpan TaskSidebarAnimationDuration = TimeSpan.FromMilliseconds(180);
    private bool _taskSidebarCollapsed;
    private double _taskSidebarWidth = TaskSidebarDefaultWidth;
    private ColumnDefinition? _taskSidebarColumn, _taskSidebarDividerColumn;
    private GridSplitter? _taskSidebarSplitter;
    private Grid? _taskSidebarSurface;
    private StackPanel? _taskArchiveTabsPanel;
    private FrameworkElement? _taskSidebarActions;
    private RadioButton? _normalTasksTab, _archivedTasksTab;
    private FrameworkElement? _taskSidebarHeadingArea;
    private Button? _taskSidebarToggle;
    private DispatcherTimer? _taskSidebarAnimationTimer;
    private readonly Stopwatch _taskSidebarAnimationClock = new();
    private double _taskSidebarAnimationFrom, _taskSidebarAnimationTo;
    private bool _taskSidebarAnimating;

    private Button CreateTaskSidebarToggle()
    {
        var arrow = new Path { Data = Geometry.Parse("M 9,3 L 4,8 L 9,13"),
            Stroke = Brushes.White, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 12, Height = 16, Stretch = Stretch.Uniform };
        _taskSidebarToggle = new Button { Content = arrow, Width = 18, Height = 40,
            Padding = new Thickness(0), Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _taskSidebarToggle.SetResourceReference(StyleProperty, "QuietIconButton");
        _taskSidebarToggle.Click += (_, _) => SetTaskSidebarCollapsed(!_taskSidebarCollapsed);
        return _taskSidebarToggle;
    }

    private void SetTaskSidebarCollapsed(bool collapsed)
    {
        if (_closing || _closePending || _taskSidebarColumn is null) return;
        if (collapsed && !_taskSidebarCollapsed)
        {
            if (_taskWorkspaceVisible) SyncTaskPlanWidths();
            if (!_taskSidebarAnimating) CaptureTaskSidebarWidth();
        }
        _taskSidebarCollapsed = collapsed;
        if (_taskWorkspaceVisible && _taskPlanColumn is not null)
        {
            _notesHidden = collapsed;
            _taskPlanColumn.Width = collapsed ? new GridLength(0) : _savedNotesWidth;
            ApplyNotesVisibility();
        }
        if (collapsed)
            foreach (var row in _projectTaskRows.Values) row.Select.ContextMenu.IsOpen = false;
        ApplyTaskSidebarState(animate: true);
        UpdateToolbarState();
        SaveSettings();
    }

    private void CaptureTaskSidebarWidth()
    {
        if (_taskSidebarCollapsed || _taskSidebarAnimating || _taskSidebarColumn is null) return;
        double width = _taskSidebarColumn.ActualWidth > 0 ? _taskSidebarColumn.ActualWidth : _taskSidebarColumn.Width.Value;
        if (double.IsFinite(width)) _taskSidebarWidth = Math.Clamp(width, TaskSidebarMinimumWidth, TaskSidebarMaximumWidth);
    }

    private void LoadTaskSidebarSettings(bool collapsed, double width)
    {
        _taskSidebarCollapsed = collapsed;
        if (_taskWorkspaceVisible)
        {
            _notesHidden = collapsed;
            if (_taskPlanColumn is not null)
                _taskPlanColumn.Width = collapsed ? new GridLength(0) : _savedNotesWidth;
            ApplyNotesVisibility();
        }
        _taskSidebarWidth = double.IsFinite(width) ? Math.Clamp(width, TaskSidebarMinimumWidth, TaskSidebarMaximumWidth) : TaskSidebarDefaultWidth;
        StopTaskSidebarAnimation();
        ApplyTaskSidebarState(animate: false);
    }

    private void LoadTaskPlanWidthSetting(double? width)
    {
        if (width is not { } value || !double.IsFinite(value) || value <= 0) return;
        // The setting stores the plan-to-terminal star-weight ratio, not one column's raw weight.
        double terminalWeight = _taskTerminalColumn is not null && _taskTerminalColumn.Width.IsStar &&
            double.IsFinite(_taskTerminalColumn.Width.Value) && _taskTerminalColumn.Width.Value > 0
            ? _taskTerminalColumn.Width.Value : 7;
        _savedNotesWidth = new GridLength(value * terminalWeight, GridUnitType.Star);
        if (_taskPlanColumn is not null && !_taskSidebarCollapsed)
            _taskPlanColumn.Width = _savedNotesWidth;
        if (!_taskWorkspaceVisible)
            NotesColumn.Width = _savedNotesWidth;
        else
            SyncTaskPlanWidths();
    }

    private double TaskPlanTerminalWidthRatio()
    {
        double planWeight = _savedNotesWidth.IsStar
            ? _savedNotesWidth.Value
            : _taskPlanColumn is not null && _taskPlanColumn.ActualWidth > 0
                ? _taskPlanColumn.ActualWidth : _savedNotesWidth.Value;
        double terminalWeight = _taskTerminalColumn is null ? 7
            : _taskTerminalColumn.Width.IsStar ? _taskTerminalColumn.Width.Value
            : _taskTerminalColumn.ActualWidth > 0 ? _taskTerminalColumn.ActualWidth : _taskTerminalColumn.Width.Value;
        if (!double.IsFinite(planWeight) || planWeight <= 0 || !double.IsFinite(terminalWeight) || terminalWeight <= 0)
            return 3d / 7d;
        return planWeight / terminalWeight;
    }

    private void UpdateTaskSidebar()
    {
        if (_taskSidebarColumn is null) return;
        // Status and project refreshes call this frequently. They may refresh labels,
        // but must never restart a user initiated width transition.
        if (!_taskSidebarAnimating) ApplyTaskSidebarState(animate: false);
    }

    private void ApplyTaskSidebarState(bool animate)
    {
        if (_taskSidebarColumn is null || _taskSidebarDividerColumn is null || _taskListScroll is null) return;
        double target = _taskSidebarCollapsed ? TaskSidebarCollapsedWidth : _taskSidebarWidth;
        if (_taskSidebarCollapsed) _taskSidebarColumn.MinWidth = TaskSidebarCollapsedWidth;
        _taskSidebarColumn.MaxWidth = TaskSidebarMaximumWidth;
        _taskSidebarDividerColumn.Width = new GridLength(_taskSidebarCollapsed ? 0 : 18);
        _taskSidebarSplitter!.Visibility = _taskSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _taskSidebarSurface?.SetValue(FrameworkElement.ClipToBoundsProperty, true);

        if (animate && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation)
        {
            double current = _taskSidebarColumn.ActualWidth;
            if (!double.IsFinite(current) || current <= 0)
                current = _taskSidebarColumn.Width.IsAbsolute ? _taskSidebarColumn.Width.Value : _taskSidebarWidth;
            if (Math.Abs(current - target) > 0.5)
            {
                if (!_taskSidebarCollapsed) _taskListScroll.Visibility = Visibility.Visible;
                StartTaskSidebarAnimation(current, target);
                UpdateTaskSidebarChrome(final: false);
                return;
            }
        }

        StopTaskSidebarAnimation();
        _taskSidebarColumn.Width = new GridLength(target);
        _taskListScroll.Visibility = _taskSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        UpdateTaskSidebarChrome(final: true);
    }

    private void UpdateTaskSidebarChrome(bool final)
    {
        bool collapsed = _taskSidebarCollapsed;
        if (final && _taskSidebarColumn is not null)
            _taskSidebarColumn.MinWidth = collapsed ? TaskSidebarCollapsedWidth : TaskSidebarMinimumWidth;
        if (_taskArchiveTabsPanel is not null)
        {
            bool hideTabs = final && collapsed;
            if (_normalTasksTab is not null) _normalTasksTab.Visibility = hideTabs ? Visibility.Collapsed : Visibility.Visible;
            if (_archivedTasksTab is not null) _archivedTasksTab.Visibility = hideTabs ? Visibility.Collapsed : Visibility.Visible;
            _normalTasksTab?.SetCurrentValue(UIElement.IsEnabledProperty, !collapsed || final);
            _archivedTasksTab?.SetCurrentValue(UIElement.IsEnabledProperty, !collapsed || final);
        }
        if (_taskSidebarActions is not null)
            _taskSidebarActions.Visibility = final && collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (_taskSidebarHeadingArea is not null)
            _taskSidebarHeadingArea.Visibility = final && collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (_taskSidebarToggle is null) return;
        _taskSidebarToggle.Visibility = Visibility.Collapsed;
        var arrow = (Path)_taskSidebarToggle.Content;
        arrow.Data = Geometry.Parse(collapsed ? "M 4,3 L 9,8 L 4,13" : "M 9,3 L 4,8 L 9,13");
        _taskSidebarToggle.ToolTip = collapsed ? "태스크 패널 펼치기" : "태스크 패널 접기";
        AutomationProperties.SetName(_taskSidebarToggle, (string)_taskSidebarToggle.ToolTip);
    }

    private void StartTaskSidebarAnimation(double from, double to)
    {
        StopTaskSidebarAnimation();
        _taskSidebarAnimationFrom = from;
        _taskSidebarAnimationTo = to;
        _taskSidebarAnimationClock.Restart();
        _taskSidebarAnimating = true;
        _taskSidebarAnimationTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _taskSidebarAnimationTimer.Tick += TaskSidebarAnimationTick;
        _taskSidebarAnimationTimer.Start();
    }

    private void TaskSidebarAnimationTick(object? sender, EventArgs e)
    {
        if (_closing || _closePending || _taskSidebarColumn is null || _taskSidebarSurface?.IsLoaded != true)
        {
            StopTaskSidebarAnimation();
            if (!_closing && !_closePending) ApplyTaskSidebarState(animate: false);
            return;
        }
        double progress = Math.Clamp(_taskSidebarAnimationClock.Elapsed.TotalMilliseconds / TaskSidebarAnimationDuration.TotalMilliseconds, 0, 1);
        double eased = 1 - Math.Pow(1 - progress, 3);
        double width = _taskSidebarAnimationFrom + (_taskSidebarAnimationTo - _taskSidebarAnimationFrom) * eased;
        _taskSidebarColumn.Width = new GridLength(width);
        if (progress < 1) return;
        StopTaskSidebarAnimation();
        _taskSidebarColumn.Width = new GridLength(_taskSidebarAnimationTo);
        if (_taskListScroll is not null) _taskListScroll.Visibility = _taskSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        UpdateTaskSidebarChrome(final: true);
    }

    private void StopTaskSidebarAnimation()
    {
        if (_taskSidebarAnimationTimer is not null)
        {
            _taskSidebarAnimationTimer.Stop();
            _taskSidebarAnimationTimer.Tick -= TaskSidebarAnimationTick;
            _taskSidebarAnimationTimer = null;
        }
        _taskSidebarAnimationClock.Stop();
        _taskSidebarAnimating = false;
    }
}
