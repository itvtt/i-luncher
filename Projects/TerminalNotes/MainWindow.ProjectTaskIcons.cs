using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TerminalNotes;

public partial class MainWindow
{
    private sealed record TaskStatusVisual(Border Surface, TextBlock Label,
        Grid Signal, TaskGlyph Symbol, AutomationActivityIndicator Indicator);

    private static void SetTaskActionIcon(ContentControl button, TaskGlyphKind kind, string name)
    {
        if (button.Content is not TextBlock glyph)
        {
            glyph = new TextBlock { FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 17,
                FontWeight = FontWeights.SemiBold, Width = 20, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false };
            glyph.SetBinding(ForegroundProperty, new Binding(nameof(Foreground)) { Source = button });
            button.Content = glyph;
        }
        glyph.Text = kind switch
        {
            TaskGlyphKind.Multi => "M",
            TaskGlyphKind.Single => "S",
            TaskGlyphKind.Play => "▶",
            TaskGlyphKind.Resume => "↻",
            TaskGlyphKind.Stop => "■",
            TaskGlyphKind.Open => "↗",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetShowOnDisabled(button, true);
    }

    private static void SetProjectTaskStatusIcon(ContentControl button, TaskGlyphKind kind, bool active,
        Brush foreground, string actionName, string statusDescription, string actionDescription)
    {
        if (active)
        {
            var indicator = button.Content as AutomationActivityIndicator ?? new AutomationActivityIndicator { Width = 16, Height = 16 };
            indicator.SetStatus(true, false, false, foreground, statusDescription);
            button.Content = indicator;
        }
        else
        {
            var glyph = button.Content as TaskGlyph ?? new TaskGlyph { Width = 16, Height = 16 };
            glyph.Kind = kind;
            glyph.Foreground = foreground;
            button.Content = glyph;
        }
        AutomationProperties.SetName(button, actionName);
        AutomationProperties.SetHelpText(button, actionDescription + "\n현재 상태: " + statusDescription);
        button.ToolTip = actionDescription + "\n현재 상태: " + statusDescription;
        ToolTipService.SetShowOnDisabled(button, true);
    }

    private static TaskStatusVisual CreateTaskStatusVisual(string name)
    {
        var label = new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var symbol = new TaskGlyph { Visibility = Visibility.Collapsed };
        var indicator = new AutomationActivityIndicator { Width = 16, Height = 16, Visibility = Visibility.Collapsed };
        var signal = new Grid { Width = 16, Height = 16 };
        signal.Children.Add(symbol); signal.Children.Add(indicator);
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        if (name.Length > 0) content.Children.Add(label);
        content.Children.Add(signal);
        var surface = new Border { Child = content, Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(3) };
        return new(surface, label, signal, symbol, indicator);
    }

    private static TaskGlyphKind TaskStateGlyph(string label, bool completed, bool needsInput)
    {
        if (completed) return TaskGlyphKind.Complete;
        if (needsInput || label.Contains("실패") || label.Contains("확인 필요") || label.Contains("확인 불가")) return TaskGlyphKind.Attention;
        if (label.Contains("일시정지")) return TaskGlyphKind.Pause;
        if (label.Contains("중단")) return TaskGlyphKind.Stop;
        return TaskGlyphKind.Wait;
    }

    private static void UpdateTaskStatusVisual(TaskStatusVisual visual, string name, string label,
        bool active, bool needsInput, bool completed, bool animateActivity = true)
    {
        var symbol = TaskStateGlyph(label, completed, needsInput);
        bool animate = animateActivity && active && symbol == TaskGlyphKind.Wait;
        if (!animateActivity && active && symbol == TaskGlyphKind.Wait) symbol = TaskGlyphKind.Play;
        string description = name.Length == 0 ? label : name + " · " + label;
        string color = completed || needsInput || active ? "#FFFFFF" : "#B8B8B8";
        Brush brush = StateColorBrush(color);
        visual.Label.Foreground = brush;
        visual.Symbol.Foreground = brush;
        bool singleWaiting = visual.Label.Text.Length == 0 && symbol == TaskGlyphKind.Wait && !animate;
        visual.Symbol.Kind = singleWaiting ? TaskGlyphKind.Single : symbol;
        visual.Surface.Background = completed ? Brushes.Transparent : needsInput ? StateColorBrush("#9A5700") :
            active ? StateColorBrush("#12643A") : Brushes.Transparent;
        visual.Surface.ToolTip = description;
        AutomationProperties.SetName(visual.Label, description);
        AutomationProperties.SetName(visual.Surface, description);
        bool showSignal = animate || symbol != TaskGlyphKind.Wait || singleWaiting;
        visual.Signal.Visibility = showSignal ? Visibility.Visible : Visibility.Collapsed;
        visual.Label.Margin = new Thickness(0, 0, showSignal ? 6 : 0, 0);
        visual.Symbol.Visibility = !animate && showSignal ? Visibility.Visible : Visibility.Collapsed;
        visual.Indicator.Visibility = animate ? Visibility.Visible : Visibility.Collapsed;
        visual.Indicator.SetStatus(animate, needsInput, completed, brush, description);
    }
}
