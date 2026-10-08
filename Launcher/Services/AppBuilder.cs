using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ILauncher.Models;

namespace ILauncher.Services;

internal sealed record AppBuildOutput(string ExecutablePath, string WorkingDirectory);

internal sealed class AppBuilder
{
    private const int DiagnosticCharacterLimit = 12_000;
    private const string WorkspaceMarkerName = ".i-luncher-workspace.json";
    private readonly string _rootDirectory;

    internal AppBuilder(string rootDirectory)
    {
        _rootDirectory = NormalizeDirectory(rootDirectory);
    }

    internal static string ResolveCatalogRoot(string rootDirectory) =>
        new AppBuilder(rootDirectory).GetBuildSourceDirectory();

    internal AppBuildOutput? TryGetExistingOutput(LauncherEntry entry)
    {
        AppBuildOutput output;
        string fingerprintDirectory;
        if (File.Exists(Path.Combine(_rootDirectory, WorkspaceMarkerName)))
        {
            output = new AppBuildOutput(entry.ExecutablePath, entry.WorkingDirectory);
            fingerprintDirectory = _rootDirectory;
        }
        else
        {
            if (!TryGetExternalWorkspace(out var workspaceDirectory)) return null;
            output = MapToWorkspace(entry, workspaceDirectory);
            fingerprintDirectory = workspaceDirectory;
        }
        if (!File.Exists(output.ExecutablePath) || !Directory.Exists(output.WorkingDirectory)) return null;
        if (entry.IsAutoDiscoveredSourceProject && !HasMatchingSourceFingerprint(entry, fingerprintDirectory)) return null;
        return output;
    }

    internal async Task<AppBuildOutput> BuildAsync(LauncherEntry entry)
    {
        var definition = entry.BuildDefinition
            ?? throw new InvalidOperationException($"{entry.Name}에는 빌드 방법이 구성되어 있지 않습니다.");
        if (!File.Exists(definition.ProjectPath))
            throw new LauncherBuildException($"{entry.Name} 프로젝트를 찾을 수 없습니다: {definition.ProjectPath}");
        if (definition.BootstrapProjectPath is not null && !File.Exists(definition.BootstrapProjectPath))
            throw new LauncherBuildException($"{entry.Name} 부트스트랩 프로젝트를 찾을 수 없습니다: {definition.BootstrapProjectPath}");

        var buildSourceDirectory = GetBuildSourceDirectory();
        var buildScript = Path.Combine(buildSourceDirectory, "Build.ps1");
        if (!File.Exists(buildScript))
            throw new LauncherBuildException($"빌드 스크립트를 찾을 수 없습니다: {buildScript}");

        await EnsureDotNetSdkAsync(buildSourceDirectory).ConfigureAwait(false);

        string? sourceFingerprintBefore = entry.IsAutoDiscoveredSourceProject
            ? await Task.Run(() => CreateBuildInputFingerprint(entry)).ConfigureAwait(false)
            : null;

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            throw new LauncherBuildException("앱 빌드에 필요한 Windows PowerShell을 찾을 수 없습니다.");

        string[] arguments =
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", buildScript, "-App", entry.Id
        };
        if (entry.IsAutoDiscoveredSourceProject)
        {
            AppInstaller.EnsureContained(_rootDirectory, definition.ProjectPath, "source project");
            arguments = [.. arguments, "-Project", Path.GetRelativePath(_rootDirectory, definition.ProjectPath)];
        }
        var runsInsideManagedWorkspace = File.Exists(Path.Combine(_rootDirectory, WorkspaceMarkerName));

        ProcessResult result;
        try
        {
            result = await RunProcessAsync(powershell, arguments, buildSourceDirectory).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            throw new LauncherBuildException("앱 빌드 프로세스를 시작하지 못했습니다.", ex);
        }

        if (result.ExitCode != 0)
            throw new LauncherBuildException(FormatBuildFailure(entry.Name, result.ExitCode, result.Diagnostics));

        AppBuildOutput output;
        if (runsInsideManagedWorkspace)
        {
            output = new AppBuildOutput(entry.ExecutablePath, entry.WorkingDirectory);
        }
        else
        {
            var workspaceDirectory = GetExternalWorkspaceDirectory();
            if (!IsExpectedWorkspace(workspaceDirectory))
                throw new LauncherBuildException("외부 빌드 작업공간을 확인하지 못했습니다.");
            output = MapToWorkspace(entry, workspaceDirectory);
        }

