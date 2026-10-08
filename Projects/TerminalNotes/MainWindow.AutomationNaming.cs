using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TerminalNotes;

public partial class MainWindow
{
    private static bool IsValidAutomationProjectName(string? name)
    {
        string normalized = name?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 60;
    }

    /// <summary>Requests the project name before a direct automation start when none is recorded yet.</summary>
    internal Task<string> RequestAutomationProjectNameAsync(string project)
    {
        project = CanonicalProjectPath(project);
        var window = new Window
        {
            Owner = this,
            Title = "프로젝트 이름",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            MinWidth = 380,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(16, 24, 36)),
            Foreground = Brushes.White
        };
        window.Resources.MergedDictionaries.Add(Resources);
        NameScope.SetNameScope(window, new NameScope());

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "자동화 프로젝트 이름을 입력하세요.",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = project,
            Margin = new Thickness(0, 5, 0, 12),
            FontSize = 13,
            Foreground = Brushes.LightSteelBlue,
            TextWrapping = TextWrapping.Wrap
        });

        var nameInput = new TextBox
        {
            Name = "ProjectAutomationNameInput",
            Text = ProjectDisplayName(project),
            MaxLength = 60,
            Height = 32,
            Padding = new Thickness(9, 5, 9, 5)
        };
        window.RegisterName(nameInput.Name, nameInput);
        panel.Children.Add(nameInput);

        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(232, 164, 154)),
            Margin = new Thickness(0, 5, 0, 10)
        };
        panel.Children.Add(error);

        string? acceptedName = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 82, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        var accept = new Button { Content = "계속", IsDefault = true, MinWidth = 82, Height = 32 };
        accept.Click += (_, _) =>
        {
            if (!IsValidAutomationProjectName(nameInput.Text))
            {
                error.Text = "프로젝트 이름을 1~60자로 입력하세요.";
                nameInput.Focus();
                return;
            }

            acceptedName = nameInput.Text.Trim();
            window.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) =>
        {
            nameInput.Focus();
            nameInput.SelectAll();
        };

        if (window.ShowDialog() != true || acceptedName is null)
            throw new OperationCanceledException("프로젝트 이름 입력이 취소되어 자동화를 시작하지 않았습니다.");

        return Task.FromResult(acceptedName);
    }
}
