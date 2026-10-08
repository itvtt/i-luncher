using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TerminalNotes;

/// <summary>An activity signal, never an estimated completion percentage.</summary>
internal sealed class AutomationActivityIndicator : FrameworkElement
{
    private static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        nameof(Pulse), typeof(double), typeof(AutomationActivityIndicator),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private double Pulse { get => (double)GetValue(PulseProperty); set => SetValue(PulseProperty, value); }
    private bool _active, _needsInput, _completed;
    private Brush _accent = Brushes.Gray;
    public bool IsAnimating { get; private set; }

    public AutomationActivityIndicator()
    {
        Width = 48;
        Height = 48;
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public void SetStatus(bool active, bool needsInput, bool completed, Brush accent, string description)
    {
        _active = active;
        _needsInput = needsInput;
        _completed = completed;
        _accent = accent;
        AutomationProperties.SetName(this, description);
        UpdateAnimation();
        InvalidateVisual();
    }

    private void UpdateAnimation()
    {
        bool animate = _active && IsVisible;
        if (animate == IsAnimating) return;
        IsAnimating = animate;
        BeginAnimation(PulseProperty, animate ? new DoubleAnimation(0, Math.PI * 2, TimeSpan.FromSeconds(1.2))
        {
            RepeatBehavior = RepeatBehavior.Forever
        } : null);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double scale = Math.Min(ActualWidth, ActualHeight) / 48;
        if (scale <= 0) return;
        drawing.PushTransform(new TranslateTransform((ActualWidth - 48 * scale) / 2, (ActualHeight - 48 * scale) / 2));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        drawing.PushOpacity(.12);
        drawing.DrawEllipse(_accent, null, new Point(24, 24), 22, 22);
        drawing.Pop();
        drawing.DrawEllipse(null, new Pen(_accent, 2), new Point(24, 24), 20, 20);
        if (_completed)
        {
            var pen = new Pen(_accent, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawing.DrawLine(pen, new Point(15, 24), new Point(21, 30));
            drawing.DrawLine(pen, new Point(21, 30), new Point(33, 18));
        }
        else if (_needsInput)
        {
            drawing.DrawRoundedRectangle(_accent, null, new Rect(17, 16, 5, 16), 1, 1);
            drawing.DrawRoundedRectangle(_accent, null, new Rect(26, 16, 5, 16), 1, 1);
        }
        else
        {
            for (int bar = 0; bar < 3; bar++)
            {
                double height = _active ? 8 + 12 * (.5 + .5 * Math.Sin(Pulse + bar * 1.7)) : 4;
                drawing.DrawRoundedRectangle(_accent, null, new Rect(15 + bar * 7, 24 - height / 2, 4, height), 2, 2);
            }
        }
        drawing.Pop();
        drawing.Pop();
    }
}