        if (!File.Exists(output.ExecutablePath))
            throw new LauncherBuildException($"빌드는 완료됐지만 실행 파일을 찾을 수 없습니다: {output.ExecutablePath}");
        if (!Directory.Exists(output.WorkingDirectory))
            throw new LauncherBuildException($"빌드는 완료됐지만 작업 폴더를 찾을 수 없습니다: {output.WorkingDirectory}");
        if (entry.IsAutoDiscoveredSourceProject)
        {
            string fingerprintDirectory = runsInsideManagedWorkspace ? _rootDirectory : GetExternalWorkspaceDirectory();
            string? sourceFingerprintAfter = await Task.Run(() => CreateBuildInputFingerprint(entry)).ConfigureAwait(false);
            if (sourceFingerprintBefore is not null && sourceFingerprintAfter is not null
                && !string.Equals(sourceFingerprintBefore, sourceFingerprintAfter, StringComparison.Ordinal))
            {
                DeleteSourceFingerprint(entry, fingerprintDirectory);
                throw new LauncherBuildException($"{entry.Name} source files changed while the build was running. Launch it again to build the current source.");
            }
            if (sourceFingerprintBefore is not null && sourceFingerprintAfter is not null
                && string.Equals(sourceFingerprintBefore, sourceFingerprintAfter, StringComparison.Ordinal))
                SaveSourceFingerprint(entry, fingerprintDirectory, sourceFingerprintAfter);
            else
                DeleteSourceFingerprint(entry, fingerprintDirectory);
        }
        return output;
    }

    private bool HasMatchingSourceFingerprint(LauncherEntry entry, string workspaceDirectory)
    {
        if (entry.SourceProjectDirectory is null || entry.BuildDefinition is null) return false;
        try
        {
            string path = GetSourceFingerprintPath(entry, workspaceDirectory);
            string? currentFingerprint = CreateBuildInputFingerprint(entry);
            if (currentFingerprint is null) return false;
            return File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(),
                currentFingerprint, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private string? CreateBuildInputFingerprint(LauncherEntry entry) => entry.SourceProjectDirectory is null || entry.BuildDefinition is null
        ? null
        : SourceProjectDiscovery.CreateBuildInputFingerprint(_rootDirectory, entry.SourceProjectDirectory,
            entry.BuildDefinition.ProjectPath);

    private static void SaveSourceFingerprint(LauncherEntry entry, string workspaceDirectory, string fingerprint)
    {
        try
        {
            File.WriteAllText(GetSourceFingerprintPath(entry, workspaceDirectory), fingerprint, Encoding.ASCII);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A failed cache write only causes a future launch to rebuild.
        }
    }

    private static void DeleteSourceFingerprint(LauncherEntry entry, string workspaceDirectory)
    {
        try { File.Delete(GetSourceFingerprintPath(entry, workspaceDirectory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static string GetSourceFingerprintPath(LauncherEntry entry, string workspaceDirectory) =>
        Path.Combine(workspaceDirectory, ".i-luncher-source-" + entry.Id + ".sha256");

    private async Task EnsureDotNetSdkAsync(string workingDirectory)
    {
        ProcessResult result;
        try
        {
            result = await RunProcessAsync("dotnet", ["--list-sdks"], workingDirectory).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            throw new LauncherBuildException("앱을 빌드하려면 .NET SDK가 필요합니다. .NET SDK를 설치한 뒤 다시 실행하세요.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new LauncherBuildException(".NET SDK를 확인하지 못했습니다.", ex);
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Diagnostics))
            throw new LauncherBuildException("앱을 빌드할 수 있는 .NET SDK를 찾지 못했습니다. .NET SDK를 설치한 뒤 다시 실행하세요.");
    }

    private AppBuildOutput MapToWorkspace(LauncherEntry entry, string workspaceDirectory)
    {
        AppInstaller.EnsureContained(_rootDirectory, entry.ExecutablePath, "빌드 실행 파일");
        AppInstaller.EnsureContained(_rootDirectory, entry.WorkingDirectory, "빌드 작업 폴더");
        return new AppBuildOutput(
            Path.GetFullPath(Path.GetRelativePath(_rootDirectory, entry.ExecutablePath), workspaceDirectory),
            Path.GetFullPath(Path.GetRelativePath(_rootDirectory, entry.WorkingDirectory), workspaceDirectory));
    }

    private bool TryGetExternalWorkspace(out string workspaceDirectory)
    {
        workspaceDirectory = GetExternalWorkspaceDirectory();
        return !Path.GetFullPath(workspaceDirectory).Equals(_rootDirectory, StringComparison.OrdinalIgnoreCase)
            && IsExpectedWorkspace(workspaceDirectory);
    }

    private string GetBuildSourceDirectory()
    {
        var markerPath = Path.Combine(_rootDirectory, WorkspaceMarkerName);
        if (!File.Exists(markerPath)) return _rootDirectory;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
            var marker = document.RootElement;
            if (!marker.TryGetProperty("sourcePath", out var sourcePathValue)
                || string.IsNullOrWhiteSpace(sourcePathValue.GetString()))
                throw new LauncherBuildException("외부 빌드 작업공간 표식에 원본 경로가 없습니다.");

            var sourceDirectory = NormalizeDirectory(sourcePathValue.GetString()!);
            if (!Directory.Exists(sourceDirectory))
                throw new LauncherBuildException($"원본 프로젝트 폴더를 찾을 수 없습니다: {sourceDirectory}");

            var expectedWorkspace = GetWorkspaceDirectory(sourceDirectory);
            if (!expectedWorkspace.Equals(_rootDirectory, StringComparison.OrdinalIgnoreCase))
                throw new LauncherBuildException("외부 빌드 작업공간 표식이 현재 폴더와 일치하지 않습니다.");
            return sourceDirectory;
        }
        catch (LauncherBuildException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException)
        {
            throw new LauncherBuildException("외부 빌드 작업공간 표식을 읽지 못했습니다.", ex);
        }
    }

    private string GetExternalWorkspaceDirectory()
    {
        return GetWorkspaceDirectory(_rootDirectory);
    }

    private static string GetWorkspaceDirectory(string sourceDirectory)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new LauncherBuildException("외부 빌드 작업공간의 기본 폴더를 확인하지 못했습니다.");
        var normalizedSource = NormalizeDirectory(sourceDirectory).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSource))).ToLowerInvariant();
        return Path.GetFullPath(Path.Combine(localAppData, "i-luncher", "Workspaces", hash[..24]));
    }

    private static string NormalizeDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullPath)!;
        return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? root : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private bool IsExpectedWorkspace(string workspaceDirectory)
    {
        var markerPath = Path.Combine(workspaceDirectory, WorkspaceMarkerName);
        try
        {
            if (!File.Exists(markerPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
            var marker = document.RootElement;
            var normalizedRoot = _rootDirectory.ToUpperInvariant();
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot))).ToLowerInvariant();
            return marker.TryGetProperty("version", out var version) && version.GetInt32() == 1
                && marker.TryGetProperty("sourcePathHash", out var hash)
                && string.Equals(hash.GetString(), expectedHash, StringComparison.Ordinal)
                && marker.TryGetProperty("normalizedSourcePath", out var normalized)
                && string.Equals(normalized.GetString(), normalizedRoot, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static string FormatBuildFailure(string appName, int exitCode, string diagnostics)
    {
        var message = $"{appName} 빌드가 종료 코드 {exitCode}(으)로 실패했습니다.";
        return string.IsNullOrWhiteSpace(diagnostics) ? message : message + Environment.NewLine + diagnostics.Trim();
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var diagnostics = new BoundedTextBuffer(DiagnosticCharacterLimit);
        var outputTask = CopyToBufferAsync(process.StandardOutput, diagnostics);
        var errorTask = CopyToBufferAsync(process.StandardError, diagnostics);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, diagnostics.ToString());
    }

    private static async Task CopyToBufferAsync(TextReader reader, BoundedTextBuffer buffer)
    {
        var characters = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(characters).ConfigureAwait(false)) != 0)
            buffer.Append(characters.AsSpan(0, count));
    }

    private sealed record ProcessResult(int ExitCode, string Diagnostics);

    private sealed class BoundedTextBuffer(int capacity)
    {
        private readonly StringBuilder _contents = new(capacity);
        private readonly object _sync = new();

        internal void Append(ReadOnlySpan<char> value)
        {
            lock (_sync)
            {
                if (value.Length >= capacity)
                {
                    _contents.Clear();
                    _contents.Append(value[^capacity..]);
                    return;
                }
                var overflow = _contents.Length + value.Length - capacity;
                if (overflow > 0) _contents.Remove(0, overflow);
                _contents.Append(value);
            }
        }

        public override string ToString()
        {
            lock (_sync) return _contents.ToString();
        }
    }
}

internal sealed class LauncherBuildException : InvalidOperationException
{
    internal LauncherBuildException(string message) : base(message) { }
    internal LauncherBuildException(string message, Exception innerException) : base(message, innerException) { }
}
