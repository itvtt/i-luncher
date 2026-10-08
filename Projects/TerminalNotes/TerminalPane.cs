using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TerminalNotes.Controls;
using TerminalNotes.Services;
using TerminalNotes.TerminalEngine;

namespace TerminalNotes;

public sealed partial class TerminalPane : Grid, IDisposable
{
    private const string Origin = "https://terminalnotes.local/";
    private readonly WebView2 _browser = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(12, 16, 22) };
    private readonly TextBlock _notice = new() { Text = "터미널을 시작하고 있습니다…", Margin = new Thickness(24), Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap };
    private readonly ConPtySession _session = new();
    private readonly TerminalActivityTracker _activityTracker = new();
    private readonly TerminalInputCapture _inputCapture = new();
    private readonly TerminalTokenUsage _tokenUsage = new();
    private readonly OpenCodeProjectContext? _openCodeProjectContext;
    private readonly string? _openCodeConfigContent;
    private readonly Queue<string> _pending = new();
    private readonly Dictionary<long, TaskCompletionSource> _pendingTextSends = [];
    private long _nextTextSendId;
    private int _queuedCharacters;
    private bool _writeInFlight, _failed;
    private string? _retryOutput;
    private int _outputPostFailures;
    private readonly object _outputLock = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _activityTimer;
    private bool _ready, _disposed, _automationBusy, _workFailed;
    private readonly TaskCompletionSource<Exception?> _loadedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> _terminalStartedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _initializeTask;
    private (short Columns, short Rows)? _pendingWebReadyDimensions;
    private readonly bool _startupWork;
    private readonly bool _protectTuiViewport;
    private readonly bool _usesNativeOpenCodeStatus;
    private readonly bool _usesNativeOpenCodeTui;
    private readonly bool _useNativeTerminalSurface;
    private readonly string? _effectiveStartupCommand;
    private readonly OpenCodeStatusMonitor? _openCodeStatus;
    private int _sessionCloseStarted;
    private string _lastLifecycleStage = "initializing";
    private OpenCodeStatusSnapshot? _openCodeSnapshot;
    private bool _manualInputOverride;
    private bool _lastNativeRecentActivity;
    private OpenCodeSubmissionObservation? _lastManualSubmissionObservation;
    public string WorkingDirectory { get; }
    public bool LaunchesCodex { get; }
    public string? StartupCommand { get; }
    internal bool IsOpenCodeTui => _usesNativeOpenCodeTui;
    internal bool IsOpenCodeTuiAttached => _usesNativeOpenCodeTui && _session.IsRunning;
    internal uint? OpenCodeTuiProcessId => _usesNativeOpenCodeTui ? _session.ProcessId : null;
    internal uint? OpenCodeServerProcessId => _usesNativeOpenCodeStatus ? _headlessOpenCodeHost?.ProcessId : null;
    internal string? OpenCodeTuiSessionId => _usesNativeOpenCodeTui ? _headlessSessionId : null;
    internal string LastLifecycleStage => _lastLifecycleStage;
    public bool IsRunning => _usesNativeOpenCodeStatus ? _headlessOpenCodeHost?.IsRunning == true : _session.IsRunning;
    internal uint? SessionProcessId => _usesNativeOpenCodeStatus ? _headlessOpenCodeHost?.ProcessId : _session.ProcessId;
    public bool IsReady => _ready && !_disposed && !_failed && IsRunning && (!_usesNativeOpenCodeTui || _session.IsRunning);
    public TerminalActivity Activity
    {
        get
        {
            TerminalActivity tracked = _activityTracker.Activity;
            if (_usesNativeOpenCodeStatus && tracked is not (TerminalActivity.Error or TerminalActivity.Exited))
            {
                if (_failed || (_ready && !IsRunning)) return TerminalActivity.Exited;
                if (_manualInputOverride) return TerminalActivity.NeedsInput;
                if (_autoResume?.IsStalled == true && _openCodeSnapshot?.Kind != OpenCodeStatusKind.NeedsInput) return TerminalActivity.Quiet;
                bool recentActivity = _openCodeStatus?.HasRecentActivity == true;
                if (!recentActivity && _nativeAutomationSupervisionNotice is not null && _openCodeSnapshot?.Kind is not (OpenCodeStatusKind.NeedsInput or OpenCodeStatusKind.Pending))
                    return TerminalActivity.Quiet;
                return _openCodeSnapshot?.Kind switch
                {
                    OpenCodeStatusKind.Busy => recentActivity ? TerminalActivity.Active : TerminalActivity.Quiet,
                    OpenCodeStatusKind.NeedsInput => TerminalActivity.NeedsInput,
                    OpenCodeStatusKind.Idle => TerminalActivity.Idle,
                    OpenCodeStatusKind.Pending => TerminalActivity.Starting,
                    _ => TerminalActivity.Quiet
                };
            }
            if (tracked is TerminalActivity.Error or TerminalActivity.RateLimited or TerminalActivity.NeedsInput or TerminalActivity.Exited) return tracked;
            if (_workFailed) return TerminalActivity.Error;
            return _automationBusy ? TerminalActivity.Active : tracked;
        }
    }
    public string ActivityLabel
    {
        get
        {
            if (_externalContinuationNotice is not null) return _externalContinuationNotice;
            if (WasContinuedExternally) return "CMD에서 이어가기";
            TerminalActivity tracked = _activityTracker.Activity;
            if (_usesNativeOpenCodeStatus && tracked is not (TerminalActivity.Error or TerminalActivity.Exited))
            {
                if (_failed || (_ready && !IsRunning)) return "터미널 종료";
                if (_manualInputOverride) return "확인 필요 (수동)";
                if (_autoResume?.IsStalled == true && _openCodeSnapshot?.Kind != OpenCodeStatusKind.NeedsInput) return _autoResume.Status;
                bool recentActivity = _openCodeStatus?.HasRecentActivity == true;
                if (!recentActivity && _nativeAutomationSupervisionNotice is not null && _openCodeSnapshot?.Kind is not (OpenCodeStatusKind.NeedsInput or OpenCodeStatusKind.Pending))
                    return _nativeAutomationSupervisionNotice;
                string nativeLabel = _openCodeSnapshot?.Kind switch
                {
                    OpenCodeStatusKind.Busy => recentActivity ? "진행 중" : "응답 대기",
                    OpenCodeStatusKind.NeedsInput => "승인·선택 대기",
                    OpenCodeStatusKind.Idle => HasCompletedWork ? "응답 완료" : "대기 중",
                    OpenCodeStatusKind.Pending => "요청 전달 중",
                    _ => "OpenCode 상태 확인 불가"
                };
                if (_workFailed && _openCodeSnapshot?.Kind == OpenCodeStatusKind.Idle)
                    return "작업 실패";
                return string.IsNullOrEmpty(_autoResumeStatus) ? nativeLabel : nativeLabel + " · " + _autoResumeStatus;
            }
            if (tracked is TerminalActivity.Error or TerminalActivity.RateLimited or TerminalActivity.NeedsInput or TerminalActivity.Exited)
                return _activityTracker.ActivityLabel;
            if (_workFailed) return "작업 실패";
            if (_automationBusy) return "진행 중";
            return HasCompletedWork ? "응답 완료" : _activityTracker.ActivityLabel;
        }
    }
    public bool HasCompletedWork => !_automationBusy && !_workFailed && !_manualInputOverride &&
        (_usesNativeOpenCodeStatus
            ? _openCodeSnapshot is { Kind: OpenCodeStatusKind.Idle, HasCompletedTurn: true } && IsRunning
            : _activityTracker.HasCompletedWork);
    internal bool IsUserInputBlocked { get; set; }
    internal OpenCodeSubmissionObservation? LastManualSubmissionObservation => _lastManualSubmissionObservation;
    internal long NativeSubmissionGeneration => _openCodeStatus?.SubmissionGeneration ?? 0;
    public string TokenUsageLabel => _tokenUsage.Label;
    public event Action<string>? StatusChanged;
    internal event Action<string>? LifecycleStageChanged;
    internal event Action<string, string, string>? LifecycleFailureReported;
    public event Action? ActivityChanged;
    internal event Action<OpenCodeAutoResumeDiagnostic>? AutoResumeDiagnosticEmitted;
    public event Action? ActivatedByUser;
    public event Action? Ready;
    public event Action<string>? InputSubmitted;
    internal event Action<long, long>? NativeSubmissionConfirmed;
    public event Action? TokenUsageChanged;
    public event Action<string, bool>? Shortcut;
    public event Action<int>? ProcessExited;
    public event Action<bool>? ContextMenuRequested;

    public TerminalPane(
        string workingDirectory,
        bool launchCodex = false,
        string? startupCommand = null,
        bool startupWork = false,
        bool protectTuiViewport = false,
        string? planDocumentPath = null,
        string? projectName = null,
        string? paneKey = null,
        bool useProjectContext = true)
    {
        _headlessLifetimeToken = _headlessLifetimeCancellation.Token;
        WorkingDirectory = workingDirectory;
        LaunchesCodex = launchCodex;
        StartupCommand = startupCommand;
        _startupWork = startupWork;
        _protectTuiViewport = protectTuiViewport;
        _effectiveStartupCommand = startupCommand;
        _usesNativeOpenCodeStatus = protectTuiViewport && (startupCommand is "opencode" or "opencode --auto");
        _usesNativeOpenCodeTui = _usesNativeOpenCodeStatus &&
            !string.Equals(Environment.GetEnvironmentVariable("TERMINALNOTES_OPENCODE_RENDERER"), "headless", StringComparison.OrdinalIgnoreCase);
        _useNativeTerminalSurface = _usesNativeOpenCodeTui || !_usesNativeOpenCodeStatus &&
            !string.Equals(Environment.GetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER"), "webview", StringComparison.OrdinalIgnoreCase);
        if (_usesNativeOpenCodeStatus)
        {
            if (useProjectContext)
            {
                _openCodeProjectContext = new OpenCodeProjectContext(workingDirectory, planDocumentPath, projectName);
                try
                {
                    _openCodeConfigContent = _openCodeProjectContext.CreateProcessConfigContent(
                        Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT"));
                }
                catch
                {
                    _openCodeProjectContext.Dispose();
                    throw;
                }

            }

            try
            {
                using var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
                _effectiveStartupCommand = $"opencode --hostname 127.0.0.1 --port {port}" +
                    (startupCommand == "opencode --auto" ? " --auto" : "");
                Uri nativeEndpoint = new($"http://127.0.0.1:{port}/");
                NativeOpenCodeEndpoint = nativeEndpoint;
                _openCodeStatus = new OpenCodeStatusMonitor(nativeEndpoint, workingDirectory);
                InitializeHeadlessOpenCode(nativeEndpoint, paneKey);
                if (useProjectContext) InitializeAutoResume(nativeEndpoint, planDocumentPath);
                _openCodeSnapshot = _openCodeStatus.Snapshot;
                _openCodeStatus.StatusChanged += snapshot =>
                {
                    if (_disposed) return;
                    void Update()
                    {
                        if (_disposed) return;
                        // Baseline and SSE callbacks can reach the dispatcher out of order.
                        _openCodeSnapshot = _openCodeStatus.Snapshot;
                        ApplyActivityChange();
                        ScheduleNativeStateRefresh();
                    }
                    if (Dispatcher.CheckAccess()) Update();
                    else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Update);
                };
                _openCodeStatus.ActivityChanged += () =>
                {
                    if (_disposed) return;
                    void Update()
                    {
                        if (_disposed) return;
                        _lastNativeRecentActivity = _openCodeStatus.HasRecentActivity;
                        ApplyActivityChange();
                        ScheduleNativeStateRefresh();
                    }
                    if (Dispatcher.CheckAccess()) Update();
                    else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Update);
                };
                _openCodeStatus.SubmissionConfirmed += observation =>
                {
                    if (_disposed || Dispatcher.HasShutdownStarted) return;
                    void Raise()
                    {
                        if (!_disposed && observation.Generation == _openCodeStatus.SubmissionGeneration)
                            NativeSubmissionConfirmed?.Invoke(observation.ConfirmationVersion, observation.Generation);
                    }
                    if (Dispatcher.CheckAccess()) Raise();
                    else Dispatcher.BeginInvoke((Action)Raise);
                };
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
            {
                // A missing status connection is explicitly unknown, never inferred from prose.
                System.Diagnostics.Trace.WriteLine("OpenCode status connection could not be initialized: " + ex.GetType().Name);
            }
        }
        _browser.AllowExternalDrop = false;
        if (_useNativeTerminalSurface)
        {
            _nativeTerminalEmulator = new TerminalEmulator(_usesNativeOpenCodeTui ? 40 : 80, _usesNativeOpenCodeTui ? 24 : 24, 5000);
            _nativeTerminalSurface = new NativeTerminalSurface(_nativeTerminalEmulator);
            _nativeTerminalEmulator.Response += response =>
            {
                if (_disposed || !_ready || !IsRunning) return;
                try { _session.Write(response); }
                catch (Exception exception) { StatusChanged?.Invoke("터미널 응답: " + exception.Message); }
            };
            _nativeTerminalSurface.Input += HandleNativeSurfaceInput;
            _nativeTerminalSurface.InputFailed += exception => StatusChanged?.Invoke("터미널 입력: " + exception.Message);
            _nativeTerminalSurface.DimensionsChanged += HandleNativeSurfaceDimensionsChanged;
            _nativeTerminalSurface.Activated += () => ActivatedByUser?.Invoke();
            _nativeTerminalSurface.PasteRequested += PasteFromClipboard;
            _nativeTerminalSurface.CopyRequested += CopySelection;
            _nativeTerminalSurface.ContextMenuRequested += hasSelection => ContextMenuRequested?.Invoke(hasSelection);
            _nativeTerminalSurface.Shortcut += (key, shift) => Shortcut?.Invoke(key, shift);
            _nativeTerminalSurface.TextSendCompleted += HandleTerminalSendTextAck;
            if (_usesNativeOpenCodeTui)
                Children.Add(NativeTerminalSurface.CreateScrollableViewport(_nativeTerminalSurface));
            else
                Children.Add(_nativeTerminalSurface);
            _browser.Visibility = Visibility.Collapsed;
        }
        else Children.Add(_browser);
        Children.Add(_notice);
        ApplyActivityVisuals();
        // Native console output must not wait behind an indefinitely non-idle window message queue.
        // Each tick posts one output batch, with acknowledgement backpressure.
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(32), DispatcherPriority.Loaded, (_, _) => FlushOutput(), Dispatcher);
        _activityTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            if (!_usesNativeOpenCodeStatus) _activityTracker.Tick();
            else
            {
                bool recent = _openCodeStatus?.HasRecentActivity == true;
                if (recent != _lastNativeRecentActivity)
                {
                    _lastNativeRecentActivity = recent;
                    ApplyActivityChange();
                }
            }
        }, Dispatcher);
        _activityTracker.ActivityChanged += OnActivityTrackerChanged;
        _inputCapture.Submitted += text =>
        {
            if (text.TrimStart().StartsWith('/')) _autoResume?.Suspend("세션 명령 입력 · 자동 진행 대기");
            if (!string.IsNullOrWhiteSpace(text)) ClearWorkFailed();
            _manualInputOverride = false;
            _lastManualSubmissionObservation = null;
            if (!string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith('/'))
                _lastManualSubmissionObservation = _openCodeStatus?.ObserveSubmission();
            if (_usesNativeOpenCodeTui)
                ObserveOpenCodeTuiManualSubmission(text);
            InputSubmitted?.Invoke(text);
        };
        _tokenUsage.Changed += OnTokenUsageChanged;
        _activityTimer.Start();
        _session.LifecycleChanged += OnSessionLifecycleChanged;
        _session.InputFailed += OnInputFailed;
        _session.Exited += _ => Dispatcher.BeginInvoke(() =>
        {
            ReportLifecycleStage("session_exited");
            if (_usesNativeOpenCodeTui)
                HandleOpenCodeTuiExit();
            else if (!_usesNativeOpenCodeStatus)
                _activityTracker.MarkExited();
            if (!_usesNativeOpenCodeTui) _openCodeStatus?.Dispose();
            FailPendingTextSends(new InvalidOperationException("The terminal process exited."));
        });
        _session.OutputReceived += text =>
        {
            _tokenUsage.Observe(text);
            if (_useNativeTerminalSurface)
            {
                if (!_disposed && !_failed) _nativeTerminalSurface?.Feed(text);
                return;
            }
            lock (_outputLock)
            {
                if (_disposed || _failed) return;
                if (_queuedCharacters > 8_000_000)
                {
                    _pending.Clear(); _queuedCharacters = 0;
                    _pending.Enqueue("\x1b[0m\r\n[화면 출력이 너무 많아 일부 출력을 생략했습니다]\r\n");
                }
                _pending.Enqueue(text); _queuedCharacters += text.Length;
            }
        };
        _session.Exited += code => Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            StatusChanged?.Invoke(_usesNativeOpenCodeTui
                ? $"OpenCode TUI 종료 · 코드 {code} (서버는 유지 중)"
                : $"터미널 종료 · 코드 {code}");
            ProcessExited?.Invoke(code);
        });
        Loaded += (_, _) =>
        {
            _loadedCompletion.TrySetResult(null);
            StartWebViewInitialization();
            TryStartTerminalWhenLaidOut();
        };
        SizeChanged += (_, _) => TryStartTerminalWhenLaidOut();
        IsVisibleChanged += (_, _) => TryStartTerminalWhenLaidOut();
    }

    /// <summary>
    /// Waits until this pane has been loaded, laid out, and its ConPTY process
    /// has started. This is the launch handoff for workflows that create a pane
    /// in a lazy tab. Native OpenCode status readiness is checked separately.
    /// </summary>
    internal async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (Dispatcher.CheckAccess() && IsLoaded)
        {
            _loadedCompletion.TrySetResult(null);
            StartWebViewInitialization();
            TryStartTerminalWhenLaidOut();
        }

        Exception? loadFailure = await _loadedCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (loadFailure is not null)
            ExceptionDispatchInfo.Capture(loadFailure).Throw();

        Exception? startupFailure = await _terminalStartedCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (startupFailure is not null)
            ExceptionDispatchInfo.Capture(startupFailure).Throw();
    }

    private void StartWebViewInitialization()
    {
        if (_disposed || _failed || _initializeTask is not null) return;
        if (_useNativeTerminalSurface)
        {
            _browser.Visibility = Visibility.Collapsed;
            _nativeTerminalSurface!.Visibility = Visibility.Visible;
            _initializeTask = Task.CompletedTask;
            return;
        }
        _initializeTask = InitializeAsync();
    }

    private void TryStartTerminalWhenLaidOut()
    {
        if (_disposed || _failed || _ready ||
            (_usesNativeOpenCodeStatus && !_usesNativeOpenCodeTui && !_nativeViewReady) ||
            (_usesNativeOpenCodeTui || !_usesNativeOpenCodeStatus) && _pendingWebReadyDimensions is null ||
            !IsLoaded || (!IsVisible && !_allowBackgroundAutomationStartup) || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        if (_usesNativeOpenCodeStatus)
        {
            if (_usesNativeOpenCodeTui)
                _openCodeTuiInitialDimensions = _pendingWebReadyDimensions;
            _ = StartHeadlessOpenCodeAsync();
            _pendingWebReadyDimensions = null;
            return;
        }

        var dimensions = _pendingWebReadyDimensions!.Value;

        try
        {
            _activityTracker.MarkStarted(_startupWork);
            ReportLifecycleStage("session_start_requested");
            _session.Start(
                WorkingDirectory,
                dimensions.Columns,
                dimensions.Rows,
                LaunchesCodex,
                _effectiveStartupCommand,
                _openCodeConfigContent,
                // Native readiness must follow OpenCode's lifetime. The view stays
                // visible after exit, but an idle PowerShell host must not look live.
                exitOnStartupCommandCompletion: _usesNativeOpenCodeStatus);
            _openCodeStatus?.Start();
            _pendingWebReadyDimensions = null;
            _ready = true;
            _notice.Visibility = Visibility.Collapsed;
            ApplyActivityVisuals();
            _terminalStartedCompletion.TrySetResult(null);
            ReportLifecycleStage("session_started");
            Ready?.Invoke();
            StatusChanged?.Invoke("터미널 준비 완료 · " + WorkingDirectory);
        }
        catch (Exception exception)
        {
            _pendingWebReadyDimensions = null;
            _terminalStartedCompletion.TrySetResult(exception);
            ShowError("터미널 시작 실패: " + exception.Message);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(OpenCodeProjectContext.DataDirectory, "WebView2"));
            if (_disposed) return;
            await _browser.EnsureCoreWebView2Async(env);
            if (_disposed) return;
            var core = _browser.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("terminalnotes.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) ShowError("터미널 화면을 열 수 없습니다: " + e.WebErrorStatus); };
            core.ProcessFailed += (_, _) =>
            {
                if (_disposed) return;
                _failed = true;
                _flushTimer.Stop();
                FailPendingTextSends(new InvalidOperationException("The terminal view process failed."));
                BeginSessionClose("webview_failed");
                ShowError("터미널 화면 오류로 이 세션을 종료했습니다. 새 터미널을 열어주세요.");
                ProcessExited?.Invoke(-1);
            };
            core.Navigate(Origin + (_usesNativeOpenCodeStatus ? "opencode.html" : "terminal.html" + (_protectTuiViewport ? "?tui=true" : "")));
        }
        catch (Exception ex) { if (!_disposed) ShowError("터미널 시작 실패. Microsoft Edge WebView2 Runtime 설치를 확인하세요.\n" + ex.Message); }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || _failed || !e.Source.StartsWith(Origin, StringComparison.Ordinal)) return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (_usesNativeOpenCodeStatus)
            {
                HandleNativeWebMessage(root);
                return;
            }
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    if (_ready || _pendingWebReadyDimensions is not null) return;
                    _pendingWebReadyDimensions = (Size(root, "cols"), Size(root, "rows"));
                    TryStartTerminalWhenLaidOut();
                    break;
                case "input":
                    if (_ready)
                    {
                        string input = root.GetProperty("data").GetString() ?? "";
                        bool isProgrammatic = root.TryGetProperty("programmatic", out var programmatic) && programmatic.ValueKind == JsonValueKind.True;
                        long inputSendId = 0;
                        bool hasSendId = root.TryGetProperty("sendId", out var sendElement) && sendElement.TryGetInt64(out inputSendId);
                        Exception? inputError = HandleTerminalInput(input, isProgrammatic, hasSendId ? inputSendId : null);
                        if (inputError is not null) throw inputError;
                    }
                    break;
                case "sendTextAck":
                    long ackSendId = root.GetProperty("id").GetInt64();
                    Exception? ackError = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                        ? new InvalidOperationException(error.GetString()) : null;
                    HandleTerminalSendTextAck(ackSendId, ackError);
                    break;
                case "resize":
                    short columns = Size(root, "cols");
                    short rows = Size(root, "rows");
                    if (_ready)
                    {
                        ReportLifecycleStage("resize_requested");
                        _session.RequestResize(columns, rows);
                    }
                    else if (_pendingWebReadyDimensions is not null)
                    {
                        _pendingWebReadyDimensions = (columns, rows);
                        TryStartTerminalWhenLaidOut();
                    }
                    break;
                case "drained": _writeInFlight = false; break;
                case "activityTail":
                    if (_usesNativeOpenCodeStatus) break;
                    _activityTracker.ObserveRenderedScreen(
                        root.GetProperty("data").GetString() ?? "",
                        root.GetProperty("screen").GetString() ?? "",
                        root.GetProperty("layout").GetString() ?? "",
                        root.TryGetProperty("promptAtCursor", out var promptAtCursor) && promptAtCursor.ValueKind == JsonValueKind.True);
                    break;
                case "activated": ActivatedByUser?.Invoke(); break;
                case "contextMenu": ContextMenuRequested?.Invoke(root.TryGetProperty("hasSelection", out var selection) && selection.ValueKind == JsonValueKind.True); break;
                case "copy": CopyText(root.GetProperty("data").GetString()); break;
                case "paste": PasteFromClipboard(); break;
                case "shortcut": Shortcut?.Invoke(root.GetProperty("key").GetString() ?? "", root.GetProperty("shift").GetBoolean()); break;
            }
        }
        catch (Exception ex) { StatusChanged?.Invoke("터미널: " + ex.Message); if (!_ready) ShowError(ex.Message); }
    }
    private static short Size(JsonElement root, string name) => (short)Math.Clamp(root.GetProperty(name).GetInt32(), 2, 500);
    private void ShowError(string text)
    {
        _openCodeStatus?.Dispose();
        _activityTracker.MarkError();
        var error = new InvalidOperationException(text);
        _loadedCompletion.TrySetResult(error);
        _terminalStartedCompletion.TrySetResult(error);
        _notice.Text = text;
        _notice.Visibility = Visibility.Visible;
        _browser.Visibility = Visibility.Collapsed;
        if (_nativeTerminalSurface is not null) _nativeTerminalSurface.Visibility = Visibility.Collapsed;
        StatusChanged?.Invoke(text);
    }
    private void FlushOutput()
    {
        if (!_ready || _disposed || _failed || _writeInFlight) return;
        var chunk = new StringBuilder(_retryOutput);
        lock (_outputLock)
        {
            while (_retryOutput is null && _pending.Count > 0 && chunk.Length < 65536) { var text = _pending.Dequeue(); _queuedCharacters -= text.Length; chunk.Append(text); }
        }
        if (chunk.Length == 0) return;
        _writeInFlight = true;
        var output = chunk.ToString();
        if (Post(new { type = "output", data = output })) { _retryOutput = null; _outputPostFailures = 0; }
        else
        {
            _writeInFlight = false;
            _retryOutput = output;
            if (++_outputPostFailures >= 3)
            {
                _failed = true; _flushTimer.Stop();
                BeginSessionClose("webview_post_failed");
                ShowError("터미널 화면과 연결이 끊겨 이 세션을 종료했습니다. 새 터미널을 열어주세요.");
            }
        }
    }
    private bool Post(object message)
    {
        if (_disposed || _browser.CoreWebView2 is null) return false;
        try { _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message)); return true; }
        catch (Exception ex) { StatusChanged?.Invoke("터미널 화면: " + ex.Message); return false; }
    }
    private void OnActivityTrackerChanged()
    {
        if (_disposed) return;
        if (Dispatcher.CheckAccess()) ApplyActivityChange();
        else Dispatcher.BeginInvoke(() => { if (!_disposed) ApplyActivityChange(); });
    }
    private void OnSessionLifecycleChanged(ConPtyLifecycleEvent lifecycleEvent)
    {
        string stage = lifecycleEvent.Operation + "_" + lifecycleEvent.Phase;
        if (lifecycleEvent.ExceptionType is not null)
            stage += "_" + lifecycleEvent.ExceptionType;
        ReportLifecycleStage(stage);
        if (lifecycleEvent.Phase == "faulted")
        {
            string exceptionType = lifecycleEvent.ExceptionType ?? "UnknownException";
            try { LifecycleFailureReported?.Invoke(lifecycleEvent.Operation, exceptionType, stage); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Terminal lifecycle fault observer failed: " + ex.GetType().Name); }
        }
    }

    private void ReportLifecycleStage(string stage)
    {
        _lastLifecycleStage = stage;
        try { LifecycleStageChanged?.Invoke(stage); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Terminal lifecycle observer failed: " + ex.GetType().Name); }
    }

    private void BeginSessionClose(string reason)
    {
        ReportLifecycleStage(reason);
        if (Interlocked.Exchange(ref _sessionCloseStarted, 1) != 0) return;
        if (_usesNativeOpenCodeStatus)
        {
            try { _headlessLifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
            try { _headlessManualDispatchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            _ = ObserveOpenCodeCloseAsync();
            return;
        }
        try { _ = ObserveSessionCloseAsync(_session.CloseAsync()); }
        catch (Exception ex)
        {
            ReportLifecycleStage("close_request_failed_" + ex.GetType().Name);
            System.Diagnostics.Trace.WriteLine("Terminal cleanup request failed: " + ex.GetType().Name);
        }
    }

    private async Task ObserveOpenCodeCloseAsync()
    {
        try
        {
            await _session.CloseAsync().ConfigureAwait(false);
            _headlessOpenCodeHost?.Dispose();
        }
        catch (Exception ex)
        {
            _headlessOpenCodeHost?.Dispose();
            ReportLifecycleStage("opencode_close_failed_" + ex.GetType().Name);
            System.Diagnostics.Trace.WriteLine("OpenCode cleanup failed: " + ex.GetType().Name);
        }
    }

    private async Task ObserveSessionCloseAsync(Task closeTask)
    {
        try { await closeTask.ConfigureAwait(false); }
        catch (Exception ex)
        {
            ReportLifecycleStage("close_failed_" + ex.GetType().Name);
            System.Diagnostics.Trace.WriteLine("Terminal cleanup failed: " + ex.GetType().Name);
        }
    }

    private void OnTokenUsageChanged()
    {
        if (_disposed) return;
        if (Dispatcher.CheckAccess()) TokenUsageChanged?.Invoke();
        else Dispatcher.BeginInvoke(() => { if (!_disposed) TokenUsageChanged?.Invoke(); });
    }
    private void FailPendingTextSends(Exception error)
    {
        foreach (SavedPlanDispatch dispatch in _savedPlanDispatches.Values.ToArray())
            CancelSavedPlanDispatch(dispatch, error);
        foreach (var completion in _pendingTextSends.Values) completion.TrySetException(error);
        _pendingTextSends.Clear();
    }
    private void OnInputFailed(Exception error)
    {
        void Handle()
        {
            if (_disposed || _failed) return;
            _failed = true;
            _flushTimer.Stop();
            FailPendingTextSends(new IOException("Terminal input could not be written.", error));
            BeginSessionClose("input_failed");
            ShowError("터미널 입력 연결에 실패해 세션을 종료했습니다. 새 터미널을 열어주세요.\n" + error.Message);
        }

        if (Dispatcher.CheckAccess()) Handle();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke((Action)Handle);
    }

    private void ApplyActivityChange()
    {
        ApplyActivityVisuals();
        ActivityChanged?.Invoke();
    }
    private void ApplyActivityVisuals()
    {
        string color = TerminalActivityPalette.Background(Activity, HasCompletedWork);
        string foreground = TerminalActivityPalette.Foreground(Activity, HasCompletedWork);
        var mediaColor = System.Windows.Media.Color.FromRgb(
            Convert.ToByte(color[1..3], 16),
            Convert.ToByte(color[3..5], 16),
            Convert.ToByte(color[5..7], 16));
        Background = new SolidColorBrush(mediaColor);
        _browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            mediaColor.R,
            mediaColor.G,
            mediaColor.B);
        if (_ready)
        {
            bool useDarkAnsi = string.Equals(color, "#FF8A00", StringComparison.OrdinalIgnoreCase);
            Post(new
            {
                type = "theme",
                background = color,
                foreground,
                cursor = foreground,
                selectionBackground = SelectionBackground(color),
                selectionForeground = foreground,
                useDarkAnsi,
                forceStateBackground = _protectTuiViewport
            });
            _nativeTerminalSurface?.SetTheme(color, foreground, SelectionBackground(color), _protectTuiViewport);
        }
    }
    private static string SelectionBackground(string background) => background.ToUpperInvariant() switch
    {
        "#FF8A00" => "#FFC47A",
        "#00853F" => "#00572D",
        _ => "#43536B"
    };
    public void FocusTerminal()
    {
        if (!_ready || _disposed || _failed) return;
        if (_useNativeTerminalSurface)
        {
            _nativeTerminalSurface?.FocusSurface();
            return;
        }
        _browser.Focus();
        Post(new { type = "focus" });
    }
    public bool CanPasteFromClipboard
    {
        get
        {
            if (IsUserInputBlocked) return false;
            try { return Clipboard.ContainsText(); }
            catch { return false; }
        }
    }
    public void CopySelection()
    {
        if (_useNativeTerminalSurface) { CopyText(_nativeTerminalSurface?.SelectedText); return; }
        PostTerminalCommand("copySelection");
    }
    public void SelectAll()
    {
        if (_useNativeTerminalSurface) { _nativeTerminalSurface?.SelectAllText(); return; }
        PostTerminalCommand("selectAll");
    }
    public void ClearSelection()
    {
        if (_useNativeTerminalSurface) { _nativeTerminalSurface?.ClearSelectionText(); return; }
        PostTerminalCommand("clearSelection");
    }
    public void ClearScreen()
    {
        if (_useNativeTerminalSurface) { _nativeTerminalSurface?.ClearTerminal(); return; }
        PostTerminalCommand("clear");
    }
    public void PasteFromClipboard()
    {
        try
        {
            if (IsUserInputBlocked) return;
            if (!Clipboard.ContainsText()) return;
            string paste = Clipboard.GetText();
            if (paste.Length > 200_000)
            {
                StatusChanged?.Invoke("붙여넣기는 200,000자 이하로 나눠주세요.");
                return;
            }
            if (_usesNativeOpenCodeStatus && !_usesNativeOpenCodeTui)
            {
                PostNative(new { type = "paste", data = paste });
                return;
            }
            if ((paste.Contains('\n') || paste.Contains('\r')) &&
                MessageBox.Show(Window.GetWindow(this), "여러 줄을 터미널에 붙여넣으면 명령이 실행될 수 있습니다. 계속할까요?", "여러 줄 붙여넣기", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (_useNativeTerminalSurface)
            {
                _nativeTerminalSurface?.PasteText(paste);
                return;
            }
            Post(new { type = "paste", data = paste });
        }
        catch (Exception ex) { StatusChanged?.Invoke("클립보드: " + ex.Message); }
    }
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { StatusChanged?.Invoke("클립보드: " + ex.Message); }
    }
    private void PostTerminalCommand(string type)
    {
        if (!Post(new { type })) StatusChanged?.Invoke("터미널 화면이 아직 준비되지 않았습니다.");
        else FocusTerminal();
    }
    public Task SendTextAsync(string text, bool submit = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Dispatcher.CheckAccess()) return Dispatcher.InvokeAsync(() => SendTextAsync(text, submit)).Task.Unwrap();
        if (!IsReady) throw new InvalidOperationException("The terminal is not ready.");

        if (_usesNativeOpenCodeStatus && !_usesNativeOpenCodeTui)
        {
            if (submit) return SendNativeUiPromptAsync(Guid.NewGuid().ToString("N"), text, null, null);
            if (!PostNative(new { type = "nativeSetInput", text }))
                throw new InvalidOperationException("Could not send text to the OpenCode composer.");
            return Task.CompletedTask;
        }

        if (_useNativeTerminalSurface)
        {
            _inputCapture.PrepareForProgrammaticSubmission();
            long nativeId = ++_nextTextSendId;
            SavedPlanDispatch? nativeExpectedDispatch = _savedPlanDispatchForNextSend;
            SavedPlanDispatch? nativeAttachedDispatch = AttachSavedPlanDispatch(nativeId, text, submit);
            if (nativeExpectedDispatch is not null && !ReferenceEquals(nativeExpectedDispatch, nativeAttachedDispatch))
                throw new InvalidOperationException("Could not safely track the saved plan submission.");
            var nativeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingTextSends.Add(nativeId, nativeCompletion);
            ClearWorkFailed();
            _nativeTerminalSurface!.SendText(text, submit, nativeId);
            return AwaitTextSendAsync(nativeId, nativeCompletion.Task);
        }

        _inputCapture.PrepareForProgrammaticSubmission();
        long id = ++_nextTextSendId;
        SavedPlanDispatch? expectedDispatch = _savedPlanDispatchForNextSend;
        SavedPlanDispatch? attachedDispatch = AttachSavedPlanDispatch(id, text, submit);
        if (expectedDispatch is not null && !ReferenceEquals(expectedDispatch, attachedDispatch))
            throw new InvalidOperationException("Could not safely track the saved plan submission.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingTextSends.Add(id, completion);
        if (!Post(new { type = "sendText", id, data = text, submit }))
        {
            _pendingTextSends.Remove(id);
            if (_savedPlanDispatches.TryGetValue(id, out SavedPlanDispatch? dispatch))
                CancelSavedPlanDispatch(dispatch, new InvalidOperationException("Could not send text to the terminal view."));
            CompleteSavedPlanDispatch(id);
            throw new InvalidOperationException("Could not send text to the terminal view.");
        }
        ClearWorkFailed();
        return AwaitTextSendAsync(id, completion.Task);
    }
    private async Task AwaitTextSendAsync(long id, Task acknowledgement)
    {
        try { await acknowledgement.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException timeout)
        {
            if (Dispatcher.CheckAccess()) _pendingTextSends.Remove(id);
            else await Dispatcher.InvokeAsync(() => _pendingTextSends.Remove(id));
            throw new TimeoutException("Terminal input acknowledgement timed out; the text may have been submitted and was not sent again.", timeout);
        }
    }
    public void ObserveTokenUsage(string text) => _tokenUsage.Observe(text);
    public void MarkWorkCompleted()
    {
        _openCodeStatus?.ConfirmOwnedTurnCompleted();
        bool changed = _workFailed;
        _workFailed = false;
        _activityTracker.MarkWorkCompleted();
        if (changed) ApplyActivityChange();
    }
    internal void MarkWorkFailed()
    {
        if (_disposed || _workFailed) return;
        _workFailed = true;
        ApplyActivityChange();
    }
    internal void SetAutomationBusy(bool busy)
    {
        if (_disposed) return;
        if (!busy) SetNativeAutomationSupervisionNotice(null);
        bool changed = _automationBusy != busy || _nativeAutomationActive != busy;
        _nativeAutomationActive = busy;
        if (busy && _workFailed)
        {
            _workFailed = false;
            changed = true;
        }
        if (!changed) return;
        _automationBusy = busy;
        ApplyActivityChange();
    }
    private void ClearWorkFailed()
    {
        if (!_workFailed) return;
        _workFailed = false;
        ApplyActivityChange();
    }
    public void MarkNeedsInput() { _manualInputOverride = true; _activityTracker.MarkNeedsInput(); ApplyActivityChange(); }
    public void ClearActivityOverride() { _manualInputOverride = false; _activityTracker.ClearActivityOverride(); ApplyActivityChange(); }
    public void Interrupt()
    {
        CancelNativeAutomation();
        if (_usesNativeOpenCodeStatus)
        {
            try { _headlessManualDispatchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }
        _autoResume?.Suspend("사용자 중단 · 자동 진행 멈춤");
        CancelIncompleteSavedPlanDispatches();
        _inputCapture.Observe("\x03");
        ClearSavedPlanComposerDraftAfterInterrupt();
        if (_usesNativeOpenCodeTui) _openCodeTuiManualText = null;
        if (_usesNativeOpenCodeTui)
        {
            // The app stop action targets its verified task session. Keyboard
            // shortcuts inside OpenCode continue to be handled by the TUI.
            _ = RunNativeUiOperationAsync(AbortNativeSessionAsync);
            FocusTerminal();
            return;
        }
        if (_usesNativeOpenCodeStatus && !_usesNativeOpenCodeTui)
        {
            _ = RunNativeUiOperationAsync(AbortNativeSessionAsync);
            return;
        }
        if (IsRunning) _session.Write("\x03");
        FocusTerminal();
    }

    /// <summary>
    /// Updates the linked Markdown path exposed to this native OpenCode terminal.
    /// OpenCode rereads the app-owned instruction file before each model request.
    /// </summary>
    public void UpdatePlanDocumentContext(string? path, string? projectName = null)
    {
        // Stopped role tabs may remain visible until their replacement is created.
        // Project-wide name/plan updates must not reopen their disposed context.
        if (_disposed) return;
        _openCodeProjectContext?.UpdatePlanDocumentContext(path, projectName);
        BindSavedPlanExecutionPath(path);
        _autoResume?.UpdatePlan(path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        var disposedError = new ObjectDisposedException(nameof(TerminalPane));
        _loadedCompletion.TrySetResult(disposedError);
        _terminalStartedCompletion.TrySetResult(disposedError);
        _automationBusy = false;
        _workFailed = false;
        CancelNativeAutomation();
        IsUserInputBlocked = true;
        _disposed = true; _flushTimer.Stop(); _activityTimer.Stop(); _nativeRefreshTimer?.Stop();
        BeginSessionClose("pane_disposed");
        _autoResume?.Dispose();
        _openCodeStatus?.Dispose();
        foreach (var completion in _pendingTextSends.Values) completion.TrySetCanceled();
        _pendingTextSends.Clear();
        _activityTracker.MarkExited();
        _openCodeProjectContext?.Dispose();
        _headlessOpenCodeClient?.Dispose();
        _nativeTerminalSurface?.DisposeSurface();
        _browser.Dispose();
    }
}
