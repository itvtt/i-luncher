using System.IO;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private int NextTerminalSlot()
    {
        var occupied = TerminalTabs.Items.Cast<TabItem>().Select(tab => ((TerminalTabState)tab.Tag).Sequence).ToHashSet();
        return Enumerable.Range(1, occupied.Count + 1).First(slot => !occupied.Contains(slot));
    }

    private static List<TerminalLaunchProfile> NormalizeProfiles(IEnumerable<TerminalLaunchProfile> profiles) => profiles
        .Where(profile => profile is not null && profile.Slot is >= 1 and <= 8)
        .GroupBy(profile => profile.Slot)
        .Select(group => group.Last())
        .Select(profile => profile with
        {
            Tool = TerminalTool.OpenCode,
            Command = null,
            Mode = TerminalPermissionMode.Standard,
            Title = NormalizeProfileTitle(profile.Title),
            WorkingDirectory = null
        })
        .OrderBy(profile => profile.Slot).ToList();

    private static string? NormalizeProfileTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string trimmed = title.Trim();
        int length = Math.Min(60, trimmed.Length);
        if (length > 0 && char.IsHighSurrogate(trimmed[length - 1])) length--;
        return trimmed[..length];
    }

    private ResolvedTerminalLaunch ResolveTerminalProfile(int slot)
    {
        var profile = _terminalProfiles.FirstOrDefault(item => item.Slot == slot) ?? new TerminalLaunchProfile(slot);
        TerminalTool resolvedTool = _autoStartCodex ? TerminalTool.OpenCode : TerminalTool.PowerShell;
        TerminalPermissionMode mode = _useAutoMode ? TerminalPermissionMode.Auto : TerminalPermissionMode.Standard;
        string? command = _autoStartCodex ? TerminalLaunchModes.Command(TerminalTool.OpenCode, mode) : null;
        var launch = new ResolvedTerminalLaunch(false, command, _autoStartCodex ? "OpenCode" : "PowerShell");
        launch = launch with { WorkingDirectory = CurrentProjectPath, Tool = resolvedTool };
        return string.IsNullOrWhiteSpace(profile.Title) ? launch : launch with { Title = profile.Title };
    }

    private void CreateProfileTerminal(TerminalPane? source = null) =>
        AddTerminalCore(explicitNew: true, workingDirectory: source?.WorkingDirectory);

    private void TerminalProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _closePending) return;
        if (_openCodeStartupBusy) { SetStatus("OpenCode 설치 확인 또는 자동 설치가 진행 중입니다."); return; }
        var dialog = new TerminalProfilesDialog(_workspace, _useAutoMode) { Owner = this };
        dialog.Resources.MergedDictionaries.Add(Resources);
        if (dialog.ShowDialog() != true || _closing) return;
        if (!string.Equals(CanonicalProjectPath(_workspace), CanonicalProjectPath(dialog.DefaultWorkspace), StringComparison.OrdinalIgnoreCase))
            _selectedProjectPath = null;
        _workspace = dialog.DefaultWorkspace;
        _useAutoMode = dialog.UseAutoMode;
        _setupCompleted = true;
        WorkspaceLabel.Text = _workspace;
        WorkspaceLabel.ToolTip = _workspace;
        _ = RefreshProjectTasksAsync();
        _ = ScanDocumentsAsync();
        SetStatus("작업 설정을 저장했습니다. 새 터미널부터 적용합니다.");
        SaveSettings();
    }

    private sealed record ResolvedTerminalLaunch(bool LaunchCodex, string? Command, string Title, string? WorkingDirectory = null, TerminalTool Tool = TerminalTool.PowerShell);
}
