using ILauncher.Models;

namespace ILauncher.Services;

public interface ILauncherCatalog
{
    string RootDirectory { get; }
    CatalogResult Load();
    Task<string> InstallAsync(LauncherEntry entry);
    Task<string> UninstallAsync(LauncherEntry entry);
    Task<string> UninstallAsync(LauncherEntry entry, bool deleteFiles) => deleteFiles
        ? Task.FromException<string>(new NotSupportedException("This catalog does not support full removal."))
        : UninstallAsync(entry);
    void Launch(LauncherEntry entry);
    Task LaunchAsync(LauncherEntry entry);
}

public interface IIntegratedAppProvider
{
    IReadOnlyList<LauncherEntry> Entries { get; }
    Task LaunchAsync(string appId);
}
