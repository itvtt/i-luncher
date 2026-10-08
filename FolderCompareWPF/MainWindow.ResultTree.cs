using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FolderCompareWPF;

public partial class MainWindow
{
    private bool _deferTreeMaterialization;
    private int _treeVersion;
    private readonly Dictionary<TreeViewItem, ResultBucket> _resultBuckets = new();

    private sealed class ResultBucket
    {
        public string RelativePath { get; init; } = "";
        public string Name { get; init; } = "";
        public Dictionary<string, ResultBucket> ByName { get; } = new(RelativePathComparer);
        public List<ResultBucket> Folders { get; } = new();
        public List<FileListEntry> Files { get; } = new();
        public TreeViewItem? View { get; set; }
        public bool Started { get; set; }
        public bool Scheduled { get; set; }
        public int FolderViews { get; set; }
        public int FileViews { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<object>? BoundItems { get; set; }
    }

    private void AppendTreeItem(TreeViewItem? parent, string category, string rel)
    {
        if (parent == null) return;
        if (!_resultBuckets.TryGetValue(parent, out var bucket))
        {
            bucket = new ResultBucket { View = parent };
            _resultBuckets.Add(parent, bucket);
            ConnectBucket(bucket);
        }
        string[] parts = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!bucket.ByName.TryGetValue(parts[i], out var folder))
            {
                folder = new ResultBucket
                {
                    Name = parts[i],
                    RelativePath = System.IO.Path.Combine(bucket.RelativePath, parts[i])
                };
                bucket.ByName.Add(parts[i], folder);
                bucket.Folders.Add(folder);
                UpdateBucket(bucket);
            }
            bucket = folder;
        }
        bucket.Files.Add(new FileListEntry(category, rel));
        UpdateBucket(bucket);
    }

    private void ConnectBucket(ResultBucket bucket)
    {
        bucket.View!.Expanded += (sender, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, sender)) UpdateBucket(bucket);
        };
    }

    private void UpdateBucket(ResultBucket bucket)
    {
        if (bucket.View == null) return;
        if (!_deferTreeMaterialization) { MaterializeBucket(bucket); return; }
        if (!bucket.View.IsExpanded)
        {
            if (!bucket.Started && bucket.View.Items.Count == 0)
                bucket.View.Items.Add(new TreeViewItem { Header = "불러오는 중…", IsEnabled = false });
            return;
        }
        if (bucket.Scheduled) return;
        bucket.Scheduled = true;
        int version = _treeVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            bucket.Scheduled = false;
            if (version != _treeVersion || !bucket.View.IsExpanded) return;
            MaterializeBucket(bucket);
            if (bucket.FolderViews < bucket.Folders.Count || bucket.FileViews < bucket.Files.Count) UpdateBucket(bucket);
        }));
    }

    private void MaterializeBucket(ResultBucket bucket)
    {
        if (bucket.View == null) return;
        if (!bucket.Started && _deferTreeMaterialization && bucket.Folders.Count == 0 && bucket.Files.Count >= 1024)
        {
            // Assign a flat large collection once instead of issuing hundreds of thousands
            // of Items.Add notifications. Future arrivals still update incrementally.
            foreach (var entry in bucket.Files) entry.Parent = bucket.View;
            bucket.View.Items.Clear();
            bucket.BoundItems = new System.Collections.ObjectModel.ObservableCollection<object>(bucket.Files);
            bucket.View.ItemsSource = bucket.BoundItems;
            bucket.FileViews = bucket.Files.Count;
            bucket.Started = true;
            return;
        }
        if (!bucket.Started) { bucket.View.Items.Clear(); bucket.Started = true; }
        long started = Stopwatch.GetTimestamp();
        while (bucket.FolderViews < bucket.Folders.Count)
        {
            var folder = bucket.Folders[bucket.FolderViews];
            folder.View = new TreeViewItem
            {
                Header = folder.Name, Tag = new TreeFolderTag(folder.RelativePath), ToolTip = folder.RelativePath
            };
            ConnectBucket(folder);
            if (bucket.BoundItems != null) bucket.BoundItems.Insert(bucket.FolderViews++, folder.View);
            else bucket.View.Items.Insert(bucket.FolderViews++, folder.View);
            UpdateBucket(folder);
            if (_deferTreeMaterialization && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8) return;
        }
        while (bucket.FileViews < bucket.Files.Count)
        {
            var entry = bucket.Files[bucket.FileViews++];
            entry.Parent = bucket.View;
            if (_deferTreeMaterialization)
            {
                // Let WPF generate containers for the viewport only, not for every file.
                if (bucket.BoundItems != null) bucket.BoundItems.Add(entry);
                else bucket.View.Items.Add(entry);
            }
            else
            {
                var fileNode = new TreeViewItem { Header = entry.Name, Tag = entry.FileTag, ToolTip = entry.RelativePath, DataContext = entry };
                entry.ExplicitContainer = fileNode;
                bucket.View.Items.Add(fileNode);
            }
            if (_deferTreeMaterialization && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8) return;
        }
    }
}
