using System.Text;

namespace TerminalNotes.TerminalEngine;

public static class TerminalInputEncoder
{
    public static byte[] EncodeKey(TerminalKey key, bool shift = false, bool alt = false,
        bool control = false, bool applicationCursorKeys = false)
    {
        string value;
        int modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);
        bool modified = modifier != 1;
        value = key switch
        {
            TerminalKey.Enter => alt ? "\x1b\r" : "\r",
            TerminalKey.Backspace => alt ? "\x1b\x7f" : "\x7f",
            TerminalKey.Tab => shift ? (modified ? $"\x1b[1;{modifier}Z" : "\x1b[Z") : (alt ? "\x1b\t" : "\t"),
            TerminalKey.Escape => alt ? "\x1b\x1b" : "\x1b",
            TerminalKey.Up => Cursor("A", "A", applicationCursorKeys, modifier),
            TerminalKey.Down => Cursor("B", "B", applicationCursorKeys, modifier),
            TerminalKey.Right => Cursor("C", "C", applicationCursorKeys, modifier),
            TerminalKey.Left => Cursor("D", "D", applicationCursorKeys, modifier),
            TerminalKey.Home => Cursor("H", "H", applicationCursorKeys, modifier),
            TerminalKey.End => Cursor("F", "F", applicationCursorKeys, modifier),
            TerminalKey.Insert => Tilde(2, modifier),
            TerminalKey.Delete => Tilde(3, modifier),
            TerminalKey.PageUp => Tilde(5, modifier),
            TerminalKey.PageDown => Tilde(6, modifier),
            >= TerminalKey.F1 and <= TerminalKey.F4 => Function(key, modifier),
            >= TerminalKey.F5 and <= TerminalKey.F12 => Tilde(key switch
            {
                TerminalKey.F5 => 15, TerminalKey.F6 => 17, TerminalKey.F7 => 18,
                TerminalKey.F8 => 19, TerminalKey.F9 => 20, TerminalKey.F10 => 21,
                TerminalKey.F11 => 23, _ => 24
            }, modifier),
            _ => ""
        };
        return Encoding.UTF8.GetBytes(value);
    }

    public static byte[] EncodeText(string text, bool alt = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (!alt || bytes.Length == 0) return bytes;
        byte[] prefixed = new byte[bytes.Length + 1];
        prefixed[0] = 0x1b;
        bytes.CopyTo(prefixed, 1);
        return prefixed;
    }

    /// <summary>Encodes conventional Ctrl+character input to its C0/DEL byte.</summary>
    public static byte[] EncodeControl(char character)
    {
        char value = char.ToUpperInvariant(character);
        int code = value switch
        {
            ' ' => 0,
            >= '@' and <= '_' => value - '@',
            '?' => 0x7f,
            _ => throw new ArgumentOutOfRangeException(nameof(character), "Control input supports @ through _ and ?.")
        };
        return [(byte)code];
    }

    public static byte[] EncodePaste(string text, bool bracketedPaste)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] content = Encoding.UTF8.GetBytes(text);
        if (!bracketedPaste) return content;
        byte[] result = new byte[content.Length + 12];
        Encoding.ASCII.GetBytes("\x1b[200~").CopyTo(result, 0);
        content.CopyTo(result, 6);
        Encoding.ASCII.GetBytes("\x1b[201~").CopyTo(result, 6 + content.Length);
        return result;
    }

    /// <summary>Encodes a one-based SGR mouse report; button 0-2, wheel 64-67.</summary>
    public static byte[] EncodeMouse(int button, int column, int row, bool release = false,
        bool shift = false, bool alt = false, bool control = false, bool motion = false)
    {
        int code = Math.Clamp(button, 0, 127) + (motion ? 32 : 0) + (shift ? 4 : 0) + (alt ? 8 : 0) + (control ? 16 : 0);
        return Encoding.ASCII.GetBytes($"\x1b[<{code};{Math.Max(1, column)};{Math.Max(1, row)}{(release ? 'm' : 'M')}");
    }

    private static string Cursor(string normal, string application, bool applicationMode, int modifier) =>
        modifier == 1 ? (applicationMode ? $"\x1bO{application}" : $"\x1b[{normal}") : $"\x1b[1;{modifier}{normal}";
    private static string Tilde(int number, int modifier) => modifier == 1 ? $"\x1b[{number}~" : $"\x1b[{number};{modifier}~";
    private static string TildeOrCursor(string final, int number, int modifier) => modifier == 1 ? $"\x1b[{final}" : $"\x1b[1;{modifier}{final}";
    private static string Function(TerminalKey key, int modifier)
    {
        char final = key switch { TerminalKey.F1 => 'P', TerminalKey.F2 => 'Q', TerminalKey.F3 => 'R', _ => 'S' };
        return modifier == 1 ? $"\x1bO{final}" : $"\x1b[1;{modifier}{final}";
    }
}
