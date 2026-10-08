using System.Windows;
using System.Windows.Controls;

namespace TerminalNotes;

public partial class MainWindow
{
    private TabItem? ActionTerminal(object sender) =>
        (sender as FrameworkElement)?.Tag as TabItem ?? TerminalTabs.SelectedItem as TabItem;

    private void TerminalClose_Click(object sender, RoutedEventArgs e)
    {
        var tab = ActionTerminal(sender);
        if (tab is not null && TerminalTabs.Items.Contains(tab))
            CloseTerminal(tab, ((TerminalTabState)tab.Tag).Pane);
    }
}
