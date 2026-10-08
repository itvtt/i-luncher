using System.Globalization;
using System.Text;

namespace TerminalNotes.TerminalEngine;

/// <summary>A small, dependency-free VT screen emulator for common VT100/VT220/xterm applications.</summary>
/// <remarks>
/// VT support is intentionally partial. Callers must serialize Feed, Resize, and snapshot/selection access;
/// this type does not synchronize concurrent operations internally.
/// </remarks>
public sealed class TerminalEmulator
{
    private const int MaxControlLength = 8192;
    private const int MaxCellTextLength = 128;
    private readonly int _scrollbackLimit;
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly List<MutableLine> _normal = [];
    private readonly List<MutableLine> _alternate = [];
    private readonly HashSet<int> _tabStops = [];
    private List<MutableLine> _active;
    private readonly StringBuilder _control = new();
    private readonly StringBuilder _graphemePending = new();
    private ParserState _parser;
    private TerminalStyle _style = TerminalStyle.Default;
    private TerminalStyle _savedStyle = TerminalStyle.Default;
    private int _columns, _rows, _x, _y, _savedX, _savedY, _topMargin, _bottomMargin;
    private int _lastCellColumn = -1, _lastCellRow = -1;
    private bool _feeding, _dirty;
    private bool _wrapPending, _originMode, _autoWrap = true, _savedWrap = true;
    private bool _joinNextRune;
    private bool _cursorVisible = true, _alternateScreen, _applicationCursorKeys, _bracketedPaste, _sgrMouse;
    private bool _g0DecSpecialGraphics, _g1DecSpecialGraphics, _useG1Charset;
    private int _mouseMode;
    private string? _title;
    private long _version;

    public event Action<string>? Response;
    public event Action? Changed;

    public TerminalEmulator(int columns = 80, int rows = 24, int scrollbackLimit = 5000)
    {
        if (scrollbackLimit < 0) throw new ArgumentOutOfRangeException(nameof(scrollbackLimit));
        _scrollbackLimit = scrollbackLimit;
        _columns = Math.Clamp(columns, 2, 500);
        _rows = Math.Clamp(rows, 2, 300);
        _topMargin = 0; _bottomMargin = _rows - 1;
        ResetTabStops();
        for (int i = 0; i < _rows; i++) { _normal.Add(NewLine()); _alternate.Add(NewLine()); }
        _active = _normal;
    }

