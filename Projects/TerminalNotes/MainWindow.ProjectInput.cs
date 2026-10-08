using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private sealed class ProjectInputDraft
    {
        public string Text = "";
        public string? SavedInput;
        public string? SavedPlan;
    }

    private readonly Dictionary<string, ProjectInputDraft> _projectInputDrafts = new(StringComparer.OrdinalIgnoreCase);
    private TextBox? _projectInputEditor;
    private TextBlock? _projectInputHint;
    // Retained as a compatibility hook for UI automation; the composer has no separate action button.
    private Button? _projectInputSend;
    private string? _projectInputProject;
    private bool _loadingProjectInput, _projectInputSending;

    private Grid CreateProjectInputComposer(FrameworkElement actionButtons)
    {
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _projectInputHint = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "플랜에 추가", Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
            ToolTip = "요청을 입력한 뒤 아래 저장 또는 실행을 사용하세요. 저장은 요청을 plan.md에 추가하고, 실행은 저장 후 현재 터미널로 보냅니다." };
        grid.Children.Add(_projectInputHint);
        _projectInputEditor = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 84, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 14, Padding = new Thickness(8), MaxLength = 28000,
            Background = (System.Windows.Media.Brush)FindResource("Canvas"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            ToolTip = "플랜에 추가할 요청을 적어주세요. Enter는 줄바꿈, Ctrl+Enter는 저장 후 실행입니다." };
        _projectInputEditor.TextChanged += (_, _) =>
        {
            if (_loadingProjectInput || _projectInputProject is null) return;
            var draft = _projectInputDrafts[_projectInputProject];
            draft.Text = _projectInputEditor.Text;
            if (draft.SavedInput != draft.Text) { draft.SavedInput = null; draft.SavedPlan = null; }
            RefreshProjectInputSendState();
        };
        _projectInputEditor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            { e.Handled = true; TrackJournalWork(SaveTabDocumentAsync(execute: false)); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            { e.Handled = true; TrackJournalWork(SaveTabDocumentAsync(execute: true)); }
        };
        _projectInputSend = CreateTabDocumentButton("저장 후 요청 보내기", "호환용 숨김 컨트롤");
        _projectInputSend.Visibility = Visibility.Collapsed;
        Grid.SetRow(_projectInputEditor, 1); grid.Children.Add(_projectInputEditor);
        if (actionButtons.Parent is Panel parent) parent.Children.Remove(actionButtons);
        Grid.SetRow(actionButtons, 2);
        Grid.SetColumn(actionButtons, 0);
        actionButtons.Margin = new Thickness(0, 8, 0, 0);
        grid.Children.Add(actionButtons);
        return grid;
    }

    private void UpdateProjectInputComposer()
    {
        if (_projectInputEditor is null) return;
        string? project = _composerState?.Pane.WorkingDirectory ?? _selectedProjectPath;
        _projectInputProject = project is null ? null : CanonicalProjectPath(project);
        _loadingProjectInput = true;
        try
        {
            if (_projectInputProject is { } key)
            {
                if (!_projectInputDrafts.TryGetValue(key, out var draft)) _projectInputDrafts[key] = draft = new();
                if (_projectInputEditor.Text != draft.Text) _projectInputEditor.Text = draft.Text;
            }
            else _projectInputEditor.Clear();
            _projectInputHint!.Text = project is null ? "프로젝트를 먼저 선택하세요" : "플랜에 추가";
            _projectInputEditor.IsEnabled = project is not null && !_projectInputSending && !_closing && !_closePending;
            RefreshProjectInputSendState();
        }
        finally { _loadingProjectInput = false; }
    }

    private void RefreshProjectInputSendState()
    {
        RefreshActiveTabDocumentControls();
    }

    private Task SubmitProjectInputAsync() => SaveAndDispatchProjectInputAsync(dispatch: true);

    private Task SaveProjectInputAsync() => SaveAndDispatchProjectInputAsync(dispatch: false);

    private async Task SaveAndDispatchProjectInputAsync(bool dispatch)
    {
        if (_projectInputSending || _tabDocumentSaving || _closing || _closePending || _projectInputProject is not { } project) return;
        var draft = _projectInputDrafts[project];
        string input = draft.Text;
        if (string.IsNullOrWhiteSpace(input)) return;
        if (HasExternalTerminal(project)) { SetStatus(ExternalTerminalBlockReason); return; }
        var runtime = RuntimeFor(project);
        long intent = runtime.IntentVersion;
        bool saved = false;
        CancellationTokenSource? startupForStop = null;
        Task startupNoticeTask = Task.CompletedTask;
        void ReportInputStartPhase(string phase)
        {
            runtime.SingleStartPhase = phase;
            SetStatus(phase);
            UpdateProjectTaskActivity();
        }
        _projectInputSending = _tabDocumentSaving = true;
        if (_tabDocumentEditor is not null) _tabDocumentEditor.IsEnabled = false;
        UpdateProjectInputComposer();
        try
        {
            string path = DefaultProjectPlanPath(project);
            if (draft.SavedInput != input)
            {
                var session = await LoadAutomationPlanAsync(null, project, 1, path);
                string appended = PlanInputAppend.Append(session.InitialDraft, input);
                await SaveAutomationPlanAsync(session, appended, inputSubmission: true);
                draft.SavedInput = input;
                draft.SavedPlan = appended;
            }
            else
            {
                var session = await LoadAutomationPlanAsync(null, project, 1, path);
                if (session.InitialDraft != draft.SavedPlan || await File.ReadAllTextAsync(path) != draft.SavedPlan)
                    throw new InvalidOperationException("요청 저장 후 plan.md가 변경되었습니다. 내용을 확인한 뒤 다시 전송하세요.");
            }
            saved = true;
            if (!dispatch)
            {
                SetStatus("요청을 plan.md에 저장했습니다. 실행을 누르면 현재 터미널로 보냅니다.");
                return;
            }
            var startup = new CancellationTokenSource(TerminalExecutionTiming.StartupReadyTimeout);
            startupForStop = startup;
            if (runtime.SingleStartupCancellation is not null) throw new InvalidOperationException("이미 시작 확인 중인 작업이 있습니다.");
            runtime.SingleStartupCancellation = startup;
            runtime.SingleStopped = false;
            runtime.SingleError = null;
            BeginTaskNotificationAttempt(project);
            ReportInputStartPhase("◌ OpenCode 실행 확인 중");
            startupNoticeTask = ReportSlowStartupProgressAsync(runtime, startup.Token);
            if (!await EnsureProjectOpenCodeReadyAsync(runtime, intent, startup.Token))
            {
                if (startup.IsCancellationRequested && runtime.IntentVersion == intent) startup.Token.ThrowIfCancellationRequested();
                return;
            }
            var tab = EnsureSingleProjectTerminal(project);
            var pane = ((TerminalTabState)tab.Tag).Pane;
            bool StillOwned() => !_closing && !_closePending && runtime.IntentVersion == intent && !runtime.SingleStopped &&
                runtime.SingleTerminal == tab && TerminalTabs.Items.Contains(tab) &&
                DirectoriesEqual(CurrentProjectPath, project) && !HasExternalTerminal(project);
            ReportInputStartPhase("◌ 터미널 연결 확인 중");
            await pane.EnsureReadyAsync(startup.Token);
            ReportInputStartPhase("◌ OpenCode 입력 준비 확인 중");
            while (!pane.CanExecuteSavedPlan && StillOwned() && !startup.IsCancellationRequested)
            {
                if (!IsNativeOpenCodeTaskPane(pane)) break;
                var state = pane.NativeAutomationDiagnostic;
                if (state.Failed || state.Disposed || !pane.IsRunning)
                    throw new InvalidOperationException(pane.PlanExecutionBlockReason);
                if (pane.NativeOpenCodeStatus is OpenCodeStatusKind.Busy or OpenCodeStatusKind.NeedsInput ||
                    state.HasPendingInput || state.ManualOverride) break;
                await Task.Delay(100, startup.Token);
            }
            if (!StillOwned()) return;
            if (startup.IsCancellationRequested) startup.Token.ThrowIfCancellationRequested();
            if (!pane.CanExecuteSavedPlan)
                throw new InvalidOperationException(pane.PlanExecutionBlockReason);
            startup.CancelAfter(Timeout.InfiniteTimeSpan);
            ReportInputStartPhase("◌ 입력 전달·수신 확인 중");
            bool DispatchOwned() => StillOwned() && !startup.IsCancellationRequested;
            string prompt = $"Read the current project plan at \"{path}\" and apply the following request. Check each requirement against the current code, changes, and validation evidence before editing. Report already satisfied items with evidence instead of repeating the implementation. Do not rewrite code only because a plan or todo item is marked incomplete; make and validate one minimal change only when you find a confirmed gap or defect. If all requirements are met, reuse valid existing verification evidence and finish without changes.\n\n" + input;
            ConfigureSingleContinuousImprovement(project);
            if (!StillOwned() || !await pane.SubmitProjectInputAsync(prompt, false, DispatchOwned))
                throw new InvalidOperationException(pane.PlanExecutionBlockReason is { Length: > 0 } reason ? reason : "터미널이 플랜 입력을 받지 않았습니다.");
            pane.SetAutoResumeEnabled(_autoResumeEnabled);
            draft.Text = ""; draft.SavedInput = null; draft.SavedPlan = null;
            runtime.SingleSubmitted = true; runtime.SinglePlanInputNeeded = false; runtime.SingleStopped = false; runtime.SingleError = null;
            SetStatus("plan.md에 추가하고 터미널로 전송했습니다.");
        }
        catch (Exception ex)
        {
            if (runtime.IntentVersion != intent || runtime.SingleStopped) return;
            string reason = ex is OperationCanceledException && startupForStop?.IsCancellationRequested == true
                ? "터미널 입력 준비 시간이 초과되었습니다. 연결 상태를 확인한 뒤 다시 전송하세요."
                : ex.Message;
            if (startupForStop is not null && ReferenceEquals(runtime.SingleStartupCancellation, startupForStop))
                runtime.SingleError = reason;
            SetStatus((saved ? "plan.md에 저장했습니다. 터미널 전송을 확인하세요 · " : "전송하지 못했습니다 · ") + reason);
        }
        finally
        {
            startupForStop?.Cancel();
            try { await startupNoticeTask; }
            catch (OperationCanceledException) { }
            bool ownsStartup = startupForStop is not null && ReferenceEquals(runtime.SingleStartupCancellation, startupForStop);
            if (ownsStartup) { runtime.SingleStartupCancellation = null; runtime.SingleStartPhase = null; }
            startupForStop?.Dispose();
            _projectInputSending = _tabDocumentSaving = false;
            UpdateWorkbenchTarget();
            UpdateProjectTaskActivity();
        }
    }
}
