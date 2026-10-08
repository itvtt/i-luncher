using System.IO;

namespace ILauncher.Models;

public sealed class LauncherEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string Category { get; init; } = "기타";
    public bool IsDistribution { get; init; }
    public bool IsIntegrated { get; init; }
    public required string ExecutablePath { get; init; }
    public string IconPath { get; init; } = "";
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];

    internal LauncherInstallDefinition? InstallDefinition { get; init; }
    internal LauncherBuildDefinition? BuildDefinition { get; init; }
    internal string? InstallSourceDirectory { get; init; }
    internal string? InstallIconSourceDirectory { get; init; }
    internal string? InstalledSharedPayloadDirectory { get; init; }
    internal IReadOnlyList<string> InstallShortcutArguments { get; init; } = [];
    internal bool IsAutoDiscoveredSourceProject { get; init; }
    internal string? SourceProjectDirectory { get; init; }
    internal string? InstalledAppDirectory { get; init; }
    internal string? InstalledExecutableRelativePath { get; init; }
    internal string? InstalledWorkingDirectoryRelativePath { get; init; }

    public bool CanInstall => InstallDefinition is not null;
    public bool HasInstallFiles => InstallDefinition switch
    {
        { Type: LauncherInstallType.Command } install => File.Exists(install.ExecutablePath),
        { Type: LauncherInstallType.SharedPayload } => InstallSourceDirectory is not null
            && InstalledExecutableRelativePath is not null
            && File.Exists(Path.GetFullPath(InstalledExecutableRelativePath, InstallSourceDirectory)),
        { Type: LauncherInstallType.Portable } => File.Exists(ExecutablePath) || CanBuild,
        _ => false
    };
    public bool CanBuild => BuildDefinition is { } build
        && File.Exists(build.ProjectPath)
        && (build.BootstrapProjectPath is null || File.Exists(build.BootstrapProjectPath));

    public bool IsAvailable => IsIntegrated ||
        TryGetInstalledExecutablePath() is not null || File.Exists(ExecutablePath) || CanBuild;

    internal string? TryGetInstalledExecutablePath()
    {
        if (InstalledAppDirectory is null || InstalledExecutableRelativePath is null)
            return null;

        var currentVersionFile = Path.Combine(InstalledAppDirectory, "current.version");
        try
        {
            if (!File.Exists(currentVersionFile) || HasReparsePoint(InstalledAppDirectory, currentVersionFile))
                return null;

            var version = File.ReadAllText(currentVersionFile).Trim();
            if (version.Length != 64 || version.Any(character => !Uri.IsHexDigit(character)))
                return null;

            bool isSharedPayload = InstallDefinition?.Type == LauncherInstallType.SharedPayload;
            string payloadDirectory = isSharedPayload
                ? InstalledSharedPayloadDirectory ?? string.Empty
                : InstalledAppDirectory;
            if (string.IsNullOrWhiteSpace(payloadDirectory)) return null;
            var versionDirectory = Path.Combine(payloadDirectory, version);
            var completionMarker = Path.Combine(versionDirectory, ".install-complete");
            if (!File.Exists(completionMarker) || HasReparsePoint(payloadDirectory, completionMarker))
                return null;

            var executablePath = Path.GetFullPath(InstalledExecutableRelativePath, versionDirectory);
            var root = Path.GetFullPath(versionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!executablePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                HasReparsePoint(payloadDirectory, executablePath))
                return null;
            return File.Exists(executablePath) ? executablePath : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasReparsePoint(string baseDirectory, string path)
    {
        var current = Path.GetFullPath(baseDirectory);
        for (var ancestor = new DirectoryInfo(current); ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        foreach (var segment in Path.GetRelativePath(baseDirectory, path)
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or ".") continue;
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) return false;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }
}

internal enum LauncherInstallType
{
    Portable,
    Command,
    SharedPayload
}

internal sealed record LauncherInstallDefinition(
    LauncherInstallType Type,
    string? ExecutablePath,
    IReadOnlyList<string> Arguments);

internal sealed record LauncherBuildDefinition(
    string ProjectPath,
    string? BootstrapProjectPath);
