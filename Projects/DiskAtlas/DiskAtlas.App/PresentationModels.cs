using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using DiskAtlas.Core;

namespace DiskAtlas.App;

public interface IPathItem { ScanNode Node { get; } }

public sealed class FolderRow : INotifyPropertyChanged, IPathItem
{
    private bool _isExpanded;
    public FolderRow(ScanNode node, int level) { Node = node; Level = level; }
    public ScanNode Node { get; }
    public int Level { get; }
    public string Name => Node.Name;
    public Thickness Indent => new(Level * 17, 0, 0, 0);
    public bool HasChildren => Node.Children.Any(n => n.IsDirectory);
    public Visibility ExpanderVisibility => HasChildren ? Visibility.Visible : Visibility.Hidden;
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; Changed(); Changed(nameof(ExpanderGlyph)); } }
    public string ExpanderGlyph => IsExpanded ? "▾" : "▸";
    public string SizeText => Format.Bytes(Node.Size);
    public string AllocatedText => Format.Bytes(Node.AllocatedSize);
    public string FileCountText => Node.FileCount.ToString("N0");
    public string ModifiedText => Format.Date(Node.LastWriteTimeUtc);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class FileRow : IPathItem
{
    public FileRow(ScanNode node) => Node = node;
    public ScanNode Node { get; }
    public string Name => Node.Name;
    public string Directory => Path.GetDirectoryName(Node.FullPath) ?? "";
    public string SizeText => Format.Bytes(Node.Size);
    public string AllocatedText => Format.Bytes(Node.AllocatedSize);
    public string ModifiedText => Format.Date(Node.LastWriteTimeUtc);
}

public sealed class ExtensionRow
{
    public required string Extension { get; init; }
    public required long Count { get; init; }
    public required long Size { get; init; }
    public required double Percent { get; init; }
    public string CountText => Count.ToString("N0");
    public string SizeText => Format.Bytes(Size);
    public string PercentText => Percent.ToString("0.0") + "%";
    public System.Windows.Media.Brush ColorBrush => FileTypeColors.GetBrush(Extension);
}

public sealed class SearchResultRow : IPathItem
{
    public SearchResultRow(SearchHit hit) { Hit = hit; Node = hit.Node; }
    public SearchHit Hit { get; }
    public ScanNode Node { get; }
    public string Name => Node.Name;
    public string Directory => Path.GetDirectoryName(Node.FullPath) ?? "";
    public string MatchKind => Hit.MatchKind.Equals("Name", StringComparison.OrdinalIgnoreCase) ? "이름" : Hit.MatchKind.Equals("Contents", StringComparison.OrdinalIgnoreCase) ? "내용" : Hit.MatchKind;
    public string Preview => Hit.LineNumber > 0 ? $"{Hit.LineNumber:N0}줄 · {Hit.Preview}" : Hit.Preview;
    public string SizeText => Format.Bytes(Node.Size);
}

public sealed record DriveItem(string Label, string Path)
{
    public override string ToString() => Label;
}

public sealed record SearchModeChoice(string Label, SearchMode Mode)
{
    public override string ToString() => Label;
}

public sealed record MetricBasisChoice(string Label, bool UseAllocated)
{
    public override string ToString() => Label;
}

public static class Format
{
    private static readonly string[] Suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
    public static string Bytes(long bytes)
    {
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < Suffixes.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:N0} {Suffixes[unit]}" : $"{value:N1} {Suffixes[unit]}";
    }
    public static string Date(DateTime utc) => utc == default ? "—" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public static string Duration(TimeSpan duration) => duration.TotalMinutes >= 1 ? duration.ToString(@"mm\:ss\.f") : duration.ToString(@"ss\.fff") + "초";
}
