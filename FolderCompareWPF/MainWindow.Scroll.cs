using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace FolderCompareWPF;

public partial class MainWindow
{
    private bool _isSyncingScroll;
    private double? _pendingScrollA;
    private double? _pendingScrollB;

    private void ResetScrollSynchronization()
    {
        _pendingScrollA = null;
        _pendingScrollB = null;
    }

    private void RecomputeAndDrawMarkers()
    {
        CanvasScrollMarkers.Children.Clear();
        if (_isLoadingEditors || _isDirtyA || _isDirtyB || _currentDiffRows.Count == 0) return;
        DrawSideMarkers(RtbDiffA, _paragraphsA, true);
        DrawSideMarkers(RtbDiffB, _paragraphsB, false);
    }

    private void DrawSideMarkers(System.Windows.Controls.RichTextBox editor, List<Paragraph?> paragraphs, bool sideA)
    {
        double height = CanvasScrollMarkers.ActualHeight;
        double width = CanvasScrollMarkers.ActualWidth / 2;
        double extent = Math.Max(editor.ExtentHeight, editor.ViewportHeight);
        if (extent <= 0 || height <= 0) return;
        double left = sideA ? 0 : width;
        for (int i = 0; i < Math.Min(paragraphs.Count, _currentDiffRows.Count); i++)
        {
            var row = _currentDiffRows[i];
            DiffType type = (sideA ? row.BackgroundTypeA : row.BackgroundTypeB) ?? row.Type;
            if (type == DiffType.Same || paragraphs[i] is not Paragraph paragraph) continue;
            Rect rect = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) continue;
            // Character rectangles are viewport-relative, while the map uses document coordinates.
            double top = Math.Clamp(rect.Top + editor.VerticalOffset, 0, extent);
            var marker = new System.Windows.Shapes.Rectangle
            {
                Width = width, Height = 3,
                Fill = new SolidColorBrush(type == DiffType.Added ? Color.FromRgb(92, 207, 144) :
                    type == DiffType.Removed ? Color.FromRgb(249, 120, 139) : Color.FromRgb(238, 197, 75)),
                ToolTip = $"{(sideA ? "A" : "B")} · { (sideA ? row.LineNoA : row.LineNoB)}행",
                Tag = (sideA, i)
            };
            Canvas.SetTop(marker, Math.Clamp(top / extent * height, 0, Math.Max(0, height - 3)));
            Canvas.SetLeft(marker, left);
            CanvasScrollMarkers.Children.Add(marker);
        }
        var viewport = new System.Windows.Shapes.Rectangle
        {
            Width = width, Height = Math.Min(height, Math.Max(2, editor.ViewportHeight / extent * height)),
            Stroke = new SolidColorBrush(Color.FromRgb(174, 194, 222)), StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(25, 174, 194, 222)), IsHitTestVisible = false
        };
        Canvas.SetLeft(viewport, left);
        Canvas.SetTop(viewport, Math.Clamp(editor.VerticalOffset / extent * height, 0, Math.Max(0, height - viewport.Height)));
        CanvasScrollMarkers.Children.Add(viewport);
    }

    private void CanvasScrollMarkers_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CanvasScrollMarkers.ActualHeight <= 0 || _isDirtyA || _isDirtyB) return;
        bool sideA = e.GetPosition(CanvasScrollMarkers).X < CanvasScrollMarkers.ActualWidth / 2;
        var editor = sideA ? RtbDiffA : RtbDiffB;
        double offset = e.GetPosition(CanvasScrollMarkers).Y / CanvasScrollMarkers.ActualHeight * editor.ExtentHeight;
        if (e.OriginalSource is FrameworkElement { Tag: ValueTuple<bool, int> tag })
        {
            sideA = tag.Item1;
            editor = sideA ? RtbDiffA : RtbDiffB;
            var paragraph = (sideA ? _paragraphsA : _paragraphsB)[tag.Item2];
            if (paragraph != null) offset = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top + editor.VerticalOffset;
        }
        editor.ScrollToVerticalOffset(Math.Max(0, offset - editor.ViewportHeight / 2));
        e.Handled = true;
    }

    private void CanvasScrollMarkers_SizeChanged(object sender, SizeChangedEventArgs e) => RecomputeAndDrawMarkers();
    private void RtbDiffA_ScrollChanged(object sender, ScrollChangedEventArgs e) => HandleScroll(RtbDiffA, RtbDiffB, true, e);
    private void RtbDiffB_ScrollChanged(object sender, ScrollChangedEventArgs e) => HandleScroll(RtbDiffB, RtbDiffA, false, e);

    private void HandleScroll(System.Windows.Controls.RichTextBox source, System.Windows.Controls.RichTextBox target, bool sideA, ScrollChangedEventArgs e)
    {
        LnDiffATransform.Y = -RtbDiffA.VerticalOffset;
        LnDiffBTransform.Y = -RtbDiffB.VerticalOffset;
        // ScrollToVerticalOffset completes during a later layout pass. A synchronous
        // guard alone cannot prevent that delayed event from scrolling the driver back.
        double? pending = sideA ? _pendingScrollA : _pendingScrollB;
        if (pending.HasValue && e.VerticalChange != 0)
        {
            if (sideA) _pendingScrollA = null;
            else _pendingScrollB = null;
            if (Math.Abs(source.VerticalOffset - pending.Value) < 1)
            {
                RecomputeAndDrawMarkers();
                return;
            }
        }
        if (_isSyncingScroll || _isLoadingEditors) return;
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            _isSyncingScroll = true;
            try
            {
                var sourceParagraphs = sideA ? _paragraphsA : _paragraphsB;
                var targetParagraphs = sideA ? _paragraphsB : _paragraphsA;
                double offset = source.VerticalOffset;
                if (!_isDirtyA && !_isDirtyB)
                {
                    // Anchor to the same diff row, including files with inserted or removed lines.
                    for (int i = 0; i < Math.Min(sourceParagraphs.Count, targetParagraphs.Count); i++)
                    {
                        if (sourceParagraphs[i] is not Paragraph a || targetParagraphs[i] is not Paragraph b) continue;
                        Rect rectA = a.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                        Rect rectB = b.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                        if (rectA.IsEmpty || rectB.IsEmpty || rectA.Bottom < 0) continue;
                        offset = rectB.Top + target.VerticalOffset - rectA.Top;
                        break;
                    }
                }
                offset = Math.Clamp(offset, 0, Math.Max(0, target.ExtentHeight - target.ViewportHeight));
                if (Math.Abs(target.VerticalOffset - offset) >= 0.5)
                {
                    if (sideA) _pendingScrollB = offset;
                    else _pendingScrollA = offset;
                    target.ScrollToVerticalOffset(offset);
                }
            }
            finally { _isSyncingScroll = false; }
        }
        RecomputeAndDrawMarkers();
    }
}
