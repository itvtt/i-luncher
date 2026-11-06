using System.Text.RegularExpressions;
using ILauncher.Models;

namespace ILauncher.Services;

public sealed record AppProgress(string Message, double? Fraction = null);

/// <summary>릴리스를 고르고 받아 설치·업데이트하는 흐름 (GitHub + AppInstaller)</summary>
public sealed class AppManager(GitHubClient github, AppInstaller installer, string downloadDirectory, Func<string?> defaultHost)
{
    public AppInstaller Installer => installer;
    public GitHubClient GitHub => github;

    public GitHubRepository Repository(AppDefinition app) =>
        GitHubRepository.Parse(app.Repository, defaultHost()) ?? throw new InvalidOperationException($"{app.Name}의 저장소 주소가 올바르지 않습니다: {app.Repository}");

    public Task<GitHubRelease?> GetLatestAsync(AppDefinition app, CancellationToken cancellationToken = default) =>
        github.GetLatestReleaseAsync(Repository(app), app.IncludePrerelease, cancellationToken);

    /// <summary>Velopack 으로 만든 릴리스 (자체 업데이터가 있으므로 설치 프로그램으로 설치)</summary>
    public static bool IsVelopackRelease(GitHubRelease release) =>
        release.Assets.Any(asset => asset.Name.Equals("RELEASES", StringComparison.OrdinalIgnoreCase)
                                    || asset.Name.StartsWith("releases.", StringComparison.OrdinalIgnoreCase) && asset.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                    || asset.Name.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase));

    /// <summary>앱 설정과 릴리스로 정한 설치 방법</summary>
    public static string ResolveKind(AppDefinition app, GitHubRelease release)
    {
        if (!string.IsNullOrWhiteSpace(app.Kind)) return app.EffectiveKind;
        if (IsVelopackRelease(release) && SelectSetupAsset(app, release) is not null) return InstallKinds.Setup;
        return SelectZipAsset(app, release) is not null ? InstallKinds.Zip
            : SelectSetupAsset(app, release) is not null ? InstallKinds.Setup : InstallKinds.Zip;
    }

    private static readonly string[] ZipExcludes = ["source", "src", "symbol", "pdb", "debug", "arm64", "x86", "linux", "osx", "mac"];

    /// <summary>
    /// 설치할 zip: 지정한 이름(와일드카드 가능) → win-x64 가 들어간 zip → 앱 이름으로 시작하는 zip → 하나뿐인 zip.
    /// 소스·기호·다른 플랫폼 zip 은 고르지 않음
    /// </summary>
    public static GitHubAsset? SelectZipAsset(AppDefinition app, GitHubRelease release)
    {
        if (!string.IsNullOrWhiteSpace(app.Asset))
            return release.Assets.FirstOrDefault(asset => Matches(asset.Name, app.Asset!));
        var zips = release.Assets.Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && !ZipExcludes.Any(word => Regex.IsMatch(asset.Name, $@"(^|[^a-z]){word}([^a-z]|$)", RegexOptions.IgnoreCase))).ToList();
        return zips.FirstOrDefault(asset => Regex.IsMatch(asset.Name, @"win(dows)?[-_.]?x64|win64", RegexOptions.IgnoreCase))
            ?? zips.FirstOrDefault(asset => asset.Name.StartsWith(app.EffectiveInstallName, StringComparison.OrdinalIgnoreCase))
            ?? zips.FirstOrDefault(asset => Regex.IsMatch(asset.Name, @"win|portable", RegexOptions.IgnoreCase))
            ?? (zips.Count == 1 ? zips[0] : null);
    }

    /// <summary>설치 프로그램: 지정한 이름 → 이름에 Setup/Install 이 들어간 exe → msi</summary>
    public static GitHubAsset? SelectSetupAsset(AppDefinition app, GitHubRelease release)
    {
        if (!string.IsNullOrWhiteSpace(app.SetupAsset))
            return release.Assets.FirstOrDefault(asset => Matches(asset.Name, app.SetupAsset!));
        var exes = release.Assets.Where(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(asset.Name, "setup|install", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(asset.Name, "arm64|x86(?!_64)", RegexOptions.IgnoreCase)).ToList();
        return exes.FirstOrDefault(asset => asset.Name.StartsWith(app.EffectiveInstallName, StringComparison.OrdinalIgnoreCase))
            ?? exes.FirstOrDefault()
            ?? release.Assets.FirstOrDefault(asset => asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
    }

    public static string DefaultSetupArguments(AppDefinition app, GitHubRelease release) =>
        !string.IsNullOrWhiteSpace(app.SetupArguments) ? app.SetupArguments!
        : IsVelopackRelease(release) ? "--silent"
        : "/S";

    /// <summary>zip 이름에서 설치 폴더 이름을 짐작 (DeskCal-win-x64.zip → DeskCal). 앱 추가 화면에서 씀</summary>
    public static string? GuessInstallName(string assetName)
    {
        string name = Path.GetFileNameWithoutExtension(assetName);
        name = Regex.Replace(name, @"[-_.]?(win(dows)?[-_.]?(x64|64)?|portable|setup|v?\d+(\.\d+)+.*)$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"[-_.]?(win(dows)?[-_.]?(x64|64)?|portable|setup)$", "", RegexOptions.IgnoreCase);
        return name.Length > 0 ? AppDefinition.SafeFolderName(name) : null;
    }

    private static bool Matches(string name, string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?')) return string.Equals(name, pattern.Trim(), StringComparison.OrdinalIgnoreCase);
        string regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 최신 릴리스로 설치(또는 업데이트). zip 방식인데 이 PC 에 필요한 .NET 실행 환경이 없으면
    /// 릴리스의 설치 프로그램(실행 환경을 함께 넣어 주는 Setup)으로 대신 설치.
    /// </summary>
    public async Task<InstalledApp> InstallAsync(AppDefinition app, GitHubRelease release, IProgress<AppProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var repository = Repository(app);
        string folder = Path.Combine(downloadDirectory, AppDefinition.SafeFolderName(app.Id));
        var status = new Progress<string>(message => progress?.Report(new AppProgress(message)));
        try
        {
            string kind = ResolveKind(app, release);
            if (kind == InstallKinds.Zip)
            {
                GitHubAsset asset = SelectZipAsset(app, release)
                    ?? throw new InvalidOperationException($"{release.Tag} 릴리스에 설치할 zip 파일이 없습니다." +
                        (string.IsNullOrWhiteSpace(app.Asset) ? "" : $" (지정한 파일: {app.Asset})"));
                string zip = await DownloadAsync(repository, asset, folder, progress, cancellationToken).ConfigureAwait(false);
                try
                {
                    return await installer.InstallZipAsync(app, zip, release, status, cancellationToken).ConfigureAwait(false);
                }
                catch (RuntimeMissingException) when (SelectSetupAsset(app, release) is not null)
                {
                    progress?.Report(new AppProgress(".NET 실행 환경이 없어 설치 프로그램으로 설치합니다…"));
                }
            }
            GitHubAsset setup = SelectSetupAsset(app, release)
                ?? throw new InvalidOperationException($"{release.Tag} 릴리스에 설치 프로그램이 없습니다." +
                    (string.IsNullOrWhiteSpace(app.SetupAsset) ? "" : $" (지정한 파일: {app.SetupAsset})"));
            string setupPath = await DownloadAsync(repository, setup, folder, progress, cancellationToken).ConfigureAwait(false);
            return await installer.RunSetupAsync(app, setupPath, DefaultSetupArguments(app, release), status, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"설치 프로그램은 끝났지만 설치된 {app.Name}을(를) 찾지 못했습니다. " +
                    $"앱 편집에서 설치 폴더 이름(지금: {app.EffectiveInstallName})이 설치 프로그램과 같은지 확인하세요.");
        }
        finally
        {
            TryDeleteDirectory(folder);
        }
    }

    private async Task<string> DownloadAsync(GitHubRepository repository, GitHubAsset asset, string folder, IProgress<AppProgress>? progress,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(folder, AppDefinition.SafeFolderName(asset.Name));
        string size = asset.Size > 0 ? $" ({asset.Size / 1048576.0:N1} MB)" : "";
        progress?.Report(new AppProgress($"{asset.Name}{size} 받는 중…", 0));
        var fraction = new Progress<double>(value => progress?.Report(new AppProgress($"{asset.Name}{size} 받는 중… {value:P0}", value)));
        await github.DownloadAssetAsync(repository, asset, path, fraction, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
