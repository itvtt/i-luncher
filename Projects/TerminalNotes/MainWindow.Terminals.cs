using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private static string TokenUsageSuffix(TerminalPane pane) =>
        pane.TokenUsageLabel == "토큰 정보 없음" ? "" : " · " + pane.TokenUsageLabel;
    private sealed record ProjectSessionGroup(string Project, string Name, TabItem[] Tabs);

    private int _splitCapacity = 1;
    private bool _arrangingTerminals;
    private bool _fillingTerminalLayout;
    private bool _terminalLayoutFitPending;
    private readonly List<ContentControl> _splitContents = [];
    private readonly Dictionary<TabItem, Button> _sessionButtons = [];
    private readonly Dictionary<string, Button> _unopenedProjectButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TabItem, (Border Frame, TextBlock Title, TextBlock Status, Grid Heading, Button Stop)> _splitFrames = [];
    private TabItem[] _visibleSplitTabs = [];
    private int _appliedCapacity;

    private void LayoutIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string capacity }) SetTerminalLayout(int.Parse(capacity));
    }

    private void SetTerminalLayout(int capacity)
    {
        ChangeTerminalLayout(capacity, fillVacancies: true);
    }

    private void ChangeTerminalLayout(int capacity, bool fillVacancies)
    {
        if (_closing || _closePending) return;
        int nextCapacity = capacity is 1 or 2 or 3 or 4 or 6 or 8 ? capacity : 1;
        _splitCapacity = nextCapacity;
        foreach (int count in new[] { 1, 2, 3, 4, 6, 8 })
            if (FindName("Layout" + count) is RadioButton option) option.IsChecked = count == _splitCapacity;
        if (_taskWorkspaceVisible)
        {
            ApplyTerminalLayout();
            return;
        }
        var selected = TerminalTabs.SelectedItem as TabItem;
        _arrangingTerminals = true;
        _fillingTerminalLayout = fillVacancies;
        try
        {
            while (fillVacancies && _splitCapacity > 1 && TerminalTabs.Items.Count < _splitCapacity)
            {
                int before = TerminalTabs.Items.Count;
                AddTerminal();
                if (TerminalTabs.Items.Count == before) break; // Invalid/missing workspace: keep existing sessions.
            }
            if (selected is not null && TerminalTabs.Items.Contains(selected)) TerminalTabs.SelectedItem = selected;
        }
        finally
        {
            _fillingTerminalLayout = false;
            _arrangingTerminals = false;
        }
        ApplyTerminalLayout();
        _ = Dispatcher.BeginInvoke(() => ActiveTerminal?.FocusTerminal());
    }

    private void FitTerminalLayoutToCount()
    {
        if (_taskWorkspaceVisible) { ApplyTerminalLayout(); return; }
        if (_arrangingTerminals)
        {
            if (!_fillingTerminalLayout) _terminalLayoutFitPending = true;
            return;
        }
        _terminalLayoutFitPending = false;
        int capacity = TerminalTabs.Items.Count switch
        {
            <= 1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 or 6 => 6,
            _ => 8
        };
        ChangeTerminalLayout(capacity, fillVacancies: false);
    }

    private void FitTerminalLayoutAfterExplicitClose()
    {
        if (!_taskWorkspaceVisible || (_additionalTerminalsVisible && TerminalTabs.Items.Count > 0))
        {
            // Closing an extra session must not compact the user's task slots.
            FitTerminalLayoutToCount();
            return;
        }

        int placedProjects = _terminalSlotLayout.Compact();
        int capacity = new[] { 1, 2, 3, 4, 6, 8 }.First(value => value >= Math.Max(1, placedProjects));
        ChangeTerminalLayout(capacity, fillVacancies: false);
    }
    private void ApplyTerminalLayoutAfterArrangement()
    {
        if (_terminalLayoutFitPending) FitTerminalLayoutToCount();
        else ApplyTerminalLayout();
    }

    private void ApplyTerminalLayout()
    {
        if (_closing || _arrangingTerminals || SplitTerminals is null) return;
        var tabs = TerminalTabs.Items.Cast<TabItem>().Where(tab => !IsTaskTerminal(tab)).ToArray();
        bool alongsideTasks = _taskWorkspaceVisible && tabs.Length > 0;
        int capacity = alongsideTasks ? AdditionalTerminalCapacity(tabs.Length) : _splitCapacity;
        UpdateAdditionalTerminalHost(alongsideTasks, capacity);
        if (_taskWorkspaceVisible)
        {
            ArrangeSingleProjectViews();
            if (!alongsideTasks) { UpdateDispatcher(); return; }
        }
        int index = Math.Max(0, Array.IndexOf(tabs, TerminalTabs.SelectedItem));
        var visible = alongsideTasks ? tabs : capacity == 1 ? Array.Empty<TabItem>() : tabs.Skip(index / capacity * capacity).Take(capacity).ToArray();
        if (_appliedCapacity == capacity && _visibleSplitTabs.SequenceEqual(visible))
        {
            UpdateDispatcher();
            return;
        }
        EndTitleEdit(false);
        _arrangingTerminals = true;
        try
        {
            // Detach views before attaching them to another WPF parent. The ConPTY stays alive.
            ClearTerminalSplitViews();
            foreach (var tab in tabs) tab.Content = null;
            bool split = alongsideTasks || capacity > 1;
            SingleTerminalHeader.Visibility = Visibility.Collapsed;
            TerminalTabsRow.Height = split ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            SplitRow.Height = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            SplitTerminals.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            if (!split)
            {
                foreach (var tab in tabs) tab.Content = ((TerminalTabState)tab.Tag).Pane;
            }
            else
            {
                (SplitTerminals.Columns, SplitTerminals.Rows) = capacity switch
                {
                    2 => (2, 1),
                    3 => (3, 1),
                    4 => (2, 2),
                    6 => (3, 2),
                    8 => (4, 2),
                    _ => (1, 1)
                };
                foreach (var tab in visible)
                {
                    var state = (TerminalTabState)tab.Tag;
                    var title = new TextBlock
                    {
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap,
                        FontSize = 13,
                        LineHeight = 18,
                        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White
                    };
                    var status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 1, 0, 0) };
                    var activate = new Grid { Background = Brushes.Transparent,
                        Margin = new Thickness(6, 0, 6, 0), MinHeight = 27, Focusable = true,
                        Cursor = System.Windows.Input.Cursors.Hand };
                    activate.Children.Add(title);
                    activate.MouseLeftButtonDown += (_, _) =>
                    {
                        if (_titleEditor?.Parent == activate) return;
                        SelectTerminal(tab, false);
                        activate.Focus();
                    };
                    activate.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.F2) { BeginTerminalTaskNameEdit(tab); e.Handled = true; } };
                    var titleHost = new ContentControl { Content = activate, Tag = tab,
                        ToolTip = $"{ProjectDisplayName(state.Pane.WorkingDirectory)} · 더블클릭 또는 F2를 눌러 작업 이름 편집\n{CanonicalProjectPath(state.Pane.WorkingDirectory)}" };
                    System.Windows.Automation.AutomationProperties.SetName(titleHost, $"{ProjectDisplayName(state.Pane.WorkingDirectory)} 작업 이름. 더블클릭 또는 F2로 편집");
                    titleHost.MouseDoubleClick += (_, e) =>
                    {
                        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
                        e.Handled = true;
                        BeginTerminalTaskNameEdit(tab);
                    };
                    var stop = new Button { Content = "■", Tag = tab, Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(2, 0, 3, 0), ToolTip = "이 터미널의 작업 중단 · Ctrl+C" };
                    stop.Click += TerminalInterrupt_Click;
                    var close = new Button { Content = "×", Tag = tab, Width = 26, Height = 26, Padding = new Thickness(0), ToolTip = "이 터미널 닫기 · 세션 종료" };
                    close.Click += TerminalClose_Click;
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 2) };
                    actions.Children.Add(stop); actions.Children.Add(close);
                    status.Margin = new Thickness(6, 0, 4, 2);
                    status.VerticalAlignment = VerticalAlignment.Center;
                    status.TextTrimming = TextTrimming.CharacterEllipsis;
                    var metaRow = new Grid { Visibility = Visibility.Collapsed };
                    metaRow.ColumnDefinitions.Add(new ColumnDefinition());
                    metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    metaRow.Children.Add(status);
                    Grid.SetColumn(actions, 1); metaRow.Children.Add(actions);
                    var heading = new Grid();
                    heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    heading.Children.Add(titleHost);
                    Grid.SetRow(metaRow, 1); heading.Children.Add(metaRow);
                    heading.Visibility = Visibility.Visible;
                    System.Windows.Automation.AutomationProperties.SetName(heading, $"{ProjectDisplayName(state.Pane.WorkingDirectory)} 터미널 분할");
                    activate.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { SelectTerminal(tab); e.Handled = true; } };
                    var content = new ContentControl { Content = state.Pane, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
                    _splitContents.Add(content);
                    var grid = new Grid();
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    grid.RowDefinitions.Add(new RowDefinition());
                    grid.Children.Add(heading);
                    Grid.SetRow(content, 1); grid.Children.Add(content);
                    var frame = new Border { Child = grid, BorderThickness = new Thickness(1), Margin = new Thickness(1),
                        Background = Brushes.Transparent };
                    frame.SetResourceReference(Border.BorderBrushProperty, "Line");
                    _splitFrames.Add(tab, (frame, title, status, heading, stop));
                    SplitTerminals.Children.Add(frame);
                }
            }
            _visibleSplitTabs = visible;
            _appliedCapacity = capacity;
        }
        finally { _arrangingTerminals = false; }
        UpdateDispatcher();
    }

    private void SelectTerminal(TabItem tab, bool focus = true)
    {
        if (_closing || _arrangingTerminals || !TerminalTabs.Items.Contains(tab)) return;
        if (IsTaskTerminal(tab) && tab.Tag is TerminalTabState taskState)
        {
            string project = taskState.Pane.WorkingDirectory;
            bool multi = RuntimeFor(project).SingleTerminal != tab;
            if (IsMultiProjectTask(project) != multi)
            {
                if (ProjectTaskIsRunning(project)) { SetStatus("실행 중인 작업의 모드를 먼저 완료하거나 중단하세요."); return; }
                SetProjectTaskMode(project, multi);
            }
        }
        if (IsTaskTerminal(tab) && !IsDisplayedTaskProject(((TerminalTabState)tab.Tag).Pane.WorkingDirectory))
            ShowProjectTask(((TerminalTabState)tab.Tag).Pane.WorkingDirectory);
        if (IsTaskTerminal(tab) && tab.Tag is TerminalTabState selectedTask)
        {
            EnsureProjectSlotVisible(selectedTask.Pane.WorkingDirectory);
            ArrangeSingleProjectViews();
        }
        TerminalTabs.SelectedItem = tab;
        SessionPickerPopup.IsOpen = false;
        UpdateDispatcher();
        if (focus) ((TerminalTabState)tab.Tag).Pane.FocusTerminal();
    }

    private static Brush ActivityBrush(TerminalPane pane) =>
        StateColorBrush(TerminalActivityPalette.Accent(pane.Activity, pane.HasCompletedWork));

    private static Brush StateColorBrush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    private static Brush TerminalForeground(TerminalPane pane) =>
        StateColorBrush(TerminalActivityPalette.Foreground(pane.Activity, pane.HasCompletedWork));

    private static Brush TerminalBackground(TerminalPane pane) =>
        StateColorBrush(TerminalActivityPalette.Background(pane.Activity, pane.HasCompletedWork));

    private async Task WaitForProjectDirectoryScanAsync()
    {
        while (!_closing)
        {
            var scan = _projectDirectoryScan;
            try { await scan; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // RefreshProjectTasksAsync reports the failure; saved projects remain available.
            }
            if (ReferenceEquals(scan, _projectDirectoryScan)) return;
        }
    }

    private string[] GetKnownPickerProjects() => _projectTaskRows.Keys
        .Concat(_projectDirectoryScan.IsCompletedSuccessfully ? _projectDirectoryScan.Result : [])
        .Concat(_documentBindings.Select(binding => binding.Project))
        .Concat(_projectNames.Select(binding => binding.Project))
        .Concat(TerminalTabs.Items.Cast<TabItem>().Where(tab => !((TerminalTabState)tab.Tag).IsIndependent).Select(tab => ((TerminalTabState)tab.Tag).Pane.WorkingDirectory))
        .Select(CanonicalProjectPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(ProjectDisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(project => project, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private string[] GetTerminalPickerProjects() => GetKnownPickerProjects()
        .Where(project => !DirectoriesEqual(project, _workspace) && !IsArchivedProject(project))
        .ToArray();

    private void UpdateDispatcher()
    {
        if (_closing || DispatcherSessions is null || TerminalTabs is null) return;
        if (_editingTitleTab is not null && (!TerminalTabs.Items.Contains(_editingTitleTab) || TerminalTabs.SelectedItem != _editingTitleTab)) EndTitleEdit(false);
        var tabs = TerminalTabs.Items.Cast<TabItem>().ToArray();
        var independentTabs = tabs.Where(tab => ((TerminalTabState)tab.Tag).IsIndependent).ToArray();
        var projectGroups = tabs.Where(tab => !((TerminalTabState)tab.Tag).IsIndependent)
            .GroupBy(tab => CanonicalProjectPath(((TerminalTabState)tab.Tag).Pane.WorkingDirectory), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProjectSessionGroup(group.Key, ProjectDisplayName(group.Key), group.ToArray()))
            .ToArray();
        var groupsByProject = projectGroups.ToDictionary(group => group.Project, StringComparer.OrdinalIgnoreCase);
        var groupedTabs = projectGroups.SelectMany(group => group.Tabs).Concat(independentTabs).ToArray();
        var knownProjects = GetTerminalPickerProjects();
        TerminalCountLabel.Text = _projectDirectoryScan.IsCompleted
            ? $"프로젝트 {knownProjects.Length} · 터미널 {tabs.Length}"
            : "프로젝트 불러오는 중…";
        var projectsWithoutTabs = knownProjects.Where(project => !groupsByProject.ContainsKey(project)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string staleProject in _unopenedProjectButtons.Keys.Where(project => !projectsWithoutTabs.Contains(project)).ToArray())
            _unopenedProjectButtons.Remove(staleProject);
        foreach (string project in projectsWithoutTabs)
        {
            if (!_unopenedProjectButtons.TryGetValue(project, out var button))
            {
                button = new Button
                {
                    Padding = new Thickness(9, 8, 9, 8), Margin = new Thickness(0, 0, 0, 5),
                    FontSize = 14, Width = 292, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Tag = project
                };
                button.Click += (_, _) =>
                {
                    if (button.Tag is not string selectedProject || !CanSelectPickerProject(selectedProject)) return;
                    SessionPickerPopup.IsOpen = false;
                    SelectProject(selectedProject);
                };
                _unopenedProjectButtons.Add(project, button);
            }

            string name = ProjectDisplayName(project);
            button.Tag = project;
            button.Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = project, FontSize = 12, Opacity = 0.72, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) }
                }
            };
            button.ToolTip = project;
            AutomationProperties.SetName(button, name + " 프로젝트 열기");
            button.IsEnabled = CanSelectPickerProject(project);
        }
        NewTerminalButton.IsEnabled = !_closePending && ManualTerminalCount < 8;
        UpdateTerminalLifecycleButtons();
        var selectedTab = TerminalTabs.SelectedItem as TabItem;
        var selectedState = selectedTab?.Tag as TerminalTabState;
        string? selectedProject = selectedState is null ? null : CanonicalProjectPath(selectedState.Pane.WorkingDirectory);
        bool rerunning = selectedProject is not null && _rerunningProjectTasks.Contains(selectedProject);
        SingleTerminalStop.IsEnabled = ActiveTerminal?.IsRunning == true || rerunning;
        SingleTerminalClose.IsEnabled = tabs.Length > 0;
        foreach (var removed in _sessionButtons.Keys.Except(tabs).ToArray())
        {
            DispatcherSessions.Children.Remove(_sessionButtons[removed]);
            _sessionButtons.Remove(removed);
        }
        foreach (var tab in groupedTabs)
        {
            var state = (TerminalTabState)tab.Tag;
            string project = CanonicalProjectPath(state.Pane.WorkingDirectory);
            string taskName = state.IsIndependent ? state.Title : ProjectDisplayName(project);
            if (_taskTerminalLabels.TryGetValue(tab, out var taskLabel))
            {
                taskLabel.Text = taskName;
                if (taskLabel.Parent is Grid surface && surface.Parent is ContentControl heading)
                {
                    heading.ToolTip = $"{taskName} · 더블클릭 또는 F2를 눌러 작업 이름 편집\n{project}";
                    System.Windows.Automation.AutomationProperties.SetName(heading, $"{taskName} 작업 이름. 더블클릭 또는 F2로 편집");
                }
            }
            var projectGroup = state.IsIndependent ? null : groupsByProject[project];
            if (!_sessionButtons.TryGetValue(tab, out var button))
            {
                button = new Button { Padding = new Thickness(9, 8, 9, 8), Margin = new Thickness(0, 0, 0, 5), FontSize = 14, Width = 292, HorizontalContentAlignment = HorizontalAlignment.Left, ContextMenu = tab.ContextMenu };
                button.Click += (_, _) => SelectTerminal(tab);
                button.MouseDoubleClick += (_, e) => { e.Handled = true; RenameTerminal(tab); };
                _sessionButtons.Add(tab, button);
            }
            var color = ActivityBrush(state.Pane);
            string usage = TokenUsageSuffix(state.Pane);
            string title = $"{state.Sequence:00} {state.Title} · {state.Pane.ActivityLabel}{usage}";
            var pickerContent = new StackPanel();
            if (projectGroup is not null && ReferenceEquals(projectGroup.Tabs[0], tab))
            {
                pickerContent.Children.Add(new TextBlock
                {
                    Text = $"{projectGroup.Name} · {projectGroup.Tabs.Length}개 세션",
                    Foreground = color,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 0, 4),
                    ToolTip = $"{projectGroup.Name}\n{projectGroup.Tabs.Length}개 세션\n{projectGroup.Project}"
                });
            }
            var pickerTitle = new TextBlock
            {
                Text = state.Title,
                Foreground = color,
                FontSize = 16,
                LineHeight = 24,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 52
            };
            var pickerStatus = new TextBlock
            {
                Text = $"{(state.IsIndependent ? "독립 터미널" : ProjectDisplayName(project))} · {state.Sequence:00} · {state.Pane.ActivityLabel}{usage}",
                Foreground = color,
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            pickerContent.Children.Add(pickerTitle);
            pickerContent.Children.Add(pickerStatus);
            button.Content = pickerContent;
            button.Foreground = color;
            button.BorderBrush = tab.IsSelected ? StateColorBrush("#DEE4ED") : StateColorBrush("#303640");
            button.ToolTip = $"{projectGroup?.Name ?? state.Title} · {title}\n{project}";
            ((TextBlock)((StackPanel)tab.Header).Children[0]).Foreground = color;
            if (_splitFrames.TryGetValue(tab, out var split))
            {
                split.Title.Text = taskName;
                split.Status.Text = $"{ProjectDisplayName(project)} · {state.Sequence:00} · {state.Pane.ActivityLabel}{usage}";
                split.Title.Foreground = (Brush)FindResource("TextPrimary");
                split.Status.Foreground = TerminalForeground(state.Pane);
                split.Stop.IsEnabled = state.Pane.IsRunning;
                split.Heading.Visibility = Visibility.Visible;
                split.Frame.BorderThickness = new Thickness(1);
                split.Frame.Margin = new Thickness(1);
                split.Frame.Background = Brushes.Transparent;
                split.Heading.ToolTip = $"{taskName} · 터미널 분할";
                System.Windows.Automation.AutomationProperties.SetName(split.Heading, $"{taskName} 터미널 분할");
                if (split.Title.Parent is Grid titleSurface && titleSurface.Parent is ContentControl titleHost)
                {
                    titleHost.ToolTip = $"{taskName} · 더블클릭 또는 F2를 눌러 {(state.IsIndependent ? "터미널" : "작업")} 이름 편집\n{project}";
                    System.Windows.Automation.AutomationProperties.SetName(titleHost, $"{taskName} {(state.IsIndependent ? "터미널" : "작업")} 이름. 더블클릭 또는 F2로 편집");
                }
                split.Frame.BorderBrush = tab.IsSelected ? StateColorBrush("#84ADFF") : StateColorBrush("#536174");
            }
        }
        var orderedButtons = new List<UIElement>();
        foreach (string project in knownProjects)
        {
            if (groupsByProject.TryGetValue(project, out var group))
                orderedButtons.AddRange(group.Tabs.Select(tab => (UIElement)_sessionButtons[tab]));
            else
                orderedButtons.Add(_unopenedProjectButtons[project]);
        }
        orderedButtons.AddRange(independentTabs.Select(tab => (UIElement)_sessionButtons[tab]));
        if (DispatcherSessions.Children.Count != orderedButtons.Count ||
            !DispatcherSessions.Children.Cast<UIElement>().SequenceEqual(orderedButtons))
        {
            DispatcherSessions.Children.Clear();
            foreach (var button in orderedButtons) DispatcherSessions.Children.Add(button);
        }
        if (TerminalTabs.SelectedItem is TabItem selected)
        {
            var state = (TerminalTabState)selected.Tag;
            SingleTerminalHeader.ContextMenu = selected.ContextMenu;
            ActiveSessionLabel.Text = state.IsIndependent ? $"{state.Title} · {state.Sequence:00}{TokenUsageSuffix(state.Pane)}" : $"{ProjectDisplayName(state.Pane.WorkingDirectory)} · {state.Sequence:00} {state.Title}{TokenUsageSuffix(state.Pane)}";
            SingleTerminalTitle.Text = state.Title;
            SingleTerminalHeader.Background = TerminalBackground(state.Pane);
            SingleTerminalTitle.Foreground = TerminalForeground(state.Pane);
            SingleTerminalMeta.Text = $"{ProjectDisplayName(state.Pane.WorkingDirectory)} · {state.Sequence:00} · {state.Pane.ActivityLabel}{TokenUsageSuffix(state.Pane)}";
            SingleTerminalMeta.ToolTip = state.Pane.WorkingDirectory;
            SingleTerminalMeta.Foreground = TerminalForeground(state.Pane);
            ActiveSessionLabel.Foreground = ActivityBrush(state.Pane);
            SessionPickerButton.ToolTip = $"{ProjectDisplayName(state.Pane.WorkingDirectory)} · {state.Title} · {state.Pane.ActivityLabel}{TokenUsageSuffix(state.Pane)}\n{state.Pane.WorkingDirectory}";
        }
        else
        {
            SingleTerminalHeader.ContextMenu = null;
            SingleTerminalHeader.Background = StateColorBrush("#181B21");
            SingleTerminalTitle.Foreground = SingleTerminalMeta.Foreground = Brushes.White;
            ActiveSessionLabel.Text = "터미널 선택";
            SingleTerminalTitle.Text = "＋ 터미널로 새 세션을 시작하세요";
            SingleTerminalMeta.Text = "";
            ActiveSessionLabel.Foreground = new SolidColorBrush(Color.FromRgb(196, 208, 222));
            SessionPickerButton.ToolTip = "터미널 선택 · 전체 프로젝트와 세션 보기";
        }
        UpdateCompactDashboard();
        UpdateWorkbenchTarget();
    }

    private bool CanSelectPickerProject(string project) =>
        !_closing && !_closePending && !_tabDocumentSaving && !_openCodeStartupBusy &&
        !_projectArchiveWrites.Contains(project) && !_projectTaskControlWrites.Contains(project) &&
        !_projectTaskOptionWrites.Contains(project) && !_externalTerminalWrites.Contains(project) &&
        !HasExternalTerminal(project);

    private static void AddActivityMenuItems(ContextMenu menu, TerminalPane pane)
    {
        var waiting = new MenuItem { Header = "확인 필요로 표시" };
        waiting.Click += (_, _) => pane.MarkNeedsInput();
        menu.Items.Add(waiting);
        var automatic = new MenuItem { Header = "상태 자동 감지로 전환" };
        automatic.Click += (_, _) => pane.ClearActivityOverride();
        menu.Items.Add(automatic);
        menu.Items.Add(new Separator());
    }

    private void NextWaiting_Click(object sender, RoutedEventArgs e)
    {
        var tabs = TerminalTabs.Items.Cast<TabItem>().ToArray();
        int start = TerminalTabs.SelectedIndex;
        for (int offset = 1; offset <= tabs.Length; offset++)
        {
            var tab = tabs[(Math.Max(-1, start) + offset) % tabs.Length];
            if (((TerminalTabState)tab.Tag).Pane.Activity.ToString() == "NeedsInput")
            {
                SelectTerminal(tab);
                return;
            }
        }
        SetStatus("확인이 필요한 터미널이 없습니다.");
    }
}
