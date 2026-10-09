using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskAtlas.Core;

namespace DiskAtlas.App;

public sealed class ScanNodeEventArgs(ScanNode node) : EventArgs { public ScanNode Node { get; } = node; }

public sealed class TreemapControl : FrameworkElement
{
    private const int NodeLimit = 1500;
    private const int FrameLimit = 700;
    private const int DominantTypeWorkLimit = 12000;
    private readonly List<Tile> _tiles = [];
    private readonly List<Tile> _folderFrames = [];
    private readonly HashSet<ScanNode> _aggregates = [];
    private readonly Dictionary<ScanNode, string> _dominantExtensions = [];
    private ScanNode? _root;
    private ScanNode? _selected;
    private ScanNode? _hovered;
    private string? _highlightedExtension;
    private int _dominantTypeWork;
    private DrawingGroup? _mapDrawing;
    private Size _mapDrawingSize;

    private static readonly Brush CanvasBrush = FrozenBrush(Color.FromRgb(15, 23, 42));
    private static readonly Brush MutedTextBrush = FrozenBrush(Color.FromRgb(148, 163, 184));
    private static readonly Brush DarkTextBrush = FrozenBrush(Color.FromRgb(15, 23, 42));
    private static readonly Brush SoftWhiteBrush = FrozenBrush(Color.FromArgb(220, 255, 255, 255));
    private static readonly Pen CanvasPen = FrozenPen(Color.FromRgb(51, 65, 85), 1);
    private static readonly Pen FolderPen = FrozenPen(Color.FromArgb(190, 226, 232, 240), .8);
    private static readonly Pen HoverPen = FrozenPen(Color.FromRgb(255, 255, 255), 2);
    private static readonly Pen SelectedPen = FrozenPen(Color.FromRgb(250, 204, 21), 3);

    public event EventHandler<ScanNodeEventArgs>? NodeSelected;
    public event EventHandler<ScanNodeEventArgs>? FolderInvoked;
    public int VisibleCount { get; private set; }
    public bool UseAllocatedSize { get; private set; }
    public string? HighlightedExtension => _highlightedExtension;

    public TreemapControl()
    {
        SnapsToDevicePixels = true;
        Focusable = true;
        MouseMove += (_, e) =>
        {
            var node = Hit(e.GetPosition(this));
            ToolTip = node is null ? null : TooltipFor(node);
            if (ReferenceEquals(node, _hovered)) return;
            _hovered = node;
            InvalidateVisual();
        };
        MouseLeave += (_, _) =>
        {
            ToolTip = null;
            if (_hovered is null) return;
            _hovered = null;
            InvalidateVisual();
        };
    }

    public void SetRoot(ScanNode root, bool useAllocatedSize, ScanNode? selected = null)
    {
        _dominantExtensions.Clear();
        _root = root;
        UseAllocatedSize = useAllocatedSize;
        _selected = selected;
        _mapDrawing = null;
        InvalidateVisual();
    }

    public void SelectNode(ScanNode? node) { _selected = node; InvalidateVisual(); }

    public void HighlightExtension(string? extension)
    {
        _highlightedExtension = extension is null ? null : NormalizeExtension(extension);
        _mapDrawing = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = new Size(ActualWidth, ActualHeight);
        if (_mapDrawing is null || _mapDrawingSize != size)
        {
            var drawing = new DrawingGroup();
            using (var mapDc = drawing.Open()) RenderMap(mapDc);
            drawing.Freeze();
            _mapDrawing = drawing;
            _mapDrawingSize = size;
        }
        dc.DrawDrawing(_mapDrawing);
        DrawInteractionOutline(dc, _hovered, HoverPen);
        DrawInteractionOutline(dc, _selected, SelectedPen);
    }

    private void RenderMap(DrawingContext dc)
    {
        _tiles.Clear();
        _folderFrames.Clear();
        _aggregates.Clear();
        _dominantTypeWork = 0;
        VisibleCount = 0;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(CanvasBrush, CanvasPen, bounds);
        if (_root is null || bounds.Width < 4 || bounds.Height < 4)
        {
            DrawText(dc, "분석하면 파일 크기와 비율이 여기에 표시됩니다.", 12, new Point(12, 12), MutedTextBrush, bounds.Width - 24);
            return;
        }
        if (!_root.Children.Any(n => Weight(n) > 0))
        {
            DrawText(dc, "이 폴더에는 크기가 있는 항목이 없습니다.", 12, new Point(12, 12), MutedTextBrush, bounds.Width - 24);
            return;
        }

        var leafBudget = NodeLimit;
        var frameBudget = FrameLimit;
        DrawLevel(dc, _root.Children, bounds, 0, NodeLimit, ref leafBudget, ref frameBudget, Weight(_root));
        VisibleCount = _tiles.Count;
    }

