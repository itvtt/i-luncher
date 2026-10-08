using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ILauncher.Bootstrapper
{
    public sealed class BootstrapperOptions
    {
        public BootstrapperOptions(string embeddedCatalogJson, string expectedCatalogSha256,
            string cacheRoot, string bootstrapperExecutablePath)
        {
            EmbeddedCatalogJson = embeddedCatalogJson;
            ExpectedCatalogSha256 = expectedCatalogSha256;
            CacheRoot = cacheRoot;
            BootstrapperExecutablePath = bootstrapperExecutablePath;
            CatalogAssetName = "online-catalog.json";
            MaximumCatalogBytes = 2 * 1024 * 1024;
            MaximumPackageBytes = 1024L * 1024L * 1024L;
            MaximumExpandedBytes = 2L * 1024L * 1024L * 1024L;
            MaximumArchiveEntries = 10000;
            MaximumPathLength = 240;
            StartMenuRoot = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) +
                            "\\Programs\\i-luncher";
        }

        public string EmbeddedCatalogJson { get; set; }
        public string ExpectedCatalogSha256 { get; set; }
        public string CacheRoot { get; set; }
        public string BootstrapperExecutablePath { get; set; }
        public string StartMenuRoot { get; set; }
        public string PackageMirrorBase { get; set; }
        public string CatalogAssetName { get; set; }
        public long MaximumCatalogBytes { get; set; }
        public long MaximumPackageBytes { get; set; }
        public long MaximumExpandedBytes { get; set; }
        public int MaximumArchiveEntries { get; set; }
        public int MaximumPathLength { get; set; }
    }

    public interface IBootstrapperCredentialProvider
    {
        string GetToken(Uri apiOrigin);
    }

    public sealed class NullBootstrapperCredentialProvider : IBootstrapperCredentialProvider
    {
        public static readonly NullBootstrapperCredentialProvider Instance =
            new NullBootstrapperCredentialProvider();

        private NullBootstrapperCredentialProvider() { }

        public string GetToken(Uri apiOrigin) { return null; }
    }

    public sealed class BootstrapperSource
    {
        internal BootstrapperSource(string serverUrl, string apiUrl, string repository, string tagName)
        {
            ServerUrl = serverUrl;
            ApiUrl = apiUrl;
            Repository = repository;
            TagName = tagName;
        }

        public string ServerUrl { get; private set; }
        public string ApiUrl { get; private set; }
        public string Repository { get; private set; }
        public string TagName { get; private set; }
    }

    public sealed class BootstrapperRuntime
    {
        internal BootstrapperRuntime(string version, string asset, string sha256, long bytes)
        {
            Version = version;
            Asset = asset;
            Sha256 = sha256;
            Bytes = bytes;
        }

        public string Version { get; private set; }
        public string Asset { get; private set; }
        public string Sha256 { get; private set; }
        public long Bytes { get; private set; }
    }

    public sealed class BootstrapperApp
    {
        internal BootstrapperApp(string id, string name, string description, string category,
            string executable, string iconBase64, string asset, string sha256, long bytes)
        {
            Id = id;
            Name = name;
            Description = description;
            Category = category;
            Executable = executable;
            IconBase64 = iconBase64;
            Asset = asset;
            Sha256 = sha256;
            Bytes = bytes;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Category { get; private set; }
        public string Executable { get; private set; }
        public string IconBase64 { get; private set; }
        public string Asset { get; private set; }
        public string Sha256 { get; private set; }
        public long Bytes { get; private set; }
    }

    public sealed class BootstrapperCatalog
    {
        internal BootstrapperCatalog(int schemaVersion, string version, BootstrapperSource source,
            BootstrapperRuntime runtime, IList<BootstrapperApp> apps, string sha256)
        {
            SchemaVersion = schemaVersion;
            Version = version;
            Source = source;
            Runtime = runtime;
            Apps = new ReadOnlyCollection<BootstrapperApp>(new List<BootstrapperApp>(apps));
            Sha256 = sha256;
        }

        public int SchemaVersion { get; private set; }
        public string Version { get; private set; }
        public BootstrapperSource Source { get; private set; }
        public BootstrapperRuntime Runtime { get; private set; }
        public ReadOnlyCollection<BootstrapperApp> Apps { get; private set; }
        public string Sha256 { get; private set; }

        public BootstrapperApp FindApp(string id)
        {
            foreach (BootstrapperApp app in Apps)
                if (String.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase)) return app;
            return null;
        }
    }

    public sealed class BootstrapperAppStatus
    {
        internal BootstrapperAppStatus(BootstrapperApp app, bool isReady, bool isInstalled,
            string executablePath, string error)
        {
            App = app;
            IsReady = isReady;
            IsInstalled = isInstalled;
            ExecutablePath = executablePath;
            Error = error;
        }

        public BootstrapperApp App { get; private set; }
        public bool IsReady { get; private set; }
        public bool IsInstalled { get; private set; }
        public string ExecutablePath { get; private set; }
        public string Error { get; private set; }
    }

    public sealed class BootstrapperReadyApp
    {
        internal BootstrapperReadyApp(BootstrapperApp app, string executablePath,
            string workingDirectory, string runtimeDirectory)
        {
            App = app;
            ExecutablePath = executablePath;
            WorkingDirectory = workingDirectory;
            RuntimeDirectory = runtimeDirectory;
        }

        public BootstrapperApp App { get; private set; }
        public string ExecutablePath { get; private set; }
        public string WorkingDirectory { get; private set; }
        public string RuntimeDirectory { get; private set; }
    }

    public sealed class BootstrapperLaunchResult
    {
        internal BootstrapperLaunchResult(int processId, string executablePath)
        {
            ProcessId = processId;
            ExecutablePath = executablePath;
        }

        public int ProcessId { get; private set; }
        public string ExecutablePath { get; private set; }
    }

    public sealed class BootstrapperInstallResult
    {
        internal BootstrapperInstallResult(string shortcutPath, string receiptPath,
            string installedBootstrapperPath)
        {
            ShortcutPath = shortcutPath;
            ReceiptPath = receiptPath;
            InstalledBootstrapperPath = installedBootstrapperPath;
        }

        public string ShortcutPath { get; private set; }
        public string ReceiptPath { get; private set; }
        public string InstalledBootstrapperPath { get; private set; }
    }

    public sealed class BootstrapperUninstallResult
    {
        internal BootstrapperUninstallResult(string appId, bool removed)
        {
            AppId = appId;
            Removed = removed;
        }

        public string AppId { get; private set; }
        public bool Removed { get; private set; }
    }

    public sealed class BootstrapperProgressEventArgs : EventArgs
    {
        internal BootstrapperProgressEventArgs(string appId, string phase, long bytesTransferred,
            long bytesTotal)
        {
            AppId = appId;
            Phase = phase;
            BytesTransferred = bytesTransferred;
            BytesTotal = bytesTotal;
        }

        public string AppId { get; private set; }
        public string Phase { get; private set; }
        public long BytesTransferred { get; private set; }
        public long BytesTotal { get; private set; }
    }

    public interface IBootstrapperEngine
    {
        event EventHandler<BootstrapperProgressEventArgs> ProgressChanged;
        System.Threading.Tasks.Task<BootstrapperCatalog> LoadCatalogAsync(
            System.Threading.CancellationToken cancellationToken);
        BootstrapperAppStatus GetStatus(string appId);
        System.Threading.Tasks.Task<BootstrapperReadyApp> EnsureReadyAsync(string appId,
            System.Threading.CancellationToken cancellationToken);
        System.Threading.Tasks.Task<BootstrapperLaunchResult> LaunchAsync(string appId,
            System.Threading.CancellationToken cancellationToken);
        System.Threading.Tasks.Task<BootstrapperInstallResult> InstallAsync(string appId,
            System.Threading.CancellationToken cancellationToken);
        System.Threading.Tasks.Task<BootstrapperUninstallResult> UninstallAsync(string appId,
            System.Threading.CancellationToken cancellationToken);
    }
}
