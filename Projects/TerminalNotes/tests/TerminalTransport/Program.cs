using System.Diagnostics;
using System.Text;
using TerminalNotes.TerminalTransport;

static void Assert(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

if (OperatingSystem.IsWindows()) await VerifyWindowsConPtyAsync();
else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) await VerifyUnixPtyAsync();
else Console.WriteLine("SKIP: unsupported OS");

static async Task VerifyWindowsConPtyAsync()
{
    string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    if (!File.Exists(powershell)) throw new FileNotFoundException("Windows PowerShell is required for the ConPTY smoke test.", powershell);
    string cwd = Path.Combine(Path.GetTempPath(), "terminal-transport-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(cwd);
    using var output = new MemoryStream();
    var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var echoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var exit = new TaskCompletionSource<TerminalProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var terminal = TerminalProcessFactory.Create();
    terminal.OutputReceived += bytes =>
    {
        lock (output) output.Write(bytes);
        if (GetText(output).Contains("READY|", StringComparison.Ordinal)) received.TrySetResult();
        if (GetText(output).Contains("ECHO:한글", StringComparison.Ordinal)) echoed.TrySetResult();
    };
    terminal.Exited += result => exit.TrySetResult(result);
    await terminal.StartAsync(new TerminalProcessOptions
    {
        FileName = powershell,
        Arguments = ["-NoLogo", "-NoProfile", "-Command", "[Console]::Write('READY|'+$env:TERMINAL_TRANSPORT_TEST+'|'+[Environment]::CurrentDirectory+'|'+$Host.UI.RawUI.WindowSize.Width+'x'+$Host.UI.RawUI.WindowSize.Height+'|'); $line=[Console]::ReadLine(); [Console]::Write('ECHO:'+$line+'|'+$Host.UI.RawUI.WindowSize.Width+'x'+$Host.UI.RawUI.WindowSize.Height); exit 23"],
        WorkingDirectory = cwd,
        Columns = 80,
        Rows = 24,
        Environment = new Dictionary<string, string?> { ["TERMINAL_TRANSPORT_TEST"] = "ok" }
    });
    await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    terminal.Resize(91, 31);
    await terminal.WriteAsync(Encoding.UTF8.GetBytes("한글\r"));
    await echoed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    TerminalProcessExit result = await exit.Task.WaitAsync(TimeSpan.FromSeconds(10));
    string text = GetText(output);
    Assert(text.Contains($"READY|ok|{cwd}|80x24|", StringComparison.Ordinal), "ConPTY initial env, cwd or dimensions were wrong.");
    Assert(text.Contains("ECHO:한글", StringComparison.Ordinal), "ConPTY input/output lost UTF-8 text: " + text.Replace("\r", "\\r").Replace("\n", "\\n"));
    Assert(text.Contains("91x31", StringComparison.Ordinal), "ConPTY resize was not applied.");
    Assert(result.ExitCode == 23, $"Expected process exit 23, got {result.ExitCode}.");
    Assert(!terminal.IsRunning, "Exited ConPTY still reports running.");

    await VerifyWindowsApplicationResolutionAsync(cwd);

    // Exercise Ctrl+C handling and owned tree cleanup in a separate persistent shell.
    var interrupted = new TaskCompletionSource<TerminalProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var ctrlC = TerminalProcessFactory.Create();
    ctrlC.Exited += value => interrupted.TrySetResult(value);
    await ctrlC.StartAsync(new TerminalProcessOptions
    {
        FileName = powershell,
        Arguments = ["-NoLogo", "-NoProfile", "-Command", "while($true){Start-Sleep -Milliseconds 100}"],
        WorkingDirectory = cwd
    });
    await ctrlC.WriteAsync(new byte[] { 3 });
    _ = await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(8));

    var childStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var childOutput = new MemoryStream();
    await using var owned = TerminalProcessFactory.Create();
    owned.OutputReceived += bytes =>
    {
        lock (childOutput) childOutput.Write(bytes);
        string value = GetText(childOutput);
        var match = System.Text.RegularExpressions.Regex.Match(value, @"CHILD=(\d+)");
        if (match.Success) childStarted.TrySetResult(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    };
    await owned.StartAsync(new TerminalProcessOptions
    {
        FileName = powershell,
        Arguments = ["-NoLogo", "-NoProfile", "-Command", "$child=Start-Process -FilePath ($PSHOME+'\\powershell.exe') -ArgumentList @('-NoLogo','-NoProfile','-Command','Start-Sleep -Seconds 60') -PassThru; [Console]::Write('CHILD='+$child.Id); while($true){Start-Sleep -Milliseconds 100}"],
        WorkingDirectory = cwd
    });
    int childPid = await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await owned.CloseAsync();
    try
    {
        using var child = Process.GetProcessById(childPid);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (ArgumentException) { }
    Directory.Delete(cwd, recursive: true);
    Console.WriteLine("PASS: Windows ConPTY start, cwd/env/size, UTF-8 input/output, resize, Ctrl+C, exit and owned-tree close.");
}

static async Task VerifyWindowsApplicationResolutionAsync(string workingDirectory)
{
    string systemCommandPrompt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");
    string bareOutput = await RunWindowsCommandAsync("cmd.exe", ["/d", "/c", "echo BARE_PATH_OK"], workingDirectory);
    Assert(bareOutput.Contains("BARE_PATH_OK", StringComparison.Ordinal), "Bare cmd.exe was not resolved through the effective child PATH.");

    string customPath = Path.Combine(workingDirectory, "search path with spaces");
    Directory.CreateDirectory(customPath);
    string customExecutable = Path.Combine(customPath, "test tool.exe");
    File.Copy(systemCommandPrompt, customExecutable);
    string customOutput = await RunWindowsCommandAsync("test tool", ["/d", "/c", "echo CUSTOM_PATH_OK"], workingDirectory,
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["pAtH"] = customPath });
    Assert(customOutput.Contains("CUSTOM_PATH_OK", StringComparison.Ordinal), "Case-insensitive child PATH override, spaced path or .exe fallback failed.");

    string relativeOutput = await RunWindowsCommandAsync(Path.Combine("search path with spaces", "test tool"),
        ["/d", "/c", "echo RELATIVE_PATH_OK"], workingDirectory,
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Path"] = null });
    Assert(relativeOutput.Contains("RELATIVE_PATH_OK", StringComparison.Ordinal), "Extensionless explicit path was not resolved relative to the child working directory.");

    await using var withoutPath = TerminalProcessFactory.Create();
    try
    {
        await withoutPath.StartAsync(new TerminalProcessOptions
        {
            FileName = "cmd.exe",
            WorkingDirectory = workingDirectory,
            Environment = new Dictionary<string, string?> { ["PATH"] = null }
        });
        throw new InvalidOperationException("A removed child PATH unexpectedly resolved cmd.exe.");
    }
    catch (FileNotFoundException) { }
}

static async Task<string> RunWindowsCommandAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
    IReadOnlyDictionary<string, string?>? environment = null)
{
    using var output = new MemoryStream();
    var exited = new TaskCompletionSource<TerminalProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var terminal = TerminalProcessFactory.Create();
    terminal.OutputReceived += bytes => { lock (output) output.Write(bytes); };
    terminal.Exited += value => exited.TrySetResult(value);
    await terminal.StartAsync(new TerminalProcessOptions
    {
        FileName = fileName,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        Environment = environment ?? new Dictionary<string, string?>()
    });
    TerminalProcessExit result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert(result.ExitCode == 0, $"'{fileName}' exited with code {result.ExitCode}.");
    lock (output) return GetText(output);
}

static async Task VerifyUnixPtyAsync()
{
    string helper = Path.Combine(AppContext.BaseDirectory, "terminalnotes-pty");
    if (!File.Exists(helper))
    {
        Console.WriteLine("SKIP: build native/terminalnotes-pty with native/build-unix-helper.sh to run Unix PTY smoke test.");
        return;
    }
    string cwd = Path.Combine(Path.GetTempPath(), "terminal-transport-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(cwd);
    using var output = new MemoryStream();
    var exit = new TaskCompletionSource<TerminalProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously);
    var gotPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var terminal = TerminalProcessFactory.Create();
    terminal.OutputReceived += bytes =>
    {
        lock (output) output.Write(bytes);
        if (GetText(output).Contains("READY", StringComparison.Ordinal)) gotPrompt.TrySetResult();
    };
    terminal.Exited += result => exit.TrySetResult(result);
    await terminal.StartAsync(new TerminalProcessOptions
    {
        FileName = "/bin/sh",
        Arguments = ["-c", "printf READY; IFS= read -r line; printf 'ECHO:%s:%s:%s' \"$line\" \"$PWD\" \"$TERMINAL_TRANSPORT_TEST\"; exit 23"],
        WorkingDirectory = cwd,
        Environment = new Dictionary<string, string?> { ["TERMINAL_TRANSPORT_TEST"] = "ok" },
        UnixHelperPath = helper
    });
    await gotPrompt.Task.WaitAsync(TimeSpan.FromSeconds(10));
    terminal.Resize(90, 30);
    await terminal.WriteAsync(Encoding.UTF8.GetBytes("한글\n"));
    TerminalProcessExit result = await exit.Task.WaitAsync(TimeSpan.FromSeconds(10));
    string text = GetText(output);
    Assert(text.Contains("ECHO:한글:" + cwd + ":ok", StringComparison.Ordinal), "Unix PTY lost input, cwd, environment or UTF-8 output.");
    Assert(result.ExitCode == 23, $"Expected process exit 23, got {result.ExitCode}.");
    Assert(!terminal.IsRunning, "Exited Unix PTY still reports running.");

    var resistantReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var resistant = TerminalProcessFactory.Create();
    resistant.OutputReceived += bytes =>
    {
        lock (output) output.Write(bytes);
        if (GetText(output).Contains("RESIST", StringComparison.Ordinal)) resistantReady.TrySetResult();
    };
    await resistant.StartAsync(new TerminalProcessOptions
    {
        FileName = "/bin/sh",
        // The interactive shell gives this external foreground command its own process group.
        // Ignoring HUP/TERM makes the helper's escalation and group targeting observable.
        Arguments = ["-i"],
        WorkingDirectory = cwd,
        UnixHelperPath = helper
    });
    await resistant.WriteAsync(Encoding.UTF8.GetBytes("sh -c \"trap '' HUP TERM; printf RESIST; while :; do sleep 1; done\"\n"));
    await resistantReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var closeWatch = Stopwatch.StartNew();
    await resistant.CloseAsync().WaitAsync(TimeSpan.FromSeconds(4));
    Assert(closeWatch.Elapsed < TimeSpan.FromSeconds(4), "Unix close did not escalate past ignored HUP/TERM.");

    var pipelineReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var pipeline = TerminalProcessFactory.Create();
    pipeline.OutputReceived += bytes =>
    {
        lock (output) output.Write(bytes);
        if (GetText(output).Contains("PIPE_SURVIVOR", StringComparison.Ordinal)) pipelineReady.TrySetResult();
    };
    await pipeline.StartAsync(new TerminalProcessOptions
    {
        FileName = "/bin/sh",
        Arguments = ["-i"],
        WorkingDirectory = cwd,
        UnixHelperPath = helper
    });
    // `true` is the pipeline's process-group leader and exits immediately; the
    // second foreground member remains and ignores both polite close signals.
    await pipeline.WriteAsync(Encoding.UTF8.GetBytes("/bin/true | sh -c 'trap \"\" HUP TERM; printf PIPE_SURVIVOR; while :; do sleep 1; done'\n"));
    await pipelineReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
    closeWatch.Restart();
    await pipeline.CloseAsync().WaitAsync(TimeSpan.FromSeconds(4));
    Assert(closeWatch.Elapsed < TimeSpan.FromSeconds(4), "Unix close failed to kill a foreground group whose leader had already exited.");
    Directory.Delete(cwd, recursive: true);
    Console.WriteLine("PASS: Unix PTY argv/cwd/env, UTF-8, resize, exit and HUP/TERM-resistant foreground-group cleanup, including an exited pipeline leader.");
}

static string GetText(MemoryStream stream)
{
    lock (stream) return Encoding.UTF8.GetString(stream.ToArray());
}
