using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TerminalNotes;

public partial class MainWindow
{
    private TextBox? _titleEditor;
    private TextBlock? _editingTitleLabel;
    private TabItem? _editingTitleTab;
    private string? _editingTitleProject;
    private Button? _editingTitleButton;

    private void BeginTitleEdit(TabItem tab)
    {
        if (_closing || !TerminalTabs.Items.Contains(tab)) return;
        if (_editingTitleTab == tab && _titleEditor is not null) return;
        EndTitleEdit(false);
        SelectTerminal(tab, false);
        var label = _taskTerminalLabels.GetValueOrDefault(tab) ?? _splitFrames.GetValueOrDefault(tab).Title ?? SingleTerminalTitle;
        if (label?.Parent is not Grid surface) return;
        BeginInlineTitleEdit(label, surface, ((TerminalTabState)tab.Tag).Title, tab);
    }

    private void BeginTerminalTaskNameEdit(TabItem tab)
    {
        if (_closing || _closePending || !TerminalTabs.Items.Contains(tab)) return;
        if (((TerminalTabState)tab.Tag).IsIndependent) { BeginTitleEdit(tab); return; }
        string project = CanonicalProjectPath(((TerminalTabState)tab.Tag).Pane.WorkingDirectory);
        var label = _taskTerminalLabels.GetValueOrDefault(tab) ?? _splitFrames.GetValueOrDefault(tab).Title;
        if (label is not null && _titleEditor is not null && _editingTitleLabel == label && _editingTitleProject == project) return;
        if (_titleEditor is not null) EndTitleEdit(false);
        SelectTerminal(tab, false);
        label = _taskTerminalLabels.GetValueOrDefault(tab) ?? _splitFrames.GetValueOrDefault(tab).Title;
        if (label?.Parent is not Grid surface) return;
        BeginInlineTitleEdit(label, surface, ProjectDisplayName(project), project: project);
    }

    private void BeginProjectTitleEdit(string project)
    {
        if (_closing || _closePending) return;
        project = CanonicalProjectPath(project);
        if (_editingTitleProject == project && _titleEditor is not null) return;
        EndTitleEdit(false);
        if (IsDisplayedTaskProject(project) && _taskHeading?.Parent is Grid heading)
            BeginInlineTitleEdit(_taskHeading, heading, ProjectDisplayName(project), project: project);
        else if (_projectTaskRows.TryGetValue(project, out var row) && row.Select.Parent is Grid surface)
            BeginInlineTitleEdit(row.Title, surface, ProjectDisplayName(project), project: project, button: row.Select);
    }

    private void BeginInlineTitleEdit(TextBlock label, Grid surface, string title,
        TabItem? tab = null, string? project = null, Button? button = null)
    {
        var editor = new TextBox
        {
            Text = title,
            MaxLength = 60,
            Padding = new Thickness(3, 1, 3, 1),
            FontSize = label.FontSize,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Enter 적용 · Esc 취소",
            AcceptsReturn = false,
            AcceptsTab = false
        };
        System.Windows.Automation.AutomationProperties.SetName(editor, project is null ? "터미널 제목 편집" : "태스크 제목 편집");
        _titleEditor = editor;
        _editingTitleLabel = label;
        _editingTitleTab = tab;
        _editingTitleProject = project;
        _editingTitleButton = button;
        label.Visibility = Visibility.Collapsed;
        if (button is not null)
        {
            button.Visibility = Visibility.Hidden;
            editor.Margin = button.Margin;
            editor.Height = button.Height;
        }
        FrameworkElement anchor = (FrameworkElement?)button ?? label;
        Grid.SetColumn(editor, Grid.GetColumn(anchor));
        Grid.SetRow(editor, Grid.GetRow(anchor));
        Grid.SetColumnSpan(editor, Grid.GetColumnSpan(anchor));
        Grid.SetRowSpan(editor, Grid.GetRowSpan(anchor));
        surface.Children.Add(editor);
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            if (e.Key == Key.Enter && string.IsNullOrWhiteSpace(editor.Text))
            {
                editor.ToolTip = "제목을 입력하세요 · Esc 취소";
                editor.SelectAll();
                return;
            }
            EndTitleEdit(e.Key == Key.Enter);
            if (_titleEditor is not null) return;
            if (tab is not null && TerminalTabs.Items.Contains(tab)) ((TerminalTabState)tab.Tag).Pane.FocusTerminal();
            else button?.Focus();
        };
        editor.LostKeyboardFocus += (_, _) =>
        {
            if (_titleEditor == editor) EndTitleEdit(project is not null && !string.IsNullOrWhiteSpace(editor.Text));
        };
        editor.Focus();
        editor.SelectAll();
    }

    private void EndTitleEdit(bool apply)
    {
        var editor = _titleEditor;
        var label = _editingTitleLabel;
        var tab = _editingTitleTab;
        var project = _editingTitleProject;
        var button = _editingTitleButton;
        _titleEditor = null;
        if (apply && editor is not null && project is not null)
        {
            try { RememberProjectName(project, editor.Text); }
            catch (Exception ex) { _titleEditor = editor; SetStatus("제목 변경 실패 · " + ex.Message); return; }
        }
        _editingTitleLabel = null;
        _editingTitleTab = null;
        _editingTitleProject = null;
        _editingTitleButton = null;
        if (editor?.Parent is Grid surface) surface.Children.Remove(editor);
        if (label is not null) label.Visibility = Visibility.Visible;
        if (button is not null) button.Visibility = Visibility.Visible;
        if (apply && editor is not null && tab is not null && TerminalTabs.Items.Contains(tab))
            SetTerminalTitle(tab, NormalizeProfileTitle(editor.Text) ?? ((TerminalTabState)tab.Tag).Title);
        if (project is not null) UpdateProjectTaskTree();
    }
}
