using System.Text;
using System.Diagnostics;
using TerminalNotes.TerminalEngine;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string Row(TerminalSnapshot snapshot, int row) => string.Concat(snapshot.Lines[row].Cells.Select(cell => cell.Text));

void Run()
{
    VerifyChunkBoundaries();
    VerifyCharacterSetsAndTabs();
    VerifyScreenStylesAndUnicode();
    VerifyAlternateScreenAndScrollback();
    VerifyInputEncoding();
    VerifyRepliesAndControlBounds();
    VerifyFuzzBounds();
    Console.WriteLine("PASS chunked UTF-8 and VT parsing equivalence");
    Console.WriteLine("PASS G0/G1 ASCII and DEC graphics plus tab stops/navigation");
    Console.WriteLine("PASS cursor, erase, SGR, Unicode width and combining text");
    Console.WriteLine("PASS alternate screen isolation and scrollback snapshot offsets");
    Console.WriteLine("PASS input key, text, paste, and mouse encodings");
    Console.WriteLine("PASS bounded control strings, recovery, and device replies");
    Console.WriteLine("PASS deterministic parser fuzz bounds");
    MeasureMixedOutputThroughput();
}

void VerifyCharacterSetsAndTabs()
{
    var terminal = new TerminalEmulator(24, 4);
    terminal.Feed("\x1b(0lqk\x1b(B");
    Check(Row(terminal.GetSnapshot(), 0).StartsWith("┌─┐", StringComparison.Ordinal),
        "ESC ( 0 DEC special graphics designation did not draw a box, or its final byte leaked to the screen.");
    terminal.Feed("\x1b)0\x0elqk\u000fABC");
    string selectedCharsetRow = Row(terminal.GetSnapshot(), 0);
    Check(selectedCharsetRow.Substring(3, 6) == "┌─┐ABC",
        "SO/SI did not select G1/G0; row codepoints: " + string.Join(",", selectedCharsetRow.EnumerateRunes().Select(rune => rune.Value.ToString("X4"))));
    Check(!terminal.GetSnapshot().ApplicationCursorKeys, "SO/SI must not toggle application cursor keys.");
    terminal.Feed("\x1b)B\u000eABC");
    Check(Row(terminal.GetSnapshot(), 0).Substring(9, 3) == "ABC",
        "ESC ) B should restore ASCII G1 and consume its designation byte.");

    var tabs = new TerminalEmulator(20, 3);
    tabs.Feed("a\tb");
    Check(tabs.GetSnapshot().CursorColumn == 9 && Row(tabs.GetSnapshot(), 0)[8] == 'b',
        "Default HT did not advance to the next 8-column tab stop.");
    tabs.Feed("\x1b[1;4H\x1bH\x1b[1;1H\tX");
    Check(tabs.GetSnapshot().CursorColumn == 4 && Row(tabs.GetSnapshot(), 0)[3] == 'X',
        "ESC H did not set a custom tab stop at the cursor.");
    tabs.Feed("\x1b[1;4H\x1b[0g\x1b[1;1H\tY");
    Check(tabs.GetSnapshot().CursorColumn == 9 && Row(tabs.GetSnapshot(), 0)[8] == 'Y',
        "CSI 0 g did not clear the current custom tab stop.");
    tabs.Feed("\x1b[3g\x1b[1;1H\x1b[IZ");
    Check(tabs.GetSnapshot().CursorColumn == 19 && Row(tabs.GetSnapshot(), 0)[19] == 'Z',
        "CSI 3 g / CHT did not clear stops and clamp forward navigation at the last column.");
    tabs.Feed("\x1b[1;18H\x1b[ZQ");
    Check(tabs.GetSnapshot().CursorColumn == 1 && Row(tabs.GetSnapshot(), 0)[0] == 'Q',
        "CBT did not move back to column zero when no tab stops remain.");
    tabs.Feed("\x05");
    Check(tabs.GetSnapshot().Lines.Count == 3, "ENQ changed the terminal screen unexpectedly.");
}