    private void DrawLevel(DrawingContext dc, IEnumerable<ScanNode> source, Rect bounds, int depth, int quota,
        ref int leafBudget, ref int frameBudget, long expectedWeight)
    {
        if (quota <= 0 || leafBudget <= 0 || bounds.Width < 2 || bounds.Height < 2) return;
        var selection = SelectLargest(source, quota);
        if (selection.Nodes.Count == 0) return;
        var denominator = Math.Max(selection.TotalWeight, Math.Max(0, expectedWeight));
        var levelTiles = new List<Tile>(selection.Nodes.Count);
        Squarify(selection.Nodes.Select(n => new WeightedNode(n, Math.Max(1, Weight(n)))).ToList(), bounds, levelTiles, denominator);
        var slots = AllocateSlots(levelTiles, quota);

        foreach (var tile in levelTiles)
            if (tile.Node.IsDirectory) DrawFolderFrame(dc, tile, depth, ref frameBudget);

        foreach (var tile in levelTiles)
        {
            if (leafBudget <= 0) break;
            var allotted = slots.GetValueOrDefault(tile.Node);
            if (allotted <= 0) continue;
            if (!tile.Node.IsDirectory)
            {
                DrawLeaf(dc, tile);
                leafBudget--;
                continue;
            }
            var inner = InnerBounds(tile.Bounds, depth);
            var before = _tiles.Count;
            if (inner.Width >= 5 && inner.Height >= 5 && tile.Node.Children.Count > 0)
                DrawLevel(dc, tile.Node.Children, inner, depth + 1, Math.Min(allotted, leafBudget), ref leafBudget, ref frameBudget, Weight(tile.Node));
            if (_tiles.Count == before && leafBudget > 0)
            {
                DrawAggregate(dc, tile, depth);
                leafBudget--;
            }
        }
    }

    private void DrawFolderFrame(DrawingContext dc, Tile tile, int depth, ref int frameBudget)
    {
        if (frameBudget <= 0) return;
        frameBudget--;
        _folderFrames.Add(tile);
        var rect = Inset(tile.Bounds, .7);
        if (rect.IsEmpty) return;
        var extension = DominantExtension(tile.Node);
        var opacity = _highlightedExtension is null ? .30 : ExtensionMatches(extension) ? .48 : .10;
        dc.PushOpacity(opacity);
        dc.DrawRectangle(FileTypeColors.GetBrush(extension), null, rect);
        dc.Pop();
        dc.DrawRectangle(null, FolderPen, rect);
        var header = HeaderHeight(rect, depth);
        if (header > 5) DrawText(dc, tile.Node.Name, 9.5, new Point(rect.X + 4, rect.Y + 2), SoftWhiteBrush, rect.Width - 8);
    }

    private void DrawLeaf(DrawingContext dc, Tile tile)
    {
        _tiles.Add(tile);
        var rect = Inset(tile.Bounds, .65);
        var dimmed = _highlightedExtension is not null && !ExtensionMatches(tile.Node.Extension);
        if (dimmed) dc.PushOpacity(.16);
        dc.DrawRectangle(FileTypeColors.GetBrush(tile.Node.Extension), null, rect);
        if (rect.Width > 54 && rect.Height > 25)
        {
            var textBrush = ReadableTextBrush(FileTypeColors.GetColor(tile.Node.Extension));
            DrawText(dc, tile.Node.Name, 9.5, new Point(rect.X + 4, rect.Y + 3), textBrush, rect.Width - 8);
            if (rect.Height > 42) DrawText(dc, Format.Bytes(Weight(tile.Node)), 8.5, new Point(rect.X + 4, rect.Y + 18), textBrush, rect.Width - 8);
        }
        if (dimmed) dc.Pop();
    }

