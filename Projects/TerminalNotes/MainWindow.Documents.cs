using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TerminalNotes;

public partial class MainWindow
{
    private readonly HashSet<string> _expandedDocumentFolders =
        new(StringComparer.OrdinalIgnoreCase);
    private string[] _renderedDocumentKeys = [];
    private string? _renderedDocumentQuery;
    private string? _renderedDocumentWorkspace;
    private string? _selectedDocumentTreeKey;
    private TreeViewItem? _documentTreeSelectionToRestore;
    private bool _buildingDocumentTree;

    private void FilterDocuments()
    {
        if (DocumentList is null || DocumentSearch is null)
        {
            return;
        }

        if (!string.Equals(_workspace, _renderedDocumentWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            _expandedDocumentFolders.Clear();
            _selectedDocumentTreeKey = null;
            _renderedDocumentKeys = [];
            _renderedDocumentQuery = null;
            _renderedDocumentWorkspace = _workspace;
        }

        string query = DocumentSearch.Text.Trim();
        List<DocumentEntry> matches = _documents
            .Where(entry => entry.Display.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        string[] documentKeys = matches
            .Select(entry => $"{entry.Path}\0{entry.Display}")
            .ToArray();

        if (string.Equals(query, _renderedDocumentQuery, StringComparison.OrdinalIgnoreCase) &&
            documentKeys.SequenceEqual(_renderedDocumentKeys, StringComparer.Ordinal))
        {
            return;
        }

        RememberTreeSelection();
        var root = BuildDocumentTree(matches);

        _buildingDocumentTree = true;
        try
        {
            _documentTreeSelectionToRestore = null;
            DocumentList.Items.Clear();
            if (matches.Count == 0)
            {
                DocumentList.Items.Add(new TreeViewItem
                {
                    Header = string.IsNullOrEmpty(query)
                        ? "Markdown 문서가 없습니다."
                        : "검색 결과가 없습니다.",
                    Foreground = Brushes.Gray,
                    IsEnabled = false,
                    Padding = new Thickness(6, 5, 6, 5)
                });
            }
            else
            {
                foreach (TreeViewItem item in CreateTreeItems(root, string.Empty, !string.IsNullOrEmpty(query)))
                {
                    DocumentList.Items.Add(item);
                }

                if (_documentTreeSelectionToRestore is not null)
                {
                    _documentTreeSelectionToRestore.IsSelected = true;
                }
            }
        }
        finally
        {
            _buildingDocumentTree = false;
        }

        _renderedDocumentQuery = query;
        _renderedDocumentKeys = documentKeys;
    }

    private async void DocumentList_Open(object sender, MouseButtonEventArgs e)
    {
        TreeViewItem? item = FindTreeViewItem(e.OriginalSource as DependencyObject);
        if (item?.Tag is not DocumentEntry entry)
        {
            return;
        }

        e.Handled = true;
        await OpenDocumentAsync(entry.Path);
    }

    private async void DocumentList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DocumentList.SelectedItem is not TreeViewItem item)
        {
            return;
        }

        e.Handled = true;
        if (item.Tag is DocumentEntry entry)
        {
            await OpenDocumentAsync(entry.Path);
        }
        else if (item.Tag is DocumentFolderEntry)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void RememberTreeSelection()
    {
        if (DocumentList.SelectedItem is not TreeViewItem selected)
        {
            return;
        }

        _selectedDocumentTreeKey = selected.Tag switch
        {
            DocumentEntry entry => FileTreeKey(entry.Path),
            DocumentFolderEntry folder => FolderTreeKey(folder.RelativePath),
            _ => _selectedDocumentTreeKey
        };
    }

    private static DocumentTreeBranch BuildDocumentTree(IEnumerable<DocumentEntry> entries)
    {
        var root = new DocumentTreeBranch();
        foreach (DocumentEntry entry in entries)
        {
            string[] parts = entry.Display.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                root.Files.Add(entry);
                continue;
            }

            DocumentTreeBranch branch = root;
            for (int index = 0; index < parts.Length - 1; index++)
            {
                if (!branch.Folders.TryGetValue(parts[index], out DocumentTreeBranch? child))
                {
                    child = new DocumentTreeBranch();
                    branch.Folders.Add(parts[index], child);
                }

                branch = child;
            }

            branch.Files.Add(entry);
        }

        return root;
    }

