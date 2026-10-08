using System.IO;
using System.Windows;

namespace TerminalNotes;

// Dependency seam for startup tests: tests never invoke a real installer.
public sealed record WorkspaceSetupResult(string Workspace, bool UseAutoMode);

public sealed record OpenCodeStartupHooks(
    Func<CancellationToken, Task<bool>> CheckAsync,
    Func<bool> ConfirmInstallation,
    Func<CancellationToken, Task<AutoInstallResult>> InstallAsync,
    Func<string, bool, Task<WorkspaceSetupResult?>>? ChooseWorkspaceAsync = null);

public partial class MainWindow
{
    private readonly OpenCodeStartupHooks? _startupHooks;
    private readonly CancellationTokenSource _startupCancellation = new();
    private bool _openCodeReady;
    private bool _openCodeStartupBusy;
    private bool _setupCompleted;
    private bool _useAutoMode;

    private static bool IsLegacyDefaultWorkspace(string directory)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), @"Z:\codex", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private bool EnsureWorkspaceDirectory(string directory)
    {
        try { Directory.CreateDirectory(directory); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetStatus("작업 폴더를 만들 수 없습니다 · " + directory + " · " + ex.Message);
            return false;
        }
    }

    private string? _openCodeInstallationFailure;

    private async Task<AutoInstallDetectionResult> DetectOpenCodeAsync(CancellationToken cancellationToken)
    {
        if (_startupHooks is not { } hooks) return await AutoInstall.CheckAsync(cancellationToken);
        bool installed = await hooks.CheckAsync(cancellationToken);
        return new(installed, installed ? "OpenCode 실행 확인 완료" : "OpenCode 실행 확인 실패");
    }

    private async Task<bool> CheckOpenCodeAsync(CancellationToken cancellationToken)
    {
        var detection = await DetectOpenCodeAsync(cancellationToken);
        _openCodeInstallationFailure = detection.IsInstalled ? null : detection.Message;
        RecordAutomationDiagnostic("installation_check", detection.Message,
            severity: detection.IsInstalled ? "info" : "error");
        return detection.IsInstalled;
    }

    private async Task PrepareOpenCodeAndStartAsync(string? title, bool createTerminal = true, bool explicitNew = false, string? workingDirectory = null)
    {
        if (_openCodeStartupBusy || _closing || _closePending) return;
        _openCodeStartupBusy = true;
        try
        {
            CancellationToken token = _startupCancellation.Token;
            SetStatus("OpenCode 설치 여부를 확인하고 있습니다…");
            bool installed = await CheckOpenCodeAsync(token);
            if (_closing || _closePending || token.IsCancellationRequested) return;
            if (!installed)
            {
                bool consent = _startupHooks?.ConfirmInstallation() ??
                    MessageBox.Show(this,
                        "OpenCode가 설치되어 있지 않거나 실행할 수 없습니다.\n\n" +
                        _openCodeInstallationFailure + "\n\n설치할까요?",
                        "OpenCode 설치", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No) == MessageBoxResult.Yes;
                if (_closing || _closePending || token.IsCancellationRequested) return;
                if (!consent)
                {
                    SetStatus("OpenCode 실행 불가 · " + _openCodeInstallationFailure + " · ‘+ 터미널’에서 다시 확인할 수 있습니다.");
                    return;
                }
                SetStatus("OpenCode를 설치하고 있습니다…");
                AutoInstallResult result = _startupHooks is { } hooks
                    ? await hooks.InstallAsync(token)
                    : await AutoInstall.RunAsync(token);
                if (_closing || _closePending || token.IsCancellationRequested) return;
                // The hook's return alone is not proof that OpenCode is now runnable.
                installed = await CheckOpenCodeAsync(token);
                if (_closing || _closePending || token.IsCancellationRequested) return;
                if (!installed)
                {
                    SetStatus(result.Message + " · " + _openCodeInstallationFailure + " · ‘+ 터미널’에서 다시 확인할 수 있습니다.");
                    return;
                }
            }
            if (explicitNew)
            {
                _openCodeReady = true;
                AddIndependentTerminal(title, workingDirectory);
                return;
            }
            if (!_setupCompleted)
            {
                WorkspaceSetupResult? setup;
                if (_startupHooks?.ChooseWorkspaceAsync is { } choose)
                    setup = await choose(_workspace, _useAutoMode);
                else if (_startupHooks is not null)
                    setup = new(_workspace, _useAutoMode);
                else
                {
                    var dialog = new TerminalProfilesDialog(_workspace, _useAutoMode, firstRun: true) { Owner = this };
                    dialog.Resources.MergedDictionaries.Add(Resources);
                    setup = dialog.ShowDialog() == true ? new(dialog.DefaultWorkspace, dialog.UseAutoMode) : null;
                }
                if (_closing || _closePending || token.IsCancellationRequested) return;
                if (setup is null) { SetStatus("작업 시작을 취소했습니다."); return; }
                string directory = Path.GetFullPath(setup.Workspace);
                if (!EnsureWorkspaceDirectory(directory)) return;
                _workspace = directory;
                _useAutoMode = setup.UseAutoMode;
                _setupCompleted = true;
                WorkspaceLabel.Text = _workspace;
                WorkspaceLabel.ToolTip = _workspace;
                SaveSettings();
            }
            if (!EnsureWorkspaceDirectory(_workspace)) return;
            if (_startInWorkspaceProject && _selectedProjectPath is null)
            {
                // Scheduler initialization can refresh/load the editor synchronously.
                // Create/migrate the canonical plan before exposing this project, so
                // its first snapshot cannot observe a missing file which our later
                // terminal creation then turns into a false external-edit conflict.
                EnsureProjectPlanDocument(_workspace);
                _selectedProjectPath = CanonicalProjectPath(_workspace);
                SaveSettings();
            }
            _openCodeReady = true;
            InitializeAutomation();
            _ = RefreshProjectTasksAsync();
            if (createTerminal) AddTerminalCore(title, explicitNew, workingDirectory);
            else
            {
                UpdateWorkbenchTarget();
                SetStatus("기존 프로젝트를 열거나 새 프로젝트를 시작하세요.");
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closing) SetStatus("OpenCode 시작 준비를 취소했습니다. ‘+ 터미널’에서 다시 확인할 수 있습니다.");
        }
        catch (Exception ex)
        {
            RecordAutomationDiagnostic("installation_check_failed", "OpenCode 시작 준비 실패 · " + ex.Message,
                severity: "error", exception: ex);
            if (!_closing) SetStatus("OpenCode 시작 준비 실패 · " + ex.Message);
        }
        finally { _openCodeStartupBusy = false; }
    }
}
