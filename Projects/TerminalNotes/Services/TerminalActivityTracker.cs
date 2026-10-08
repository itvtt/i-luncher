using System.Text;
using System.Text.RegularExpressions;

namespace TerminalNotes.Services;

public enum TerminalActivity
{
    Starting,
    Active,
    NeedsInput,
    Quiet,
    Idle,
    RateLimited,
    Exited,
    Error
}

/// <summary>
/// Classifies terminal activity from observable I/O without treating a live
/// process or silence alone as proof that work is in progress or blocked.
/// </summary>
public sealed class TerminalActivityTracker
{
    private static readonly Regex AnsiSequence = new(
        "\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)|\\x1B\\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PowerShellPrompt = new(
        @"(?i)(?:^|\n)\s*PS\s+[^\r\n]*?>\s*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DeviceResponse = new(
        @"\A(?:\x1b\[[0-9;:?><=$]*[cRntxyIO])+\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InterruptHint = new(
        @"(?i)\besc(?:ape)?\s+(?:to\s+)?(?:interrupt|cancel|stop)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenCodeComposer = new(
        @"(?i)┃\s*Ask\s+anything(?:…|\.\.\.)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenCodeIdleFooter = new(
        @"(?i)(?:ctrl\+p\s+)?commands\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenCodeComposerLine = new(
        @"(?i)(?:┃|│|\|)?\s*Ask\s+anything(?:…|\.\.\.)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenCodeBorder = new(
        @"^[\s┃│|]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenCodeBusyLine = new(
        @"(?i)\b(?:working|thinking|running|processing|building|progress)\b|[▓⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenCodeChromeLine = new(
        @"(?i)(?:^\s*(?:\+p\s+|ctrl\+p\s+)?commands\b|\besc(?:ape)?\s+(?:to\s+)?(?:interrupt|cancel|stop)\b|\bopencode\s+zen\b.*\b\d+\.\d+\.\d+\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] ApprovalPrompts =
    [
        "Do you want to proceed?",
        "Would you like to proceed?",
        "Would you like to run",
        "Press Enter to continue",
        "Allow this command",
        "Approve this command",
        "(y/n)",
        "[y/N]",
        "계속하시겠습니까",
        "승인하시겠습니까",
        "명령을 허용"
    ];

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _activeWindow;
    private readonly TimeSpan _startupWindow;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _lastOutputAt;
    private bool _manualNeedsInput;
    private bool _inferredNeedsInput;
    private string? _lastScreen;
    private string? _lastLayout;
    private bool _screenBusy;
    private bool _idlePrompt;
    private bool _screenLimited;
    private bool _workSubmitted;
    private bool _screenBusyObserved;
    private bool _completionPending;
    private bool _completionScreenChanged;
    private bool _completionTuiResponseObserved;
    private bool _openCodeComposerReady;
    private bool _busyHintWasActiveAtSubmit;
    private string? _busyHintAtSubmit;
    private string? _completionBaselineContent;
    private string? _submittedPrompt;
    private readonly StringBuilder _inputDraft = new();
    private DateTimeOffset? _completionCandidateAt;
    private bool _hasCompletedWork;
    private TerminalActivity _activity;
    private string _activityLabel;

    public TerminalActivityTracker(
        TimeProvider? timeProvider = null,
        TimeSpan? activeWindow = null,
        TimeSpan? startupWindow = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _activeWindow = activeWindow ?? TimeSpan.FromSeconds(1.5);
        _startupWindow = startupWindow ?? TimeSpan.FromSeconds(5);
        _startedAt = _timeProvider.GetUtcNow();
        _activity = TerminalActivity.Starting;
        _activityLabel = LabelFor(_activity, manualNeedsInput: false);
    }

    public event Action? ActivityChanged;

    public TerminalActivity Activity
    {
        get
        {
            lock (_gate)
            {
                return _activity;
            }
        }
    }

    public string ActivityLabel
    {
        get
        {
            lock (_gate)
            {
                return _activityLabel;
            }
        }
    }

    public bool HasCompletedWork
    {
        get
        {
            lock (_gate)
            {
                return _hasCompletedWork;
            }
        }
    }

    public void MarkStarted(bool startupWork = false)
    {
        bool changed;
        lock (_gate)
        {
            _startedAt = _timeProvider.GetUtcNow();
            _lastOutputAt = null;
            _manualNeedsInput = false;
            _inferredNeedsInput = false;
            _lastScreen = null;
            _lastLayout = null;
            _screenBusy = false;
            _idlePrompt = false;
            _screenLimited = false;
            _workSubmitted = startupWork;
            _screenBusyObserved = false;
            _completionPending = startupWork;
            _completionScreenChanged = false;
            _completionTuiResponseObserved = false;
            _openCodeComposerReady = false;
            _busyHintWasActiveAtSubmit = false;
            _busyHintAtSubmit = null;
            _completionBaselineContent = null;
            _submittedPrompt = null;
            _inputDraft.Clear();
            _completionCandidateAt = null;
            changed = SetCompletedLocked(false);
            changed |= SetStateLocked(TerminalActivity.Starting, manualNeedsInput: false);
        }

        RaiseChanged(changed);
    }

    public void ObserveOutput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            if (IsTerminalState(_activity))
            {
                return;
            }

            _lastOutputAt = _timeProvider.GetUtcNow();
            _inferredNeedsInput = false;
            _idlePrompt = false;
            _screenBusy = false;
            _screenLimited = false;
            _openCodeComposerReady = false;
            _completionCandidateAt = null;
            changed = _manualNeedsInput
                ? SetStateLocked(TerminalActivity.NeedsInput, manualNeedsInput: true)
                : SetStateLocked(TerminalActivity.Active, manualNeedsInput: false);
        }

