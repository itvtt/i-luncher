using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private const string TaskUncategorizedCategory = "미분류";
    private TreeView? _taskTree;
    private TreeViewItem? _taskCategoryCreateNode;
    private bool _updatingProjectTaskTree;
    // Kept as the default group's alias for older callers and tests.
    private TreeViewItem? _taskOrderGroup;
    private TextBlock? _taskOrderCountLabel;
    private readonly Dictionary<string, TreeViewItem> _taskCategoryGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _taskCategoryCountLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeSingleTaskProjects = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeProjectTaskTree()
    {
        _taskTree = new TreeView { Margin = new Thickness(8, 0, 8, 8) };
        _taskTree.SetResourceReference(StyleProperty, "TaskTree");
        _taskCategoryCreateNode = CreateTaskCategoryEntry();
        _taskTree.Items.Add(_taskCategoryCreateNode);
        _taskRows!.Children.Add(_taskTree);
        UpdateProjectTaskTree();
    }

    private TreeViewItem CreateTaskGroup(string category, string color)
    {
        var header = new Grid { MaxWidth = 260 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var folder = new TaskGlyph { Kind = TaskGlyphKind.Folder, Width = 14, Height = 14,
            Margin = new Thickness(1, 0, 6, 0), Foreground = StateColorBrush(color) };
        Grid.SetColumn(folder, 0); header.Children.Add(folder);
        var label = new TextBlock { Text = category, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = StateColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };
        var count = new TextBlock { Text = "0", FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
            Foreground = StateColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); header.Children.Add(label);
        Grid.SetColumn(count, 2); header.Children.Add(count);
        var group = new TreeViewItem { Header = header, IsExpanded = true, Margin = new Thickness(0, 0, 0, 6), Tag = category,
            AllowDrop = true, Focusable = true };
        group.SetResourceReference(StyleProperty, "TaskTreeGroup");
        group.ToolTip = category;
        group.PreviewDragOver += (_, e) =>
        {
            bool accepts = CanAssignCategoryDrop(e.Data);
            e.Effects = accepts ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        group.Drop += (_, e) =>
        {
            if (e.Handled) return;
            if (!CanAssignCategoryDrop(e.Data) || e.Data.GetData(TaskSlotDragFormat) is not string project)
            { e.Effects = DragDropEffects.None; return; }
            e.Effects = group.Tag is string targetCategory && AssignProjectToCategory(project, targetCategory)
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        group.ContextMenu = CreateCategoryContextMenu(category);
        group.ContextMenuOpening += (_, _) =>
        {
            if (group.Tag is string currentCategory && currentCategory.Equals(TaskUncategorizedCategory, StringComparison.OrdinalIgnoreCase)) group.ContextMenu = null;
        };
        _taskCategoryGroups[category] = group;
        _taskCategoryCountLabels[category] = count;
        if (category.Equals(TaskUncategorizedCategory, StringComparison.OrdinalIgnoreCase))
        { _taskOrderGroup = group; _taskOrderCountLabel = count; }
        return group;
    }

    private ContextMenu CreateCategoryContextMenu(string category)
    {
        var menu = new ContextMenu();
        if (!category.Equals(TaskUncategorizedCategory, StringComparison.OrdinalIgnoreCase))
        {
            var rename = new MenuItem { Header = "카테고리 이름 변경" };
            rename.Click += (_, _) => ShowTaskCategoryDialog("카테고리 이름 변경", category, value => RenameTaskCategory(category, value));
            var remove = new MenuItem { Header = "카테고리 삭제" };
            remove.Click += (_, _) => RemoveTaskCategory(category);
            menu.Items.Add(rename); menu.Items.Add(remove);
        }
        return menu;
    }

    private bool CanAssignCategoryDrop(IDataObject data) => !_closing && !_closePending && !_tabDocumentSaving &&
        data.GetDataPresent(TaskSlotDragFormat) && data.GetData(TaskSlotDragFormat) is string path &&
        _projectTaskRows.ContainsKey(CanonicalProjectPath(path)) && IsVisibleTaskProject(CanonicalProjectPath(path)) &&
        !_projectArchiveWrites.Contains(CanonicalProjectPath(path));

    private bool ProjectTaskTreeIsActive(string project)
    {
        if (_startingProjectTasks.Contains(project)) return true;
        if (IsMultiProjectTask(project)) return ProjectTaskIsRunning(project);
        var runtime = RuntimeFor(project);
        var terminal = runtime.SingleTerminal?.Tag as TerminalTabState;
        bool waitingForPlan = runtime.SinglePlanInputNeeded && !runtime.SingleStopped && runtime.SingleError is null &&
            terminal?.ExitCode is null && terminal?.Pane.IsRunning == true && IsNativeOpenCodeTaskPane(terminal.Pane) &&
            !terminal.Pane.NativeAutomationDiagnostic.Failed && !terminal.Pane.NativeAutomationDiagnostic.Disposed;
        if (waitingForPlan) { _activeSingleTaskProjects.Remove(project); return true; }
        bool active = SingleTaskIsRunning(runtime);
        if (runtime.SinglePlanInputNeeded || runtime.SingleStopped || runtime.SingleError is not null || terminal?.ExitCode is not null || terminal?.Pane.IsAutoResumePaused == true ||
            terminal?.Pane.NativeAutomationDiagnostic.Failed == true || (!runtime.StartingSingle && terminal is null) ||
            (!active && terminal?.Pane.HasVerifiedTaskCompletion == true)) _activeSingleTaskProjects.Remove(project);
        else if (active || runtime.SingleSubmitted && terminal?.Pane.HasVerifiedTaskCompletion != true) _activeSingleTaskProjects.Add(project);
        return _activeSingleTaskProjects.Contains(project);
    }

    private void PlaceProjectTaskNode(string project, TreeViewItem node)
    {
        if (_taskTree is null) return;
        if (!IsVisibleTaskProject(project)) { DetachProjectTaskNode(node); return; }
        string category = _projectCategoryCatalog.GetCategory(CanonicalProjectPath(_workspace), CanonicalProjectPath(project)) ?? TaskUncategorizedCategory;
        if (!_taskCategoryGroups.TryGetValue(category, out var group)) group = EnsureTaskCategoryGroup(category);
        var currentGroup = _taskCategoryGroups.Values.FirstOrDefault(candidate => candidate.Items.Contains(node));
        if (ReferenceEquals(currentGroup, group)) return;
        if (currentGroup is not null) currentGroup.Items.Remove(node);
        if (!group.Items.Contains(node)) group.Items.Add(node);
    }

    private TreeViewItem EnsureTaskCategoryGroup(string category)
    {
        if (_taskCategoryGroups.TryGetValue(category, out var existing)) return existing;
        var group = CreateTaskGroup(category, category.Equals(TaskUncategorizedCategory, StringComparison.OrdinalIgnoreCase) ? "#9AA2AF" : "#77B5A4");
        int createIndex = _taskCategoryCreateNode is null ? -1 : _taskTree!.Items.IndexOf(_taskCategoryCreateNode);
        _taskTree!.Items.Insert(createIndex >= 0 ? createIndex : _taskTree.Items.Count, group);
        SortTaskCategoryGroups();
        return group;
    }

    private void SortTaskCategoryGroups()
    {
        if (_taskTree is null) return;
        if (_titleEditor is not null) return;
        var groups = _taskCategoryGroups.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Value).ToArray();
        for (int index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            int previous = _taskTree.Items.IndexOf(group);
            if (previous == index) continue;
            if (previous >= 0) _taskTree.Items.RemoveAt(previous);
            _taskTree.Items.Insert(index, group);
        }
    }

    private void DetachProjectTaskNode(TreeViewItem node)
    {
        if (_taskTree is null) return;
        foreach (var group in _taskCategoryGroups.Values)
            if (group.Items.Contains(node)) { group.Items.Remove(node); return; }
        _taskTree.Items.Remove(node);
    }

    private bool ProjectTaskIsCompleted(string project, bool running)
    {
        if (running || HasExternalTerminal(project)) return false;
        if (IsMultiProjectTask(project)) return IsVerifiedTaskRun(LatestProjectRun(project));
        var runtime = RuntimeFor(project);
        return !runtime.SinglePlanInputNeeded && !runtime.SingleStopped && runtime.SingleTerminal?.Tag is TerminalTabState single && single.Pane.HasVerifiedTaskCompletion;
    }

    private void UpdateProjectTaskTree()
    {
        if (_taskTree is null) return;
        bool wasUpdating = _updatingProjectTaskTree;
        _updatingProjectTaskTree = true;
        try
        {
            var selected = _taskTree.SelectedItem as TreeViewItem;
            EnsureTaskCategoryGroup(TaskUncategorizedCategory);
            foreach (string category in _projectCategoryCatalog.GetCategories(CurrentTaskCategoryWorkspace)) EnsureTaskCategoryGroup(category);
            foreach (var (project, row) in _projectTaskRows)
            {
                _ = ProjectTaskTreeIsActive(project);
                PlaceProjectTaskNode(project, row.Node);
            }
            var validCategories = _projectCategoryCatalog.GetCategories(CurrentTaskCategoryWorkspace)
                .Append(TaskUncategorizedCategory).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string stale in _taskCategoryGroups.Keys.Where(category => !validCategories.Contains(category)).ToArray())
            {
                var staleGroup = _taskCategoryGroups[stale];
                if (staleGroup.Items.Count != 0) continue;
                _taskTree.Items.Remove(staleGroup);
                _taskCategoryGroups.Remove(stale);
                _taskCategoryCountLabels.Remove(stale);
            }
            foreach (var (category, group) in _taskCategoryGroups.ToArray())
            {
                var ordered = group.Items.OfType<TreeViewItem>()
                    .Where(node => node.Tag is string project && IsVisibleTaskProject(project))
                    .OrderBy(node => ProjectDisplayName((string)node.Tag!), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(node => (string)node.Tag!, StringComparer.OrdinalIgnoreCase).ToArray();
                int editingIndex = _editingTitleProject is { } editing && _titleEditor is not null
                    ? Array.FindIndex(ordered, node => node.Tag is string path && DirectoriesEqual(path, editing)) : -1;
                if (editingIndex >= 0)
                {
                    for (int i = 0; i < editingIndex; i++) MoveTaskNodeIntoPosition(group, ordered[i], i);
                    for (int i = ordered.Length - 1; i > editingIndex; i--) MoveTaskNodeIntoPosition(group, ordered[i], i);
                }
                else for (int i = 0; i < ordered.Length; i++) MoveTaskNodeIntoPosition(group, ordered[i], i);
                int count = group.Items.Count;
                _taskCategoryCountLabels[category].Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
                string accessibleName = $"{category} · {count}";
                group.ToolTip = accessibleName;
                System.Windows.Automation.AutomationProperties.SetName(group, accessibleName);
            }
            SortTaskCategoryGroups();
            if (selected is not null && selected.Parent is TreeViewItem parent && parent.Items.Contains(selected) && !selected.IsSelected) selected.IsSelected = true;
            UpdateArchiveListHeading();
        }
        finally { _updatingProjectTaskTree = wasUpdating; }
    }

    private static void MoveTaskNodeIntoPosition(TreeViewItem group, TreeViewItem node, int index)
    {
        int previous = group.Items.IndexOf(node);
        if (previous == index) return;
        if (previous >= 0) group.Items.RemoveAt(previous);
        group.Items.Insert(index, node);
    }

    private void RenameTaskCategoryGroupHeader(string oldName, string newName)
    {
        if (!_taskCategoryGroups.TryGetValue(oldName, out var group)) return;
        if (!oldName.Equals(newName, StringComparison.Ordinal))
        {
            _taskCategoryCountLabels.TryGetValue(oldName, out var countLabel);
            _taskCategoryGroups.Remove(oldName);
            _taskCategoryCountLabels.Remove(oldName);
            _taskCategoryGroups[newName] = group;
            if (countLabel is not null) _taskCategoryCountLabels[newName] = countLabel;
        }
        group.Tag = newName;
        if (group.Header is Grid groupHeader)
        {
            var label = groupHeader.Children.OfType<TextBlock>().FirstOrDefault();
            if (label is not null) label.Text = newName;
        }
        group.ToolTip = newName;
        group.ContextMenu = CreateCategoryContextMenu(newName);
    }

    private void UpdateProjectTaskDeadline(string project, ProjectTaskRow row, AutomationRun? run, bool running)
    {
        bool improving = _projectTaskContinuousModes.GetValueOrDefault(project);
        if (!improving)
        {
            row.Deadline.Text = "—";
            row.Deadline.ToolTip = "진행 중인 Loop의 종료 예약이 없습니다.";
            return;
        }
        if (_projectTaskImprovementUntil.GetValueOrDefault(project) is not { } end)
        {
            row.Deadline.Text = "∞";
            row.Deadline.ToolTip = "Loop · 종료 예약 없음";
        }
        else
        {
            row.Deadline.Text = end <= DateTimeOffset.Now ? "시간 만료" : $"{end.LocalDateTime:MM/dd HH:mm}";
            row.Deadline.ToolTip = $"Loop · {end.LocalDateTime:yyyy-MM-dd HH:mm} 이후 새 개선을 시작하지 않고 현재 개선의 최종 검토를 마친 뒤 종료합니다.";
        }
    }
}
