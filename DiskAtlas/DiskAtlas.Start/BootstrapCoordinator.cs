namespace DiskAtlas.Startup;

internal sealed class BootstrapCoordinator
{
    private const int ElevationCancelled = 1223;
    private readonly string _sid;
    private readonly InstallationPaths _paths;

    internal BootstrapCoordinator(string sid)
    {
        _sid = sid;
        _paths = new InstallationPaths(sid);
    }

    internal async Task<int> EnsureInstalledAndLaunchAsync(CancellationToken cancellationToken = default)
    {
        PackageManifest source = await LoadSourceManifestAsync(cancellationToken).ConfigureAwait(false);
        InstallationReceipt? receipt = await TryLoadCurrentReceiptAsync(cancellationToken).ConfigureAwait(false);

        if (receipt is null || !receipt.Fingerprint.Equals(source.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            int installResult = await InstallElevatedAsync(source, cancellationToken).ConfigureAwait(false);
            if (installResult != 0)
            {
                return installResult;
            }

            receipt = await TryLoadCurrentReceiptAsync(cancellationToken).ConfigureAwait(false);
        }

        if (receipt is null || !receipt.Fingerprint.Equals(source.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("현재 DiskAtlas 패키지가 설치되지 않았습니다. --install 옵션으로 다시 설치해 주세요.");
        }

        ValidateReceiptPath(receipt);
        new TaskSchedulerService(_paths).ValidateAndRun(receipt.AppPath);
        return 0;
    }

    internal async Task<int> InstallElevatedAsync(CancellationToken cancellationToken = default)
    {
        PackageManifest source = await LoadSourceManifestAsync(cancellationToken).ConfigureAwait(false);
        return await InstallElevatedAsync(source, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<int> UninstallElevatedAsync()
    {
        using SetupStage stage = SetupStage.CreateUninstallStage(_sid);
        int result = await Program.RunElevatedHelperAsync("--elevated-uninstall", _sid, stage).ConfigureAwait(false);
        return result == ElevationCancelled ? ElevationCancelled : result;
    }

    private async Task<int> InstallElevatedAsync(PackageManifest source, CancellationToken cancellationToken)
    {
        using SetupStage stage = await SetupStage.CreateInstallStageAsync(source, _sid, cancellationToken).ConfigureAwait(false);
        int result = await Program.RunElevatedHelperAsync("--elevated-install", _sid, stage).ConfigureAwait(false);
        return result == ElevationCancelled ? ElevationCancelled : result;
    }

    private static Task<PackageManifest> LoadSourceManifestAsync(CancellationToken cancellationToken) =>
        PackageManifest.LoadAsync(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "app")),
            cancellationToken);

    private async Task<InstallationReceipt?> TryLoadCurrentReceiptAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.ReceiptPath))
        {
            return null;
        }

        ValidateProtectedPath(_paths.ILauncherRoot, directory: true);
        ValidateProtectedPath(_paths.ProductRoot, directory: true);
        ValidateProtectedPath(_paths.UserRoot, directory: true);
        ValidateProtectedPath(_paths.ReceiptPath, directory: false);
        InstallationReceipt receipt = InstallationReceipt.Load(_paths.ReceiptPath);
        ValidateReceiptPath(receipt);

        string installedManifestPath = Path.Combine(Path.GetDirectoryName(receipt.AppPath)!, "package.sha256");
        if (!File.Exists(installedManifestPath))
        {
            return null;
        }

        PackageManifest installed = await PackageManifest.LoadAsync(
            Path.GetDirectoryName(receipt.AppPath)!,
            cancellationToken).ConfigureAwait(false);
        return installed.Fingerprint.Equals(receipt.Fingerprint, StringComparison.OrdinalIgnoreCase)
            ? receipt
            : null;
    }

    private void ValidateReceiptPath(InstallationReceipt receipt)
    {
        string expected = _paths.GetAppExecutable(receipt.Fingerprint);
        if (!receipt.AppPath.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            !PathPolicy.IsContainedBy(receipt.AppPath, _paths.UserRoot) ||
            !File.Exists(receipt.AppPath))
        {
            throw new InvalidDataException("설치 영수증의 실행 파일 경로가 올바르지 않습니다.");
        }

        PathPolicy.RejectExistingReparsePoints(receipt.AppPath);
        ValidateProtectedPath(_paths.PackagesRoot, directory: true);
        ValidateProtectedPath(_paths.GetPackageRoot(receipt.Fingerprint), directory: true);
        ValidateProtectedPath(_paths.GetAppDirectory(receipt.Fingerprint), directory: true);
        ValidateProtectedPath(receipt.AppPath, directory: false);
    }

    private static void ValidateProtectedPath(string path, bool directory)
    {
        PathPolicy.RejectExistingReparsePoints(path);
        AclService.ValidateProtectedInstallation(path, directory);
    }
}