    private IEnumerable<TreeViewItem> CreateTreeItems(
        DocumentTreeBranch branch,
        string parentRelativePath,
        bool expandForSearch)
    {
        foreach ((string folderName, DocumentTreeBranch child) in branch.Folders)
        {
            string relativePath = string.IsNullOrEmpty(parentRelativePath)
                ? folderName
                : Path.Combine(parentRelativePath, folderName);
            var folder = new DocumentFolderEntry(relativePath);
            var icon = new TextBlock
            {
                Text = "▸",
                Width = 16,
                Foreground = Brushes.LightSteelBlue,
                FontWeight = FontWeights.SemiBold
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(icon);
            header.Children.Add(new TextBlock
            {
                Text = folderName,
                Foreground = Brushes.LightSteelBlue,
                FontWeight = FontWeights.SemiBold
            });

            var item = new TreeViewItem
            {
                Header = header,
                Tag = folder,
                Padding = new Thickness(3, 3, 5, 3),
                ToolTip = relativePath
            };
            item.Expanded += (_, args) =>
            {
                if (!ReferenceEquals(args.OriginalSource, item))
                {
                    return;
                }

                icon.Text = "▾";
                if (!_buildingDocumentTree && string.IsNullOrEmpty(_renderedDocumentQuery))
                {
                    _expandedDocumentFolders.Add(relativePath);
                }
            };
            item.Collapsed += (_, args) =>
            {
                if (!ReferenceEquals(args.OriginalSource, item))
                {
                    return;
                }

                icon.Text = "▸";
                if (!_buildingDocumentTree && string.IsNullOrEmpty(_renderedDocumentQuery))
                {
                    _expandedDocumentFolders.Remove(relativePath);
                }
            };
            item.Selected += (_, args) =>
            {
                if (ReferenceEquals(args.OriginalSource, item))
                {
                    _selectedDocumentTreeKey = FolderTreeKey(relativePath);
                }
            };

            foreach (TreeViewItem childItem in CreateTreeItems(child, relativePath, expandForSearch))
            {
                item.Items.Add(childItem);
            }

            bool shouldExpand = expandForSearch || _expandedDocumentFolders.Contains(relativePath);
            item.IsExpanded = shouldExpand;
            icon.Text = shouldExpand ? "▾" : "▸";
            RestoreTreeSelection(item, FolderTreeKey(relativePath));
            yield return item;
        }

        foreach (DocumentEntry entry in branch.Files
                     .OrderBy(file => Path.GetFileName(file.Path).Equals(
                         "AGENTS.md",
                         StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(file => Path.GetFileName(file.Path), StringComparer.OrdinalIgnoreCase))
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = "#",
                Width = 16,
                Foreground = Brushes.MediumAquamarine,
                FontWeight = FontWeights.Bold
            });
            header.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(entry.Path),
                Foreground = Brushes.Gainsboro
            });

            var item = new TreeViewItem
            {
                Header = header,
                Tag = entry,
                ToolTip = entry.Path,
                Padding = new Thickness(3, 3, 5, 3)
            };
            item.Selected += (_, args) =>
            {
                if (ReferenceEquals(args.OriginalSource, item))
                {
                    _selectedDocumentTreeKey = FileTreeKey(entry.Path);
                }
            };
            RestoreTreeSelection(item, FileTreeKey(entry.Path));
            yield return item;
        }
    }

    private void RestoreTreeSelection(TreeViewItem item, string itemKey)
    {
        if (string.Equals(_selectedDocumentTreeKey, itemKey, StringComparison.OrdinalIgnoreCase))
        {
            _documentTreeSelectionToRestore = item;
        }
    }

    private static TreeViewItem? FindTreeViewItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TreeViewItem item)
            {
                return item;
            }

            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    private static string FolderTreeKey(string relativePath) => "folder:" + relativePath;

    private static string FileTreeKey(string path) => "file:" + Path.GetFullPath(path);

    private sealed class DocumentTreeBranch
    {
        public SortedDictionary<string, DocumentTreeBranch> Folders { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<DocumentEntry> Files { get; } = [];
    }

    private sealed record DocumentFolderEntry(string RelativePath);
}
