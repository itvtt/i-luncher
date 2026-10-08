namespace DiskAtlas.Startup;

internal sealed class InstallerService
{
    private readonly InstallationPaths _paths;

    internal InstallerService(string sid) => _paths = new InstallationPaths(sid);

    internal async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        string sourcePayload = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "app"));
        PathPolicy.RejectExistingReparsePoints(sourcePayload);
        PackageManifest source = await PackageManifest.LoadAsync(sourcePayload, cancellationToken).ConfigureAwait(false);
        await source.VerifyAsync(cancellationToken).ConfigureAwait(false);

        EnsureProtectedInstallRoots();

        string finalPackageRoot = _paths.GetPackageRoot(source.Fingerprint);
        string finalAppDirectory = _paths.GetAppDirectory(source.Fingerprint);
        if (Directory.Exists(finalPackageRoot))
        {
            await ValidateExistingPackageAsync(source, finalAppDirectory, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string stagingPackageRoot = Path.Combine(
                _paths.PackagesRoot,
                $".staging-{source.Fingerprint}-{Guid.NewGuid():N}");
            string stagingAppDirectory = Path.Combine(stagingPackageRoot, "app");
            await PackageCopier.CopyVerifiedAsync(source, stagingAppDirectory, cancellationToken).ConfigureAwait(false);
            AclService.ProtectInstallation(stagingPackageRoot);

            try
            {
                Directory.Move(stagingPackageRoot, finalPackageRoot);
            }
            catch (IOException) when (Directory.Exists(finalPackageRoot))
            {
                await ValidateExistingPackageAsync(source, finalAppDirectory, cancellationToken).ConfigureAwait(false);
            }
        }

        AclService.ProtectInstallation(_paths.ProductRoot);
        string appPath = _paths.GetAppExecutable(source.Fingerprint);
        PathPolicy.RejectExistingReparsePoints(appPath);

        byte[]? previousReceipt = File.Exists(_paths.ReceiptPath)
            ? await File.ReadAllBytesAsync(_paths.ReceiptPath, cancellationToken).ConfigureAwait(false)
            : null;
        var receipt = new InstallationReceipt(source.Fingerprint, appPath);
        InstallationReceipt.WriteAtomically(_paths.ReceiptPath, receipt);
        AclService.ProtectInstallation(_paths.UserRoot);

        try
        {
            new TaskSchedulerService(_paths).Register(appPath);
        }
        catch
        {
            RestoreReceipt(previousReceipt);
            throw;
        }
    }

    internal void Uninstall()
    {
        ValidateKnownUserRoot();

        if (!Directory.Exists(_paths.UserRoot))
        {
            new TaskSchedulerService(_paths).Delete();
            return;
        }

        try
        {
            EnsureTreeNotInUse(_paths.UserRoot);
            new TaskSchedulerService(_paths).Delete();
            SafeDirectoryRemoval.DeleteTreeWithoutFollowingReparsePoints(_paths.UserRoot);
        }
        catch (IOException exception)
        {
            throw new IOException("DiskAtlas가 실행 중일 수 있습니다. 앱을 닫은 뒤 제거를 다시 시도해 주세요.", exception);
        }
    }

    private void EnsureProtectedInstallRoots()
    {
        PathPolicy.RejectExistingReparsePoints(_paths.ILauncherRoot);
        Directory.CreateDirectory(_paths.ILauncherRoot);
        PathPolicy.RejectExistingReparsePoints(_paths.ILauncherRoot);
        AclService.ProtectInstallation(_paths.ILauncherRoot, recursive: false);

        PathPolicy.RejectExistingReparsePoints(_paths.ProductRoot);
        Directory.CreateDirectory(_paths.ProductRoot);
        PathPolicy.RejectExistingReparsePoints(_paths.ProductRoot);
        AclService.ProtectInstallation(_paths.ProductRoot);

        Directory.CreateDirectory(_paths.PackagesRoot);
        PathPolicy.RejectExistingReparsePoints(_paths.PackagesRoot);
        AclService.ProtectInstallation(_paths.UserRoot);
    }

    private static void EnsureTreeNotInUse(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            PathPolicy.RejectExistingReparsePoints(directory);
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                PathPolicy.RejectExistingReparsePoints(file);
                using var handle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                PathPolicy.RejectExistingReparsePoints(child);
                pending.Push(child);
            }
        }
    }

    private static async Task ValidateExistingPackageAsync(
        PackageManifest source,
        string installedAppDirectory,
        CancellationToken cancellationToken)
    {
        PathPolicy.RejectExistingReparsePoints(installedAppDirectory);
        PackageManifest installed = await PackageManifest.LoadAsync(installedAppDirectory, cancellationToken).ConfigureAwait(false);
        if (!installed.Fingerprint.Equals(source.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("기존 설치 패키지의 지문이 현재 패키지와 다릅니다.");
        }

        await installed.VerifyAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ValidateKnownUserRoot()
    {
        var expected = new InstallationPaths(_paths.UserSid);
        if (!_paths.UserRoot.Equals(expected.UserRoot, StringComparison.OrdinalIgnoreCase) ||
            !PathPolicy.IsContainedBy(_paths.UserRoot, expected.ProductRoot))
        {
            throw new InvalidOperationException("제거할 설치 경로가 올바르지 않습니다.");
        }

        PathPolicy.RejectExistingReparsePoints(_paths.UserRoot);
    }

    private void RestoreReceipt(byte[]? previousReceipt)
    {
        if (previousReceipt is null)
        {
            File.Delete(_paths.ReceiptPath);
            return;
        }

        string rollbackPath = _paths.ReceiptPath + ".rollback-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(rollbackPath, previousReceipt);
        File.Move(rollbackPath, _paths.ReceiptPath, overwrite: true);
    }
}
