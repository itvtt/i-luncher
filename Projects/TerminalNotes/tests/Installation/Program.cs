using System.Text;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--live-check", StringComparer.Ordinal))
        {
            AutoInstallDetectionResult detected = await AutoInstall.CheckAsync();
            Console.WriteLine($"OpenCode installed={detected.IsInstalled}, version={detected.Version}");
            Console.WriteLine($"Command={detected.Path}");
            Console.WriteLine(detected.Message);
            return detected.IsInstalled ? 0 : 1;
        }
        string tempRoot = Path.Combine(Path.GetTempPath(), "TerminalNotes-InstallationTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            await VerifyMissingCommandIsReadOnlyAsync(tempRoot);
            await VerifyFreshEnvironmentPathIsProbedAndRefreshedAsync(tempRoot);
            await VerifyPowerShellCommandCanBeDetectedAsync(tempRoot);
            await VerifyInvalidCommandIsNotReportedAsInstalledAsync(tempRoot);
            await VerifyCandidateFailureReasonsAreSafeAndActionableAsync(tempRoot);
            await VerifyNativePayloadFallbackRequiresBrokenWrapperAsync(tempRoot);
            await VerifySingleBrokenNpmShimWithoutPayloadIsNeverProbedAsync(tempRoot);
            await VerifyNativeFallbackRejectsUnmarkedWrapperAsync(tempRoot);
            await VerifyNativeManifestMismatchIsRejectedAsync(tempRoot);
            await VerifyValidWrapperWinsOverNativePayloadAsync(tempRoot);
            await VerifyNativeVersionMustMatchRootPackageAsync(tempRoot);
            await VerifyRealCmdAndPowerShellVersionProbesAsync(tempRoot);
            await VerifyCancellationAsync();
            Console.WriteLine("PASS: OpenCode detection probes commands safely and keeps installation disconnected.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); }
            catch { }
        }
    }

    private static async Task VerifyMissingCommandIsReadOnlyAsync(string tempRoot)
    {
        string empty = MakeDirectory(tempRoot, "empty");
        var runner = new FakeRunner(_ => new OpenCodeVersionProbeResult(false));
        string originalPath = empty;
        var service = new OpenCodeInstallationService(
            runner,
            () => [empty],
            () => originalPath,
            value => originalPath = value,
            MakeDirectory(tempRoot, "managed"));

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(!result.IsInstalled && result.Path is null && result.Version is null, "A missing command was reported as installed.");
        Require(runner.ProbedPaths.Count == 0, "The detector started a command that did not exist.");
        Require(originalPath == empty, "A missing OpenCode command changed PATH.");
    }

    private static async Task VerifyFreshEnvironmentPathIsProbedAndRefreshedAsync(string tempRoot)
    {
        string stale = MakeDirectory(tempRoot, "stale path with spaces");
        string refreshed = MakeDirectory(tempRoot, "refreshed path with spaces");
        File.WriteAllText(Path.Combine(stale, "opencode.cmd"), "fixture");
        File.WriteAllText(Path.Combine(refreshed, "opencode.exe"), "fixture");
        string processPath = stale;
        var runner = new FakeRunner(path => path.EndsWith("opencode.exe", StringComparison.OrdinalIgnoreCase)
            ? new OpenCodeVersionProbeResult(true, "1.2.3")
            : new OpenCodeVersionProbeResult(false, Error: "stale npm shim"));
        var service = new OpenCodeInstallationService(
            runner,
            () => [stale, refreshed],
            () => processPath,
            value => processPath = value,
            MakeDirectory(tempRoot, "managed"));

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(result.IsInstalled && result.Version == "1.2.3", "The valid command from refreshed PATH was not detected.");
        Require(result.Path == Path.Combine(refreshed, "opencode.exe"), "The detector returned the wrong resolved command path.");
        Require(runner.ProbedPaths.SequenceEqual([Path.Combine(stale, "opencode.cmd"), Path.Combine(refreshed, "opencode.exe")]),
            "The detector did not probe PATH candidates in order.");
        Require(processPath.Split(Path.PathSeparator).Any(path => string.Equals(Path.GetFullPath(path), refreshed, StringComparison.OrdinalIgnoreCase)),
            "The successful refreshed path was not added to the process PATH.");
        Require(string.Equals(Path.GetFullPath(processPath.Split(Path.PathSeparator)[0]), refreshed, StringComparison.OrdinalIgnoreCase),
            "The validated command directory did not take priority over a broken earlier command.");
    }

    private static async Task VerifyPowerShellCommandCanBeDetectedAsync(string tempRoot)
    {
        string directory = MakeDirectory(tempRoot, "managed powershell");
        string managed = MakeDirectory(tempRoot, "managed app bin");
        File.WriteAllText(Path.Combine(directory, "opencode.ps1"), "fixture");
        string processPath = "C:\\old-path";
        var runner = new FakeRunner(_ => new OpenCodeVersionProbeResult(true, "2.0.0"));
        var service = new OpenCodeInstallationService(runner, () => [directory], () => processPath, value => processPath = value, managed);

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(result.IsInstalled && result.Path?.EndsWith("opencode.ps1", StringComparison.OrdinalIgnoreCase) == true,
            "A PowerShell OpenCode shim was not discovered.");
        Require(result.Version == "2.0.0", "The detected version was not returned.");
    }

    private static async Task VerifyInvalidCommandIsNotReportedAsInstalledAsync(string tempRoot)
    {
        string directory = MakeDirectory(tempRoot, "invalid");
        File.WriteAllText(Path.Combine(directory, "opencode.exe"), "fixture");
        string processPath = directory;
        var runner = new FakeRunner(_ => new OpenCodeVersionProbeResult(false, Error: "bad executable"));
        var service = new OpenCodeInstallationService(runner, () => [directory], () => processPath, value => processPath = value,
            MakeDirectory(tempRoot, "managed-invalid"));

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(!result.IsInstalled && result.Message.Contains("version check", StringComparison.OrdinalIgnoreCase),
            "A file that failed --version was reported as installed.");
        Require(result.Message.Contains(Path.Combine(directory, "opencode.exe"), StringComparison.OrdinalIgnoreCase),
            "The failed candidate path was omitted from the detection message.");
        Require(processPath == directory, "A failed command changed PATH.");
    }

    private static async Task VerifyCandidateFailureReasonsAreSafeAndActionableAsync(string tempRoot)
    {
        string directory = MakeDirectory(tempRoot, "failure reasons");
        string invalidPath = Path.Combine(directory, "opencode.exe");
        string timeoutPath = Path.Combine(directory, "opencode.cmd");
        string exitPath = Path.Combine(directory, "opencode.ps1");
        File.WriteAllText(invalidPath, "fixture");
        File.WriteAllText(timeoutPath, "fixture");
        File.WriteAllText(exitPath, "fixture");

        var runner = new FakeRunner(path => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".exe" => new OpenCodeVersionProbeResult(false,
                Error: "not a valid Windows executable (Win32 error 193)"),
            ".cmd" => new OpenCodeVersionProbeResult(false,
                Error: "The OpenCode version check timed out."),
            ".ps1" => new OpenCodeVersionProbeResult(false,
                Error: "The command exited with code 7 without a valid OpenCode version. Secret=do-not-display"),
            _ => new OpenCodeVersionProbeResult(false, Error: "unexpected")
        });
        var service = new OpenCodeInstallationService(
            runner,
            () => [directory],
            () => directory,
            _ => { },
            MakeDirectory(tempRoot, "managed-failure-reasons"));

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(!result.IsInstalled, "Failed candidates were reported as installed.");
        Require(result.Message.Contains(invalidPath, StringComparison.OrdinalIgnoreCase) &&
                result.Message.Contains(timeoutPath, StringComparison.OrdinalIgnoreCase) &&
                result.Message.Contains(exitPath, StringComparison.OrdinalIgnoreCase),
            "One or more failed candidate paths were omitted from the detection message.");
        Require(result.Message.Contains("Win32 error 193", StringComparison.OrdinalIgnoreCase),
            "Invalid executable diagnostics did not identify Win32 error 193.");
        Require(result.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase),
            "Timeout diagnostics were omitted.");
        Require(result.Message.Contains("exited with code 7", StringComparison.OrdinalIgnoreCase),
            "Nonzero exit diagnostics were omitted.");
        Require(!result.Message.Contains("do-not-display", StringComparison.Ordinal),
            "Raw probe detail was exposed in the detection message.");
    }

    private static async Task VerifyNativePayloadFallbackRequiresBrokenWrapperAsync(string tempRoot)
    {
        string directory = MakeDirectory(tempRoot, "native fallback");
        string nativeExecutable = WriteNativePackageFixture(directory, brokenWrapper: true);
        WriteBrokenNpmShims(directory);
        string processPath = directory;
        var runner = new FakeRunner(path => path.Equals(nativeExecutable, StringComparison.OrdinalIgnoreCase)
            ? new OpenCodeVersionProbeResult(true, "1.18.32")
            : new OpenCodeVersionProbeResult(false, Error: "shim failed"));
        var service = new OpenCodeInstallationService(
            runner,
            () => [directory],
            () => processPath,
            value => processPath = value,
            MakeDirectory(tempRoot, "managed-native-fallback"));

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(result.IsInstalled && result.Path == nativeExecutable && result.Version == "1.18.32",
            "A valid existing native payload was not selected after both shims failed.");
        Require(result.Message.Contains("existing native payload", StringComparison.OrdinalIgnoreCase),
            "Native payload use was not identified in the detection message.");
        Require(runner.ProbedPaths.Contains(nativeExecutable, StringComparer.OrdinalIgnoreCase),
            "The native payload was not verified with --version.");
        Require(runner.ProbedPaths.SequenceEqual([nativeExecutable], StringComparer.OrdinalIgnoreCase),
            "A broken npm CMD/PowerShell shim or its postinstall placeholder was passed to the runner.");
        Require(string.Equals(Path.GetFullPath(processPath.Split(Path.PathSeparator)[0]),
                Path.GetFullPath(Path.GetDirectoryName(nativeExecutable)!), StringComparison.OrdinalIgnoreCase),
            "Native fallback did not refresh only the app process PATH with its bin directory.");
        File.Delete(Path.Combine(directory, "opencode.ps1"));
        runner.ProbedPaths.Clear();
        result = await service.CheckAsync();
        Require(result.IsInstalled && result.Path == nativeExecutable &&
                runner.ProbedPaths.SequenceEqual([nativeExecutable], StringComparer.OrdinalIgnoreCase),
            "A lone broken CMD shim should select only the verified native payload without executing the shim.");
    }

    private static async Task VerifySingleBrokenNpmShimWithoutPayloadIsNeverProbedAsync(string tempRoot)
    {
        string directory = MakeDirectory(tempRoot, "single broken npm shim");
        WriteBrokenNpmShims(directory, includePowerShell: false);
        var runner = new FakeRunner(_ => new OpenCodeVersionProbeResult(false, Error: "must not run"));
        var service = new OpenCodeInstallationService(
            runner,
            () => [directory],
            () => directory,
            _ => { },
            MakeDirectory(tempRoot, "managed-single-broken-shim"));

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(!result.IsInstalled, "An incomplete broken npm shim was reported as installed.");
        Require(runner.ProbedPaths.Count == 0,
            "A lone npm shim pointing to the known postinstall placeholder was executed without a native fallback.");
    }

    private static async Task VerifyNativeFallbackRejectsUnmarkedWrapperAsync(string tempRoot)
    {
        string directory = MakeDirectory(tempRoot, "native unmarked wrapper");
        string nativeExecutable = WriteNativePackageFixture(directory, brokenWrapper: false);
        WriteBrokenNpmShims(directory);
        var runner = new FakeRunner(path => path.Equals(nativeExecutable, StringComparison.OrdinalIgnoreCase)
            ? new OpenCodeVersionProbeResult(true, "1.18.32")
            : new OpenCodeVersionProbeResult(false, Error: "shim failed"));
        var service = new OpenCodeInstallationService(
            runner,
            () => [directory],
            () => directory,
            _ => { },
            MakeDirectory(tempRoot, "managed-native-unmarked"));

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(!result.IsInstalled, "A native payload was used without the exact broken-wrapper marker.");
        Require(!runner.ProbedPaths.Contains(nativeExecutable, StringComparer.OrdinalIgnoreCase),
            "An unmarked wrapper still triggered native fallback probing.");
    }

    private static async Task VerifyNativeManifestMismatchIsRejectedAsync(string tempRoot)
    {
        string directory = MakeDirectory(tempRoot, "native manifest mismatch");
        string nativeExecutable = WriteNativePackageFixture(directory, brokenWrapper: true, nativeVersion: "1.18.31");
        WriteBrokenNpmShims(directory);
        var runner = new FakeRunner(_ => new OpenCodeVersionProbeResult(false, Error: "shim failed"));
        var service = new OpenCodeInstallationService(
            runner,
            () => [directory],
            () => directory,
            _ => { },
            MakeDirectory(tempRoot, "managed-native-manifest-mismatch"));

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(!result.IsInstalled, "A native package with a mismatched manifest version was accepted.");
        Require(!runner.ProbedPaths.Contains(nativeExecutable, StringComparer.OrdinalIgnoreCase),
            "A native executable with a mismatched manifest was probed.");
        Require(runner.ProbedPaths.Count == 0,
            "A known broken npm shim was passed to the fake process runner.");
    }

    private static async Task VerifyValidWrapperWinsOverNativePayloadAsync(string tempRoot)
    {
        string directory = MakeDirectory(tempRoot, "valid wrapper preference");
        string nativeExecutable = WriteNativePackageFixture(directory, brokenWrapper: true);
        string wrapper = Path.Combine(directory, "opencode.cmd");
        File.WriteAllText(wrapper, "@echo off\r\necho OpenCode v1.18.32\r\n");
        File.WriteAllText(Path.Combine(directory, "opencode.ps1"), "Write-Output 'OpenCode v1.18.32'\r\n");
        var runner = new FakeRunner(path => path.Equals(wrapper, StringComparison.OrdinalIgnoreCase)
            ? new OpenCodeVersionProbeResult(true, "1.18.32")
            : new OpenCodeVersionProbeResult(false, Error: "shim failed"));
        var service = new OpenCodeInstallationService(
            runner,
            () => [directory],
            () => directory,
            _ => { },
            MakeDirectory(tempRoot, "managed-wrapper-preference"));

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(result.IsInstalled && result.Path == wrapper,
            "A valid wrapper was not preferred over the existing native payload.");
        Require(!runner.ProbedPaths.Contains(nativeExecutable, StringComparer.OrdinalIgnoreCase),
            "The native fallback was probed even though the wrapper succeeded.");
        Require(runner.ProbedPaths.SequenceEqual([wrapper], StringComparer.OrdinalIgnoreCase),
            "A healthy custom shim did not retain priority over fallback discovery.");
    }

    private static async Task VerifyNativeVersionMustMatchRootPackageAsync(string tempRoot)
    {
        string directory = MakeDirectory(tempRoot, "native version mismatch");
        string nativeExecutable = WriteNativePackageFixture(directory, brokenWrapper: true);
        WriteBrokenNpmShims(directory);
        var runner = new FakeRunner(path => path.Equals(nativeExecutable, StringComparison.OrdinalIgnoreCase)
            ? new OpenCodeVersionProbeResult(true, "1.18.31")
            : new OpenCodeVersionProbeResult(false, Error: "shim failed"));
        var service = new OpenCodeInstallationService(
            runner,
            () => [directory],
            () => directory,
            _ => { },
            MakeDirectory(tempRoot, "managed-native-version-mismatch"));

        AutoInstallDetectionResult result = await service.CheckAsync();
        Require(!result.IsInstalled, "A native payload whose --version disagreed with package.json was accepted.");
        Require(runner.ProbedPaths.SequenceEqual([nativeExecutable], StringComparer.OrdinalIgnoreCase),
            "Only the version-mismatched native payload should be probed before rejection.");
    }

    private static void WriteFailedShims(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "opencode.cmd"), "fixture");
        File.WriteAllText(Path.Combine(directory, "opencode.ps1"), "fixture");
    }

    private static void WriteBrokenNpmShims(string directory, bool includePowerShell = true)
    {
        string packageRoot = Path.Combine(directory, "node_modules", "opencode-ai");
        string wrapperDirectory = Path.Combine(packageRoot, "bin");
        Directory.CreateDirectory(wrapperDirectory);
        string brokenWrapperPath = Path.Combine(wrapperDirectory, "opencode.exe");
        const string marker = "Error: opencode-ai's postinstall script was not run.";
        if (!File.Exists(brokenWrapperPath))
            File.WriteAllBytes(brokenWrapperPath, Encoding.UTF8.GetBytes(marker.PadRight(479, ' ')));

        string commandTarget = "node_modules\\opencode-ai\\bin\\opencode.exe";
        File.WriteAllText(Path.Combine(directory, "opencode.cmd"), $"@echo off\r\n\"%~dp0{commandTarget}\" %*\r\n");
        if (includePowerShell)
            File.WriteAllText(Path.Combine(directory, "opencode.ps1"),
                "& (Join-Path $PSScriptRoot 'node_modules/opencode-ai/bin/opencode.exe') @args\r\n");
    }

    private static string WriteNativePackageFixture(
        string directory,
        bool brokenWrapper,
        string rootVersion = "1.18.32",
        string nativeVersion = "1.18.32")
    {
        string packageRoot = Path.Combine(directory, "node_modules", "opencode-ai");
        string rootBin = Path.Combine(packageRoot, "bin");
        string nativeRoot = Path.Combine(packageRoot, "node_modules", "opencode-windows-x64");
        string nativeBin = Path.Combine(nativeRoot, "bin");
        Directory.CreateDirectory(rootBin);
        Directory.CreateDirectory(nativeBin);
        File.WriteAllText(Path.Combine(packageRoot, "package.json"),
            $"{{\"name\":\"opencode-ai\",\"version\":\"{rootVersion}\"}}");
        File.WriteAllText(Path.Combine(rootBin, "opencode.exe"),
            brokenWrapper ? "Error: opencode-ai's postinstall script was not run." : "normal executable");
        File.WriteAllText(Path.Combine(nativeRoot, "package.json"),
            $"{{\"name\":\"opencode-windows-x64\",\"version\":\"{nativeVersion}\"}}");
        string nativeExecutable = Path.Combine(nativeBin, "opencode.exe");
        File.WriteAllText(nativeExecutable, "native executable fixture");
        return nativeExecutable;
    }

    private static async Task VerifyRealCmdAndPowerShellVersionProbesAsync(string tempRoot)
    {
        string directory = MakeDirectory(tempRoot, "real command fixtures with spaces");
        string cmdPath = Path.Combine(directory, "opencode.cmd");
        string ps1Path = Path.Combine(directory, "opencode.ps1");
        await File.WriteAllTextAsync(cmdPath, "@echo off\r\necho OpenCode v3.4.5\r\n");
        await File.WriteAllTextAsync(ps1Path, "Write-Output 'OpenCode 4.5.6'\r\n");

        var runner = new WindowsOpenCodeCommandRunner();
        OpenCodeVersionProbeResult cmd = await runner.ProbeVersionAsync(cmdPath, directory, CancellationToken.None);
        Require(cmd.Succeeded && cmd.Version == "3.4.5", "The .cmd --version probe failed for a path containing spaces: " + cmd.Error);
        OpenCodeVersionProbeResult ps1 = await runner.ProbeVersionAsync(ps1Path, directory, CancellationToken.None);
        Require(ps1.Succeeded && ps1.Version == "4.5.6", "The .ps1 --version probe failed: " + ps1.Error);

        string slowPs1Path = Path.Combine(directory, "slow-version.ps1");
        await File.WriteAllTextAsync(slowPs1Path, "Start-Sleep -Seconds 9; Write-Output 'OpenCode 5.6.7'\r\n");
        OpenCodeVersionProbeResult slow = await runner.ProbeVersionAsync(slowPs1Path, directory, CancellationToken.None);
        Require(slow.Succeeded && slow.Version == "5.6.7",
            "The version probe rejected a valid command that needed more than the old 8-second budget: " + slow.Error);

        string invalidPath = Path.Combine(directory, "invalid-version.cmd");
        await File.WriteAllTextAsync(invalidPath, "@echo off\r\necho command succeeded\r\n");
        OpenCodeVersionProbeResult invalid = await runner.ProbeVersionAsync(invalidPath, directory, CancellationToken.None);
        Require(!invalid.Succeeded, "A command with no plausible version string passed validation.");

        byte[] neImage = new byte[0x42];
        neImage[0] = (byte)'M'; neImage[1] = (byte)'Z'; neImage[0x3C] = 0x40;
        neImage[0x40] = (byte)'N'; neImage[0x41] = (byte)'E';
        var invalidImages = new (string Name, byte[] Bytes)[]
        {
            ("text", Encoding.UTF8.GetBytes("not a Windows executable")),
            ("truncated-mz", [(byte)'M', (byte)'Z']),
            ("ne", neImage)
        };
        foreach (var image in invalidImages)
        {
            string invalidExecutablePath = Path.Combine(directory, "invalid-" + image.Name + ".exe");
            await File.WriteAllBytesAsync(invalidExecutablePath, image.Bytes);
            OpenCodeVersionProbeResult invalidExecutable = await runner.ProbeVersionAsync(
                invalidExecutablePath, directory, CancellationToken.None);
            Require(!invalidExecutable.Succeeded &&
                    invalidExecutable.Error?.Contains("not a valid Windows executable", StringComparison.OrdinalIgnoreCase) == true &&
                    invalidExecutable.Error.Contains("not started", StringComparison.OrdinalIgnoreCase),
                $"The {image.Name} image was not rejected before process start: {invalidExecutable.Error}");
        }
    }

    private static async Task VerifyCancellationAsync()
    {
        var runner = new FakeRunner(_ => new OpenCodeVersionProbeResult(false));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var service = new OpenCodeInstallationService(runner, () => [], () => null, _ => { }, "C:\\managed");
        bool canceledAsRequested = false;
        try
        {
            await service.CheckAsync(canceled.Token);
        }
        catch (OperationCanceledException)
        {
            canceledAsRequested = true;
        }
        Require(canceledAsRequested, "CheckAsync ignored cancellation.");

    }

    private static string MakeDirectory(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FakeRunner(Func<string, OpenCodeVersionProbeResult> probe) : IOpenCodeCommandRunner
    {
        public List<string> ProbedPaths { get; } = [];

        public Task<OpenCodeVersionProbeResult> ProbeVersionAsync(
            string commandPath,
            string searchPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbedPaths.Add(commandPath);
            return Task.FromResult(probe(commandPath));
        }
    }
}
