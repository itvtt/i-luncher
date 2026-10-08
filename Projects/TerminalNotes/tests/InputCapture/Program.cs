using TerminalNotes.Services;

var submissions = new List<string>();
var capture = new TerminalInputCapture();
Require(TerminalInputCapture.IsTerminalResponse("\x1b[12;40R\x1b[0n\x1b[I") &&
    TerminalInputCapture.IsTerminalResponse("\x1b]10;rgb:ffff/0000/1234\x1b\\") &&
    TerminalInputCapture.IsTerminalResponse("\x1b]4;12;rgb:ff/00/aa\x07"), "Native CSI/OSC reports were classified as user input.");
Require(!TerminalInputCapture.IsTerminalResponse("\r") && !TerminalInputCapture.IsTerminalResponse("\x1b[D") &&
    !TerminalInputCapture.IsTerminalResponse("hello\x1b[0n") && !TerminalInputCapture.IsTerminalResponse("\x1b]10;?\x07"),
    "User input or a query was mistaken for a terminal response.");
capture.Submitted += submissions.Add;

Require(TerminalInputCapture.IsTerminalResponse("\x1b[<35;12;9M\x1b[<64;12;9M\x1b[<0;12;9m") &&
    !TerminalInputCapture.IsTerminalResponse("\x1b[<35;12;9M실제 입력"), "Mouse reports must not conceal typed text.");
capture.Observe("\x1b[<35;12;9M\x1b[<64;12;9M");
Require(!capture.HasPendingInput, "Mouse movement and scrolling left an ambiguous draft.");
capture.Observe("초안");
capture.Observe("\x1b[<0;12;9m");
Require(capture.HasPendingInput, "Mouse reporting discarded the existing draft.");
capture.Clear();

capture.Observe("helo");
capture.Observe("\x1b[D");
capture.Observe("l\r");
Require(submissions.SequenceEqual(["hello"]), "Cursor editing was not reconstructed.");

capture.Observe("ab");
capture.Observe("\x1b[D한\x1b[H\x1b[3~\x1b[F\x7f\r");
Require(submissions[^1] == "한", "Home/end/delete, backspace, or Unicode editing failed.");

capture.Observe("\x1b[20");
capture.Observe("0~first\n둘\r셋\x1b[20");
capture.Observe("1~\r");
Require(submissions[^1] == "first\n둘\r셋", "Split bracketed multiline paste was not preserved.");

int beforeCancel = submissions.Count;
capture.Observe("do-not-record\x03\r");
Require(submissions.Count == beforeCancel, "Ctrl+C did not clear the pending submission.");

capture.Observe("secret\x1b[Aedited\r");
Require(submissions.Count == beforeCancel, "History recall was guessed and reported.");
capture.Observe("known command\r");
Require(submissions[^1] == "known command", "Capture did not recover after an ambiguous submission.");

capture.Observe("tui\x1b[?1hvalue\r");
Require(submissions[^1] == "known command", "An unknown terminal mode sequence was reported.");

capture.Observe("\x1b[?1;2c\x1b[12;40R\x1b[0n\x1b[I\x1b[O");
capture.Observe("after device reports\r");
Require(submissions[^1] == "after device reports", "Terminal device or focus responses poisoned manual capture.");

capture.Observe("\x1b[?1h");
capture.PrepareForProgrammaticSubmission();
capture.Observe("after programmatic submit\r");
Require(submissions[^1] == "after programmatic submit", "Empty stale ambiguity survived a programmatic submission boundary.");

capture.Observe("partial user text\x1b[?1h");
capture.PrepareForProgrammaticSubmission();
capture.Observe("\r");
Require(submissions[^1] == "after programmatic submit", "Programmatic preparation discarded or trusted a real partial user line.");

var textUsage = new TerminalTokenUsage();
int textChanges = 0;
textUsage.Changed += () => textChanges++;
textUsage.Observe("\x1b[32mTokens used: 1,200\x1b[0m\r\n");
textUsage.Observe("Tokens used: 1,200\r\n");
Require(textUsage.TotalTokens == 1200, "Explicit total token output was not parsed.");
Require(textUsage.Label == "토큰 1.2K" && textChanges == 1,
    "A duplicate redraw changed or accumulated the token count.");

var turnUsage = new TerminalTokenUsage();
int turnChanges = 0;
turnUsage.Changed += () => turnChanges++;
const string firstTurn = "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":100,\"output_tokens\":20}}";
turnUsage.Observe(firstTurn[..31]);
turnUsage.Observe(firstTurn[31..] + "\n");
turnUsage.Observe(firstTurn + "\n");
turnUsage.Observe("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":30,\"output_tokens\":5}}\n");
Require(turnUsage.InputTokens == 130 && turnUsage.OutputTokens == 25 && turnUsage.TotalTokens == 155,
    "Known turn counters were not accumulated correctly.");
Require(turnChanges == 2, "A duplicate turn event was accumulated.");
turnUsage.Observe("{\"type\":\"thread.started\",\"thread_id\":\"next-cli-exec\"}\n");
turnUsage.Observe(firstTurn + "\n");
Require(turnUsage.InputTokens == 230 && turnUsage.OutputTokens == 45 && turnUsage.TotalTokens == 275,
    "An identical turn counter in a new thread scope was incorrectly suppressed.");

var countUsage = new TerminalTokenUsage();
countUsage.Observe("{\"type\":\"thread.token_count\",\"payload\":{\"info\":{\"total_token_usage\":{\"input_tokens\":1000,\"output_tokens\":200,\"total_tokens\":1200}}}}\n");
Require(countUsage.InputTokens == 1000 && countUsage.OutputTokens == 200 && countUsage.TotalTokens == 1200,
    "Nested token_count counters were not parsed.");

var openCodeUsage = new TerminalTokenUsage();
const string openCodeStep = "{\"type\":\"step_finish\",\"sessionID\":\"session-one\",\"part\":{\"id\":\"part-one\",\"tokens\":{\"input\":123,\"output\":45,\"reasoning\":0,\"cache\":{\"read\":0,\"write\":0}}}}";
openCodeUsage.Observe(openCodeStep + "\n");
openCodeUsage.Observe(openCodeStep + "\n");
Require(openCodeUsage.TotalTokens == 168, "A duplicate OpenCode step_finish part was accumulated.");
openCodeUsage.Observe(openCodeStep.Replace("session-one", "session-two", StringComparison.Ordinal) + "\n");
Require(openCodeUsage.InputTokens == 246 && openCodeUsage.OutputTokens == 90 && openCodeUsage.TotalTokens == 336,
    "An identical OpenCode part id in a new session was suppressed.");

var unknown = new TerminalTokenUsage();
unknown.Observe("ordinary terminal output: 42 files\r\n{not json}\r\n");
unknown.Observe("{\"type\":\"thread.token_count\",\"usage\":{\"total_tokens\":null,\"input_tokens\":\"12\"}}\n");
Require(unknown.TotalTokens is null && unknown.Label == "토큰 정보 없음",
    "Unrelated output invented a token count.");

Console.WriteLine("Input capture and token usage tests passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
