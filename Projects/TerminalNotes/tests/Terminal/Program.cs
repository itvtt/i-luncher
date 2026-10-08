using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerminalNotes.Services;

internal static partial class Program
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private static async Task<int> Main(string[] args)
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Terminal Notes ConPTY Smoke {Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(testDirectory, "appdata"));

        try
        {
            VerifyTerminalInputBuffer();
            await VerifyNativeLifecycleIsolationAsync();
            if (args.Contains("--opencode-context-only", StringComparer.Ordinal))
            {
                await VerifyOpenCodeProjectContextEnvironment(testDirectory);
                Console.WriteLine("PASS: pane-specific OpenCode config, dynamic plan binding, Unicode ConPTY environment, and parent environment isolation.");
            }
            else
            {
                await VerifyCodexLaunchOption(testDirectory);
                await VerifyCustomStartupCommand(testDirectory);
                await VerifyStartupCommandExitLifecycle(testDirectory);
                await VerifyNormalExitWithPendingInputDoesNotReportFailure(testDirectory);
                await VerifyConcurrentTerminalInputIsolation(testDirectory);
                await VerifyOpenCodeProjectContextEnvironment(testDirectory);
                await VerifyPermissionModeCommandsReachNativeSession(testDirectory);
                await VerifyInteractiveSession(testDirectory);
                await VerifyDisposalKillsDescendants(testDirectory);
                Console.WriteLine("PASS: ConPTY startup and permission commands, CLI stubs, interactive, Unicode, resize, Ctrl+C, environment, and ownership checks passed.");
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(testDirectory, recursive: true);
            }
            catch
            {
                // A failed cleanup should not hide the terminal test result.
            }
        }
    }

    private static void VerifyTerminalInputBuffer()
    {
        var buffered = new TerminalInputBuffer();
        const int bufferedChunks = 2048;
        for (int index = 0; index < bufferedChunks; index++)
            Require(buffered.TryWrite(BitConverter.GetBytes(index)), "An unbounded input queue rejected a chunk while its consumer was paused.");
        for (int index = 0; index < bufferedChunks; index++)
        {
            Require(buffered.Reader.TryRead(out byte[]? bytes) && BitConverter.ToInt32(bytes!) == index,
                "Buffered input chunks were lost, duplicated, or reordered.");
        }

        var concurrent = new TerminalInputBuffer();
        const int writerCount = 8;
        const int chunksPerWriter = 256;
        Parallel.For(0, writerCount, writer =>
        {
            for (int index = 0; index < chunksPerWriter; index++)
            {
                byte[] chunk = Encoding.UTF8.GetBytes($"{writer}:{index}");
                if (!concurrent.TryWrite(chunk)) throw new InvalidOperationException("Concurrent input chunk was rejected.");
            }
        });
        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (int writer = 0; writer < writerCount; writer++)
            for (int index = 0; index < chunksPerWriter; index++)
                expected.Add($"{writer}:{index}");
        var received = new HashSet<string>(StringComparer.Ordinal);
        while (concurrent.Reader.TryRead(out byte[]? chunk))
            Require(received.Add(Encoding.UTF8.GetString(chunk)), "Concurrent input chunks were merged or duplicated.");
        Require(received.SetEquals(expected), "Concurrent input chunks were lost or altered.");

        var failed = new TerminalInputBuffer();
        var writeFailure = new IOException("synthetic input writer failure");
        Require(failed.TryFail(writeFailure) && !failed.TryFail(new IOException("second failure")),
            "The input queue did not retain a single writer failure.");
        Require(!failed.TryWrite([1]) && ReferenceEquals(failed.Failure, writeFailure),
            "The failed input queue accepted more writes.");

        var normallyCompleted = new TerminalInputBuffer();
        normallyCompleted.Complete();
        Require(!normallyCompleted.TryFail(new IOException("normal exit")) && normallyCompleted.Failure is null,
            "Normal queue completion was reclassified as an input failure.");
    }

    private static async Task VerifyNativeLifecycleIsolationAsync()
    {
        using var coalescedResizeEntered = new ManualResetEventSlim();
        using var releaseCoalescedResize = new ManualResetEventSlim();
        using var latestResizeApplied = new ManualResetEventSlim();
        var appliedSizes = new ConcurrentQueue<(short Columns, short Rows)>();
        var coalescing = new ConPtyLifecycleWorker();
        coalescing.RequestResize(80, 24, (columns, rows) =>
        {
            appliedSizes.Enqueue((columns, rows));
            coalescedResizeEntered.Set();
            releaseCoalescedResize.Wait(TestTimeout);
        });
        Require(coalescedResizeEntered.Wait(TestTimeout), "The coalescing resize worker did not start.");
        coalescing.RequestResize(90, 30, (columns, rows) => appliedSizes.Enqueue((columns, rows)));
        coalescing.RequestResize(100, 40, (columns, rows) =>
        {
            appliedSizes.Enqueue((columns, rows));
            latestResizeApplied.Set();
        });
        releaseCoalescedResize.Set();
        Require(latestResizeApplied.Wait(TestTimeout), "A coalesced latest resize was not applied after the native call returned.");
        await coalescing.CloseAsync(() => { }).WaitAsync(TestTimeout);
        Require(appliedSizes.SequenceEqual(new[] { ((short)80, (short)24), ((short)100, (short)40) }),
            "Resize requests were not coalesced to the latest dimensions while the native call was blocked.");

        using var nativeResizeEntered = new ManualResetEventSlim();
        using var releaseNativeResize = new ManualResetEventSlim();
        var blocked = new ConPtyLifecycleWorker();
        blocked.RequestResize(80, 24, (columns, rows) =>
        {
            nativeResizeEntered.Set();
            releaseNativeResize.Wait(TestTimeout);
        });

        Require(nativeResizeEntered.Wait(TestTimeout), "The isolated native resize worker did not start.");
        var requestTimer = Stopwatch.StartNew();
        blocked.RequestResize(90, 30, (columns, rows) => appliedSizes.Enqueue((columns, rows)));
        blocked.RequestResize(100, 40, (columns, rows) => appliedSizes.Enqueue((columns, rows)));
        using var blockedPaneClosed = new ManualResetEventSlim();
        Task blockedClose = blocked.CloseAsync(() => blockedPaneClosed.Set());
        requestTimer.Stop();
        Require(requestTimer.Elapsed < TimeSpan.FromMilliseconds(250),
            "A blocked native operation held its caller while close was requested.");
        Require(blockedPaneClosed.Wait(TestTimeout) && blockedClose.IsCompletedSuccessfully,
            "Close did not run on an independent cleanup thread while native resize remained blocked.");

        using var otherPaneResized = new ManualResetEventSlim();
        bool otherPaneClosed = false;
        var otherPane = new ConPtyLifecycleWorker();
        otherPane.RequestResize(120, 36, (_, _) => otherPaneResized.Set());
        Require(otherPaneResized.Wait(TestTimeout), "An independent pane could not execute its native resize.");
        await otherPane.CloseAsync(() => otherPaneClosed = true).WaitAsync(TestTimeout);
        Require(otherPaneResized.IsSet && otherPaneClosed,
            "A blocked native operation in one pane prevented another pane from resizing and closing.");

        releaseNativeResize.Set();
        await blockedClose.WaitAsync(TestTimeout);
        Require(ReferenceEquals(blocked.CloseAsync(() => throw new InvalidOperationException("must not run twice")), blockedClose),
            "Repeated close did not return the original close task.");
    }
    private static async Task VerifyOpenCodeProjectContextEnvironment(string testDirectory)
    {
        string planPath = Path.Combine(testDirectory, "plan` \uACBD\uB85C.md");
        string nextPlanPath = Path.Combine(testDirectory, "next` plan.md");
        File.WriteAllText(planPath, "PRIVATE_PLAN_CONTENT_MUST_NOT_BE_COPIED");
        File.WriteAllText(nextPlanPath, "NEXT_PRIVATE_PLAN_CONTENT_MUST_NOT_BE_COPIED");

        const string inheritedConfig = "{\"username\":\"테스트—🚀\",\"model\":\"example/model\",\"instructions\":[\"project-rules.md\",\"team rules.md\"]}";
        string? originalConfig = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT");
        Environment.SetEnvironmentVariable("OPENCODE_CONFIG_CONTENT", inheritedConfig);
        try
        {
            string mergedConfig;
            string instructionFile;
            using (var context = new OpenCodeProjectContext(testDirectory, planPath, "rraad"))
            {
                instructionFile = context.InstructionFilePath;
                Require(File.Exists(instructionFile), "The pane-owned OpenCode instruction file was not created.");
                string instructions = File.ReadAllText(instructionFile);
                Require(instructions.Contains(planPath, StringComparison.Ordinal) &&
                        instructions.Contains(testDirectory, StringComparison.Ordinal) &&
                        instructions.Contains("project `rraad`", StringComparison.Ordinal) &&
                        !instructions.Contains("PRIVATE_PLAN_CONTENT_MUST_NOT_BE_COPIED", StringComparison.Ordinal),
                    "The app-owned instructions did not bind the project and plan paths without copying plan contents.");

                mergedConfig = context.CreateProcessConfigContent(Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT"));
                using (var parsed = JsonDocument.Parse(mergedConfig))
                {
                    JsonElement root = parsed.RootElement;
                    Require(root.GetProperty("username").GetString() == "테스트—🚀" &&
                            root.GetProperty("model").GetString() == "example/model",
                        "Merging plan instructions changed inherited OpenCode settings or Unicode.");
                    string[] configuredInstructions = root.GetProperty("instructions").EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty).ToArray();
                    Require(configuredInstructions.Contains("project-rules.md") && configuredInstructions.Contains("team rules.md") &&
                            configuredInstructions.Contains(instructionFile),
                        "Merging plan instructions did not preserve inherited instruction paths.");
                }
                using (var withPlugin = JsonDocument.Parse(context.CreateProcessConfigContent("{\"plugin\":[\"example-plugin\"]}")))
                {
                    var plugins = withPlugin.RootElement.GetProperty("plugin").EnumerateArray().Select(item => item.GetString()!).ToArray();
                    Require(plugins.Length == 1 && plugins[0] == "example-plugin",
                        "Question privacy preserves inherited plugins without adding the input journal.");
                    using var twice = JsonDocument.Parse(context.CreateProcessConfigContent(withPlugin.RootElement.GetRawText()));
                    Require(twice.RootElement.GetProperty("plugin").GetArrayLength() == 1,
                        "Config merging must preserve the plugin list.");
                }
                Require(Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT") == inheritedConfig,
                    "Preparing a pane-specific OpenCode config changed the parent process environment.");

                context.UpdatePlanDocumentContext(nextPlanPath);
                string updatedInstructions = File.ReadAllText(instructionFile);
                Require(updatedInstructions.Contains(nextPlanPath, StringComparison.Ordinal) &&
                        !updatedInstructions.Contains(planPath, StringComparison.Ordinal) &&
                        !updatedInstructions.Contains("NEXT_PRIVATE_PLAN_CONTENT_MUST_NOT_BE_COPIED", StringComparison.Ordinal),
                    "Updating a linked plan did not atomically replace only the path in the app-owned context.");
                using (var refreshedConfig = JsonDocument.Parse(context.CreateProcessConfigContent(inheritedConfig)))
                {
                    Require(refreshedConfig.RootElement.GetProperty("instructions").EnumerateArray()
                        .Any(item => item.GetString() == instructionFile),
                        "Updating a linked plan changed the stable instruction-file reference.");
                }

                context.UpdatePlanDocumentContext(null);
                string noPlanInstructions = File.ReadAllText(instructionFile);
                Require(noPlanInstructions.Contains("Greetings and simple questions", StringComparison.Ordinal) &&
                        noPlanInstructions.Contains("implementation or build", StringComparison.Ordinal) &&
                        noPlanInstructions.Contains(testDirectory, StringComparison.Ordinal),
                    "An unbound pane does not preserve its project root and distinguish simple questions from plan-dependent implementation.");

                bool invalidConfigRejected = false;
                try { context.CreateProcessConfigContent("not-json"); }
                catch (InvalidOperationException) { invalidConfigRejected = true; }
                Require(invalidConfigRejected, "Malformed inherited OPENCODE_CONFIG_CONTENT was silently replaced.");
                Require(Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT") == inheritedConfig,
                    "Rejecting malformed config changed the parent process environment.");

                // Verify the exact merged JSON crosses the ConPTY Unicode environment block,
                // while the parent retains its inherited config after the child starts.
                var marker = new Regex("OPENCODE_CONFIG_CHILD=(?<payload>[A-Za-z0-9+/=]+)", RegexOptions.CultureInvariant);
                using var terminal = new TerminalProbe();
                terminal.Start(
                    testDirectory,
                    100,
                    30,
                    startupCommand: "Write-Output ('OPENCODE_CONFIG_CHILD=' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($env:OPENCODE_CONFIG_CONTENT)))",
                    openCodeConfigContent: mergedConfig);
                await terminal.WaitForMatchAsync(marker, TestTimeout);
                Match childMatch = marker.Match(terminal.Text);
                string childConfig = Encoding.UTF8.GetString(Convert.FromBase64String(childMatch.Groups["payload"].Value));
                using var childJson = JsonDocument.Parse(childConfig);
                bool preservedInstruction = childJson.RootElement.GetProperty("instructions").EnumerateArray()
                    .Any(item => item.GetString() == instructionFile);
                Require(string.Equals(childConfig, mergedConfig, StringComparison.Ordinal) && preservedInstruction,
                    $"The process-specific OpenCode config did not survive the native Unicode environment block (instruction={preservedInstruction}, payloadLength={childConfig.Length}).");
                Require(Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT") == inheritedConfig,
                    "Starting the pane child changed the parent process environment.");
            }
            Require(!File.Exists(instructionFile), "Disposing a pane context did not remove its app-owned instruction file.");
            using (var edgeNameContext = new OpenCodeProjectContext(testDirectory, planPath, "`edge name`"))
            {
                string edgeNameInstructions = File.ReadAllText(edgeNameContext.InstructionFilePath);
                Require(edgeNameInstructions.Contains("project `` `edge name` `` at directory", StringComparison.Ordinal),
                    "A remembered project name starting and ending with backticks was not delimited safely in Markdown.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_CONTENT", originalConfig);
        }
    }

    private static async Task VerifyPermissionModeCommandsReachNativeSession(string testDirectory)
    {
        string stubDirectory = Path.Combine(testDirectory, "permission mode native stubs");
        Directory.CreateDirectory(stubDirectory);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(
            Path.Combine(stubDirectory, "opencode.cmd"),
            "@echo off\r\necho OPENCODE_NATIVE_ARGS=[%*]\r\nexit /b 0\r\n",
            utf8);

        var cases = new[]
        {
            (Label: "CASE_OPENCODE_STANDARD", Tool: TerminalTool.OpenCode, Mode: TerminalPermissionMode.Standard),
            (Label: "CASE_OPENCODE_AUTO", Tool: TerminalTool.OpenCode, Mode: TerminalPermissionMode.Auto),
            (Label: "CASE_DEFAULT_STANDARD", Tool: TerminalTool.Default, Mode: TerminalPermissionMode.Standard),
            (Label: "CASE_DEFAULT_AUTO", Tool: TerminalTool.Default, Mode: TerminalPermissionMode.Auto)
        };

        var startupParts = new List<string>
        {
            "Remove-Item Alias:opencode -ErrorAction SilentlyContinue",
            "Remove-Item Function:opencode -ErrorAction SilentlyContinue"
        };
        foreach (var testCase in cases)
        {
            string command = TerminalLaunchModes.Command(testCase.Tool, testCase.Mode)
                ?? throw new InvalidOperationException($"No command for {testCase.Tool}/{testCase.Mode}.");
            startupParts.Add($"Write-Output '{testCase.Label}'");
            startupParts.Add(command);
        }
        startupParts.Add("Write-Output 'NATIVE_PERMISSION_DONE'");

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", stubDirectory);
        try
        {
            using var terminal = new TerminalProbe();
            terminal.Start(
                testDirectory,
                100,
                30,
                startupCommand: string.Join("; ", startupParts));
            await terminal.WaitForAsync("NATIVE_PERMISSION_DONE", TestTimeout);

            string plain = StripAnsiRegex().Replace(terminal.Text, string.Empty);
            string expectedOrder =
                @"CASE_OPENCODE_STANDARD[\s\S]*OPENCODE_NATIVE_ARGS=\[\]" +
                @"[\s\S]*CASE_OPENCODE_AUTO[\s\S]*OPENCODE_NATIVE_ARGS=\[--auto\]" +
                @"[\s\S]*CASE_DEFAULT_STANDARD[\s\S]*OPENCODE_NATIVE_ARGS=\[\]" +
                @"[\s\S]*CASE_DEFAULT_AUTO[\s\S]*OPENCODE_NATIVE_ARGS=\[--auto\]";
            Require(
                Regex.IsMatch(plain, expectedOrder, RegexOptions.CultureInvariant),
                "Permission mode arguments did not reach the native ConPTY stubs exactly or in order.\n" + plain);

            terminal.Session.Write("Write-Output 'PERMISSION_MODE_SHELL_ALIVE'; exit 36\r");
            await terminal.WaitForAsync("PERMISSION_MODE_SHELL_ALIVE", TestTimeout);
            Require(
                await terminal.ExitCode.WaitAsync(TestTimeout) == 36,
                "PowerShell did not remain interactive after permission mode stubs exited.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    private static async Task VerifyCustomStartupCommand(string testDirectory)
    {
        string stubDirectory = Path.Combine(testDirectory, "custom CLI stubs");
        Directory.CreateDirectory(stubDirectory);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(
            Path.Combine(stubDirectory, "codex.cmd"),
            "@echo off\r\necho UNEXPECTED_REAL_CODEX_PATH\r\nexit /b 0\r\n",
            utf8);
        File.WriteAllText(
            Path.Combine(stubDirectory, "claude.cmd"),
            "@echo off\r\necho CLAUDE_STUB_ARGS=%*\r\necho CLAUDE_STUB_CWD=%CD%\r\nexit /b 0\r\n",
            utf8);
        File.WriteAllText(
            Path.Combine(stubDirectory, "opencode.cmd"),
            "@echo off\r\necho OPENCODE_STUB_ARGS=%*\r\nexit /b 0\r\n",
            utf8);

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable(
            "PATH",
            string.IsNullOrEmpty(originalPath) ? stubDirectory : $"{stubDirectory};{originalPath}");

        try
        {
            const string customCommand =
                "claude.cmd --message \"spaced value\"; " +
                "opencode.cmd --mode \"safe space\"; " +
                "Write-Output \"CUSTOM_PS=한글 'quoted' 값\"";
            using (var customTerminal = new TerminalProbe())
            {
                customTerminal.Start(
                    testDirectory,
                    100,
                    30,
                    launchCodex: true,
                    startupCommand: customCommand);
                await customTerminal.WaitForAsync("CLAUDE_STUB_ARGS=--message \"spaced value\"", TestTimeout);
                await customTerminal.WaitForAsync("OPENCODE_STUB_ARGS=--mode \"safe space\"", TestTimeout);
                await customTerminal.WaitForAsync($"CLAUDE_STUB_CWD={testDirectory}", TestTimeout);
                await customTerminal.WaitForAsync("CUSTOM_PS=한글 'quoted' 값", TestTimeout);
                Require(
                    !customTerminal.Text.Contains("UNEXPECTED_REAL_CODEX_PATH", StringComparison.Ordinal),
                    "launchCodex took precedence over the explicit startup command.");
                customTerminal.Session.Write("Write-Output 'CUSTOM_SHELL_ALIVE'; exit 34\r");
                await customTerminal.WaitForAsync("CUSTOM_SHELL_ALIVE", TestTimeout);
                Require(
                    await customTerminal.ExitCode.WaitAsync(TestTimeout) == 34,
                    "PowerShell did not remain interactive after the custom CLI stubs exited.");
            }

            using var failingTerminal = new TerminalProbe();
            failingTerminal.Start(
                testDirectory,
                80,
                24,
                startupCommand: "throw \"의도된 'quoted' 오류\"");
            await failingTerminal.WaitForAsync("Startup command failed:", TestTimeout);
            await failingTerminal.WaitForAsync("의도된 'quoted' 오류", TestTimeout);
            failingTerminal.Session.Write("Write-Output 'ERROR_SHELL_ALIVE'; exit 35\r");
            await failingTerminal.WaitForAsync("ERROR_SHELL_ALIVE", TestTimeout);
            Require(
                await failingTerminal.ExitCode.WaitAsync(TestTimeout) == 35,
                "PowerShell did not remain interactive after a custom startup error.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    private static async Task VerifyStartupCommandExitLifecycle(string testDirectory)
    {
        using (var successfulTerminal = new TerminalProbe())
        {
            successfulTerminal.Start(
                testDirectory,
                80,
                24,
                startupCommand: "Write-Output 'EXIT_MODE_SUCCESS'",
                exitOnStartupCommandCompletion: true);
            await successfulTerminal.WaitForAsync("EXIT_MODE_SUCCESS", TestTimeout);
            await successfulTerminal.ExitCode.WaitAsync(TestTimeout);
            Require(!successfulTerminal.Session.IsRunning,
                "The ConPTY remained running after a successful startup command in exit-on-completion mode.");
        }

        using var failingTerminal = new TerminalProbe();
        failingTerminal.Start(
            testDirectory,
            80,
            24,
            startupCommand: "throw 'EXIT_MODE_FAILURE'",
            exitOnStartupCommandCompletion: true);
        await failingTerminal.WaitForAsync("Startup command failed:", TestTimeout);
        await failingTerminal.WaitForAsync("EXIT_MODE_FAILURE", TestTimeout);
        await failingTerminal.ExitCode.WaitAsync(TestTimeout);
        Require(!failingTerminal.Session.IsRunning,
            "The ConPTY remained running after a failed startup command in exit-on-completion mode.");

        using var legacyTerminal = new TerminalProbe();
        legacyTerminal.Start(
            testDirectory,
            80,
            24,
            startupCommand: "Write-Output 'LEGACY_MODE_SHELL_READY'");
        await legacyTerminal.WaitForAsync("LEGACY_MODE_SHELL_READY", TestTimeout);
        Require(legacyTerminal.Session.IsRunning,
            "The default startup-command mode no longer leaves an interactive PowerShell shell available.");
        legacyTerminal.Session.Write("exit 37\r");
        Require(await legacyTerminal.ExitCode.WaitAsync(TestTimeout) == 37,
            "The default startup-command shell did not accept a follow-up command.");
    }

    private static async Task VerifyNormalExitWithPendingInputDoesNotReportFailure(string testDirectory)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var terminal = new TerminalProbe();
            int inputFailures = 0;
            terminal.Session.InputFailed += _ => Interlocked.Increment(ref inputFailures);
            terminal.Start(
                testDirectory,
                80,
                24,
                startupCommand: "Start-Sleep -Milliseconds 200; exit 0",
                exitOnStartupCommandCompletion: true);

            // Queue more than the anonymous-pipe capacity while the child is alive,
            // then let its ordinary exit race the writer and exit watcher.
            terminal.Session.Write(new string('x', 4 * 1024 * 1024));
            await terminal.ExitCode.WaitAsync(TestTimeout);
            await Task.Delay(150);
            Require(inputFailures == 0,
                "Normal child exit with queued input was reported as an input-writer failure.");
        }
    }

    private static async Task VerifyConcurrentTerminalInputIsolation(string testDirectory)
    {
        using var backgroundTerminal = new TerminalProbe();
        using var foregroundTerminal = new TerminalProbe();
        backgroundTerminal.Start(
            testDirectory,
            80,
            24,
            startupCommand: "while ($true) { Write-Output 'BG_TICK'; Start-Sleep -Milliseconds 10 } ");
        await backgroundTerminal.WaitForAsync("BG_TICK", TestTimeout);

        foregroundTerminal.Start(testDirectory, 80, 24, startupCommand: "Write-Output 'FG_READY'");
        await foregroundTerminal.WaitForAsync("FG_READY", TestTimeout);

        int backgroundTicksBefore = CountOccurrences(backgroundTerminal.Text, "BG_TICK");
        const int inputCount = 48;
        var expectedResults = new string[inputCount];
        for (int index = 0; index < inputCount; index++)
        {
            string value = $"한글-{index:D2}-😀";
            expectedResults[index] = $"FG_RESULT:{value}";
            foregroundTerminal.Session.Write(
                $"$v='{value}'; Start-Sleep -Milliseconds 15; Write-Output ('FG_' + 'RESULT:' + $v)\r");
        }

        foreach (string result in expectedResults)
            await foregroundTerminal.WaitForAsync(result, TestTimeout);

        string foregroundOutput = foregroundTerminal.Text;
        int previousResultIndex = -1;
        foreach (string result in expectedResults)
        {
            int resultIndex = foregroundOutput.IndexOf(result, StringComparison.Ordinal);
            Require(resultIndex > previousResultIndex &&
                    foregroundOutput.IndexOf(result, resultIndex + result.Length, StringComparison.Ordinal) < 0,
                "Foreground terminal input was missing, duplicated, or delivered out of FIFO order.");
            previousResultIndex = resultIndex;
        }

        string backgroundOutput = backgroundTerminal.Text;
        Require(CountOccurrences(backgroundOutput, "BG_TICK") >= backgroundTicksBefore + 3,
            "The secondary terminal did not keep producing output during foreground input.");
        Require(!backgroundOutput.Contains("FG_RESULT:", StringComparison.Ordinal) &&
                !foregroundOutput.Contains("BG_TICK", StringComparison.Ordinal),
            "Concurrent terminal output or input leaked across ConPTY sessions.");
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }
    private static async Task VerifyCodexLaunchOption(string testDirectory)
    {
        string stubDirectory = Path.Combine(testDirectory, "stub commands");
        Directory.CreateDirectory(stubDirectory);
        File.WriteAllText(
            Path.Combine(stubDirectory, "codex.cmd"),
            "@echo off\r\necho CODEX_STUB_STARTED\r\necho CODEX_STUB_CWD=%CD%\r\nexit /b 0\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable(
            "PATH",
            string.IsNullOrEmpty(originalPath) ? stubDirectory : $"{stubDirectory};{originalPath}");

        try
        {
            using (var regularTerminal = new TerminalProbe())
            {
                regularTerminal.Start(testDirectory, 80, 24, launchCodex: false);
                await regularTerminal.WaitForAsync("PS ", TestTimeout);
                await Task.Delay(250);
                Require(
                    !regularTerminal.Text.Contains("CODEX_STUB_STARTED", StringComparison.Ordinal),
                    "Codex launched when launchCodex was false.");
                regularTerminal.Session.Write("exit 31\r");
                Require(
                    await regularTerminal.ExitCode.WaitAsync(TestTimeout) == 31,
                    "The regular PowerShell session did not exit normally.");
            }

            using var codexTerminal = new TerminalProbe();
            codexTerminal.Start(testDirectory, 80, 24, launchCodex: true);
            await codexTerminal.WaitForAsync("CODEX_STUB_STARTED", TestTimeout);
            await codexTerminal.WaitForAsync($"CODEX_STUB_CWD={testDirectory}", TestTimeout);
            codexTerminal.Session.Write("Write-Output 'CODEX_SHELL_ALIVE'; exit 32\r");
            await codexTerminal.WaitForAsync("CODEX_SHELL_ALIVE", TestTimeout);
            Require(
                await codexTerminal.ExitCode.WaitAsync(TestTimeout) == 32,
                "PowerShell did not remain interactive after the Codex stub exited.");

            Environment.SetEnvironmentVariable("PATH", Path.Combine(testDirectory, "empty PATH"));
            using var missingCodexTerminal = new TerminalProbe();
            missingCodexTerminal.Start(testDirectory, 80, 24, launchCodex: true);
            await missingCodexTerminal.WaitForAsync("Codex CLI was not found on PATH.", TestTimeout);
            Require(
                missingCodexTerminal.Text.Contains("Codex CLI를 찾을 수 없습니다.", StringComparison.Ordinal),
                "The missing-Codex message did not include Korean guidance.");
            missingCodexTerminal.Session.Write("Write-Output 'MISSING_CODEX_SHELL_ALIVE'; exit 33\r");
            await missingCodexTerminal.WaitForAsync("MISSING_CODEX_SHELL_ALIVE", TestTimeout);
            Require(
                await missingCodexTerminal.ExitCode.WaitAsync(TestTimeout) == 33,
                "PowerShell did not remain interactive after a missing-Codex error.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    private static async Task VerifyInteractiveSession(string testDirectory)
    {
        using var terminal = new TerminalProbe();
        terminal.Start(testDirectory, 80, 24);
        await terminal.WaitForAsync("PS ", TestTimeout);

        terminal.Session.Resize(101, 31);
        await Task.Delay(150);
        terminal.Session.Write(
            "$size=$Host.UI.RawUI.WindowSize; " +
            "Write-Output 'MARKER_한글_😀'; " +
            "Write-Output ('CWD=' + (Get-Location).Path); " +
            "Write-Output ('SIZE=' + $size.Width + 'x' + $size.Height); " +
            "Write-Output ('TERM=' + $env:TERM); " +
            "Write-Output ('COLORTERM=' + $env:COLORTERM)\r");

        await terminal.WaitForAsync("COLORTERM=truecolor", TestTimeout);
        string firstOutput = terminal.Text;
        Require(firstOutput.Contains("MARKER_한글_😀", StringComparison.Ordinal), "UTF-8 Korean/emoji marker was corrupted.");
        Require(firstOutput.Contains($"CWD={testDirectory}", StringComparison.OrdinalIgnoreCase), "Working directory with spaces was not preserved.");
        Require(firstOutput.Contains("SIZE=101x31", StringComparison.Ordinal), "Resize did not reach the console client.");
        Require(firstOutput.Contains("TERM=xterm-256color", StringComparison.Ordinal), "TERM was not set for the child.");
        Require(firstOutput.Contains("COLORTERM=truecolor", StringComparison.Ordinal), "COLORTERM was not set for the child.");

        terminal.Session.Write("Write-Output 'BEFORE_CTRL_C'; ping.exe -t 127.0.0.1\r");
        await terminal.WaitForMatchAsync(
            new Regex("BEFORE_CTRL_C[\\s\\S]*TTL=", RegexOptions.CultureInvariant),
            TestTimeout);
        terminal.Session.Write("\u0003");
        await Task.Delay(1_000);
        terminal.Session.Write("Write-Output 'AFTER_CTRL_C'\r");

        await terminal.WaitForAsync("AFTER_CTRL_C", TestTimeout);
        terminal.Session.Write("exit 23\r");
        int exitCode = await terminal.ExitCode.WaitAsync(TestTimeout);
        Require(exitCode == 23, $"Expected PowerShell exit code 23, received {exitCode}.");
        Require(!terminal.Session.IsRunning, "IsRunning remained true after process exit.");
    }

    private static async Task VerifyDisposalKillsDescendants(string testDirectory)
    {
        var terminal = new TerminalProbe();
        terminal.Start(testDirectory, 80, 24);
        await terminal.WaitForAsync("PS ", TestTimeout);
        terminal.Session.Write(
            "$child=Start-Process powershell.exe -ArgumentList '-NoLogo','-Command','Start-Sleep -Seconds 60' -PassThru; " +
            "Write-Output ('CHILD_PID=' + $child.Id); " +
            "while ($true) { Start-Sleep -Milliseconds 50 }\r");

        Match match = await terminal.WaitForMatchAsync(ChildPidRegex(), TestTimeout);
        int childProcessId = int.Parse(match.Groups[1].Value);

        var disposeTimer = Stopwatch.StartNew();
        terminal.Dispose();
        disposeTimer.Stop();
        Require(disposeTimer.Elapsed < TimeSpan.FromSeconds(3), $"Dispose blocked for {disposeTimer.Elapsed}.");
        Require(!terminal.Session.IsRunning, "IsRunning remained true after disposal.");

        await WaitUntilAsync(
            () => !IsProcessAlive(childProcessId),
            TestTimeout,
            $"Owned descendant process {childProcessId} survived job disposal.");
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed >= timeout)
            {
                throw new InvalidOperationException(failure);
            }

            await Task.Delay(50);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [GeneratedRegex(@"CHILD_PID=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ChildPidRegex();

    [GeneratedRegex("\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)|\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex StripAnsiRegex();

    private sealed class TerminalProbe : IDisposable
    {
        private readonly ConcurrentQueue<string> _chunks = new();
        private readonly SemaphoreSlim _outputChanged = new(0);
        private readonly TaskCompletionSource<int> _exitCode =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public TerminalProbe()
        {
            Session.OutputReceived += OnOutput;
            Session.Exited += code => _exitCode.TrySetResult(code);
        }

        public ConPtySession Session { get; } = new();

        public Task<int> ExitCode => _exitCode.Task;

        public string Text => string.Concat(_chunks);

        public void Start(
            string workingDirectory,
            short columns,
            short rows,
            bool launchCodex = false,
            string? startupCommand = null,
            string? openCodeConfigContent = null,
            bool exitOnStartupCommandCompletion = false)
        {
            Session.Start(
                workingDirectory,
                columns,
                rows,
                launchCodex,
                startupCommand,
                openCodeConfigContent,
                exitOnStartupCommandCompletion);
        }

        public async Task WaitForAsync(string marker, TimeSpan timeout)
        {
            await WaitForMatchAsync(
                new Regex(Regex.Escape(marker), RegexOptions.CultureInvariant),
                timeout);
        }

        public async Task<Match> WaitForMatchAsync(Regex regex, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                Match match = regex.Match(Text);
                if (match.Success)
                {
                    return match;
                }

                try
                {
                    await _outputChanged.WaitAsync(cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    string escaped = Text.Replace("\u001b", "<ESC>", StringComparison.Ordinal);
                    throw new TimeoutException($"Timed out waiting for /{regex}/. Output: {escaped}");
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Session.Dispose();
            _outputChanged.Dispose();
        }

        private void OnOutput(string text)
        {
            _chunks.Enqueue(text);

            // Support cursor-inheritance handshakes if the ConPTY flags change later.
            if (text.Contains("\u001b[6n", StringComparison.Ordinal))
            {
                Session.Write("\u001b[1;1R");
            }

            try
            {
                _outputChanged.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