void MeasureMixedOutputThroughput()
{
    const int measuredBytes = 1024 * 1024;
    const string sample = "\x1b[32mbuild✓\x1b[0m 한🙂\r\n";
    var payloadBuilder = new StringBuilder(measuredBytes + 64);
    int sampleBytes = Encoding.UTF8.GetByteCount(sample), builtBytes = 0;
    while (builtBytes < measuredBytes) { payloadBuilder.Append(sample); builtBytes += sampleBytes; }
    byte[] payload = Encoding.UTF8.GetBytes(payloadBuilder.ToString());
    var terminal = new TerminalEmulator(120, 36, scrollbackLimit: 500);
    var timer = Stopwatch.StartNew();
    terminal.Feed(payload);
    timer.Stop();
    Console.WriteLine($"MEASURED Feed mixed ANSI/Unicode: {payload.Length:N0} bytes in {timer.Elapsed.TotalMilliseconds:N1} ms ({payload.Length / Math.Max(timer.Elapsed.TotalSeconds, 0.000001) / (1024 * 1024):N2} MiB/s); scrollback={terminal.GetSnapshot().ScrollbackCount}");
}

void VerifyChunkBoundaries()
{
    const string fixture = "A한🙂e\u0301\r\n\x1b[31;1mred\x1b[0m\x1b[2;4H!\x1b]2;terminal title\x07";
    byte[] bytes = Encoding.UTF8.GetBytes(fixture);
    var all = new TerminalEmulator(20, 4);
    int changedEvents = 0;
    all.Changed += () => changedEvents++;
    all.Feed(bytes);
    Check(changedEvents == 1, "A single Feed call should publish one batched Changed event.");
    var bytewise = new TerminalEmulator(20, 4);
    foreach (byte value in bytes) bytewise.Feed(new[] { value });
    TerminalSnapshot expected = all.GetSnapshot(), actual = bytewise.GetSnapshot();
    Check(expected.Title == actual.Title && expected.CursorColumn == actual.CursorColumn && expected.CursorRow == actual.CursorRow,
        "Chunked parser metadata diverged.");
    for (int row = 0; row < expected.Rows; row++)
    {
        Check(Row(expected, row) == Row(actual, row), $"UTF-8 chunk boundary changed row {row}.");
        for (int col = 0; col < expected.Columns; col++)
            Check(expected.Lines[row].Cells[col] == actual.Lines[row].Cells[col], $"UTF-8 chunk boundary changed cell {row},{col}.");
    }
}

void VerifyScreenStylesAndUnicode()
{
    var terminal = new TerminalEmulator(14, 3);
    terminal.Feed("A한e\u0301🙂\x1b[2;5H\x1b[38;2;10;20;30;48;5;196;3mX\x1b[0m\x1b[2K");
    TerminalSnapshot snapshot = terminal.GetSnapshot();
    Check(snapshot.Lines[0].Cells[0].Text == "A" && snapshot.Lines[0].Cells[1].Text == "한" && snapshot.Lines[0].Cells[1].Width == 2 && snapshot.Lines[0].Cells[2].Width == 0,
        "Wide CJK cell and continuation were not represented correctly.");
    Check(snapshot.Lines[0].Cells[3].Text == "e\u0301", "Combining mark was detached from its base cell.");
    Check(snapshot.Lines[0].Cells[4].Text == "🙂" && snapshot.Lines[0].Cells[4].Width == 2,
        "Supplementary-plane emoji width was not retained.");
    Check(Row(snapshot, 1).All(character => character == ' '), "CSI erase-line did not clear the selected row.");
    terminal.Feed("\x1b[1;1H\x1b[38;2;10;20;30;48;5;196;3mX");
    TerminalCell styled = terminal.GetSnapshot().Lines[0].Cells[0];
    Check(styled.Text == "X" && styled.Style.Italic && styled.Style.Foreground == new TerminalColor(10, 20, 30) && styled.Style.Background == new TerminalColor(255, 0, 0),
        "Truecolor/indexed SGR style was not applied.");
    string selectedWide = terminal.GetSelectionText(1, 0, 1, 0);
    Check(selectedWide == "한", "Selection starting on a wide-cell continuation lost the glyph.");
    Check(terminal.GetSelectionText(2, 0, 2, 0) == "한", "Selection ending/starting on a wide-cell continuation split the glyph.");
    terminal.Feed("\x1b[1;1H\x1b[38:2::40:50:60mC");
    Check(terminal.GetSnapshot().Lines[0].Cells[0].Style.Foreground == new TerminalColor(40, 50, 60), "Colon-form truecolor SGR was not applied.");
}

