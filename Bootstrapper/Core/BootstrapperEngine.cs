using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Directory = ILauncher.Bootstrapper.LongPathDirectory;
using File = ILauncher.Bootstrapper.LongPathFile;

namespace ILauncher.Bootstrapper
{
    public sealed class BootstrapperEngine : IBootstrapperEngine, IDisposable
    {
        private static readonly object ProcessEnvironmentGate = new object();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly BootstrapperOptions _options;
        private readonly IBootstrapperCredentialProvider _credentials;
        private readonly Func<IBootstrapperHttpTransport> _transportFactory;
        private readonly Func<IBootstrapperHttpTransport> _anonymousTransportFactory;
        private readonly SemaphoreSlim _catalogUpdateGate = new SemaphoreSlim(1, 1);
        private BootstrapperCatalog _embeddedCatalog;
        private BootstrapperCatalog _catalog;

        public event EventHandler<BootstrapperProgressEventArgs> ProgressChanged;

        public BootstrapperEngine(BootstrapperOptions options, IBootstrapperCredentialProvider credentials)
            : this(options, credentials, delegate { return new BootstrapperHttpTransport(); },
                delegate { return new BootstrapperHttpTransport(); }) { }

        public BootstrapperEngine(string embeddedCatalogJson, string expectedCatalogSha256,
            string cacheRoot, string bootstrapperExecutablePath, IBootstrapperCredentialProvider credentials)
            : this(CreateOptions(embeddedCatalogJson, expectedCatalogSha256, cacheRoot, bootstrapperExecutablePath), credentials) { }

        private static BootstrapperOptions CreateOptions(string catalogJson, string catalogSha256,
            string cacheRoot, string executablePath)
        {
            return new BootstrapperOptions(catalogJson, catalogSha256, cacheRoot, executablePath);
        }

        internal BootstrapperEngine(BootstrapperOptions options, IBootstrapperCredentialProvider credentials,
            Func<IBootstrapperHttpTransport> transportFactory,
            Func<IBootstrapperHttpTransport> anonymousTransportFactory)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (credentials == null) throw new ArgumentNullException("credentials");
            if (transportFactory == null || anonymousTransportFactory == null)
                throw new ArgumentNullException("transportFactory");
            _options = options;
            _credentials = credentials;
            _transportFactory = transportFactory;
            _anonymousTransportFactory = anonymousTransportFactory;
            ValidateOptions();
        }

