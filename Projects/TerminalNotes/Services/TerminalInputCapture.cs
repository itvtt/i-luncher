using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerminalNotes.Services;

/// <summary>
/// Reconstructs ordinary line-editor submissions from terminal input. It is
/// deliberately conservative: history recall and unknown control sequences
/// make the current submission ambiguous, so it is not reported.
/// </summary>
public sealed class TerminalInputCapture
{
    private const string PasteStart = "\x1b[200~";
    private const string PasteEnd = "\x1b[201~";
    private readonly List<string> _text = [];
    private string _pending = "";
    private int _cursor;
    private bool _inBracketedPaste;
    private bool _ambiguous;
    private bool _ignoreLineFeed;

    /// <summary>
    /// True while this capture cannot prove that the terminal composer is empty.
    /// Ambiguous controls and incomplete pastes count as pending input so a
    /// programmatic request never risks being appended to an existing draft.
    /// </summary>
    public bool HasPendingInput => _text.Count != 0 || _pending.Length != 0 || _inBracketedPaste || _ambiguous;

    public event Action<string>? Submitted;

    public void Clear()
    {
        Reset();
        _ignoreLineFeed = false;
    }

    /// <summary>Whole xterm device, pointer, focus and color reports cannot contain composer text.</summary>
    public static bool IsTerminalResponse(string input)
    {
        if (input.Length == 0) return false;
        int index = 0;
        while (index < input.Length)
        {
            if (input.AsSpan(index).StartsWith("\x1b]", StringComparison.Ordinal))
            {
                int bell = input.IndexOf('\x07', index + 2);
                int st = input.IndexOf("\x1b\\", index + 2, StringComparison.Ordinal);
                int terminator = bell < 0 ? st : st < 0 ? bell : Math.Min(bell, st);
                if (terminator < 0 || terminator - index > 128) return false;
                string report = input[(index + 2)..terminator];
                if (!Regex.IsMatch(report, @"\A(?:4;[0-9]+;|1[012];)rgb:[0-9a-fA-F]{1,4}/[0-9a-fA-F]{1,4}/[0-9a-fA-F]{1,4}\z",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return false;
                index = terminator + (terminator == bell ? 1 : 2);
                continue;
            }
            if (index + 2 >= input.Length || input[index] != '\x1b' || input[index + 1] != '[') return false;
            int end = index + 2;
            while (end < input.Length && input[end] is < '@' or > '~') end++;
            if (end >= input.Length || !IsDeviceResponse(input[index..(end + 1)])) return false;
            index = end + 1;
        }
        return true;
    }

    /// <summary>
    /// Clears stale terminal-response ambiguity before an application-driven
    /// submission, but never discards text or an in-progress user paste.
    /// </summary>
    public void PrepareForProgrammaticSubmission()
    {
        if (_text.Count != 0 || _inBracketedPaste) return;
        _ambiguous = false;
        _pending = "";
        _cursor = 0;
        _ignoreLineFeed = false;
    }

    public void Observe(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0) return;

        string data = _pending + input;
        _pending = "";
        int index = 0;
        while (index < data.Length)
        {
            if (_inBracketedPaste)
            {
                int end = data.IndexOf(PasteEnd, index, StringComparison.Ordinal);
                if (end >= 0)
                {
                    Insert(data[index..end]);
                    _inBracketedPaste = false;
                    index = end + PasteEnd.Length;
                    continue;
                }

                int retained = LongestSuffixPrefix(data, index, PasteEnd);
                int insertEnd = data.Length - retained;
                Insert(data[index..insertEnd]);
                if (retained > 0) _pending = data[insertEnd..];
                return;
            }

            char current = data[index];
            if (current == '\x1b')
            {
                int consumed = ConsumeEscape(data, index);
                if (consumed == 0) _pending = data[index..];
                if (consumed <= 0) return;
                index += consumed;
                continue;
            }

            if (current is '\r' or '\n')
            {
                if (current == '\n' && _ignoreLineFeed)
                {
                    _ignoreLineFeed = false;
                    index++;
                    continue;
                }

                _ignoreLineFeed = current == '\r';
                Submit();
                index++;
                continue;
            }

            _ignoreLineFeed = false;
            switch (current)
            {
                case '\x03': // Ctrl+C
                    Reset();
                    index++;
                    break;
                case '\b':
                case '\x7f':
                    Backspace();
                    index++;
                    break;
                case '\x01': // Ctrl+A
                    _cursor = 0;
                    index++;
                    break;
                case '\x05': // Ctrl+E
                    _cursor = _text.Count;
                    index++;
                    break;
                case '\x02': // Ctrl+B
                    Move(-1);
                    index++;
                    break;
                case '\x06': // Ctrl+F
                    Move(1);
                    index++;
                    break;
                case '\x04': // Ctrl+D
                    Delete();
                    index++;
                    break;
                default:
                    if (char.IsControl(current))
                    {
                        // Shell editing modes and TUI controls cannot be reconstructed
                        // safely from bytes alone.
                        _ambiguous = true;
                        index++;
                    }
                    else
                    {
                        int next = index + 1;
                        while (next < data.Length && data[next] != '\x1b' && !char.IsControl(data[next])) next++;
                        Insert(data[index..next]);
                        index = next;
                    }
                    break;
            }
        }
    }

    private int ConsumeEscape(string data, int index)
    {
        if (IsPartialAtEnd(data, index, PasteStart)) return 0;
        if (data.AsSpan(index).StartsWith(PasteStart, StringComparison.Ordinal))
        {
            _inBracketedPaste = true;
            return PasteStart.Length;
        }

        if (index + 1 >= data.Length) return 0;
        if (data[index + 1] != '[')
        {
            // Alt chords and application escape protocols are not line-editor text.
            _ambiguous = true;
            return Math.Min(2, data.Length - index);
        }

        int end = index + 2;
        while (end < data.Length && data[end] is < '@' or > '~') end++;
        if (end >= data.Length) return 0;

        string sequence = data[index..(end + 1)];
        switch (sequence)
        {
            case "\x1b[D": Move(-1); break;
            case "\x1b[C": Move(1); break;
            case "\x1b[H":
            case "\x1b[1~":
            case "\x1b[7~": _cursor = 0; break;
            case "\x1b[F":
            case "\x1b[4~":
            case "\x1b[8~": _cursor = _text.Count; break;
            case "\x1b[3~": Delete(); break;
            case "\x1b[I":
            case "\x1b[O":
                // Focus reports are generated by the terminal, not typed text.
                break;
            case "\x1b[A":
            case "\x1b[B":
                // The recalled history text is never present in the input stream.
                _ambiguous = true;
                break;
            default:
                if (!IsDeviceResponse(sequence)) _ambiguous = true;
                break;
        }

        return end + 1 - index;
    }

    private void Insert(string value)
    {
        if (value.Length == 0) return;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            _text.Insert(_cursor++, element);
        }
    }

