using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Reflection.PortableExecutable;

namespace TerminalNotes.Services;

public sealed record OpenCodeVersionProbeResult(bool Succeeded, string? Version = null, string? Error = null);

/// <summary>Runs the version check for one resolved Windows OpenCode command.</summary>
public interface IOpenCodeCommandRunner
{
    Task<OpenCodeVersionProbeResult> ProbeVersionAsync(
        string commandPath,
        string searchPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-only OpenCode discovery. It probes PATH commands and the app's per-user bin location;
/// it does not download, replace, or otherwise install files.
/// </summary>
public sealed class OpenCodeInstallationService
{
    private static readonly string[] CommandExtensions = [".exe", ".cmd", ".ps1"];
    private static readonly string[] NativeWindowsX64Packages = [
        "opencode-windows-x64",
        "opencode-windows-x64-baseline"];
    private const long SmallBrokenWrapperLimit = 64 * 1024;
    private const string BrokenWrapperMarker = "opencode-ai's postinstall script was not run";
    private readonly IOpenCodeCommandRunner _commandRunner;
    private readonly Func<IReadOnlyList<string>> _searchPaths;
    private readonly Func<string?> _processPath;
    private readonly Action<string> _setProcessPath;
    private readonly string _managedBinPath;

    public OpenCodeInstallationService(
        IOpenCodeCommandRunner? commandRunner = null,
        Func<IReadOnlyList<string>>? searchPaths = null,
        Func<string?>? processPath = null,
        Action<string>? setProcessPath = null,
        string? managedBinPath = null)
    {
        _commandRunner = commandRunner ?? new WindowsOpenCodeCommandRunner();
        _searchPaths = searchPaths ?? GetEnvironmentSearchPaths;
        _processPath = processPath ?? (() => Environment.GetEnvironmentVariable("PATH"));
        _setProcessPath = setProcessPath ?? (value => Environment.SetEnvironmentVariable("PATH", value, EnvironmentVariableTarget.Process));
        _managedBinPath = managedBinPath ?? GetManagedBinPath();
    }

    public Task<AutoInstallDetectionResult> CheckAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => CheckCoreAsync(cancellationToken), cancellationToken);

    private async Task<AutoInstallDetectionResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> paths;
        try
        {
            paths = GetDistinctPaths(_searchPaths().Append(_managedBinPath));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AutoInstallDetectionResult(false,
                "Could not read the OpenCode command search paths (" + exception.GetType().Name + ").");
        }

        string childSearchPath = string.Join(Path.PathSeparator, paths);
        bool foundCandidate = false;
        var failures = new List<string>();
        foreach (string directory in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool foundCommandShim = false;
            bool foundPowerShellShim = false;
            bool commandShimFailed = true;
            bool powerShellShimFailed = true;
            foreach (string extension in CommandExtensions)
            {
                string commandPath;
                try
                {
                    commandPath = Path.Combine(directory, "opencode" + extension);
                    if (!File.Exists(commandPath))
                        continue;
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    continue;
                }

                foundCandidate = true;
                if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)) foundCommandShim = true;
                if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)) foundPowerShellShim = true;
                // npm shims can invoke an .exe-named text placeholder. Trying the
                // shim first raises Windows compatibility dialogs before fallback.
                if (IsKnownBrokenNpmShim(commandPath) ||
                    extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && IsSmallBrokenWrapper(commandPath))
                {
                    failures.Add($"{commandPath}: npm postinstall placeholder; command not started");
                    continue;
                }
                OpenCodeVersionProbeResult probe;
                try
                {
                    probe = await _commandRunner.ProbeVersionAsync(commandPath, childSearchPath, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    probe = new OpenCodeVersionProbeResult(false, Error: DescribeProbeException(exception));
                }

                if (!probe.Succeeded || string.IsNullOrWhiteSpace(probe.Version))
                {
                    failures.Add($"{commandPath}: {DescribeProbeFailure(probe)}");
                    continue;
                }

                if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)) commandShimFailed = false;
                if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)) powerShellShimFailed = false;

                RefreshProcessPathIfNeeded(directory, paths);
                return new AutoInstallDetectionResult(
                    true,
                    $"OpenCode {probe.Version} is available.",
                    commandPath,
                    probe.Version);
            }

            if ((foundCommandShim || foundPowerShellShim) && commandShimFailed && powerShellShimFailed)
            {
                AutoInstallDetectionResult? native = await TryExistingNativePayloadAsync(
                    directory, childSearchPath, paths, cancellationToken).ConfigureAwait(false);
                if (native is not null)
                    return native;
            }
        }

        string message = foundCandidate
            ? "OpenCode candidates were found, but none passed the version check: " + string.Join("; ", failures)
            : "OpenCode is not installed or is not available on PATH.";
        return new AutoInstallDetectionResult(false, message);
    }

    private async Task<AutoInstallDetectionResult?> TryExistingNativePayloadAsync(
        string directory,
        string childSearchPath,
        IReadOnlyList<string> refreshedPaths,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() ||
            !Environment.Is64BitProcess ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return null;

        string packageRoot = Path.Combine(directory, "node_modules", "opencode-ai");
        string manifestPath = Path.Combine(packageRoot, "package.json");
        if (!TryReadPackageManifest(manifestPath, "opencode-ai", out string? packageVersion) ||
            string.IsNullOrWhiteSpace(packageVersion))
            return null;

        string brokenWrapperPath = Path.Combine(packageRoot, "bin", "opencode.exe");
        if (!IsSmallBrokenWrapper(brokenWrapperPath))
            return null;

        string nativeSearchPath = string.Join(Path.PathSeparator,
            new[] { Path.Combine(packageRoot, "node_modules") }.Concat(refreshedPaths));
        foreach (string packageName in NativeWindowsX64Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string nativeRoot = Path.Combine(packageRoot, "node_modules", packageName);
            string nativeManifest = Path.Combine(nativeRoot, "package.json");
            if (!TryReadPackageManifest(nativeManifest, packageName, out string? nativeVersion) ||
                !string.Equals(nativeVersion, packageVersion, StringComparison.Ordinal))
                continue;

            string nativeExecutable = Path.Combine(nativeRoot, "bin", "opencode.exe");
            if (!File.Exists(nativeExecutable)) continue;
            OpenCodeVersionProbeResult probe;
            try
            {
                probe = await _commandRunner.ProbeVersionAsync(
                    nativeExecutable, nativeSearchPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                probe = new OpenCodeVersionProbeResult(false, Error: DescribeProbeException(exception));
            }

            if (!probe.Succeeded || string.IsNullOrWhiteSpace(probe.Version) ||
                !string.Equals(probe.Version, packageVersion, StringComparison.Ordinal))
                continue;

            string nativeDirectory = Path.GetDirectoryName(nativeExecutable)!;
            RefreshProcessPathIfNeeded(nativeDirectory, refreshedPaths);
            return new AutoInstallDetectionResult(
                true,
                $"OpenCode {probe.Version} is available via the existing native payload ({nativeExecutable}).",
                nativeExecutable,
                probe.Version);
        }

        return null;
    }

    internal static bool IsKnownBrokenNpmShim(string commandPath)
    {
        string extension = Path.GetExtension(commandPath);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using FileStream stream = File.OpenRead(commandPath);
            if (stream.Length > SmallBrokenWrapperLimit) return false;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string shim = reader.ReadToEnd().Replace('\\', '/');
            if (!shim.Contains("node_modules/opencode-ai/bin/opencode.exe", StringComparison.OrdinalIgnoreCase)) return false;
            return IsSmallBrokenWrapper(Path.Combine(Path.GetDirectoryName(commandPath)!,
                "node_modules", "opencode-ai", "bin", "opencode.exe"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsSmallBrokenWrapper(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length > SmallBrokenWrapperLimit) return false;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd().Contains(BrokenWrapperMarker, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryReadPackageManifest(string path, string expectedName, out string? version)
    {
        version = null;
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length > SmallBrokenWrapperLimit) return false;
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("name", out JsonElement name) ||
                !string.Equals(name.GetString(), expectedName, StringComparison.Ordinal) ||
                !root.TryGetProperty("version", out JsonElement value))
                return false;
            version = value.GetString();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            DecoderFallbackException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string DescribeProbeFailure(OpenCodeVersionProbeResult probe)
    {
        string reason = probe.Error?.Trim() ?? "version probe failed";
        if (reason.Length == 0) return "version probe failed";
        string lowered = reason.ToLowerInvariant();
        Match win32Code = Regex.Match(reason, @"Win32\s+error\s+(?<code>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (win32Code.Success)
        {
            string code = win32Code.Groups["code"].Value;
            return code is "193" or "216"
                ? $"not a valid Windows executable (Win32 error {code})"
                : $"command start failed (Win32 error {code})";
        }
        if (lowered.Contains("not a valid win32", StringComparison.Ordinal) ||
            lowered.Contains("not a valid windows executable", StringComparison.Ordinal))
            return "not a valid Windows executable";
        if (lowered.Contains("timed out", StringComparison.Ordinal) || lowered.Contains("timeout", StringComparison.Ordinal))
            return "version check timed out";
        if (lowered.Contains("exit code", StringComparison.Ordinal) ||
            lowered.Contains("exited with code", StringComparison.Ordinal) ||
            lowered.Contains("nonzero", StringComparison.Ordinal))
        {
            Match exitCode = Regex.Match(reason, @"exit(?:ed)?\s+with\s+code\s+(?<code>-?\d+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return exitCode.Success
                ? $"command exited with code {exitCode.Groups["code"].Value} without a valid OpenCode version"
                : "command exited unsuccessfully without a valid OpenCode version";
        }
        if (lowered.Contains("returned no valid", StringComparison.Ordinal) ||
            lowered.Contains("no valid opencode version", StringComparison.Ordinal))
            return "command returned no valid OpenCode version";
        if (lowered.Contains("access denied", StringComparison.Ordinal))
            return "command start failed (access denied)";
        if (lowered.Contains("file not found", StringComparison.Ordinal) ||
            lowered.Contains("could not find", StringComparison.Ordinal))
            return "command start failed (file not found)";
        return "version probe failed";
    }

    internal static string DescribeProbeException(Exception exception) => exception switch
    {
        Win32Exception win32 when win32.NativeErrorCode is 193 or 216 =>
            $"not a valid Windows executable (Win32 error {win32.NativeErrorCode})",
        Win32Exception win32 => $"command start failed (Win32 error {win32.NativeErrorCode})",
        UnauthorizedAccessException => "command start failed (access denied)",
        FileNotFoundException => "command start failed (file not found)",
        TimeoutException => "version check timed out",
        _ => "command probe failed (" + exception.GetType().Name + ")"
    };

    private void RefreshProcessPathIfNeeded(string detectedDirectory, IReadOnlyList<string> refreshedPaths)
    {
        string currentPath = _processPath() ?? string.Empty;
        string[] currentEntries = GetDistinctPaths(currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        string[] allEntries = [detectedDirectory, .. GetDistinctPaths(currentEntries.Concat(refreshedPaths))
            .Where(path => !PathsEqual(path, detectedDirectory))];
        string updatedPath = string.Join(Path.PathSeparator, allEntries);
        if (string.Equals(currentPath, updatedPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _setProcessPath(updatedPath);
        }
        catch
        {
            // Detection still succeeded; this best-effort refresh only helps later child processes.
        }
    }

    private static IReadOnlyList<string> GetEnvironmentSearchPaths()
    {
        var values = new List<string>();
        AddPathValue(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process));
        AddPathValue(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        AddPathValue(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        return GetDistinctPaths(values);

        void AddPathValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            string expanded = Environment.ExpandEnvironmentVariables(value);
            values.AddRange(expanded.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    private static string GetManagedBinPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(AppContext.BaseDirectory, "OpenCode", "bin");
        return Path.Combine(localAppData, "TerminalNotes", "OpenCode", "bin");
    }

    private static string[] GetDistinctPaths(IEnumerable<string> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values)
        {
            string trimmed = value.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;

            try
            {
                string normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(trimmed));
                string key = Path.TrimEndingDirectorySeparator(normalized);
                if (seen.Add(key))
                    result.Add(key);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // An invalid PATH segment cannot contain a launchable command.
            }
        }

        return result.ToArray();
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}

internal sealed class WindowsOpenCodeCommandRunner : IOpenCodeCommandRunner
{
    private const int OutputLimit = 4096;

    public async Task<OpenCodeVersionProbeResult> ProbeVersionAsync(
        string commandPath,
        string searchPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(commandPath);
        if (OpenCodeInstallationService.IsKnownBrokenNpmShim(commandPath))
            return new OpenCodeVersionProbeResult(false, Error: "npm postinstall placeholder; command not started");
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using FileStream stream = File.OpenRead(commandPath);
                using var pe = new PEReader(stream);
                if (pe.PEHeaders.PEHeader is null)
                    return new OpenCodeVersionProbeResult(false, Error: "not a valid Windows executable (invalid PE header; not started)");
            }
            catch (BadImageFormatException)
            {
                return new OpenCodeVersionProbeResult(false, Error: "not a valid Windows executable (invalid PE header; not started)");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new OpenCodeVersionProbeResult(false, Error: OpenCodeInstallationService.DescribeProbeException(exception));
            }
        }
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["PATH"] = searchPath;

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = commandPath;
            startInfo.ArgumentList.Add("--version");
        }
        else if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (commandPath.IndexOfAny(['%', '\r', '\n', '"']) >= 0)
                return new OpenCodeVersionProbeResult(false, Error: "The command path contains characters unsafe for cmd.exe.");

            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            // cmd.exe does not follow the normal argv quoting rules, so use its documented
            // /c double-quote form after rejecting expansion and quote characters above.
            startInfo.Arguments = $"/d /s /c \"\"{commandPath}\" --version\"";
        }
        else if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            startInfo.FileName = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(commandPath);
            startInfo.ArgumentList.Add("--version");
        }
        else
        {
            return new OpenCodeVersionProbeResult(false, Error: "Unsupported OpenCode command type.");
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var standardOutput = new BoundedTextBuffer(OutputLimit);
        process.OutputDataReceived += (_, eventArgs) => standardOutput.AppendLine(eventArgs.Data);
        // Drain stderr so a noisy candidate cannot block, but never expose raw
        // stderr because it may contain credentials or user/project data.
        process.ErrorDataReceived += (_, _) => { };

        try
        {
            if (!process.Start())
                return new OpenCodeVersionProbeResult(false, Error: "The OpenCode command did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TerminalExecutionTiming.InstallationProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                process.WaitForExit();
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                    throw;
                return new OpenCodeVersionProbeResult(false, Error: "The OpenCode version check timed out.");
            }

            string output = standardOutput.ToString();
            Match match = Regex.Match(
                output,
                @"(?:^|\s)(?:OpenCode\s+)?v?(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)(?:\s|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);
            string? version = match.Success ? match.Groups["version"].Value : null;
            if (process.ExitCode != 0)
            {
                return new OpenCodeVersionProbeResult(false,
                    Error: $"The command exited with code {process.ExitCode} without a valid OpenCode version.");
            }
            if (string.IsNullOrWhiteSpace(version))
                return new OpenCodeVersionProbeResult(false, Error: "The command returned no valid OpenCode version.");

            return new OpenCodeVersionProbeResult(true, version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new OpenCodeVersionProbeResult(false, Error: OpenCodeInstallationService.DescribeProbeException(exception));
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between the check and the kill.
        }
    }

    private sealed class BoundedTextBuffer(int limit)
    {
        private readonly StringBuilder _value = new();
        private readonly object _gate = new();

        public void AppendLine(string? line)
        {
            if (line is null)
                return;
            lock (_gate)
            {
                if (_value.Length >= limit)
                    return;
                int remaining = limit - _value.Length;
                _value.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
                if (_value.Length < limit)
                    _value.AppendLine();
            }
        }

        public override string ToString()
        {
            lock (_gate)
                return _value.ToString();
        }
    }
}