        public Task<BootstrapperCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_catalog != null) return Task.FromResult(_catalog);
            if (String.IsNullOrEmpty(_options.EmbeddedCatalogJson))
                throw new InvalidDataException("The embedded online catalog is missing.");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(_options.EmbeddedCatalogJson);
            if (bytes.Length > _options.MaximumCatalogBytes)
                throw new InvalidDataException("The embedded online catalog exceeds its size limit.");
            string actualHash = SafeFiles.Sha256(bytes);
            if (!String.Equals(actualHash, _options.ExpectedCatalogSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The embedded online catalog failed its integrity check.");
            _embeddedCatalog = CatalogParser.Parse(_options.EmbeddedCatalogJson, actualHash, _options);
            _catalog = _embeddedCatalog;
            if (!_options.ReadOnlyStatus) SaveCatalogArchive(_embeddedCatalog);
            BootstrapperCatalog cached = TryReadLastGoodCatalog();
            if (cached != null && CompareReleaseVersions(cached.Version, _catalog.Version) >= 0)
            {
                if (CompareReleaseVersions(cached.Version, _catalog.Version) != 0 ||
                    String.Equals(cached.Sha256, _catalog.Sha256, StringComparison.OrdinalIgnoreCase))
                    _catalog = cached;
            }
            return Task.FromResult(_catalog);
        }

        public async Task<BootstrapperCatalog> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (_options.ReadOnlyStatus) return _catalog;
            await _catalogUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    BootstrapperCatalog latest = await FetchLatestCatalogAsync(cancellationToken).ConfigureAwait(false);
                    int comparison = CompareReleaseVersions(latest.Version, _catalog.Version);
                    if (comparison < 0)
                        throw new InvalidDataException("The release feed moved behind the last known catalog; the cached catalog was preserved.");
                    if (comparison == 0 && !String.Equals(latest.Sha256, _catalog.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("An immutable catalog version changed content; the cached catalog was preserved.");
                    EnsureInstalledAppsRemainAvailable(latest);
                    latest.IsFromCache = false;
                    SaveCatalogArchive(latest);
                    SaveLastGoodCatalog(latest);
                    _catalog = latest;
                    return latest;
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    BootstrapperCatalog cached = TryReadLastGoodCatalog();
                    if (cached != null && CompareReleaseVersions(cached.Version, _catalog.Version) >= 0)
                    {
                        cached.IsFromCache = true;
                        _catalog = cached;
                    }
                    else if (_catalog != _embeddedCatalog)
                    {
                        _catalog.IsFromCache = true;
                    }
                    return _catalog;
                }
            }
            finally { _catalogUpdateGate.Release(); }
        }

        public BootstrapperAppStatus GetStatus(string appId)
        {
            BootstrapperCatalog catalog = GetCatalog();
            BootstrapperApp app = catalog.FindApp(appId);
            if (app == null) app = FindInstalledAppFromReceipt(appId) ?? (_embeddedCatalog == null ? null : _embeddedCatalog.FindApp(appId));
            if (app == null) throw new ArgumentException("The app is not in the current catalog and has no known installation.", "appId");
            BootstrapperApp availableApp = catalog.FindApp(appId);
            string receipt = ReceiptPath(app);
            string ownedShortcut;
            Dictionary<string, string> receiptFields;
            bool installed = TryGetValidInstallReceipt(app, receipt, out ownedShortcut, out receiptFields);
            string installedExecutable = null;
            bool installedReady = installed && TryResolveReceiptReady(app, receiptFields, out installedExecutable);
            string appDirectory = AppCachePath(app);
            string executable = SafeFiles.CombineContained(appDirectory, app.Executable, "App executable");
            string runtimeDirectory = RuntimeCachePath(catalog.Runtime);
            bool ready = installedReady || (IsValidAppCache(app, appDirectory, executable) && IsValidRuntimeCache(catalog.Runtime, runtimeDirectory));
            LegacyManagedInstall legacy = TryFindLegacyInstall(app);
            string statusExecutable = installedReady ? installedExecutable : ready ? executable : legacy == null ? null : legacy.ExecutablePath;
            bool anyInstalled = installed || legacy != null;
            string installedVersion = installed ? ResolveInstalledVersion(app, receiptFields) : null;
            if (installedVersion == null && installedReady)
                installedVersion = ReadInstalledBinaryVersion(installedExecutable);
            if (installedVersion == null && legacy != null)
                installedVersion = ReadInstalledBinaryVersion(legacy.ExecutablePath);
            bool updateAvailable = anyInstalled && availableApp != null &&
                IsNewerAppVersion(availableApp.Version, installedVersion);
            return new BootstrapperAppStatus(app, ready || legacy != null, anyInstalled,
                statusExecutable, null, availableApp == null ? null : availableApp.Version, installedVersion, updateAvailable);
        }

        public Task<BootstrapperReadyApp> EnsureReadyAsync(string appId, CancellationToken cancellationToken)
        {
            return EnsureReadyAsync(GetCatalog(), appId, cancellationToken);
        }

        public async Task<BootstrapperReadyApp> EnsureReadyAsync(BootstrapperCatalog catalog, string appId,
            CancellationToken cancellationToken)
        {
            if (catalog == null) throw new ArgumentNullException("catalog");
            EnsureCatalogSourcePinned(catalog);
            BootstrapperApp app = FindApp(catalog, appId);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.CacheRoot, true);
            string runtimeDestination = RuntimeCachePath(catalog.Runtime);
            string appDestination = AppCachePath(app);
            await EnsureRuntimeAsync(catalog, runtimeDestination, cancellationToken).ConfigureAwait(false);
            await EnsureAppAsync(catalog, app, appDestination, cancellationToken).ConfigureAwait(false);
            string executable = SafeFiles.CombineContained(appDestination, app.Executable, "App executable");
            string runtime = RuntimeDirectory(catalog);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(executable, false);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(runtime, false);
            return new BootstrapperReadyApp(app, executable, Path.GetDirectoryName(executable), runtime);
        }

        public Task<BootstrapperLaunchResult> LaunchAsync(string appId, CancellationToken cancellationToken)
        {
            BootstrapperCatalog catalog = GetCatalog();
            BootstrapperApp app = catalog.FindApp(appId) ?? FindInstalledAppFromReceipt(appId);
            if (app == null) throw new ArgumentException("The app is not in the current catalog and has no known installation.", "appId");
            if (!GetStatus(appId).IsInstalled)
                throw new InvalidOperationException("Install the app before launching it.");

            string receipt = ReceiptPath(app);
            string ownedShortcut;
            Dictionary<string, string> fields;
            if (TryGetValidInstallReceipt(app, receipt, out ownedShortcut, out fields))
            {
                string executable;
                string workingDirectory;
                string runtimeDirectory;
                string[] arguments;
                if (!TryResolveReceiptExecution(app, fields, out executable, out workingDirectory,
                        out runtimeDirectory, out arguments))
                    throw new InvalidDataException("The installed app cache failed validation; the current installation was preserved.");
                cancellationToken.ThrowIfCancellationRequested();
                int processId = StartShellProcess(executable, workingDirectory, arguments, runtimeDirectory);
                return Task.FromResult(new BootstrapperLaunchResult(processId, executable));
            }

            LegacyManagedInstall legacy = TryFindLegacyInstall(app);
            if (legacy == null)
                throw new InvalidDataException("The installed app receipt could not be validated; the installation was preserved.");
            cancellationToken.ThrowIfCancellationRequested();
            int legacyProcessId = StartShellProcess(legacy.ExecutablePath, legacy.WorkingDirectory,
                legacy.Arguments, legacy.RuntimeDirectory);
            return Task.FromResult(new BootstrapperLaunchResult(legacyProcessId, legacy.ExecutablePath));
        }
        private LegacyManagedInstall TryFindLegacyInstall(BootstrapperApp app)
        {
            try { return new LegacyManagedInstallStore(_options).Find(app); }
            catch { return null; }
        }

        private static int StartShellProcess(string executablePath, string workingDirectory,
            string[] arguments, string runtimeDirectory)
        {
            if (String.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
                throw new FileNotFoundException("The installed app executable is missing.", executablePath);
            if (String.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException("The installed app working directory is missing.");

            lock (ProcessEnvironmentGate)
            {
                string oldRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
                string oldRootX64 = Environment.GetEnvironmentVariable("DOTNET_ROOT_X64");
                try
                {
                    if (!String.IsNullOrEmpty(runtimeDirectory))
                    {
                        Environment.SetEnvironmentVariable("DOTNET_ROOT", runtimeDirectory, EnvironmentVariableTarget.Process);
                        Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", runtimeDirectory, EnvironmentVariableTarget.Process);
                    }
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = true,
                        Arguments = BuildArguments(arguments)
                    };
                    using (Process process = Process.Start(startInfo))
                    {
                        if (process == null) throw new InvalidOperationException("Windows did not start the app process.");
                        return process.Id;
                    }
                }
                finally
                {
                    Environment.SetEnvironmentVariable("DOTNET_ROOT", oldRoot, EnvironmentVariableTarget.Process);
                    Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", oldRootX64, EnvironmentVariableTarget.Process);
                }
            }
        }

        private static string BuildArguments(string[] arguments)
        {
            if (arguments == null || arguments.Length == 0) return String.Empty;
            var builder = new StringBuilder();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append(QuoteArgument(arguments[i] ?? String.Empty));
            }
            return builder.ToString();
        }

        public Task<BootstrapperInstallResult> InstallAsync(string appId, CancellationToken cancellationToken)
        {
            return InstallForCatalogAsync(GetCatalog(), appId, cancellationToken);
        }

        public Task<BootstrapperInstallResult> UpdateAsync(string appId, CancellationToken cancellationToken)
        {
            BootstrapperCatalog catalog = GetCatalog();
            BootstrapperAppStatus status = GetStatus(appId);
            if (!status.IsInstalled)
                throw new InvalidOperationException("Install the app before updating it.");
            if (!status.UpdateAvailable)
                throw new InvalidOperationException("No update is available.");
            return InstallForCatalogAsync(catalog, appId, cancellationToken);
        }

        private async Task<BootstrapperInstallResult> InstallForCatalogAsync(BootstrapperCatalog catalog,
            string appId, CancellationToken cancellationToken)
        {
            EnsureCatalogSourcePinned(catalog);
            BootstrapperApp app = FindApp(catalog, appId);
            await EnsureReadyAsync(catalog, app.Id, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            string stableBootstrapper = StableBootstrapperPath(catalog);
            string receipt = ReceiptPath(app);
            string shortcut = ShortcutPath(app);
            string installDirectory = Path.GetDirectoryName(receipt);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.CacheRoot, true);
            SafeFiles.EnsureSafeDirectory(_options.CacheRoot, installDirectory, true);
            SafeFiles.EnsureSafeDirectory(_options.CacheRoot, Path.GetDirectoryName(stableBootstrapper), true);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.StartMenuRoot, true);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.BootstrapperExecutablePath, false);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(stableBootstrapper, false);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(receipt, false);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(shortcut, false);

            SemaphoreSlim gate = InstallLocks.GetOrAdd("install:" + app.Id, delegate { return new SemaphoreSlim(1, 1); });
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string previousOwnedShortcut;
                Dictionary<string, string> previousFields;
                bool hadManagedReceipt = TryGetValidInstallReceipt(app, receipt, out previousOwnedShortcut, out previousFields);
                if (hadManagedReceipt)
                    shortcut = previousOwnedShortcut;
                LegacyManagedInstall legacyInstall = hadManagedReceipt ? null : TryFindLegacyInstall(app);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(shortcut, false);
                Directory.CreateDirectory(Path.GetDirectoryName(stableBootstrapper));
                if (!File.Exists(stableBootstrapper) ||
                    !String.Equals(SafeFiles.Sha256File(stableBootstrapper), SafeFiles.Sha256File(_options.BootstrapperExecutablePath), StringComparison.OrdinalIgnoreCase))
                {
                    string copyTemp = stableBootstrapper + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Copy(_options.BootstrapperExecutablePath, copyTemp, false);
                        if (File.Exists(stableBootstrapper)) File.Replace(copyTemp, stableBootstrapper, null, true);
                        else File.Move(copyTemp, stableBootstrapper);
                    }
                    finally { if (File.Exists(copyTemp)) File.Delete(copyTemp); }
                }

                byte[] oldReceipt = File.Exists(receipt) ? File.ReadAllBytes(receipt) : null;
                byte[] oldShortcut = File.Exists(shortcut) ? File.ReadAllBytes(shortcut) : null;
                SafeFiles.EnsureExistingPathHasNoReparsePoints(Path.Combine(installDirectory, "app-icon.ico"), false);
                string iconPath = WriteIconIfUsable(app, installDirectory);
                byte[] receiptBytes = new UTF8Encoding(false).GetBytes(
                    "appId=" + app.Id + "\r\n" + "catalogSha256=" + catalog.Sha256 + "\r\n" +
                    "packageSha256=" + app.Sha256 + "\r\n" + "runtimeSha256=" + catalog.Runtime.Sha256 + "\r\n" +
                    "appVersion=" + (app.Version ?? String.Empty) + "\r\n" +
                    "appVersionSource=" + (app.VersionSource ?? (app.Version == null ? "unknown" : "catalog")) + "\r\n" +
                    "runtimeVersion=" + catalog.Runtime.Version + "\r\n" +
                    "argumentsBase64=" + EncodeArguments(app.Arguments) + "\r\n" +
                    "executable=" + app.Executable + "\r\n" + "launcher=" + stableBootstrapper + "\r\n");
                try
                {
                    CreateShortcut(shortcut, stableBootstrapper, app.Id, iconPath);
                    SafeFiles.WriteAtomic(receipt, receiptBytes);
                    if (legacyInstall != null)
                        RetireLegacyRegistrations(legacyInstall, shortcut);
                }
                catch
                {
                    RestoreFile(shortcut, oldShortcut);
                    RestoreFile(receipt, oldReceipt);
                    if (legacyInstall != null)
                        foreach (KeyValuePair<string, byte[]> legacyFile in legacyInstall.RegistrationFiles)
                            RestoreFile(legacyFile.Key, legacyFile.Value);
                    throw;
                }
                return new BootstrapperInstallResult(shortcut, receipt, stableBootstrapper);
            }
            finally { gate.Release(); }
        }

        public async Task<BootstrapperUninstallResult> UninstallAsync(string appId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BootstrapperCatalog catalog = GetCatalog();
            BootstrapperApp app = catalog.FindApp(appId) ?? FindInstalledAppFromReceipt(appId);
            if (app == null) throw new ArgumentException("The app is not in the current catalog and has no known installation.", "appId");
            SemaphoreSlim gate = InstallLocks.GetOrAdd("install:" + app.Id, delegate { return new SemaphoreSlim(1, 1); });
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string receipt = ReceiptPath(app);
                var registrationFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(receipt))
                {
                    string shortcut;
                    if (!TryGetValidInstallReceipt(app, receipt, out shortcut))
                        throw new InvalidDataException("The app installation record or shortcut failed validation; it was preserved.");
                    AddRegistrationSnapshot(registrationFiles, receipt, Path.Combine(_options.CacheRoot, "installed"));
                    AddRegistrationSnapshot(registrationFiles, shortcut, _options.StartMenuRoot);
                    string appIcon = SafeFiles.CombineContained(Path.GetDirectoryName(receipt), "app-icon.ico", "Installed shortcut icon");
                    SafeFiles.EnsureExistingPathHasNoReparsePoints(appIcon, false);
                    if (File.Exists(appIcon))
                    {
                        byte[] icon = File.ReadAllBytes(appIcon);
                        if (!IsIconFile(icon)) throw new InvalidDataException("The installed shortcut icon failed validation; it was preserved.");
                        AddRegistrationSnapshot(registrationFiles, appIcon, Path.Combine(_options.CacheRoot, "installed"));
                    }
                }

                LegacyManagedInstall legacy = new LegacyManagedInstallStore(_options).Find(app);
                if (legacy != null)
                {
                    foreach (KeyValuePair<string, byte[]> snapshot in legacy.RegistrationFiles)
                    {
                        string ownedFileName = Path.GetFileName(snapshot.Key);
                        if (ownedFileName.StartsWith("shortcut-icon.", StringComparison.OrdinalIgnoreCase) &&
                            !IsSupportedLegacyIcon(ownedFileName, snapshot.Value))
                            throw new InvalidDataException("A legacy shortcut icon failed validation; it was preserved.");
                        string root = SafeFiles.IsWithinOrEqual(_options.StartMenuRoot, snapshot.Key)
                            ? _options.StartMenuRoot : Path.Combine(_options.CacheRoot, "Apps");
                        AddRegistrationSnapshot(registrationFiles, snapshot.Key, root, snapshot.Value);
                    }
                }

                if (registrationFiles.Count == 0)
                {
                    if (FindOwnedShortcuts(app.Id).Length != 0)
                        throw new InvalidDataException("The app shortcut exists without a valid install receipt; it was preserved.");
                    return new BootstrapperUninstallResult(app.Id, false);
                }

                SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.CacheRoot, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.StartMenuRoot, false);
                foreach (string path in registrationFiles.Keys)
                    SafeFiles.EnsureExistingPathHasNoReparsePoints(path, false);
                foreach (string candidateShortcut in FindOwnedShortcuts(app.Id))
                    if (!registrationFiles.ContainsKey(Path.GetFullPath(candidateShortcut)))
                        throw new InvalidDataException("An app shortcut without a matching managed receipt was preserved.");
                try
                {
                    foreach (string path in registrationFiles.Keys)
                        File.Delete(path);
                }
                catch (Exception uninstallError)
                {
                    var rollbackErrors = new List<Exception>();
                    foreach (KeyValuePair<string, byte[]> snapshot in registrationFiles)
                    {
                        try
                        {
                            if (!File.Exists(snapshot.Key))
                            {
                                string root = SafeFiles.IsWithinOrEqual(_options.StartMenuRoot, snapshot.Key)
                                    ? _options.StartMenuRoot : _options.CacheRoot;
                                SafeFiles.EnsureSafeDirectory(root, Path.GetDirectoryName(snapshot.Key), true);
                                SafeFiles.EnsureExistingPathHasNoReparsePoints(snapshot.Key, false);
                                SafeFiles.WriteAtomic(snapshot.Key, snapshot.Value);
                            }
                        }
                        catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
                    }
                    if (rollbackErrors.Count > 0)
                    {
                        rollbackErrors.Insert(0, uninstallError);
                        throw new AggregateException("Uninstall failed and one or more install records could not be restored.",
                            rollbackErrors.ToArray());
                    }
                    throw;
                }
                return new BootstrapperUninstallResult(app.Id, true);
            }
            finally { gate.Release(); }
        }

        private static void AddRegistrationSnapshot(IDictionary<string, byte[]> snapshots, string path, string allowedRoot,
            byte[] knownBytes = null)
        {
            string fullRoot = Path.GetFullPath(allowedRoot);
            string fullPath = Path.GetFullPath(path);
            if (!SafeFiles.IsWithinOrEqual(fullRoot, fullPath) || String.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An install registration file is outside its owned directory.");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(fullRoot, false);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(fullPath, false);
            byte[] bytes = knownBytes ?? File.ReadAllBytes(fullPath);
            byte[] existing;
            if (snapshots.TryGetValue(fullPath, out existing))
            {
                if (!BytesEqual(existing, bytes))
                    throw new InvalidDataException("Two install receipts claim different contents for one registration file.");
                return;
            }
            snapshots.Add(fullPath, bytes);
        }

        private void RetireLegacyRegistrations(LegacyManagedInstall legacy, string replacementShortcut)
        {
            if (legacy == null) return;
            string appsRoot = SafeFiles.CombineContained(_options.CacheRoot, "Apps", "Legacy app registration root");
            foreach (KeyValuePair<string, byte[]> registration in legacy.RegistrationFiles)
            {
                string path = Path.GetFullPath(registration.Key);
                if (String.Equals(path, Path.GetFullPath(replacementShortcut), StringComparison.OrdinalIgnoreCase)) continue;
                if (!SafeFiles.IsWithinOrEqual(appsRoot, path) && !SafeFiles.IsWithinOrEqual(_options.StartMenuRoot, path))
                    throw new InvalidDataException("A legacy registration file is outside its owned directory.");
                SafeFiles.EnsureExistingPathHasNoReparsePoints(path, false);
                if (!File.Exists(path) || !BytesEqual(File.ReadAllBytes(path), registration.Value))
                    throw new InvalidDataException("A legacy registration changed during migration; it was preserved.");
                File.Delete(path);
            }
        }

        private async Task EnsureRuntimeAsync(BootstrapperCatalog catalog, string destination,
            CancellationToken cancellationToken)
        {
            SemaphoreSlim gate = InstallLocks.GetOrAdd("runtime:" + catalog.Runtime.Sha256, delegate { return new SemaphoreSlim(1, 1); });
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsValidRuntimeCache(catalog.Runtime, destination)) return;
                if (Directory.Exists(destination) || File.Exists(destination))
                    throw new InvalidDataException("A runtime cache entry exists but failed validation; it was preserved.");
                string parent = Path.GetDirectoryName(destination);
                SafeFiles.EnsureSafeDirectory(_options.CacheRoot, parent, true);
                string stage = Path.Combine(parent, ".stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                try
                {
                    string zip = Path.Combine(stage, "package.zip");
                    using (PackageDownloader downloader = Downloader(catalog))
                        await downloader.DownloadAsync(catalog.Runtime.Asset, catalog.Runtime.Sha256,
                            catalog.Runtime.Bytes, zip, "runtime", Progress, cancellationToken).ConfigureAwait(false);
                    string extracted = Path.Combine(stage, "payload");
                    SafeZipExtractor.ExtractRuntime(zip, extracted, _options.CacheRoot, _options,
                        catalog.Runtime, null);
                    File.Delete(zip);
                    WriteReadyMarker(extracted, "runtime", catalog.Runtime.Sha256, catalog.Runtime.Version);
                    Directory.Move(extracted, destination);
                }
                finally { SafeFiles.DeleteOwnedStaging(parent, stage); }
            }
            finally { gate.Release(); }
        }

        private async Task EnsureAppAsync(BootstrapperCatalog catalog, BootstrapperApp app,
            string destination, CancellationToken cancellationToken)
        {
            SemaphoreSlim gate = InstallLocks.GetOrAdd("app:" + app.Id + ":" + app.Sha256,
                delegate { return new SemaphoreSlim(1, 1); });
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string executable = SafeFiles.CombineContained(destination, app.Executable, "App executable");
                if (IsValidAppCache(app, destination, executable)) return;
                if (Directory.Exists(destination) || File.Exists(destination))
                    throw new InvalidDataException("An app cache entry exists but failed validation; it was preserved.");
                string parent = Path.GetDirectoryName(destination);
                SafeFiles.EnsureSafeDirectory(_options.CacheRoot, parent, true);
                string stage = Path.Combine(parent, ".stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                try
                {
                    string zip = Path.Combine(stage, "package.zip");
                    using (PackageDownloader downloader = Downloader(catalog))
                        await downloader.DownloadAsync(app.Asset, app.Sha256, app.Bytes, zip,
                            app.Id, Progress, cancellationToken).ConfigureAwait(false);
                    string extracted = Path.Combine(stage, "payload");
                    SafeZipExtractor.ExtractApp(zip, extracted, _options.CacheRoot, _options, app, null);
                    File.Delete(zip);
                    WriteReadyMarker(extracted, app.Id, app.Sha256, String.Empty);
                    Directory.Move(extracted, destination);
                }
                finally { SafeFiles.DeleteOwnedStaging(parent, stage); }
            }
            finally { gate.Release(); }
        }

        private PackageDownloader Downloader(BootstrapperCatalog catalog)
        {
            return new PackageDownloader(_options, catalog, _credentials, _transportFactory(), _anonymousTransportFactory);
        }

        private bool IsValidRuntimeCache(BootstrapperRuntime runtime, string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return false;
                SafeFiles.EnsureExistingPathHasNoReparsePoints(directory, false);
                return MarkerMatches(directory, "runtime", runtime.Sha256, runtime.Version) &&
                       File.Exists(Path.Combine(directory, "dotnet.exe")) &&
                       File.Exists(Path.Combine(directory, "host", "fxr", runtime.Version, "hostfxr.dll")) &&
                       File.Exists(Path.Combine(directory, "shared", "Microsoft.NETCore.App", runtime.Version, "coreclr.dll")) &&
                       File.Exists(Path.Combine(directory, "shared", "Microsoft.WindowsDesktop.App", runtime.Version, "PresentationFramework.dll"));
            }
            catch { return false; }
        }

        private bool IsValidAppCache(BootstrapperApp app, string directory, string executable)
        {
            try
            {
                if (!Directory.Exists(directory) || !File.Exists(executable)) return false;
                SafeFiles.EnsureExistingPathHasNoReparsePoints(executable, false);
                return MarkerMatches(directory, app.Id, app.Sha256, String.Empty);
            }
            catch { return false; }
        }

        private static bool MarkerMatches(string directory, string id, string hash, string version)
        {
            string marker = Path.Combine(directory, ".ready");
            if (!File.Exists(marker)) return false;
            string expected = id + "\n" + hash + "\n" + version + "\n";
            return String.Equals(File.ReadAllText(marker, Encoding.UTF8), expected, StringComparison.Ordinal);
        }

        private static void WriteReadyMarker(string directory, string id, string hash, string version)
        {
            SafeFiles.WriteAtomic(Path.Combine(directory, ".ready"),
                new UTF8Encoding(false).GetBytes(id + "\n" + hash + "\n" + version + "\n"));
        }

        private string RuntimeCachePath(BootstrapperRuntime runtime)
        {
            return SafeFiles.CombineContained(_options.CacheRoot, Path.Combine("runtime", runtime.Sha256), "Runtime cache");
        }

        private string RuntimeDirectory(BootstrapperCatalog catalog)
        {
            return RuntimeCachePath(catalog.Runtime);
        }

        private string AppCachePath(BootstrapperApp app)
        {
            return SafeFiles.CombineContained(_options.CacheRoot,
                Path.Combine("apps", app.Id, app.Sha256), "App cache");
        }

        private string ReceiptPath(BootstrapperApp app)
        {
            return SafeFiles.CombineContained(_options.CacheRoot,
                Path.Combine("installed", app.Id, "current.receipt"), "Install receipt");
        }

        private string ReceiptPath(string appId)
        {
            if (String.IsNullOrWhiteSpace(appId) || appId.Length > 80 ||
                !System.Text.RegularExpressions.Regex.IsMatch(appId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$"))
                return null;
            return SafeFiles.CombineContained(_options.CacheRoot,
                Path.Combine("installed", appId, "current.receipt"), "Install receipt");
        }

        private BootstrapperApp FindInstalledAppFromReceipt(string appId)
        {
            string receipt = ReceiptPath(appId);
            if (receipt == null) return null;
            try
            {
                SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.CacheRoot, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(receipt, false);
                if (!File.Exists(receipt) || new FileInfo(receipt).Length > 16 * 1024) return null;
                string[] lines = File.ReadAllLines(receipt, new UTF8Encoding(false, true));
                Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in lines)
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0 || fields.ContainsKey(line.Substring(0, separator))) return null;
                    fields.Add(line.Substring(0, separator), line.Substring(separator + 1));
                }
                string receiptAppId, catalogSha, packageSha, runtimeSha;
                if (!fields.TryGetValue("appId", out receiptAppId) || !String.Equals(receiptAppId, appId, StringComparison.Ordinal) ||
                    !fields.TryGetValue("catalogSha256", out catalogSha) || !SafeFiles.IsHash(catalogSha) ||
                    !fields.TryGetValue("packageSha256", out packageSha) || !SafeFiles.IsHash(packageSha) ||
                    !fields.TryGetValue("runtimeSha256", out runtimeSha) || !SafeFiles.IsHash(runtimeSha)) return null;
                BootstrapperCatalog recorded = FindCatalogByHash(catalogSha);
                BootstrapperApp app = recorded == null ? null : recorded.FindApp(appId);
                if (app == null || !String.Equals(app.Sha256, packageSha, StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(recorded.Runtime.Sha256, runtimeSha, StringComparison.OrdinalIgnoreCase)) return null;
                string ownedShortcut;
                Dictionary<string, string> validatedFields;
                return TryGetValidInstallReceipt(app, receipt, out ownedShortcut, out validatedFields) ? app : null;
            }
            catch { return null; }
        }

        private void EnsureInstalledAppsRemainAvailable(BootstrapperCatalog candidate)
        {
            if (_catalog == null || candidate == null) return;
            foreach (BootstrapperApp currentApp in _catalog.Apps)
            {
                if (candidate.FindApp(currentApp.Id) != null) continue;
                try
                {
                    if (GetStatus(currentApp.Id).IsInstalled)
                        throw new InvalidDataException("The release catalog omitted an installed app; the last-good catalog was preserved.");
                }
                catch (InvalidDataException) { throw; }
                catch { }
            }
        }

        private string ShortcutPath(BootstrapperApp app)
        {
            string suffix = " (" + app.Id + ").lnk";
            int available = 239 - Path.GetFullPath(_options.StartMenuRoot).Length - 1;
            if (available < suffix.Length) throw new PathTooLongException("The configured Start Menu root leaves no room for this app shortcut.");
            string displayName = app.Name.Length > available - suffix.Length
                ? app.Name.Substring(0, Math.Max(1, available - suffix.Length)) : app.Name;
            string name = displayName + suffix;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(_options.StartMenuRoot, name);
        }

        private string StableBootstrapperPath(BootstrapperCatalog catalog)
        {
            return SafeFiles.CombineContained(_options.CacheRoot,
                Path.Combine("launcher", catalog.Sha256, "i-luncher.exe"), "Stable launcher");
        }

        private bool TryGetValidInstallReceipt(BootstrapperApp app, string receiptPath, out string ownedShortcut)
        {
            Dictionary<string, string> ignored;
            return TryGetValidInstallReceipt(app, receiptPath, out ownedShortcut, out ignored);
        }

        private bool TryGetValidInstallReceipt(BootstrapperApp app, string receiptPath,
            out string ownedShortcut, out Dictionary<string, string> receiptFields)
        {
            ownedShortcut = null;
            receiptFields = null;
            try
            {
                SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.CacheRoot, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.StartMenuRoot, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(receiptPath, false);
                if (!File.Exists(receiptPath)) return false;
                Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
                string[] lines = File.ReadAllLines(receiptPath, new UTF8Encoding(false, true));
                for (int i = 0; i < lines.Length; i++)
                {
                    int separator = lines[i].IndexOf('=');
                    if (separator <= 0 || fields.ContainsKey(lines[i].Substring(0, separator))) return false;
                    fields.Add(lines[i].Substring(0, separator), lines[i].Substring(separator + 1));
                }
                if ((fields.Count != 6 && fields.Count != 9 && fields.Count != 10) || !fields.ContainsKey("appId") || !fields.ContainsKey("catalogSha256") ||
                    !fields.ContainsKey("packageSha256") || !fields.ContainsKey("runtimeSha256") ||
                    !fields.ContainsKey("executable") || !fields.ContainsKey("launcher")) return false;
                string[] allowedFields = { "appId", "catalogSha256", "packageSha256", "runtimeSha256", "executable", "launcher",
                    "appVersion", "appVersionSource", "runtimeVersion", "argumentsBase64" };
                if (fields.Keys.Any(key => !allowedFields.Contains(key, StringComparer.Ordinal))) return false;
                bool hasRuntimeMetadata = fields.ContainsKey("runtimeVersion") && fields.ContainsKey("argumentsBase64");
                bool hasLegacyVersionedMetadata = fields.ContainsKey("appVersion") && hasRuntimeMetadata && !fields.ContainsKey("appVersionSource");
                bool hasNewVersionedMetadata = fields.ContainsKey("appVersion") && fields.ContainsKey("appVersionSource") && hasRuntimeMetadata;
                if (!IsValidReceiptVersionMetadata(fields)) return false;
                if (!String.Equals(fields["appId"], app.Id, StringComparison.Ordinal) ||
                    !SafeFiles.IsHash(fields["catalogSha256"]) || !SafeFiles.IsHash(fields["packageSha256"]) ||
                    !SafeFiles.IsHash(fields["runtimeSha256"])) return false;
                string[] decodedArguments;
                if (hasRuntimeMetadata && (!IsSafeVersion(fields["runtimeVersion"]) ||
                    !TryDecodeArguments(fields["argumentsBase64"], out decodedArguments))) return false;
                if (hasLegacyVersionedMetadata && !IsSafeVersion(fields["appVersion"])) return false;

                string appDirectory = SafeFiles.CombineContained(_options.CacheRoot,
                    Path.Combine("apps", app.Id, fields["packageSha256"]), "Installed app cache");
                string executable = SafeFiles.CombineContained(appDirectory, fields["executable"], "Installed app executable");
                SafeFiles.EnsureExistingPathHasNoReparsePoints(appDirectory, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(executable, false);

                string runtimeDirectory = SafeFiles.CombineContained(_options.CacheRoot,
                    Path.Combine("runtime", fields["runtimeSha256"]), "Installed runtime cache");
                SafeFiles.EnsureExistingPathHasNoReparsePoints(runtimeDirectory, false);

                string stableBootstrapper = SafeFiles.CombineContained(_options.CacheRoot,
                    Path.Combine("launcher", fields["catalogSha256"], "i-luncher.exe"), "Installed launcher");
                if (!String.Equals(Path.GetFullPath(fields["launcher"]), Path.GetFullPath(stableBootstrapper), StringComparison.OrdinalIgnoreCase))
                    return false;
                SafeFiles.EnsureExistingPathHasNoReparsePoints(stableBootstrapper, false);

                string[] shortcuts = FindOwnedShortcuts(app.Id);
                string matchingShortcut = null;
                foreach (string shortcutPath in shortcuts)
                {
                    try
                    {
                        SafeFiles.EnsureExistingPathHasNoReparsePoints(shortcutPath, false);
                        if (!IsOnlineShortcut(shortcutPath, stableBootstrapper, app.Id)) continue;
                        if (matchingShortcut != null) return false;
                        matchingShortcut = shortcutPath;
                    }
                    catch { }
                }
                if (matchingShortcut == null) return false;
                ownedShortcut = matchingShortcut;
                receiptFields = fields;
                return true;
            }
            catch { return false; }
        }

        private bool TryResolveReceiptReady(BootstrapperApp app, IDictionary<string, string> fields,
            out string executable)
        {
            string ignoredWorkingDirectory, ignoredRuntimeDirectory;
            string[] ignoredArguments;
            return TryResolveReceiptExecution(app, fields, out executable, out ignoredWorkingDirectory,
                out ignoredRuntimeDirectory, out ignoredArguments);
        }

        private bool TryResolveReceiptExecution(BootstrapperApp app, IDictionary<string, string> fields,
            out string executable, out string workingDirectory, out string runtimeDirectory,
            out string[] arguments)
        {
            executable = null;
            workingDirectory = null;
            runtimeDirectory = null;
            arguments = new string[0];
            try
            {
                string appDirectory = SafeFiles.CombineContained(_options.CacheRoot,
                    Path.Combine("apps", app.Id, fields["packageSha256"]), "Installed app cache");
                executable = SafeFiles.CombineContained(appDirectory, fields["executable"], "Installed app executable");
                if (!String.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
                string appMarker = Path.Combine(appDirectory, ".ready");
                SafeFiles.EnsureExistingPathHasNoReparsePoints(appDirectory, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(executable, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(appMarker, false);
                string expectedAppMarker = app.Id + "\n" + fields["packageSha256"] + "\n\n";
                if (!File.Exists(executable) || !File.Exists(appMarker) ||
                    !String.Equals(File.ReadAllText(appMarker, Encoding.UTF8), expectedAppMarker, StringComparison.Ordinal)) return false;

                runtimeDirectory = SafeFiles.CombineContained(_options.CacheRoot,
                    Path.Combine("runtime", fields["runtimeSha256"]), "Installed runtime cache");
                SafeFiles.EnsureExistingPathHasNoReparsePoints(runtimeDirectory, false);
                string runtimeMarker = SafeFiles.CombineContained(runtimeDirectory, ".ready", "Installed runtime marker");
                SafeFiles.EnsureExistingPathHasNoReparsePoints(runtimeMarker, false);
                if (!File.Exists(runtimeMarker)) return false;
                string[] markerLines = File.ReadAllLines(runtimeMarker, new UTF8Encoding(false, true));
                if (markerLines.Length != 3 || !String.Equals(markerLines[0], "runtime", StringComparison.Ordinal) ||
                    !String.Equals(markerLines[1], fields["runtimeSha256"], StringComparison.OrdinalIgnoreCase)) return false;
                if (fields.ContainsKey("runtimeVersion") && !String.Equals(markerLines[2], fields["runtimeVersion"], StringComparison.Ordinal)) return false;
                if (!File.Exists(Path.Combine(runtimeDirectory, "dotnet.exe")) ||
                    !File.Exists(Path.Combine(runtimeDirectory, "host", "fxr", markerLines[2], "hostfxr.dll")) ||
                    !File.Exists(Path.Combine(runtimeDirectory, "shared", "Microsoft.NETCore.App", markerLines[2], "coreclr.dll")) ||
                    !File.Exists(Path.Combine(runtimeDirectory, "shared", "Microsoft.WindowsDesktop.App", markerLines[2], "PresentationFramework.dll"))) return false;

                if (fields.ContainsKey("argumentsBase64") && !TryDecodeArguments(fields["argumentsBase64"], out arguments)) return false;
                workingDirectory = Path.GetDirectoryName(executable);
                return Directory.Exists(workingDirectory);
            }
            catch
            {
                executable = null;
                workingDirectory = null;
                runtimeDirectory = null;
                arguments = new string[0];
                return false;
            }
        }

        private string ResolveInstalledVersion(BootstrapperApp app, IDictionary<string, string> fields)
        {
            string value;
            string versionSource;
            if (fields.TryGetValue("appVersionSource", out versionSource) &&
                (versionSource == "manifest" || versionSource == "project") &&
                fields.TryGetValue("appVersion", out value) && IsSafeVersion(value)) return value;
            string packageSha;
            string packageVersion;
            if (versionSource == "package" && fields.TryGetValue("appVersion", out value) &&
                fields.TryGetValue("packageSha256", out packageSha) &&
                LegacyAppVersionIndex.TryResolve(app.Id, packageSha, out packageVersion) &&
                String.Equals(value, packageVersion, StringComparison.Ordinal)) return value;
            string catalogSha;
            if (!fields.TryGetValue("catalogSha256", out catalogSha)) return null;
            BootstrapperCatalog recorded = FindCatalogByHash(catalogSha);
            if (recorded == null) return null;
            BootstrapperApp recordedApp = recorded.FindApp(app.Id);
            return recordedApp != null && String.Equals(recordedApp.Sha256, fields["packageSha256"], StringComparison.OrdinalIgnoreCase) &&
                   String.Equals(recorded.Runtime.Sha256, fields["runtimeSha256"], StringComparison.OrdinalIgnoreCase)
                ? recordedApp.Version : null;
        }

        private static string ReadInstalledBinaryVersion(string executablePath)
        {
            try
            {
                SafeFiles.EnsureExistingPathHasNoReparsePoints(executablePath, false);
                if (!File.Exists(executablePath)) return null;
                string version = ReadBinaryVersion(executablePath);
                if (!String.IsNullOrEmpty(version)) return version;
                string assemblyPath = Path.Combine(Path.GetDirectoryName(executablePath),
                    Path.GetFileNameWithoutExtension(executablePath) + ".dll");
                SafeFiles.EnsureExistingPathHasNoReparsePoints(assemblyPath, false);
                return File.Exists(assemblyPath) ? ReadBinaryVersion(assemblyPath) : null;
            }
            catch { return null; }
        }

        private static string ReadBinaryVersion(string path)
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            string version = info.ProductVersion == null ? null : info.ProductVersion.Trim();
            if (IsSafeVersion(version)) return version;
            version = info.FileVersion == null ? null : info.FileVersion.Trim();
            return IsSafeVersion(version) ? version : null;
        }

        internal static bool IsValidReceiptVersionMetadata(IDictionary<string, string> fields)
        {
            if (fields == null) return false;
            bool hasRuntimeVersion = fields.ContainsKey("runtimeVersion");
            bool hasArguments = fields.ContainsKey("argumentsBase64");
            bool hasAppVersion = fields.ContainsKey("appVersion");
            bool hasVersionSource = fields.ContainsKey("appVersionSource");
            bool hasRuntimeMetadata = hasRuntimeVersion && hasArguments;
            bool legacyVersioned = hasAppVersion && hasRuntimeMetadata && !hasVersionSource;
            bool sourcedVersioned = hasAppVersion && hasVersionSource && hasRuntimeMetadata;
            if (hasRuntimeVersion != hasArguments || (hasVersionSource && !hasAppVersion)) return false;
            if ((fields.Count == 9) != legacyVersioned || (fields.Count == 10) != sourcedVersioned ||
                (fields.Count == 6 && (hasRuntimeMetadata || hasAppVersion || hasVersionSource))) return false;
            if (fields.Count == 9) return IsSafeVersion(fields["appVersion"]);
            if (fields.Count != 10) return fields.Count == 6 && !hasRuntimeVersion && !hasAppVersion && !hasVersionSource;
            string source = fields["appVersionSource"];
            string version = fields["appVersion"];
            return (source == "unknown" && version.Length == 0) ||
                ((source == "manifest" || source == "project" || source == "catalog" || source == "package") && IsSafeVersion(version));
        }

        private BootstrapperCatalog FindCatalogByHash(string sha256)
        {
            if (_embeddedCatalog != null && String.Equals(_embeddedCatalog.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                return _embeddedCatalog;
            if (_catalog != null && String.Equals(_catalog.Sha256, sha256, StringComparison.OrdinalIgnoreCase)) return _catalog;
            string path = CatalogArchivePath(sha256);
            try
            {
                if (!File.Exists(path)) return null;
                SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.CacheRoot, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(path, false);
                string json = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
                string actual = SafeFiles.Sha256(new UTF8Encoding(false, true).GetBytes(json));
                if (!String.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase)) return null;
                BootstrapperCatalog parsed = CatalogParser.Parse(json, actual, _options);
                EnsureCatalogSourcePinned(parsed);
                return parsed;
            }
            catch { return null; }
        }

        private static bool IsSafeVersion(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
                System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$");
        }

        internal static bool IsNewerAppVersion(string available, string installed)
        {
            string[] availableCore, availablePre, installedCore, installedPre;
            if (!TryParseAppVersion(available, out availableCore, out availablePre) ||
                !TryParseAppVersion(installed, out installedCore, out installedPre)) return false;
            for (int i = 0; i < 4; i++)
            {
                string left = i < availableCore.Length ? availableCore[i] : "0";
                string right = i < installedCore.Length ? installedCore[i] : "0";
                int comparison = CompareNumericText(left, right);
                if (comparison != 0) return comparison > 0;
            }
            if (availablePre == null) return installedPre != null;
            if (installedPre == null) return false;
            int count = Math.Min(availablePre.Length, installedPre.Length);
            for (int i = 0; i < count; i++)
            {
                bool leftNumeric = IsAsciiDigits(availablePre[i]);
                bool rightNumeric = IsAsciiDigits(installedPre[i]);
                int comparison;
                if (leftNumeric && rightNumeric) comparison = CompareNumericText(availablePre[i], installedPre[i]);
                else if (leftNumeric != rightNumeric) comparison = leftNumeric ? -1 : 1;
                else comparison = String.CompareOrdinal(availablePre[i], installedPre[i]);
                if (comparison != 0) return comparison > 0;
            }
            return availablePre.Length > installedPre.Length;
        }

        private static bool TryParseAppVersion(string value, out string[] core, out string[] prerelease)
        {
            core = null;
            prerelease = null;
            if (String.IsNullOrEmpty(value) || value.Length > 64) return false;
            if (value[0] == 'v' || value[0] == 'V') value = value.Substring(1);
            int plus = value.IndexOf('+');
            if (plus >= 0)
            {
                if (value.IndexOf('+', plus + 1) >= 0 || !IsVersionIdentifiers(value.Substring(plus + 1), false)) return false;
                value = value.Substring(0, plus);
            }
            int dash = value.IndexOf('-');
            if (dash >= 0)
            {
                string prereleaseText = value.Substring(dash + 1);
                if (!IsVersionIdentifiers(prereleaseText, true)) return false;
                prerelease = prereleaseText.Split('.');
                value = value.Substring(0, dash);
            }
            string[] parts = value.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;
            foreach (string part in parts)
                if (!IsAsciiDigits(part) || (part.Length > 1 && part[0] == '0')) return false;
            core = parts;
            return true;
        }

        private static bool IsVersionIdentifiers(string value, bool prerelease)
        {
            if (String.IsNullOrEmpty(value)) return false;
            string[] identifiers = value.Split('.');
            foreach (string identifier in identifiers)
            {
                if (identifier.Length == 0) return false;
                bool numeric = true;
                foreach (char c in identifier)
                {
                    if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
                          (c >= 'a' && c <= 'z') || c == '-')) return false;
                    if (c < '0' || c > '9') numeric = false;
                }
                if (prerelease && numeric && identifier.Length > 1 && identifier[0] == '0') return false;
            }
            return true;
        }

        private static bool IsAsciiDigits(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;
            foreach (char c in value) if (c < '0' || c > '9') return false;
            return true;
        }

        private static int CompareNumericText(string left, string right)
        {
            int leftStart = 0, rightStart = 0;
            while (leftStart < left.Length - 1 && left[leftStart] == '0') leftStart++;
            while (rightStart < right.Length - 1 && right[rightStart] == '0') rightStart++;
            int leftLength = left.Length - leftStart, rightLength = right.Length - rightStart;
            if (leftLength != rightLength) return leftLength < rightLength ? -1 : 1;
            return String.CompareOrdinal(left, leftStart, right, rightStart, leftLength);
        }

        private static string EncodeArguments(IList<string> arguments)
        {
            if (arguments == null || arguments.Count == 0) return String.Empty;
            List<string> encoded = new List<string>();
            foreach (string argument in arguments)
                encoded.Add(Convert.ToBase64String(new UTF8Encoding(false, true).GetBytes(argument ?? String.Empty)));
            return String.Join(",", encoded.ToArray());
        }

        private static bool TryDecodeArguments(string encoded, out string[] arguments)
        {
            arguments = new string[0];
            try
            {
                if (String.IsNullOrEmpty(encoded)) return true;
                string[] parts = encoded.Split(',');
                if (parts.Length > 32) return false;
                List<string> decoded = new List<string>();
                int total = 0;
                foreach (string part in parts)
                {
                    string value = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(part));
                    if (value.Any(character => character < 0x20)) return false;
                    total += value.Length;
                    if (value.Length > 4096 || total > 8192) return false;
                    decoded.Add(value);
                }
                arguments = decoded.ToArray();
                return true;
            }
            catch { return false; }
        }

        private static bool IsOnlineShortcut(string shortcutPath, string stableBootstrapper, string appId)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;
            object shell = Activator.CreateInstance(shellType);
            object link = null;
            try
            {
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { shortcutPath });
                Type linkType = link.GetType();
                string target = (string)linkType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
                string arguments = (string)linkType.InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null);
                return !String.IsNullOrEmpty(target) &&
                       String.Equals(Path.GetFullPath(target), Path.GetFullPath(stableBootstrapper), StringComparison.OrdinalIgnoreCase) &&
                       String.Equals(arguments, "--app " + QuoteArgument(appId) + " --noUI", StringComparison.Ordinal);
            }
            finally
            {
                if (link != null && System.Runtime.InteropServices.Marshal.IsComObject(link))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        private string[] FindOwnedShortcuts(string appId)
        {
            if (!Directory.Exists(_options.StartMenuRoot)) return new string[0];
            SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.StartMenuRoot, false);
            string suffix = " (" + appId + ").lnk";
            List<string> matches = new List<string>();
            foreach (string entry in Directory.GetFileSystemEntries(_options.StartMenuRoot))
            {
                if (Path.GetFileName(entry).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    matches.Add(entry);
            }
            return matches.ToArray();
        }

        private static void CreateShortcut(string shortcutPath, string target, string appId, string iconPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Windows Script Host is unavailable for shortcut creation.");
            object shell = Activator.CreateInstance(shellType);
            object link = null;
            try
            {
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { shortcutPath });
                Type linkType = link.GetType();
                SetComProperty(linkType, link, "TargetPath", target);
                SetComProperty(linkType, link, "Arguments", "--app " + QuoteArgument(appId) + " --noUI");
                SetComProperty(linkType, link, "WorkingDirectory", Path.GetDirectoryName(target));
                SetComProperty(linkType, link, "Description", "Launch " + appId + " using i-luncher");
                if (!String.IsNullOrEmpty(iconPath)) SetComProperty(linkType, link, "IconLocation", iconPath + ",0");
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                if (link != null && System.Runtime.InteropServices.Marshal.IsComObject(link))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void SetComProperty(Type type, object instance, string property, object value)
        {
            type.InvokeMember(property, BindingFlags.SetProperty, null, instance, new[] { value });
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private string WriteIconIfUsable(BootstrapperApp app, string installDirectory)
        {
            if (String.IsNullOrEmpty(app.IconBase64)) return null;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(app.IconBase64); }
            catch { return null; }
            if (bytes.Length < 6 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0) return null;
            string path = Path.Combine(installDirectory, "app-icon.ico");
            SafeFiles.WriteAtomic(path, bytes);
            return path;
        }

        private static void RestoreFile(string path, byte[] previous)
        {
            if (previous == null)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                if (File.Exists(path))
                {
                    byte[] current = File.ReadAllBytes(path);
                    if (BytesEqual(current, previous)) return;
                }
                SafeFiles.WriteAtomic(path, previous);
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private static bool IsIconFile(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 6 && bytes[0] == 0 && bytes[1] == 0 &&
                   bytes[2] == 1 && bytes[3] == 0;
        }

        private static bool IsSupportedLegacyIcon(string fileName, byte[] bytes)
        {
            if (bytes == null) return false;
            string extension = Path.GetExtension(fileName);
            if (String.Equals(extension, ".ico", StringComparison.OrdinalIgnoreCase)) return IsIconFile(bytes);
            if (String.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
                return bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e &&
                       bytes[3] == 0x47 && bytes[4] == 0x0d && bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a;
            if (String.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase))
                return bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4d;
            return false;
        }

        private void Progress(string appId, long transferred, long total)
        {
            EventHandler<BootstrapperProgressEventArgs> handler = ProgressChanged;
            if (handler != null) handler(this, new BootstrapperProgressEventArgs(appId, "download", transferred, total));
        }

        private async Task<BootstrapperCatalog> FetchLatestCatalogAsync(CancellationToken cancellationToken)
        {
            BootstrapperCatalog pinned = _embeddedCatalog ?? await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
            Uri apiBase = new Uri(pinned.Source.ApiUrl.TrimEnd('/') + "/", UriKind.Absolute);
            string[] ownerRepository = pinned.Source.Repository.Split('/');
            string repositoryPath = Uri.EscapeDataString(ownerRepository[0]) + "/" + Uri.EscapeDataString(ownerRepository[1]);
            Uri releaseUri = new Uri(apiBase, "repos/" + repositoryPath + "/releases/latest");
            byte[] releaseBytes = await RequestApiBytesAsync(releaseUri, "application/vnd.github+json",
                _options.MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);
            Dictionary<string, object> release = CatalogParser.Object(StrictJson.Parse(new UTF8Encoding(false, true).GetString(releaseBytes)), "latest release");
            string tag = JsonText(release, "tag_name", 64);
            if (!RegexSafeTag(tag)) throw new InvalidDataException("The release feed returned an invalid release tag.");
            string version = tag.TrimStart('v', 'V');
            int rollback = CompareReleaseVersions(version, _catalog.Version);
            if (rollback < 0) throw new InvalidDataException("The release feed is older than the last known catalog.");
            string assetName = "i-luncher-online-catalog-" + version + ".json";
            List<object> assets = JsonArray(release, "assets");
            Dictionary<string, object> selected = null;
            foreach (object value in assets)
            {
                Dictionary<string, object> asset = CatalogParser.Object(value, "release asset");
                if (!String.Equals(JsonText(asset, "name", 200), assetName, StringComparison.Ordinal)) continue;
                if (selected != null) throw new InvalidDataException("The release contains duplicate online catalogs.");
                selected = asset;
            }
            if (selected == null) throw new FileNotFoundException("The latest release does not contain its versioned online catalog.");

            long assetId = JsonInteger(selected, "id");
            long assetSize = JsonInteger(selected, "size");
            if (assetId <= 0 || assetSize <= 0 || assetSize > _options.MaximumCatalogBytes)
                throw new InvalidDataException("The latest catalog asset exceeds its size limit.");
            string assetUrl = JsonText(selected, "url", 2048);
            Uri expectedAssetUri = new Uri(apiBase, "repos/" + repositoryPath + "/releases/assets/" +
                assetId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Uri reportedAssetUri;
            if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out reportedAssetUri) ||
                !SameOrigin(new Uri(apiBase.GetLeftPart(UriPartial.Authority)), reportedAssetUri) ||
                !String.Equals(expectedAssetUri.AbsoluteUri, reportedAssetUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) ||
                !String.IsNullOrEmpty(reportedAssetUri.UserInfo) || !String.IsNullOrEmpty(reportedAssetUri.Fragment) ||
                !String.IsNullOrEmpty(reportedAssetUri.Query))
                throw new InvalidDataException("The latest catalog asset URL is outside the pinned release API endpoint.");

            string digest = JsonOptionalText(selected, "digest", 80);
            string expectedDigest = null;
            if (!String.IsNullOrEmpty(digest))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
                    throw new InvalidDataException("The release catalog has an unsupported asset digest.");
                expectedDigest = digest.Substring(7);
            }
            byte[] catalogBytes = await RequestAssetBytesAsync(reportedAssetUri, assetSize, cancellationToken).ConfigureAwait(false);
            if (catalogBytes.LongLength != assetSize) throw new InvalidDataException("The catalog asset size does not match release metadata.");
            string actualDigest = SafeFiles.Sha256(catalogBytes);
            if (expectedDigest != null && !String.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The catalog asset digest does not match release metadata.");
            string json = new UTF8Encoding(false, true).GetString(catalogBytes);
            BootstrapperCatalog catalog = CatalogParser.Parse(json, actualDigest, _options);
            if (!String.Equals(catalog.Version, version, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(catalog.Source.TagName, tag, StringComparison.Ordinal))
                throw new InvalidDataException("The latest release tag and catalog version do not match.");
            EnsureCatalogSourcePinned(catalog);
            return catalog;
        }

        private async Task<byte[]> RequestApiBytesAsync(Uri uri, string accept, long maximumBytes,
            CancellationToken cancellationToken)
        {
            Uri apiOrigin = new Uri(new Uri((_embeddedCatalog ?? _catalog).Source.ApiUrl).GetLeftPart(UriPartial.Authority));
            if (!SameOrigin(apiOrigin, uri)) throw new InvalidDataException("Refusing to send API credentials outside the pinned API origin.");
            using (IBootstrapperHttpTransport transport = _transportFactory())
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.Accept.ParseAdd(accept);
                AddApiCredential(request, apiOrigin);
                using (HttpResponseMessage response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (IsRedirect(response.StatusCode)) throw new InvalidDataException("The release API metadata request attempted to redirect.");
                    if (!response.IsSuccessStatusCode) throw new IOException("The release API request failed with HTTP status " + (int)response.StatusCode + ".");
                    return await ReadBoundedResponseAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<byte[]> RequestAssetBytesAsync(Uri uri, long expectedBytes, CancellationToken cancellationToken)
        {
            Uri apiOrigin = new Uri(new Uri((_embeddedCatalog ?? _catalog).Source.ApiUrl).GetLeftPart(UriPartial.Authority));
            if (!SameOrigin(apiOrigin, uri)) throw new InvalidDataException("Refusing to send API credentials outside the pinned API origin.");
            using (IBootstrapperHttpTransport transport = _transportFactory())
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.Accept.ParseAdd("application/octet-stream");
                AddApiCredential(request, apiOrigin);
                using (HttpResponseMessage response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (IsRedirect(response.StatusCode))
                    {
                        Uri location = response.Headers.Location;
                        ValidateHttpsRedirect(location);
                        using (IBootstrapperHttpTransport anonymous = _anonymousTransportFactory())
                        using (HttpRequestMessage redirected = new HttpRequestMessage(HttpMethod.Get, location))
                        using (HttpResponseMessage downloaded = await anonymous.SendAsync(redirected, cancellationToken).ConfigureAwait(false))
                        {
                            if (IsRedirect(downloaded.StatusCode)) throw new InvalidDataException("The catalog download redirected more than once.");
                            if (!downloaded.IsSuccessStatusCode) throw new IOException("The catalog asset download failed with HTTP status " + (int)downloaded.StatusCode + ".");
                            return await ReadBoundedResponseAsync(downloaded.Content, Math.Min(_options.MaximumCatalogBytes, expectedBytes), cancellationToken).ConfigureAwait(false);
                        }
                    }
                    if (!response.IsSuccessStatusCode) throw new IOException("The catalog asset request failed with HTTP status " + (int)response.StatusCode + ".");
                    return await ReadBoundedResponseAsync(response.Content, Math.Min(_options.MaximumCatalogBytes, expectedBytes), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private void AddApiCredential(HttpRequestMessage request, Uri apiOrigin)
        {
            string token;
            try { token = _credentials == null ? null : _credentials.GetToken(apiOrigin); }
            catch { throw new InvalidDataException("Credential provider failed."); }
            if (String.IsNullOrWhiteSpace(token)) return;
            if (token.Length > 4096 || token.IndexOf('\r') >= 0 || token.IndexOf('\n') >= 0)
                throw new InvalidDataException("Credential provider returned an invalid token.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        private static async Task<byte[]> ReadBoundedResponseAsync(HttpContent content, long maximumBytes,
            CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maximumBytes)
                throw new InvalidDataException("Release metadata exceeds the configured size limit.");
            using (Stream input = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (output.Length > maximumBytes - count) throw new InvalidDataException("Release metadata exceeds the configured size limit.");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
        }

        private void EnsureCatalogSourcePinned(BootstrapperCatalog candidate)
        {
            BootstrapperCatalog pinned = _embeddedCatalog ?? _catalog;
            if (candidate == null || pinned == null ||
                !String.Equals(candidate.Source.ApiUrl.TrimEnd('/'), pinned.Source.ApiUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(candidate.Source.ServerUrl.TrimEnd('/'), pinned.Source.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(candidate.Source.Repository, pinned.Source.Repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The online catalog source does not match the embedded trust anchor.");
        }

        private string CatalogArchivePath(string sha256)
        {
            if (!SafeFiles.IsHash(sha256)) throw new InvalidDataException("Catalog archive hash is invalid.");
            return SafeFiles.CombineContained(_options.CacheRoot, Path.Combine("updates", "catalogs", sha256.ToLowerInvariant() + ".json"), "Catalog archive");
        }

        private void SaveCatalogArchive(BootstrapperCatalog catalog)
        {
            try
            {
                string path = CatalogArchivePath(catalog.Sha256);
                SafeFiles.EnsureSafeDirectory(_options.CacheRoot, Path.GetDirectoryName(path), true);
                SafeFiles.WriteAtomic(path, new UTF8Encoding(false, true).GetBytes(catalog.Json));
            }
            catch { }
        }

        private string LastGoodCatalogPath
        {
            get { return SafeFiles.CombineContained(_options.CacheRoot, Path.Combine("updates", "last-good.catalog"), "Last-good catalog"); }
        }

        private void SaveLastGoodCatalog(BootstrapperCatalog catalog)
        {
            SafeFiles.EnsureSafeDirectory(_options.CacheRoot, Path.GetDirectoryName(LastGoodCatalogPath), true);
            byte[] json = new UTF8Encoding(false, true).GetBytes(catalog.Json);
            string envelope = catalog.Sha256 + "\n" + Convert.ToBase64String(json);
            SafeFiles.WriteAtomic(LastGoodCatalogPath, new UTF8Encoding(false).GetBytes(envelope));
        }

        private BootstrapperCatalog TryReadLastGoodCatalog()
        {
            try
            {
                string path = LastGoodCatalogPath;
                if (!File.Exists(path)) return null;
                SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.CacheRoot, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(path, false);
                byte[] envelopeBytes = File.ReadAllBytes(path);
                if (envelopeBytes.Length > _options.MaximumCatalogBytes * 2 + 128) return null;
                string envelope = new UTF8Encoding(false, true).GetString(envelopeBytes);
                int separator = envelope.IndexOf('\n');
                if (separator != 64) return null;
                string expectedHash = envelope.Substring(0, separator);
                if (!SafeFiles.IsHash(expectedHash)) return null;
                byte[] catalogBytes = Convert.FromBase64String(envelope.Substring(separator + 1));
                if (catalogBytes.Length > _options.MaximumCatalogBytes ||
                    !String.Equals(SafeFiles.Sha256(catalogBytes), expectedHash, StringComparison.OrdinalIgnoreCase)) return null;
                string json = new UTF8Encoding(false, true).GetString(catalogBytes);
                BootstrapperCatalog cached = CatalogParser.Parse(json, expectedHash, _options);
                EnsureCatalogSourcePinned(cached);
                cached.IsFromCache = true;
                return cached;
            }
            catch { return null; }
        }

        private static int CompareReleaseVersions(string left, string right)
        {
            string leftCore = left.Split('+')[0].Split('-')[0];
            string rightCore = right.Split('+')[0].Split('-')[0];
            Version leftVersion, rightVersion;
            if (!Version.TryParse(leftCore, out leftVersion) || !Version.TryParse(rightCore, out rightVersion))
                throw new InvalidDataException("Release version is not a comparable numeric version.");
            int core = leftVersion.CompareTo(rightVersion);
            if (core != 0) return core;
            bool leftPrerelease = left.IndexOf('-') >= 0;
            bool rightPrerelease = right.IndexOf('-') >= 0;
            if (leftPrerelease != rightPrerelease) return leftPrerelease ? -1 : 1;
            if (!leftPrerelease) return 0;
            return String.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool RegexSafeTag(string value)
        {
            return value != null && System.Text.RegularExpressions.Regex.IsMatch(value,
                "^v?[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$");
        }

        private static string JsonText(IDictionary<string, object> values, string key, int maxLength)
        {
            object value;
            string text = values.TryGetValue(key, out value) ? value as string : null;
            if (String.IsNullOrWhiteSpace(text) || text.Length > maxLength)
                throw new InvalidDataException("Release API returned invalid " + key + ".");
            return text;
        }

        private static string JsonOptionalText(IDictionary<string, object> values, string key, int maxLength)
        {
            object value;
            if (!values.TryGetValue(key, out value) || value == null) return String.Empty;
            string text = value as string;
            if (text == null || text.Length > maxLength) throw new InvalidDataException("Release API returned invalid " + key + ".");
            return text;
        }

        private static long JsonInteger(IDictionary<string, object> values, string key)
        {
            object value;
            JsonNumber number = values.TryGetValue(key, out value) ? value as JsonNumber : null;
            long parsed;
            if (number == null || !Int64.TryParse(number.Text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                throw new InvalidDataException("Release API returned invalid " + key + ".");
            return parsed;
        }

        private static List<object> JsonArray(IDictionary<string, object> values, string key)
        {
            object value;
            List<object> items = values.TryGetValue(key, out value) ? value as List<object> : null;
            if (items == null || items.Count > 1000) throw new InvalidDataException("Release API returned invalid " + key + ".");
            return items;
        }

        private static bool IsRedirect(HttpStatusCode status)
        {
            int value = (int)status;
            return value == 301 || value == 302 || value == 303 || value == 307 || value == 308;
        }

        private static void ValidateHttpsRedirect(Uri location)
        {
            if (location == null || location.Scheme != Uri.UriSchemeHttps ||
                !String.IsNullOrEmpty(location.UserInfo) || !String.IsNullOrEmpty(location.Fragment))
                throw new InvalidDataException("The catalog download redirect is not a trusted HTTPS URL.");
        }

        private static bool SameOrigin(Uri left, Uri right)
        {
            return String.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   String.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;
        }

        private BootstrapperCatalog GetCatalog()
        {
            if (_catalog == null) LoadCatalogAsync(CancellationToken.None).GetAwaiter().GetResult();
            return _catalog;
        }

        private static BootstrapperApp FindApp(BootstrapperCatalog catalog, string appId)
        {
            BootstrapperApp app = catalog.FindApp(appId);
            if (app == null) throw new ArgumentException("The requested app ID is not in the pinned catalog.", "appId");
            return app;
        }

        private void ValidateOptions()
        {
            if (String.IsNullOrWhiteSpace(_options.CacheRoot) || String.IsNullOrWhiteSpace(_options.BootstrapperExecutablePath))
                throw new ArgumentException("Cache and bootstrapper paths are required.");
            if (!SafeFiles.IsHash(_options.ExpectedCatalogSha256)) throw new ArgumentException("Expected catalog hash is invalid.");
            if (_options.MaximumCatalogBytes <= 0 || _options.MaximumPackageBytes <= 0 ||
                _options.MaximumExpandedBytes <= 0 || _options.MaximumArchiveEntries <= 0 || _options.MaximumPathLength < 32)
                throw new ArgumentException("Bootstrapper limits are invalid.");
            if (String.IsNullOrWhiteSpace(_options.StartMenuRoot)) throw new ArgumentException("Start menu root is required.");
        }

        public void Dispose() { }
    }
}