void VerifyAlternateScreenAndScrollback()
{
    var terminal = new TerminalEmulator(8, 2, 4);
    terminal.Feed("base\r\nmain");
    string before = Row(terminal.GetSnapshot(), 1);
    terminal.Feed("\x1b[?1049hALT");
    TerminalSnapshot alternate = terminal.GetSnapshot();
    Check(alternate.AlternateScreen && Row(alternate, 0).StartsWith("ALT", StringComparison.Ordinal), "Alternate screen was not entered/cleared.");
    terminal.Feed("\x1b[?1049l");
    TerminalSnapshot restored = terminal.GetSnapshot();
    Check(!restored.AlternateScreen && Row(restored, 1) == before, "Leaving alternate screen did not restore the primary buffer.");
    terminal.Feed("\x1b[?1047hSTALE\x1b[?1047l\x1b[?1047h");
    Check(Row(terminal.GetSnapshot(), 0).All(character => character == ' '), "DEC 1047 re-entry did not clear its alternate buffer.");
    terminal.Feed("\x1b[?1047l");

    var scrolling = new TerminalEmulator(4, 2, 3);
    scrolling.Feed("a\r\nb\r\nc\r\nd\r\ne");
    TerminalSnapshot bottom = scrolling.GetSnapshot();
    TerminalSnapshot above = scrolling.GetSnapshot(1);
    Check(bottom.ScrollbackCount == 3 && Row(bottom, 1).StartsWith("e", StringComparison.Ordinal), "Scrollback did not retain the capped history and current screen.");
    Check(Row(above, 1).StartsWith("d", StringComparison.Ordinal), "Positive scrollback offset did not move the viewport upward.");
    string transcript = scrolling.GetAllText();
    Check(transcript.Contains("d", StringComparison.Ordinal) && transcript.Contains("e", StringComparison.Ordinal) &&
          transcript.Contains("a", StringComparison.Ordinal) && !transcript.EndsWith("\n", StringComparison.Ordinal),
        "GetAllText did not return the retained normal-buffer history with trimmed row padding.");
    var cappedTranscript = new TerminalEmulator(4, 2, 2);
    cappedTranscript.Feed("a\r\nb\r\nc\r\nd\r\ne");
    Check(!cappedTranscript.GetAllText().Contains("a", StringComparison.Ordinal),
        "GetAllText returned normal-buffer rows older than the configured scrollback cap.");
    var wrapped = new TerminalEmulator(4, 3);
    wrapped.Feed("abcdEF\r\nZ");
    Check(wrapped.GetAllText().StartsWith("abcdEF" + Environment.NewLine + "Z", StringComparison.Ordinal),
        "GetAllText inserted a newline between soft-wrapped rows or lost hard line breaks.");
    var resizedWrapped = new TerminalEmulator(4, 3);
    resizedWrapped.Feed("abcdEF");
    resizedWrapped.Resize(3, 3);
    Check(resizedWrapped.GetAllText().StartsWith("abcEF", StringComparison.Ordinal),
        "Shrinking the viewport discarded soft-wrap metadata from scrollback rows.");
    scrolling.Feed("\x1b[?1049hALT");
    Check(scrolling.GetAllText().StartsWith("ALT", StringComparison.Ordinal) &&
          !scrolling.GetAllText().Contains("d", StringComparison.Ordinal),
        "GetAllText included normal scrollback while the alternate screen was active.");
    scrolling.Feed("\x1b[?1049l");
    string current = Row(scrolling.GetSnapshot(), 1);
    scrolling.Feed("\x1b[3J");
    Check(Row(scrolling.GetSnapshot(), 1) == current && scrolling.GetSnapshot().ScrollbackCount == 0, "ED 3 should clear history while retaining the current screen.");
}

