using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private Border? _projectPlanInputNotice;

    internal void ShowProjectPlanInputNotice(bool visible)
    {
        if (_projectPlanInputNotice is null)
        {
            if (!visible) return;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            SetRow(_browser, 1);
            SetRow(_notice, 1);
            _projectPlanInputNotice = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 48, 64)),
                Padding = new Thickness(10, 6, 10, 6),
                Child = new TextBlock
                {
                    Text = "플랜이 없습니다. 아래 터미널에 작업 목표나 플랜을 입력해 주세요.",
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_projectPlanInputNotice, "ProjectPlanInputNotice");
            Children.Add(_projectPlanInputNotice);
            InputSubmitted += _ => ShowProjectPlanInputNotice(false);
        }
        _projectPlanInputNotice.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}