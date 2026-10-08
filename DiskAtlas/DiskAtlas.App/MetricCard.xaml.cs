using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiskAtlas.App;

public partial class MetricCard : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(MetricCard));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(MetricCard));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(MetricCard));
    public static readonly DependencyProperty IsLastProperty = DependencyProperty.Register(nameof(IsLast), typeof(bool), typeof(MetricCard), new(false, OnIsLastChanged));

    public MetricCard()
    {
        Resources.MergedDictionaries.Add(MainWindow.CreateAppResources());
        InitializeComponent();
    }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public bool IsLast { get => (bool)GetValue(IsLastProperty); set => SetValue(IsLastProperty, value); }

    private static void OnIsLastChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MetricCard)d).Edge.BorderThickness = (bool)e.NewValue ? new Thickness(0) : new Thickness(0, 0, 1, 0);
}
