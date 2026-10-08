using System.Windows.Controls;

namespace TerminalNotes;

public partial class MainWindow
{
    private bool _autoResumeEnabled = true;
    private CheckBox? _autoResumeToggle;
    private bool _loadingAutoResumeSetting;

    private void LoadAutoResumeSetting(bool enabled)
    {
        _loadingAutoResumeSetting = true;
        try
        {
            _autoResumeEnabled = enabled;
            if (_autoResumeToggle is not null) _autoResumeToggle.IsChecked = enabled;
        }
        finally { _loadingAutoResumeSetting = false; }
    }

    private void ApplyAutoResumeSetting(bool enabled)
    {
        if (_loadingAutoResumeSetting) return;
        _autoResumeEnabled = enabled;
        foreach (var tab in TerminalTabs.Items.Cast<TabItem>())
            if (tab.Tag is TerminalTabState { IsIndependent: false } state) state.Pane.SetAutoResumeEnabled(enabled);
        if (_autoResumeToggle is not null && _autoResumeToggle.IsChecked != enabled)
            _autoResumeToggle.IsChecked = enabled;
        SaveSettings();
    }
}
