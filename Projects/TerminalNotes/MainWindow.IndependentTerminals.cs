using System.IO;
using System.Windows.Controls;

namespace TerminalNotes;

public partial class MainWindow
{
    private void AddIndependentTerminal(string? title = null, string? workingDirectory = null)
    {
        if (_closing || _closePending) return;
        string directory = workingDirectory ?? ActiveTerminal?.WorkingDirectory ?? _workspace;
        if (_autoStartCodex && !_openCodeReady)
        {
            _ = PrepareOpenCodeAndStartAsync(title, explicitNew: true, workingDirectory: directory);
            return;
        }
        if (ManualTerminalCount >= 8) { SetStatus("일반 터미널은 최대 8개까지 열 수 있습니다."); return; }
        if (!Directory.Exists(directory)) { SetStatus("터미널 작업 폴더를 찾을 수 없습니다 · " + directory); return; }
        int slot = NextTerminalSlot();
        var launch = ResolveTerminalProfile(slot) with { WorkingDirectory = directory, Title = _autoStartCodex ? "OpenCode" : "PowerShell" };
        TabItem item;
        try { item = CreateTerminalTabCore(slot, launch, title, createPlanDocument: false, independent: true); }
        catch (Exception exception) { SetStatus("터미널을 시작하지 못했습니다 · " + exception.Message); return; }
        int position = TerminalTabs.Items.Cast<TabItem>().Count(tab => ((TerminalTabState)tab.Tag).Sequence < slot);
        TerminalTabs.Items.Insert(position, item);
        TerminalTabs.SelectedItem = item;
        if (_arrangingTerminals)
        {
            if (!_fillingTerminalLayout) _terminalLayoutFitPending = true;
            ApplyTerminalLayout();
        }
        else FitTerminalLayoutToCount();
        UpdateTerminalCount();
    }

    private TerminalTabState? SelectedPlanTerminal()
    {
        var selected = (TerminalTabs.SelectedItem as TabItem)?.Tag as TerminalTabState;
        if (selected?.IsIndependent != true) return selected;
        if (_selectedProjectPath is null) return null;
        return _automationRuntimes.TryGetValue(CanonicalProjectPath(_selectedProjectPath), out var runtime) &&
            runtime.SingleTerminal is { Tag: TerminalTabState state } tab && TerminalTabs.Items.Contains(tab)
            ? state : null;
    }

    private async Task RunIndependentTerminalLifecycleAsync(TabItem tab, TerminalTabState state, bool restart)
    {
        if (!_terminalLifecycleOperations.Add(tab)) return;
        UpdateTerminalLifecycleButtons();
        try
        {
            if (state.Pane.WasContinuedExternally) return;
            await state.Pane.StopForTerminalLifecycleAsync();
            if (_closing || _closePending || !TerminalTabs.Items.Contains(tab)) return;
            if (!restart) { SetStatus("선택한 터미널을 중단했습니다."); return; }
            var replacement = CreateTerminalLifecycleReplacement(state);
            int index = TerminalTabs.Items.IndexOf(tab);
            bool selected = ReferenceEquals(TerminalTabs.SelectedItem, tab);
            tab.Content = null;
            TerminalTabs.Items.RemoveAt(index);
            TerminalTabs.Items.Insert(index, replacement);
            if (selected) TerminalTabs.SelectedItem = replacement;
            DisposeSafely(state.Pane);
            FitTerminalLayoutToCount();
            UpdateTerminalCount();
            SetStatus("선택한 터미널을 시작했습니다.");
        }
        catch (Exception exception)
        {
            SetStatus((restart ? "터미널 시작" : "터미널 중단") + " 실패 · " + exception.Message);
        }
        finally
        {
            _terminalLifecycleOperations.Remove(tab);
            UpdateTerminalLifecycleButtons();
        }
    }
}