    public void Feed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _feeding = true;
        try { foreach (Rune rune in text.EnumerateRunes()) ProcessRune(rune); }
        finally { _feeding = false; PublishChange(); }
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        char[] buffer = new char[Math.Max(4, Encoding.UTF8.GetMaxCharCount(bytes.Length))];
        _decoder.Convert(bytes, buffer, flush: false, out _, out int charsUsed, out _);
        _feeding = true;
        try
        {
            for (int i = 0; i < charsUsed; i++)
            {
                char c = buffer[i];
                if (char.IsHighSurrogate(c) && i + 1 < charsUsed && char.IsLowSurrogate(buffer[i + 1]))
                    ProcessRune(new Rune(c, buffer[++i]));
                else if (Rune.TryCreate(c, out Rune rune)) ProcessRune(rune);
                else ProcessRune(Rune.ReplacementChar);
            }
        }
        finally { _feeding = false; PublishChange(); }
    }

    public void Resize(int columns, int rows)
    {
        int cols = Math.Clamp(columns, 2, 500), height = Math.Clamp(rows, 2, 300);
        if (cols == _columns && height == _rows) return;
        int oldRows = _rows, oldCols = _columns;
        int oldNormalHistory = Math.Max(0, _normal.Count - oldRows);
        int resizedCursorRow = _y;
        int resizedSavedRow = _savedY;
        if (height < oldRows)
        {
            resizedCursorRow = Math.Max(0, _y - (oldRows - height));
            resizedSavedRow = Math.Max(0, _savedY - (oldRows - height));
        }
        else if (height > oldRows && !_alternateScreen)
        {
            int exposedHistory = Math.Min(oldNormalHistory, height - oldRows);
            resizedCursorRow = _y + exposedHistory;
            resizedSavedRow = _savedY + exposedHistory;
        }
        _columns = cols; _rows = height;
        _tabStops.RemoveWhere(stop => stop >= cols);
        if (cols > oldCols)
            for (int column = ((oldCols / 8) + 1) * 8; column < cols; column += 8) _tabStops.Add(column);
        ResizeBuffer(_normal, oldRows, oldCols, height, cols);
        ResizeBuffer(_alternate, oldRows, oldCols, height, cols);
        _x = Math.Clamp(_x, 0, cols - 1); _y = Math.Clamp(resizedCursorRow, 0, height - 1);
        _savedX = Math.Clamp(_savedX, 0, cols - 1); _savedY = Math.Clamp(resizedSavedRow, 0, height - 1);
        _topMargin = 0; _bottomMargin = height - 1; _wrapPending = false;
        ChangedNow();
    }

    public TerminalSnapshot GetSnapshot(int scrollbackOffset = 0)
    {
        int history = _alternateScreen ? 0 : Math.Max(0, _active.Count - _rows);
        int offset = Math.Clamp(scrollbackOffset, 0, history);
        int start = Math.Max(0, _active.Count - _rows - offset);
        var lines = new List<TerminalLine>(_rows);
        for (int i = 0; i < _rows; i++)
            lines.Add(ToImmutableLine(start + i < _active.Count ? _active[start + i] : NewLine()));
        int cursorRow = _y;
        return new TerminalSnapshot(_columns, _rows, _x, cursorRow, _cursorVisible, _alternateScreen,
            _applicationCursorKeys, _bracketedPaste, _mouseMode, _sgrMouse, history, _title, _version, lines);
    }

    /// <summary>Returns the active buffer as text in one pass, joining soft-wrapped rows and trimming row padding.</summary>
    public string GetAllText()
    {
        var rowTexts = new List<(string Text, bool Wrapped)>(_active.Count);
        int lastNonEmpty = -1;
        for (int rowIndex = 0; rowIndex < _active.Count; rowIndex++)
        {
            MutableLine line = _active[rowIndex];
            int lastCell = line.FindLastIndex(cell => cell.Text.Length > 0 && cell.Text != " ");
            var row = new StringBuilder(Math.Max(0, lastCell + 1));
            for (int column = 0; column <= lastCell; column++)
                if (line[column].Width != 0) row.Append(line[column].Text);
            string text = row.ToString();
            rowTexts.Add((text, line.Wrapped));
            if (text.Length != 0) lastNonEmpty = rowIndex;
        }

        if (lastNonEmpty < 0) return string.Empty;
        var result = new StringBuilder();
        for (int rowIndex = 0; rowIndex <= lastNonEmpty; rowIndex++)
        {
            (string text, bool wrapped) = rowTexts[rowIndex];
            result.Append(text);
            if (rowIndex < lastNonEmpty && !wrapped) result.AppendLine();
        }
        return result.ToString();
    }

    public string GetSelectionText(int startColumn, int startRow, int endColumn, int endRow, int scrollbackOffset = 0)
    {
        TerminalSnapshot snapshot = GetSnapshot(scrollbackOffset);
        int startIndex = Math.Clamp(startRow, 0, snapshot.Rows - 1) * snapshot.Columns + Math.Clamp(startColumn, 0, snapshot.Columns - 1);
        int endIndex = Math.Clamp(endRow, 0, snapshot.Rows - 1) * snapshot.Columns + Math.Clamp(endColumn, 0, snapshot.Columns - 1);
        if (startIndex > endIndex) (startIndex, endIndex) = (endIndex, startIndex);
        var result = new StringBuilder();
        int firstRow = startIndex / snapshot.Columns, firstColumn = startIndex % snapshot.Columns;
        if (snapshot.Lines[firstRow].Cells[firstColumn].Width == 0 && firstColumn > 0) startIndex--;
        int lastRow = endIndex / snapshot.Columns, lastColumn = endIndex % snapshot.Columns;
        if (snapshot.Lines[lastRow].Cells[lastColumn].Width == 0 && lastColumn > 0) endIndex--;
        for (int flat = startIndex; flat <= endIndex;)
        {
            int row = flat / snapshot.Columns, col = flat % snapshot.Columns;
            TerminalCell cell = snapshot.Lines[row].Cells[col];
            if (cell.Width != 0) result.Append(cell.Text);
            flat += Math.Max(cell.Width, 1);
            if (flat <= endIndex && flat / snapshot.Columns > row && !snapshot.Lines[row].Wrapped)
                result.AppendLine();
        }
        return result.ToString();
    }

    public void Clear()
    {
        _normal.Clear(); _alternate.Clear();
        for (int i = 0; i < _rows; i++) { _normal.Add(NewLine()); _alternate.Add(NewLine()); }
        _active = _alternateScreen ? _alternate : _normal;
        _x = _y = 0; _wrapPending = false; _topMargin = 0; _bottomMargin = _rows - 1;
        _joinNextRune = false; _graphemePending.Clear(); _lastCellColumn = _lastCellRow = -1;
        ChangedNow();
    }

    private void ProcessRune(Rune rune)
    {
        if (_parser is ParserState.DiscardCsi or ParserState.DiscardOsc or ParserState.DiscardOscEscape or ParserState.DiscardString or ParserState.DiscardStringEscape)
        {
            ProcessDiscardedControl(rune);
            return;
        }
        if (_parser == ParserState.Osc || _parser == ParserState.OscEscape || _parser == ParserState.String || _parser == ParserState.StringEscape)
        {
            ProcessStringControl(rune);
            return;
        }
        if (_parser == ParserState.Csi)
        {
            if (rune.Value is >= 0x40 and <= 0x7e) { ExecuteCsi((char)rune.Value, _control.ToString()); _control.Clear(); _parser = ParserState.Ground; ChangedNow(); }
            else if (rune.Value >= 0x20 && rune.Value <= 0x3f) AppendControl(rune);
            else if (rune.Value == 0x1b) { _control.Clear(); _parser = ParserState.Escape; }
            return;
        }
        if (_parser is ParserState.CharsetDesignationG0 or ParserState.CharsetDesignationG1)
        {
            bool decSpecial = rune.Value == '0';
            if (_parser == ParserState.CharsetDesignationG0) _g0DecSpecialGraphics = decSpecial;
            else _g1DecSpecialGraphics = decSpecial;
            _parser = ParserState.Ground;
            ChangedNow();
            return;
        }
        if (_parser == ParserState.Escape)
        {
            _parser = ParserState.Ground;
            switch (rune.Value)
            {
                case '[': _control.Clear(); _parser = ParserState.Csi; break;
                case ']': _control.Clear(); _parser = ParserState.Osc; break;
                case 'P': case '^': case '_': _control.Clear(); _parser = ParserState.String; break;
                case '(': _parser = ParserState.CharsetDesignationG0; break;
                case ')': _parser = ParserState.CharsetDesignationG1; break;
                case 'H': _tabStops.Add(_x); break;
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': LineFeed(false); break;
                case 'E': _x = 0; LineFeed(false); break;
                case 'M': ReverseIndex(); break;
                case 'c': Reset(); break;
                case '=': break;
                case '>': break;
            }
            ChangedNow(); return;
        }
        if (rune.Value == 0x1b) { FlushPendingGrapheme(); _parser = ParserState.Escape; return; }
        if (rune.Value < 0x20 || rune.Value == 0x7f) { HandleControl((char)rune.Value); return; }
        PutRune(rune);
    }

    private void HandleControl(char c)
    {
        FlushPendingGrapheme();
        switch (c)
        {
            case '\a': break;
            case '\b': _x = Math.Max(0, _x - 1); _wrapPending = false; break;
            case '\t': _x = NextTabStop(_x); _wrapPending = false; break;
            case '\n': case '\v': case '\f': LineFeed(false); break;
            case '\r': _x = 0; _wrapPending = false; break;
            case '\x0e': _useG1Charset = true; break;
            case '\x0f': _useG1Charset = false; break;
            case '\x05': Response?.Invoke("TerminalNotes terminal"); break;
        }
        ChangedNow();
    }

    private void ProcessStringControl(Rune rune)
    {
        if (_parser is ParserState.OscEscape or ParserState.StringEscape)
        {
            if (rune.Value == '\\') FinishStringControl();
            else { AppendControl(new Rune(0x1b)); AppendControl(rune); _parser = _parser == ParserState.OscEscape ? ParserState.Osc : ParserState.String; }
            return;
        }
        if (_parser == ParserState.Osc && rune.Value == 0x07) { FinishStringControl(); return; }
        if (rune.Value == 0x1b) { _parser = _parser == ParserState.Osc ? ParserState.OscEscape : ParserState.StringEscape; return; }
        AppendControl(rune);
    }

    private void ProcessDiscardedControl(Rune rune)
    {
        switch (_parser)
        {
            case ParserState.DiscardCsi:
                if (rune.Value is >= 0x40 and <= 0x7e) _parser = ParserState.Ground;
                else if (rune.Value == 0x1b) _parser = ParserState.Escape;
                break;
            case ParserState.DiscardOsc:
                if (rune.Value == 0x07) _parser = ParserState.Ground;
                else if (rune.Value == 0x1b) _parser = ParserState.DiscardOscEscape;
                break;
            case ParserState.DiscardOscEscape:
                _parser = rune.Value == '\\' ? ParserState.Ground : rune.Value == 0x1b ? ParserState.DiscardOscEscape : ParserState.DiscardOsc;
                break;
            case ParserState.DiscardString:
                if (rune.Value == 0x1b) _parser = ParserState.DiscardStringEscape;
                break;
            case ParserState.DiscardStringEscape:
                _parser = rune.Value == '\\' ? ParserState.Ground : rune.Value == 0x1b ? ParserState.DiscardStringEscape : ParserState.DiscardString;
                break;
        }
    }

    private void FinishStringControl()
    {
        if (_parser is ParserState.Osc or ParserState.OscEscape)
        {
            string payload = _control.ToString();
            int separator = payload.IndexOf(';');
            if (separator > 0 && int.TryParse(payload[..separator], out int command) && command is 0 or 2)
            {
                _title = payload[(separator + 1)..];
                ChangedNow();
            }
        }
        _control.Clear(); _parser = ParserState.Ground;
    }

    private void AppendControl(Rune rune)
    {
        if (_control.Length < MaxControlLength) { _control.Append(rune.ToString()); return; }
        _control.Clear();
        _parser = _parser switch
        {
            ParserState.Csi => ParserState.DiscardCsi,
            ParserState.Osc or ParserState.OscEscape => ParserState.DiscardOsc,
            _ => ParserState.DiscardString
        };
    }

    private void PutRune(Rune rune)
    {
        if (rune.Value is >= 0x5f and <= 0x7e && (_useG1Charset ? _g1DecSpecialGraphics : _g0DecSpecialGraphics))
            rune = DecSpecialGraphics(rune);
        int width = RuneWidth(rune);
        if (width == 0)
        {
            int col = _lastCellColumn;
            int row = _lastCellRow;
            if (row >= 0 && row < _rows && col >= 0 && col < _columns)
                AppendCellText(_active[ScreenIndex(row)][col], rune);
            if (rune.Value == 0x200d) _joinNextRune = true;
            return;
        }
        if (_joinNextRune && _lastCellRow >= 0)
        {
            AppendCellText(_active[ScreenIndex(_lastCellRow)][_lastCellColumn], rune);
            _joinNextRune = false;
            return;
        }
        FlushPendingGrapheme();
        if (_wrapPending)
        {
            if (_autoWrap) { _active[ScreenIndex(_y)].Wrapped = true; _x = 0; LineFeed(true); }
            _wrapPending = false;
        }
        if (width == 2 && _x == _columns - 1)
        {
            if (_autoWrap) { _active[ScreenIndex(_y)].Wrapped = true; _x = 0; LineFeed(true); }
            else width = 1;
        }
        var line = _active[ScreenIndex(_y)];
        _lastCellRow = _y; _lastCellColumn = _x;
        ClearCellAt(line, _x);
        line[_x] = new MutableCell(rune.ToString(), width, _style);
        if (width == 2) line[_x + 1] = new MutableCell("", 0, _style);
        if (_x + width >= _columns) { _x = _columns - 1; _wrapPending = true; }
        else _x += width;
        ChangedNow();
    }

    private void FlushPendingGrapheme()
    {
        _joinNextRune = false;
        _lastCellColumn = _lastCellRow = -1;
    }
    private static void AppendCellText(MutableCell cell, Rune rune)
    {
        string text = rune.ToString();
        if (cell.Text.Length + text.Length <= MaxCellTextLength) cell.Text += text;
    }

    private void ExecuteCsi(char final, string raw)
    {
        char prefix = raw.Length > 0 && raw[0] is '?' or '>' or '!' or '<' or '=' or ':' ? raw[0] : '\0';
        if (prefix == '?' && final is not ('h' or 'l') || prefix == '>' && final != 'c' ||
            prefix == '!' && (final != 'p' || raw != "!") || prefix is '<' or '=' or ':')
            return;
        bool privateMode = raw.StartsWith('?');
        bool bang = raw.StartsWith('!');
        string cleaned = raw.TrimStart('?', '>', '!', '<', '=', ':');
        int[] p = cleaned.Length == 0 ? [] : cleaned.Split(';').Select(ParseNumber).ToArray();
        int n = P(p, 0, 1, zeroIsOne: true);
        switch (final)
        {
            case 'A': _y = Math.Max(_originMode ? _topMargin : 0, _y - n); break;
            case 'B': _y = Math.Min(_originMode ? _bottomMargin : _rows - 1, _y + n); break;
            case 'C': _x = Math.Min(_columns - 1, _x + n); break;
            case 'D': _x = Math.Max(0, _x - n); break;
            case 'E': _y = Math.Min(_rows - 1, _y + n); _x = 0; break;
            case 'F': _y = Math.Max(0, _y - n); _x = 0; break;
            case 'G': case '`': _x = Math.Clamp(P(p, 0, 1) - 1, 0, _columns - 1); break;
            case 'd': _y = Math.Clamp(P(p, 0, 1) - 1 + (_originMode ? _topMargin : 0), _originMode ? _topMargin : 0, _originMode ? _bottomMargin : _rows - 1); break;
            case 'H': case 'f': _y = Math.Clamp(P(p, 0, 1) - 1 + (_originMode ? _topMargin : 0), _originMode ? _topMargin : 0, _originMode ? _bottomMargin : _rows - 1); _x = Math.Clamp(P(p, 1, 1) - 1, 0, _columns - 1); break;
            case 'J': EraseDisplay(P(p, 0, 0)); break;
            case 'K': EraseLine(P(p, 0, 0)); break;
            case 'X': EraseChars(n); break;
            case '@': InsertChars(n); break;
            case 'P': DeleteChars(n); break;
            case 'L': InsertLines(n); break;
            case 'M': DeleteLines(n); break;
            case 'S': ScrollUp(n, false); break;
            case 'T': ScrollDown(n); break;
            case 'I': for (int i = 0; i < n; i++) _x = NextTabStop(_x); break;
            case 'Z': for (int i = 0; i < n; i++) _x = PreviousTabStop(_x); break;
            case 'm': ApplySgr(cleaned); break;
            case 'r': _topMargin = Math.Clamp(P(p, 0, 1) - 1, 0, _rows - 1); _bottomMargin = Math.Clamp(P(p, 1, _rows) - 1, _topMargin, _rows - 1); _x = 0; _y = _originMode ? _topMargin : 0; break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'h': case 'l': SetModes(p, privateMode, final == 'h'); break;
            case 'g':
                if (P(p, 0, 0) == 3) _tabStops.Clear();
                else if (P(p, 0, 0) == 0) _tabStops.Remove(_x);
                break;
            case 'n': if (P(p, 0, 0) == 5) Response?.Invoke("\x1b[0n"); else if (P(p, 0, 0) == 6) Response?.Invoke($"\x1b[{_y + 1};{_x + 1}R"); break;
            case 'c':
                if (prefix == '>' && (p.Length == 0 || p.Length == 1 && p[0] == 0)) Response?.Invoke("\x1b[>0;1;0c");
                else if (prefix == '\0' && (p.Length == 0 || p.Length == 1 && p[0] == 0)) Response?.Invoke("\x1b[?1;2c");
                break;
            case 'q': if (raw.EndsWith('"')) { } break;
            case 'p': if (bang) Reset(); break;
        }
        _x = Math.Clamp(_x, 0, _columns - 1); _y = Math.Clamp(_y, 0, _rows - 1); _wrapPending = false;
    }

    private void SetModes(int[] values, bool dec, bool enabled)
    {
        foreach (int mode in values)
        {
            if (!dec) { if (mode == 4) { } continue; }
            switch (mode)
            {
                case 6: _originMode = enabled; _x = 0; _y = enabled ? _topMargin : 0; break;
                case 7: _autoWrap = enabled; break;
                case 25: _cursorVisible = enabled; break;
                case 1: _applicationCursorKeys = enabled; break;
                case 47: case 1047: case 1049: SetAlternate(enabled, mode == 1049, enabled && mode is 1047 or 1049); break;
                case 1000: case 1002: case 1003: _mouseMode = enabled ? mode : (_mouseMode == mode ? 0 : _mouseMode); break;
                case 1006: _sgrMouse = enabled; break;
                case 2004: _bracketedPaste = enabled; break;
            }
        }
    }

    private void SetAlternate(bool enabled, bool saveRestore, bool clearOnEnter)
    {
        if (enabled == _alternateScreen) return;
        if (enabled)
        {
            if (saveRestore) SaveCursor();
            _alternateScreen = true; _active = _alternate; _x = _y = 0;
            if (clearOnEnter || _active.Count != _rows) { _active.Clear(); for (int i = 0; i < _rows; i++) _active.Add(NewLine()); }
        }
        else
        {
            _alternateScreen = false; _active = _normal;
            if (saveRestore) RestoreCursor();
        }
        _wrapPending = false;
    }

    private void ApplySgr(string parameters)
    {
        if (!parameters.Contains(':'))
        {
            ApplySgr(parameters.Length == 0 ? [] : parameters.Split(';').Select(ParseNumber).ToArray());
            return;
        }
        foreach (string item in parameters.Split(';'))
        {
            string[] fields = item.Split(':');
            int[] values = fields.Select(ParseNumber).ToArray();
            if (values.Length >= 3 && values[0] is 38 or 48 && values[1] == 2)
            {
                int[] rgb = fields.Skip(2).Where(field => field.Length != 0).Select(ParseNumber).TakeLast(3).ToArray();
                if (rgb.Length == 3)
                {
                    TerminalColor color = new((byte)Math.Clamp(rgb[0], 0, 255), (byte)Math.Clamp(rgb[1], 0, 255), (byte)Math.Clamp(rgb[2], 0, 255));
                    _style = values[0] == 38 ? _style with { Foreground = color } : _style with { Background = color };
                }
            }
            else if (values.Length >= 3 && values[0] is 38 or 48 && values[1] == 5)
            {
                TerminalColor color = Palette(values[^1]);
                _style = values[0] == 38 ? _style with { Foreground = color } : _style with { Background = color };
            }
            else ApplySgr(values);
        }
    }

    private void ApplySgr(int[] p)
    {
        if (p.Length == 0) p = [0];
        for (int i = 0; i < p.Length; i++)
        {
            int value = p[i];
            switch (value)
            {
                case 0: _style = TerminalStyle.Default; break;
                case 1: _style = _style with { Bold = true }; break;
                case 3: _style = _style with { Italic = true }; break;
                case 4: _style = _style with { Underline = true }; break;
                case 7: _style = _style with { Inverse = true }; break;
                case 9: _style = _style with { Strike = true }; break;
                case 22: _style = _style with { Bold = false }; break;
                case 23: _style = _style with { Italic = false }; break;
                case 24: _style = _style with { Underline = false }; break;
                case 27: _style = _style with { Inverse = false }; break;
                case 29: _style = _style with { Strike = false }; break;
                case >= 30 and <= 37: _style = _style with { Foreground = AnsiColor(value - 30, false) }; break;
                case >= 40 and <= 47: _style = _style with { Background = AnsiColor(value - 40, false) }; break;
                case >= 90 and <= 97: _style = _style with { Foreground = AnsiColor(value - 90 + 8, false) }; break;
                case >= 100 and <= 107: _style = _style with { Background = AnsiColor(value - 100 + 8, false) }; break;
                case 39: _style = _style with { Foreground = TerminalColor.Default }; break;
                case 49: _style = _style with { Background = TerminalColor.Default }; break;
                case 38: case 48:
                    bool foreground = value == 38;
                    if (i + 1 < p.Length && p[i + 1] == 5 && i + 2 < p.Length)
                    {
                        TerminalColor color = Palette(p[i + 2]); i += 2;
                        _style = foreground ? _style with { Foreground = color } : _style with { Background = color };
                    }
                    else if (i + 4 < p.Length && p[i + 1] == 2)
                    {
                        TerminalColor color = new((byte)Math.Clamp(p[i + 2], 0, 255), (byte)Math.Clamp(p[i + 3], 0, 255), (byte)Math.Clamp(p[i + 4], 0, 255)); i += 4;
                        _style = foreground ? _style with { Foreground = color } : _style with { Background = color };
                    }
                    break;
            }
        }
    }

    private void EraseDisplay(int mode)
    {
        if (mode == 3)
        {
            if (!_alternateScreen) while (_active.Count > _rows) _active.RemoveAt(0);
            return;
        }
        if (mode == 2)
        {
            for (int row = 0; row < _rows; row++) EraseLineAt(ScreenIndex(row), 0, _columns - 1);
            return;
        }
        int current = ScreenIndex(_y);
        if (mode == 0)
        {
            EraseLineAt(current, _x, _columns - 1);
            for (int i = current + 1; i < _active.Count; i++) EraseLineAt(i, 0, _columns - 1);
        }
        else if (mode == 1)
        {
            for (int i = ScreenIndex(0); i < current; i++) EraseLineAt(i, 0, _columns - 1);
            EraseLineAt(current, 0, _x);
        }
    }

    private void EraseLine(int mode) => EraseLineAt(ScreenIndex(_y), mode == 0 ? _x : 0, mode == 1 ? _x : _columns - 1);
    private void EraseChars(int count) => EraseLineAt(ScreenIndex(_y), _x, Math.Min(_columns - 1, _x + count - 1));
    private void EraseLineAt(int index, int from, int to)
    {
        if (index < 0 || index >= _active.Count) return;
        MutableLine line = _active[index];
        for (int col = Math.Max(0, from); col <= Math.Min(to, _columns - 1); col++)
        {
            ClearCellAt(line, col);
            line[col] = new MutableCell(" ", 1, _style with { Inverse = false });
        }
        line.Wrapped = false;
    }

    private void InsertChars(int count)
    {
        MutableLine line = _active[ScreenIndex(_y)];
        for (int i = _columns - 1; i >= _x + count; i--) line[i] = line[i - count];
        for (int i = _x; i < Math.Min(_columns, _x + count); i++) line[i] = new MutableCell(" ", 1, _style);
        SanitizeWideCells(line);
    }
    private void DeleteChars(int count)
    {
        MutableLine line = _active[ScreenIndex(_y)];
        for (int i = _x; i < _columns; i++) line[i] = i + count < _columns ? line[i + count] : new MutableCell(" ", 1, _style);
        SanitizeWideCells(line);
    }
    private void InsertLines(int count)
    {
        if (_y < _topMargin || _y > _bottomMargin) return;
        int baseIndex = _alternateScreen ? 0 : _active.Count - _rows;
        count = Math.Min(count, _bottomMargin - _y + 1);
        for (int i = 0; i < count; i++) { _active.Insert(baseIndex + _y, NewLine()); _active.RemoveAt(baseIndex + _bottomMargin + 1); }
    }
    private void DeleteLines(int count)
    {
        if (_y < _topMargin || _y > _bottomMargin) return;
        int baseIndex = _alternateScreen ? 0 : _active.Count - _rows;
        count = Math.Min(count, _bottomMargin - _y + 1);
        for (int i = 0; i < count; i++) { _active.RemoveAt(baseIndex + _y); _active.Insert(baseIndex + _bottomMargin, NewLine()); }
    }
    private void ScrollUp(int count, bool saveHistory)
    {
        count = Math.Min(count, _bottomMargin - _topMargin + 1);
        for (int i = 0; i < count; i++)
        {
            int top = ScreenIndex(_topMargin), bottom = ScreenIndex(_bottomMargin);
            MutableLine removed = _active[top];
            _active.RemoveAt(top); _active.Insert(bottom, NewLine());
            if (saveHistory && !_alternateScreen && _topMargin == 0 && _bottomMargin == _rows - 1)
            {
                _normal.Insert(Math.Max(0, _normal.Count - _rows), removed);
                TrimHistory();
            }
        }
    }
    private void ScrollDown(int count)
    {
        count = Math.Min(count, _bottomMargin - _topMargin + 1);
        for (int i = 0; i < count; i++) { _active.RemoveAt(ScreenIndex(_bottomMargin)); _active.Insert(ScreenIndex(_topMargin), NewLine()); }
    }

    private void LineFeed(bool carriageReturn)
    {
        if (carriageReturn) _x = 0;
        if (_y == _bottomMargin) ScrollUp(1, saveHistory: true);
        else if (_y < _rows - 1) _y++;
    }
    private void ReverseIndex()
    {
        if (_y == _topMargin) ScrollDown(1);
        else _y = Math.Max(0, _y - 1);
    }
    private int ScreenIndex(int row) => _alternateScreen ? Math.Clamp(row, 0, _active.Count - 1) : Math.Clamp(_active.Count - _rows + row, 0, _active.Count - 1);

    private void SaveCursor() { _savedX = _x; _savedY = _y; _savedStyle = _style; _savedWrap = _autoWrap; }
    private void RestoreCursor() { _x = Math.Clamp(_savedX, 0, _columns - 1); _y = Math.Clamp(_savedY, 0, _rows - 1); _style = _savedStyle; _autoWrap = _savedWrap; }
    private void Reset()
    {
        _style = TerminalStyle.Default; _originMode = false; _autoWrap = true; _cursorVisible = true;
        _applicationCursorKeys = false; _bracketedPaste = false; _mouseMode = 0; _sgrMouse = false;
        _g0DecSpecialGraphics = _g1DecSpecialGraphics = _useG1Charset = false;
        ResetTabStops();
        _alternateScreen = false; _active = _normal; _title = null; Clear();
    }

    private void ResetTabStops()
    {
        _tabStops.Clear();
        for (int column = 8; column < _columns; column += 8) _tabStops.Add(column);
    }

    private int NextTabStop(int column)
    {
        int next = _columns - 1;
        foreach (int stop in _tabStops)
            if (stop > column && stop < next) next = stop;
        return next;
    }

    private int PreviousTabStop(int column)
    {
        int previous = 0;
        foreach (int stop in _tabStops)
            if (stop < column && stop > previous) previous = stop;
        return previous;
    }

    private static Rune DecSpecialGraphics(Rune rune) => rune.Value switch
    {
        0x5f => new Rune(' '), 0x60 => new Rune('◆'), 0x61 => new Rune('▒'),
        0x62 => new Rune('␉'), 0x63 => new Rune('␌'), 0x64 => new Rune('␍'),
        0x65 => new Rune('␊'), 0x66 => new Rune('°'), 0x67 => new Rune('±'),
        0x68 => new Rune('␤'), 0x69 => new Rune('␋'), 0x6a => new Rune('┘'),
        0x6b => new Rune('┐'), 0x6c => new Rune('┌'), 0x6d => new Rune('└'),
        0x6e => new Rune('┼'), 0x6f => new Rune('⎺'), 0x70 => new Rune('⎻'),
        0x71 => new Rune('─'), 0x72 => new Rune('⎼'), 0x73 => new Rune('⎽'),
        0x74 => new Rune('├'), 0x75 => new Rune('┤'), 0x76 => new Rune('┴'),
        0x77 => new Rune('┬'), 0x78 => new Rune('│'), 0x79 => new Rune('≤'),
        0x7a => new Rune('≥'), 0x7b => new Rune('π'), 0x7c => new Rune('≠'),
        0x7d => new Rune('£'), 0x7e => new Rune('·'), _ => rune
    };

    private void ResizeBuffer(List<MutableLine> buffer, int oldRows, int oldCols, int rows, int cols)
    {
        int screenStart = Math.Max(0, buffer.Count - oldRows);
        if (cols < oldCols)
        {
            for (int i = 0; i < buffer.Count; i++)
            {
                MutableLine line = buffer[i];
                if (cols < line.Count && line[cols].Width == 0 && cols > 0) line[cols - 1] = new MutableCell(" ", 1, _style);
                if (line.Count > cols) line.RemoveRange(cols, line.Count - cols);
                Pad(line, cols);
            }
        }
        else for (int i = 0; i < buffer.Count; i++) Pad(buffer[i], cols);
        if (rows < oldRows)
        {
            int remove = Math.Min(oldRows - rows, Math.Max(0, buffer.Count - rows));
            if (buffer == _alternate) remove = Math.Min(remove, buffer.Count - rows);
            if (remove > 0) buffer.RemoveRange(screenStart, remove);
        }
        else if (rows > oldRows)
        {
            int add = rows - oldRows;
            if (buffer == _normal)
            {
                int history = Math.Max(0, buffer.Count - oldRows);
                if (history >= add) { /* expose existing scrollback rows as screen */ }
                else for (int i = history; i < add; i++) buffer.Add(NewLine());
            }
            else for (int i = 0; i < add; i++) buffer.Add(NewLine());
        }
        if (buffer.Count < rows) for (int i = buffer.Count; i < rows; i++) buffer.Add(NewLine());
        if (buffer == _alternate && buffer.Count > rows) buffer.RemoveRange(0, buffer.Count - rows);
    }

    private void TrimHistory()
    {
        while (_normal.Count > _rows + _scrollbackLimit) _normal.RemoveAt(0);
    }
    private MutableLine NewLine() { var line = new MutableLine(); for (int i = 0; i < _columns; i++) line.Add(new MutableCell(" ", 1, _style with { Inverse = false })); return line; }
    private static TerminalLine ToImmutableLine(MutableLine line) => new(line.Select(c => new TerminalCell(c.Text, c.Width, c.Style)), line.Wrapped);
    private static void Pad(List<MutableCell> line, int columns) { while (line.Count < columns) line.Add(new MutableCell(" ", 1, TerminalStyle.Default)); }
    private static void SanitizeWideCells(List<MutableCell> line) { for (int i = 0; i < line.Count; i++) if (line[i].Width == 2 && (i + 1 >= line.Count || line[i + 1].Width != 0)) line[i] = new MutableCell(" ", 1, line[i].Style); else if (line[i].Width == 0 && (i == 0 || line[i - 1].Width != 2)) line[i] = new MutableCell(" ", 1, line[i].Style); }
    private static void ClearCellAt(List<MutableCell> line, int x) { if (line[x].Width == 0 && x > 0) line[x - 1] = new MutableCell(" ", 1, line[x - 1].Style); if (line[x].Width == 2 && x + 1 < line.Count) line[x + 1] = new MutableCell(" ", 1, line[x + 1].Style); }
    private static int ParseNumber(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? Math.Clamp(result, 0, 100000) : 0;
    private static int P(int[] values, int index, int fallback, bool zeroIsOne = false) { int value = index < values.Length ? values[index] : fallback; return zeroIsOne && value == 0 ? 1 : value; }
    private void ChangedNow() { _dirty = true; if (!_feeding) PublishChange(); }
    private void PublishChange() { if (!_dirty) return; _dirty = false; _version++; Changed?.Invoke(); }

    private static TerminalColor AnsiColor(int index, bool bright)
    {
        int[,] colors = { {0,0,0},{205,0,0},{0,205,0},{205,205,0},{0,0,238},{205,0,205},{0,205,205},{229,229,229},{127,127,127},{255,0,0},{0,255,0},{255,255,0},{92,92,255},{255,0,255},{0,255,255},{255,255,255} };
        int i = Math.Clamp(index, 0, 15); return new((byte)colors[i,0], (byte)colors[i,1], (byte)colors[i,2]);
    }
    private static TerminalColor Palette(int index)
    {
        index = Math.Clamp(index, 0, 255);
        if (index < 16) { int[,] baseColors = { {0,0,0},{205,0,0},{0,205,0},{205,205,0},{0,0,238},{205,0,205},{0,205,205},{229,229,229},{127,127,127},{255,0,0},{0,255,0},{255,255,0},{92,92,255},{255,0,255},{0,255,255},{255,255,255} }; return new((byte)baseColors[index,0], (byte)baseColors[index,1], (byte)baseColors[index,2]); }
        if (index >= 232) { byte gray = (byte)(8 + (index - 232) * 10); return new(gray, gray, gray); }
        int cube = index - 16; return new((byte)(cube / 36 == 0 ? 0 : 55 + cube / 36 % 6 * 40), (byte)(cube / 6 % 6 == 0 ? 0 : 55 + cube / 6 % 6 * 40), (byte)(cube % 6 == 0 ? 0 : 55 + cube % 6 * 40));
    }
    private static int RuneWidth(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        int value = rune.Value;
        return value >= 0x1100 && (value <= 0x115f || value is 0x2329 or 0x232a || value is >= 0x2e80 and <= 0xa4cf || value is >= 0xac00 and <= 0xd7a3 || value is >= 0xf900 and <= 0xfaff || value is >= 0xfe10 and <= 0xfe6f || value is >= 0xff00 and <= 0xff60 || value is >= 0xffe0 and <= 0xffe6 || value is >= 0x1f300 and <= 0x1faff || value is >= 0x20000 and <= 0x3fffd) ? 2 : 1;
    }

    private enum ParserState { Ground, Escape, CharsetDesignationG0, CharsetDesignationG1, Csi, Osc, OscEscape, String, StringEscape, DiscardCsi, DiscardOsc, DiscardOscEscape, DiscardString, DiscardStringEscape }
    private sealed class MutableCell(string text, int width, TerminalStyle style) { public string Text = text; public int Width = width; public TerminalStyle Style = style; }
    private sealed class MutableLine : List<MutableCell> { public bool Wrapped; }
}
