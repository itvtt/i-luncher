using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private readonly HashSet<string> _externalTerminalWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TabItem, Button> _cmdTerminalButtons = new();
    private const string ExternalTerminalBlockReason = "CMD에서 이어가는 중입니다. 해당 프로젝트의 OpenCode CMD를 종료한 뒤 앱에서 실행하세요.";
    private bool HasExternalTerminal(string project) => _externalTerminalWrites.Contains(project) || ExternalTerminalLauncher.IsProjectActive(project);

    private Button CreateCmdTerminalButton(TabItem tab)
    {
        var button = new Button { Content = "CMD", Width = 42, Height = 26, FontSize = 13,
            Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
        AutomationProperties.SetName(button, "CMD에서 이어가기");
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += async (_, _) => await ContinueTerminalInCmdAsync(tab);
        _cmdTerminalButtons[tab] = button;
        return button;
    }

    private void UpdateCmdTerminalButtons()
    {
        foreach (var (tab, button) in _cmdTerminalButtons)
        {
            var pane = ((TerminalTabState)tab.Tag).Pane;
            button.IsEnabled = CanContinueTerminalInCmd(pane);
            button.ToolTip = CmdContinuationToolTip(pane);
        }
    }

    private bool CanContinueTerminalInCmd(TerminalPane pane) =>
        !_closing && !_closePending && !_tabDocumentSaving &&
        !_projectTaskControlWrites.Contains(CanonicalProjectPath(pane.WorkingDirectory)) &&
        !ExternalTerminalLauncher.IsProjectActive(pane.WorkingDirectory) && pane.HasContinuationSession;

    private static string CmdContinuationToolTip(TerminalPane pane) =>
        ExternalTerminalLauncher.IsProjectActive(pane.WorkingDirectory)
            ? "이미 CMD에서 이어가고 있습니다. 열린 CMD 창을 사용하세요."
            : pane.HasContinuationSession
            ? "CMD에서 이어가기 · 프로젝트의 앱 자동 진행을 중단하고 기존 대화를 독립된 CMD에서 엽니다. 앱을 종료해도 사용할 수 있습니다."
            : "이어 열 OpenCode 대화가 없습니다. 이 터미널에서 작업을 시작한 뒤 사용할 수 있습니다.";

    private void UpdateCmdTerminalMenuItem(TabItem tab, MenuItem item)
    {
        if (!TerminalTabs.Items.Contains(tab) || tab.Tag is not TerminalTabState state)
        {
            item.IsEnabled = false;
            return;
        }
        item.IsEnabled = CanContinueTerminalInCmd(state.Pane);
        item.ToolTip = CmdContinuationToolTip(state.Pane);
    }

    private async Task ContinueTerminalInCmdAsync(TabItem tab)
    {
        if (_closing || _closePending || _tabDocumentSaving || tab.Tag is not TerminalTabState { IsIndependent: false } selected || !TerminalTabs.Items.Contains(tab)) return;
        string project = CanonicalProjectPath(selected.Pane.WorkingDirectory);
        if (ExternalTerminalLauncher.IsProjectActive(project)) { SetStatus(ExternalTerminalBlockReason); return; }
        if (!_projectTaskControlWrites.Add(project)) return;
        _externalTerminalWrites.Add(project);
        _tabDocumentSaving = true;
        var panes = TerminalTabs.Items.Cast<TabItem>().Where(t => !((TerminalTabState)t.Tag).IsIndependent).Select(t => ((TerminalTabState)t.Tag).Pane)
            .Where(p => DirectoriesEqual(p.WorkingDirectory, project)).ToArray();
        var inputBlocks = panes.ToDictionary(p => p, p => p.IsUserInputBlocked);
        void CheckHandoffOwner()
        {
            if (_closing || _closePending || !TerminalTabs.Items.Contains(tab) || !ReferenceEquals(tab.Tag, selected))
                throw new OperationCanceledException("터미널 이관이 취소되었습니다.");
        }
        try
        {
            UpdateProjectTaskActivity();
            var runtime = RuntimeFor(project);
            for (int attempt = 0; (runtime.Polling || runtime.MicroPolling) && attempt < 100; attempt++) await Task.Delay(50);
            if (runtime.Polling || runtime.MicroPolling || runtime.StartingRun || runtime.StartingSingle || runtime.StartingNative is not null || runtime.Dispatching is not null)
                throw new InvalidOperationException("요청 준비·정리 중입니다. 잠시 후 CMD 버튼을 다시 누르세요.");
            if (panes.Any(p => p.NativeAutomationDiagnostic.HasPendingInput))
                throw new InvalidOperationException("터미널에 입력 중인 내용을 전송하거나 지운 뒤 CMD에서 이어가세요.");
            foreach (var pane in panes) pane.IsUserInputBlocked = true;
            var installation = await new OpenCodeInstallationService().CheckAsync();
            if (!installation.IsInstalled || installation.Path is null) throw new InvalidOperationException("OpenCode 실행 파일을 확인하지 못했습니다.");
            string session = await selected.Pane.CaptureContinuationSessionAsync();
            if (ExternalTerminalLauncher.IsSessionActive(project, session)) throw new InvalidOperationException("이 대화는 이미 CMD에서 열려 있습니다. 열린 CMD 창을 사용하세요.");
            // Capture other role IDs while their servers still exist so each CMD button remains useful after handoff.
            foreach (var pane in panes.Where(p => p != selected.Pane && p.HasContinuationSession && !p.WasContinuedExternally))
                await pane.CaptureContinuationSessionAsync();
            CheckHandoffOwner();
            string plan = selected.PlanDocumentPath ?? DefaultProjectPlanPath(project);
            if (_tabDocumentStates.TryGetValue(plan, out var edit))
            {
                if (edit.IsLoading && edit.LoadTask is { } load) await load;
                if (edit.Snapshot is not null && edit.Draft != edit.Snapshot.Text)
                {
                    edit.Snapshot = await new TerminalTabDocumentStore(project).SaveAsync(edit.Slot, edit.Mode, edit.Draft, edit.Snapshot);
                    edit.Draft = edit.Snapshot.Text;
                }
                else if (!edit.IsLoaded && edit.Draft.Length > 0) throw new InvalidOperationException("플랜을 읽고 저장한 뒤 CMD에서 이어가세요.");
            }
            CheckHandoffOwner();
            var launch = ExternalTerminalLauncher.Prepare(installation.Path, project, session, plan,
                ProjectDisplayName(project) + " · " + selected.Title, selected.Pane.StartupCommand == "opencode --auto");
            // Persist an explicit stop before retiring embedded processes. Restart recovery must not reclaim this work.
            await StopProjectTaskCoreAsync(project);
            CheckHandoffOwner();
            foreach (var pane in panes.Where(p => !p.WasContinuedExternally)) await pane.StopForExternalContinuationAsync();
            CheckHandoffOwner();
            await ExternalTerminalLauncher.LaunchAsync(launch);
            selected.Pane.CompleteExternalContinuation(session);
            SetStatus("CMD에서 이어 열었습니다 · 다음 요청은 CMD에 입력하세요. 앱을 종료해도 유지됩니다.");
        }
        catch (Exception ex)
        {
            selected.Pane.MarkExternalContinuationFailed();
            SetStatus("CMD 이어가기 · " + ex.Message);
        }
        finally
        {
            foreach (var (pane, blocked) in inputBlocks)
                if (!pane.WasContinuedExternally) pane.IsUserInputBlocked = blocked;
            _externalTerminalWrites.Remove(project);
            _projectTaskControlWrites.Remove(project);
            _tabDocumentSaving = false;
            UpdateProjectTaskActivity();
            UpdateWorkbenchTarget();
        }
    }
}