        RaiseChanged(changed);
    }

    public void ObserveRenderedTail(string renderedTail)
    {
        ArgumentNullException.ThrowIfNull(renderedTail);
        bool changed;
        lock (_gate)
        {
            if (IsTerminalState(_activity))
            {
                return;
            }

            _idlePrompt = LooksLikeIdlePrompt(renderedTail);
            _inferredNeedsInput = !PowerShellPrompt.IsMatch(renderedTail.Replace("\r", "")) && LooksLikeInputPrompt(renderedTail);
            changed = EvaluateLocked(_timeProvider.GetUtcNow());
        }

        RaiseChanged(changed);
    }

    // The renderer reports text after applying ANSI commands. Cursor queries,
    // colour changes and repainting identical content are not new output.
    public void ObserveRenderedScreen(string renderedTail, string screen, string layout, bool promptAtCursor = false)
    {
        bool changed;
        lock (_gate)
        {
            if (IsTerminalState(_activity)) return;
            bool resized = _lastLayout is not null && _lastLayout != layout;
            bool screenChanged = !resized && !string.IsNullOrWhiteSpace(screen) && screen != _lastScreen;
            if (screenChanged)
            {
                _lastOutputAt = _timeProvider.GetUtcNow();
                if (_completionPending)
                    _completionScreenChanged = true;
            }
            _lastScreen = screen;
            _lastLayout = layout;
            if (_completionPending && !resized && _completionBaselineContent is not null &&
                HasOpenCodeResponseDelta(_completionBaselineContent, screen, _submittedPrompt))
            {
                _completionTuiResponseObserved = true;
                _completionScreenChanged = true;
            }
            string footer = string.Join('\n', screen.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(8));
            string recent = string.Join('\n', renderedTail.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(4));
            _screenLimited = Regex.IsMatch(recent, @"(?i)(?:you['’]ve hit your (?:usage )?limit|(?:token|usage|rate) limit (?:exceeded|reached)|(?:exceeded|reached).{0,25}(?:token|usage|rate) limit|insufficient_quota|(?:토큰|사용량|요청).{0,12}(?:한도|제한).{0,12}(?:초과|도달|소진)|(?:credit|quota)s? (?:exhausted|exceeded)|out of (?:extra usage|credits))");
            string? busyHint = FindInterruptHint(footer);
            bool unchangedPreSubmitHint = _workSubmitted && !_busyHintWasActiveAtSubmit &&
                busyHint is not null && string.Equals(busyHint, _busyHintAtSubmit, StringComparison.Ordinal);
            _screenBusy = busyHint is not null && (_workSubmitted || OpenCodeBusyLine.IsMatch(busyHint)) &&
                !unchangedPreSubmitHint;
            if (_screenBusy)
            {
                _workSubmitted = true;
                _screenBusyObserved = true;
                _completionCandidateAt = null;
            }
            bool openCodeIdleComposer = !_screenBusy &&
                OpenCodeComposer.IsMatch(recent) && OpenCodeIdleFooter.IsMatch(footer);
            _openCodeComposerReady = openCodeIdleComposer;
            _idlePrompt = promptAtCursor || LooksLikeIdlePrompt(renderedTail) || openCodeIdleComposer;
            // A shell prompt means the command has returned. Agent input rows
            // can remain visible while busy or while asking for confirmation.
            bool shellIdle = PowerShellPrompt.IsMatch(renderedTail.Replace("\r", ""));
            bool approval = ApprovalPrompts.Any(prompt => recent.Contains(prompt, StringComparison.OrdinalIgnoreCase));
            string inputEvidence = openCodeIdleComposer ? BeforeOpenCodeComposer(renderedTail) : renderedTail;
            _inferredNeedsInput = !shellIdle && (approval ||
                (!_screenBusy && LooksLikeInputPrompt(inputEvidence)));
            bool completed = _completionPending && _completionScreenChanged && shellIdle &&
                !_screenBusy && !_inferredNeedsInput && !_screenLimited;
            changed = _screenBusy ? SetCompletedLocked(false) : false;
            if (completed)
            {
                _completionPending = false;
                _completionScreenChanged = false;
                _workSubmitted = false;
                _screenBusyObserved = false;
                _completionCandidateAt = null;
                changed |= SetCompletedLocked(true);
            }
            bool returnedToTuiComposer = _workSubmitted && _completionPending && _completionScreenChanged &&
                _idlePrompt && !_screenBusy && !_inferredNeedsInput && !_screenLimited &&
                (_screenBusyObserved || (openCodeIdleComposer && _completionTuiResponseObserved));
            if (returnedToTuiComposer)
            {
                // The composer can remain visible while an agent is working.
                // Require a busy hint to disappear, or an actual response plus
                // OpenCode's ready composer, then keep that screen stable.
                if (_completionCandidateAt is null || screenChanged)
                    _completionCandidateAt = _timeProvider.GetUtcNow();
            }
            else
            {
                _completionCandidateAt = null;
            }
            changed |= EvaluateLocked(_timeProvider.GetUtcNow());
        }
        RaiseChanged(changed);
    }

    public void ObserveUserInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // xterm.onData also includes device/cursor replies, not just keyboard input.
        // Match the same keyboard-impossible CSI reports accepted by the input
        // capture. Arrow/history and other real key sequences must still reset
        // completion like any other user input.
        if (DeviceResponse.IsMatch(text)) return;
        bool changed;
        lock (_gate)
        {
            if (IsTerminalState(_activity))
            {
                return;
            }

            bool answeringPrompt = _manualNeedsInput || _inferredNeedsInput || _activity == TerminalActivity.NeedsInput;
            bool wasBusy = _screenBusy;
            string? currentBusyHint = FindInterruptHint(_lastScreen ?? string.Empty);
            _inferredNeedsInput = false;
            _screenBusy = false;
            _idlePrompt = false;
            _screenLimited = false;
            _lastOutputAt = null;
            _completionCandidateAt = null;
            changed = SetCompletedLocked(false);
            if (text.Length > 0 && answeringPrompt)
            {
                _manualNeedsInput = false;
                _workSubmitted = true;
            }
            if (text.Contains('\r') || text.Contains('\n'))
            {
                CaptureSubmittedInputLocked(text);
                _manualNeedsInput = false;
                _workSubmitted = true;
                _completionPending = true;
                _completionScreenChanged = false;
                _completionTuiResponseObserved = false;
                _screenBusyObserved = false;
                _completionBaselineContent = OpenCodeResponseContent(_lastScreen ?? string.Empty);
                _busyHintAtSubmit = currentBusyHint;
                _busyHintWasActiveAtSubmit = wasBusy;
                _openCodeComposerReady = false;
            }
            else
            {
                CaptureDraftInputLocked(text);
            }
            changed |= EvaluateLocked(_timeProvider.GetUtcNow());
        }

        RaiseChanged(changed);
    }

    public void MarkNeedsInput()
    {
        bool changed;
        lock (_gate)
        {
            if (IsTerminalState(_activity))
            {
                return;
            }

            _manualNeedsInput = true;
            _completionCandidateAt = null;
            changed = SetCompletedLocked(false);
            changed |= SetStateLocked(TerminalActivity.NeedsInput, manualNeedsInput: true);
        }

        RaiseChanged(changed);
    }

    public void ClearActivityOverride()
    {
        bool changed;
        lock (_gate)
        {
            if (_activity == TerminalActivity.Error)
            {
                return;
            }

            _manualNeedsInput = false;
            changed = EvaluateLocked(_timeProvider.GetUtcNow());
        }

        RaiseChanged(changed);
    }

    public void MarkExited()
    {
        bool changed;
        lock (_gate)
        {
            if (_activity == TerminalActivity.Error)
            {
                return;
            }

            _manualNeedsInput = false;
            _inferredNeedsInput = false;
            _completionPending = false;
            _completionScreenChanged = false;
            _completionTuiResponseObserved = false;
            _completionCandidateAt = null;
            _screenBusyObserved = false;
            _openCodeComposerReady = false;
            _completionBaselineContent = null;
            _submittedPrompt = null;
            _inputDraft.Clear();
            changed = SetCompletedLocked(false);
            changed |= SetStateLocked(TerminalActivity.Exited, manualNeedsInput: false);
        }

        RaiseChanged(changed);
    }

    public void MarkError()
    {
        bool changed;
        lock (_gate)
        {
            _manualNeedsInput = false;
            _inferredNeedsInput = false;
            _completionPending = false;
            _completionScreenChanged = false;
            _completionTuiResponseObserved = false;
            _completionCandidateAt = null;
            _screenBusyObserved = false;
            _openCodeComposerReady = false;
            _completionBaselineContent = null;
            _submittedPrompt = null;
            _inputDraft.Clear();
            changed = SetCompletedLocked(false);
            changed |= SetStateLocked(TerminalActivity.Error, manualNeedsInput: false);
        }

        RaiseChanged(changed);
    }

    public void MarkWorkCompleted()
    {
        bool changed;
        lock (_gate)
        {
            if (IsTerminalState(_activity))
            {
                return;
            }

            _workSubmitted = false;
            _screenBusyObserved = false;
            _completionPending = false;
            _completionScreenChanged = false;
            _completionTuiResponseObserved = false;
            _completionCandidateAt = null;
            _manualNeedsInput = false;
            _inferredNeedsInput = false;
            _screenBusy = false;
            _screenLimited = false;
            _openCodeComposerReady = false;
            _completionBaselineContent = null;
            _submittedPrompt = null;
            _inputDraft.Clear();
            changed = SetCompletedLocked(true);
            changed |= SetStateLocked(TerminalActivity.Idle, manualNeedsInput: false);
        }

        RaiseChanged(changed);
    }

    public void Tick()
    {
        bool changed;
        lock (_gate)
        {
            changed = EvaluateLocked(_timeProvider.GetUtcNow());
        }

        RaiseChanged(changed);
    }

    public static bool LooksLikeInputPrompt(string renderedTail)
    {
        if (string.IsNullOrWhiteSpace(renderedTail))
        {
            return false;
        }

        string plainText = AnsiSequence.Replace(renderedTail, string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal);

        string[] recentLines = plainText
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(3)
            .ToArray();
        string recentText = string.Join('\n', recentLines);
        return ApprovalPrompts.Any(prompt =>
            recentText.Contains(prompt, StringComparison.OrdinalIgnoreCase)) ||
            recentLines.Any(line => Regex.IsMatch(line, @"[?？]\s*$"));
    }

    private static bool LooksLikeIdlePrompt(string text) => PowerShellPrompt.IsMatch(text.Replace("\r", "")) ||
        Regex.IsMatch(text, @"(?:^|\n)\s*[›❯]\s*\z");

    private static string BeforeOpenCodeComposer(string text)
    {
        string plainText = AnsiSequence.Replace(text, string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal);
        Match composer = OpenCodeComposer.Match(plainText);
        return composer.Success ? plainText[..composer.Index] : plainText;
    }

    private void CaptureSubmittedInputLocked(string text)
    {
        int newline = text.IndexOfAny(['\r', '\n']);
        string beforeSubmit = newline >= 0 ? text[..newline] : text;
        CaptureDraftInputLocked(beforeSubmit);
        _submittedPrompt = _inputDraft.ToString().Trim();
        _inputDraft.Clear();
        if (newline >= 0 && newline + 1 < text.Length)
            CaptureDraftInputLocked(text[(newline + 1)..]);
    }

    private void CaptureDraftInputLocked(string text)
    {
        foreach (char value in text)
        {
            if (value is '\b' or '\x7f')
            {
                if (_inputDraft.Length > 0) _inputDraft.Length--;
            }
            else if (!char.IsControl(value))
            {
                _inputDraft.Append(value);
            }
            else if (value == '\x1b')
            {
                // Navigation/editing sequences make the local draft uncertain.
                _inputDraft.Clear();
            }
        }
    }

    private static string? FindInterruptHint(string screen) => screen
        .Split('\n')
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .TakeLast(8)
        .LastOrDefault(InterruptHint.IsMatch)?.Trim();

    private static bool HasOpenCodeResponseDelta(string baseline, string screen, string? submittedPrompt)
    {
        string[] previous = baseline.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] current = OpenCodeResponseContent(screen)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (current.Length == 0) return false;

        var knownWords = TokenizeOpenCodeContent(string.Join(' ', previous)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string promptWord in TokenizeOpenCodeContent(submittedPrompt ?? string.Empty))
            knownWords.Add(promptWord);
        return TokenizeOpenCodeContent(string.Join(' ', current)).Any(word => !knownWords.Contains(word));
    }

    private static IEnumerable<string> TokenizeOpenCodeContent(string text) =>
        Regex.Matches(text, @"[\p{L}\p{N}]+")
            .Cast<Match>()
            .Select(match => match.Value);

    private static string OpenCodeResponseContent(string screen) => string.Join('\n',
        AnsiSequence.Replace(screen, string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(line => OpenCodeBorder.Replace(line.Trim(), string.Empty).Trim())
            .Where(line => line.Length > 0 &&
                !OpenCodeComposerLine.IsMatch(line) &&
                !OpenCodeChromeLine.IsMatch(line)));

    private bool EvaluateLocked(DateTimeOffset now)
    {
        if (IsTerminalState(_activity))
        {
            return false;
        }

        if (_screenLimited) return SetStateLocked(TerminalActivity.RateLimited, manualNeedsInput: false);

        if (_manualNeedsInput)
        {
            return SetStateLocked(TerminalActivity.NeedsInput, manualNeedsInput: true);
        }

        if (_inferredNeedsInput)
        {
            return SetStateLocked(TerminalActivity.NeedsInput, manualNeedsInput: false);
        }
        if (_screenBusy)
            return SetStateLocked(TerminalActivity.Active, manualNeedsInput: false);

        if (_completionCandidateAt is { } returnedAt && now - returnedAt >= _activeWindow &&
            _workSubmitted && _completionPending && _idlePrompt && !_screenLimited &&
            (_screenBusyObserved || (_openCodeComposerReady && _completionTuiResponseObserved)))
        {
            // The interactive TUI has returned to its stable composer after a
            // busy transition or a visible response. This is idle evidence, not
            // proof that the task succeeded, so HasCompletedWork stays false.
            _workSubmitted = false;
            _completionPending = false;
            _completionScreenChanged = false;
            _completionTuiResponseObserved = false;
            _screenBusyObserved = false;
            _completionCandidateAt = null;
            _completionBaselineContent = null;
            _submittedPrompt = null;
        }

        // Once meaningful work starts, silence, repainting and a persistent
        // agent input row are not completion evidence. Keep the active state
        // until a recognized TUI composer returns, a question or terminal state
        // appears, or completion is authoritative.
        if (_workSubmitted)
            return SetStateLocked(TerminalActivity.Active, manualNeedsInput: false);

        if (_idlePrompt) return SetStateLocked(TerminalActivity.Idle, manualNeedsInput: false);

        // Startup banners, suggestions and typing are not submitted work.
        // Explicit busy, question and limit signals above still take precedence.
        if (!_workSubmitted && _lastScreen is not null)
            return SetStateLocked(TerminalActivity.Idle, manualNeedsInput: false);

        if (_lastOutputAt is { } lastOutput && now - lastOutput <= _activeWindow)
        {
            return SetStateLocked(TerminalActivity.Active, manualNeedsInput: false);
        }

        if (_lastOutputAt is null && now - _startedAt <= _startupWindow)
        {
            return SetStateLocked(TerminalActivity.Starting, manualNeedsInput: false);
        }

        return SetStateLocked(TerminalActivity.Quiet, manualNeedsInput: false);
    }

    private bool SetStateLocked(TerminalActivity activity, bool manualNeedsInput)
    {
        string label = LabelFor(activity, manualNeedsInput);
        if (_activity == activity && string.Equals(_activityLabel, label, StringComparison.Ordinal))
        {
            return false;
        }

        _activity = activity;
        _activityLabel = label;
        return true;
    }

    private bool SetCompletedLocked(bool completed)
    {
        if (_hasCompletedWork == completed)
        {
            return false;
        }

        _hasCompletedWork = completed;
        return true;
    }

    private static bool IsTerminalState(TerminalActivity activity) =>
        activity is TerminalActivity.Exited or TerminalActivity.Error;

    private static string LabelFor(TerminalActivity activity, bool manualNeedsInput) => activity switch
    {
        TerminalActivity.Starting => "시작 중",
        TerminalActivity.Active => "진행 중",
        TerminalActivity.NeedsInput when manualNeedsInput => "확인 필요 (수동)",
        TerminalActivity.NeedsInput => "확인 필요",
        TerminalActivity.Quiet => "대기 중 · 출력 없음",
        TerminalActivity.Idle => "대기 중",
        TerminalActivity.RateLimited => "토큰·사용량 한도",
        TerminalActivity.Exited => "종료됨",
        TerminalActivity.Error => "오류",
        _ => activity.ToString()
    };

    private void RaiseChanged(bool changed)
    {
        if (changed)
        {
            ActivityChanged?.Invoke();
        }
    }
}