void VerifyInputEncoding()
{
    Check(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.Up, applicationCursorKeys: true)) == "\x1bOA", "Application cursor key was encoded incorrectly.");
    Check(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.Home)) == "\x1b[H" &&
          Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.Home, applicationCursorKeys: true)) == "\x1bOH" &&
          Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.End)) == "\x1b[F" &&
          Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.End, applicationCursorKeys: true)) == "\x1bOF",
        "Home/End did not follow normal/application cursor-key modes.");
    Check(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.F5, control: true)) == "\x1b[15;5~", "Modified function key was encoded incorrectly.");
    Check(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.Enter, alt: true)) == "\x1b\r" &&
          Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.Backspace, alt: true)) == "\x1b\x7f",
        "Alt+Enter/Backspace must preserve the key byte with a single ESC prefix.");
    Check(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeText("한", alt: true)) == "\x1b한", "Alt text did not prefix UTF-8 text exactly once.");
    Check(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeControl('c')) == "\x03" && Encoding.UTF8.GetString(TerminalInputEncoder.EncodeControl('?')) == "\x7f", "Control character encoding is incorrect.");
    Check(Encoding.UTF8.GetString(TerminalInputEncoder.EncodePaste("a\nb", bracketedPaste: true)) == "\x1b[200~a\nb\x1b[201~", "Bracketed paste framing is incorrect.");
    Check(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeMouse(0, 3, 2, release: true)) == "\x1b[<0;3;2m", "SGR mouse release must retain its button code.");
}

