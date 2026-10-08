using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private readonly Dictionary<string, TabDocumentEditState> _tabDocumentStates = new(StringComparer.OrdinalIgnoreCase);
    private Grid? _tabDocumentSurface;
    private TextBox? _tabDocumentEditor;
    private TextBlock? _tabDocumentTitle, _tabDocumentStatus;
    private Button? _tabDocumentSave, _tabDocumentExecute;
    private string? _activeTabDocumentPath;
    private TabDocumentEditState? _loadingTabDocumentState;
    private int _tabDocumentGeneration;
    private CancellationTokenSource? _tabDocumentLoadCancellation;
    private bool _updatingTabDocumentEditor;

    private void InitializeTabDocuments(Grid host)
    {
        var surface = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition());
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _tabDocumentTitle = title;
        header.Children.Add(title);
        surface.Children.Add(header);

        var editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Malgun Gothic"),
            FontSize = 15,
            Padding = new Thickness(9, 8, 9, 8),
            Background = (Brush)FindResource("Canvas"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            Foreground = (Brush)FindResource("TextPrimary"),
            IsEnabled = false,
            ToolTip = "Markdown 제목(##)으로 항목 구분 · Ctrl+S 저장 · Ctrl+Enter 실행"
        };
        editor.TextChanged += (_, _) =>
        {
            if (_updatingTabDocumentEditor || _activeTabDocumentPath is null ||
                !_tabDocumentStates.TryGetValue(_activeTabDocumentPath, out var state)) return;
            state.Draft = editor.Text;
            state.Error = null;
            RefreshTabDocumentControls(state);
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                TrackJournalWork(SaveTabDocumentAsync(execute: false));
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                TrackJournalWork(SaveTabDocumentAsync(execute: true));
            }
        };
        _tabDocumentEditor = editor;
        Grid.SetRow(editor, 2);
        surface.Children.Add(editor);

        var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var status = new TextBlock
        {
            FontSize = 13,
            Foreground = (Brush)FindResource("Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "문서 상태"
        };
        _tabDocumentStatus = status;
        var statusRow = new Grid();
        statusRow.ColumnDefinitions.Add(new ColumnDefinition());
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusRow.Children.Add(status);
        var autoResume = new CheckBox
        {
            Content = "자동 진행", FontSize = 13, IsChecked = _autoResumeEnabled,
            Foreground = Brushes.Gainsboro,
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "OpenCode의 미완료 작업을 감시해 자동으로 이어갑니다. 승인·질문 대기는 유지하고, 중단 버튼을 누르면 해당 작업의 자동 진행도 멈춥니다."
        };
        autoResume.Checked += (_, _) => ApplyAutoResumeSetting(true);
        autoResume.Unchecked += (_, _) => ApplyAutoResumeSetting(false);
        _autoResumeToggle = autoResume;
        footer.Children.Add(statusRow);
        var buttons = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var save = CreateTabDocumentButton("저장", "플랜 저장 · Ctrl+S");
        save.FontWeight = FontWeights.SemiBold;
        save.Click += (_, _) => TrackJournalWork(SaveTabDocumentAsync(execute: false));
        _tabDocumentSave = save;
        var execute = CreateTabDocumentButton("실행", "현재 MD를 저장한 뒤 선택한 방식으로 실행 · Ctrl+Enter");
        ToolTipService.SetShowOnDisabled(execute, true);
        execute.FontWeight = FontWeights.SemiBold;
        execute.SetResourceReference(BackgroundProperty, "PrimaryBrush");
        execute.SetResourceReference(BorderBrushProperty, "PrimaryBrush");
        execute.Click += (_, _) => TrackJournalWork(SaveTabDocumentAsync(execute: true));
        _tabDocumentExecute = execute;
        var actionButtons = new[] { save, execute };
        var backgrounds = new[] { "SurfaceElevated", "PrimaryBrush" };
        var borders = new[] { "Line", "PrimaryBrush" };
        for (int index = 0; index < actionButtons.Length; index++)
        {
            var button = actionButtons[index];
            button.MinWidth = 56;
            button.Height = 30;
            button.Padding = new Thickness(7, 2, 7, 2);
            button.Margin = new Thickness(index == 0 ? 0 : 5, 0, 0, 0);
            button.SetResourceReference(BackgroundProperty, backgrounds[index]);
            button.SetResourceReference(BorderBrushProperty, borders[index]);
            button.SetResourceReference(FrameworkElement.StyleProperty, "DocumentActionButton");
        }
        execute.SetResourceReference(BackgroundProperty, "PrimaryBrush");
        execute.SetResourceReference(BorderBrushProperty, "PrimaryBrush");
        var buttonGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttonGroup.Children.Add(save);
        buttonGroup.Children.Add(execute);
        Grid.SetColumn(autoResume, 0);
        autoResume.Margin = new Thickness(0);
        autoResume.HorizontalAlignment = HorizontalAlignment.Left;
        buttons.Children.Add(autoResume);
        Grid.SetColumn(buttonGroup, 1);
        buttons.Children.Add(buttonGroup);
        var executionControls = CreatePlanExecutionControls();
        Grid.SetRow(executionControls, 1);
        footer.Children.Add(executionControls);
        Grid.SetRow(buttons, 1);
        surface.Children.Add(buttons);
        Grid.SetRow(footer, 3);
        surface.Children.Add(footer);
        var composer = CreateProjectInputComposer(buttonGroup);
        Grid.SetRow(composer, 4);
        surface.Children.Add(composer);

        _tabDocumentSurface = surface;
        surface.Visibility = Visibility.Collapsed;
        host.Children.Add(surface);
    }

    private static Button CreateTabDocumentButton(string text, string toolTip) => new()
    {
        Content = text,
        Height = 34,
        MinWidth = 62,
        Padding = new Thickness(7, 3, 7, 3),
        Margin = new Thickness(5, 0, 0, 0),
        FontSize = 13,
        ToolTip = toolTip
    };

    private void SetTabDocumentModeVisible(bool visible)
    {
        if (_tabDocumentSurface is not null)
            _tabDocumentSurface.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTabDocumentEditor(TerminalTabState? terminal)
    {
        if (_tabDocumentEditor is null) return;
        if (terminal is null && _selectedProjectPath is null)
        {
            SetTabDocumentModeVisible(false);
            InvalidateActiveTabDocument();
            return;
        }

        SetTabDocumentModeVisible(true);
        string project = CanonicalProjectPath(terminal?.Pane.WorkingDirectory ?? CurrentProjectPath);
        const TerminalInputMode mode = TerminalInputMode.Plan;
        const int slot = 1;
        string planPath;
        try { planPath = Path.GetFullPath(DefaultProjectPlanPath(project)); }
        catch (Exception ex)
        {
            InvalidateActiveTabDocument();
            _tabDocumentEditor.IsEnabled = false;
            _tabDocumentTitle!.Text = "Plan 문서 경로 오류";
            _tabDocumentStatus!.Text = ex.Message;
            _tabDocumentSave!.IsEnabled = _tabDocumentExecute!.IsEnabled = false;
            return;
        }

        var store = new TerminalTabDocumentStore(project, planPath);
        string path;
        try { path = Path.GetFullPath(store.GetDocumentPath(slot, mode)); }
        catch (Exception ex)
        {
            InvalidateActiveTabDocument();
            _tabDocumentEditor.IsEnabled = false;
            _tabDocumentTitle!.Text = $"{InputModeLabel(mode)} 문서 경로 오류";
            _tabDocumentStatus!.Text = ex.Message;
            _tabDocumentSave!.IsEnabled = _tabDocumentExecute!.IsEnabled = false;
            return;
        }

        bool pathChanged = !string.Equals(path, _activeTabDocumentPath, StringComparison.OrdinalIgnoreCase);
        if (pathChanged)
        {
            InvalidateActiveTabDocument();
            _activeTabDocumentPath = path;
            _updatingTabDocumentEditor = true;
            try
            {
                _tabDocumentEditor.Text = "";
                _tabDocumentEditor.IsEnabled = false;
            }
            finally { _updatingTabDocumentEditor = false; }
        }
        _activeTabDocumentPath = path;
        _tabDocumentTitle!.Text = Path.GetFileName(path);
        _tabDocumentTitle.ToolTip = path;
        if (_tabDocumentStates.TryGetValue(path, out var cached))
        {
            if (cached.IsLoaded)
            {
                ShowTabDocumentDraft(cached);
                RefreshTabDocumentControls(cached);
            }
            else if (!cached.IsLoading && cached.Error is null)
            {
                BeginTabDocumentLoad(project, mode, path, store, cached);
            }
            else
            {
                _tabDocumentEditor.IsEnabled = false;
                RefreshTabDocumentControls(cached);
            }
            return;
        }

        var state = new TabDocumentEditState(path, slot, mode);
        _tabDocumentStates.Add(path, state);
        _updatingTabDocumentEditor = true;
        try
        {
            _tabDocumentEditor.Text = "";
            _tabDocumentEditor.IsEnabled = false;
        }
        finally { _updatingTabDocumentEditor = false; }
        RefreshTabDocumentControls(state);
        BeginTabDocumentLoad(project, mode, path, store, state);
    }

    private void BeginTabDocumentLoad(
        string project,
        TerminalInputMode mode,
        string path,
        TerminalTabDocumentStore store,
        TabDocumentEditState state)
    {
        _tabDocumentLoadCancellation?.Cancel();
        _tabDocumentLoadCancellation?.Dispose();
        _tabDocumentLoadCancellation = new CancellationTokenSource();
        var cancellation = _tabDocumentLoadCancellation.Token;
        int generation = ++_tabDocumentGeneration;
        state.LoadGeneration = generation;
        _loadingTabDocumentState = state;
        state.IsLoading = true;
        state.Error = null;
        RefreshTabDocumentControls(state);
        state.LoadTask = LoadTabDocumentAsync(project, mode, path, store, state, generation, cancellation);
    }

    private async Task LoadTabDocumentAsync(
        string project,
        TerminalInputMode mode,
        string path,
        TerminalTabDocumentStore store,
        TabDocumentEditState state,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await store.LoadAsync(state.Slot, mode, cancellationToken);
            if (!IsTabDocumentLoadCurrent(state, path, generation, cancellationToken)) return;
            state.Snapshot = snapshot;
            state.Draft = snapshot.Text;
            state.IsLoaded = true;
            state.Error = null;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (IsTabDocumentLoadCurrent(state, path, generation, cancellationToken))
            {
                state.Error = "문서를 불러오지 못했습니다 · " + ex.Message;
                state.IsLoaded = false;
            }
        }
        finally
        {
            if (state.LoadGeneration == generation)
            {
                state.IsLoading = false;
                if (ReferenceEquals(_loadingTabDocumentState, state)) _loadingTabDocumentState = null;
            }
        }

        if (!IsTabDocumentLoadCurrent(state, path, generation, cancellationToken) ||
            !IsCurrentPlanDocumentBinding(path) ||
            !string.Equals(CanonicalProjectPath(project), CanonicalProjectPath(CurrentProjectPath), StringComparison.OrdinalIgnoreCase)) return;
        if (state.IsLoaded) ShowTabDocumentDraft(state);
        RefreshTabDocumentControls(state);
    }

    private bool IsTabDocumentLoadCurrent(
        TabDocumentEditState state,
        string path,
        int generation,
        CancellationToken cancellationToken) =>
        !_closing && !cancellationToken.IsCancellationRequested && generation == state.LoadGeneration &&
        string.Equals(path, _activeTabDocumentPath, StringComparison.OrdinalIgnoreCase);

    private void ShowTabDocumentDraft(TabDocumentEditState state)
    {
        if (_tabDocumentEditor is null || !string.Equals(state.Path, _activeTabDocumentPath, StringComparison.OrdinalIgnoreCase)) return;
        _updatingTabDocumentEditor = true;
        try
        {
            if (_tabDocumentEditor.Text != state.Draft) _tabDocumentEditor.Text = state.Draft;
            _tabDocumentEditor.IsEnabled = state.IsLoaded && !state.IsLoading && !_tabDocumentSaving;
        }
        finally { _updatingTabDocumentEditor = false; }
    }

    private void RefreshTabDocumentControls(TabDocumentEditState state)
    {
        UpdatePlanExecutionControls();
        if (!string.Equals(state.Path, _activeTabDocumentPath, StringComparison.OrdinalIgnoreCase)) return;
        bool active = !_closing && !_closePending && IsCurrentPlanDocumentBinding(state.Path) &&
            (_composerState is null || IsTerminalBoundToPlanPath(_composerState, state.Path));
        bool hasPendingInput = active && TryGetPendingProjectInput(state.Path, out _);
        bool dirty = state.IsLoaded && state.Snapshot is not null && !string.Equals(state.Draft, state.Snapshot.Text, StringComparison.Ordinal);
        string? executionBlock = PlanExecutionBlockReason(_composerState, _planExecutionMode);
        bool waitingForPlan = !dirty && IsReadyProjectWaitingForPlan(state);
        bool showExecutionBlock = _planExecutionMode == PlanExecutionMode.Conductor && active &&
            state.IsLoaded && !state.IsLoading && !_tabDocumentSaving &&
            !string.IsNullOrWhiteSpace(state.Draft) && executionBlock is not null;
        if (_tabDocumentStatus is not null)
        {
            _tabDocumentStatus.Text = state.Error ?? (state.IsLoading ? "불러오는 중…" :
                !state.IsLoaded ? "불러오기 실패" : showExecutionBlock ? executionBlock : dirty ? "미저장" :
                waitingForPlan ? "플랜 없음 · 작업 시작 대기" : "");
            _tabDocumentStatus.ToolTip = waitingForPlan
                ? "플랜 내용을 입력하면 시작할 수 있습니다. 지금은 OpenCode 터미널에 직접 입력해도 됩니다."
                : _tabDocumentStatus.Text;
            _tabDocumentStatus.Foreground = state.Error is not null || showExecutionBlock
                ? new SolidColorBrush(Color.FromRgb(232, 164, 154))
                : waitingForPlan ? new SolidColorBrush(Color.FromRgb(255, 190, 92))
                : new SolidColorBrush(Color.FromRgb(165, 173, 186));
        }
        if (_tabDocumentEditor is not null) _tabDocumentEditor.IsEnabled = active && state.IsLoaded && !state.IsLoading && !_tabDocumentSaving;
        if (_tabDocumentSave is not null)
        {
            _tabDocumentSave.IsEnabled = active && state.IsLoaded && !state.IsLoading && !_tabDocumentSaving && !_projectInputSending;
            _tabDocumentSave.ToolTip = hasPendingInput
                ? "plan.md와 입력한 요청을 저장 · Ctrl+S"
                : "플랜 저장 · Ctrl+S";
        }
        if (_tabDocumentExecute is not null)
        {
            _tabDocumentExecute.Content = "실행";
            bool canExecute = active && state.IsLoaded && !state.IsLoading && !_tabDocumentSaving && !_projectInputSending &&
                (hasPendingInput
                    ? !HasExternalTerminal(_projectInputProject!)
                    : !string.IsNullOrWhiteSpace(state.Draft) && executionBlock is null);
            _tabDocumentExecute.IsEnabled = canExecute;
            _tabDocumentExecute.ToolTip = hasPendingInput
                ? HasExternalTerminal(_projectInputProject!) ? ExternalTerminalBlockReason : "plan.md와 요청을 저장한 뒤 현재 터미널로 전송 · Ctrl+Enter"
                : executionBlock ?? (_planExecutionMode == PlanExecutionMode.Conductor
                    ? "현재 MD를 저장한 뒤 새 지휘자 작업 시작 · Ctrl+Enter"
                    : "현재 MD를 저장한 뒤 플랜 실행 · Ctrl+Enter");
        }
    }

    private bool TryGetPendingProjectInput(string planPath, out string project)
    {
        project = "";
        if (_projectInputProject is not { Length: > 0 } key ||
            !_projectInputDrafts.TryGetValue(key, out var draft) || string.IsNullOrWhiteSpace(draft.Text))
            return false;
        try
        {
            if (!string.Equals(Path.GetFullPath(planPath), Path.GetFullPath(DefaultProjectPlanPath(key)), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        project = key;
        return true;
    }

    private bool IsReadyProjectWaitingForPlan(TabDocumentEditState state)
    {
        if (!state.IsLoaded || state.IsLoading || state.Error is not null || !string.IsNullOrWhiteSpace(state.Draft) ||
            _tabDocumentSaving || _selectedProjectPath is not { Length: > 0 } project)
            return false;
        try
        {
            if (!string.Equals(Path.GetFullPath(state.Path), Path.GetFullPath(DefaultProjectPlanPath(project)), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        if (!_automationRuntimes.TryGetValue(CanonicalProjectPath(project), out var runtime) || runtime.SingleError is not null ||
            runtime.SingleTerminal is not { } tab ||
            !TerminalTabs.Items.Contains(tab) || tab.Tag is not TerminalTabState terminal)
            return false;
        var pane = terminal.Pane;
        if (!_projectPlanWaitSubmissionBaselines.TryGetValue(CanonicalProjectPath(project), out var baseline) ||
            !baseline.Pane.TryGetTarget(out var baselinePane) || !ReferenceEquals(baselinePane, pane) ||
            pane.NativeSubmissionGeneration != baseline.SubmissionGeneration ||
            pane.Activity is TerminalActivity.Active or TerminalActivity.NeedsInput or
                TerminalActivity.Exited or TerminalActivity.Error ||
            pane.NativeOpenCodeStatus is OpenCodeStatusKind.Busy or OpenCodeStatusKind.Pending)
            return false;
        return IsNativeOpenCodeTaskPane(pane) && pane.IsReady && pane.IsRunning &&
            !pane.NativeAutomationDiagnostic.Failed && !pane.NativeAutomationDiagnostic.Disposed;
    }

    private void RefreshActiveTabDocumentControls()
    {
        if (_activeTabDocumentPath is not { } path || !_tabDocumentStates.TryGetValue(path, out var state)) return;
        RefreshTabDocumentControls(state);
    }

    private bool _tabDocumentSaving;

    private async Task ReloadTabDocumentAsync()
    {
        if (_tabDocumentSaving) return;
        if (_activeTabDocumentPath is null || !_tabDocumentStates.TryGetValue(_activeTabDocumentPath, out var state)) return;
        bool dirty = state.IsLoaded && state.Snapshot is not null && !string.Equals(state.Draft, state.Snapshot.Text, StringComparison.Ordinal);
        if (dirty && MessageBox.Show(this,
            "현재 초안을 버리고 디스크의 최신 문서를 다시 읽을까요?",
            "Markdown 다시 읽기", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_composerState is null && _selectedProjectPath is null) return;
        string project = CanonicalProjectPath(_composerState?.Pane.WorkingDirectory ?? CurrentProjectPath);
        state.IsLoaded = false;
        state.Error = null;
        string path = Path.GetFullPath(DefaultProjectPlanPath(project));
        var store = new TerminalTabDocumentStore(project, path);
        BeginTabDocumentLoad(project, TerminalInputMode.Plan, path, store, state);
        await Task.CompletedTask;
    }

    private async Task SaveTabDocumentAsync(bool execute)
    {
        if (_closing || _closePending || _tabDocumentSaving || _activeTabDocumentPath is null ||
            !_tabDocumentStates.TryGetValue(_activeTabDocumentPath, out var edit) || !edit.IsLoaded || edit.IsLoading) return;
        var terminal = _composerState;
        if (terminal is null && _selectedProjectPath is null) return;
        if (terminal is not null && !IsTerminalBoundToPlanPath(terminal, edit.Path)) return;
        if (execute && edit.Mode != TerminalInputMode.Plan) return;
        if (TryGetPendingProjectInput(edit.Path, out var inputProject))
        {
            if (!string.Equals(inputProject, CanonicalProjectPath(terminal?.Pane.WorkingDirectory ?? CurrentProjectPath), StringComparison.OrdinalIgnoreCase))
                return;
            if (execute) await SubmitProjectInputAsync();
            else await SaveProjectInputAsync();
            return;
        }
        if (execute && string.IsNullOrWhiteSpace(edit.Draft))
        {
            SetStatus("실행할 플랜 내용을 먼저 입력하세요.");
            return;
        }

        TerminalTabState? capturedTerminal = terminal;
        string capturedProject = CanonicalProjectPath(terminal?.Pane.WorkingDirectory ?? CurrentProjectPath);
        long capturedIntent = RuntimeFor(capturedProject).IntentVersion;
        TerminalInputMode capturedMode = TerminalInputMode.Plan;
        const int capturedSlot = 1;
        string capturedDraft = edit.Draft;
        TabDocumentSnapshot expectedSnapshot = edit.Snapshot!;
        string capturedPath = edit.Path;
        PlanExecutionMode capturedExecutionMode = _planExecutionMode;
        bool documentSaved = false;
        _tabDocumentSaving = true;
        RefreshTabDocumentControls(edit);
        if (execute)
            RecordAutomationDiagnostic("plan_execute_requested", "MD 저장 후 실행 요청 · " + capturedExecutionMode);
        try
        {
            if (!string.Equals(Path.GetFullPath(capturedPath), Path.GetFullPath(DefaultProjectPlanPath(capturedProject)), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Plan 경로가 현재 프로젝트의 기준 MD와 다릅니다.");
            var store = new TerminalTabDocumentStore(capturedProject, capturedPath);
            string expectedPath = Path.GetFullPath(store.GetDocumentPath(capturedSlot, capturedMode));
            if (!string.Equals(Path.GetFullPath(capturedPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("문서 경로가 선택한 프로젝트·터미널·모드와 일치하지 않습니다.");
            var snapshot = await store.SaveAsync(capturedSlot, capturedMode, capturedDraft, expectedSnapshot);
            if (!string.Equals(Path.GetFullPath(snapshot.Path), expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("저장된 문서 경로가 선택한 프로젝트·터미널·모드와 일치하지 않습니다.");
            bool draftChangedDuringSave = !string.Equals(edit.Draft, capturedDraft, StringComparison.Ordinal);
            edit.Snapshot = snapshot;
            documentSaved = true;
            if (!draftChangedDuringSave) edit.Draft = snapshot.Text;
            edit.Error = null;
            if (!execute)
            {
                SetStatus("저장 완료 · " + Path.GetFileName(snapshot.Path));
                return;
            }

            bool sourceStillValid = !_closing && !_closePending &&
                RuntimeFor(capturedProject).IntentVersion == capturedIntent &&
                string.Equals(CanonicalProjectPath(CurrentProjectPath), capturedProject, StringComparison.OrdinalIgnoreCase) &&
                (capturedTerminal is null || TerminalTabs.Items.Cast<TabItem>().Any(tab =>
                    tab.Tag is TerminalTabState open && ReferenceEquals(open, capturedTerminal) &&
                    ReferenceEquals(open.Pane, capturedTerminal.Pane)));
            if (!sourceStillValid)
            {
                SetStatus("플랜 문서를 저장했습니다. 프로젝트가 바뀌어 실행하지 않았습니다.");
                return;
            }

            if (draftChangedDuringSave) throw new InvalidOperationException("저장 중 플랜 초안이 바뀌어 실행하지 않았습니다.");
            await ExecuteSavedPlanAsync(capturedTerminal, snapshot, capturedExecutionMode);
        }
        catch (OperationCanceledException)
        {
            if (!_closing) SetStatus(documentSaved
                ? "플랜은 저장했습니다. 실행을 취소했습니다."
                : "플랜 저장을 취소했습니다.");
        }
        catch (IOException ex)
        {
            edit.Error = documentSaved
                ? "플랜은 저장했지만 실행하지 못했습니다 · " + ex.Message
                : "MD 저장 실패 · 초안은 유지했습니다 · " + ex.Message;
            SetStatus(edit.Error);
            RecordAutomationDiagnostic(documentSaved ? "plan_execution_failed" : "plan_save_failed",
                edit.Error, severity: "error", exception: ex);
        }
        catch (Exception ex)
        {
            edit.Error = (documentSaved ? "플랜은 저장했지만 실행하지 못했습니다 · " : "문서를 저장하지 못했습니다 · ") + ex.Message;
            SetStatus(edit.Error);
            RecordAutomationDiagnostic(documentSaved ? "plan_execution_failed" : "plan_save_failed",
                edit.Error, severity: "error", exception: ex);
        }
        finally
        {
            _tabDocumentSaving = false;
            if (_tabDocumentStates.TryGetValue(edit.Path, out var current) && ReferenceEquals(current, edit))
            {
                ShowTabDocumentDraft(edit);
                RefreshTabDocumentControls(edit);
            }
            if (_activeTabDocumentPath is not null &&
                _tabDocumentStates.TryGetValue(_activeTabDocumentPath, out var active))
                RefreshTabDocumentControls(active);
        }
    }

    private async Task<AutomationPlanEditSession> LoadAutomationPlanAsync(
        TerminalTabState? terminal, string project, int slot, string planDocumentPath, bool refreshCleanSnapshot = false)
    {
        project = CanonicalProjectPath(project);
        slot = 1;
        string path = Path.GetFullPath(DefaultProjectPlanPath(project));
        if (!string.Equals(Path.GetFullPath(planDocumentPath), path, StringComparison.OrdinalIgnoreCase) ||
            !IsProjectBoundToPlanPath(project, path))
            throw new IOException("프로젝트의 플랜 경로가 바뀌었습니다. 프로젝트를 다시 선택하세요.");
        var store = new TerminalTabDocumentStore(project, path);
        if (terminal is not null && !IsTerminalBoundToPlanPath(terminal, path))
            throw new IOException("터미널의 플랜 경로가 바뀌었습니다. 프로젝트를 다시 선택하세요.");
        _tabDocumentStates.TryGetValue(path, out var edit);
        if (edit is { IsLoading: true, LoadTask: { } pendingLoad })
            await pendingLoad;
        if (terminal is not null && !IsTerminalBoundToPlanPath(terminal, path))
            throw new IOException("플랜 경로가 바뀌었습니다. 프로젝트를 다시 선택하세요.");
        if (edit is null || edit.Snapshot is null)
        {
            var snapshot = await store.LoadAsync(slot, TerminalInputMode.Plan);
            if (!_tabDocumentStates.TryGetValue(path, out edit))
            {
                edit = new TabDocumentEditState(path, slot, TerminalInputMode.Plan);
                _tabDocumentStates.Add(path, edit);
            }
            if (edit.Snapshot is null)
            {
                InvalidateTabDocumentLoadForState(edit);
                edit.Snapshot = snapshot;
                edit.Draft = snapshot.Text;
                edit.IsLoaded = true;
                edit.IsLoading = false;
                edit.Error = null;
            }
        }
        else if (refreshCleanSnapshot && edit.IsLoaded && edit.Snapshot is { } cachedSnapshot &&
            string.Equals(edit.Draft, cachedSnapshot.Text, StringComparison.Ordinal))
        {
            // Start should use the latest on-disk plan when the editor has no
            // unsaved changes. Recheck after the await so a concurrent edit wins.
            var refreshed = await store.LoadAsync(slot, TerminalInputMode.Plan);
            if (_tabDocumentStates.TryGetValue(path, out var currentEdit) && ReferenceEquals(currentEdit, edit) &&
                ReferenceEquals(edit.Snapshot, cachedSnapshot) &&
                string.Equals(edit.Draft, cachedSnapshot.Text, StringComparison.Ordinal))
            {
                edit.Snapshot = refreshed;
                edit.Draft = refreshed.Text;
                edit.IsLoaded = true;
                edit.IsLoading = false;
                edit.Error = null;
            }
        }
        if (!IsProjectBoundToPlanPath(project, path))
            throw new IOException("프로젝트의 플랜 경로가 바뀌었습니다. 프로젝트를 다시 선택하세요.");
        if ((terminal is not null || refreshCleanSnapshot) && IsCurrentPlanDocumentBinding(path))
        {
            ShowTabDocumentDraft(edit);
            RefreshTabDocumentControls(edit);
        }
        if (!edit.IsLoaded || edit.Snapshot is null)
            throw new IOException("플랜을 불러오는 중입니다. 잠시 뒤 다시 실행하세요.");
        return new AutomationPlanEditSession(
            terminal, Path.GetFullPath(project), slot, path, store, edit, edit.Snapshot!, edit.Draft);
    }

    private async Task<TabDocumentSnapshot> SaveAutomationPlanAsync(AutomationPlanEditSession session, string draft, bool inputSubmission = false)
    {
        if (_closing || _closePending || _tabDocumentSaving && !inputSubmission)
            throw new InvalidOperationException("플랜 문서를 저장할 수 없는 상태입니다.");
        if (string.IsNullOrWhiteSpace(draft))
            throw new InvalidOperationException("실행할 플랜 내용을 입력하세요.");
        if (session.Terminal is { } terminal && !TerminalTabs.Items.Cast<TabItem>().Any(tab =>
                tab.Tag is TerminalTabState open && ReferenceEquals(open, terminal) &&
                ReferenceEquals(open.Pane, terminal.Pane)))
            throw new IOException("대상 터미널이 닫혔습니다. 실행할 터미널을 다시 선택하세요.");
        if (session.Terminal is { } boundTerminal && !IsTerminalBoundToPlanPath(boundTerminal, session.Path))
            throw new IOException("터미널의 플랜 경로가 바뀌었습니다. 프로젝트를 다시 선택하세요.");
        if (!IsProjectBoundToPlanPath(session.Project, session.Path))
            throw new IOException("프로젝트의 플랜 MD 경로가 바뀌었습니다. 최신 경로에서 다시 실행하세요.");

        string expectedPath = Path.GetFullPath(session.Store.GetDocumentPath(session.Slot, TerminalInputMode.Plan));
        if (!string.Equals(expectedPath, session.Path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(session.Snapshot.Path), expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("플랜 문서 경로가 캡처한 프로젝트의 플랜에 맞지 않습니다.");
        if (session.Edit.IsLoading)
            throw new IOException("오른쪽 플랜 문서를 불러오는 중입니다. 잠시 뒤 다시 실행하세요.");
        if (!session.Edit.IsLoaded || !SameDocumentSnapshot(session.Edit.Snapshot, session.Snapshot) ||
            !string.Equals(session.Edit.Draft, session.InitialDraft, StringComparison.Ordinal))
            throw new IOException("플랜 초안이 바뀌었습니다. 최신 내용을 확인한 뒤 다시 실행하세요.");

        bool alreadySaving = _tabDocumentSaving;
        _tabDocumentSaving = true;
        RefreshTabDocumentControls(session.Edit);
        try
        {
            var saved = await session.Store.SaveAsync(session.Slot, TerminalInputMode.Plan, draft, session.Snapshot);
            session.Edit.Snapshot = saved;
            if (string.Equals(session.Edit.Draft, session.InitialDraft, StringComparison.Ordinal))
                session.Edit.Draft = saved.Text;
            session.Edit.IsLoaded = true;
            session.Edit.Error = null;
            session.Snapshot = saved;
            session.InitialDraft = saved.Text;
            return saved;
        }
        catch (Exception ex)
        {
            session.Edit.Error = "플랜 저장 실패 · 초안은 유지했습니다 · " + ex.Message;
            RecordAutomationDiagnostic("automation_plan_save_failed", session.Edit.Error,
                severity: "error", exception: ex);
            throw;
        }
        finally
        {
            _tabDocumentSaving = alreadySaving;
            if (_tabDocumentStates.TryGetValue(session.Path, out var current) && ReferenceEquals(current, session.Edit))
            {
                ShowTabDocumentDraft(session.Edit);
                RefreshTabDocumentControls(session.Edit);
            }
            if (_activeTabDocumentPath is not null &&
                _tabDocumentStates.TryGetValue(_activeTabDocumentPath, out var active))
                RefreshTabDocumentControls(active);
        }
    }

    private static bool SameDocumentSnapshot(TabDocumentSnapshot? left, TabDocumentSnapshot right) =>
        left is not null && left.Exists == right.Exists &&
        string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Text, right.Text, StringComparison.Ordinal);

    private void InvalidateTabDocumentLoadForState(TabDocumentEditState state)
    {
        state.LoadGeneration = ++_tabDocumentGeneration;
        state.IsLoading = false;
        state.LoadTask = null;
        if (!ReferenceEquals(_loadingTabDocumentState, state)) return;
        _tabDocumentLoadCancellation?.Cancel();
        _tabDocumentLoadCancellation?.Dispose();
        _tabDocumentLoadCancellation = null;
        _loadingTabDocumentState = null;
    }

    private sealed class AutomationPlanEditSession(
        TerminalTabState? terminal,
        string project,
        int slot,
        string path,
        TerminalTabDocumentStore store,
        TabDocumentEditState edit,
        TabDocumentSnapshot snapshot,
        string initialDraft)
    {
        public TerminalTabState? Terminal { get; } = terminal;
        public string Project { get; } = project;
        public int Slot { get; } = slot;
        public string Path { get; } = path;
        public TerminalTabDocumentStore Store { get; } = store;
        public TabDocumentEditState Edit { get; } = edit;
        public TabDocumentSnapshot Snapshot { get; set; } = snapshot;
        public string InitialDraft { get; set; } = initialDraft;
    }

    internal string CurrentPlanDocumentText()
    {
        var terminal = SelectedPlanTerminal();
        if (terminal is null && _selectedProjectPath is null) return "";
        try
        {
            string project = CanonicalProjectPath(terminal?.Pane.WorkingDirectory ?? CurrentProjectPath);
            string path = Path.GetFullPath(DefaultProjectPlanPath(project));
            if (_tabDocumentStates.TryGetValue(path, out var edit))
            {
                if (edit.IsLoaded && string.Equals(path, _activeTabDocumentPath, StringComparison.OrdinalIgnoreCase) && _tabDocumentEditor is not null)
                    return _tabDocumentEditor.Text;
                return edit.IsLoaded ? edit.Draft : "";
            }
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        return "";
    }

    private bool IsCurrentPlanDocumentBinding(string path)
    {
        if (_composerState is null && _selectedProjectPath is null) return false;
        try
        {
            string project = CanonicalProjectPath(_composerState?.Pane.WorkingDirectory ?? CurrentProjectPath);
            return string.Equals(Path.GetFullPath(DefaultProjectPlanPath(project)), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsTerminalBoundToPlanPath(TerminalTabState terminal, string path)
    {
        if (terminal.IsIndependent) return false;
        try
        {
            string project = CanonicalProjectPath(terminal.Pane.WorkingDirectory);
            string canonicalPath = Path.GetFullPath(DefaultProjectPlanPath(project));
            return string.Equals(canonicalPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(canonicalPath, Path.GetFullPath(terminal.PlanDocumentPath, project), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
    private void InvalidateActiveTabDocument()
    {
        if (_activeTabDocumentPath is null && _loadingTabDocumentState is null && _tabDocumentLoadCancellation is null) return;
        _tabDocumentGeneration++;
        _tabDocumentLoadCancellation?.Cancel();
        _tabDocumentLoadCancellation?.Dispose();
        _tabDocumentLoadCancellation = null;
        if (_loadingTabDocumentState is not null)
        {
            _loadingTabDocumentState.IsLoading = false;
            _loadingTabDocumentState = null;
        }
        _activeTabDocumentPath = null;
        _updatingTabDocumentEditor = true;
        try
        {
            if (_tabDocumentEditor is not null)
            {
                _tabDocumentEditor.Clear();
                _tabDocumentEditor.IsEnabled = false;
            }
        }
        finally { _updatingTabDocumentEditor = false; }
    }

    private void DisposeTabDocuments()
    {
        _tabDocumentGeneration++;
        _tabDocumentLoadCancellation?.Cancel();
        _tabDocumentLoadCancellation?.Dispose();
        _tabDocumentLoadCancellation = null;
        if (_loadingTabDocumentState is not null) _loadingTabDocumentState.IsLoading = false;
        _loadingTabDocumentState = null;
        _activeTabDocumentPath = null;
    }

    private sealed class TabDocumentEditState(string path, int slot, TerminalInputMode mode)
    {
        public string Path { get; } = path;
        public int Slot { get; } = slot;
        public TerminalInputMode Mode { get; } = mode;
        public string Draft { get; set; } = "";
        public TabDocumentSnapshot? Snapshot { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsLoading { get; set; }
        public int LoadGeneration { get; set; }
        public Task? LoadTask { get; set; }
        public string? Error { get; set; }
    }
}
