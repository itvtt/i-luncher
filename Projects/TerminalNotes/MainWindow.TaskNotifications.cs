using System.IO;
using System.Windows.Threading;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private sealed class TaskNotificationBinding
    {
        public string AttemptId { get; } = Guid.NewGuid().ToString("N");
        public required long IntentVersion { get; init; }
        public bool WasStoppedAtStart { get; init; }
        public TerminalPane? Pane { get; set; }
        public long ConfirmedGeneration { get; set; }
        public bool Accepted { get; set; }
        public bool ManualWork { get; set; }
    }

    private readonly Dictionary<string, TaskNotificationBinding> _taskNotificationBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskNotificationTracker _taskNotificationTracker = new();
    private readonly Dictionary<string, OpenCodeAutoResumeDiagnostic> _autoResumeDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _autoResumeWarningsShown = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TerminalPane, Action<OpenCodeAutoResumeDiagnostic>> _autoResumeDiagnosticHandlers = [];
    private readonly DispatcherTimer _taskNotificationTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _observingTaskNotifications;
    private Task? _autoResumeDiagnosticRestoreTask;
    internal Action<TaskNotificationNotice>? TaskNotificationSinkOverride { get; set; }

    private void StartTaskNotificationMonitoring()
    {
        _taskNotificationTimer.Tick += (_, _) => ObserveTaskNotifications();
        _taskNotificationTimer.Start();
    }

    private void AttachAutoResumeDiagnostics(string project, TerminalPane pane)
    {
        if (_autoResumeDiagnosticHandlers.ContainsKey(pane)) return;
        project = CanonicalProjectPath(project);
        Action<OpenCodeAutoResumeDiagnostic> handler = diagnostic =>
        {
            void Apply()
            {
                if (_closing || _resourcesDisposed) return;
                string key = CanonicalProjectPath(project);
                _autoResumeDiagnostics[key] = diagnostic;
                string eventCode = new(diagnostic.Code.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
                RecordAutomationDiagnostic("auto_resume_" + eventCode, diagnostic.Reason,
                    severity: diagnostic.RequiresUserAction ? "warning" : "info",
                    deduplicate: true, projectDirectory: key,
                    lastSubstantiveProgressAt: diagnostic.LastSubstantiveProgressAt,
                    requiresUserAction: diagnostic.RequiresUserAction,
                    sessionId: diagnostic.SessionId, messageId: diagnostic.MessageId,
                    exceptionType: diagnostic.ExceptionType, errorCode: diagnostic.ErrorCode,
                    phase: diagnostic.Phase, timestamp: diagnostic.Timestamp);
                if (diagnostic.RequiresUserAction) ShowAutoResumeActionNotification(key, diagnostic);
                else _autoResumeWarningsShown.Remove(key);
                UpdateProjectTaskActivity();
            }

            if (Dispatcher.CheckAccess()) Apply();
            else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke((Action)(() =>
            {
                try { Apply(); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Auto-resume diagnostic UI update failed: {ex.GetType().Name}"); }
            }));
        };
        _autoResumeDiagnosticHandlers.Add(pane, handler);
        pane.AutoResumeDiagnosticEmitted += handler;
        _autoResumeDiagnosticRestoreTask ??= RestoreAutoResumeDiagnosticsAsync();
    }

    private void ShowAutoResumeActionNotification(string project, OpenCodeAutoResumeDiagnostic diagnostic)
    {
        string key = diagnostic.Code + "/" + diagnostic.Phase + "/" + diagnostic.SessionId;
        if (_tray is null || !_taskNotificationPreferences.CanNotify(DateTimeOffset.UtcNow) ||
            _autoResumeWarningsShown.TryGetValue(project, out string? shown) && shown == key) return;
        _autoResumeWarningsShown[project] = key;
        string name = ProjectDisplayName(project);
        _tray.ShowTaskNotification("Loop 확인 필요", $"{name}\n{diagnostic.Reason}\n태스크 상태에서 원인과 진단 로그를 확인하세요.", true,
            () => !_closing && !_closePending && _taskNotificationPreferences.CanNotify(DateTimeOffset.UtcNow) &&
                _autoResumeDiagnostics.TryGetValue(project, out var latest) && latest.RequiresUserAction &&
                latest.Code == diagnostic.Code && latest.Phase == diagnostic.Phase && latest.Timestamp == diagnostic.Timestamp);
    }

    private async Task RestoreAutoResumeDiagnosticsAsync()
    {
        string path = _settingsPath + ".automation-diagnostics.jsonl";
        if (!File.Exists(path)) return;
        var latestByProject = new Dictionary<string, AutomationDiagnosticEvent>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const int maxTailBytes = 1024 * 1024;
            const int maxTailLines = 3000;
            string[] lines;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true))
            {
                long start = Math.Max(0, stream.Length - maxTailBytes);
                stream.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[checked((int)(stream.Length - start))];
                int read = 0;
                while (read < buffer.Length)
                {
                    int count = await stream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
                    if (count == 0) break;
                    read += count;
                }
                string tail = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                if (start > 0)
                {
                    int firstLineBreak = tail.IndexOf('\n');
                    tail = firstLineBreak < 0 ? string.Empty : tail[(firstLineBreak + 1)..];
                }
                lines = tail.Split('\n');
                if (lines.Length > maxTailLines) lines = lines[^maxTailLines..];
            }
            for (int index = lines.Length - 1; index >= 0; index--)
            {
                try
                {
                    var entry = System.Text.Json.JsonSerializer.Deserialize<AutomationDiagnosticEvent>(lines[index],
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entry is null || !entry.EventName.StartsWith("auto_resume_", StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(entry.ProjectDirectory)) continue;
                    string project = CanonicalProjectPath(entry.ProjectDirectory);
                    latestByProject.TryAdd(project, entry);
                }
                catch (System.Text.Json.JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Auto-resume diagnostic restore failed: {ex.GetType().Name}");
            return;
        }

        if (Dispatcher.HasShutdownStarted) return;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    foreach (var (project, entry) in latestByProject)
                    {
                        if (_autoResumeDiagnostics.TryGetValue(project, out var current) && current.Timestamp >= entry.Timestamp) continue;
                        var restored = new OpenCodeAutoResumeDiagnostic(entry.Timestamp, entry.EventName["auto_resume_".Length..],
                            entry.Phase ?? "", entry.Detail, entry.SessionId, entry.MessageId,
                            entry.LastSubstantiveProgressAt, entry.ErrorCode, entry.ExceptionType,
                            entry.RequiresUserAction == true);
                        _autoResumeDiagnostics[project] = restored;
                    }
                    if (!_closing && !_resourcesDisposed) UpdateProjectTaskActivity();
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Auto-resume diagnostic restore UI failed: {ex.GetType().Name}"); }
            }).Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine($"Auto-resume diagnostic dispatch failed: {ex.GetType().Name}");
        }
    }

    private OpenCodeAutoResumeDiagnostic? LatestAutoResumeDiagnostic(string project) =>
        _autoResumeDiagnostics.TryGetValue(CanonicalProjectPath(project), out var diagnostic) ? diagnostic : null;

    private void OpenAutoResumeDiagnostics() => OpenDiagnosticLog(".automation-diagnostics.jsonl", "자동 진행");

    private void OpenUiDiagnostics() => OpenDiagnosticLog(".ui-diagnostics.jsonl", "화면 멈춤");

    private void OpenDiagnosticLog(string suffix, string description)
    {
        string path = _settingsPath + suffix;
        try
        {
            if (!File.Exists(path))
            {
                SetStatus(description + " 진단 로그가 아직 없습니다 · " + path);
                return;
            }
            var startInfo = new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            startInfo.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus(description + " 진단 로그를 열지 못했습니다 · " + AutomationDiagnostics.Sanitize(ex.Message));
        }
    }

    private void BeginTaskNotificationAttempt(string project, bool reusePendingAttempt = false)
    {
        project = CanonicalProjectPath(project);
        var runtime = RuntimeFor(project);
        if (reusePendingAttempt && _taskNotificationBindings.TryGetValue(project, out var pending) &&
            pending.IntentVersion == runtime.IntentVersion && !pending.Accepted) return;
        _taskNotificationBindings[project] = new TaskNotificationBinding
        {
            IntentVersion = runtime.IntentVersion,
            WasStoppedAtStart = runtime.SingleStopped,
            Pane = (runtime.SingleTerminal?.Tag as TerminalTabState)?.Pane
        };
    }

    private void ObserveTaskNotificationSubmission(TerminalTabState state, long version, long generation)
    {
        if (_closing || _closePending || generation <= 0 || generation != state.Pane.NativeSubmissionGeneration) return;
        var runtime = _automationRuntimes.Values.FirstOrDefault(value =>
            value.SingleTerminal?.Tag is TerminalTabState task && ReferenceEquals(task.Pane, state.Pane));
        if (runtime is null || HasExternalTerminal(runtime.Project)) return;
        bool manual = state.ManualSubmissionPending && version > state.ManualSubmissionConfirmationBaseline &&
            generation == state.ManualSubmissionGeneration;
        if (manual) BeginTaskNotificationAttempt(runtime.Project);
        if (!_taskNotificationBindings.TryGetValue(runtime.Project, out var binding) ||
            binding.IntentVersion != runtime.IntentVersion) return;
        binding.Pane = state.Pane;
        binding.ConfirmedGeneration = generation;
        binding.Accepted = true;
        binding.ManualWork |= manual;
    }

    private TaskNotificationObservation? ReadTaskNotificationObservation(string project, TaskNotificationBinding binding)
    {
        if (!_automationRuntimes.TryGetValue(project, out var runtime) || runtime.IntentVersion != binding.IntentVersion ||
            HasExternalTerminal(project)) return null;
        var state = runtime.SingleTerminal?.Tag as TerminalTabState;
        var pane = state?.Pane;
        bool starting = runtime.StartingSingle || _startingProjectTasks.Contains(project) ||
            runtime.SingleStartupCancellation is not null || _rerunningProjectTasks.Contains(project);
        if (binding.Pane is not null && !ReferenceEquals(binding.Pane, pane))
        {
            if (binding.Accepted) return null;
            binding.Pane = pane;
        }
        else if (binding.Pane is null) binding.Pane = pane;

        bool accepted = binding.Accepted && pane is not null &&
            binding.ConfirmedGeneration == pane.NativeSubmissionGeneration &&
            (binding.ManualWork || runtime.SingleSubmitted && !runtime.SinglePlanInputNeeded);
        bool waiting = pane?.NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput ||
            pane?.Activity == TerminalActivity.NeedsInput || pane?.NativeAutomationDiagnostic.ManualOverride == true;
        bool failed = runtime.SingleError is not null || pane?.NativeAutomationDiagnostic.Failed == true ||
            accepted && (state?.ExitCode is not null || pane?.Activity == TerminalActivity.Exited);
        bool verifiedCompletion = accepted && pane is { IsRunning: true } && pane.HasCompletedWork &&
            pane.HasVerifiedTaskCompletion && !SingleTaskIsRunning(runtime);
        bool verifiedStall = accepted && pane is { IsRunning: true } && pane.HasVerifiedNotificationStall;
        return new TaskNotificationObservation(project, binding.AttemptId, true,
            runtime.SingleStopped && (!binding.WasStoppedAtStart || binding.Accepted) || _stoppingProjectTasks.Contains(project), starting, accepted,
            verifiedCompletion, waiting, failed, verifiedStall);
    }

    private void ObserveTaskNotifications()
    {
        if (_closing || _closePending || _resourcesDisposed || _observingTaskNotifications) return;
        _observingTaskNotifications = true;
        try
        {
            RefreshTaskNotificationOptions();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool allowed = _taskNotificationPreferences.CanNotify(now) &&
                (_tray is not null || TaskNotificationSinkOverride is not null);
            foreach (var (project, binding) in _taskNotificationBindings.ToArray())
            {
                var observation = ReadTaskNotificationObservation(project, binding);
                if (observation is null)
                {
                    _taskNotificationBindings.Remove(project);
                    _taskNotificationTracker.Forget(project);
                    continue;
                }
                var notice = _taskNotificationTracker.Observe(observation, now, allowed);
                if (notice is null) continue;
                if (TaskNotificationSinkOverride is { } sink) { sink(notice); continue; }
                TerminalPane? pane = binding.Pane;
                long generation = binding.ConfirmedGeneration;
                bool StillRelevant()
                {
                    if (_closing || _closePending || !_taskNotificationPreferences.CanNotify(DateTimeOffset.UtcNow) ||
                        !_taskNotificationBindings.TryGetValue(project, out var current) || !ReferenceEquals(current, binding) ||
                        !ReferenceEquals(current.Pane, pane) || current.ConfirmedGeneration != generation) return false;
                    var latest = ReadTaskNotificationObservation(project, current);
                    if (latest is null || latest.IsStopped || latest.IsStarting || latest.IsAwaitingInput) return false;
                    return notice.Kind switch
                    {
                        TaskNotificationKind.Completed => latest.HasAcceptedSubmission && latest.HasVerifiedCompletion && !latest.HasDefiniteFailure,
                        TaskNotificationKind.Failed => latest.HasDefiniteFailure,
                        TaskNotificationKind.Stalled => latest.HasAcceptedSubmission && latest.HasVerifiedStall && !latest.HasDefiniteFailure,
                        _ => false
                    };
                }
                string name = ProjectDisplayName(project);
                string title = notice.Kind == TaskNotificationKind.Completed ? "작업 완료" :
                    notice.Kind == TaskNotificationKind.Failed ? "작업 실행 확인 필요" : "작업 진행 확인 필요";
                string body = notice.Kind == TaskNotificationKind.Completed ? $"{name}\n작업 완료를 확인했습니다." :
                    notice.Kind == TaskNotificationKind.Failed ? $"{name}\n실행 오류 또는 종료가 확인됐습니다. 태스크를 확인하세요." :
                    $"{name}\n오랫동안 진행 변화가 없습니다. 태스크를 확인하세요.";
                _tray?.ShowTaskNotification(title, body, notice.Kind != TaskNotificationKind.Completed, StillRelevant);
            }
        }
        catch (Exception ex)
        {
            // Notification delivery must never interrupt task execution or the UI timer.
            System.Diagnostics.Trace.WriteLine($"Task notification failed: {ex.GetType().Name}");
        }
        finally { _observingTaskNotifications = false; }
    }

    private void DisposeTaskNotifications()
    {
        _taskNotificationTimer.Stop();
        DisposeTaskNotificationOptions();
        foreach (var (pane, handler) in _autoResumeDiagnosticHandlers) pane.AutoResumeDiagnosticEmitted -= handler;
        _autoResumeDiagnosticHandlers.Clear();
        _autoResumeDiagnostics.Clear();
        _autoResumeWarningsShown.Clear();
        _taskNotificationBindings.Clear();
        _tray?.CancelTaskNotifications();
    }
}
