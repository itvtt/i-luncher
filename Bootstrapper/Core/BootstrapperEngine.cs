using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private BootstrapperCatalog _catalog;

        public event EventHandler<BootstrapperProgressEventArgs> ProgressChanged;

        public BootstrapperEngine(BootstrapperOptions options, IBootstrapperCredentialProvider credentials)
            : this(options, credentials, delegate { return new BootstrapperHttpTransport(); },
                delegate { return new BootstrapperHttpTransport(); }) { }

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
            _catalog = CatalogParser.Parse(_options.EmbeddedCatalogJson, actualHash, _options);
            return Task.FromResult(_catalog);
        }

        public BootstrapperAppStatus GetStatus(string appId)
        {
            BootstrapperCatalog catalog = GetCatalog();
            BootstrapperApp app = FindApp(catalog, appId);
            string appDirectory = AppCachePath(app);
            string executable = SafeFiles.CombineContained(appDirectory, app.Executable, "App executable");
            string runtimeDirectory = RuntimeCachePath(catalog.Runtime);
            bool ready = IsValidAppCache(app, appDirectory, executable) && IsValidRuntimeCache(catalog.Runtime, runtimeDirectory);
            string receipt = ReceiptPath(app);
            string ownedShortcut;
            bool installed = TryGetValidInstallReceipt(app, receipt, out ownedShortcut);
            return new BootstrapperAppStatus(app, ready, installed, ready ? executable : null, null);
        }

        public async Task<BootstrapperReadyApp> EnsureReadyAsync(string appId, CancellationToken cancellationToken)
        {
            BootstrapperCatalog catalog = GetCatalog();
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

        public async Task<BootstrapperLaunchResult> LaunchAsync(string appId, CancellationToken cancellationToken)
        {
            BootstrapperReadyApp ready = await EnsureReadyAsync(appId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            int processId;
            lock (ProcessEnvironmentGate)
            {
                string oldRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
                string oldRootX64 = Environment.GetEnvironmentVariable("DOTNET_ROOT_X64");
                try
                {
                    Environment.SetEnvironmentVariable("DOTNET_ROOT", ready.RuntimeDirectory, EnvironmentVariableTarget.Process);
                    Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", ready.RuntimeDirectory, EnvironmentVariableTarget.Process);
                    using (Process process = Process.Start(new ProcessStartInfo
                    {
                        FileName = ready.ExecutablePath,
                        WorkingDirectory = ready.WorkingDirectory,
                        UseShellExecute = true
                    }))
                    {
                        if (process == null) throw new InvalidOperationException("Windows did not start the app process.");
                        processId = process.Id;
                    }
                }
                finally
                {
                    Environment.SetEnvironmentVariable("DOTNET_ROOT", oldRoot, EnvironmentVariableTarget.Process);
                    Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", oldRootX64, EnvironmentVariableTarget.Process);
                }
            }
            return new BootstrapperLaunchResult(processId, ready.ExecutablePath);
        }

        public async Task<BootstrapperInstallResult> InstallAsync(string appId, CancellationToken cancellationToken)
        {
            BootstrapperCatalog catalog = GetCatalog();
            BootstrapperApp app = FindApp(catalog, appId);
            await EnsureReadyAsync(app.Id, cancellationToken).ConfigureAwait(false);
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
                if (TryGetValidInstallReceipt(app, receipt, out previousOwnedShortcut))
                    shortcut = previousOwnedShortcut;
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
                    "executable=" + app.Executable + "\r\n" + "launcher=" + stableBootstrapper + "\r\n");
                try
                {
                    CreateShortcut(shortcut, stableBootstrapper, app.Id, iconPath);
                    SafeFiles.WriteAtomic(receipt, receiptBytes);
                }
                catch
                {
                    RestoreFile(shortcut, oldShortcut);
                    RestoreFile(receipt, oldReceipt);
                    throw;
                }
                return new BootstrapperInstallResult(shortcut, receipt, stableBootstrapper);
            }
            finally { gate.Release(); }
        }

        public async Task<BootstrapperUninstallResult> UninstallAsync(string appId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BootstrapperApp app = FindApp(GetCatalog(), appId);
            SemaphoreSlim gate = InstallLocks.GetOrAdd("install:" + app.Id, delegate { return new SemaphoreSlim(1, 1); });
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string receipt = ReceiptPath(app);
                bool hasReceipt = File.Exists(receipt);
                string shortcut;
                if (!hasReceipt)
                {
                    if (FindOwnedShortcuts(app.Id).Length != 0)
                        throw new InvalidDataException("The app shortcut exists without a valid install receipt; it was preserved.");
                    return new BootstrapperUninstallResult(app.Id, false);
                }
                if (!TryGetValidInstallReceipt(app, receipt, out shortcut))
                    throw new InvalidDataException("The app installation record or shortcut failed validation; it was preserved.");

                SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.CacheRoot, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(_options.StartMenuRoot, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(receipt, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(shortcut, false);
                string appIcon = SafeFiles.CombineContained(Path.GetDirectoryName(receipt), "app-icon.ico", "Installed shortcut icon");
                SafeFiles.EnsureExistingPathHasNoReparsePoints(appIcon, false);
                byte[] oldReceipt = File.ReadAllBytes(receipt);
                byte[] oldShortcut = File.ReadAllBytes(shortcut);
                byte[] oldIcon = File.Exists(appIcon) ? File.ReadAllBytes(appIcon) : null;
                if (oldIcon != null && !IsIconFile(oldIcon))
                    throw new InvalidDataException("The installed shortcut icon failed validation; it was preserved.");
                try
                {
                    File.Delete(shortcut);
                    if (oldIcon != null) File.Delete(appIcon);
                    File.Delete(receipt);
                }
                catch
                {
                    RestoreFile(shortcut, oldShortcut);
                    RestoreFile(appIcon, oldIcon);
                    RestoreFile(receipt, oldReceipt);
                    throw;
                }
                return new BootstrapperUninstallResult(app.Id, true);
            }
            finally { gate.Release(); }
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
            ownedShortcut = null;
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
                if (fields.Count != 6 || !fields.ContainsKey("appId") || !fields.ContainsKey("catalogSha256") ||
                    !fields.ContainsKey("packageSha256") || !fields.ContainsKey("runtimeSha256") ||
                    !fields.ContainsKey("executable") || !fields.ContainsKey("launcher")) return false;
                if (!String.Equals(fields["appId"], app.Id, StringComparison.Ordinal) ||
                    !SafeFiles.IsHash(fields["catalogSha256"]) || !SafeFiles.IsHash(fields["packageSha256"]) ||
                    !SafeFiles.IsHash(fields["runtimeSha256"])) return false;

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
                if (shortcuts.Length != 1) return false;
                string shortcutPath = shortcuts[0];
                SafeFiles.EnsureExistingPathHasNoReparsePoints(shortcutPath, false);

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
                    if (!String.Equals(Path.GetFullPath(target), Path.GetFullPath(stableBootstrapper), StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(arguments, "--app " + QuoteArgument(app.Id) + " --noUI", StringComparison.Ordinal)) return false;
                    ownedShortcut = shortcutPath;
                    return true;
                }
                finally
                {
                    if (link != null && System.Runtime.InteropServices.Marshal.IsComObject(link))
                        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                    if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
                        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                }
            }
            catch { return false; }
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

        private void Progress(string appId, long transferred, long total)
        {
            EventHandler<BootstrapperProgressEventArgs> handler = ProgressChanged;
            if (handler != null) handler(this, new BootstrapperProgressEventArgs(appId, "download", transferred, total));
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
