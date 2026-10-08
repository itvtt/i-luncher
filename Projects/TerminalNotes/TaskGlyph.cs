using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TerminalNotes;

internal enum TaskGlyphKind { Multi, Single, Play, Resume, Stop, Open, Wait, Pause, Attention, Complete, Folder }

/// <summary>Small, font-independent symbols shared by task actions and status cells.</summary>
internal sealed class TaskGlyph : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(TaskGlyphKind), typeof(TaskGlyph),
        new FrameworkPropertyMetadata(TaskGlyphKind.Wait, FrameworkPropertyMetadataOptions.AffectsRender));

    public TaskGlyphKind Kind { get => (TaskGlyphKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private static readonly IReadOnlyDictionary<TaskGlyphKind, Geometry> Outlines = new Dictionary<TaskGlyphKind, Geometry>
    {
        [TaskGlyphKind.Multi] = Shape("M2,6 L2,2 11,2 11,4 M5,5 L14,5 14,14 5,14 Z"),
        [TaskGlyphKind.Single] = Shape("M2,3 L14,3 14,13 2,13 Z M2,6 L14,6"),
        [TaskGlyphKind.Play] = Shape("M5,3 L13,8 5,13 Z"),
        [TaskGlyphKind.Resume] = Shape("M2,3 L2,13 M6,3 L14,8 6,13 Z"),
        [TaskGlyphKind.Stop] = Shape("M4,4 L12,4 12,12 4,12 Z"),
        [TaskGlyphKind.Open] = Shape("M7,3 L3,3 3,13 13,13 13,9 M8,2 L14,2 14,8 M14,2 L7,9"),
        [TaskGlyphKind.Pause] = Shape("M5,3 L5,13 M11,3 L11,13"),
        [TaskGlyphKind.Attention] = Shape("M8,2 L15,14 1,14 Z M8,6 L8,9 M8,11.5 L8,11.6"),
        [TaskGlyphKind.Complete] = Shape("M3,8 L6.5,11.5 13,4.5"),
        [TaskGlyphKind.Folder] = Shape("M1.5,4 L6,4 8,6 14.5,6 14.5,13 1.5,13 Z M1.5,4 L1.5,2.5 6,2.5 8,4 13,4 13,6")
    };

    public TaskGlyph()
    {
        Width = Height = 16;
        Focusable = false;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    private static Geometry Shape(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double scale = Math.Min(ActualWidth, ActualHeight) / 16;
        if (scale <= 0) return;
        drawing.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (Kind == TaskGlyphKind.Wait)
            foreach (double x in new[] { 3d, 8d, 13d }) drawing.DrawEllipse(Foreground, null, new Point(x, 8), 1, 1);
        else drawing.DrawGeometry(null, pen, Outlines[Kind]);
        drawing.Pop();
    }
}
