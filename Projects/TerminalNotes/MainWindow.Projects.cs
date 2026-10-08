using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private string? _selectedProjectPath;
    private string CurrentProjectPath => _selectedProjectPath ?? _workspace;

    private void ShowProjectPicker(Button anchor)
    {
        if (_closing || _closePending || _openCodeStartupBusy) return;
        var menu = new ContextMenu { PlacementTarget = anchor };
        var projects = _documentBindings.Select(binding => binding.Project)
            .Concat(_projectNames.Select(binding => binding.Project))
            .Concat(TerminalTabs.Items.Cast<TabItem>().Where(tab => !((TerminalTabState)tab.Tag).IsIndependent).Select(tab => ((TerminalTabState)tab.Tag).Pane.WorkingDirectory))
            .Append(_workspace).Append(CurrentProjectPath)
            .Select(CanonicalProjectPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ProjectDisplayName, StringComparer.CurrentCultureIgnoreCase);
        foreach (string project in projects)
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = ProjectDisplayName(project), FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = project, FontSize = 13, Opacity = 0.7, MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis });
            var item = new MenuItem
            {
                Header = label,
                ToolTip = project + "\n" + DefaultProjectPlanPath(project),
                IsCheckable = true,
                IsChecked = string.Equals(project, CanonicalProjectPath(CurrentProjectPath), StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (_, _) => SelectProject(project);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "새 프로젝트…", ToolTip = "작업 폴더 바로 아래에 프로젝트 폴더와 시작 플랜을 만듭니다." };
        AutomationProperties.SetName(create, "새 프로젝트 만들기");
        create.Click += (_, _) => ShowNewProjectDialog();
        menu.Items.Add(create);
        var browse = new MenuItem { Header = "프로젝트 열기…" };
        browse.Click += (_, _) => OpenExistingProject();
        menu.Items.Add(browse);
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    internal void OpenExistingProject() => OpenExistingProjectCore(targetSlot: null);

    internal void OpenExistingProjectForSlot(int targetSlot) => OpenExistingProjectCore(targetSlot);

    private void OpenExistingProjectCore(int? targetSlot)
    {
        if (_closing || _closePending || _openCodeStartupBusy || _tabDocumentSaving) return;
        var dialog = new OpenFolderDialog { Title = "프로젝트 폴더 선택", Multiselect = false };
        if (Directory.Exists(_workspace)) dialog.InitialDirectory = _workspace;
        if (dialog.ShowDialog(this) == true)
        {
            if (targetSlot is int slot) SelectProjectForSlot(dialog.FolderName, slot);
            else SelectProject(dialog.FolderName);
        }
    }

    internal void ShowNewProjectDialog() => ShowNewProjectDialogCore(targetSlot: null);

    internal void ShowNewProjectDialogForSlot(int targetSlot) => ShowNewProjectDialogCore(targetSlot);

    private void ShowNewProjectDialogCore(int? targetSlot)
    {
        if (!CanCreateNewProject()) return;
        var dialog = new NewProjectDialog { Owner = this };
        dialog.Resources[typeof(Button)] = FindResource("DocumentActionButton");
        if (dialog.ShowDialog() == true)
            CreateNewProjectCore(dialog.ProjectName, targetSlot);
    }

    private bool CanCreateNewProject()
    {
        if (_closing || _closePending || _openCodeStartupBusy || _tabDocumentSaving || _documentBusy || _loadingDocument)
            return false;
        return true;
    }

    private void CreateNewProject(string projectName) => CreateNewProjectCore(projectName, targetSlot: null);

    private void CreateNewProjectForSlot(string projectName, int targetSlot) => CreateNewProjectCore(projectName, targetSlot);

    private void CreateNewProjectCore(string projectName, int? targetSlot)
    {
        if (!CanCreateNewProject()) return;
        if (!NewProjectDialog.TryValidateFolderName(projectName, out string normalizedName, out string validationError))
        {
            SetStatus(validationError);
            return;
        }
        if (string.Equals(normalizedName, ProjectArchiveService.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("archive는 프로젝트 보관 폴더 이름입니다. 다른 프로젝트 이름을 입력하세요.");
            return;
        }

        string projectPath;
        try
        {
            string workspace = CanonicalProjectPath(_workspace);
            if (!Directory.Exists(workspace))
            {
                SetStatus("작업 폴더를 찾을 수 없습니다 · " + workspace);
                return;
            }

            projectPath = Path.GetFullPath(Path.Combine(workspace, normalizedName));
            if (!string.Equals(Path.GetDirectoryName(projectPath), workspace, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("프로젝트 이름은 작업 폴더 바로 아래의 폴더 이름이어야 합니다.");
                return;
            }
            if (Directory.Exists(projectPath) || File.Exists(projectPath))
            {
                SetStatus("같은 이름의 파일 또는 프로젝트 폴더가 이미 있습니다 · " + projectPath);
                return;
            }

            string planPath = TerminalTabDocumentStore.GetProjectPlanPath(projectPath);
            string planDirectory = Path.GetDirectoryName(planPath)!;
            if (!CreateDirectoryW(projectPath, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error is 80 or 183)
                {
                    SetStatus("같은 이름의 파일 또는 프로젝트 폴더가 이미 있습니다 · " + projectPath);
                    return;
                }
                throw new Win32Exception(error);
            }
            Directory.CreateDirectory(planDirectory);
            using (var stream = new FileStream(planPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write($"# {normalizedName}\n\n## 목표 (필수)\n\n\n## 요구사항 (권장)\n\n\n## 제약 사항 (권장)\n\n\n## 완료 기준 (권장)\n\n\n## 참고 자료 (권장)\n\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
            SetStatus("새 프로젝트를 만들지 못했습니다 · " + ex.Message);
            return;
        }

        SetStatus("프로젝트를 만들었습니다 · " + projectPath);
        RememberProjectName(projectPath, normalizedName);
        if (targetSlot is int slot) SelectProjectForSlot(projectPath, slot);
        else SelectProject(projectPath);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    private static extern bool CreateDirectoryW(string pathName, IntPtr securityAttributes);

    private void SelectProject(string project) => SelectProjectCore(project, targetSlot: null);

    private void SelectProjectForSlot(string project, int targetSlot) => SelectProjectCore(project, targetSlot);

    private void SelectProjectCore(string project, int? targetSlot)
    {
        if (_closing || _closePending || _openCodeStartupBusy || _tabDocumentSaving) return;
        try
        {
            project = CanonicalProjectPath(project);
            if (!Directory.Exists(project)) { SetStatus("프로젝트 폴더를 찾을 수 없습니다 · " + project); return; }
            if (targetSlot is int requestedSlot)
            {
                if (requestedSlot < 0 || requestedSlot >= _splitCapacity)
                {
                    SetStatus("선택한 터미널 칸을 사용할 수 없습니다.");
                    return;
                }
                if (HasExternalTerminal(project)) { SetStatus(ExternalTerminalBlockReason); return; }
            }
            string planPath = EnsureProjectPlanDocument(project);
            var originalBindings = _documentBindings.ToArray();
            string? originalSelection = _selectedProjectPath;
            _documentBindings.RemoveAll(binding =>
                string.Equals(binding.Project, project, StringComparison.OrdinalIgnoreCase));
            _documentBindings.Add(new(project, planPath));
            _selectedProjectPath = project;
            SaveSettings();
            SetStatus(ProjectDisplayName(project) + " · " + planPath);
            _terminalSlotLayout.SetCapacity(_splitCapacity);
            if (targetSlot is int slotToAssign)
            {
                EnsureProjectTaskRow(project);
                if (!AssignProjectToSlot(project, slotToAssign))
                {
                    _documentBindings.Clear();
                    _documentBindings.AddRange(originalBindings);
                    _selectedProjectPath = originalSelection;
                    SaveSettings();
                    UpdateWorkbenchTarget();
                    return;
                }
                _ = RefreshProjectTasksAsync();
                return;
            }
            if (_terminalSlotLayout.CanEnsureVisibleWithoutReplacing(project))
            {
                OpenProjectTask(project);
            }
            else
            {
                _showArchivedTasks = IsArchivedProject(project);
                (_showArchivedTasks ? _archivedTasksTab : _normalTasksTab)!.IsChecked = true;
                SelectTaskProject(project);
                EnsureProjectTaskRow(project);
                ShowProjectTaskList();
                SetStatus($"{ProjectDisplayName(project)} · 기존 터미널 배치를 유지합니다. 태스크를 원하는 칸으로 끌어 놓으세요.");
            }
            _ = RefreshProjectTasksAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetStatus("프로젝트를 열지 못했습니다 · " + ex.Message);
        }
    }
}
