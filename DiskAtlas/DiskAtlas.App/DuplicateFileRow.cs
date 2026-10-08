using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using DiskAtlas.Core;

namespace DiskAtlas.App;

public sealed class DuplicateFileRow : INotifyPropertyChanged, IPathItem
{
    private bool _isSelected;
    private string _error = "";

    public DuplicateFileRow(DuplicateFile file, string groupId, string groupLabel, int filesInGroup)
    {
        File = file;
        Node = new ScanNode
        {
            Name = Path.GetFileName(file.FullPath),
            FullPath = file.FullPath,
            Size = file.Size,
            AllocatedSize = file.Node.AllocatedSize,
            LastWriteTimeUtc = file.LastWriteTimeUtc,
            Attributes = file.Node.Attributes
        };
        GroupId = groupId;
        GroupLabel = groupLabel;
        FilesInGroup = filesInGroup;
    }

    public DuplicateFile File { get; }
    public ScanNode Node { get; }
    public string GroupId { get; }
    public string GroupLabel { get; }
    public int FilesInGroup { get; }
    public string Name => Path.GetFileName(File.FullPath);
    public string Directory => Path.GetDirectoryName(File.FullPath) ?? "";
    public string FullPath => File.FullPath;
    public long Size => File.Size;
    public string SizeText => Format.Bytes(File.Size);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Changed();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Error
    {
        get => _error;
        set { if (_error == value) return; _error = value; Changed(); }
    }

    public event EventHandler? SelectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
