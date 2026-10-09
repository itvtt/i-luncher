using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;

namespace ILauncher.Models;

public sealed class LauncherEntry : INotifyPropertyChanged
{
    private string? _availableVersion;
    private bool _updateAvailable;
    private bool _onlineManaged;
    private bool _onlineInstalled;
    private string? _onlineInstalledVersion;
    private string? _onlineExecutablePath;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string Category { get; init; } = "기타";
    public bool IsDistribution { get; init; }
    public bool IsIntegrated { get; init; }
    public required string ExecutablePath { get; init; }
    public string? BundledVersion { get; init; }
    public string IconPath { get; init; } = "";
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? AvailableVersion
    {
        get => _availableVersion;
        internal set
        {
            if (_availableVersion == value) return;
            _availableVersion = value;
            OnPropertyChanged();
        }
    }
    public string? InstalledVersion => OnlineInstalledVersion ?? TryGetInstalledDisplayVersion();
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        internal set
        {
            if (_updateAvailable == value) return;
            _updateAvailable = value;
            OnPropertyChanged();
        }
    }

    internal LauncherInstallDefinition? InstallDefinition { get; init; }
    internal LauncherBuildDefinition? BuildDefinition { get; init; }
    internal string? InstallSourceDirectory { get; init; }
    internal string? InstallIconSourceDirectory { get; init; }
    internal string? InstalledSharedPayloadDirectory { get; init; }
    internal IReadOnlyList<string> InstallShortcutArguments { get; init; } = [];
    internal bool IsAutoDiscoveredSourceProject { get; init; }
    internal string? SourceProjectDirectory { get; init; }
    internal string? InstalledAppDirectory { get; init; }
    internal string? InstalledExecutableRelativePath { get; set; }
    internal string? InstalledWorkingDirectoryRelativePath { get; set; }
    internal string? InstallVersion { get; set; }
    internal bool LaunchInstalledPayload { get; set; }
    internal bool OnlineManaged
    {
        get => _onlineManaged;
        set
        {
            if (_onlineManaged == value) return;
            _onlineManaged = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(HasInstallFiles));
            OnPropertyChanged(nameof(IsAvailable));
        }
    }
    internal bool OnlineInstalled
    {
        get => _onlineInstalled;
        set
        {
            if (_onlineInstalled == value) return;
            _onlineInstalled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanUninstall));
        }
    }
    internal string? OnlineInstalledVersion
    {
        get => _onlineInstalledVersion;
        set
        {
            if (_onlineInstalledVersion == value) return;
            _onlineInstalledVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InstalledVersion));
        }
    }
    internal string? OnlineExecutablePath
    {
        get => _onlineExecutablePath;
        set
        {
            if (_onlineExecutablePath == value) return;
            _onlineExecutablePath = value;
            OnPropertyChanged(nameof(CanUninstall));
        }
    }

    public bool CanInstall => InstallDefinition is not null || OnlineManaged;
    public bool CanUninstall => InstallDefinition?.Type is LauncherInstallType.Portable or LauncherInstallType.SharedPayload
        && TryGetInstalledExecutablePath() is not null || OnlineInstalled;
    public bool HasInstallFiles => OnlineManaged || (InstallDefinition switch
    {
        { Type: LauncherInstallType.Command } install => File.Exists(install.ExecutablePath),
        { Type: LauncherInstallType.SharedPayload } => InstallSourceDirectory is not null
            && InstalledExecutableRelativePath is not null
            && File.Exists(Path.GetFullPath(InstalledExecutableRelativePath, InstallSourceDirectory)),
        { Type: LauncherInstallType.Portable } => File.Exists(ExecutablePath) || CanBuild,
        _ => false
    });
    public bool CanBuild => BuildDefinition is { } build
        && File.Exists(build.ProjectPath)
        && (build.BootstrapProjectPath is null || File.Exists(build.BootstrapProjectPath));

    public bool IsAvailable => OnlineManaged || IsIntegrated ||
        TryGetInstalledExecutablePath() is not null || File.Exists(ExecutablePath) || CanBuild;

    internal string? TryGetInstalledExecutablePath()
    {
        if (OnlineManaged)
            return OnlineInstalled && OnlineExecutablePath is { } onlinePath && File.Exists(onlinePath)
                ? onlinePath : null;
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

    private string? TryGetInstalledDisplayVersion()
    {
        if (InstalledAppDirectory is null || TryGetInstalledExecutablePath() is not { } executablePath) return null;
        try
        {
            var currentVersionFile = Path.Combine(InstalledAppDirectory, "current.version");
            var hash = File.ReadAllText(currentVersionFile).Trim();
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit))
            {
                var metadata = Path.Combine(InstalledAppDirectory, "installed-version-" + hash + ".txt");
                if (File.Exists(metadata) && !HasReparsePoint(InstalledAppDirectory, metadata))
                {
                    var value = File.ReadAllText(metadata).Trim();
                    if (IsSafeDisplayVersion(value)) return value;
                }
            }

            // These binaries are read only after the owned install marker and executable path checks pass.
            var binaryVersion = ReadBinaryVersion(executablePath);
            if (binaryVersion is not null) return binaryVersion;
            var managedAssemblyPath = Path.Combine(Path.GetDirectoryName(executablePath)!,
                Path.GetFileNameWithoutExtension(executablePath) + ".dll");
            if (!HasReparsePoint(InstalledAppDirectory, managedAssemblyPath) && File.Exists(managedAssemblyPath))
                return ReadBinaryVersion(managedAssemblyPath);
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (System.Security.SecurityException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    internal static string? ReadBundledVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return ReadBinaryVersion(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static string? ReadBinaryVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        var productVersion = info.ProductVersion?.Trim();
        if (IsSafeDisplayVersion(productVersion)) return productVersion;
        var fileVersion = info.FileVersion?.Trim();
        return IsSafeDisplayVersion(fileVersion) ? fileVersion : null;
    }

    private static bool IsSafeDisplayVersion(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+');

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

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
