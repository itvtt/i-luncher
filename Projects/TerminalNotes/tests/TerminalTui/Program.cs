using System.Text;
using System.Threading.Channels;
using TerminalNotes.TerminalEngine;
using TerminalNotes.TerminalTransport;

// Explicit acceptance check for an installed OpenCode executable. No model
// request is submitted; every home/config/data path belongs to this fixture.
if (args.Length != 1) { Console.Error.WriteLine("Usage: TerminalNotes.TerminalTuiTests <absolute OpenCode executable path>"); return 2; }
string executable = Path.GetFullPath(args[0]);
if (!File.Exists(executable)) throw new FileNotFoundException("OpenCode executable is missing.", executable);
string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes-Tui-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var environment = new Dictionary<string, string?>
{
    ["HOME"] = fixture, ["USERPROFILE"] = fixture,
    ["XDG_CONFIG_HOME"] = Path.Combine(fixture, "config"), ["XDG_DATA_HOME"] = Path.Combine(fixture, "data"),
    ["XDG_CACHE_HOME"] = Path.Combine(fixture, "cache"), ["XDG_STATE_HOME"] = Path.Combine(fixture, "state"),
    ["OPENCODE_CONFIG_DIR"] = Path.Combine(fixture, "config"), ["OPENCODE_CONFIG_CONTENT"] = "{\"plugin\":[]}",
    ["OPENCODE_DISABLE_AUTOUPDATE"] = "1", ["TERM"] = "xterm-256color", ["COLORTERM"] = "truecolor"
};
foreach (string variable in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" }) Directory.CreateDirectory(environment[variable]!);
await using var process = TerminalProcessFactory.Create();
var engine = new TerminalEmulator(120, 35);
var gate = new object();
var replies = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
string? inputFailure = null;
TerminalProcessExit? exit = null;
engine.Response += reply => replies.Writer.TryWrite(Encoding.UTF8.GetBytes(reply));
process.OutputReceived += bytes => { lock (gate) engine.Feed(bytes); };
process.InputFailed += error => inputFailure = error.Message;
process.Exited += value => exit = value;
Task? replyPump = null;
try
{
    await process.StartAsync(new TerminalProcessOptions { FileName = executable, WorkingDirectory = fixture, Columns = 120, Rows = 35, Environment = environment }, lifetime.Token);
    replyPump = Task.Run(async () =>
    {
        await foreach (byte[] bytes in replies.Reader.ReadAllAsync(lifetime.Token)) await process.WriteAsync(bytes, lifetime.Token);
    });
    await Until(() => Snapshot().AlternateScreen && Screen().Any(char.IsLetter), "TUI alternate screen");
    await Task.Delay(1500, lifetime.Token);
    await process.WriteAsync(Encoding.UTF8.GetBytes("native-engine-smoke"), lifetime.Token);
    await Until(() => Screen().Contains("native-engine-smoke", StringComparison.Ordinal), "TUI input echo without submission");
    foreach ((int cols, int rows) in new[] { (100, 28), (140, 42), (85, 25), (120, 35), (95, 30), (120, 35) })
    {
        lock (gate) engine.Resize(cols, rows);
        process.Resize(cols, rows);
        await Task.Delay(250, lifetime.Token);
        if (!process.IsRunning) throw new InvalidOperationException("OpenCode exited during resize: " + exit);
    }
    await Until(() => Screen().Contains("native-engine-smoke", StringComparison.Ordinal), "TUI input preserved after resize");
    if (inputFailure is not null) throw new IOException(inputFailure);
    await File.WriteAllTextAsync(Path.Combine(fixture, "screen.txt"), Screen());
    Console.WriteLine("PASS real OpenCode TUI: alternate screen, typed draft (not submitted), six resizes, draft preserved.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error);
    Console.Error.WriteLine("Screen: " + Screen());
    return 1;
}
finally
{
    lifetime.Cancel(); replies.Writer.TryComplete();
    await process.CloseAsync();
    if (replyPump is not null) try { await replyPump; } catch (OperationCanceledException) { }
    Console.WriteLine("Isolated fixture: " + fixture);
}
TerminalSnapshot Snapshot() { lock (gate) return engine.GetSnapshot(); }
string Screen() => string.Join("\n", Snapshot().Lines.Select(line => string.Concat(line.Cells.Select(cell => cell.Text))));
async Task Until(Func<bool> check, string name)
{
    var deadline = DateTime.UtcNow.AddSeconds(35);
    while (DateTime.UtcNow < deadline)
    {
        lifetime.Token.ThrowIfCancellationRequested();
        if (check()) return;
        if (!process.IsRunning) throw new InvalidOperationException("OpenCode exited: " + exit);
        await Task.Delay(100, lifetime.Token);
    }
    throw new TimeoutException(name);
}
