using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private bool _showArchivedTasks;
    private readonly HashSet<string> _projectArchiveWrites = new(StringComparer.OrdinalIgnoreCase);
    private bool IsArchivedProject(string project) => ProjectArchiveService.IsArchived(_workspace, project);
    private bool IsVisibleTaskProject(string project) => IsArchivedProject(project) == _showArchivedTasks;
    private string TaskListName => _showArchivedTasks ? "아카이브" : "태스크";

    private Grid CreateProjectArchiveTabs(StackPanel projectActions)
    {
        var header = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        _taskArchiveTabsPanel = panel;
        var tabGroup = new Border
        {
            Background = StateColorBrush("#15191F"),
            BorderBrush = StateColorBrush("#292F39"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel
        };
        Grid.SetColumn(tabGroup, 0);
        header.Children.Add(tabGroup);
        RadioButton Tab(string text, bool archived)
        {
            var tab = new RadioButton { Content = text, GroupName = "ProjectArchiveTabs",
                Width = archived ? 68 : 56, Height = 32, IsChecked = !archived };
            tab.SetResourceReference(StyleProperty, "ProjectArchiveTab");
            tab.Checked += (_, _) =>
            {
                _showArchivedTasks = archived;
                ShowProjectTaskList();
            };
            panel.Children.Add(tab);
            return tab;
        }
        _normalTasksTab = Tab("태스크", false);
        _archivedTasksTab = Tab("아카이브", true);
        tabGroup.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = _normalTasksTab });
        Grid.SetColumn(projectActions, 2);
        header.Children.Add(projectActions);
        return header;
    }

    private void UpdateArchiveListHeading()
    {
        int count = _projectTaskRows.Keys.Count(IsVisibleTaskProject);
        if (_taskHeading is not null)
            if (_taskHeading.Parent is UIElement headingHost)
                headingHost.Visibility = _displayedTaskProject is null ? Visibility.Collapsed : Visibility.Visible;
        if (_taskEmpty is not null)
        {
            _taskEmpty.Text = _showArchivedTasks ? "보관한 프로젝트가 없습니다." : "설정한 작업 폴더에 프로젝트가 없습니다.";
            _taskEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_taskTree is not null) _taskTree.Visibility = Visibility.Visible;
    }

    private bool CanMoveProject(string project)
    {
        if (HasExternalTerminal(project)) return false;
        if (_closing || _closePending || _tabDocumentSaving || _projectArchiveWrites.Count > 0 ||
            _projectTaskControlWrites.Contains(project) || _projectTaskOptionWrites.Contains(project) || ProjectTaskIsRunning(project)) return false;
        string? parent = Path.GetDirectoryName(CanonicalProjectPath(project));
        if (!IsArchivedProject(project) && (parent is null || !DirectoriesEqual(parent, _workspace))) return false;
        return !TerminalTabs.Items.Cast<TabItem>().Any(tab => tab.Tag is TerminalTabState state &&
            DirectoriesEqual(state.Pane.WorkingDirectory, project) &&
            (state.IsIndependent || state.Pane.NativeOpenCodeStatus is OpenCodeStatusKind.Busy or OpenCodeStatusKind.Pending or OpenCodeStatusKind.NeedsInput ||
             state.Pane.HasUnfinishedAutoResumeWork || state.Pane.NativeAutomationDiagnostic.HasPendingInput ||
             state.Pane.IsRunning && state.Pane.Activity == TerminalActivity.Active));
    }

    private async Task MoveProjectArchiveAsync(string project)
    {
        project = CanonicalProjectPath(project);
        if (TerminalTabs.Items.Cast<TabItem>().Any(tab => tab.Tag is TerminalTabState { IsIndependent: true } state &&
            DirectoriesEqual(state.Pane.WorkingDirectory, project)))
        {
            SetStatus("해당 폴더를 사용하는 독립 터미널을 닫은 뒤 이동하세요.");
            return;
        }
        if (!CanMoveProject(project)) { SetStatus("작업·예약·승인 대기를 중단하고 문서 저장을 마친 뒤 이동하세요."); return; }
        try { _ = ProjectArchiveService.Destination(_workspace, project); }
        catch (Exception ex) { SetStatus("프로젝트 이동 불가 · " + ex.Message); return; }
        _projectArchiveWrites.Add(project);
        _projectTaskControlWrites.Add(project);
        _tabDocumentSaving = true;
        _taskScanGeneration++;
        UpdateProjectTaskActivity();
        UpdateWorkbenchTarget();
        try
        {
            var runtime = RuntimeFor(project);
            for (int attempt = 0; (runtime.Polling || runtime.MicroPolling) && attempt < 100; attempt++) await Task.Delay(50);
            if (runtime.Polling || runtime.MicroPolling) throw new InvalidOperationException("작업 정리 중입니다. 잠시 후 다시 이동하세요.");
            if (_closing || _closePending) return;
            if (_editingTitleProject == project) EndTitleEdit(true);
            string plan = DefaultProjectPlanPath(project);
            if (_tabDocumentStates.TryGetValue(plan, out var edit))
            {
                if (edit.IsLoading && edit.LoadTask is { } load) await load;
                if (edit.Snapshot is not null && edit.Draft != edit.Snapshot.Text)
                {
                    edit.Snapshot = await new TerminalTabDocumentStore(project).SaveAsync(edit.Slot, edit.Mode, edit.Draft, edit.Snapshot);
                    edit.Draft = edit.Snapshot.Text;
                }
                else if (!edit.IsLoaded && edit.Draft.Length > 0)
                    throw new InvalidOperationException("플랜을 읽은 뒤 저장하고 다시 이동하세요.");
            }
            var persisted = await new AutomationStore(project).LoadAsync();
            if (persisted.Any(run => run.State is AutomationState.Running or AutomationState.Scheduled))
                throw new InvalidOperationException("실행·예약 중인 작업을 중단한 뒤 이동하세요.");
            string name = ProjectDisplayName(project);
            bool multi = IsMultiProjectTask(project);
            // A process keeps its original working directory. Retire only idle sessions belonging to this project.
            foreach (var tab in TerminalTabs.Items.Cast<TabItem>().Where(tab => DirectoriesEqual(((TerminalTabState)tab.Tag).Pane.WorkingDirectory, project)).ToArray())
            {
                var pane = ((TerminalTabState)tab.Tag).Pane;
                DetachTaskTerminal(tab);
                DisposeSafely(pane);
                _automationPaneTokens.Remove(pane);
                _automationPanesToReplace.Remove(pane);
                TerminalTabs.Items.Remove(tab);
            }
            runtime.Team.Clear(); runtime.SingleTerminal = null;
            string destination = await ProjectArchiveService.MoveAsync(_workspace, project, name, multi);
            RebindMovedProject(project, destination);
            _showArchivedTasks = IsArchivedProject(destination);
            (_showArchivedTasks ? _archivedTasksTab : _normalTasksTab)!.IsChecked = true;
            ShowProjectTaskList();
            await PersistAutomationProjectsAsync();
            SaveSettings();
            SetStatus(_showArchivedTasks ? "아카이브로 이동했습니다 · 보관한 위치에서 계속 개발할 수 있습니다." : "프로젝트를 작업 폴더로 복원했습니다.");
        }
        catch (Exception ex) { SetStatus("프로젝트 이동 실패 · " + ex.Message); }
        finally
        {
            _projectArchiveWrites.Remove(project);
            _projectTaskControlWrites.Remove(project);
            _tabDocumentSaving = false;
            UpdateTerminalCount();
            await RefreshProjectTasksAsync();
        }
    }

    private void RebindMovedProject(string source, string destination)
    {
        if (_projectTaskViews.Remove(source, out var view)) _taskDeck!.Children.Remove(view);
        _automationRuntimes.Remove(source);
        _activeSingleTaskProjects.Remove(source);
        if (_projectTaskRows.Remove(source, out var row))
        {
            DetachProjectTaskNode(row.Node);
        }
        _terminalSlotLayout.Rename(source, destination);
        _projectCategoryCatalog.MoveProject(source, destination);
        MovePreference(_projectTaskMultiModes, source, destination);
        MovePreference(_projectTaskContinuousModes, source, destination);
        MovePreference(_projectTaskImprovementUntil, source, destination);
        MovePreference(_projectInputDrafts, source, destination);
        if (_loadedSingleImprovementPreferences.Remove(source)) _loadedSingleImprovementPreferences.Add(destination);
        if (_projectTaskContinuousModes.TryGetValue(destination, out bool improving))
        {
            try { SingleProjectContinuousImprovementStore.Write(destination, improving, _projectTaskImprovementUntil.GetValueOrDefault(destination)); }
            catch (Exception ex) { SetStatus("프로젝트는 이동했습니다. Loop 설정 저장 확인 필요 · " + ex.Message); }
        }
        for (int i = 0; i < _projectNames.Count; i++)
            if (DirectoriesEqual(_projectNames[i].Project, source)) _projectNames[i] = _projectNames[i] with { Project = destination };
        for (int i = 0; i < _documentBindings.Count; i++)
            if (DirectoriesEqual(_documentBindings[i].Project, source)) _documentBindings[i] = new(destination, DefaultProjectPlanPath(destination));
        foreach (var run in _knownAutomationRuns.Values.Where(run => DirectoriesEqual(run.ProjectDirectory, source)).ToArray())
            _knownAutomationRuns[run.Id] = run with { ProjectDirectory = destination };
        _automationProjects.Remove(source);
        if (File.Exists(new AutomationStore(destination).StorePath)) _automationProjects.Add(destination);
        _tabDocumentStates.Remove(DefaultProjectPlanPath(source));
        if (DirectoriesEqual(_selectedProjectPath, source)) _selectedProjectPath = destination;
        if (DirectoriesEqual(_rememberedSelectedProjectPath, source)) _rememberedSelectedProjectPath = destination;
        if (DirectoriesEqual(_displayedTaskProject, source)) _displayedTaskProject = null;
        InvalidateActiveTabDocument();
    }

    private static void MovePreference<T>(Dictionary<string, T> values, string source, string destination)
    {
        if (values.Remove(source, out var value)) values[destination] = value;
    }

    private async Task PersistAutomationProjectsAsync()
    {
        await _automationProjectWriteGate.WaitAsync();
        try
        {
            string temporary = AutomationProjectsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_automationProjects));
            File.Move(temporary, AutomationProjectsPath, true);
        }
        finally { _automationProjectWriteGate.Release(); }
    }
}
