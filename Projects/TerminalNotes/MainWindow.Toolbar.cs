using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TerminalNotes;

public partial class MainWindow
{
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void SessionPicker_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionPickerPopup.IsOpen) UpdateDispatcher();
        SessionPickerPopup.IsOpen = !SessionPickerPopup.IsOpen;
    }

    private void PreviewDocument_Click(object sender, RoutedEventArgs e) => ToggleDocumentPreview();
    private async void TerminalProcessToggle_Click(object sender, RoutedEventArgs e) => await ToggleSelectedTerminalProcessAsync();

    private async void TerminalRerun_Click(object sender, RoutedEventArgs e)
    {
        var tab = (sender as FrameworkElement)?.Tag as TabItem ?? TerminalTabs.SelectedItem as TabItem;
        if (tab is null || !TerminalTabs.Items.Contains(tab)) return;
        if (((TerminalTabState)tab.Tag).IsIndependent) return;
        string project = CanonicalProjectPath(((TerminalTabState)tab.Tag).Pane.WorkingDirectory);
        await RerunProjectTaskAsync(project);
    }

    private async void TerminalInterrupt_Click(object sender, RoutedEventArgs e)
    {
        var tab = (sender as FrameworkElement)?.Tag as TabItem ?? TerminalTabs.SelectedItem as TabItem;
        if (tab is null || !TerminalTabs.Items.Contains(tab)) return;
        SelectTerminal(tab, false);
        var pane = ((TerminalTabState)tab.Tag).Pane;
        if (((TerminalTabState)tab.Tag).IsIndependent) { pane.Interrupt(); return; }
        string project = CanonicalProjectPath(pane.WorkingDirectory);
        if (_rerunningProjectTasks.Contains(project))
        {
            if (_stoppingProjectTasks.Contains(project))
            {
                var runtime = RuntimeFor(project);
                runtime.SingleStopped = true;
                runtime.SingleStartupCancellation?.Cancel();
                runtime.IntentVersion++;
            }
            else await StopProjectTaskAsync(project);
            return;
        }
        pane.Interrupt();
    }

    private void TerminalHeading_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        var tab = (sender as FrameworkElement)?.Tag as TabItem ?? TerminalTabs.SelectedItem as TabItem;
        if (tab is null || !TerminalTabs.Items.Contains(tab)) return;
        e.Handled = true;
        SelectTerminal(tab, false);
        RenameTerminal(tab);
    }

    private void ToggleDocumentPreview()
    {
        if (_notesHidden) ToggleNotes();
        DocumentTabs.SelectedIndex = DocumentTabs.SelectedIndex == 0 ? 1 : 0;
    }

    private void UpdateToolbarState()
    {
        if (ToolbarSaveButton is null) return;
        ToolbarSaveButton.IsEnabled = !_documentBusy;
        NewDocumentButton.IsEnabled = OpenDocumentButton.IsEnabled = !_documentBusy;
        PreviewDocumentButton.Content = DocumentTabs.SelectedIndex == 1 ? "편집" : "미리보기";
        ToolbarSaveButton.Background = (Brush)new BrushConverter().ConvertFromString(_document.IsDirty ? "#1D4D3E" : "#182432")!;
        ToolbarSaveButton.ToolTip = _document.IsDirty ? "변경된 Markdown 저장 · Ctrl+S" : "현재 Markdown 저장 · Ctrl+S";
        FocusButton.Background = StateColorBrush(_notesHidden ? "#111114" : "#303037");
        string focusAction = _taskWorkspaceVisible
            ? (_notesHidden ? "태스크·플랜 패널 펼치기" : "태스크·플랜 패널 접기")
            : (_notesHidden ? "플랜 패널 펼치기" : "플랜 패널 접기");
        FocusButton.ToolTip = focusAction + " · Ctrl+Shift+M";
        System.Windows.Automation.AutomationProperties.SetName(FocusButton, focusAction);
        if (FocusButton.Content is System.Windows.Shapes.Path arrow)
            arrow.Data = Geometry.Parse(_notesHidden ? "M 4,3 L 9,8 L 4,13" : "M 9,3 L 4,8 L 9,13");
    }
}