void VerifyRepliesAndControlBounds()
{
    var terminal = new TerminalEmulator(12, 3);
    var replies = new List<string>();
    terminal.Response += replies.Add;
    terminal.Feed("\x1b[5n\x1b[6n\x1b[c\x1b[0c\x1b[>c\x1b[>0c");
    Check(replies.SequenceEqual(["\x1b[0n", "\x1b[1;1R", "\x1b[?1;2c", "\x1b[?1;2c", "\x1b[>0;1;0c", "\x1b[>0;1;0c"]),
        "DSR and primary/secondary DA requests produced incorrect replies.");
    replies.Clear();
    terminal.Feed("\x1b[?1;2c\x1b[>0;1;0c\x1b[?6n\x1b[1c");
    Check(replies.Count == 0, "A DA response or unsupported/private request was answered again.");
    replies.Clear();
    var originTerminal = new TerminalEmulator(12, 10);
    originTerminal.Response += replies.Add;
    originTerminal.Feed("\x1b[4;8r\x1b[?6h\x1b[99;2H\x1b[6n\x1b[99d\x1b[6n");
    Check(replies.SequenceEqual(["\x1b[8;2R", "\x1b[8;2R"]), "Origin-mode CUP/VPA escaped the active scroll region.");
    var boundedCombining = new TerminalEmulator(4, 2);
    boundedCombining.Feed("A" + string.Concat(Enumerable.Repeat("\u0301", 10000)));
    Check(boundedCombining.GetSnapshot().Lines[0].Cells[0].Text.Length <= 128,
        "Combining/ZWJ accumulation was not bounded per cell.");
    var zwjControlBoundary = new TerminalEmulator(8, 2);
    zwjControlBoundary.Feed("A\u200d\rB");
    Check(zwjControlBoundary.GetSnapshot().Lines[0].Cells[0].Text == "B",
        "A ZWJ join incorrectly crossed a carriage-return control boundary.");
    var decstr = new TerminalEmulator(8, 2);
    decstr.Feed("KEEP\x1b[!1p");
    Check(Row(decstr.GetSnapshot(), 0).StartsWith("KEEP", StringComparison.Ordinal),
        "Unsupported parameterized DECSTR reset terminal contents.");
    var cursorResize = new TerminalEmulator(12, 3);
    cursorResize.Feed("a\r\nb\r\nc\r\nd\r\ne\x1b[2;1H");
    Check(cursorResize.GetSnapshot().CursorRow == 1,
        "Cursor row was incorrectly offset by scrollback history.");
    cursorResize.Resize(12, 4);
    Check(cursorResize.GetSnapshot().CursorRow == 2,
        "Growing the viewport did not preserve the cursor's physical line when exposing history.");
    cursorResize.Resize(12, 3);
    Check(cursorResize.GetSnapshot().CursorRow == 1,
        "Shrinking the viewport detached the cursor from its visible line.");
    var partialHistoryGrow = new TerminalEmulator(12, 3);
    partialHistoryGrow.Feed("a\r\nb\r\nc\r\nd\x1b[2;1H");
    Check(partialHistoryGrow.GetSnapshot().ScrollbackCount == 1 && partialHistoryGrow.GetSnapshot().CursorRow == 1,
        "Partial-history resize setup did not produce the expected cursor and history.");
    partialHistoryGrow.Resize(12, 5);
    Check(partialHistoryGrow.GetSnapshot().CursorRow == 2,
        "Growing beyond retained history did not shift the cursor by the exposed history rows.");
    var alternateResize = new TerminalEmulator(12, 5);
    alternateResize.Feed("\x1b[?1049h\x1b[4;1H X");
    Check(alternateResize.GetSnapshot().CursorRow == 3, "Alternate-screen resize setup did not position the cursor.");
    alternateResize.Resize(12, 3);
    Check(alternateResize.GetSnapshot().CursorRow == 1,
        "Shrinking the alternate screen did not shift the cursor with the removed top rows.");
    var partialScroll = new TerminalEmulator(4, 5);
    partialScroll.Feed("OUT0\r\nTOP1\r\nMID2\r\nBOT3\r\nOUT4\x1b[2;4r\x1b[4;1H\n");
    TerminalSnapshot region = partialScroll.GetSnapshot();
    Check(Row(region, 0).StartsWith("OUT0", StringComparison.Ordinal) && Row(region, 4).StartsWith("OUT4", StringComparison.Ordinal),
        "Scrolling a partial DECSTBM region shifted lines outside the region into scrollback.");
    partialScroll.Feed("\x1b[5;1H\x1b[99999999L\x1b[99999999M\x1b[99999999S\x1b[99999999T");
    Check(partialScroll.GetSnapshot().Lines.Count == 5, "Large CSI counts escaped the screen bounds.");
    terminal.Feed("\x1b]2;" + new string('x', 12000) + "\x07Z");
    Check(Row(terminal.GetSnapshot(), 0).StartsWith("Z", StringComparison.Ordinal), "Oversized OSC did not recover to ground state.");
    Check(terminal.GetSnapshot().Title is null, "Oversized OSC unexpectedly changed title.");
}

void VerifyFuzzBounds()
{
    var random = new Random(17461);
    var terminal = new TerminalEmulator(10, 5, 16);
    for (int i = 0; i < 3000; i++)
    {
        int choice = random.Next(6);
        string input = choice switch
        {
            0 => $"\x1b[{random.Next(0, 100)};{random.Next(0, 100)}H",
            1 => $"\x1b[{random.Next(0, 100)}{(char)random.Next(64, 127)}",
            2 => $"\x1b[?{random.Next(1, 2500)}{(random.Next(2) == 0 ? 'h' : 'l')}",
            3 => char.ConvertFromUtf32(random.Next(0x20, 0x10ffff)),
            4 => "\x1b[38;2;999;-8;256m",
            _ => "\x1b[" + new string('9', random.Next(0, 80)) + "m"
        };
        terminal.Feed(input);
        if (i % 10 == 0) terminal.Resize(random.Next(2, 100), random.Next(2, 40));
        TerminalSnapshot snapshot = terminal.GetSnapshot(random.Next(0, 100));
        Check(snapshot.Lines.Count == snapshot.Rows && snapshot.Lines.All(line => line.Cells.Count == snapshot.Columns), "Fuzzed snapshot dimensions escaped bounds.");
        Check(snapshot.CursorColumn >= 0 && snapshot.CursorColumn < snapshot.Columns && snapshot.CursorRow >= 0 && snapshot.CursorRow < snapshot.Rows,
            "Fuzzed cursor escaped screen bounds.");
    }
}

Run();
