using TerminalNotes.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TerminalNotes;

internal sealed class TaskNotificationMinutesDialog : Window
{
    private readonly TextBox _minutesInput;
    private readonly TextBlock _errorText;

    public int Minutes { get; private set; }

    public TaskNotificationMinutesDialog(Window owner)
    {
        Owner = owner;
        Title = "\uC54C\uB9BC \uBBF8\uB8E8\uAE30";
        Width = 380;
        Height = 230;
        MinWidth = Width;
        MinHeight = Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 20, 23));
        Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 244, 245));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 14;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "\uC5BC\uB9C8\uB098 \uC54C\uB9BC\uC744 \uBBF8\uB8F0\uAE4C\uC694?",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(heading);

        var description = new TextBlock
        {
            Text = "\uBD84 \uB2E8\uC704\uB85C \uC591\uC218\uB97C \uC785\uB825\uD558\uC138\uC694.",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170)),
            Margin = new Thickness(0, 0, 0, 7)
        };
        Grid.SetRow(description, 1);
        root.Children.Add(description);

        _minutesInput = new TextBox
        {
            Text = "15",
            Padding = new Thickness(8, 6, 8, 6),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(9, 9, 11)),
            Foreground = Foreground,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(72, 81, 93)),
            BorderThickness = new Thickness(1),
            CaretBrush = Foreground
        };
        _minutesInput.PreviewKeyDown += MinutesInput_PreviewKeyDown;
        Grid.SetRow(_minutesInput, 2);
        root.Children.Add(_minutesInput);

        _errorText = new TextBlock
        {
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 167, 167)),
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_errorText, 3);
        root.Children.Add(_errorText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = CreateButton("\uCDE8\uC18C");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => DialogResult = false;
        var apply = CreateButton("\uBBF8\uB8E8\uAE30", primary: true);
        apply.IsDefault = true;
        apply.Click += (_, _) => AcceptMinutes();
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _minutesInput.Focus();
            _minutesInput.SelectAll();
        };
    }

    private static Button CreateButton(string text, bool primary = false) => new()
    {
        Content = text,
        Padding = new Thickness(13, 7, 13, 7),
        Margin = new Thickness(8, 10, 0, 0),
        MinWidth = 72,
        Background = new System.Windows.Media.SolidColorBrush(primary
            ? System.Windows.Media.Color.FromRgb(37, 99, 235)
            : System.Windows.Media.Color.FromRgb(32, 32, 36)),
        Foreground = System.Windows.Media.Brushes.White,
        BorderBrush = new System.Windows.Media.SolidColorBrush(primary
            ? System.Windows.Media.Color.FromRgb(37, 99, 235)
            : System.Windows.Media.Color.FromRgb(48, 48, 55)),
        BorderThickness = new Thickness(1)
    };

    private void MinutesInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AcceptMinutes();
        }
    }

    private void AcceptMinutes()
    {
        if (!TaskNotificationPreferences.TryGetSnoozeUntil(_minutesInput.Text, DateTimeOffset.UtcNow, out var untilUtc))
        {
            _errorText.Text = "\uC624\uB958: 1\uBD84 \uC774\uC0C1\uC758 \uC720\uD6A8\uD55C \uC815\uC218\uB97C \uC785\uB825\uD558\uC138\uC694.";
            _errorText.Visibility = Visibility.Visible;
            _minutesInput.Focus();
            _minutesInput.SelectAll();
            return;
        }

        Minutes = checked((int)Math.Ceiling((untilUtc - DateTimeOffset.UtcNow).TotalMinutes));
        DialogResult = true;
    }
}
