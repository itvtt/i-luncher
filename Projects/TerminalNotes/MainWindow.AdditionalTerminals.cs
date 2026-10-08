using System.Windows;
using System.Windows.Controls;

namespace TerminalNotes;

public partial class MainWindow
{
    private bool _additionalTerminalsVisible;

    private static int AdditionalTerminalCapacity(int count) => count switch
    {
        <= 1 => 1,
        2 => 2,
        3 => 3,
        4 => 4,
        5 or 6 => 6,
        _ => 8
    };

    private static int TerminalLayoutColumns(int capacity) => capacity switch
    {
        2 or 4 => 2,
        3 or 6 => 3,
        8 => 4,
        _ => 1
    };

    private void ClearTerminalSplitViews()
    {
        foreach (var content in _splitContents) content.Content = null;
        _splitContents.Clear();
        SplitTerminals.Children.Clear();
        _splitFrames.Clear();
    }

    private void UpdateAdditionalTerminalHost(bool visible, int capacity)
    {
        if (_taskTerminalArea is null) return;
        if (_additionalTerminalsVisible != visible)
        {
            EndTitleEdit(false);
            // Owned task panes stay in their existing hosts. Only the independent
            // session renderer moves, without changing task ownership or slots.
            if (SplitTerminals.Parent is Panel parent) parent.Children.Remove(SplitTerminals);
            _taskTerminalArea.ColumnDefinitions.Clear();
            if (visible)
            {
                _taskTerminalArea.ColumnDefinitions.Add(new ColumnDefinition());
                _taskTerminalArea.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetRow(SplitTerminals, 1);
                Grid.SetColumn(SplitTerminals, 1);
                _taskTerminalArea.Children.Add(SplitTerminals);
                Grid.SetColumnSpan(TerminalAreaHeader, 2);
            }
            else
            {
                Grid.SetRow(SplitTerminals, 3);
                Grid.SetColumn(SplitTerminals, 0);
                TerminalHost.Children.Add(SplitTerminals);
                Grid.SetColumnSpan(TerminalAreaHeader, 1);
                ClearTerminalSplitViews();
                SplitTerminals.Visibility = Visibility.Collapsed;
                _visibleSplitTabs = [];
            }
            _additionalTerminalsVisible = visible;
            _appliedCapacity = 0;
        }
        if (visible)
        {
            // Equal column widths keep each pane usable as extras are added.
            var taskWidth = new GridLength(TerminalTabs.Items.Cast<TabItem>().Any(IsTaskTerminal) ? TerminalLayoutColumns(_splitCapacity) : 0, GridUnitType.Star);
            var extraWidth = new GridLength(TerminalLayoutColumns(capacity), GridUnitType.Star);
            if (_taskTerminalArea.ColumnDefinitions[0].Width != taskWidth) _taskTerminalArea.ColumnDefinitions[0].Width = taskWidth;
            if (_taskTerminalArea.ColumnDefinitions[1].Width != extraWidth) _taskTerminalArea.ColumnDefinitions[1].Width = extraWidth;
        }
    }
}
