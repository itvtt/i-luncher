using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow : Window
{
    private MarkdownDocument _document = MarkdownDocument.Create();
    private string? _rememberedDocumentPath;
    public const string DefaultCodexWorkspace = @"C:\opencode";
    internal static string DefaultSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotes", "settings.json");
    private string _workspace = DefaultCodexWorkspace;
    private string? _rememberedSelectedProjectPath;
    private readonly bool _autoStartCodex;
    private readonly string? _initialWorkspace;
    private bool _startInWorkspaceProject;
    private TrayIconService? _tray;
    private bool _windowInitialized, _resourcesDisposed;
    private bool _loadingDocument, _closing, _notesHidden, _previewReady, _documentBusy, _closePending;
    private int _previewGeneration;
    private string _preferredNewline = Environment.NewLine;
    private string? _previewNavigationUri;
    private List<TerminalLaunchProfile> _terminalProfiles = [];
    private GridLength _savedNotesWidth = new(3, GridUnitType.Star);
    private List<DocumentEntry> _documents = [];
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly string _settingsPath;
    private Settings? _settings;
    private DispatcherHeartbeatWatchdog? _dispatcherHeartbeat;
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
    private Task? _previewInitialization;
    public MainWindow() : this(null) { }
    public MainWindow(string? settingsPath, bool autoStartCodex = true, string? initialWorkspace = null, OpenCodeStartupHooks? startupHooks = null)
    {
        _autoStartCodex = autoStartCodex;
        _initialWorkspace = initialWorkspace;
        _startupHooks = startupHooks;
        _settingsPath = settingsPath ?? DefaultSettingsPath;
        InitializeComponent();
        _settings = ReadSettings();
        _projectCategoryCatalog = new ProjectCategoryCatalog(_settings?.ProjectTaskCategories);
        CaptureConfiguredWindowMinimums();
        _dispatcherHeartbeat = new DispatcherHeartbeatWatchdog(Dispatcher, _settingsPath + ".ui-diagnostics.jsonl", duration =>
        {
            if (!_closing)
                SetStatus($"화면 응답이 {duration.TotalSeconds:0}초 중단된 뒤 복구되었습니다. 진단 기록: {_settingsPath}.ui-diagnostics.jsonl");
        });
        InitializeWorkbench();
        InitializeProjectTasks();
        NotesSplitter.DragCompleted += (_, _) => SaveSettings();
        UpdateToolbarState();
        Closed += (_, _) => { DetachWindowBoundsHook(); DisposeResources(); };
        TerminalTabs.ContextMenu = new ContextMenu();
        AddSessionMenuItems(TerminalTabs.ContextMenu);
        SourceInitialized += (_, _) => { ApplyDarkTitleBar(this); AttachWindowBoundsHook(); };
        LocationChanged += (_, _) => UpdateEffectiveWindowMinimums();
        StateChanged += (_, _) => OnWindowBoundsStateChanged();
        PreviewKeyDown += OnAppKeyDown;
        DataObject.AddPastingHandler(MarkdownEditor, (_, e) =>
        {
            if (e.DataObject.GetDataPresent(DataFormats.UnicodeText) && e.DataObject.GetData(DataFormats.UnicodeText) is string text)
                e.DataObject = new DataObject(DataFormats.UnicodeText, text.ReplaceLineEndings(_preferredNewline));
        });
        _previewTimer.Tick += async (_, _) => { _previewTimer.Stop(); if (DocumentTabs.SelectedIndex == 1) await RefreshPreviewAsync(); };
    }
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_windowInitialized) return;
        _windowInitialized = true;
        try { _tray = new TrayIconService(RestoreFromTray, () => CloseAllTerminals(), () => _ = RequestExitAsync()); }
        catch (Exception ex) { SetStatus("트레이 아이콘을 만들 수 없습니다 · " + ex.Message); }
        var settings = _settings;
        InitializeTaskNotificationOptions(settings?.TaskNotificationsEnabled ?? true, settings?.TaskNotificationsSnoozedUntilUtc);
        StartTaskNotificationMonitoring();
        _rememberedDocumentPath = settings?.Document;
        LoadTaskSidebarSettings(settings?.TaskSidebarCollapsed ?? false, settings?.TaskSidebarWidth ?? TaskSidebarDefaultWidth);
        LoadTaskPlanWidthSetting(settings?.TaskPlanWidth);
        LoadAutoResumeSetting(settings?.AutoResumeEnabled ?? true);
        LoadDocumentBindings(settings?.DocumentBindings);
        LoadProjectNames(settings?.ProjectNames);
        if (settings?.ProjectTaskMultiModes is { } modes)
            foreach (var (project, multi) in modes) _projectTaskMultiModes[CanonicalProjectPath(project)] = false;
        if (!string.IsNullOrWhiteSpace(settings?.SelectedProject))
        {
            try { _rememberedSelectedProjectPath = CanonicalProjectPath(settings.SelectedProject); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { _rememberedSelectedProjectPath = null; }
        }
        _useAutoMode = settings?.UseAutoMode ?? (settings?.TerminalProfiles?.LastOrDefault(profile => profile.Slot == 1)?.Mode == TerminalPermissionMode.Auto);
        _setupCompleted = settings?.SetupCompleted ?? false;
        _terminalProfiles = NormalizeProfiles(settings?.TerminalProfiles ?? []);
        bool persistNormalizedProfiles = _autoStartCodex && settings is not null &&
            (settings.LaunchDefaultsVersion < 2 ||
             settings.TerminalProfiles is { } storedProfiles && !storedProfiles.SequenceEqual(_terminalProfiles));
        // Retain the persisted workspace key for compatibility with existing settings.
        if (!string.IsNullOrWhiteSpace(settings?.CodexWorkspace)) _workspace = settings.CodexWorkspace;
        bool migrateWorkspace = settings is not null && settings.WorkspaceDefaultsVersion < 1 && IsLegacyDefaultWorkspace(_workspace);
        if (migrateWorkspace) _workspace = DefaultCodexWorkspace;
        if (_initialWorkspace is not null)
        {
            _workspace = Path.GetFullPath(_initialWorkspace);
            _selectedProjectPath = null;
            _startInWorkspaceProject = true;
        }
        ApplyInitialWindowBounds(settings?.Width ?? Width, settings?.Height ?? Height);
        var args = Environment.GetCommandLineArgs();
        var workspaceFlag = Array.IndexOf(args, "--workspace");
        if (workspaceFlag >= 0 && workspaceFlag + 1 < args.Length)
        {
            try
            {
                _workspace = Path.GetFullPath(args[workspaceFlag + 1]);
                _selectedProjectPath = null;
                _startInWorkspaceProject = true;
            }
            catch (Exception ex) { SetStatus("시작 경로가 올바르지 않습니다 · " + ex.Message); }
        }
        WorkspaceLabel.Text = _workspace; WorkspaceLabel.ToolTip = _workspace;
        InitializeSleepPrevention();
        _ = RefreshProjectTasksAsync();
        if (!_autoStartCodex)
        {
            InitializeAutomation();
            if (_selectedProjectPath is not null || _startInWorkspaceProject) AddTerminal();
            else UpdateWorkbenchTarget();
        }
        else if (_selectedProjectPath is not null || _startInWorkspaceProject) AddTerminal();
        else _ = PrepareOpenCodeAndStartAsync(null, createTerminal: false);
        RefreshConversationHistory();
        Activated += (_, _) => { if (!_closing) RefreshConversationHistory(); };
        if (persistNormalizedProfiles || migrateWorkspace) SaveSettings();
    }
    private TerminalPane? ActiveTerminal => (TerminalTabs.SelectedItem as TabItem)?.Tag is TerminalTabState state ? state.Pane : null;
    private void AddTerminal() => AddTerminalCore();
    private void AddTerminalCore(string? title = null, bool explicitNew = false, string? workingDirectory = null)
    {
        if (_closing || _closePending) return;
        if (explicitNew)
        {
            AddIndependentTerminal(title, workingDirectory);
            return;
        }
        if (_autoStartCodex && !_openCodeReady)
        {
            _ = PrepareOpenCodeAndStartAsync(title, explicitNew: explicitNew, workingDirectory: workingDirectory);
            return;
        }
        if (ManualTerminalCount >= 8) { SetStatus("일반 터미널은 최대 8개까지 열 수 있습니다."); return; }
        int slot = NextTerminalSlot();
        var launch = ResolveTerminalProfile(slot);
        if (!string.IsNullOrWhiteSpace(workingDirectory)) launch = launch with { WorkingDirectory = workingDirectory };
        var directory = launch.WorkingDirectory ?? _workspace;
        if (_selectedProjectPath is not null && !Directory.Exists(directory))
        {
            SetStatus("선택한 프로젝트 폴더를 찾을 수 없습니다. ‘프로젝트’에서 다시 선택하세요 · " + directory);
            return;
        }
        if (!EnsureWorkspaceDirectory(directory)) return;
        bool taskRunning = ProjectTaskIsRunning(directory);
        var runtime = RuntimeFor(directory);
        if (!explicitNew && taskRunning) { OpenProjectTask(directory); return; }
        if (!explicitNew && runtime.SingleTerminal is { } existing && TerminalTabs.Items.Contains(existing))
        {
            SetProjectTaskMode(directory, false);
            OpenProjectTask(directory);
            return;
        }
        bool hasTaskOwner = runtime.SingleTerminal is not null || runtime.Team.Count > 0;
        bool attachAsTask = explicitNew ? !taskRunning && !hasTaskOwner : true;
        TabItem item;
        try { item = CreateTerminalTab(slot, launch, title); }
        catch (Exception ex)
        {
            SetStatus("프로젝트 MD를 연결하지 못해 터미널을 시작하지 않았습니다 · " + ex.Message);
            return;
        }
        if (attachAsTask)
        {
            _projectTaskMultiModes[CanonicalProjectPath(directory)] = false;
            AttachSingleProjectTerminal(CanonicalProjectPath(directory), item);
        }
        // Extra sessions share the terminal area with the existing task views.
        int position = TerminalTabs.Items.Cast<TabItem>().Count(tab => ((TerminalTabState)tab.Tag).Sequence < slot);
        TerminalTabs.Items.Insert(position, item); TerminalTabs.SelectedItem = item;
        if (_arrangingTerminals)
        {
            if (!_fillingTerminalLayout) _terminalLayoutFitPending = true;
            ApplyTerminalLayout();
        }
        else FitTerminalLayoutToCount();
        UpdateTerminalCount();
        SelectTaskProject(directory);
        if (attachAsTask) ShowProjectTask(directory);
    }
    private TabItem CreateTerminalTab(int slot, ResolvedTerminalLaunch launch, string? title = null)
        => CreateTerminalTabCore(slot, launch, title, createPlanDocument: true);

    private TabItem CreateTerminalTabWithoutPlanDocument(int slot, ResolvedTerminalLaunch launch, string? title = null)
        => CreateTerminalTabCore(slot, launch, title, createPlanDocument: false);

    private TabItem CreateTerminalTabCore(int slot, ResolvedTerminalLaunch launch, string? title, bool createPlanDocument,
        string? existingPlanDocumentPath = null, bool suppressAutoResume = false, bool independent = false, string? independentSessionKey = null)
    {
        string project = launch.WorkingDirectory ?? CurrentProjectPath;
        string planDocumentPath = independent ? string.Empty : existingPlanDocumentPath ?? (createPlanDocument ? EnsureProjectPlanDocument(project) : DefaultProjectPlanPath(project));
        independentSessionKey = independent ? independentSessionKey ?? "independent-" + Guid.NewGuid().ToString("N") : null;
        var terminal = new TerminalPane(project, launch.LaunchCodex, launch.Command,
            launch.Tool == TerminalTool.Custom && !string.IsNullOrWhiteSpace(launch.Command),
            protectTuiViewport: launch.Tool == TerminalTool.OpenCode, planDocumentPath: independent ? null : planDocumentPath,
            projectName: independent ? null : ProjectDisplayName(project), paneKey: independentSessionKey ?? slot.ToString(CultureInfo.InvariantCulture),
            useProjectContext: !independent);
        terminal.SetAutoResumeEnabled(!independent && !suppressAutoResume && _autoResumeEnabled);
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var state = new TerminalTabState
        {
            Sequence = slot, Title = title ?? launch.Title, Pane = terminal, Tool = launch.Tool,
            PlanDocumentPath = planDocumentPath, IsIndependent = independent, IndependentSessionKey = independentSessionKey
        };
        string projectIdentity = Path.GetFileName(Path.TrimEndingDirectorySeparator(project));
        var lifecycleContextLock = new object();
        uint? lastSessionProcessId = terminal.SessionProcessId;
        terminal.LifecycleStageChanged += stage =>
        {
            lock (lifecycleContextLock)
            {
                lastSessionProcessId = terminal.SessionProcessId ?? lastSessionProcessId;
                _dispatcherHeartbeat?.UpdateTerminalContext(state.Sequence, projectIdentity, stage, lastSessionProcessId);
            }
        };
        terminal.LifecycleFailureReported += (operation, exceptionType, stage) =>
        {
            uint? processId;
            lock (lifecycleContextLock)
            {
                lastSessionProcessId = terminal.SessionProcessId ?? lastSessionProcessId;
                processId = lastSessionProcessId;
            }
            _dispatcherHeartbeat?.RecordTerminalFailure(state.Sequence, projectIdentity, processId, stage, operation, exceptionType);
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_closing)
                        SetStatus($"터미널 {state.Sequence}번 {operation} 오류 · {exceptionType}");
                }), DispatcherPriority.Background);
            }
            catch (InvalidOperationException) { }
        };
        _dispatcherHeartbeat?.UpdateTerminalContext(state.Sequence, projectIdentity, terminal.LastLifecycleStage,
            lastSessionProcessId, newSession: true);
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "×", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(12, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "터미널 닫기" };
        header.Children.Add(label); header.Children.Add(close);
        var item = new TabItem { Header = header, Content = terminal, Tag = state };
        UpdateTerminalTitle(item);
        var rename = new MenuItem { Header = "이름 바꾸기…" };
        rename.Click += (_, _) => RenameTerminal(item);
        item.ContextMenu = new ContextMenu(); item.ContextMenu.Items.Add(rename);
        var rerun = new MenuItem { Header = "저장된 plan.md 다시 읽기", Tag = item };
        rerun.Click += TerminalRerun_Click;
        if (!independent) item.ContextMenu.Items.Add(rerun);
        var interrupt = new MenuItem { Header = "이 터미널 중단", Tag = item };
        interrupt.Click += TerminalInterrupt_Click;
        item.ContextMenu.Items.Add(interrupt);
        var continueInCmd = new MenuItem { Header = "CMD에서 이어가기", Tag = item };
        ToolTipService.SetShowOnDisabled(continueInCmd, true);
        continueInCmd.Click += async (sender, _) =>
        {
            if ((sender as FrameworkElement)?.Tag is TabItem target)
                await ContinueTerminalInCmdAsync(target);
        };
        if (!independent) item.ContextMenu.Items.Add(continueInCmd);
        var closeMenu = new MenuItem { Header = "이 터미널 닫기" };
        closeMenu.Click += (_, _) => CloseTerminal(item, terminal);
        item.ContextMenu.Items.Add(closeMenu);
        item.ContextMenu.Items.Add(new Separator());
        AddActivityMenuItems(item.ContextMenu, terminal);
        AddSessionMenuItems(item.ContextMenu);
        if (!independent) item.ContextMenu.Opened += (_, _) => UpdateCmdTerminalMenuItem(item, continueInCmd);
        terminal.ContextMenu = CreateTerminalContextMenu(terminal);
        terminal.ContextMenuRequested += hasSelection =>
        {
            if (_closing || !TerminalTabs.Items.Contains(item)) return;
            UpdateTerminalContextMenu(terminal, hasSelection);
            terminal.ContextMenu.PlacementTarget = terminal;
            terminal.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            terminal.ContextMenu.IsOpen = true;
        };
        label.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { e.Handled = true; RenameTerminal(item); } };
        close.Click += (_, e) => { e.Handled = true; CloseTerminal(item, terminal); };
        terminal.StatusChanged += SetStatus;
        terminal.Shortcut += (key, shift) => HandleShortcut(key, shift, terminal);
        terminal.ActivityChanged += () => { UpdateDispatcher(); UpdateProjectTaskActivity(); };
        if (!independent) terminal.RestartStalledProcessAsync = request => RestartStalledSingleProjectAsync(item, request);
        terminal.TokenUsageChanged += () => UpdateDispatcher();
        if (!independent) terminal.InputSubmitted += text => ObserveManualSubmission(state, text);
        terminal.NativeSubmissionConfirmed += (version, generation) =>
        {
            if (independent) return;
            ObserveTaskNotificationSubmission(state, version, generation);
            ObserveNativeSubmissionConfirmation(state, version, generation);
        };
        terminal.ActivatedByUser += () => SelectTerminal(item, false);
        terminal.Ready += () =>
        {
            UpdateDispatcher();
            if (!_closing && IsActive && ActiveTerminal == terminal && _titleEditor is null && !MarkdownEditor.IsKeyboardFocusWithin) terminal.FocusTerminal();
        };
        terminal.ProcessExited += code => { state.ExitCode = code; UpdateTerminalTitle(item); };
        return item;
    }
    private void RenameTerminal(TabItem item) => BeginTitleEdit(item);
    private void SetTerminalTitle(TabItem item, string title)
    {
        if (item.Tag is not TerminalTabState state || string.IsNullOrWhiteSpace(title)) return;
        state.Title = title.Trim();
        if (_taskTerminalLabels.TryGetValue(item, out var label))
            label.Text = ProjectDisplayName(CanonicalProjectPath(state.Pane.WorkingDirectory));
        UpdateTerminalTitle(item);
        UpdateDispatcher();
    }
    private static void UpdateTerminalTitle(TabItem item)
    {
        var state = (TerminalTabState)item.Tag;
        var label = (TextBlock)((StackPanel)item.Header).Children[0];
        label.Text = $"{state.Sequence:00}  {state.Title}" + (state.ExitCode is int code ? $" · 종료 ({code})" : "");
        item.ToolTip = state.Title + "\n" + state.Pane.WorkingDirectory + "\n더블클릭 또는 우클릭으로 이름 변경";
    }
    private void CloseTerminal(TabItem item, TerminalPane terminal)
    {
        if (_closing || _closePending) return;
        if (terminal.IsRunning && MessageBox.Show(this, "이 터미널에서 실행 중인 셸과 프로그램이 종료됩니다. 닫을까요?", "터미널 닫기", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        DetachTaskTerminal(item);
        DisposeSafely(terminal); TerminalTabs.Items.Remove(item);
        FitTerminalLayoutAfterExplicitClose();
        UpdateTerminalCount();
        if (TerminalTabs.Items.Count == 0) SetStatus("새 터미널을 열어 작업을 시작하세요. Ctrl+Shift+T");
    }
    private void UpdateTerminalCount()
    {
        TerminalCountLabel.Text = $"터미널 {TerminalTabs.Items.Count}";
        _tray?.UpdateSessionCount(TerminalTabs.Items.Count);
        UpdateDispatcher();
    }
    private void AddSessionMenuItems(ContextMenu menu)
    {
        AddTaskNotificationOptions(menu);
        menu.Items.Add(new Separator());
        var closeAll = new MenuItem { Header = "터미널 모두 닫기" };
        closeAll.Click += (_, _) => CloseAllTerminals();
        menu.Items.Add(closeAll);
        var hide = new MenuItem { Header = "트레이로 보내기" };
        hide.Click += (_, _) => HideToTray();
        menu.Items.Add(hide);
        var exit = new MenuItem { Header = "프로그램 종료" };
        exit.Click += async (_, _) => await RequestExitAsync();
        menu.Items.Add(exit);
    }
    private void CloseAllTerminals(bool confirm = true)
    {
        if (_closing || _closePending) return;
        var tabs = TerminalTabs.Items.Cast<TabItem>().ToArray();
        if (tabs.Length == 0) { FitTerminalLayoutAfterExplicitClose(); SetStatus("열려 있는 터미널이 없습니다."); return; }
        if (confirm)
        {
            RestoreFromTray();
            if (MessageBox.Show(this, $"터미널 {tabs.Length}개와 그 안에서 실행 중인 모든 작업을 종료할까요?", "터미널 모두 닫기", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        foreach (var tab in tabs)
        {
            DetachTaskTerminal(tab);
            DisposeSafely(((TerminalTabState)tab.Tag).Pane);
        }
        TerminalTabs.Items.Clear();
        FitTerminalLayoutAfterExplicitClose();
        UpdateTerminalCount();
        SetStatus("모든 터미널을 종료했습니다. 새 OpenCode 또는 셸을 열 수 있습니다.");
    }
    private void HideToTray()
    {
        if (_closing || _closePending) return;
        if (_tray is null) { SetStatus("트레이 아이콘이 없어 창을 숨길 수 없습니다."); return; }
        SaveSettings();
        Hide();
        _tray.ShowHiddenNotice();
    }
    private void RestoreFromTray()
    {
        if (_closing) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _ = Dispatcher.BeginInvoke(() => ActiveTerminal?.FocusTerminal(), DispatcherPriority.Background);
    }
    private void NewTerminal_Click(object sender, RoutedEventArgs e) => CreateProfileTerminal();
    internal bool ActivateFromLauncher(string? project)
    {
        if (_closing || _closePending) return false;
        RestoreFromTray();
        if (project is not null && Directory.Exists(project)) SelectProject(project);
        return true;
    }
    private void Interrupt_Click(object sender, RoutedEventArgs e) => ActiveTerminal?.Interrupt();
    private void TerminalTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == TerminalTabs && !_arrangingTerminals)
        {
            if ((TerminalTabs.SelectedItem as TabItem)?.Tag is TerminalTabState { IsIndependent: false } selected)
            {
                string project = CanonicalProjectPath(selected.Pane.WorkingDirectory);
                if (!string.Equals(_selectedProjectPath, project, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedProjectPath = project;
                    SaveSettings();
                }
            }
            ApplyTerminalLayout();
            UpdateTerminalLifecycleButtons();
            Dispatcher.BeginInvoke(() => ActiveTerminal?.FocusTerminal(), DispatcherPriority.Background);
        }
    }
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ManualTerminalCount >= 8) { SetStatus("새 폴더에 터미널을 열려면 기존 일반 터미널 하나를 닫아주세요."); return; }
        var dialog = new OpenFolderDialog { Title = "OpenCode 작업 경로 선택 · 다음 실행에도 적용", InitialDirectory = Directory.Exists(_workspace) ? _workspace : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (_documentBusy || dialog.ShowDialog(this) != true || !await ConfirmDocumentChangeAsync()) return;
        _workspace = dialog.FolderName; _selectedProjectPath = null; WorkspaceLabel.Text = _workspace; WorkspaceLabel.ToolTip = _workspace;
        SetDocument(MarkdownDocument.Create());
        AddTerminal();
        _ = ScanDocumentsAsync();
        SaveSettings();
        SetStatus("기본 폴더를 저장했습니다 · " + _workspace);
    }
    private void ToggleNotes_Click(object sender, RoutedEventArgs e) => ToggleNotes();
    private void ToggleNotes()
    {
        if (_taskWorkspaceVisible)
        {
            SetTaskSidebarCollapsed(!_taskSidebarCollapsed);
            return;
        }
        if (!_notesHidden)
        {
            _savedNotesWidth = NotesColumn.Width;
        }
        _notesHidden = !_notesHidden;
        _taskSidebarCollapsed = _notesHidden;
        NotesColumn.Width = _notesHidden ? new GridLength(0) : _savedNotesWidth;
        ApplyTaskSidebarState(animate: false);
        ApplyNotesVisibility();
        UpdateToolbarState();
        SaveSettings();
        ActiveTerminal?.FocusTerminal();
    }

    private void ApplyNotesVisibility()
    {
        NotesPanel.Visibility = NotesSplitter.Visibility = _notesHidden ? Visibility.Collapsed : Visibility.Visible;
        FocusButton.Visibility = Visibility.Visible;
        if (_taskWorkspaceVisible && _taskPlanColumn is not null && _taskRightArea is not null)
        {
            NotesColumn.MinWidth = 0;
            _taskPlanColumn.MinWidth = _notesHidden ? 0 : 240;
            _taskRightArea.ColumnDefinitions[1].Width = new GridLength(18);
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
        }
        else
        {
            NotesColumn.MinWidth = _notesHidden ? 0 : 240;
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(18);
        }
    }
    private async void NewDocument_Click(object sender, RoutedEventArgs e) => await NewDocumentAsync();
    private async Task NewDocumentAsync()
    {
        if (_documentBusy || !await ConfirmDocumentChangeAsync()) return;
        SetDocument(MarkdownDocument.Create());
        if (_notesHidden) ToggleNotes();
        DocumentTabs.SelectedIndex = 0; MarkdownEditor.Focus();
    }
    private async void OpenDocument_Click(object sender, RoutedEventArgs e) => await ChooseDocumentAsync();
    private async Task ChooseDocumentAsync()
    {
        if (_documentBusy) return;
        var dialog = new OpenFileDialog { Title = "Markdown 문서 열기", Filter = "Markdown 문서|*.md;*.markdown|텍스트 문서|*.txt|모든 파일|*.*", InitialDirectory = _workspace };
        if (dialog.ShowDialog(this) == true) await OpenDocumentAsync(dialog.FileName);
    }
    private async Task OpenDocumentAsync(string path, bool confirm = true)
    {
        if (_documentBusy || (confirm && !await ConfirmDocumentChangeAsync())) return;
        SetDocumentBusy(true);
        try
        {
            var loaded = await Task.Run(() => MarkdownDocument.Load(path));
            if (_closing) return;
            SetDocument(loaded);
            if (_notesHidden) ToggleNotes();
            SetStatus("문서를 열었습니다 · " + Path.GetFileName(path));
            SaveSettings();
        }
        catch (Exception ex) { ShowError("문서를 열 수 없습니다", ex); }
        finally { SetDocumentBusy(false); }
    }
    private void SetDocument(MarkdownDocument document)
    {
        _loadingDocument = true;
        _document = document;
        var firstNewline = document.Text.IndexOfAny(['\r', '\n']);
        _preferredNewline = firstNewline < 0 ? Environment.NewLine : document.Text[firstNewline] == '\n' ? "\n" : firstNewline + 1 < document.Text.Length && document.Text[firstNewline + 1] == '\n' ? "\r\n" : "\r";
        MarkdownEditor.Text = document.Text;
        _loadingDocument = false;
        UpdateDocumentState();
        _previewTimer.Stop(); _previewTimer.Start();
    }
    private async Task<bool> ConfirmDocumentChangeAsync()
    {
        if (!_document.IsDirty) return true;
        var answer = MessageBox.Show(this, "현재 문서의 변경 내용을 저장할까요?\n" + (_document.FilePath ?? "새 문서.md"), "저장하지 않은 변경", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && await SaveDocumentAsync());
    }
    private async void SaveDocument_Click(object sender, RoutedEventArgs e) => await SaveDocumentAsync();
    private async Task<bool> SaveDocumentAsync(bool saveAs = false)
    {
        if (_documentBusy) return false;
        string? target = null;
        if (saveAs || _document.FilePath is null)
        {
            var dialog = new SaveFileDialog { Title = "Markdown 문서 저장", Filter = "Markdown 문서|*.md|Markdown 문서|*.markdown|텍스트 문서|*.txt", DefaultExt = ".md", AddExtension = true, OverwritePrompt = false, InitialDirectory = _document.FilePath is null ? _workspace : Path.GetDirectoryName(_document.FilePath), FileName = Path.GetFileName(_document.FilePath ?? "새 문서.md") };
            if (dialog.ShowDialog(this) != true) return false;
            target = dialog.FileName;
        }
        SetDocumentBusy(true);
        try
        {
            try { await Task.Run(() => _document.Save(target)); }
            catch (DocumentConflictException ex)
            {
                if (MessageBox.Show(this, ex.Message + "\n\n현재 편집 내용으로 덮어쓸까요? 아니오를 선택하면 편집 내용은 유지됩니다.", "디스크의 문서 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
                try { await Task.Run(() => _document.Save(target, true)); } catch (Exception retry) { ShowError("저장 실패", retry); return false; }
            }
            catch (Exception ex) { ShowError("저장 실패", ex); return false; }
            UpdateDocumentState(); SaveSettings(); _ = ScanDocumentsAsync(); SetStatus("저장 완료 · " + _document.FilePath); return true;
        }
        finally { SetDocumentBusy(false); }
    }
    private void SetDocumentBusy(bool busy)
    {
        _documentBusy = busy; MarkdownEditor.IsReadOnly = busy;
        UpdateToolbarState();
        if (busy) SetStatus("문서 처리 중… 터미널은 계속 사용할 수 있습니다.");
        else if (StatusLabel.Text.StartsWith("문서 처리 중", StringComparison.Ordinal)) SetStatus("준비 완료 · 편집 내용은 유지됩니다.");
    }
    private void MarkdownEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingDocument || DocumentTitle is null) return;
        _document.Text = MarkdownEditor.Text; UpdateDocumentState();
        _previewTimer.Stop(); _previewTimer.Start();
    }
    private void UpdateDocumentState()
    {
        var name = Path.GetFileName(_document.FilePath ?? "새 문서.md");
        DocumentTitle.Text = (_document.IsDirty ? "●  " : "") + name;
        DocumentTitle.ToolTip = _document.FilePath;
        DocumentState.Text = _document.IsDirty ? "변경됨 · Ctrl+S로 저장" : _document.FilePath is null ? "저장되지 않은 문서" : "저장됨 · " + _document.FilePath;
        DocumentState.ToolTip = _document.FilePath;
        UpdateToolbarState();
        Title = "Terminal Notes";
    }
    private void MarkdownEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (EditorPosition is null) return;
        int index = MarkdownEditor.CaretIndex, line = MarkdownEditor.GetLineIndexFromCharacterIndex(index);
        EditorPosition.Text = $"Ln {line + 1}, Col {index - MarkdownEditor.GetCharacterIndexFromLineIndex(Math.Max(0, line)) + 1}  ·  {MarkdownEditor.Text.Length:N0}자";
    }
    private async void DocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == DocumentTabs && PreviewDocumentButton is not null) UpdateToolbarState();
        if (e.Source == DocumentTabs && DocumentTabs.SelectedIndex == 1 && PreviewBrowser is not null) await RefreshPreviewAsync();
    }
    private async Task InitPreviewAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(OpenCodeProjectContext.DataDirectory, "WebView2"));
        if (_closing) return;
        await PreviewBrowser.EnsureCoreWebView2Async(env);
        if (_closing) return;
        var core = PreviewBrowser.CoreWebView2;
        core.Settings.IsScriptEnabled = false; core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NavigationStarting += (_, e) => { if (e.Uri != "about:blank" && e.Uri != _previewNavigationUri) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NavigationCompleted += (_, e) => { if (!e.IsSuccess && !_closing) SetStatus("미리보기 로드 실패 · " + e.WebErrorStatus); };
        var initializedView = PreviewBrowser;
        core.ProcessFailed += (_, _) =>
        {
            if (_closing || PreviewBrowser != initializedView) return;
            _previewReady = false; _previewInitialization = null;
            PreviewBrowser = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(17, 25, 35) };
            ((TabItem)DocumentTabs.Items[1]).Content = PreviewBrowser;
            initializedView.Dispose();
            SetStatus("미리보기 화면이 종료되었습니다. 편집 탭을 선택한 뒤 미리보기를 다시 열어주세요.");
        };
        _previewReady = true;
    }
    private async Task RefreshPreviewAsync()
    {
        if (_closing) return;
        try
        {
            if (!_previewReady) { _previewInitialization ??= InitPreviewAsync(); await _previewInitialization; }
            if (_closing || !_previewReady) return;
            var text = _document.Text;
            if (text.Length > 500_000) { SetStatus("큰 문서는 편집만 지원합니다. 미리보기는 500,000자 이하 문서에서 사용하세요."); NavigatePreview("<body style='background:#111923;color:#d4dfeb'>미리보기 크기 제한 (500,000자)</body>"); return; }
            int generation = ++_previewGeneration;
            var body = await Task.Run(() => Markdown.ToHtml(text, _pipeline));
            if (_closing || generation != _previewGeneration || text != _document.Text) return;
            NavigatePreview("<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'; img-src 'none';\"><style>body{background:#111923;color:#cad6e3;font:14px/1.8 'Segoe UI','Malgun Gothic',sans-serif;padding:12px 22px;overflow-wrap:anywhere}h1,h2,h3{color:#edf4fb;line-height:1.35}h1{font-size:25px;border-bottom:1px solid #2b3a49;padding-bottom:15px}h2{font-size:20px;margin-top:28px}a{color:#78dfbd}pre{background:#0b121a;padding:14px;overflow:auto;border-radius:6px}code{font-family:Consolas,monospace;color:#a5e1ce}blockquote{border-left:3px solid #4ea68d;margin-left:0;padding-left:15px;color:#8ea9bc}table{border-collapse:collapse;width:100%}td,th{border:1px solid #2b3a49;padding:7px;text-align:left}hr{border:0;border-top:1px solid #2b3a49}img{display:none}</style></head><body>" + body + "</body></html>");
        }
        catch (Exception ex) { _previewInitialization = null; SetStatus("미리보기를 열 수 없습니다 · " + ex.Message); }
    }
    private void NavigatePreview(string html)
    {
        _previewNavigationUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(html));
        PreviewBrowser.CoreWebView2.Navigate(_previewNavigationUri);
    }
    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_document.FilePath is null) { SetStatus("문서를 먼저 저장하면 경로를 복사할 수 있습니다."); return; }
        try { Clipboard.SetText(_document.FilePath); SetStatus("문서 경로를 복사했습니다. 터미널에 붙여넣어 사용할 수 있습니다."); }
        catch (Exception ex) { ShowError("클립보드 오류", ex); }
    }
    private Task ScanDocumentsAsync()
    {
        RefreshConversationHistory();
        return Task.CompletedTask;
    }
    private static List<DocumentEntry> EnumerateDocuments(string root)
    {
        var found = new List<DocumentEntry>();
        var pending = new Stack<(string path, int depth)>(); pending.Push((root, 0));
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "packages", "dist", "artifacts", "vendor" };
        int visited = 0;
        while (pending.Count > 0 && found.Count < 500 && visited++ < 3000)
        {
            var (path, depth) = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    if (found.Count >= 500) break;
                    if ((Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".markdown", StringComparison.OrdinalIgnoreCase))) found.Add(new(file, Path.GetRelativePath(root, file)));
                }
                if (depth >= 5) continue;
                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith('.') || ignored.Contains(name) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push((dir, depth + 1));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return found.OrderBy(e => Path.GetFileName(e.Path).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(e => e.Display, StringComparer.OrdinalIgnoreCase).ToList();
    }
    private void DocumentSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterDocuments();
    private void OnAppKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && MarkdownEditor.IsKeyboardFocusWithin && !_documentBusy && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            e.Handled = true;
            int next = MarkdownEditor.SelectionStart + _preferredNewline.Length;
            MarkdownEditor.SelectedText = _preferredNewline;
            MarkdownEditor.CaretIndex = next;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || (Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        string key = e.Key.ToString().ToLowerInvariant();
        if (shift && key is "t" or "m") { e.Handled = true; if (!e.IsRepeat) HandleShortcut(key, shift, ActiveTerminal); }
    }
    private void HandleShortcut(string key, bool shift, TerminalPane? source)
    {
        if (_closing || _closePending) return;
        switch (key) { case "t" when shift: CreateProfileTerminal(source); break; case "m" when shift: ToggleNotes(); break; }
    }
    private void SetStatus(string message) { if (!_closing) StatusLabel.Text = message.ReplaceLineEndings(" "); }
    private void ShowError(string title, Exception ex) { SetStatus(title + " · " + ex.Message); MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
    private Settings? ReadSettings()
    {
        try { return File.Exists(_settingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath)) : null; }
        catch { return null; }
    }
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            string? documentPath = _document.FilePath ?? _rememberedDocumentPath;
            if (_document.FilePath is not null) _rememberedDocumentPath = _document.FilePath;
            if (_taskWorkspaceVisible && !_notesHidden) SyncTaskPlanWidths();
            else if (!_taskWorkspaceVisible && !_notesHidden) _savedNotesWidth = NotesColumn.Width;
            CaptureTaskSidebarWidth();
        var settings = new Settings(_workspace, documentPath, WindowState == WindowState.Normal ? Width : RestoreBounds.Width, WindowState == WindowState.Normal ? Height : RestoreBounds.Height, _workspace, _terminalProfiles.ToArray(), LaunchDefaultsVersion: 2, DocumentBindings: _documentBindings.ToArray(), WorkspaceDefaultsVersion: 1, ProjectNames: _projectNames.ToArray(), UseAutoMode: _useAutoMode, SetupCompleted: _setupCompleted, OpenCodeOnly: "Y", SelectedProject: _selectedProjectPath ?? _rememberedSelectedProjectPath, AutoResumeEnabled: _autoResumeEnabled, ProjectTaskMultiModes: new(_projectTaskMultiModes), TaskSidebarCollapsed: _taskSidebarCollapsed, TaskSidebarWidth: _taskSidebarWidth, TaskNotificationsEnabled: _taskNotificationPreferences.Enabled, TaskNotificationsSnoozedUntilUtc: _taskNotificationPreferences.SnoozedUntilUtc, ProjectTaskCategories: _projectCategoryCatalog.Snapshot());
            settings = settings with { TaskPlanWidth = TaskPlanTerminalWidthRatio() };
            var temp = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings)); File.Move(temp, _settingsPath, true);
        }
        catch (Exception ex) { SetStatus("환경 설정 저장 실패 · " + ex.Message); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        if (_tray is null) { _ = Dispatcher.BeginInvoke(new Action(() => _ = RequestExitAsync())); return; }
        HideToTray();
    }
    private async Task RequestExitAsync()
    {
        if (_closing || _closePending) return;
        if (_projectArchiveWrites.Count > 0) { SetStatus("프로젝트 이동을 마친 뒤 종료하세요."); return; }
        if (_externalTerminalWrites.Count > 0) { SetStatus("CMD 인계를 마친 뒤 종료하세요."); return; }
        RestoreFromTray();
        if (_documentBusy) { SetStatus("문서 처리가 끝난 뒤 닫아주세요."); return; }
        _closePending = true;
        SetExitInputBlocked(true);
        SetStatus("종료 준비 중 · 진행 중인 터미널은 계속 사용할 수 있습니다.");
        UpdateWorkbenchTarget();
        try
        {
            // Pause new managed dispatches while existing terminal input remains usable.
            // A stuck poll or journal must not leave exit preparation latched forever.
            using (var preparationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                while (_automationPolling) await Task.Delay(25, preparationTimeout.Token);
                while (_journalWrites.Count > 0)
                {
                    var accepted = _journalWrites.ToArray();
                    await Task.WhenAll(accepted).WaitAsync(preparationTimeout.Token);
                    foreach (var completed in accepted) _journalWrites.Remove(completed);
                }
            }
            if (!await ConfirmDocumentChangeAsync()) return;
            if (TerminalTabs.Items.Cast<TabItem>().Any(t => ((TerminalTabState)t.Tag).Pane.IsRunning) && MessageBox.Show(this, "앱을 닫으면 열려 있는 모든 터미널과 실행 중인 프로그램이 종료됩니다. 닫을까요?", "Terminal Notes 종료", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SaveSettings();
            RecordAutomationDiagnostic("application_exit", "프로그램 종료 · 진행·예약 작업은 다음 시작 시 자동 재개하며 사용자 일시정지는 유지합니다.",
                _automationRuntimes.Values.Select(runtime => runtime.Stage?.Claim ?? runtime.Dispatching).FirstOrDefault(claim => claim is not null));
            await _automationDiagnosticWrites.WaitAsync(TimeSpan.FromSeconds(15));
            DisposeResources();
            Close();
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { SetStatus("종료 준비가 15초 이상 응답하지 않아 취소했습니다. 터미널은 계속 사용할 수 있으며 진행 중인 기록은 보존합니다."); }
        catch (TimeoutException) { SetStatus("진단 기록 저장이 응답하지 않아 종료하지 않았습니다. 잠시 후 다시 종료해 주세요."); }
        catch (Exception ex) { SetStatus("기록 처리를 마치지 못해 종료하지 않았습니다 · " + ex.Message); }
        finally
        {
            if (!_closing)
            {
                _closePending = false;
                SetExitInputBlocked(false);
                UpdateWorkbenchTarget();
            }
        }
    }

    private void SetExitInputBlocked(bool blocked)
    {
        NewTerminalButton.IsEnabled = !blocked && ManualTerminalCount < 8;
        if (_automationWindow is not null) _automationWindow.IsEnabled = !blocked;
    }
    internal void DisposeResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true;
        _closing = true;
        _dispatcherHeartbeat?.Dispose();
        _dispatcherHeartbeat = null;
        StopTaskSidebarAnimation();
        DisposeTaskNotifications();
        DisposeSleepPrevention();
        _startupCancellation.Cancel();
        DisposeConversationHistory();
        CloseCompactWindows();
        DisposeAutomation();
        _previewTimer.Stop();
        foreach (var tab in TerminalTabs.Items.Cast<TabItem>()) DisposeSafely(((TerminalTabState)tab.Tag).Pane);
        DisposeSafely(PreviewBrowser);
        DisposeSafely(_tray);
        _tray = null;
    }
    private static void DisposeSafely(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }
    internal bool PrepareForSystemShutdown()
    {
        if (_projectArchiveWrites.Count > 0) { SetStatus("프로젝트 이동 중입니다. 이동을 마친 뒤 종료하세요."); return false; }
        if (_externalTerminalWrites.Count > 0) { SetStatus("CMD 인계를 마친 뒤 종료하세요."); return false; }
        if (_documentBusy || _document.IsDirty)
        {
            RestoreFromTray();
            SetStatus("Windows 종료를 취소했습니다. 문서 작업을 마치고 저장한 뒤 다시 종료하세요.");
            return false;
        }
        SaveSettings();
        return true;
    }
    private sealed record DocumentEntry(string Path, string Display);
    private sealed class TerminalTabState
    {
        public required TerminalPane Pane { get; init; }
        public int Sequence { get; init; }
        public TerminalTool Tool { get; init; }
        public string Title { get; set; } = "";
        public bool IsIndependent { get; init; }
        public string? IndependentSessionKey { get; init; }
        public required string PlanDocumentPath { get; set; }
        public int? ExitCode { get; set; }
        public TerminalInputMode InputMode { get; set; } = TerminalInputMode.Plan;
        public string InputDraft { get; set; } = "";
        public bool ManualSubmissionPending { get; set; }
        public long ManualSubmissionConfirmationBaseline { get; set; }
        public long ManualSubmissionGeneration { get; set; }
    }
    private sealed record Settings(string Workspace, string? Document, double Width = 1480, double Height = 920, string? CodexWorkspace = null, TerminalLaunchProfile[]? TerminalProfiles = null, int LaunchDefaultsVersion = 0, ProjectDocumentBinding[]? DocumentBindings = null, int WorkspaceDefaultsVersion = 0, ProjectNameBinding[]? ProjectNames = null, bool? UseAutoMode = null, bool SetupCompleted = false, string OpenCodeOnly = "Y", string? SelectedProject = null, bool AutoResumeEnabled = true, Dictionary<string, bool>? ProjectTaskMultiModes = null, bool TaskSidebarCollapsed = false, double TaskSidebarWidth = TaskSidebarDefaultWidth, bool TaskNotificationsEnabled = true, DateTimeOffset? TaskNotificationsSnoozedUntilUtc = null, ProjectCategoryWorkspaceState[]? ProjectTaskCategories = null, double? TaskPlanWidth = null);
    private static void ApplyDarkTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        int dark = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        // Windows 11 caption colors use COLORREF (0x00BBGGRR), preserving native window controls.
        int caption = 0x00211B18, text = 0x00F8F5F3;
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