    private void DrawAggregate(DrawingContext dc, Tile tile, int depth)
    {
        _tiles.Add(tile);
        _aggregates.Add(tile.Node);
        var rect = Inset(tile.Bounds, 1.6);
        var extension = DominantExtension(tile.Node);
        var dimmed = _highlightedExtension is not null && !ExtensionMatches(extension);
        if (dimmed) dc.PushOpacity(.18);
        dc.DrawRectangle(FileTypeColors.GetBrush(extension), FolderPen, rect);
        if (rect.Width > 58 && rect.Height > 27)
        {
            var label = depth < 2 ? tile.Node.Name : $"{tile.Node.Name} · 묶음";
            DrawText(dc, label, 9.5, new Point(rect.X + 4, rect.Y + 3), ReadableTextBrush(FileTypeColors.GetColor(extension)), rect.Width - 8);
        }
        if (dimmed) dc.Pop();
    }

    private Selection SelectLargest(IEnumerable<ScanNode> source, int limit)
    {
        var queue = new PriorityQueue<ScanNode, long>();
        double total = 0;
        foreach (var node in source)
        {
            var weight = Weight(node);
            if (weight <= 0) continue;
            total += weight;
            if (queue.Count < limit) queue.Enqueue(node, weight);
            else if (queue.TryPeek(out _, out var smallest) && weight > smallest)
            {
                queue.Dequeue();
                queue.Enqueue(node, weight);
            }
        }
        var nodes = new List<ScanNode>(queue.Count);
        while (queue.TryDequeue(out var node, out _)) nodes.Add(node);
        nodes.Sort((left, right) => Weight(right).CompareTo(Weight(left)));
        return new(nodes, total);
    }

    private Dictionary<ScanNode, int> AllocateSlots(IReadOnlyList<Tile> tiles, int quota)
    {
        var result = new Dictionary<ScanNode, int>(tiles.Count);
        var available = quota;
        foreach (var tile in tiles)
        {
            if (available <= 0) { result[tile.Node] = 0; continue; }
            result[tile.Node] = 1;
            available--;
        }
        if (available <= 0) return result;
        var directories = tiles.Where(t => t.Node.IsDirectory).ToArray();
        var total = directories.Sum(t => (double)Math.Max(1, Weight(t.Node)));
        var distributed = 0;
        foreach (var tile in directories)
        {
            var extra = total <= 0 ? 0 : (int)Math.Floor(available * Math.Max(1, Weight(tile.Node)) / total);
            result[tile.Node] += extra;
            distributed += extra;
        }
        for (var i = 0; distributed < available && directories.Length > 0; i++, distributed++)
            result[directories[i % directories.Length].Node]++;
        return result;
    }

    private string DominantExtension(ScanNode directory)
    {
        if (_dominantExtensions.TryGetValue(directory, out var cached)) return cached;
        var current = directory;
        var extension = "";
        for (var depth = 0; depth < 64 && current.IsDirectory && current.Children.Count > 0; depth++)
        {
            ScanNode? largest = null;
            long largestWeight = -1;
            foreach (var child in current.Children)
            {
                if (_dominantTypeWork++ >= DominantTypeWorkLimit) break;
                var weight = Weight(child);
                if (weight > largestWeight) { largest = child; largestWeight = weight; }
            }
            largest ??= current.Children.FirstOrDefault(n => Weight(n) > 0);
            if (largest is null) break;
            current = largest;
            if (!current.IsDirectory) extension = current.Extension;
        }
        _dominantExtensions[directory] = extension;
        return extension;
    }

