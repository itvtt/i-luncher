using System.Collections.ObjectModel;

namespace TerminalNotes.TerminalEngine;

public readonly record struct TerminalColor(byte R, byte G, byte B, bool IsDefault = false)
{
    public static TerminalColor Default => new(0, 0, 0, true);
}

public readonly record struct TerminalStyle(
    TerminalColor Foreground, TerminalColor Background, bool Bold = false, bool Italic = false,
    bool Underline = false, bool Inverse = false, bool Strike = false)
{
    public static TerminalStyle Default => new(TerminalColor.Default, TerminalColor.Default);
}

public sealed record TerminalCell(string Text, int Width, TerminalStyle Style);

public sealed class TerminalLine
{
    public IReadOnlyList<TerminalCell> Cells { get; }
    public bool Wrapped { get; }
    public TerminalLine(IEnumerable<TerminalCell> cells, bool wrapped = false)
    {
        Cells = new ReadOnlyCollection<TerminalCell>(cells.ToArray());
        Wrapped = wrapped;
    }
}

public sealed class TerminalSnapshot
{
    public int Columns { get; }
    public int Rows { get; }
    public int CursorColumn { get; }
    public int CursorRow { get; }
    public bool CursorVisible { get; }
    public bool AlternateScreen { get; }
    public bool ApplicationCursorKeys { get; }
    public bool BracketedPaste { get; }
    public int MouseMode { get; }
    public bool SgrMouse { get; }
    public int ScrollbackCount { get; }
    public string? Title { get; }
    public long Version { get; }
    public IReadOnlyList<TerminalLine> Lines { get; }
    internal TerminalSnapshot(int columns, int rows, int cursorColumn, int cursorRow, bool cursorVisible,
        bool alternateScreen, bool applicationCursorKeys, bool bracketedPaste, int mouseMode, bool sgrMouse,
        int scrollbackCount, string? title, long version, IEnumerable<TerminalLine> lines)
    {
        Columns = columns; Rows = rows; CursorColumn = cursorColumn; CursorRow = cursorRow;
        CursorVisible = cursorVisible; AlternateScreen = alternateScreen; ApplicationCursorKeys = applicationCursorKeys;
        BracketedPaste = bracketedPaste; MouseMode = mouseMode; SgrMouse = sgrMouse;
        ScrollbackCount = scrollbackCount; Title = title; Version = version;
        Lines = new ReadOnlyCollection<TerminalLine>(lines.ToArray());
    }
}

public enum TerminalKey { Enter, Backspace, Tab, Escape, Up, Down, Left, Right, Home, End, Insert, Delete, PageUp, PageDown, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12 }
