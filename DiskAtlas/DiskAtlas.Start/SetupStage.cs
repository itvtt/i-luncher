namespace DiskAtlas.Startup;

internal sealed class SetupStage : IDisposable
{
    private const string StagePrefix = "DiskAtlas-Setup";
    private bool _disposed;
    private readonly string _sid;
    private readonly List<IDisposable> _locks = [];

    private SetupStage(string rootPath, string bootstrapPath, string sid)
    {
        RootPath = rootPath;
        BootstrapPath = bootstrapPath;
        _sid = sid;
    }

    internal string RootPath { get; }
    internal string BootstrapPath { get; }

    internal static async Task<SetupStage> CreateInstallStageAsync(
        PackageManifest sourceManifest,
        string sid,
        CancellationToken cancellationToken = default)
    {
        SetupStage stage = CreateHelperStage(sid);
        try
        {
            await PackageCopier.CopyVerifiedAsync(
                sourceManifest,
                Path.Combine(stage.RootPath, "app"),
                cancellationToken).ConfigureAwait(false);
            var expectedFiles = sourceManifest.Entries
                .Select(entry => Path.Combine("app", entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                .Append(Path.Combine("app", "package.sha256"))
                .Append("DiskAtlas.exe");
            stage.Seal(expectedFiles);
            return stage;
        }
        catch
        {
            stage.Dispose();
            throw;
        }
    }

    internal static SetupStage CreateUninstallStage(string sid)
    {
        SetupStage stage = CreateHelperStage(sid);
        stage.Seal(["DiskAtlas.exe"]);
        return stage;
    }

    private static SetupStage CreateHelperStage(string sid)
    {
        string baseRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "i-luncher",
            StagePrefix));
        PathPolicy.RejectExistingReparsePoints(baseRoot);
        Directory.CreateDirectory(baseRoot);
        PathPolicy.RejectExistingReparsePoints(baseRoot);

        string root = Path.Combine(baseRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        string sourceBootstrap = Environment.ProcessPath
            ?? throw new InvalidOperationException("부트스트랩 실행 파일 경로를 확인하지 못했습니다.");
        PathPolicy.RejectExistingReparsePoints(sourceBootstrap);
        string bootstrapPath = Path.Combine(root, "DiskAtlas.exe");
        File.Copy(sourceBootstrap, bootstrapPath, overwrite: false);
        return new SetupStage(root, bootstrapPath, sid);
    }

    private void Seal(IEnumerable<string> expectedRelativeFiles)
    {
        var expected = new HashSet<string>(
            expectedRelativeFiles.Select(path => path.Replace('/', Path.DirectorySeparatorChar)),
            StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(
            Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(RootPath, path)),
            StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(actual))
        {
            throw new InvalidDataException("설치 준비 폴더에 예상하지 않은 파일이 있습니다.");
        }

        AclService.ProtectStage(RootPath, _sid);
        foreach (string directory in Directory.EnumerateDirectories(RootPath, "*", SearchOption.AllDirectories).Prepend(RootPath))
        {
            PathPolicy.RejectExistingReparsePoints(directory);
            _locks.Add(DirectoryLock.Open(directory));
        }

        foreach (string file in actual.Select(path => Path.Combine(RootPath, path)))
        {
            PathPolicy.RejectExistingReparsePoints(file);
            _locks.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            foreach (IDisposable handle in _locks)
            {
                handle.Dispose();
            }
            _locks.Clear();

            string expectedBase = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "i-luncher",
                StagePrefix));
            if (!PathPolicy.IsContainedBy(RootPath, expectedBase) ||
                Path.GetFileName(RootPath).Length != 32 ||
                !Guid.TryParseExact(Path.GetFileName(RootPath), "N", out _))
            {
                return;
            }

            AclService.RestoreStageAccess(RootPath, _sid);
            SafeDirectoryRemoval.DeleteTreeWithoutFollowingReparsePoints(RootPath);
        }
        catch
        {
            // A failed best-effort cleanup never changes installation success.
        }
    }
}