    private long Weight(ScanNode node) => UseAllocatedSize ? node.AllocatedSize : node.Size;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (Hit(e.GetPosition(this)) is not { } node) return;
        _selected = node;
        InvalidateVisual();
        NodeSelected?.Invoke(this, new(node));
        if (e.ClickCount == 2 && node.IsDirectory) FolderInvoked?.Invoke(this, new(node));
    }

    private ScanNode? Hit(Point point)
    {
        var leaf = _tiles.LastOrDefault(t => t.Bounds.Contains(point));
        if (leaf is not null) return leaf.Node;
        return _folderFrames.LastOrDefault(t => t.Bounds.Contains(point))?.Node;
    }

    private string TooltipFor(ScanNode node)
    {
        var aggregate = _aggregates.Contains(node) ? "\n폴더의 작은 파일을 합쳐 표시한 영역 · 색상은 대표 파일 유형을 표시" : "";
        return $"{node.Name}\n{Format.Bytes(Weight(node))}{aggregate}\n{node.FullPath}";
    }

    private void DrawInteractionOutline(DrawingContext dc, ScanNode? node, Pen pen)
    {
        if (node is null) return;
        var tile = _tiles.LastOrDefault(t => ReferenceEquals(t.Node, node))
            ?? _folderFrames.LastOrDefault(t => ReferenceEquals(t.Node, node));
        if (tile is not null) dc.DrawRectangle(null, pen, Inset(tile.Bounds, 1.1));
    }

    private bool ExtensionMatches(string extension) => string.Equals(_highlightedExtension, NormalizeExtension(extension), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExtension(string extension)
    {
        var value = extension.Trim();
        if (value.Equals("(확장자 없음)", StringComparison.OrdinalIgnoreCase)) return "";
        if (value.Length == 0) return "";
        return (value[0] == '.' ? value : "." + value).ToLowerInvariant();
    }

    private static Rect InnerBounds(Rect bounds, int depth)
    {
        var rect = Inset(bounds, 1.8);
        var header = HeaderHeight(rect, depth);
        return new Rect(rect.X + 1, rect.Y + header, Math.Max(0, rect.Width - 2), Math.Max(0, rect.Height - header - 1));
    }

    private static Rect Inset(Rect bounds, double amount) => new(bounds.X + amount, bounds.Y + amount,
        Math.Max(0, bounds.Width - amount * 2), Math.Max(0, bounds.Height - amount * 2));
    private static double HeaderHeight(Rect rect, int depth) => depth < 3 && rect.Height > 38 && rect.Width > 52 ? Math.Min(18, rect.Height * .20) : 2.5;

    private static Brush ReadableTextBrush(Color color)
    {
        var luminance = (.2126 * color.R + .7152 * color.G + .0722 * color.B) / 255;
        return luminance > .61 ? DarkTextBrush : Brushes.White;
    }

    private static Brush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen FrozenPen(Color color, double thickness) { var pen = new Pen(FrozenBrush(color), thickness); pen.Freeze(); return pen; }

    private static void DrawText(DrawingContext dc, string text, double size, Point origin, Brush brush, double maxWidth)
    {
        if (maxWidth <= 4) return;
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, 1.0) { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, origin);
    }

    private static void Squarify(List<WeightedNode> items, Rect bounds, List<Tile> output, double fullWeight)
    {
        var total = Math.Max(fullWeight, items.Sum(i => i.Weight));
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        var scaled = items.Select(i => i with { Weight = i.Weight * bounds.Width * bounds.Height / total }).ToList();
        var row = new List<WeightedNode>();
        var remaining = bounds;
        while (scaled.Count > 0)
        {
            var next = scaled[0];
            var side = Math.Min(remaining.Width, remaining.Height);
            if (row.Count == 0 || Worst(row.Append(next), side) <= Worst(row, side)) { row.Add(next); scaled.RemoveAt(0); }
            else LayoutRow(row, ref remaining, output);
        }
        if (row.Count > 0) LayoutRow(row, ref remaining, output);
    }

    private static double Worst(IEnumerable<WeightedNode> row, double side)
    {
        var values = row.Select(x => x.Weight).ToArray();
        if (values.Length == 0 || side <= 0) return double.MaxValue;
        var sum = values.Sum(); var square = sum * sum; var sideSquare = side * side;
        return Math.Max(sideSquare * values.Max() / square, square / (sideSquare * values.Min()));
    }

    private static void LayoutRow(List<WeightedNode> row, ref Rect remaining, List<Tile> output)
    {
        var sum = row.Sum(x => x.Weight);
        if (remaining.Width >= remaining.Height)
        {
            var width = remaining.Height <= 0 ? 0 : sum / remaining.Height; var y = remaining.Y;
            foreach (var item in row) { var height = width <= 0 ? 0 : item.Weight / width; output.Add(new(item.Node, new(remaining.X, y, width, height))); y += height; }
            remaining = new(remaining.X + width, remaining.Y, Math.Max(0, remaining.Width - width), remaining.Height);
        }
        else
        {
            var height = remaining.Width <= 0 ? 0 : sum / remaining.Width; var x = remaining.X;
            foreach (var item in row) { var width = height <= 0 ? 0 : item.Weight / height; output.Add(new(item.Node, new(x, remaining.Y, width, height))); x += width; }
            remaining = new(remaining.X, remaining.Y + height, remaining.Width, Math.Max(0, remaining.Height - height));
        }
        row.Clear();
    }

    private sealed record Selection(List<ScanNode> Nodes, double TotalWeight);
    private sealed record WeightedNode(ScanNode Node, double Weight);
    private sealed record Tile(ScanNode Node, Rect Bounds);
}
