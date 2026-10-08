using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private const string TaskSlotDragFormat = "TerminalNotes.ProjectSlot";
    private readonly TerminalSlotLayout _terminalSlotLayout = new();
    private readonly List<(Border Surface, TextBlock Label)> _terminalSlots = [];
    private readonly Dictionary<string, TextBlock> _terminalSlotNumbers = new(StringComparer.OrdinalIgnoreCase);
    private bool _draggingTaskSlot;

    private void EnableTaskSlotDrag(FrameworkElement source, string project)
    {
        Point? origin = null;
        source.PreviewMouseLeftButtonDown += (_, e) =>
        {
            origin = null;
            if (e.ClickCount != 1 || _closing || _closePending || _tabDocumentSaving) return;
            for (DependencyObject? item = e.OriginalSource as DependencyObject; item is not null && item != source;
                 item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
                if (item is TextBoxBase or ButtonBase) return;
            origin = e.GetPosition(source);
        };
        source.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => origin = null), true);
        // Mouse-up can land in a child WebView or outside this handle. Never
        // reuse that old press when a later drag returns to the title/card.
        source.MouseLeave += (_, _) => origin = null;
        source.LostMouseCapture += (_, _) => origin = null;
        source.Unloaded += (_, _) => origin = null;
        source.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) { origin = null; return; }
            if (origin is not { } start || _draggingTaskSlot) return;
            Point current = e.GetPosition(source);
            if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            origin = null;
            if (_closing || _closePending || _tabDocumentSaving) return;
            e.Handled = true;
            RunTaskSlotDrag(project, DragDrop.DoDragDrop);
        };
    }

    private void RunTaskSlotDrag(string project, Func<DependencyObject, object, DragDropEffects, DragDropEffects> drag)
    {
        _draggingTaskSlot = true;
        try
        {
            // The main window stays visible when the WebView-backed views are
            // hidden to expose WPF drop targets. It owns the OLE drag lifetime.
            ArrangeSingleProjectViews();
            drag(this, new DataObject(TaskSlotDragFormat, CanonicalProjectPath(project)), DragDropEffects.Move);
        }
        finally { _draggingTaskSlot = false; ArrangeSingleProjectViews(); }
    }

    private void EnsureProjectSlotVisible(string project)
    {
        _terminalSlotLayout.SetCapacity(_splitCapacity);
        _terminalSlotLayout.EnsureVisible(CanonicalProjectPath(project));
        UpdateProjectTaskTree();
    }

    private bool EnsureStartedProjectSlotVisible(string project)
    {
        project = CanonicalProjectPath(project);
        _terminalSlotLayout.SetCapacity(_splitCapacity);

        int slot = _terminalSlotLayout.IndexOf(project);
        if (slot >= 0)
        {
            if (slot >= _splitCapacity)
            {
                int capacity = new[] { 1, 2, 3, 4, 6, 8 }.First(value => value > slot);
                ChangeTerminalLayout(capacity, fillVacancies: false);
            }
            ArrangeSingleProjectViews();
            return true;
        }

        slot = -1;
        for (int index = 0; index < _splitCapacity; index++)
        {
            if (_terminalSlotLayout.Slots[index] is null) { slot = index; break; }
        }
        if (slot < 0)
        {
            slot = Array.FindIndex(_terminalSlotLayout.Slots.ToArray(), value => value is null);
            if (slot < 0)
            {
                SetStatus("터미널 8칸이 모두 사용 중입니다. 기존 태스크를 비운 뒤 다시 시작하세요.");
                return false;
            }

            int capacity = new[] { 1, 2, 3, 4, 6, 8 }.First(value => value > slot);
            ChangeTerminalLayout(capacity, fillVacancies: false);
        }

        _terminalSlotLayout.Assign(project, slot);
        ArrangeSingleProjectViews();
        UpdateProjectTaskTree();
        return true;
    }

    private void RemoveProjectSlot(string project)
    {
        _terminalSlotLayout.Remove(CanonicalProjectPath(project));
        ArrangeSingleProjectViews();
        UpdateProjectTaskTree();
    }

    private bool AssignProjectToSlot(string project, int slot)
    {
        if (_closing || _closePending || _tabDocumentSaving || slot < 0 || slot >= _splitCapacity) return false;
        project = CanonicalProjectPath(project);
        if (!GetKnownPickerProjects().Contains(project, StringComparer.OrdinalIgnoreCase) || !Directory.Exists(project)) return false;
        if (HasExternalTerminal(project)) { SetStatus(ExternalTerminalBlockReason); return false; }
        if (!CanSelectPickerProject(project)) return false;
        try
        {
            EnsureProjectTaskRow(project);
            SelectTaskProject(project);
            EnsureSingleProjectTerminal(project);
            _terminalSlotLayout.SetCapacity(_splitCapacity);
            _terminalSlotLayout.Assign(project, slot);
            ShowProjectTask(project);
            UpdateProjectTaskTree();
            SetStatus($"{slot + 1}번 칸 · {ProjectDisplayName(project)}");
            return true;
        }
        catch (Exception ex) { SetStatus("태스크를 배치하지 못했습니다 · " + ex.Message); return false; }
    }

    private async void ShowSlotTaskPicker(FrameworkElement anchor, int slot)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor, Placement = PlacementMode.RelativePoint,
            HorizontalOffset = 8, VerticalOffset = 8
        };
        menu.SetResourceReference(StyleProperty, "ProjectPickerMenu");
        menu.Items.Add(new MenuItem { Header = "프로젝트 불러오는 중…", IsEnabled = false });
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
        await WaitForProjectDirectoryScanAsync();
        if (_closing || _closePending || !ReferenceEquals(anchor.ContextMenu, menu)) return;
        menu.Items.Clear();
        foreach (string project in GetTerminalPickerProjects())
        {
            string captured = project;
            var choice = new MenuItem { Header = ProjectDisplayName(project), ToolTip = project, IsEnabled = CanSelectPickerProject(project) };
            choice.Click += (_, _) => AssignProjectToSlot(captured, slot);
            menu.Items.Add(choice);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "선택할 태스크가 없습니다", IsEnabled = false });
    }

    private async Task StopLegacyRoleRunsAsync(string project)
    {
        var store = new AutomationStore(project);
        foreach (var run in await store.LoadAsync())
            if (run.State is AutomationState.Running or AutomationState.Scheduled)
                _knownAutomationRuns[run.Id] = await store.StopAsync(run.Id);
    }

    private void ArrangeSingleProjectViews()
    {
        if (_taskDeck is null) return;
        _terminalSlotLayout.SetCapacity(_splitCapacity);
        // Updating selection/activity must not hide a focused WebView even briefly:
        // Chromium loses its input focus when its WPF ancestor becomes hidden.
        var visibleProjects = _terminalSlotLayout.Slots.Take(_splitCapacity)
            .Where(project => project is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (project, view) in _projectTaskViews)
            view.Visibility = !_draggingTaskSlot && visibleProjects.Contains(project)
                ? Visibility.Visible : Visibility.Hidden;
        (int columns, int rows) = _splitCapacity switch { 2 => (2, 1), 3 => (3, 1), 4 => (2, 2), 6 => (3, 2), 8 => (4, 2), _ => (1, 1) };
        if (_taskDeck.ColumnDefinitions.Count != columns || _taskDeck.RowDefinitions.Count != rows)
        {
            _taskDeck.ColumnDefinitions.Clear();
            _taskDeck.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++) _taskDeck.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < rows; i++) _taskDeck.RowDefinitions.Add(new RowDefinition());
        }
        while (_terminalSlots.Count < 8)
        {
            int index = _terminalSlots.Count;
            var label = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var choose = new Button { Content = label, Padding = new Thickness(8), Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                ToolTip = "빈 칸을 클릭해 태스크를 선택하세요. 태스크를 끌어 놓아도 됩니다." };
            choose.SetResourceReference(StyleProperty, "QuietIconButton");
            choose.Click += (_, _) => ShowSlotTaskPicker(choose, index);
            var surface = new Border { Child = choose,
                BorderThickness = new Thickness(1), Margin = new Thickness(3), AllowDrop = true };
            surface.SetResourceReference(Border.BackgroundProperty, "Surface");
            surface.SetResourceReference(Border.BorderBrushProperty, "Line");
            AutomationProperties.SetName(surface, $"터미널 {index + 1}번 칸");
            surface.PreviewDragOver += (_, e) =>
            {
                bool accepts = !_closing && !_closePending && !_tabDocumentSaving && e.Data.GetDataPresent(TaskSlotDragFormat) &&
                    e.Data.GetData(TaskSlotDragFormat) is string path && _projectTaskRows.ContainsKey(path);
                e.Effects = accepts ? DragDropEffects.Move : DragDropEffects.None;
                surface.BorderBrush = StateColorBrush(accepts ? "#84ADFF" : "#536174");
                e.Handled = true;
            };
            surface.DragLeave += (_, _) => surface.BorderBrush = StateColorBrush("#536174");
            surface.PreviewDrop += (_, e) =>
            {
                e.Handled = true;
                e.Effects = DragDropEffects.None;
                if (e.Data.GetDataPresent(TaskSlotDragFormat) && e.Data.GetData(TaskSlotDragFormat) is string path &&
                    _projectTaskRows.ContainsKey(path) && AssignProjectToSlot(path, index))
                    e.Effects = DragDropEffects.Move;
            };
            _terminalSlots.Add((surface, label));
            _taskDeck.Children.Add(surface);
        }
        for (int i = 0; i < _terminalSlots.Count; i++)
        {
            var (surface, label) = _terminalSlots[i];
            surface.Visibility = i < _splitCapacity ? Visibility.Visible : Visibility.Collapsed;
            surface.Margin = new Thickness(_splitCapacity > 1 ? 1 : 3);
            surface.BorderBrush = StateColorBrush("#536174");
            if (i >= _splitCapacity) continue;
            Grid.SetColumn(surface, i % columns); Grid.SetRow(surface, i / columns);
            string? project = _terminalSlotLayout.Slots[i];
            label.Text = project is null ? "태스크 선택\n클릭해서 목록에서 선택" : $"{i + 1} · {ProjectDisplayName(project)}";
            var slotButton = (Button)surface.Child;
            slotButton.ToolTip = project is null
                ? "빈 칸을 클릭해 태스크를 선택하세요. 태스크를 끌어 놓아도 됩니다."
                : $"{ProjectDisplayName(project)} · 이 칸에 배치됨";
            AutomationProperties.SetName(slotButton, project is null
                ? $"빈 터미널 {i + 1}번 칸 · 클릭하여 태스크 선택"
                : $"터미널 {i + 1}번 칸 · {ProjectDisplayName(project)}");
            if (project is not null && _projectTaskViews.TryGetValue(project, out var view))
            {
                Grid.SetColumn(view, i % columns); Grid.SetRow(view, i / columns);
                // Leave the slot outline visible around the live terminal in split layouts.
                view.Margin = new Thickness(_splitCapacity > 1 ? 2 : 0);
                Panel.SetZIndex(view, 1);
                view.Visibility = _draggingTaskSlot ? Visibility.Hidden : Visibility.Visible;
                if (_terminalSlotNumbers.TryGetValue(project, out var number)) number.Text = (i + 1).ToString();
            }
        }
    }
}
