using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FolderCompareWPF;

public partial class MainWindow
{
    public static readonly DependencyProperty IsFileSelectedProperty = DependencyProperty.RegisterAttached(
        "IsFileSelected", typeof(bool), typeof(MainWindow), new FrameworkPropertyMetadata(false));
    public static bool GetIsFileSelected(DependencyObject item) => item is FrameworkElement { DataContext: FileListEntry entry }
        ? entry.IsSelected : (bool)item.GetValue(IsFileSelectedProperty);
    public static void SetIsFileSelected(DependencyObject item, bool value)
    {
        if (item is FrameworkElement { DataContext: FileListEntry entry }) entry.IsSelected = value;
        else item.SetValue(IsFileSelectedProperty, value);
    }

    private readonly Dictionary<string, FileListEntry> _selectedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _isApplyingFileSelection;
    private FileListEntry? _rangeSelectionAnchor;

    private void ClearFileSelection(bool resetAnchor = true)
    {
        foreach (var item in _selectedFiles.Values) item.IsSelected = false;
        _selectedFiles.Clear();
        if (resetAnchor) _rangeSelectionAnchor = null;
    }

    private void SetFileSelection(TreeViewItem item, bool control, bool shift = false)
    {
        if (item.DataContext is not FileListEntry entry) return;
        string rel = entry.RelativePath;
        if (shift && _rangeSelectionAnchor != null)
        {
            var visibleFiles = GetVisibleFileNodes(TvFileList).ToList();
            int start = visibleFiles.IndexOf(_rangeSelectionAnchor);
            int end = visibleFiles.IndexOf(entry);
            if (start >= 0 && end >= 0)
            {
                if (!control) ClearFileSelection(resetAnchor: false);
                for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++)
                {
                    var selected = visibleFiles[i];
                    string path = selected.RelativePath;
                    _selectedFiles[path] = selected;
                    selected.IsSelected = true;
                }
                UpdateCopyButton();
                return;
            }
        }
        if (!control) ClearFileSelection();
        if (control && _selectedFiles.Remove(rel)) entry.IsSelected = false;
        else
        {
            _selectedFiles[rel] = entry;
            entry.IsSelected = true;
        }
        _rangeSelectionAnchor = entry;
        UpdateCopyButton();
    }

    private void SelectFileNode(TreeViewItem item, bool control, bool shift = false)
    {
        if (_isCopying || item.Tag is not (string _, string rel)) return;
        if (!RelativePathComparer.Equals(_currentRelativePath, rel) && !ConfirmPendingChanges()) return;
        _isApplyingFileSelection = true;
        try
        {
            SetFileSelection(item, control, shift);
            item.IsSelected = true;
            item.Focus();
        }
        finally { _isApplyingFileSelection = false; }
    }

    private static IEnumerable<FileListEntry> GetVisibleFileNodes(ItemsControl parent)
    {
        foreach (object value in parent.Items)
        {
            if (value is FileListEntry entry) { yield return entry; continue; }
            if (value is not TreeViewItem item) continue;
            if (item.DataContext is FileListEntry explicitEntry) yield return explicitEntry;
            else if (item.IsExpanded)
                foreach (var child in GetVisibleFileNodes(item)) yield return child;
        }
    }

    private void TvFileList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
            e.Key is not (Key.Up or Key.Down or Key.Home or Key.End)) return;
        var files = GetVisibleFileNodes(TvFileList).ToList();
        var current = TvFileList.SelectedItem as FileListEntry ?? (TvFileList.SelectedItem as TreeViewItem)?.DataContext as FileListEntry;
        if (files.Count == 0 || current == null) return;
        int index = files.IndexOf(current);
        if (index < 0) return;
        int target = e.Key switch
        {
            Key.Home => 0,
            Key.End => files.Count - 1,
            Key.Up => Math.Max(0, index - 1),
            _ => Math.Min(files.Count - 1, index + 1)
        };
        var container = ResolveFileContainer(files[target], bringIntoView: true);
        if (container != null)
        {
            SelectFileNode(container, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift: true);
            container.BringIntoView();
        }
        e.Handled = true;
    }

    private string[] GetSelectedCopyPaths() => _selectedFiles
        .Where(pair => pair.Value.CanCopy)
        .Select(pair => pair.Key).OrderBy(path => path, RelativePathComparer).ToArray();

    private void FileContainer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || sender is not TreeViewItem { DataContext: FileListEntry } item) return;
        SelectFileNode(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private static TreeViewItem? ResolveFileContainer(object? value, bool bringIntoView = false)
    {
        if (value is TreeViewItem item) return item;
        if (value is not FileListEntry entry || entry.Parent == null) return null;
        if (entry.ExplicitContainer != null) return entry.ExplicitContainer;
        var parent = entry.Parent;
        var container = parent.ItemContainerGenerator.ContainerFromItem(entry) as TreeViewItem;
        if (container == null && bringIntoView)
        {
            parent.UpdateLayout();
            if (parent is TreeViewItem tree && tree.Template.FindName("ItemsHost", tree) is ItemsPresenter presenter)
            {
                presenter.ApplyTemplate();
                if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(presenter) > 0 &&
                    System.Windows.Media.VisualTreeHelper.GetChild(presenter, 0) is VirtualizingPanel panel)
                {
                    int index = parent.Items.IndexOf(entry);
                    if (index >= 0) panel.BringIndexIntoViewPublic(index);
                    parent.UpdateLayout();
                    container = parent.ItemContainerGenerator.ContainerFromItem(entry) as TreeViewItem;
                }
            }
        }
        return container;
    }
}