    private void Move(int delta) => _cursor = Math.Clamp(_cursor + delta, 0, _text.Count);

    private void Backspace()
    {
        if (_cursor == 0) return;
        _text.RemoveAt(--_cursor);
    }

    private void Delete()
    {
        if (_cursor < _text.Count) _text.RemoveAt(_cursor);
    }

    private void Submit()
    {
        string value = string.Concat(_text);
        bool report = !_ambiguous && !string.IsNullOrWhiteSpace(value);
        Reset();
        if (report) Submitted?.Invoke(value);
    }

    private void Reset()
    {
        _text.Clear();
        _cursor = 0;
        _ambiguous = false;
        _inBracketedPaste = false;
        _pending = "";
    }

    private static bool IsPartialAtEnd(string value, int index, string expected)
    {
        ReadOnlySpan<char> remaining = value.AsSpan(index);
        return remaining.Length < expected.Length && expected.AsSpan().StartsWith(remaining, StringComparison.Ordinal);
    }

    private static bool IsDeviceResponse(string sequence)
    {
        // OpenCode enables SGR mouse reporting. Moving, clicking, or scrolling
        // over its view must not leave a permanent ambiguous composer draft.
        if (Regex.IsMatch(sequence, @"\A\x1b\[<[0-9]+;[0-9]+;[0-9]+[Mm]\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return true;
        char final = sequence[^1];
        if (final is not ('c' or 'R' or 'n' or 't' or 'x' or 'y' or 'I' or 'O')) return false;
        // Device attributes, cursor/status/window reports, and mode reports use
        // only numeric parameters and CSI parameter/intermediate punctuation.
        return sequence.AsSpan(2, sequence.Length - 3).IndexOfAnyExcept("0123456789;:?><=$".AsSpan()) < 0;
    }

    private static int LongestSuffixPrefix(string value, int start, string expected)
    {
        int maximum = Math.Min(expected.Length - 1, value.Length - start);
        for (int length = maximum; length > 0; length--)
        {
            if (value.AsSpan(value.Length - length).SequenceEqual(expected.AsSpan(0, length))) return length;
        }
        return 0;
    }
}
