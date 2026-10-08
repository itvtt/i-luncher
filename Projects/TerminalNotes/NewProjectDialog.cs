using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TerminalNotes;

internal sealed class NewProjectDialog : Window
{
    private readonly TextBox _nameInput;
    private readonly TextBlock _validationMessage;

    public string ProjectName => _nameInput.Text.Trim();

    public NewProjectDialog()
    {
        Title = "새 프로젝트";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brush("#141D28");
        Foreground = Brush("#E6EDF3");
        FontFamily = new FontFamily("Malgun Gothic, Segoe UI");
        FontSize = 14;

        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock
        {
            Text = "새 프로젝트",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "작업 폴더 바로 아래에 같은 이름의 폴더와 시작 플랜을 만듭니다.",
            Foreground = Brush("#C6D1DF"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 16)
        });

        _nameInput = new TextBox
        {
            MaxLength = 60,
            Padding = new Thickness(10),
            FontSize = 15,
            Background = Brush("#0C1016"),
            Foreground = Brush("#E6EDF3"),
            CaretBrush = Brush("#77E6BD"),
            BorderBrush = Brush("#3C685C")
        };
        AutomationProperties.SetName(_nameInput, "프로젝트 이름과 폴더 이름");
        AutomationProperties.SetHelpText(_nameInput, "최대 60자. 경로 구분자와 Windows 예약 이름은 사용할 수 없습니다.");
        content.Children.Add(_nameInput);

        _validationMessage = new TextBlock
        {
            Text = "폴더 이름은 최대 60자입니다.",
            Foreground = Brush("#C6D1DF"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 20)
        };
        AutomationProperties.SetLiveSetting(_validationMessage, AutomationLiveSetting.Polite);
        content.Children.Add(_validationMessage);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button
        {
            Content = "취소",
            IsCancel = true,
            MinWidth = 74,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(cancel, "프로젝트 만들기 취소");
        buttons.Children.Add(cancel);

        var create = new Button
        {
            Content = "만들기",
            IsDefault = true,
            MinWidth = 90,
            Padding = new Thickness(12, 8, 12, 8),
            Background = Brush("#245244"),
            Foreground = Brush("#E6EDF3")
        };
        AutomationProperties.SetName(create, "새 프로젝트 만들기 확인");
        create.Click += Confirm_Click;
        buttons.Children.Add(create);
        content.Children.Add(buttons);

        Content = content;
        Loaded += (_, _) =>
        {
            _nameInput.Focus();
        };
    }

    public static bool TryValidateFolderName(string? value, out string normalizedName, out string error)
    {
        normalizedName = value?.Trim() ?? string.Empty;
        error = string.Empty;

        if (normalizedName.Length == 0)
        {
            error = "프로젝트 이름을 입력하세요.";
            return false;
        }
        if (normalizedName.Length > 60)
        {
            error = "프로젝트 이름은 60자 이하여야 합니다.";
            return false;
        }
        if (normalizedName is "." or "..")
        {
            error = "점만으로 된 이름은 사용할 수 없습니다.";
            return false;
        }
        if (normalizedName.EndsWith('.') || normalizedName.EndsWith(' '))
        {
            error = "이름 끝에 점이나 공백을 둘 수 없습니다.";
            return false;
        }
        if (Path.IsPathRooted(normalizedName) || normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalizedName.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            normalizedName.Any(char.IsControl))
        {
            error = "경로 구분자나 사용할 수 없는 문자가 포함되어 있습니다.";
            return false;
        }

        string deviceName = normalizedName.Split('.', 2)[0].TrimEnd(' ', '.');
        if (IsReservedDeviceName(deviceName))
        {
            error = "Windows 예약 이름은 프로젝트 폴더에 사용할 수 없습니다.";
            return false;
        }

        return true;
    }

    private static bool IsReservedDeviceName(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                 name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            return name[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3';

        return false;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateFolderName(_nameInput.Text, out _, out string error))
        {
            _validationMessage.Text = error;
            _validationMessage.Foreground = Brush("#FFB86B");
            _nameInput.Focus();
            _nameInput.SelectAll();
            return;
        }

        DialogResult = true;
    }

    private static Brush Brush(string value) =>
        (Brush)new BrushConverter().ConvertFromString(value)!;
}
