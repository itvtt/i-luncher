using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using TerminalNotes.TerminalEngine;

namespace TerminalNotes.TerminalDesktop;

internal sealed class TerminalSurface : Control
{
    private const double FontSize = 15, PaddingSize = 3;
    private readonly TerminalEmulator _engine;
    private readonly object _engineGate;
    private readonly Typeface _font = new(OperatingSystem.IsWindows() ? "Cascadia Mono,Consolas" : "DejaVu Sans Mono,Menlo,monospace");
    private readonly IBrush _background = new SolidColorBrush(Color.Parse("#14171d"));
    private readonly IBrush _foreground = new SolidColorBrush(Color.Parse("#e3e6ec"));
    private readonly IBrush _selection = new SolidColorBrush(Color.Parse("#385575"));
    private readonly Dictionary<(string, TerminalStyle), FormattedText> _glyphs = [];
    private readonly Dictionary<TerminalColor, IBrush> _colors = [];
    private readonly DispatcherTimer _refresh;
    private readonly TerminalImeClient _ime;
    private TerminalSnapshot _snapshot;
    private readonly double _cellWidth, _cellHeight;
    private int _dirty = 1, _offset;
    private (int X, int Y)? _anchor, _end;
    private bool _dragging;
    private bool _allSelected;
    private int _reportedButton = -1;
    private KeyModifiers _modifiers;
    private string _preedit = "";
    private string? _lastTitle;
    public event Action<byte[]>? Input;
    public event Action<int, int>? GridResized;
    public event Action<string?>? TitleChanged;
    public event Action<string>? Error;

