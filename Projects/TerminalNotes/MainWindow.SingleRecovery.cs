using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private static bool RecoveryPlanMatches(TabDocumentSnapshot saved)
    {
        try { return File.Exists(saved.Path) && string.Equals(File.ReadAllText(saved.Path), saved.Text, StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private async Task<bool> RestartStalledSingleProjectAsync(TabItem tab, OpenCodeStalledRestart request)
    {
        if (_closing || _closePending || _tabDocumentSaving || tab.Tag is not TerminalTabState previous ||
            !TerminalTabs.Items.Contains(tab)) return false;
        string project = CanonicalProjectPath(previous.Pane.WorkingDirectory);
        var runtime = RuntimeFor(project);
        if (runtime.SingleTerminal != tab || runtime.SingleStopped || HasExternalTerminal(project) ||
            _projectTaskOptionWrites.Contains(project) ||
            !string.Equals(Path.GetFullPath(request.PlanPath), DefaultProjectPlanPath(project), StringComparison.OrdinalIgnoreCase) ||
            !request.CanRestart() || !_projectTaskControlWrites.Add(project)) return false;
        long intent = runtime.IntentVersion;
        int slot = _terminalSlotLayout.IndexOf(project);

        _startingProjectTasks.Add(project);
        try
        {
            string plan = await File.ReadAllTextAsync(request.PlanPath);
            if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan))) != request.PlanHash ||
                _closing || _closePending || runtime.IntentVersion != intent || !request.CanRestart()) return false;

            SetStatus("정체된 OpenCode를 종료하고 플랜을 이어갑니다 · " + ProjectDisplayName(project));
            UpdateProjectTaskActivity();
            if (!await previous.Pane.StopStalledProcessAsync(request)) return false;
            if (_closing || _closePending || runtime.IntentVersion != intent || runtime.SingleStopped || HasExternalTerminal(project)) return false;
            EnsureSingleProjectTerminal(project);
            if (slot >= 0) AssignProjectToSlot(project, slot);
            await ExecuteSingleProjectPlanCoreAsync(project, new(request.PlanPath, plan, true), request.Prompt);
            if (runtime.SingleTerminal?.Tag is TerminalTabState resumed) resumed.Pane.PreserveRestartInstruction(request);
            return runtime.IntentVersion == intent && !runtime.SingleStopped && runtime.SingleError is null && runtime.SingleSubmitted;
        }
        catch (Exception ex)
        {
            if (!runtime.SingleStopped) runtime.SingleError = "정체 복구 실패 · " + ex.Message;
            SetStatus("정체 복구 확인 필요 · " + ex.Message);
            return false;
        }
        finally
        {
            _startingProjectTasks.Remove(project);
            _projectTaskControlWrites.Remove(project);
            UpdateProjectTaskActivity();
            UpdateWorkbenchTarget();
        }
    }
}