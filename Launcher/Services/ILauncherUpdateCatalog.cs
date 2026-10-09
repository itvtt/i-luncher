using ILauncher.Models;

namespace ILauncher.Services;

public interface ILauncherUpdateCatalog
{
    Task<LauncherUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task<string> UpdateAsync(LauncherEntry entry, CancellationToken cancellationToken = default);
}

public sealed record LauncherUpdateCheckResult(
    string CatalogVersion,
    string ReleaseTag,
    IReadOnlyList<LauncherAppUpdate> Apps,
    bool IsFromCache);

public sealed record LauncherAppUpdate(
    string Id,
    string? Version,
    string? VersionSource,
    string Asset,
    string Sha256,
    long Bytes,
    string Executable,
    string Description,
    string Category,
    string? IconBase64);

public sealed record LauncherUpdateSource(string ServerUrl, string ApiUrl, string Repository);
