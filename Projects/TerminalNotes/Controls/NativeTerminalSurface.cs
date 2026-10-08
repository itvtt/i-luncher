using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using TerminalNotes.TerminalEngine;

namespace TerminalNotes.Controls;

/// <summary>A lightweight WPF renderer and input surface for the portable VT model.</summary>
public sealed class NativeTerminalSurface : FrameworkElement
{
    public const int MinimumColumns = 40;
    public const int MinimumRows = 24;
    private static readonly Typeface NormalTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface ItalicTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private readonly TerminalEmulator _terminal;
    private readonly ConcurrentQueue<string> _outputQueue = new();
    private int _outputDrainPosted;
    private long _queuedOutputCharacters;
    private readonly DispatcherTimer _cursorTimer;
    private TerminalSnapshot? _snapshot;
    private double _fontSize = 15;
    private double _cellWidth = 8.5;
    private double _cellHeight = 17;
    private int _scrollbackOffset;
    private bool _cursorOn = true;
    private bool _draggingSelection;
    private bool _hasSelection;
    private bool _selectAll;
    private bool _focused;
    private bool _minimumViewportEnabled;
    private bool _forceStateBackground;
    private ScrollViewer? _hostViewport;
    private int _anchorColumn, _anchorRow, _anchorScrollbackOffset;
    private int _selectionColumn, _selectionRow;
    private Brush _background = new SolidColorBrush(Color.FromRgb(12, 16, 22));
    private Brush _foreground = new SolidColorBrush(Color.FromRgb(237, 243, 251));
    private Brush _selection = new SolidColorBrush(Color.FromArgb(150, 67, 83, 107));
    private short _lastColumns, _lastRows;
    private long _renderedVersion = -1;

    public event Func<string, bool, long?, Exception?>? Input;
    public event Action<Exception>? InputFailed;
    public event Action<long, Exception?>? TextSendCompleted;
    public event Action<short, short>? DimensionsChanged;
    public event Action? Activated;
    public event Action? PasteRequested;
    public event Action<bool>? ContextMenuRequested;
    public event Action<string, bool>? Shortcut;
    public event Action? SelectionChanged;
    public event Action? CopyRequested;

