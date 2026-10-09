using System.ComponentModel;
using System.Windows.Controls;

namespace FolderCompareWPF;

internal sealed class FileListEntry : INotifyPropertyChanged
{
    public FileListEntry(string category, string relativePath)
    { Category = category; RelativePath = relativePath; }
    public string Category { get; }
    public string RelativePath { get; }
    public string Name => System.IO.Path.GetFileName(RelativePath);
    public object FileTag => (Category, RelativePath);
    public bool CanCopy => Category is "OnlyInA" or "Different" or "Identical";
    internal ItemsControl? Parent { get; set; }
    internal TreeViewItem? ExplicitContainer { get; set; }
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
