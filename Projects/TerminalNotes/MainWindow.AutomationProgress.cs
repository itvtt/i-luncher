using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private readonly DispatcherTimer _automationProgressTimer = new(DispatcherPriority.DataBind)
    {
        Interval = TimeSpan.FromMilliseconds(750)
    };
    private Border? _automationProgressPanel;
    private TextBlock? _automationProgressRole;
    private TextBlock? _automationProgressState;
    private AutomationActivityIndicator? _automationProgressIndicator;

    private enum AutomationProgressTone { Gray, Green, Orange }
    private sealed record AutomationProgressSnapshot(
        AutomationRole Role, string State, AutomationProgressTone Tone, bool Completed, bool Animating = false);

    private void InitializeAutomationProgressPanel(Grid host)
    {
        _automationProgressIndicator = new AutomationActivityIndicator { Margin = new Thickness(0, 0, 10, 0) };
        _automationProgressRole = new TextBlock
        {
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(235, 239, 245)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _automationProgressState = new TextBlock
        {
            FontSize = 13, Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(_automationProgressRole);
        labels.Children.Add(_automationProgressState);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(_automationProgressIndicator);
        Grid.SetColumn(labels, 1);
        body.Children.Add(labels);
        _automationProgressPanel = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromRgb(24, 27, 33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 64)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8, 12, 8),
            Child = body
        };
        Grid.SetRow(_automationProgressPanel, 0);
        host.Children.Add(_automationProgressPanel);
        _automationProgressTimer.Tick += (_, _) =>
        {
            if (_closing || _resourcesDisposed)
            {
                _automationProgressTimer.Stop();
                _automationProgressIndicator.SetStatus(false, false, false, Brushes.Gray, "중단");
                return;
            }
            UpdateAutomationProgressPanel();
            UpdateProjectTaskActivity();
        };
        Closed += (_, _) => _automationProgressTimer.Stop();
        _automationProgressTimer.Start();
    }

    private void UpdateAutomationProgressPanel()
    {
        if (_automationProgressPanel is null) return;
        AutomationProgressSnapshot? snapshot = BuildAutomationProgressSnapshot();
        if (snapshot is null)
        {
            _automationProgressPanel.Visibility = Visibility.Collapsed;
            _automationProgressIndicator!.SetStatus(false, false, false, Brushes.Gray, "실행 없음");
            return;
        }
        _automationProgressPanel.Visibility = Visibility.Visible;
        _automationProgressRole!.Text = RoleLabel(snapshot.Role);
        _automationProgressState!.Text = snapshot.State;
        Brush accent = ProgressBrush(snapshot.Tone);
        _automationProgressState.Foreground = accent;
        _automationProgressIndicator!.SetStatus(
            snapshot.Animating,
            snapshot.Tone == AutomationProgressTone.Orange, snapshot.Completed,
            accent, RoleLabel(snapshot.Role) + " · " + snapshot.State);
    }

    private AutomationProgressSnapshot? BuildAutomationProgressSnapshot(string? project = null, bool includeCheckpoints = true)
    {
        project ??= GetAutomationProgressProject();
        if (string.IsNullOrWhiteSpace(project)) return null;
        if (!IsMultiProjectTask(project)) return null;
        AutomationRun[] runs = _knownAutomationRuns.Values
            .Where(run => DirectoriesEqual(run.ProjectDirectory, project))
            .OrderByDescending(run => run.State is AutomationState.Running or AutomationState.Scheduled)
            .ThenByDescending(run => run.UpdatedAt)
            .ThenByDescending(run => run.CreatedAt)
            .ToArray();
        AutomationStepClaim? claim = FindProgressClaim(project, null);
        AutomationRun? run = claim is not null
            ? runs.FirstOrDefault(candidate => candidate.Id == claim.RunId) ?? _knownAutomationRuns.GetValueOrDefault(claim.RunId)
            : runs.FirstOrDefault(candidate => candidate.State is AutomationState.Running or AutomationState.Scheduled)
                ?? runs.FirstOrDefault(candidate => candidate.Id == _preferredAutomationRunId)
                ?? runs.FirstOrDefault();
        if (run is null && claim is null) return null;
        TerminalPane? pane = FindProgressPane(claim, run?.Id);
        var activity = GetProgressActivity(run, claim, pane);
        if (includeCheckpoints && claim is not null && activity.Tone != AutomationProgressTone.Orange &&
            run?.State is not (AutomationState.Paused or AutomationState.Stopped or AutomationState.Failed or AutomationState.Completed))
        {
            // Show work that is actually changing while the main stage waits for
            // its checkpoint. A selected tab or an old checkpoint is not evidence.
            var checkpoints = RuntimeFor(project).MicroStages.Values
                .Where(micro => micro.WorkerDispatch == claim.DispatchToken)
                .Select(micro => (micro.Role, Activity: GetCheckpointProgress(micro)))
                .ToArray();
            foreach (var checkpoint in checkpoints.OrderByDescending(item => item.Activity.Tone == AutomationProgressTone.Orange))
                if (checkpoint.Activity.Tone == AutomationProgressTone.Orange ||
                    (activity.Tone != AutomationProgressTone.Green && checkpoint.Activity.Tone == AutomationProgressTone.Green))
                    return new(checkpoint.Role, "중간 점검 · " + checkpoint.Activity.Label, checkpoint.Activity.Tone, false, checkpoint.Activity.Animating);
        }
        return new(claim?.Role ?? run!.NextRole, activity.Label, activity.Tone,
            claim is null && IsVerifiedTaskRun(run), activity.Animating);
    }

    private static (AutomationProgressTone Tone, string Label, bool Animating) GetCheckpointProgress(MicroManagementStage micro)
    {
        if (!micro.Pane.IsRunning || micro.Pane.NativeAutomationCancellation.IsCancellationRequested)
            return (AutomationProgressTone.Gray, "점검 종료 확인 중", false);
        var activity = ClassifyNativeProgressStatus(ExactProgressStatus(micro.Client, micro.Pane), micro.Pane.HasCompletedWork,
            micro.Client.LastActivityAt, DateTimeOffset.UtcNow);
        if (activity.Tone != AutomationProgressTone.Orange && (micro.Client.IsRecovering || micro.Client.LastReadWarning is not null ||
            micro.Client.TuiConnectionWarning is not null || micro.Client.IsProviderRetrying || micro.Client.IsStalled))
            return (AutomationProgressTone.Gray, micro.Client.SupervisionNotice ?? "응답 재확인 중", false);
        return activity;
    }

    private string? GetAutomationProgressProject()
    {
        if (_taskWorkspaceVisible) return _selectedProjectPath;
        var selected = TerminalTabs.SelectedItem as TabItem;
        if (selected?.Tag is TerminalTabState { IsIndependent: false } state && !string.IsNullOrWhiteSpace(state.Pane.WorkingDirectory))
            return state.Pane.WorkingDirectory;
        return _selectedProjectPath;
    }

    private AutomationStepClaim? FindProgressClaim(string project, Guid? runId)
    {
        var runtime = RuntimeFor(project);
        if (runtime.Stage is { } active && DirectoriesEqual(active.Claim.ProjectDirectory, project) &&
            (runId is null || active.Claim.RunId == runId)) return active.Claim;
        if (runtime.StartingNative is { } starting && DirectoriesEqual(starting.Pane.WorkingDirectory, project) &&
            (runId is null || starting.RunId == runId) && runtime.Dispatching is { } startingClaim)
            return startingClaim;
        if (runtime.Dispatching is { } dispatch && DirectoriesEqual(dispatch.ProjectDirectory, project) &&
            (runId is null || dispatch.RunId == runId)) return dispatch;
        return runtime.Pending.Select(item => item.Claim)
            .Where(item => DirectoriesEqual(item.ProjectDirectory, project) && (runId is null || item.RunId == runId))
            .OrderBy(item => item.ClaimedAt).FirstOrDefault();
    }

    private TerminalPane? FindProgressPane(AutomationStepClaim? claim, Guid? runId)
    {
        var runtime = _automationRuntimes.Values.FirstOrDefault(item => item.Stage?.Claim.RunId == runId || item.StartingNative?.RunId == runId);
        if (runtime is null) return null;
        if (claim is not null && runtime.Stage?.Claim.DispatchToken == claim.DispatchToken) return runtime.Stage.Pane;
        if (claim is not null && runtime.StartingNative?.RunId == claim.RunId) return runtime.StartingNative.Value.Pane;
        if (runId is Guid id && runtime.Stage?.Claim.RunId == id) return runtime.Stage.Pane;
        return null;
    }

    private (AutomationProgressTone Tone, string Label, bool Animating) GetProgressActivity(
        AutomationRun? run, AutomationStepClaim? claim, TerminalPane? pane)
    {
        var runtime = RuntimeFor(claim?.ProjectDirectory ?? run!.ProjectDirectory);
        if (claim is not null && runtime.Stage is null &&
            (runtime.StartingNative?.RunId == claim.RunId || runtime.Dispatching?.DispatchToken == claim.DispatchToken))
        {
            if (pane?.NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput)
                return (AutomationProgressTone.Orange, "승인·질문 대기", false);
            return (AutomationProgressTone.Gray, runtime.StartingNative is not null ? "시작 대기" : "요청 준비", false);
        }
        if (runtime.Stage is { } stage &&
            ((claim is not null && stage.Claim.DispatchToken == claim.DispatchToken) ||
             (claim is null && run is not null && stage.Claim.RunId == run.Id)))
        {
            if (run?.State is AutomationState.Paused or AutomationState.Stopped or AutomationState.Failed)
                return (AutomationProgressTone.Gray, AutomationStateLabel(run.State), false);
            if (stage.Pane.NativeAutomationCancellation.IsCancellationRequested)
                return (AutomationProgressTone.Gray, "중단 확인 중", false);
            if (!stage.Pane.IsRunning) return (AutomationProgressTone.Gray, "프로세스 종료 확인 중", false);
            if (ExactProgressStatus(stage.Client, stage.Pane) == OpenCodeStatusKind.NeedsInput)
                return (AutomationProgressTone.Orange, "승인·질문 대기", false);
            if (stage.Client.IsCoordinatorIntervention) return (AutomationProgressTone.Gray, "지휘 지시로 중단 확인 중", false);
            if (stage.Client.IsRecovering) return (AutomationProgressTone.Gray, "정체 작업 중단 확인 중", false);
            if (stage.Client.LastReadWarning is not null) return (AutomationProgressTone.Gray, "결과 연결 재확인 중", false);
            if (stage.Client.TuiConnectionWarning is not null) return (AutomationProgressTone.Gray, "터미널 화면 연결 재시도", false);
            if (stage.Client.IsStalled) return (AutomationProgressTone.Gray, "진행 정체 확인 중", false);
            if (stage.Client.IsProviderRetrying) return (AutomationProgressTone.Gray, "서비스 재시도 대기", false);
            if (stage.ResultReady) return (AutomationProgressTone.Gray, "역할 전환 준비", false);
            if (stage.Client.HasNativeSnapshot && stage.Client.IsNativeIdle)
                return (AutomationProgressTone.Gray, "결과 확인 중", false);
            return ClassifyNativeProgressStatus(ExactProgressStatus(stage.Client, stage.Pane), stage.Pane.HasCompletedWork,
                stage.Client.LastActivityAt, DateTimeOffset.UtcNow);
        }
        if (run is null) return (AutomationProgressTone.Gray, "대기", false);
        if (IsVerifiedTaskRun(run)) return (AutomationProgressTone.Gray, "완료", false);
        if (claim is not null) return (AutomationProgressTone.Gray, "역할 전환 준비", false);
        return (AutomationProgressTone.Gray, run.State switch
        {
            AutomationState.Scheduled => "예약됨",
            AutomationState.Running => "실행 대기",
            AutomationState.Paused => "일시정지",
            AutomationState.Stopped => "중단",
            AutomationState.Completed => "완료 근거 확인 필요",
            AutomationState.Failed => "실패",
            _ => AutomationStateLabel(run.State)
        }, false);
    }

    private static OpenCodeStatusKind ExactProgressStatus(OpenCodeAutomationClient client, TerminalPane pane) =>
        client.HasNativeSnapshot ? client.HasPendingNativeInput ? OpenCodeStatusKind.NeedsInput :
            client.IsNativeIdle ? OpenCodeStatusKind.Idle : OpenCodeStatusKind.Busy : pane.NativeOpenCodeStatus;

    private static (AutomationProgressTone Tone, string Label, bool Animating) ClassifyNativeProgressStatus(
        OpenCodeStatusKind status, bool hasCompletedTurn, DateTimeOffset? lastActivity, DateTimeOffset now)
    {
        // Native busy keeps the running color through quiet reasoning/tools.
        // Only recent response/tool changes animate; busy alone is not progress evidence.
        bool recent = lastActivity is { } at && now >= at && now - at <= TimeSpan.FromSeconds(8);
        return status switch
        {
            OpenCodeStatusKind.Busy => (AutomationProgressTone.Green, "진행 중", recent),
            OpenCodeStatusKind.NeedsInput => (AutomationProgressTone.Orange, "승인·질문 대기", false),
            OpenCodeStatusKind.Pending => (AutomationProgressTone.Gray, "응답 대기", false),
            OpenCodeStatusKind.Idle when hasCompletedTurn => (AutomationProgressTone.Gray, "결과 확인 중", false),
            OpenCodeStatusKind.Idle => (AutomationProgressTone.Gray, "대기", false),
            _ => (AutomationProgressTone.Gray, "상태 확인 불가", false)
        };
    }

    private static Brush ProgressBrush(AutomationProgressTone tone) => new SolidColorBrush(tone switch
    {
        AutomationProgressTone.Green => Color.FromRgb(64, 211, 146),
        AutomationProgressTone.Orange => Color.FromRgb(255, 179, 71),
        _ => Color.FromRgb(190, 201, 216)
    });

    private static bool DirectoriesEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return string.Equals(first, second, StringComparison.OrdinalIgnoreCase); }
    }
}