    public NativeTerminalSurface(TerminalEmulator terminal)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        InputMethod.SetIsInputMethodEnabled(this, true);
        _terminal.Changed += OnTerminalChanged;
        _cursorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Render, (_, _) =>
        {
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        }, Dispatcher);
        _cursorTimer.Start();
        _snapshot = _terminal.GetSnapshot();
        SizeChanged += (_, _) => UpdateDimensions();
        Loaded += (_, _) => UpdateDimensions();
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) { _draggingSelection = false; ReleaseMouseCapture(); }
        };
        Unloaded += (_, _) => { _draggingSelection = false; ReleaseMouseCapture(); };
        GotKeyboardFocus += (_, _) => { _focused = true; _cursorOn = true; InvalidateVisual(); Activated?.Invoke(); };
        LostKeyboardFocus += (_, _) => { _focused = false; _cursorOn = true; InvalidateVisual(); };
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseUp += OnPreviewMouseUp;
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        TextInput += OnTextInput;
    }

    public int ScrollbackOffset => _scrollbackOffset;
    public bool HasSelection => _hasSelection;
    public string SelectedText => GetSelectedText();

    public double MinimumViewportWidth
    {
        get { MeasureCell(); return MinimumColumns * _cellWidth + 20; }
    }

    public double MinimumViewportHeight
    {
        get { MeasureCell(); return MinimumRows * _cellHeight + 4; }
    }

    /// <summary>Fits the complete TUI into its pane while keeping its VT grid at least 40 by 24.</summary>
    public static ScrollViewer CreateScrollableViewport(NativeTerminalSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        surface._minimumViewportEnabled = true;
        var scale = new ScaleTransform(1, 1);
        surface.LayoutTransform = scale;
        var viewport = new ScrollViewer
        {
            Content = surface,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false,
            Background = surface._background
        };
        surface._hostViewport = viewport;
        bool fitting = false;

        void FitSurfaceToViewport()
        {
            double availableWidth = viewport.ViewportWidth;
            double availableHeight = viewport.ViewportHeight;
            if (fitting || !double.IsFinite(availableWidth) || !double.IsFinite(availableHeight)
                || availableWidth <= 0 || availableHeight <= 0) return;
            fitting = true;
            try
            {
                // Keep OpenCode's safe logical grid, but show every row in small split panes.
                // LayoutTransform also maps pointer and text input coordinates back to the surface.
                double factor = Math.Min(1, Math.Min(availableWidth / surface.MinimumViewportWidth,
                    availableHeight / surface.MinimumViewportHeight));
                double width = availableWidth / factor;
                double height = availableHeight / factor;
                if (Math.Abs(scale.ScaleX - factor) > 0.000001) scale.ScaleX = factor;
                if (Math.Abs(scale.ScaleY - factor) > 0.000001) scale.ScaleY = factor;
                if (double.IsNaN(surface.Width) || Math.Abs(surface.Width - width) > 0.5) surface.Width = width;
                if (double.IsNaN(surface.Height) || Math.Abs(surface.Height - height) > 0.5) surface.Height = height;
            }
            finally { fitting = false; }
        }

        viewport.SizeChanged += (_, _) => FitSurfaceToViewport();
        viewport.Loaded += (_, _) => FitSurfaceToViewport();
        viewport.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) FitSurfaceToViewport();
        };
        surface.SizeChanged += (_, _) => FitSurfaceToViewport();
        return viewport;
    }

    public void SetTheme(string background, string foreground, string selectionBackground, bool forceStateBackground = false)
    {
        _background = new SolidColorBrush(ParseColor(background, Colors.Black));
        _foreground = new SolidColorBrush(ParseColor(foreground, Colors.White));
        Color selection = ParseColor(selectionBackground, Color.FromRgb(67, 83, 107));
        _selection = new SolidColorBrush(forceStateBackground
            ? Color.FromArgb(145, selection.R, selection.G, selection.B)
            : selection);
        _forceStateBackground = forceStateBackground;
        if (_hostViewport is not null) _hostViewport.Background = _background;
        InvalidateVisual();
    }

    public void Feed(string text)
    {
        if (string.IsNullOrEmpty(text) || _terminal is null) return;
        long queued = Interlocked.Add(ref _queuedOutputCharacters, text.Length);
        if (queued > 8_000_000)
        {
            while (_outputQueue.TryDequeue(out var discarded)) Interlocked.Add(ref _queuedOutputCharacters, -discarded.Length);
            const string warning = "\x1b[0m\r\n[화면 출력이 너무 많아 일부 출력을 생략했습니다]\r\n";
            _outputQueue.Enqueue(warning);
            Interlocked.Add(ref _queuedOutputCharacters, warning.Length);
        }
        _outputQueue.Enqueue(text);
        ScheduleOutputDrain();
    }

    public void FocusSurface()
    {
        if (!IsKeyboardFocusWithin) Keyboard.Focus(this);
    }

    public void SelectAllText()
    {
        _snapshot ??= _terminal.GetSnapshot();
        _scrollbackOffset = 0;
        _selectAll = true;
        _hasSelection = true;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void ClearSelectionText() => SetSelection(false);
    public void ClearTerminal()
    {
        _terminal.Clear();
        _scrollbackOffset = 0;
        SetSelection(false);
    }

    public void ScrollToBottom()
    {
        _scrollbackOffset = 0;
        InvalidateVisual();
    }

    public void SendText(string text, bool submit, long sendId)
    {
        try
        {
            var snapshot = _snapshot ?? _terminal.GetSnapshot();
            Exception? error = Emit(Encoding.UTF8.GetString(TerminalInputEncoder.EncodePaste(text, snapshot.BracketedPaste)), true, sendId);
            if (error is not null) throw error;
            if (submit)
            {
                error = Emit(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.Enter)), true, sendId);
                if (error is not null) throw error;
            }
            TextSendCompleted?.Invoke(sendId, null);
        }
        catch (Exception exception) { TextSendCompleted?.Invoke(sendId, exception); }
    }

    public void PasteText(string text)
    {
        var snapshot = _snapshot ?? _terminal.GetSnapshot();
        Send(Encoding.UTF8.GetString(TerminalInputEncoder.EncodePaste(text, snapshot.BracketedPaste)));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureCell();
        double width = double.IsInfinity(availableSize.Width) ? 80 * _cellWidth : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 24 * _cellHeight : availableSize.Height;
        return new Size(Math.Max(_cellWidth * 2, width), Math.Max(_cellHeight * 2, height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        MeasureCell();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(_background, null, bounds);
        var snapshot = _snapshot ??= _terminal.GetSnapshot(_scrollbackOffset);
        if (snapshot.Version != _renderedVersion || snapshot.ScrollbackCount < _scrollbackOffset)
        {
            _scrollbackOffset = Math.Clamp(_scrollbackOffset, 0, snapshot.ScrollbackCount);
            snapshot = _snapshot = _terminal.GetSnapshot(_scrollbackOffset);
            _renderedVersion = snapshot.Version;
        }

        int visibleRows = Math.Min(snapshot.Rows, Math.Max(1, (int)Math.Ceiling(ActualHeight / _cellHeight)));
        int firstLine = Math.Max(0, snapshot.Lines.Count - visibleRows);
        for (int row = 0; row < visibleRows; row++)
        {
            int lineIndex = firstLine + row;
            if (lineIndex >= snapshot.Lines.Count) break;
            DrawLine(dc, snapshot, snapshot.Lines[lineIndex], row);
        }
        DrawSelection(dc, visibleRows, snapshot.Columns);
        if (_focused && _cursorOn && snapshot.CursorVisible && _scrollbackOffset == 0)
            DrawCursor(dc, snapshot);

        if (snapshot.ScrollbackCount > 0)
        {
            double railWidth = 10;
            double railX = Math.Max(0, ActualWidth - railWidth - 3);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 27, 36)), null, new Rect(railX, 8, railWidth, Math.Max(1, ActualHeight - 16)));
            double ratio = snapshot.Rows / (double)(snapshot.Rows + snapshot.ScrollbackCount);
            double thumbHeight = Math.Max(18, (ActualHeight - 16) * ratio);
            double maxTop = Math.Max(1, ActualHeight - 16 - thumbHeight);
            double top = maxTop * (1 - _scrollbackOffset / (double)snapshot.ScrollbackCount);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(98, 119, 142)), null, new Rect(railX + 1, 8 + top, railWidth - 2, thumbHeight), 4, 4);
        }
    }

    private void DrawLine(DrawingContext dc, TerminalSnapshot snapshot, TerminalLine line, int row)
    {
        int column = 0;
        int start = 0;
        while (start < line.Cells.Count && column < snapshot.Columns)
        {
            TerminalStyle style = line.Cells[start].Style;
            int runStart = column;
            var run = new StringBuilder();
            int runCells = 0;
            while (start < line.Cells.Count && line.Cells[start].Style == style && column < snapshot.Columns)
            {
                TerminalCell cell = line.Cells[start++];
                if (cell.Width == 0)
                {
                    if (run.Length > 0) run.Append(cell.Text);
                    continue;
                }
                run.Append(string.IsNullOrEmpty(cell.Text) ? " " : cell.Text);
                int width = Math.Clamp(cell.Width, 1, 2);
                column += width;
                runCells += width;
            }
            if (run.Length > 0) DrawRun(dc, run.ToString(), style, runStart, row, runCells);
        }
    }

    private void DrawRun(DrawingContext dc, string text, TerminalStyle style, int column, int row, int cells)
    {
        Color themeForeground = ((SolidColorBrush)_foreground).Color;
        Color stateBackground = ((SolidColorBrush)_background).Color;
        Color sourceForeground = Resolve(style.Foreground, themeForeground);
        Color sourceBackground = Resolve(style.Background, stateBackground);
        Color foreground = style.Inverse ? sourceBackground : sourceForeground;
        Color background = style.Inverse ? sourceForeground : sourceBackground;
        if (_forceStateBackground)
        {
            // Keep the activity color as the complete terminal surface, including
            // cells which carry explicit ANSI backgrounds or inverse video.
            background = stateBackground;
            if (style.Inverse && style.Background.IsDefault)
                foreground = themeForeground;
            foreground = EnsureReadableForeground(foreground, stateBackground);
        }
        if (background != stateBackground)
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(column * _cellWidth, row * _cellHeight, cells * _cellWidth, _cellHeight));
        Typeface typeface = style.Bold ? BoldTypeface : style.Italic ? ItalicTypeface : NormalTypeface;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
            _fontSize, new SolidColorBrush(foreground), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(_cellWidth, cells * _cellWidth + _cellWidth), MaxTextHeight = _cellHeight, Trimming = TextTrimming.None };
        dc.DrawText(formatted, new Point(column * _cellWidth, row * _cellHeight));
        if (style.Underline || (_forceStateBackground && style.Inverse))
            dc.DrawLine(new Pen(new SolidColorBrush(foreground), 1), new Point(column * _cellWidth, (row + 1) * _cellHeight - 1), new Point((column + cells) * _cellWidth, (row + 1) * _cellHeight - 1));
        if (style.Strike)
            dc.DrawLine(new Pen(new SolidColorBrush(foreground), 1), new Point(column * _cellWidth, row * _cellHeight + _cellHeight / 2), new Point((column + cells) * _cellWidth, row * _cellHeight + _cellHeight / 2));
    }

    private void DrawSelection(DrawingContext dc, int visibleRows, int columns)
    {
        if (!_hasSelection) return;
        if (_selectAll)
        {
            dc.DrawRectangle(_selection, null, new Rect(0, 0, columns * _cellWidth, visibleRows * _cellHeight));
            return;
        }
        if (_anchorScrollbackOffset != _scrollbackOffset) return;
        var (startColumn, startRow, endColumn, endRow) = OrderedSelection();
        int top = Math.Max(0, startRow);
        int bottom = Math.Min(visibleRows - 1, endRow);
        for (int row = top; row <= bottom; row++)
        {
            int left = row == startRow ? startColumn : 0;
            int right = row == endRow ? endColumn + 1 : columns;
            if (right <= left) continue;
            dc.DrawRectangle(_selection, null, new Rect(left * _cellWidth, row * _cellHeight, (right - left) * _cellWidth, _cellHeight));
        }
    }

    private void DrawCursor(DrawingContext dc, TerminalSnapshot snapshot)
    {
        if (snapshot.CursorColumn < 0 || snapshot.CursorColumn >= snapshot.Columns || snapshot.CursorRow < 0 || snapshot.CursorRow >= snapshot.Rows) return;
        double x = snapshot.CursorColumn * _cellWidth, y = snapshot.CursorRow * _cellHeight;
        var brush = _foreground;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, ((SolidColorBrush)brush).Color.R, ((SolidColorBrush)brush).Color.G, ((SolidColorBrush)brush).Color.B)), null,
            new Rect(x, y, Math.Max(1, _cellWidth), _cellHeight));
        if (snapshot.CursorRow < snapshot.Lines.Count && snapshot.CursorColumn < snapshot.Lines[snapshot.CursorRow].Cells.Count)
        {
            var cell = snapshot.Lines[snapshot.CursorRow].Cells[snapshot.CursorColumn];
            if (!string.IsNullOrEmpty(cell.Text))
            {
                var text = new FormattedText(cell.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, NormalTypeface,
                    _fontSize, _background, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point(x, y));
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.None) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = Keyboard.Modifiers;
        bool shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0, control = (mods & ModifierKeys.Control) != 0;
        if ((key == Key.Apps || key == Key.F10 && shift) && !control)
        {
            e.Handled = true;
            ContextMenuRequested?.Invoke(_hasSelection);
            return;
        }
        if (control && shift && key is Key.C or Key.T or Key.M)
        {
            e.Handled = true;
            if (key == Key.C) CopyRequested?.Invoke();
            else Shortcut?.Invoke(key == Key.T ? "t" : "m", true);
            return;
        }
        if (control && key == Key.V)
        {
            e.Handled = true;
            PasteRequested?.Invoke();
            return;
        }
        if (shift && !control && _snapshot is { ScrollbackCount: > 0 } && key is Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            var state = _snapshot;
            _scrollbackOffset = key switch
            {
                Key.PageUp => Math.Min(state.ScrollbackCount, _scrollbackOffset + state.Rows),
                Key.PageDown => Math.Max(0, _scrollbackOffset - state.Rows),
                Key.Home => state.ScrollbackCount,
                _ => 0
            };
            _snapshot = _terminal.GetSnapshot(_scrollbackOffset);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.CapsLock or Key.NumLock or Key.Scroll) return;
        if (control && TryEncodeControlCharacter(key, out byte controlByte))
        {
            e.Handled = true;
            Send(Encoding.UTF8.GetString([controlByte]));
            return;
        }
        if (TryMapKey(key, out TerminalKey terminalKey))
        {
            e.Handled = true;
            var snapshot = _snapshot ?? _terminal.GetSnapshot();
            Send(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(terminalKey, shift, alt, control, snapshot.ApplicationCursorKeys)));
            return;
        }
        if (key is Key.Tab or Key.Enter or Key.Back or Key.Escape)
        {
            e.Handled = true;
            var mapped = key switch { Key.Tab => TerminalKey.Tab, Key.Enter => TerminalKey.Enter, Key.Back => TerminalKey.Backspace, _ => TerminalKey.Escape };
            Send(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(mapped, shift, alt, control)));
        }
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        ModifierKeys mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0) { e.Handled = true; return; }
        e.Handled = true;
        Send(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeText(e.Text, (mods & ModifierKeys.Alt) != 0)));
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            FocusSurface();
            var snapshot = _snapshot ?? _terminal.GetSnapshot();
            if (snapshot.MouseMode != 0 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && CanReportMouse(snapshot, e.GetPosition(this)))
                SendMouse(2, e.GetPosition(this), snapshot, release: false);
            else ContextMenuRequested?.Invoke(_hasSelection);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        FocusSurface();
        Activated?.Invoke();
        var state = _snapshot ?? _terminal.GetSnapshot();
        Point point = e.GetPosition(this);
        if (state.MouseMode != 0 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && CanReportMouse(state, point))
        {
            SendMouse(0, point, state, release: false);
            _draggingSelection = false;
            e.Handled = true;
            return;
        }
        (_anchorColumn, _anchorRow) = Position(point, state);
        _anchorScrollbackOffset = _scrollbackOffset;
        (_selectionColumn, _selectionRow) = (_anchorColumn, _anchorRow);
        _draggingSelection = true;
        CaptureMouse();
        SetSelection(false);
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var state = _snapshot ?? _terminal.GetSnapshot();
        if (_draggingSelection && e.LeftButton == MouseButtonState.Pressed)
        {
            (_selectionColumn, _selectionRow) = Position(e.GetPosition(this), state);
            SetSelection(true);
            e.Handled = true;
        }
        else if (state.MouseMode == 1003 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
                 state.MouseMode == 1002 && e.LeftButton == MouseButtonState.Pressed && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (CanReportMouse(state, e.GetPosition(this)))
            {
                SendMouse(0, e.GetPosition(this), state, motion: true);
                e.Handled = true;
            }
        }
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingSelection && e.ChangedButton == MouseButton.Left)
        {
            _draggingSelection = false;
            ReleaseMouseCapture();
            (_selectionColumn, _selectionRow) = Position(e.GetPosition(this), _snapshot ?? _terminal.GetSnapshot());
            SetSelection(true);
            e.Handled = true;
            return;
        }
        var state = _snapshot ?? _terminal.GetSnapshot();
        if (state.MouseMode >= 1000 && e.ChangedButton is MouseButton.Left or MouseButton.Right && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && CanReportMouse(state, e.GetPosition(this)))
        {
            SendMouse(e.ChangedButton == MouseButton.Left ? 0 : 2, e.GetPosition(this), state, release: true);
            e.Handled = true;
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var state = _snapshot ?? _terminal.GetSnapshot();
        int steps = Math.Max(1, Math.Abs(e.Delta) / 120);
        if (state.MouseMode >= 1000 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && CanReportMouse(state, e.GetPosition(this)))
        {
            for (int i = 0; i < steps; i++) SendMouse(e.Delta > 0 ? 64 : 65, e.GetPosition(this), state);
        }
        else
        {
            _scrollbackOffset = Math.Clamp(_scrollbackOffset + (e.Delta > 0 ? steps * 3 : -steps * 3), 0, state.ScrollbackCount);
            _snapshot = _terminal.GetSnapshot(_scrollbackOffset);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    private bool CanReportMouse(TerminalSnapshot snapshot, Point point)
    {
        if (snapshot.SgrMouse) return true;
        (int column, int row) = Position(point, snapshot);
        return column <= 94 && row <= 94;
    }

    private void SendMouse(int button, Point point, TerminalSnapshot snapshot, bool release = false, bool motion = false)
    {
        (int column, int row) = Position(point, snapshot);
        ModifierKeys mods = Keyboard.Modifiers;
        byte[] bytes = snapshot.SgrMouse
            ? TerminalInputEncoder.EncodeMouse(button, column + 1, row + 1, release,
                mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Control), motion)
            : EncodeLegacyMouse(button, column, row, release, motion,
                mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Control));
        Send(Encoding.UTF8.GetString(bytes));
    }

    private static byte[] EncodeLegacyMouse(int button, int column, int row, bool release, bool motion,
        bool shift, bool alt, bool control)
    {
        int code = (release ? 3 : Math.Clamp(button, 0, 2)) + (shift ? 4 : 0) + (alt ? 8 : 0) + (control ? 16 : 0);
        if (motion) code += 32;
        return Encoding.ASCII.GetBytes($"\x1b[M{(char)(32 + code)}{(char)(33 + column)}{(char)(33 + row)}");
    }

    private string GetSelectedText()
    {
        if (!_hasSelection) return string.Empty;
        if (_selectAll) return GetEntireBufferText();
        var (startColumn, startRow, endColumn, endRow) = OrderedSelection();
        return _terminal.GetSelectionText(startColumn, startRow, endColumn, endRow, _anchorScrollbackOffset);
    }

    private string GetEntireBufferText() => _terminal.GetAllText();

    private (int StartColumn, int StartRow, int EndColumn, int EndRow) OrderedSelection()
    {
        bool forward = _anchorRow < _selectionRow || (_anchorRow == _selectionRow && _anchorColumn <= _selectionColumn);
        return forward ? (_anchorColumn, _anchorRow, _selectionColumn, _selectionRow) : (_selectionColumn, _selectionRow, _anchorColumn, _anchorRow);
    }

    private (int Column, int Row) Position(Point point, TerminalSnapshot snapshot)
    {
        int column = Math.Clamp((int)(point.X / _cellWidth), 0, Math.Max(0, snapshot.Columns - 1));
        int row = Math.Clamp((int)(point.Y / _cellHeight), 0, Math.Max(0, snapshot.Rows - 1));
        return (column, row);
    }

    private void Send(string text)
    {
        if (text.Length == 0) return;
        Exception? error = Emit(text, false, null);
        if (error is not null) InputFailed?.Invoke(error);
    }

    private Exception? Emit(string text, bool programmatic, long? sendId) => Input?.Invoke(text, programmatic, sendId);

    private void SetSelection(bool value)
    {
        _selectAll = false;
        if (_hasSelection == value && !value) return;
        _hasSelection = value;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    private void OnTerminalChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(OnTerminalChanged, DispatcherPriority.Render);
            return;
        }
        int previousScrollback = _snapshot?.ScrollbackCount ?? 0;
        _snapshot = _terminal.GetSnapshot(_scrollbackOffset);
        if (_scrollbackOffset == 0) ScrollToBottom();
        else if (_snapshot.ScrollbackCount < _scrollbackOffset) _scrollbackOffset = _snapshot.ScrollbackCount;
        else if (_snapshot.ScrollbackCount > previousScrollback) _scrollbackOffset += _snapshot.ScrollbackCount - previousScrollback;
        _snapshot = _terminal.GetSnapshot(_scrollbackOffset);
        InvalidateVisual();
    }

    private void ScheduleOutputDrain()
    {
        if (Interlocked.Exchange(ref _outputDrainPosted, 1) != 0) return;
        if (Dispatcher.HasShutdownStarted) { Interlocked.Exchange(ref _outputDrainPosted, 0); return; }
        Dispatcher.BeginInvoke(DrainOutput, DispatcherPriority.Render);
    }

    private void DrainOutput()
    {
        try
        {
            var batch = new StringBuilder(65536);
            while (batch.Length < 65536 && _outputQueue.TryDequeue(out var chunk))
            {
                Interlocked.Add(ref _queuedOutputCharacters, -chunk.Length);
                batch.Append(chunk);
            }
            if (batch.Length > 0) _terminal.Feed(batch.ToString());
        }
        finally
        {
            Interlocked.Exchange(ref _outputDrainPosted, 0);
            if (!_outputQueue.IsEmpty) ScheduleOutputDrain();
        }
    }

    private void UpdateDimensions()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        MeasureCell();
        int minimumColumns = _minimumViewportEnabled ? MinimumColumns : 2;
        int minimumRows = _minimumViewportEnabled ? MinimumRows : 2;
        short columns = (short)Math.Clamp((int)(Math.Max(1, ActualWidth - 16) / _cellWidth), minimumColumns, 500);
        short rows = (short)Math.Clamp((int)(ActualHeight / _cellHeight), minimumRows, 500);
        if (columns == _lastColumns && rows == _lastRows) return;
        _lastColumns = columns;
        _lastRows = rows;
        DimensionsChanged?.Invoke(columns, rows);
    }

    private void MeasureCell()
    {
        var formatted = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, NormalTypeface,
            _fontSize, _foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _cellWidth = Math.Max(1, formatted.WidthIncludingTrailingWhitespace);
        _cellHeight = Math.Max(1, formatted.Height * 1.15);
    }

    private static bool TryMapKey(Key key, out TerminalKey mapped)
    {
        mapped = key switch
        {
            Key.Up => TerminalKey.Up, Key.Down => TerminalKey.Down, Key.Left => TerminalKey.Left, Key.Right => TerminalKey.Right,
            Key.Home => TerminalKey.Home, Key.End => TerminalKey.End, Key.Insert => TerminalKey.Insert, Key.Delete => TerminalKey.Delete,
            Key.PageUp => TerminalKey.PageUp, Key.PageDown => TerminalKey.PageDown,
            Key.F1 => TerminalKey.F1, Key.F2 => TerminalKey.F2, Key.F3 => TerminalKey.F3, Key.F4 => TerminalKey.F4,
            Key.F5 => TerminalKey.F5, Key.F6 => TerminalKey.F6, Key.F7 => TerminalKey.F7, Key.F8 => TerminalKey.F8,
            Key.F9 => TerminalKey.F9, Key.F10 => TerminalKey.F10, Key.F11 => TerminalKey.F11, Key.F12 => TerminalKey.F12,
            _ => (TerminalKey)(-1)
        };
        return (int)mapped >= 0;
    }

    private static bool TryEncodeControlCharacter(Key key, out byte value)
    {
        int letter = key switch
        {
            >= Key.A and <= Key.Z => (int)key - (int)Key.A + 1,
            Key.Space => 0,
            Key.OemOpenBrackets => 0x1b,
            Key.Oem5 => 0x1c,
            Key.OemCloseBrackets => 0x1d,
            Key.D6 => 0x1e,
            Key.OemMinus => 0x1f,
            _ => -1
        };
        value = (byte)Math.Max(0, letter);
        return letter >= 0;
    }

    private static Color Resolve(TerminalColor color, Color fallback) => color.IsDefault ? fallback : Color.FromRgb(color.R, color.G, color.B);
    private static Color EnsureReadableForeground(Color foreground, Color background)
    {
        if (ContrastRatio(foreground, background) >= 4.5) return foreground;
        return ContrastRatio(Colors.Black, background) >= ContrastRatio(Colors.White, background)
            ? Colors.Black
            : Colors.White;
    }

    private static double ContrastRatio(Color foreground, Color background)
    {
        double first = RelativeLuminance(foreground);
        double second = RelativeLuminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte component)
        {
            double value = component / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return fallback; }
    }

    public void DisposeSurface()
    {
        _cursorTimer.Stop();
        _terminal.Changed -= OnTerminalChanged;
        _draggingSelection = false;
        ReleaseMouseCapture();
    }
}
