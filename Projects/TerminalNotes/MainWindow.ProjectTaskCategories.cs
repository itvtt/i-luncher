using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private ProjectCategoryCatalog _projectCategoryCatalog = new();

    private string CurrentTaskCategoryWorkspace => CanonicalProjectPath(_workspace);

    private bool CanMutateTaskCategories => !_closing && !_closePending && !_tabDocumentSaving && _projectArchiveWrites.Count == 0;

    private bool AddTaskCategory(string name)
    {
        if (!CanMutateTaskCategories || !_projectCategoryCatalog.TryAddCategory(CurrentTaskCategoryWorkspace, name)) return false;
        SaveSettings();
        EnsureTaskCategoryGroup(name.Trim());
        UpdateProjectTaskTree();
        return true;
    }

    private bool RenameTaskCategory(string oldName, string newName)
    {
        if (!CanMutateTaskCategories || oldName.Equals(TaskUncategorizedCategory, StringComparison.OrdinalIgnoreCase) ||
            !_projectCategoryCatalog.TryRenameCategory(CurrentTaskCategoryWorkspace, oldName, newName)) return false;
        SaveSettings();
        RenameTaskCategoryGroupHeader(oldName, newName.Trim());
        UpdateProjectTaskTree();
        return true;
    }

    private bool RemoveTaskCategory(string name)
    {
        if (!CanMutateTaskCategories || name.Equals(TaskUncategorizedCategory, StringComparison.OrdinalIgnoreCase) ||
            !_projectCategoryCatalog.RemoveCategory(CurrentTaskCategoryWorkspace, name)) return false;
        SaveSettings();
        UpdateProjectTaskTree();
        return true;
    }

    private bool AssignProjectToCategory(string project, string? category)
    {
        if (!CanMutateTaskCategories || string.IsNullOrWhiteSpace(project)) return false;
        project = CanonicalProjectPath(project);
        if (!_projectTaskRows.ContainsKey(project) || !IsVisibleTaskProject(project) || _projectArchiveWrites.Contains(project)) return false;
        if (category?.Equals(TaskUncategorizedCategory, StringComparison.OrdinalIgnoreCase) == true) category = null;
        if (category is not null &&
            !_projectCategoryCatalog.GetCategories(CurrentTaskCategoryWorkspace).Contains(category, StringComparer.OrdinalIgnoreCase)) return false;
        if (!_projectCategoryCatalog.Assign(CurrentTaskCategoryWorkspace, project, category)) return false;
        SaveSettings();
        UpdateProjectTaskTree();
        return true;
    }

    private TreeViewItem? GetTaskCategoryGroup(string category) => _taskCategoryGroups.GetValueOrDefault(category);

    private TreeViewItem CreateTaskCategoryEntry()
    {
        var create = new Button
        {
            Content = "+새카테고리", FontSize = 12, MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 4, 6, 4), ToolTip = "새 카테고리 만들기"
        };
        create.SetResourceReference(StyleProperty, "QuietIconButton");
        System.Windows.Automation.AutomationProperties.SetName(create, "새 카테고리 만들기");
        create.Click += (_, _) => ShowTaskCategoryDialog("새 카테고리 이름", null, AddTaskCategory);
        var entry = new TreeViewItem { Header = create, Focusable = false, IsTabStop = false };
        entry.SetResourceReference(StyleProperty, "TaskTreeLeaf");
        return entry;
    }

    private MenuItem CreateProjectCategoryMenuItem(string project)
    {
        var menu = new MenuItem { Header = "카테고리 지정" };
        void RefreshDestinations()
        {
            menu.Items.Clear();
            void Add(string label, string? category)
            {
                var item = new MenuItem { Header = label, IsCheckable = true,
                    IsChecked = string.Equals(_projectCategoryCatalog.GetCategory(CurrentTaskCategoryWorkspace, project) ?? TaskUncategorizedCategory, category ?? TaskUncategorizedCategory, StringComparison.OrdinalIgnoreCase),
                    IsEnabled = CanMutateTaskCategories && _projectTaskRows.ContainsKey(project) && IsVisibleTaskProject(project) && !_projectArchiveWrites.Contains(project) };
                item.Click += (_, e) => { e.Handled = true; AssignProjectToCategory(project, category); };
                menu.Items.Add(item);
            }
            Add(TaskUncategorizedCategory, null);
            foreach (string category in _projectCategoryCatalog.GetCategories(CurrentTaskCategoryWorkspace)
                .Where(value => !value.Equals(TaskUncategorizedCategory, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) Add(category, category);
        }
        RefreshDestinations();
        menu.SubmenuOpened += (_, _) => RefreshDestinations();
        return menu;
    }

    private void ShowTaskCategoryDialog(string title, string? initialValue, Func<string, bool> submit)
    {
        if (!CanMutateTaskCategories) return;
        var dialog = new Window
        {
            Title = title, Width = 360, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)FindResource("Canvas"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var input = new TextBox { Text = initialValue ?? string.Empty, MinWidth = 300, MaxLength = 64, Padding = new Thickness(8, 6, 8, 6) };
        panel.Children.Add(input);
        var feedback = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.OrangeRed, Visibility = Visibility.Collapsed };
        panel.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "취소", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "저장", MinWidth = 72, IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        dialog.Content = panel;
        save.Click += (_, _) =>
        {
            if (submit(input.Text)) dialog.DialogResult = true;
            else { feedback.Text = "이름을 확인해 주세요. 이름은 64자 이내이며 중복되거나 예약된 이름은 사용할 수 없습니다."; feedback.Visibility = Visibility.Visible; input.Focus(); input.SelectAll(); }
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog();
    }
}