    public TerminalSurface(TerminalEmulator engine, object engineGate)
    {
        _engine = engine; _engineGate = engineGate; _snapshot = Snapshot(); Focusable = true; ClipToBounds = true;
        var measure = Text("M", TerminalStyle.Default);
        _cellWidth = Math.Ceiling(measure.WidthIncludingTrailingWhitespace);
        _cellHeight = Math.Ceiling(measure.Height + 2);
        _ime = new TerminalImeClient(this);
        AddHandler(TextInputMethodClientRequestedEvent, (_, args) => args.Client = _ime);
        _engine.Changed += MarkDirty;
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => Refresh());
        _refresh.Start();
        var copy = new MenuItem { Header = "복사" }; copy.Click += async (_, _) => await CopyAsync();
        var paste = new MenuItem { Header = "붙여넣기" }; paste.Click += async (_, _) => await PasteAsync();
        var all = new MenuItem { Header = "전체 선택" }; all.Click += (_, _) => SelectAll();
        var clear = new MenuItem { Header = "선택 해제" }; clear.Click += (_, _) => ClearSelection();
        ContextMenu = new ContextMenu { ItemsSource = new[] { copy, paste, all, clear } };
        SizeChanged += (_, _) => Fit();
    }

    public void Stop() { _engine.Changed -= MarkDirty; _refresh.Stop(); }
    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);
    private TerminalSnapshot Snapshot(int offset = 0) { lock (_engineGate) return _engine.GetSnapshot(offset); }
    private void Refresh()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        _snapshot = Snapshot(_offset);
        _offset = Math.Min(_offset, _snapshot.ScrollbackCount);
        if (_snapshot.AlternateScreen) _offset = 0;
        if (_lastTitle != _snapshot.Title) { _lastTitle = _snapshot.Title; TitleChanged?.Invoke(_lastTitle); }
        _ime.CursorMoved(); InvalidateVisual();
    }
    private void Fit()
    {
        int columns = Math.Clamp((int)((Bounds.Width - 2 * PaddingSize) / _cellWidth), 2, 512);
        int rows = Math.Clamp((int)((Bounds.Height - 2 * PaddingSize) / _cellHeight), 1, 256);
        if (columns == _snapshot.Columns && rows == _snapshot.Rows) return;
        lock (_engineGate) { _engine.Resize(columns, rows); _snapshot = _engine.GetSnapshot(_offset); }
        ClearSelection(); GridResized?.Invoke(columns, rows); MarkDirty();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_background, new Rect(Bounds.Size));
        using var clip = context.PushClip(new Rect(Bounds.Size));
        for (int row = 0; row < _snapshot.Lines.Count; row++)
        {
            var cells = _snapshot.Lines[row].Cells;
            for (int column = 0; column < cells.Count; column++)
            {
                TerminalCell cell = cells[column];
                if (cell.Width == 0) continue;
                var rect = CellRect(column, row, Math.Max(1, cell.Width));
                bool selected = IsSelected(column, row) || cell.Width == 2 && IsSelected(column + 1, row);
                var background = cell.Style.Inverse ? Brush(cell.Style.Foreground, _foreground) : Brush(cell.Style.Background, _background);
                context.FillRectangle(selected ? _selection : background, rect);
                if (cell.Text.Length > 0 && cell.Text != " ")
                {
                    using var cellClip = context.PushClip(rect);
                    context.DrawText(Text(cell.Text, cell.Style), new Point(rect.X, rect.Y));
                }
                var foreground = cell.Style.Inverse ? Brush(cell.Style.Background, _background) : Brush(cell.Style.Foreground, _foreground);
                if (cell.Style.Underline) context.DrawLine(new Pen(foreground), new Point(rect.X, rect.Bottom - 2), new Point(rect.Right, rect.Bottom - 2));
                if (cell.Style.Strike) context.DrawLine(new Pen(foreground), new Point(rect.X, rect.Center.Y), new Point(rect.Right, rect.Center.Y));
            }
        }
        if (_offset == 0 && _snapshot.CursorVisible)
        {
            var rect = CursorRectangle;
            context.DrawRectangle(null, new Pen(_foreground, IsFocused ? 2 : 1), rect);
            if (_preedit.Length > 0)
            {
                FormattedText preedit = Text(_preedit, TerminalStyle.Default);
                context.FillRectangle(_background, new Rect(rect.X, rect.Y, preedit.WidthIncludingTrailingWhitespace, _cellHeight));
                context.DrawText(preedit, rect.Position);
                context.DrawLine(new Pen(_foreground), new Point(rect.X, rect.Bottom - 1), new Point(rect.X + preedit.WidthIncludingTrailingWhitespace, rect.Bottom - 1));
            }
        }
    }
    private IBrush Brush(TerminalColor color, IBrush fallback)
    {
        if (color.IsDefault) return fallback;
        if (_colors.TryGetValue(color, out var brush)) return brush;
        if (_colors.Count > 1024) _colors.Clear();
        return _colors[color] = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }
    private FormattedText Text(string value, TerminalStyle style)
    {
        if (_glyphs.TryGetValue((value, style), out var text)) return text;
        if (_glyphs.Count > 4096) _glyphs.Clear();
        var typeface = new Typeface(_font.FontFamily, style.Italic ? FontStyle.Italic : FontStyle.Normal, style.Bold ? FontWeight.Bold : FontWeight.Normal);
        var brush = style.Inverse ? Brush(style.Background, _background) : Brush(style.Foreground, _foreground);
        return _glyphs[(value, style)] = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, brush);
    }
    private Rect CellRect(int x, int y, int width = 1) => new(PaddingSize + x * _cellWidth, PaddingSize + y * _cellHeight, width * _cellWidth, _cellHeight);
    internal Rect CursorRectangle => CellRect(_snapshot.CursorColumn, _snapshot.CursorRow);

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text))
        { _preedit = ""; Send(TerminalInputEncoder.EncodeText(e.Text, _modifiers.HasFlag(KeyModifiers.Alt) && !_modifiers.HasFlag(KeyModifiers.Control))); e.Handled = true; }
    }
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); _modifiers = e.KeyModifiers;
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (control && shift && e.Key == Key.C) { e.Handled = true; await CopyAsync(); return; }
        if ((control && shift && e.Key == Key.V) || (shift && e.Key == Key.Insert)) { e.Handled = true; await PasteAsync(); return; }
        // An IME owns the keys while it is composing; only committed TextInput is sent.
        if (_preedit.Length > 0) return;
        if (shift && (e.Key == Key.PageUp || e.Key == Key.PageDown))
        { Scroll((e.Key == Key.PageUp ? 1 : -1) * _snapshot.Rows); e.Handled = true; return; }
        if (control && !alt)
        {
            int key = (int)e.Key;
            int code = e.Key >= Key.A && e.Key <= Key.Z ? key - (int)Key.A + 1
                : e.Key is Key.Space or Key.D2 ? 0 : e.Key == Key.OemOpenBrackets ? 27
                : e.Key == Key.OemPipe ? 28 : e.Key == Key.OemCloseBrackets ? 29
                : e.Key == Key.D6 ? 30 : e.Key is Key.OemMinus or Key.Subtract ? 31 : -1;
            if (code >= 0) { Send([(byte)code]); e.Handled = true; return; }
        }
        if (MapKey(e.Key) is { } terminalKey)
        { Send(TerminalInputEncoder.EncodeKey(terminalKey, shift, alt, control, _snapshot.ApplicationCursorKeys)); e.Handled = true; }
    }
    private static TerminalKey? MapKey(Key key) => key switch
    {
        Key.Enter => TerminalKey.Enter, Key.Back => TerminalKey.Backspace,
        Key.Tab => TerminalKey.Tab, Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up, Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left, Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home, Key.End => TerminalKey.End,
        Key.Insert => TerminalKey.Insert, Key.Delete => TerminalKey.Delete,
        Key.PageUp => TerminalKey.PageUp, Key.PageDown => TerminalKey.PageDown,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
        _ => null
    };
    protected override void OnKeyUp(KeyEventArgs e) { _modifiers = e.KeyModifiers; base.OnKeyUp(e); }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    { _modifiers = KeyModifiers.None; _preedit = ""; ReleaseReportedMouse(); _dragging = false; base.OnLostFocus(e); InvalidateVisual(); }
    private void Send(byte[] bytes) { _offset = 0; ClearSelection(); Input?.Invoke(bytes); MarkDirty(); }

    private (int X, int Y) Cell(Point point) => (Math.Clamp((int)((point.X - PaddingSize) / _cellWidth), 0, _snapshot.Columns - 1),
        Math.Clamp((int)((point.Y - PaddingSize) / _cellHeight), 0, _snapshot.Rows - 1));
    private bool ReportsMouse(KeyModifiers modifiers) => _snapshot.MouseMode != 0 && !modifiers.HasFlag(KeyModifiers.Shift) && _offset == 0;
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus();
        var point = e.GetCurrentPoint(this); var cell = Cell(point.Position);
        int button = point.Properties.PointerUpdateKind switch
        { PointerUpdateKind.LeftButtonPressed => 0, PointerUpdateKind.MiddleButtonPressed => 1, PointerUpdateKind.RightButtonPressed => 2, _ => -1 };
        if (button < 0) return;
        if (ReportsMouse(e.KeyModifiers))
        { _reportedButton = button; ReportMouse(button, cell, e.KeyModifiers); e.Pointer.Capture(this); e.Handled = true; }
        else if (button == 0)
        { _allSelected = false; _anchor = cell; _end = cell; _dragging = true; e.Pointer.Capture(this); e.Handled = true; InvalidateVisual(); }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var cell = Cell(e.GetPosition(this));
        if (_dragging) { _end = cell; InvalidateVisual(); e.Handled = true; }
        else if (ReportsMouse(e.KeyModifiers) && (_snapshot.MouseMode == 1003 || _snapshot.MouseMode == 1002 && _reportedButton >= 0))
        { ReportMouse(_reportedButton < 0 ? 3 : _reportedButton, cell, e.KeyModifiers, motion: true); e.Handled = true; }
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_reportedButton >= 0) { ReportMouse(_reportedButton, Cell(e.GetPosition(this)), e.KeyModifiers, release: true); _reportedButton = -1; e.Handled = true; }
        if (_dragging) { _end = Cell(e.GetPosition(this)); _dragging = false; e.Handled = true; InvalidateVisual(); }
        e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { ReleaseReportedMouse(); _dragging = false; base.OnPointerCaptureLost(e); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ReportsMouse(e.KeyModifiers))
            for (int i = 0; i < Math.Clamp((int)Math.Ceiling(Math.Abs(e.Delta.Y)), 1, 12); i++) ReportMouse(e.Delta.Y > 0 ? 64 : 65, Cell(e.GetPosition(this)), e.KeyModifiers);
        else Scroll((int)Math.Round(e.Delta.Y * 3));
        e.Handled = true;
    }
    private void ReportMouse(int button, (int X, int Y) cell, KeyModifiers modifiers, bool release = false, bool motion = false)
    {
        bool shift = modifiers.HasFlag(KeyModifiers.Shift), alt = modifiers.HasFlag(KeyModifiers.Alt), control = modifiers.HasFlag(KeyModifiers.Control);
        if (_snapshot.SgrMouse)
            Input?.Invoke(TerminalInputEncoder.EncodeMouse(button, cell.X + 1, cell.Y + 1, release, shift, alt, control, motion));
        else if (cell.X < 223 && cell.Y < 223)
        {
            int code = (release ? 3 : button) + (motion ? 32 : 0) + (shift ? 4 : 0) + (alt ? 8 : 0) + (control ? 16 : 0);
            Input?.Invoke([27, 91, 77, (byte)(32 + code), (byte)(33 + cell.X), (byte)(33 + cell.Y)]);
        }
    }
    private void ReleaseReportedMouse()
    { if (_reportedButton >= 0) { ReportMouse(_reportedButton, (_snapshot.CursorColumn, _snapshot.CursorRow), KeyModifiers.None, release: true); _reportedButton = -1; } }
    private void Scroll(int amount) { _offset = Math.Clamp(_offset + amount, 0, _snapshot.ScrollbackCount); ClearSelection(); MarkDirty(); Refresh(); }
    private void ClearSelection() { _allSelected = false; _anchor = _end = null; InvalidateVisual(); }
    private void SelectAll() { _allSelected = true; _anchor = (0, 0); _end = (_snapshot.Columns - 1, _snapshot.Rows - 1); InvalidateVisual(); }
    private bool IsSelected(int x, int y)
    {
        if (_allSelected) return true;
        if (_anchor is not { } a || _end is not { } b) return false;
        int start = a.Y * _snapshot.Columns + a.X, end = b.Y * _snapshot.Columns + b.X, current = y * _snapshot.Columns + x;
        return current >= Math.Min(start, end) && current <= Math.Max(start, end);
    }
    private async Task CopyAsync()
    {
        try
        {
            if (_anchor is { } a && _end is { } b && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                string text;
                lock (_engineGate) text = _allSelected ? _engine.GetAllText() : _engine.GetSelectionText(a.X, a.Y, b.X, b.Y, _offset);
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception error) { Error?.Invoke("복사 실패: " + error.Message); }
    }
    private async Task PasteAsync()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { } value)
                Send(TerminalInputEncoder.EncodePaste(value, Snapshot().BracketedPaste));
        }
        catch (Exception error) { Error?.Invoke("붙여넣기 실패: " + error.Message); }
    }

    private sealed class TerminalImeClient(TerminalSurface owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override Rect CursorRectangle => owner.CursorRectangle;
        public override TextSelection Selection { get; set; }
        public override void SetPreeditText(string? preeditText) { owner._preedit = preeditText ?? ""; owner.InvalidateVisual(); }
        public void CursorMoved() => RaiseCursorRectangleChanged();
    }
}
