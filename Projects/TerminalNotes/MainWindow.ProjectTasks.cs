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
    private static readonly AutomationRole[] ProjectTaskRoles = [AutomationRole.Coordinator, AutomationRole.Reviewer, AutomationRole.Implementer];
    private bool _taskWorkspaceVisible;
    private string? _displayedTaskProject;
    private Grid? _taskSurface, _taskDeck, _taskTerminalArea, _taskRightArea, _taskWorkspaceRoot;
    private ColumnDefinition? _taskPlanColumn, _taskTerminalColumn;
    private ScrollViewer? _taskListScroll;
    private StackPanel? _taskRows;
    private TextBlock? _taskHeading, _taskEmpty, _taskSummary;
    private Button? _taskBack;
    private readonly Dictionary<string, ProjectTaskRow> _projectTaskRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rerunningProjectTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _projectTaskContinuousModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset?> _projectTaskImprovementUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectPlanWaitBaseline> _projectPlanWaitSubmissionBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _projectTaskOptionWrites = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingTaskOptions;
    private readonly Dictionary<string, Grid> _projectTaskViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TabItem, ContentControl> _taskTerminalHosts = [];
    private readonly Dictionary<TabItem, TextBlock> _taskTerminalLabels = [];
    private int _taskScanGeneration;
    private Task<string[]> _projectDirectoryScan = Task.FromResult<string[]>([]);

    private sealed record ProjectTaskRow(Border Surface, TreeViewItem Node, Border Header, TextBlock Title, TextBlock State, TextBlock Deadline,
        Button Select, Button Start, Button Folder, Button Pause, Button Record, CheckBox Loop, MenuItem Until,
        RadioButton Multi, RadioButton Single, UniformGrid RolePanel,
        TaskStatusVisual SingleStatus, Dictionary<AutomationRole, TaskStatusVisual> Roles, Button DeadlineEdit, MenuItem Archive);

    private sealed record ProjectPlanWaitBaseline(WeakReference<TerminalPane> Pane, long SubmissionGeneration);

    private void InitializeProjectTasks()
    {
        WorkspaceGrid.Children.Remove(TerminalHost);
        var workspace = new Grid();
        _taskWorkspaceRoot = workspace;
        Grid.SetColumn(workspace, 2);
        var content = new Grid();
        workspace.Children.Add(content);
        content.Children.Add(TerminalHost);
        _taskSurface = new Grid();
        _taskSurface.SetResourceReference(Panel.BackgroundProperty, "Canvas");
        _taskSurface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _taskSurface.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(_taskSurface);
        var header = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
        _taskBack = new Button { Content = "← 목록", Visibility = Visibility.Collapsed, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 10, 0) };
        _taskBack.Click += (_, _) => ShowProjectTaskList();
        header.Children.Add(_taskBack);
        _taskHeading = new TextBlock { Text = "프로젝트", VerticalAlignment = VerticalAlignment.Center, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _taskHeading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _taskSummary = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        _taskSummary.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var taskTitleHost = new Grid(); taskTitleHost.Children.Add(_taskHeading);
        _taskHeading.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && _displayedTaskProject is { } project)
            { e.Handled = true; BeginProjectTitleEdit(project); }
        };
        var headings = new StackPanel(); headings.Children.Add(taskTitleHost); headings.Children.Add(_taskSummary);
        header.Children.Add(headings);
        var headingArea = new StackPanel(); headingArea.Children.Add(header);
        _taskSidebarHeadingArea = headingArea;
        var body = new Grid { ClipToBounds = true };
        _taskSidebarColumn = new ColumnDefinition { Width = new GridLength(TaskSidebarDefaultWidth), MinWidth = TaskSidebarMinimumWidth, MaxWidth = TaskSidebarMaximumWidth };
        body.ColumnDefinitions.Add(_taskSidebarColumn);
        _taskSidebarDividerColumn = new ColumnDefinition { Width = new GridLength(18) };
        body.ColumnDefinitions.Add(_taskSidebarDividerColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var taskListSplitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, Background = StateColorBrush("#303037") };
        _taskSidebarSplitter = taskListSplitter;
        taskListSplitter.DragCompleted += (_, _) => { CaptureTaskSidebarWidth(); SaveSettings(); };
        Grid.SetColumn(taskListSplitter, 1); body.Children.Add(taskListSplitter);
        var sidebarToggle = CreateTaskSidebarToggle();
        Grid.SetColumn(sidebarToggle, 1);
        Grid.SetZIndex(sidebarToggle, 1);
        body.Children.Add(sidebarToggle);
        Grid.SetRow(body, 1); _taskSurface.Children.Add(body);
        var sidebar = new Grid();
        _taskSidebarSurface = sidebar;
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition());
        Grid.SetColumn(sidebar, 0); body.Children.Add(sidebar);
        var projectActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        projectActions.HorizontalAlignment = HorizontalAlignment.Right;
        projectActions.Margin = new Thickness(0, 1, 0, 1);
        var newProject = CreateProjectActionButton(
            "M2,6 L8,6 L10,9 L22,9 L20,20 L2,20 Z M12,11 L12,18 M8.5,14.5 L15.5,14.5",
            "새 프로젝트", "새 프로젝트 폴더와 plan.md를 만듭니다.", iconOnly: true);
        newProject.Content = "new";
        newProject.Width = 44;
        newProject.Height = 28;
        newProject.Padding = new Thickness(6, 0, 6, 0);
        newProject.Margin = new Thickness(4, 0, 0, 0);
        newProject.FontSize = 12;
        newProject.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        newProject.Click += (_, _) => ShowNewProjectDialog();
        projectActions.Children.Add(newProject);
        _taskSidebarActions = projectActions;
        var taskTabs = CreateProjectArchiveTabs(projectActions);
        Grid.SetRow(taskTabs, 0); sidebar.Children.Add(taskTabs);
        Grid.SetRow(headingArea, 1); sidebar.Children.Add(headingArea);
        // These hosts stay loaded and measured when the list or another project is
        // visible. Hidden WebViews keep their process/session identity without
        // covering the list with native child windows.
        _taskRightArea = new Grid();
        _taskPlanColumn = new ColumnDefinition { Width = NotesColumn.Width, MinWidth = NotesColumn.MinWidth };
        _taskRightArea.ColumnDefinitions.Add(_taskPlanColumn);
        _taskRightArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        _taskTerminalColumn = new ColumnDefinition { Width = WorkspaceGrid.ColumnDefinitions[2].Width, MinWidth = 180 };
        _taskRightArea.ColumnDefinitions.Add(_taskTerminalColumn);
        Grid.SetColumn(_taskRightArea, 2); body.Children.Add(_taskRightArea);
        _taskTerminalArea = new Grid();
        _taskTerminalArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _taskTerminalArea.RowDefinitions.Add(new RowDefinition());
        Grid.SetColumn(_taskTerminalArea, 2); _taskRightArea.Children.Add(_taskTerminalArea);
        _taskDeck = new Grid();
        Grid.SetRow(_taskDeck, 1); _taskTerminalArea.Children.Add(_taskDeck);
        _taskRows = new StackPanel();
        _taskEmpty = new TextBlock { Text = "설정한 작업 폴더에 프로젝트가 없습니다.", Margin = new Thickness(18), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray };
        _taskRows.Children.Add(_taskEmpty);
        InitializeProjectTaskTree();
        // Reserve the scrollbar gutter so collapsing/moving a branch never shifts
        // every task's fixed action slots horizontally.
        _taskListScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _taskRows };
        _taskListScroll.SetResourceReference(Control.BackgroundProperty, "Canvas");
        // Constrain compact cards to the sidebar viewport, keeping actions reachable.
        _taskRows.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportWidth))
            { Source = _taskListScroll, Mode = System.Windows.Data.BindingMode.OneWay });
        Grid.SetRow(_taskListScroll, 2); sidebar.Children.Add(_taskListScroll);
        UpdateTaskSidebar();
        WorkspaceGrid.Children.Insert(0, workspace);
        NotesSplitter.DragCompleted += (_, _) => SyncTaskPlanWidths();
        SetTaskWorkspaceVisible(true);
        ArrangeSingleProjectViews();
        if (Layout1.Parent is FrameworkElement layoutChoices)
            (layoutChoices.Parent as Border ?? layoutChoices).Visibility = Visibility.Visible;
    }

    private void SetTaskWorkspaceVisible(bool visible)
    {
        if (_taskSurface is null) return;
        bool changingMode = visible != _taskWorkspaceVisible;
        if (changingMode)
        {
            if (visible)
            {
                SetTaskPlanWidthsFromWorkspace();
                MovePanelTo(NotesPanel, _taskRightArea!);
                MovePanelTo(NotesSplitter, _taskRightArea!);
                MovePanelTo(FocusButton, _taskRightArea!);
                Grid.SetColumn(NotesPanel, 0);
                Grid.SetColumn(NotesSplitter, 1);
                Grid.SetColumn(FocusButton, 1);
                Grid.SetColumn(_taskWorkspaceRoot!, 0);
                Grid.SetColumnSpan(_taskWorkspaceRoot!, 3);
                WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            }
            else
            {
                SyncTaskPlanWidths();
                MovePanelTo(NotesPanel, WorkspaceGrid);
                MovePanelTo(NotesSplitter, WorkspaceGrid);
                MovePanelTo(FocusButton, WorkspaceGrid);
                Grid.SetColumn(NotesPanel, 0);
                Grid.SetColumn(NotesSplitter, 1);
                Grid.SetColumn(FocusButton, 1);
                Grid.SetColumn(_taskWorkspaceRoot!, 2);
                Grid.SetColumnSpan(_taskWorkspaceRoot!, 1);
                WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(18);
            }
            _taskWorkspaceVisible = visible;
        }
        _taskSurface.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        TerminalHost.Visibility = visible ? Visibility.Hidden : Visibility.Visible;
        if (TerminalHost.RowDefinitions.Count > 0)
            TerminalHost.RowDefinitions[0].Height = visible ? new GridLength(0) : GridLength.Auto;
        if (_taskTerminalArea is not null)
        {
            Panel target = visible ? _taskTerminalArea : TerminalHost;
            if (!ReferenceEquals(TerminalAreaHeader.Parent, target))
            {
                if (TerminalAreaHeader.Parent is Panel current) current.Children.Remove(TerminalAreaHeader);
                Grid.SetRow(TerminalAreaHeader, 0);
                target.Children.Add(TerminalAreaHeader);
            }
        }
        ApplyNotesVisibility();
        UpdateWorkbenchTarget();
        if (changingMode && TerminalTabs.Items.Count > 0) ApplyTerminalLayout();
    }

    private static void MovePanelTo(UIElement child, Panel target)
    {
        if (ReferenceEquals(child is FrameworkElement element ? element.Parent : null, target)) return;
        if (child is FrameworkElement currentElement && currentElement.Parent is Panel current)
            current.Children.Remove(child);
        target.Children.Add(child);
    }

    private void SyncTaskPlanWidths()
    {
        if (!_taskWorkspaceVisible || _taskPlanColumn is null || _taskTerminalColumn is null) return;
        // GridSplitter adjusts both star weights. Keep the pair intact across
        // hide/show and workspace changes, including when the plan width is zero.
        NotesColumn.Width = _taskPlanColumn.Width;
        WorkspaceGrid.ColumnDefinitions[2].Width = _taskTerminalColumn.Width;
        if (!_notesHidden) _savedNotesWidth = _taskPlanColumn.Width;
    }

    private void SetTaskPlanWidthsFromWorkspace()
    {
        if (_taskPlanColumn is null || _taskTerminalColumn is null) return;
        _taskPlanColumn.Width = NotesColumn.Width;
        _taskTerminalColumn.Width = WorkspaceGrid.ColumnDefinitions[2].Width;
    }

    private async Task RefreshProjectTasksAsync()
    {
        if (_taskRows is null || _closing || _projectArchiveWrites.Count > 0) return;
        int generation = ++_taskScanGeneration;
        string workspace = CanonicalProjectPath(_workspace);
        string[] projects;
        try
        {
            _projectDirectoryScan = Task.Run(() =>
            {
                IEnumerable<string> Children(string directory) => Directory.Exists(directory) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0
                    ? Directory.EnumerateDirectories(directory).Where(path =>
                    !Path.GetFileName(path).StartsWith('.') &&
                    !string.Equals(Path.GetFileName(path), "terminal-notes", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetFileName(path), ProjectArchiveService.FolderName, StringComparison.OrdinalIgnoreCase) &&
                    (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0)
                    : [];
                return Children(workspace).Concat(Children(Path.Combine(workspace, ProjectArchiveService.FolderName)))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
            });
            projects = await _projectDirectoryScan;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("프로젝트 목록을 읽지 못했습니다 · " + ex.Message);
            return;
        }
        if (_closing || generation != _taskScanGeneration) return;
        // Project choices must not wait for slower, independently locked history stores.
        UpdateDispatcher();
        foreach (string project in projects)
        {
            try
            {
                if (ProjectArchiveService.ReadLocation(project) is { } location)
                {
                    if (!HasProjectName(project)) _projectNames.Add(new(project, location.DisplayName));
                    _projectTaskMultiModes.TryAdd(project, location.MultiMode);
                }
                // Discovery loads history without starting a process or claiming scheduled work.
                var store = new AutomationStore(project);
                if (File.Exists(store.StorePath))
                {
                    await StopLegacyRoleRunsAsync(project);
                    foreach (var run in await store.LoadAsync())
                    {
                        if (_closing || generation != _taskScanGeneration) return;
                        if (_knownAutomationRuns.TryGetValue(run.Id, out var known) &&
                            (known.Revision > run.Revision || known.UpdatedAt > run.UpdatedAt ||
                             known.Revision == run.Revision && known.PauseAfterCurrentStage && !run.PauseAfterCurrentStage)) continue;
                        _knownAutomationRuns[run.Id] = run;
                    }
                }
            }
            catch (Exception ex) { SetStatus("프로젝트 기록 확인 실패 · " + ex.Message); }
        }
        if (_closing || generation != _taskScanGeneration) return;
        // Keep explicitly opened/running projects reachable when workspace changes.
        projects = projects.Concat(_automationRuntimes.Values.Where(runtime => runtime.Team.Count > 0 || runtime.SingleTerminal is not null).Select(runtime => runtime.Project))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string removed in _projectTaskRows.Keys.Except(projects, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            if (_editingTitleProject == removed) EndTitleEdit(false);
            var node = _projectTaskRows[removed].Node;
            DetachProjectTaskNode(node);
            _activeSingleTaskProjects.Remove(removed);
            _projectTaskRows.Remove(removed);
        }
        foreach (string project in projects) EnsureProjectTaskRow(project);
        UpdateArchiveListHeading();
        _taskHeading!.ToolTip = _displayedTaskProject is null ? workspace : "더블클릭하여 제목 변경";
        UpdateProjectTaskActivity();
        UpdateDispatcher();
    }

    private void EnsureProjectTaskRow(string project)
    {
        project = CanonicalProjectPath(project);
        if (_taskRows is null || _projectTaskRows.ContainsKey(project)) return;
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        // Retired role/deadline cells stay hidden; task actions keep fixed widths.
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        var actions = new Grid { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 3);
        foreach (double width in new[] { 0d, 0d, 32d, 0d, 24d })
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        var modeGroup = new Border { Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4), Height = 30,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        Grid.SetColumnSpan(modeGroup, 2); actions.Children.Add(modeGroup);
        var loop = new CheckBox { Content = null, Width = 22, Height = 22, Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "반복 실행 · AI가 요청한 작업을 반복해서 점검하고 개선합니다. 실행 중에도 변경할 수 있으며, 끄면 현재 개선을 마친 뒤 멈춥니다." };
        loop.SetResourceReference(StyleProperty, "QuietCheckBox");
        if (!_projectTaskImprovementUntil.ContainsKey(project))
            _projectTaskImprovementUntil[project] = _knownAutomationRuns.Values.Where(run => DirectoriesEqual(run.ProjectDirectory, project))
                .OrderByDescending(run => run.CreatedAt).FirstOrDefault()?.Options.ImprovementUntil;
        loop.IsChecked = _projectTaskContinuousModes.TryGetValue(project, out bool selectedLoop) ? selectedLoop :
            _knownAutomationRuns.Values.Where(run => DirectoriesEqual(run.ProjectDirectory, project))
                .OrderByDescending(run => run.CreatedAt).FirstOrDefault()?.Options.ContinuousImprovement == true;
        loop.Checked += async (_, _) => { if (!_updatingTaskOptions) await ApplyProjectImprovementOptionsAsync(project, true, _projectTaskImprovementUntil.GetValueOrDefault(project)); };
        loop.Unchecked += async (_, _) => { if (!_updatingTaskOptions) await ApplyProjectImprovementOptionsAsync(project, false, _projectTaskImprovementUntil.GetValueOrDefault(project)); };
        _projectTaskContinuousModes[project] = loop.IsChecked == true;
        var until = new MenuItem { Header = "반복 실행 종료 예약", ToolTip = "이 프로젝트의 반복 실행을 마칠 날짜와 시간을 설정합니다." };
        until.Click += (_, _) =>
        {
            if (_projectTaskRows.TryGetValue(project, out var row)) row.Select.ContextMenu.IsOpen = false;
            ShowImprovementSchedule(project);
        };
        var multi = CreateTaskModeButton(project, "멀티", true);
        var single = CreateTaskModeButton(project, "싱글", false);
        multi.Visibility = single.Visibility = Visibility.Collapsed;
        Grid.SetColumn(single, 1);
        actions.Children.Add(multi);
        actions.Children.Add(single);
        var start = new Button { Padding = new Thickness(0), ToolTip = "실행 · 플랜을 저장한 뒤 시작" };
        SetTaskActionIcon(start, TaskGlyphKind.Play, "실행");
        start.SetResourceReference(StyleProperty, "TaskRunButton");
        var pause = new Button { Content = "일시정지", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(6, 0, 0, 0), ToolTip = "현재 단계 결과를 저장한 뒤 일시정지" };
        var record = new Button { Content = "기록", Padding = new Thickness(10, 6, 10, 6), ToolTip = "지휘 계획 · 검토 결과 · 완료 근거" };
        record.Click += (_, e) => { e.Handled = true; ShowProjectTaskHistory(project); };
        start.Click += async (_, e) => { e.Handled = true; await ToggleProjectTaskAsync(project); };
        pause.Click += async (_, e) => { e.Handled = true; await PauseProjectTaskAsync(project); };
        foreach (var button in new[] { start, pause, record })
        {
            ToolTipService.SetShowOnDisabled(button, true);
            button.Margin = new Thickness(0, 0, 4, 0);
            button.MinHeight = 30;
            button.Height = 30;
            if (button == start)
            {
                Grid.SetColumn(button, 2);
                actions.Children.Add(button);
            }
        }
        start.Margin = new Thickness(0, 0, 4, 0);
        var folder = CreateProjectFolderButton(project);
        Grid.SetColumn(loop, 4);
        actions.Children.Add(loop);
        var title = new TextBlock { Text = ProjectDisplayName(project), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = ProjectDisplayName(project) + "\n" + project + "\n더블클릭 또는 F2로 제목 변경" };
        title.VerticalAlignment = VerticalAlignment.Center;
        var select = new Button { Content = title, Height = 34, Margin = new Thickness(0, 0, 2, 0),
            Padding = new Thickness(4, 0, 4, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = "끌어서 터미널 칸에 배치 · 클릭하면 플랜 선택 · F2 제목 변경" };
        select.SetResourceReference(StyleProperty, "QuietIconButton");
        select.Click += (_, e) => { e.Handled = true; SelectTaskProject(project); };
        select.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { e.Handled = true; BeginProjectTitleEdit(project); }
        };
        var rename = new MenuItem { Header = "제목 변경", InputGestureText = "F2" };
        rename.Click += (_, _) => Dispatcher.BeginInvoke(() => BeginProjectTitleEdit(project));
        var contextMenu = new ContextMenu();
        select.ContextMenu = contextMenu;
        contextMenu.Items.Add(rename);
        contextMenu.Items.Add(CreateProjectCategoryMenuItem(project));
        contextMenu.Items.Add(until);
        contextMenu.Items.Add(new Separator());
        TabItem? ContextTerminal()
        {
            var runtime = RuntimeFor(project);
            return IsMultiProjectTask(project)
                ? runtime.Team.GetValueOrDefault(AutomationRole.Coordinator)
                : runtime.SingleTerminal;
        }
        var continueInCmd = new MenuItem { Header = "CMD에서 이어가기", ToolTip = "기존 작업 세션을 CMD로 제어해 이어갑니다." };
        ToolTipService.SetShowOnDisabled(continueInCmd, true);
        continueInCmd.Click += async (_, e) =>
        {
            e.Handled = true;
            contextMenu.IsOpen = false;
            if (ContextTerminal() is { } tab) await ContinueTerminalInCmdAsync(tab);
        };
        contextMenu.Items.Add(continueInCmd);
        var clearSlot = new MenuItem { Header = "터미널 칸 비우기", ToolTip = "화면 배치만 해제합니다. 터미널과 작업 세션은 계속 실행됩니다." };
        clearSlot.Click += (_, e) =>
        {
            e.Handled = true;
            contextMenu.IsOpen = false;
            RemoveProjectSlot(project);
        };
        contextMenu.Items.Add(clearSlot);
        contextMenu.Opened += (_, _) =>
        {
            var tab = ContextTerminal();
            if (tab is not null) UpdateCmdTerminalMenuItem(tab, continueInCmd);
            else continueInCmd.IsEnabled = false;
            clearSlot.IsEnabled = _terminalSlotLayout.IndexOf(CanonicalProjectPath(project)) >= 0;
        };
        var diagnosticLog = new MenuItem { Header = "자동 진행 진단 로그 열기" };
        diagnosticLog.Click += (_, e) => { e.Handled = true; OpenAutoResumeDiagnostics(); };
        contextMenu.Items.Add(new Separator()); contextMenu.Items.Add(diagnosticLog);
        var uiDiagnosticLog = new MenuItem { Header = "화면 멈춤 진단 로그 열기" };
        uiDiagnosticLog.Click += (_, e) => { e.Handled = true; OpenUiDiagnostics(); };
        contextMenu.Items.Add(uiDiagnosticLog);
        var archive = new MenuItem { Header = IsArchivedProject(project) ? "작업 폴더로 복원" : "아카이브로 보내기" };
        archive.Click += async (_, e) => { e.Handled = true; await MoveProjectArchiveAsync(project); };
        contextMenu.Items.Add(new Separator()); contextMenu.Items.Add(archive);
        EnableTaskSlotDrag(select, project);
        var titleHost = new Grid();
        titleHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folder.Width = 24;
        folder.Margin = new Thickness(0, 0, 3, 0);
        Grid.SetColumn(folder, 0);
        Grid.SetColumn(select, 1);
        titleHost.Children.Add(folder);
        titleHost.Children.Add(select);
        var stateLabel = new TextBlock { Text = "준비", FontSize = 12,
            Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34, Margin = new Thickness(4, 0, 3, 0) };
        var deadline = new TextBlock { FontSize = 13, Foreground = StateColorBrush("#B8B8B8"),
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "—", Margin = new Thickness(4, 0, 4, 0) };
        deadline.SetBinding(AutomationProperties.NameProperty, new System.Windows.Data.Binding(nameof(ToolTip)) { Source = deadline });
        var deadlineEdit = new Button { Content = deadline, Height = 30, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand };
        deadlineEdit.SetResourceReference(StyleProperty, "TaskActionButton");
        AutomationProperties.SetName(deadlineEdit, "연속 진행 종료 시간 설정");
        ToolTipService.SetShowOnDisabled(deadlineEdit, true);
        deadlineEdit.Click += (_, e) => { e.Handled = true; ShowImprovementSchedule(project); };
        var deadlineSurface = new Border { Child = deadlineEdit, Height = 30, VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderBrush = StateColorBrush("#3A3A3A"),
            BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(4, 0, 6, 0) };
        deadlineSurface.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(ToolTip)) { Source = deadline });
        deadlineSurface.Visibility = Visibility.Collapsed; Grid.SetColumn(deadlineSurface, 2);
        header.Children.Add(titleHost); header.Children.Add(actions); header.Children.Add(deadlineSurface);
        var roles = new UniformGrid { Columns = 3, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        var visuals = new Dictionary<AutomationRole, TaskStatusVisual>();
        foreach (var role in ProjectTaskRoles)
        {
            var visual = CreateTaskStatusVisual(RoleLabel(role));
            visual.Surface.BorderBrush = StateColorBrush("#3A3A3A");
            visual.Surface.BorderThickness = new Thickness(role == AutomationRole.Coordinator ? 0 : 1, 0, 0, 0);
            roles.Children.Add(visual.Surface);
            visuals.Add(role, visual);
        }
        var singleStatus = CreateTaskStatusVisual("");
        singleStatus.Surface.Visibility = Visibility.Collapsed;
        singleStatus.Surface.Width = singleStatus.Surface.Height = 30;
        singleStatus.Surface.HorizontalAlignment = HorizontalAlignment.Left;
        singleStatus.Surface.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(singleStatus.Surface, 1); header.Children.Add(singleStatus.Surface);
        roles.Visibility = Visibility.Collapsed;
        Grid.SetColumn(roles, 1); header.Children.Add(roles);
        var surface = new Border { Child = header, MinHeight = 40, Padding = new Thickness(7, 2, 5, 2),
            BorderThickness = new Thickness(2, 0, 0, 0), CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 0, 3), HorizontalAlignment = HorizontalAlignment.Stretch, Focusable = true };
        surface.SetResourceReference(Border.BackgroundProperty, "SurfaceElevated");
        surface.SetResourceReference(Border.BorderBrushProperty, "Line");
        EnableTaskSlotDrag(surface, project);
        surface.ContextMenu = select.ContextMenu;
        surface.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindButtonAncestor(source)) return;
            SelectTaskProject(project);
            if (e.ClickCount == 2) OpenProjectTask(project);
        };
        surface.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2) { BeginProjectTitleEdit(project); e.Handled = true; }
            else if (e.Key == Key.Enter) { OpenProjectTask(project); e.Handled = true; }
        };
        var node = new TreeViewItem { Header = surface, Tag = project, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        node.SetResourceReference(StyleProperty, "TaskTreeLeaf");
        node.Selected += (_, e) =>
        {
            if (!_updatingProjectTaskTree && ReferenceEquals(e.OriginalSource, node)) SelectTaskProject(project);
        };
        node.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2) { BeginProjectTitleEdit(project); e.Handled = true; }
            else if (e.Key == Key.Enter && !FindButtonAncestor(e.OriginalSource as DependencyObject ?? node)) { OpenProjectTask(project); e.Handled = true; }
        };
        _projectTaskRows.Add(project, new(surface, node, surface, title, stateLabel, deadline, select, start, folder, pause, record, loop, until, multi, single, roles, singleStatus, visuals, deadlineEdit, archive));
        _ = ProjectTaskTreeIsActive(project);
        PlaceProjectTaskNode(project, node);
        UpdateArchiveListHeading();
    }

    private static bool FindButtonAncestor(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase) return true;
        return false;
    }

    private void SelectTaskProject(string project, bool ensurePlanDocument = true)
    {
        if (_tabDocumentSaving || _closing) return;
        _selectedProjectPath = project;
        SyncProjectPlanMode(project);
        // Naming refreshes the editor. Create the canonical file before that first asynchronous read.
        if (ensurePlanDocument) EnsureProjectPlanDocument(project);
        if (!HasProjectName(project)) RememberProjectName(project, Path.GetFileName(project));
        var selected = TerminalTabs.SelectedItem as TabItem;
        if (selected?.Tag is TerminalTabState state && !DirectoriesEqual(state.Pane.WorkingDirectory, project))
            TerminalTabs.SelectedItem = null;
        SaveSettings();

        UpdateWorkbenchTarget();
        UpdateProjectTaskActivity();
    }

    private void OpenProjectTask(string project)
    {
        if (_closing || _closePending || _tabDocumentSaving) return;
        try
        {
            SelectTaskProject(project);
            if (IsMultiProjectTask(project)) EnsureAutomationTeam(project);
            else EnsureSingleProjectTerminal(project);
            ShowProjectTask(project);
        }
        catch (Exception ex) { SetStatus("프로젝트 터미널을 열지 못했습니다 · " + ex.Message); }
    }

    private bool IsDisplayedTaskProject(string project) => _taskWorkspaceVisible && DirectoriesEqual(_displayedTaskProject, project);

    private void ShowProjectTask(string project)
    {
        _showArchivedTasks = IsArchivedProject(project);
        (_showArchivedTasks ? _archivedTasksTab : _normalTasksTab)!.IsChecked = true;
        _displayedTaskProject = CanonicalProjectPath(project);
        EnsureProjectSlotVisible(project);
        EnsureProjectTaskRow(project);
        SetTaskWorkspaceVisible(true);
        UpdateTaskSidebar();
        _taskBack!.Visibility = Visibility.Collapsed;
        ((UIElement)_taskHeading!.Parent).Visibility = Visibility.Visible;
        _taskHeading!.Text = ProjectDisplayName(project);
        _taskHeading.ToolTip = "더블클릭하여 제목 변경";
        ArrangeSingleProjectViews();
        var runtime = RuntimeFor(project);
        UpdateProjectTaskView(runtime);
        var tab = IsMultiProjectTask(project) ? runtime.Team.GetValueOrDefault(AutomationRole.Coordinator) : runtime.SingleTerminal;
        if (tab is not null) SelectTerminal(tab, false);
        UpdateWorkbenchTarget();
    }

    private void ShowProjectTaskList()
    {
        _displayedTaskProject = null;
        SetTaskWorkspaceVisible(true);
        ArrangeSingleProjectViews();
        UpdateTaskSidebar();
        _taskBack!.Visibility = Visibility.Collapsed;
        UpdateArchiveListHeading();
        _taskHeading!.ToolTip = _workspace;
        UpdateProjectTaskActivity();
    }

    private void AttachProjectTaskTerminals(AutomationProjectRuntime runtime)
    {
        if (_taskDeck is null) return;
        EnsureProjectTaskRow(runtime.Project);
        if (!_projectTaskViews.TryGetValue(runtime.Project, out var view))
        {
            view = new Grid { Visibility = IsDisplayedTaskProject(runtime.Project) ? Visibility.Visible : Visibility.Hidden };
            foreach (var role in ProjectTaskRoles) view.ColumnDefinitions.Add(new ColumnDefinition());
            _projectTaskViews.Add(runtime.Project, view);
            _taskDeck.Children.Add(view);
        }
        foreach (var role in ProjectTaskRoles)
        {
            if (!runtime.Team.TryGetValue(role, out var tab) || _taskTerminalHosts.ContainsKey(tab)) continue;
            var state = (TerminalTabState)tab.Tag;
            tab.Content = null;
            state.Pane.AllowBackgroundAutomationStartup();
            var heading = new DockPanel { Margin = new Thickness(8) };
            var stop = new Button { Content = "■", Width = 28, Height = 26, Padding = new Thickness(0), ToolTip = "이 역할 작업 중단" };
            stop.Click += (_, _) => state.Pane.Interrupt();
            var cmd = CreateCmdTerminalButton(tab);
            DockPanel.SetDock(cmd, Dock.Right); heading.Children.Add(cmd);
            DockPanel.SetDock(stop, Dock.Right); heading.Children.Add(stop);
            var title = new TextBlock { Text = RoleLabel(role), FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            heading.Children.Add(title);
            heading.MouseLeftButtonDown += (_, _) => SelectTerminal(tab, false);
            heading.Visibility = Visibility.Collapsed;
            var host = new ContentControl { Content = state.Pane, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(heading); Grid.SetRow(host, 1); grid.Children.Add(host);
            var frame = new Border { Child = grid, BorderThickness = new Thickness(0), Margin = new Thickness(0), Tag = tab };
            Grid.SetColumn(frame, Array.IndexOf(ProjectTaskRoles, role)); view.Children.Add(frame);
            _taskTerminalHosts.Add(tab, host); _taskTerminalLabels.Add(tab, title);
        }
        UpdateProjectTaskView(runtime);
    }

    private void DetachTaskTerminal(TabItem tab)
    {
        foreach (var runtime in _automationRuntimes.Values)
            if (runtime.SingleTerminal == tab)
            {
                runtime.SingleTerminal = null;
                RemoveProjectSlot(runtime.Project);
                _terminalSlotNumbers.Remove(runtime.Project);
            }
        if (!_taskTerminalHosts.Remove(tab, out var host)) return;
        host.Content = null;
        _taskTerminalLabels.Remove(tab);
        _cmdTerminalButtons.Remove(tab);
        foreach (var view in _projectTaskViews.Values)
            foreach (var frame in view.Children.OfType<Border>().Where(frame => ReferenceEquals(frame.Tag, tab)).ToArray()) view.Children.Remove(frame);
    }

    private static bool IsVerifiedTaskRun(AutomationRun? run) => run?.State == AutomationState.Completed &&
        !string.IsNullOrWhiteSpace(run.DeliveryEvidence) && !string.IsNullOrWhiteSpace(run.FinalVerificationEvidence);

    private void UpdateProjectTaskActivity()
    {
        int runningCount = 0, completedCount = 0;
        foreach (var (project, row) in _projectTaskRows)
        {
            bool multi = IsMultiProjectTask(project);
            var snapshot = multi ? BuildAutomationProgressSnapshot(project, includeCheckpoints: false) : null;
            var runtime = RuntimeFor(project);
            var run = multi ? LatestProjectRun(project) : null;
            var progressClaim = multi ? FindProgressClaim(project, run?.Id) : null;
            var single = (runtime.SingleTerminal?.Tag as TerminalTabState)?.Pane;
            var autoResumeDiagnostic = multi ? null : LatestAutoResumeDiagnostic(project);
            bool running = ProjectTaskActionIsStop(project);
            bool completed = ProjectTaskIsCompleted(project, running);
            bool external = HasExternalTerminal(project);
            bool acceptedInProgress = runtime.SingleSubmitted && !runtime.SingleStopped && !runtime.SinglePlanInputNeeded &&
                runtime.SingleError is null && !completed && single is { IsRunning: true } &&
                !single.HasUnfinishedAutoResumeWork && !single.IsAutoResumePaused;
            bool editing = external || _projectTaskOptionWrites.Contains(project) || _projectTaskControlWrites.Contains(project);
            row.Archive.IsEnabled = CanMoveProject(project);
            row.Archive.ToolTip = row.Archive.IsEnabled ? "플랜을 저장하고 프로젝트 폴더 전체를 이동합니다. 보관한 위치에서도 계속 개발할 수 있습니다."
                : "실행·예약·승인 대기를 중단하고 문서 저장을 마친 뒤 이동하세요.";
            ToolTipService.SetShowOnDisabled(row.Archive, true);
            if (running) runningCount++;
            if (completed) completedCount++;
            row.Title.Text = ProjectDisplayName(project);
            row.Title.ToolTip = row.Title.Text + "\n" + project + "\n더블클릭 또는 F2로 제목 변경";
            _updatingTaskOptions = true;
            try { row.Multi.IsChecked = multi; row.Single.IsChecked = !multi; }
            finally { _updatingTaskOptions = false; }
            row.Multi.IsEnabled = row.Single.IsEnabled = !running && !editing && !_tabDocumentSaving && !_closing && !_closePending;
            row.RolePanel.Visibility = Visibility.Collapsed;
            row.Select.IsEnabled = !_tabDocumentSaving && !_closing && !_closePending;
            row.Folder.IsEnabled = !_closing && !_closePending;
            row.Loop.Visibility = Visibility.Visible;
            row.Loop.IsEnabled = row.Until.IsEnabled = !runtime.StartingRun && !editing && !_closePending && !_closing;
            UpdateProjectImprovementControls(project, row, editing);
            UpdateProjectTaskDeadline(project, row, multi ? run : null, running);
            row.DeadlineEdit.IsEnabled = row.Until.IsEnabled;
            row.DeadlineEdit.ToolTip = $"반복 실행 종료 시간 · 클릭하여 변경\n{row.Until.ToolTip}";
            row.Deadline.ToolTip = row.DeadlineEdit.ToolTip;
            row.Until.Visibility = Visibility.Visible;
            bool stopAction = ProjectTaskActionIsStop(project);
            bool stopping = _stoppingProjectTasks.Contains(project);
            row.Start.IsEnabled = !external && !_externalTerminalWrites.Contains(project) && !_closePending && !_closing && !stopping &&
                (stopAction || !editing && !_tabDocumentSaving);
            string actionLabel = stopping ? "중단 중" : stopAction ? "중단" : "시작";
            row.Start.SetResourceReference(StyleProperty, stopAction || stopping ? "TaskStopButton" : "TaskRunButton");
            string actionDescription = external ? ExternalTerminalBlockReason : actionLabel + " · " +
                (stopAction || stopping ? "이 프로젝트의 작업 중단" : "플랜이 있으면 저장 후 시작하고, 없으면 OpenCode를 열어 입력을 기다립니다.");
            row.Start.ToolTip = actionDescription;
            row.Start.Visibility = Visibility.Visible;
            row.Record.IsEnabled = run is not null;
            row.Record.Visibility = run is null ? Visibility.Collapsed : Visibility.Visible;
            row.Pause.Visibility = multi && running ? Visibility.Visible : Visibility.Collapsed;
            row.Pause.IsEnabled = running && run is not null && !run.PauseAfterCurrentStage && !editing && !_closePending && !_closing;
            row.Pause.Content = run?.PauseAfterCurrentStage == true ? "정지 예약됨" : "일시정지";
            string taskState = external ? "CMD에서 이어가기" : completed ? "✓ 완료" : multi ?
                run?.PauseAfterCurrentStage == true ? "일시정지 예정" : runtime.StartingRun ? "준비 중" : snapshot?.State ?? "준비" :
                runtime.SingleError is not null ? "⚠ 실행 확인 필요" :
                autoResumeDiagnostic?.RequiresUserAction == true ? "⚠ 자동 진행 확인 필요" :
                runtime.StartingSingle || _startingProjectTasks.Contains(project) || runtime.SingleStartupCancellation is not null ? runtime.SingleStartPhase ?? "◌ 시작 확인 중" : _rerunningProjectTasks.Contains(project) ? "재실행 준비 중" :
                single?.Activity == TerminalActivity.Exited ? "터미널 종료" :
                single?.Activity == TerminalActivity.Error ? "실행 확인 필요" :
                runtime.SingleStopped ? "중단" :
                !runtime.SinglePlanInputNeeded && single?.AutoResumeStatus is { Length: > 0 } stalled && single.Activity == TerminalActivity.Quiet ? (acceptedInProgress ? "진행 중 · " + stalled : stalled) : single?.Activity == TerminalActivity.NeedsInput ? "승인·질문 대기" :
                single?.NativeOpenCodeStatus == OpenCodeStatusKind.Busy ? (acceptedInProgress && single.Activity != TerminalActivity.Active ? "진행 중 · 응답 대기" : single.ActivityLabel) :
                single?.NativeOpenCodeStatus == OpenCodeStatusKind.Pending ? (acceptedInProgress ? "진행 중 · 요청 전달 중" : "요청 전달 중") :
                runtime.SinglePlanInputNeeded && !runtime.SingleSubmitted ? "플랜 입력 대기" :
                runtime.SingleSubmitted && single?.AutoResumeStatus is { Length: > 0 } recovery ? (acceptedInProgress ? "진행 중 · " + recovery : recovery) :
                single?.HasUnfinishedAutoResumeWork == true ? "미완료 · 재개 대기" :
                runtime.SingleSubmitted && single?.HasCompletedWork == true ? "응답 완료 · 검증 대기" :
                runtime.SingleSubmitted && single?.NativeOpenCodeStatus == OpenCodeStatusKind.Unknown &&
                    single.NativeStatusMonitorPhase is OpenCodeStatusMonitorPhase.NotMonitoring or OpenCodeStatusMonitorPhase.ReconnectWaiting
                    ? (acceptedInProgress ? "진행 중 · 상태 확인 불가" : "상태 확인 불가") : runtime.SingleSubmitted && single?.NativeOpenCodeStatus == OpenCodeStatusKind.Unknown
                        ? (acceptedInProgress ? "진행 중 · 상태 확인 중" : "응답 대기") : acceptedInProgress ? "진행 중" : "준비";
            OpenCodeStatusMonitorPhase monitorPhase = external || multi
                ? OpenCodeStatusMonitorPhase.NotMonitoring
                : single?.NativeStatusMonitorPhase ?? OpenCodeStatusMonitorPhase.NotMonitoring;
            string monitorBadge = monitorPhase switch
            {
                OpenCodeStatusMonitorPhase.Connecting => "◌ 연결 확인 중",
                OpenCodeStatusMonitorPhase.Checking => "◌ 상태 확인 중",
                OpenCodeStatusMonitorPhase.Monitoring => "● 상태 감시 중",
                OpenCodeStatusMonitorPhase.ReconnectWaiting => "⚠ 재연결 대기",
                _ => "○ 확인 안 함"
            };
            bool statusUnverified = !multi && !external && (single is null || single.NativeOpenCodeStatus == OpenCodeStatusKind.Unknown);
            row.State.Text = statusUnverified ? monitorBadge + " · " + taskState : taskState;
            string improvement = ProjectImprovementSummary(project);
            row.Loop.ToolTip = ProjectDisplayName(project) + " · " + (improvement.Length > 0 ? improvement : "반복 실행 꺼짐") +
                "\n해제하면 현재 개선을 마친 뒤 멈춥니다. 종료 시간은 프로젝트 카드를 우클릭해 설정할 수 있습니다.";
            AutomationProperties.SetName(row.Loop, ProjectDisplayName(project) + " 반복 실행");
            row.State.ToolTip = multi ? completed ? run?.FinalVerificationEvidence : run is null ? "실행 대기" :
                $"{WorkflowPhaseLabel(run.WorkflowPhase)} · {RoleLabel(run.NextRole)}\n{run.StatusDetail}" : runtime.SingleError ?? monitorBadge + " · " + taskState;
            if (!multi)
            {
                string? loopStatus = autoResumeDiagnostic?.Reason ?? single?.AutoResumeStatus;
                if (!string.IsNullOrWhiteSpace(loopStatus)) row.State.ToolTip += "\n자동 진행 상태: " + loopStatus;
                if (autoResumeDiagnostic?.LastSubstantiveProgressAt is DateTimeOffset lastProgress)
                    row.State.ToolTip += "\n마지막 작업 진척: " + lastProgress.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                row.State.ToolTip += "\n진단 로그: " + _settingsPath + ".automation-diagnostics.jsonl";
                row.State.ToolTip += "\nOpenCode 상태 감시는 연결·세션 상태를 확인하며 실제 작업 진척을 보장하지 않습니다.";
            }
            row.SingleStatus.Surface.Visibility = Visibility.Collapsed;
            if (external) row.State.ToolTip = ExternalTerminalBlockReason;
            row.Surface.ToolTip = row.State.ToolTip;
            AutomationProperties.SetName(row.Node, row.Title.Text + " · " + row.State.Text);
            AutomationProperties.SetName(row.Select, row.Title.Text);
            bool needsInput = !multi && !external && single?.Activity == TerminalActivity.NeedsInput;
            bool active = !external && (multi ? snapshot?.Tone == AutomationProgressTone.Green : single?.Activity == TerminalActivity.Active);
            UpdateTaskStatusVisual(row.SingleStatus, "싱글", row.State.Text,
                !completed && active, needsInput, completed, animateActivity: false);
            foreach (var (role, visual) in row.Roles)
            {
                bool current = snapshot?.Role == role || snapshot is null && runtime.StartingRun && role == AutomationRole.Coordinator;
                AutomationProgressTone tone = current ? snapshot?.Tone ?? AutomationProgressTone.Gray : AutomationProgressTone.Gray;
                bool animating = current && snapshot?.Animating == true;
                string label = completed ? "완료" : current ? row.State.Text : "대기";
                if (!completed && run?.State == AutomationState.Running && runtime.MicroStages.TryGetValue(role, out var micro) &&
                    micro.WorkerDispatch == progressClaim?.DispatchToken)
                {
                    var activity = GetCheckpointProgress(micro);
                    tone = activity.Tone;
                    animating = activity.Animating;
                    label = "점검 · " + activity.Label;
                }
                Brush brush = completed ? StateColorBrush("#C4A5FF") : ProgressBrush(tone);
                needsInput |= multi && tone == AutomationProgressTone.Orange;
                UpdateTaskStatusVisual(visual, RoleLabel(role), label, !completed && tone == AutomationProgressTone.Green,
                    !completed && tone == AutomationProgressTone.Orange, completed, animateActivity: animating);
                if (runtime.Team.TryGetValue(role, out var tab) && _taskTerminalLabels.TryGetValue(tab, out var title))
                { title.Foreground = completed ? StateColorBrush("#DAE0EA") : brush; title.ToolTip = label; }
            }
            bool hardError = runtime.SingleError is not null || single?.Activity == TerminalActivity.Error ||
                autoResumeDiagnostic?.RequiresUserAction == true || run?.State == AutomationState.Failed;
            bool statusPaused = run?.PauseAfterCurrentStage == true || run?.State == AutomationState.Paused;
            bool statusStopped = runtime.SingleStopped || single?.Activity == TerminalActivity.Exited || run?.State == AutomationState.Stopped;
            bool statusNeedsInput = needsInput || !multi && !statusStopped && runtime.SinglePlanInputNeeded && !runtime.SingleSubmitted;
            bool reconnectError = !statusStopped && monitorPhase == OpenCodeStatusMonitorPhase.ReconnectWaiting;
            bool statusError = external || hardError || reconnectError;
            bool effectiveActive = active && !completed && !statusNeedsInput && !statusError && !statusStopped && !statusPaused;
            bool statusAttention = !completed && !statusStopped && !statusPaused && (statusNeedsInput || needsInput);
            TaskGlyphKind statusGlyph = external ? TaskGlyphKind.Attention : completed ? TaskGlyphKind.Complete : hardError ? TaskGlyphKind.Attention :
                statusStopped ? TaskGlyphKind.Stop : statusPaused ? TaskGlyphKind.Pause : statusAttention || reconnectError ? TaskGlyphKind.Attention :
                effectiveActive || (!multi && acceptedInProgress) || stopAction ? TaskGlyphKind.Wait : TaskGlyphKind.Play;
            Brush statusBrush = external ? StateColorBrush("#FF7B72") : completed ? StateColorBrush("#D9BEFF") :
                hardError ? StateColorBrush("#FF7B72") : statusStopped || statusPaused ? StateColorBrush("#A1A1AA") :
                statusAttention || reconnectError ? StateColorBrush("#FFB347") : effectiveActive ? StateColorBrush("#40D392") : StateColorBrush("#A1A1AA");
            string statusHelp = row.State.Text + (row.State.ToolTip is string detail && detail.Length > 0 && detail != row.State.Text ? "\n" + detail : "");
            SetProjectTaskStatusIcon(row.Start, statusGlyph, effectiveActive,
                statusBrush, actionLabel, statusHelp, actionDescription);
            row.Surface.Background = StateColorBrush(completed ? "#55268A" : DirectoriesEqual(_selectedProjectPath, project) ? "#252B35" : "#1B1E24");
            row.Surface.BorderBrush = StateColorBrush(external ? "#FF7B72" : completed ? "#B18AFF" : hardError ? "#FF7B72" :
                statusStopped || statusPaused ? DirectoriesEqual(_selectedProjectPath, project) ? "#84ADFF" : "#2B2F37" :
                statusAttention || reconnectError ? "#FFB347" : effectiveActive ? "#40D392" :
                DirectoriesEqual(_selectedProjectPath, project) ? "#84ADFF" : "#2B2F37");
            row.Title.Foreground = Brushes.White;
            row.State.Foreground = StateColorBrush(external ? "#FF7B72" : completed ? "#D9BEFF" : hardError ? "#FF7B72" :
                statusStopped || statusPaused ? "#A1A1AA" : statusAttention || reconnectError ? "#FFB347" :
                effectiveActive ? "#ADFFE0" : "#A1A1AA");
        }
        UpdateProjectTaskTree();
        ObserveTaskNotifications();
        UpdateCmdTerminalButtons();
        UpdateSleepPrevention();
        if (_taskSummary is not null)
        {
            _taskSummary.Text = $"진행 {runningCount}  ·  완료 {completedCount}";
            _taskSummary.Visibility = _displayedTaskProject is not null && runningCount + completedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshActiveTabDocumentControls();
    }

    private async Task RerunProjectTaskAsync(string project)
    {
        project = CanonicalProjectPath(project);
        if (_closing || _closePending || _stoppingProjectTasks.Contains(project)) return;
        if (HasExternalTerminal(project)) { SetStatus(ExternalTerminalBlockReason); return; }
        if (!_rerunningProjectTasks.Add(project)) return;
        UpdateProjectTaskActivity();
        UpdateDispatcher();

        var runtime = RuntimeFor(project);
        if (runtime.StartingSingle || _startingProjectTasks.Contains(project))
        {
            _rerunningProjectTasks.Remove(project);
            UpdateProjectTaskActivity();
            UpdateDispatcher();
            SetStatus("현재 작업 시작이 끝난 뒤 재실행하세요.");
            return;
        }
        long ownedIntent = runtime.IntentVersion;
        BeginTaskNotificationAttempt(project);
        try
        {
            if (_projectTaskControlWrites.Contains(project) || _projectTaskOptionWrites.Contains(project) || _tabDocumentSaving)
                throw new InvalidOperationException("현재 플랜 저장 또는 작업 설정 변경이 끝난 뒤 재실행하세요.");

            string path = Path.GetFullPath(DefaultProjectPlanPath(project));
            TabDocumentSnapshot? saved = null;
            FileAttributes? pathAttributes = null;
            try { pathAttributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (pathAttributes is { } attributes)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new IOException("저장된 plan.md 경로가 파일이 아닙니다.");
                saved = await new TerminalTabDocumentStore(project, path).LoadAsync(1, TerminalInputMode.Plan);
            }
            if (_closing || _closePending || runtime.IntentVersion != ownedIntent) return;
            if (saved is null || string.IsNullOrWhiteSpace(saved.Text))
            {
                await OpenProjectTaskWithoutPlanAsync(project, runtime, ownedIntent, _startupCancellation.Token);
                return;
            }

            bool draftPreserved = _tabDocumentStates.TryGetValue(path, out var edit) && edit.IsLoaded &&
                !string.Equals(edit.Draft, saved.Text, StringComparison.Ordinal);
            if (HasExternalTerminal(project)) throw new InvalidOperationException(ExternalTerminalBlockReason);
            if (_stoppingProjectTasks.Contains(project)) return;

            if (SingleTaskIsRunning(runtime))
            {
                long beforeStopIntent = ownedIntent;
                await StopProjectTaskAsync(project);
                // Our stop increments the intent exactly once. A concurrent stop or new
                // task changes it again, so do not adopt that newer owner.
                if (_closing || _closePending || runtime.IntentVersion != beforeStopIntent + 1) return;
                ownedIntent = runtime.IntentVersion;
                var idleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
                while (SingleTaskIsRunning(runtime))
                {
                    if (_closing || _closePending || runtime.IntentVersion != ownedIntent || HasExternalTerminal(project)) return;
                    if (DateTime.UtcNow >= idleDeadline)
                        throw new TimeoutException("현재 OpenCode 작업의 중단을 확인하지 못해 재실행하지 않았습니다.");
                    await Task.Delay(100);
                }
            }

            if (_closing || _closePending || runtime.IntentVersion != ownedIntent ||
                _stoppingProjectTasks.Contains(project) || HasExternalTerminal(project)) return;

            Task execution = ExecuteSingleProjectPlanAsync(project, saved, () =>
                !_closing && !_closePending && _rerunningProjectTasks.Contains(project) &&
                !_stoppingProjectTasks.Contains(project) && !HasExternalTerminal(project),
                ownsRerunReservation: true);
            UpdateProjectTaskActivity();
            await execution;
            if (draftPreserved && !runtime.SingleStopped && !_closing && !_closePending && !HasExternalTerminal(project))
                SetStatus("저장된 plan.md를 다시 읽어 실행했습니다. 편집 중인 초안은 그대로 보존했습니다.");
        }
        catch (Exception ex)
        {
            if (_closing || _closePending || runtime.IntentVersion != ownedIntent) return;
            runtime.SingleError = ex.Message;
            SetStatus("재실행 실패 · " + ex.Message);
        }
        finally
        {
            _rerunningProjectTasks.Remove(project);
            UpdateProjectTaskActivity();
            UpdateDispatcher();
        }
    }
    private async Task StartProjectTaskAsync(string project)
    {
        if (HasExternalTerminal(project)) { SetStatus(ExternalTerminalBlockReason); return; }
        project = CanonicalProjectPath(project);
        if (_closing || _closePending || _rerunningProjectTasks.Contains(project) || !_projectTaskControlWrites.Add(project)) return;
        var runtime = RuntimeFor(project);
        long intent = runtime.IntentVersion;

        _startingProjectTasks.Add(project);
        BeginTaskNotificationAttempt(project);
        try
        {
            UpdateProjectTaskActivity();
            if (!await EnsureProjectOpenCodeReadyAsync(runtime, intent, _startupCancellation.Token)) return;
            string canonicalPlan = DefaultProjectPlanPath(project);
            bool hasLegacySource = _documentBindings.Any(binding =>
                string.Equals(binding.Project, project, StringComparison.OrdinalIgnoreCase) && File.Exists(binding.Path));
            if (!hasLegacySource && FindLatestLegacyPlan(project) is { } latestLegacy)
                hasLegacySource = File.Exists(latestLegacy);
            string plan = File.Exists(canonicalPlan) || hasLegacySource
                ? EnsureProjectPlanDocument(project)
                : canonicalPlan;
            if (!HasProjectName(project)) RememberProjectName(project, Path.GetFileName(project));
            var document = await LoadAutomationPlanAsync(null, project, 1, plan, refreshCleanSnapshot: true);
            if (_closing || _closePending || runtime.IntentVersion != intent) return;
            if (string.IsNullOrWhiteSpace(document.InitialDraft))
            {
                await OpenProjectTaskWithoutPlanAsync(project, runtime, intent, _startupCancellation.Token);
                return;
            }

            runtime.SinglePlanInputNeeded = false;
            var saved = await SaveAutomationPlanAsync(document, document.InitialDraft);
            if (_closing || _closePending || runtime.IntentVersion != intent) return;
            if (IsMultiProjectTask(project)) await StartProjectAutomationAsync(project, saved.Text, 1, saved.Path);
            else await ExecuteSingleProjectPlanAsync(project, saved);
        }
        catch (Exception ex)
        {
            if (_closing || _closePending || runtime.IntentVersion != intent) return;
            runtime.SingleError = ex.Message;
            RecordAutomationDiagnostic("single_task_start_failed", "프로젝트 실행 실패 · " + ex.Message,
                severity: "error", exception: ex);
            SetStatus("프로젝트 실행 실패 · " + ex.Message);
        }
        finally { _startingProjectTasks.Remove(project); _projectTaskControlWrites.Remove(project); UpdateProjectTaskActivity(); }
    }
    private async Task OpenProjectTaskWithoutPlanAsync(string project, AutomationProjectRuntime runtime, long intent,
        CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runtime.SingleStartupCancellation = startup;
        try
        {
            if (_closing || _closePending || runtime.IntentVersion != intent || HasExternalTerminal(project)) return;
            if (!await EnsureProjectOpenCodeReadyAsync(runtime, intent, startup.Token)) return;
            if (_closing || _closePending || runtime.IntentVersion != intent || HasExternalTerminal(project)) return;
            if (!EnsureStartedProjectSlotVisible(project))
                throw new InvalidOperationException("터미널 8개 칸이 모두 사용 중입니다. 칸을 비운 뒤 시작하세요.");

            SelectTaskProject(project, ensurePlanDocument: false);
            if (!HasProjectName(project)) RememberProjectName(project, Path.GetFileName(project));
            var tab = EnsureSingleProjectTerminalWithoutPlanDocument(project);
            ShowProjectTask(project);
            var pane = ((TerminalTabState)tab.Tag).Pane;
            pane.ShowProjectPlanInputNotice(false);
            if (!IsNativeOpenCodeTaskPane(pane))
                throw new InvalidOperationException("이 프로젝트에 연결된 터미널이 OpenCode 세션이 아닙니다. 해당 터미널을 닫거나 칸에서 해제한 뒤 다시 시작하세요.");

            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
            readiness.CancelAfter(TerminalExecutionTiming.StartupReadyTimeout);
            await pane.EnsureReadyAsync(readiness.Token);
            if (_closing || _closePending || runtime.IntentVersion != intent || runtime.SingleTerminal != tab ||
                !TerminalTabs.Items.Contains(tab) || HasExternalTerminal(project)) return;
            var state = pane.NativeAutomationDiagnostic;
            if (!pane.IsRunning || state.Failed || state.Disposed)
                throw new InvalidOperationException(pane.PlanExecutionBlockReason);

            _projectPlanWaitSubmissionBaselines[project] = new(new WeakReference<TerminalPane>(pane), pane.NativeSubmissionGeneration);
            UpdateProjectTaskActivity();
            SetStatus("플랜이 없습니다 · 플랜 작성 후 실행하거나 터미널에 직접 입력하세요.");
        }
        finally
        {
            if (ReferenceEquals(runtime.SingleStartupCancellation, startup)) runtime.SingleStartupCancellation = null;
        }
    }

    private async Task<bool> EnsureProjectOpenCodeReadyAsync(AutomationProjectRuntime runtime, long intent, CancellationToken cancellationToken)
    {
        // TASK and the plan panel share the installation check. A pane created
        // before it completes can otherwise remain a shell without OpenCode.
        while (_openCodeStartupBusy)
        {
            if (_closing || _closePending || runtime.IntentVersion != intent) return false;
            await Task.Delay(100, cancellationToken);
        }
        if (_closing || _closePending || runtime.IntentVersion != intent) return false;
        if (!_openCodeReady)
            await PrepareOpenCodeAndStartAsync(null, createTerminal: false).WaitAsync(cancellationToken);
        if (_closing || _closePending || runtime.IntentVersion != intent) return false;
        if (!_openCodeReady)
            throw new InvalidOperationException("OpenCode 시작 준비를 완료하지 못했습니다. " + _openCodeInstallationFailure);
        return true;
    }

    private async Task StopProjectTaskAsync(string project)
    {
        project = CanonicalProjectPath(project);
        if (!_stoppingProjectTasks.Add(project)) return;
        UpdateProjectTaskActivity();
        try { await StopProjectTaskCoreAsync(project); }
        catch (Exception ex) { SetStatus("프로젝트 중단 실패 · " + ex.Message); }
        finally { _stoppingProjectTasks.Remove(project); UpdateProjectTaskActivity(); }
    }

    private async Task StopProjectTaskCoreAsync(string project)
    {
        var runtime = RuntimeFor(project);
        runtime.SingleStopped = true;
        runtime.SingleStartupCancellation?.Cancel();
        if (runtime.SingleTerminal?.Tag is TerminalTabState singleState) singleState.Pane.Interrupt();
        StopMicroManagement(runtime);
        runtime.IntentVersion++;
        runtime.StartingNative?.Pane.Interrupt();
        runtime.Stage?.Pane.Interrupt();
        var store = new AutomationStore(project);
        foreach (var run in (await store.LoadAsync()).Where(run => run.State is AutomationState.Running or AutomationState.Scheduled or AutomationState.Paused))
            _knownAutomationRuns[run.Id] = await store.StopAsync(run.Id);
        if (runtime.Stage is { } stage) await RetireStoppedStageAsync(stage);
        runtime.Pending.Clear();
        UpdateAutomationProgressPanel();
    }
}
