using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ILauncher.Bootstrapper;
using ILauncher.Models;

namespace ILauncher.Services;

public sealed record CatalogResult(IReadOnlyList<LauncherEntry> Entries, IReadOnlyList<string> Errors);

public sealed class LauncherCatalog : ILauncherCatalog, ILauncherUpdateCatalog
{
    public string RootDirectory { get; }
    public bool IsDistribution { get; }
    private readonly string _appsDirectory;
    private readonly string _sharedPayloadDirectory;
    private readonly AppInstaller _installer;
    private readonly AppBuilder _builder;
    private readonly IIntegratedAppProvider? _integratedApps;
    private readonly BootstrapperEngine? _onlineEngine;
    private readonly LauncherUpdateSource? _onlineSource;
    private readonly string? _onlineSetupError;
    private readonly Dictionary<string, LauncherEntry> _loadedEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _launchLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public LauncherCatalog() : this(FindRoot(AppContext.BaseDirectory)) { }

    public LauncherCatalog(IIntegratedAppProvider integratedApps) : this(FindRoot(AppContext.BaseDirectory),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-luncher", "Apps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "i-luncher"), integratedApps) { }

    public LauncherCatalog(string rootDirectory) : this(rootDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-luncher", "Apps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "i-luncher")) { }

    public LauncherCatalog(string rootDirectory, string perUserAppsBase, string startMenuDirectory)
        : this(rootDirectory, perUserAppsBase, startMenuDirectory, null) { }

    public LauncherCatalog(string rootDirectory, string perUserAppsBase, string startMenuDirectory,
        IIntegratedAppProvider? integratedApps)
    {
        RootDirectory = AppBuilder.ResolveCatalogRoot(Path.GetFullPath(rootDirectory));
        string rootManifest = Path.Combine(RootDirectory, "i-luncher.json");
        if (File.Exists(rootManifest))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(rootManifest));
            IsDistribution = document.RootElement.TryGetProperty("distribution", out var distribution)
                && distribution.ValueKind == JsonValueKind.Object;
        }
        _appsDirectory = Path.GetFullPath(perUserAppsBase);
        _sharedPayloadDirectory = Path.Combine(_appsDirectory, "_shared-payloads");
        string updateManifest = Path.Combine(RootDirectory, "i-luncher-updates.json");
        if (IsDistribution && File.Exists(updateManifest))
        {
            try
            {
                var updateSource = ReadUpdateSource(updateManifest, RootDirectory);
                _onlineSource = updateSource.Source;
                var credentials = BootstrapperCredentialProvider.Load(updateSource.Source.ApiUrl);
                string cacheRoot = Path.GetDirectoryName(_appsDirectory)
                    ?? throw new InvalidDataException("The per-user app cache has no parent directory.");
                var options = new BootstrapperOptions(updateSource.CatalogJson, updateSource.CatalogSha256,
                    cacheRoot, updateSource.StarterPath)
                {
                    StartMenuRoot = Path.GetFullPath(startMenuDirectory)
                };
                _onlineEngine = new BootstrapperEngine(options, credentials);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or InvalidDataException or JsonException or ArgumentException
                                               or FormatException or CryptographicException)
            {
                _onlineSetupError = $"Online app updates are unavailable: {exception.Message}";
            }
        }
        _installer = new AppInstaller(_appsDirectory, startMenuDirectory);
        _builder = new AppBuilder(RootDirectory);
        _integratedApps = integratedApps;
    }

    private static string FindRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "i-luncher.json"))) return directory.FullName;
        return start;
    }

    private LauncherEntry ConfigureIntegratedInstall(LauncherEntry entry)
    {
        ValidateInstallId(entry.Id);
        string executableName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(executableName)
            || string.Equals(Path.GetFileNameWithoutExtension(executableName), "dotnet", StringComparison.OrdinalIgnoreCase))
            executableName = "i-luncher.exe";
        string installSource = AppContext.BaseDirectory;
        return new LauncherEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            Description = entry.Description,
            Category = entry.Category,
            IsDistribution = IsDistribution,
            IsIntegrated = true,
            BundledVersion = entry.BundledVersion,
            ExecutablePath = Path.Combine(installSource, executableName),
            WorkingDirectory = installSource,
            IconPath = entry.IconPath,
            InstallDefinition = new LauncherInstallDefinition(LauncherInstallType.SharedPayload, null, []),
            InstalledAppDirectory = Path.Combine(_appsDirectory, entry.Id),
            InstalledSharedPayloadDirectory = _sharedPayloadDirectory,
            InstalledExecutableRelativePath = executableName,
            InstalledWorkingDirectoryRelativePath = ".",
            InstallSourceDirectory = installSource,
            InstallIconSourceDirectory = installSource,
            InstallShortcutArguments = ["--app", entry.Id]
        };
    }

    private static void ValidateInstallId(string id)
    {
        if (id.Length is 0 or > 80 || !char.IsAsciiLetterOrDigit(id[0])
            || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            || IsReservedDeviceName(id))
            throw new FormatException("App ids used for installation must contain only letters, digits, hyphens, and underscores.");
    }

    public CatalogResult Load()
    {
        var entries = new List<(int Order, LauncherEntry Entry)>();
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var integratedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_integratedApps is not null)
        {
            int order = 0;
            foreach (LauncherEntry entry in _integratedApps.Entries)
            {
                if (!entry.IsIntegrated)
                {
                    errors.Add($"통합 앱 공급자가 일반 앱을 반환했습니다: {entry.Id}");
                    continue;
                }
                if (!ids.Add(entry.Id))
                {
                    errors.Add($"중복된 통합 앱 id입니다: {entry.Id}");
                    continue;
                }
                integratedIds.Add(entry.Id);
                entries.Add((order++, ConfigureIntegratedInstall(entry)));
            }
        }
        try
        {
            IReadOnlyList<string> appDirectories = SourceProjectDiscovery.GetAppDirectories(RootDirectory);
            foreach (string directory in appDirectories)
            {
                string manifest = Path.Combine(directory, "launcher.app.json");
                if (!File.Exists(manifest)) continue;
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
                    });
                    var json = document.RootElement;
                    string id = RequiredString(json, "id");
                    if (integratedIds.Contains(id))
                        continue;
                    string name = RequiredString(json, "name");
                    string executable = ResolveExecutable(directory, RequiredString(json, "executable"));
                    string icon = OptionalString(json, "icon");
                    string working = OptionalString(json, "workingDirectory");
                    string workingDirectory = string.IsNullOrWhiteSpace(working)
                        ? Path.GetDirectoryName(executable)! : ResolvePath(directory, working);
                    int order = json.TryGetProperty("order", out var orderValue) ? orderValue.GetInt32() : 100;
                    LauncherBuildDefinition? build = null;
                    if (!IsDistribution && json.TryGetProperty("build", out var buildConfiguration))
                    {
                        string project = ResolveBuildProject(directory, RequiredString(buildConfiguration, "project"));
                        string bootstrap = OptionalString(buildConfiguration, "bootstrapProject");
                        build = new LauncherBuildDefinition(project,
                            string.IsNullOrWhiteSpace(bootstrap) ? null : ResolveBuildProject(directory, bootstrap));
                        AppInstaller.EnsureContained(directory, executable, "빌드 실행 파일");
                        AppInstaller.EnsureContained(directory, workingDirectory, "빌드 작업 폴더");
                    }
                    LauncherInstallDefinition? install = null;
                    string? installedRoot = null;
                    string? relativeExecutable = null;
                    string? relativeWorking = null;
                    string? installSourceDirectory = null;
                    string? installedSharedDirectory = null;
                    IReadOnlyList<string> installShortcutArguments = [];
                    bool hasInstallConfiguration = json.TryGetProperty("install", out var installation);
                    string configuredInstallType = hasInstallConfiguration
                        ? RequiredString(installation, "type") : "";
                    if (IsDistribution)
                    {
                        ValidateInstallId(id);
                        AppInstaller.EnsureContained(RootDirectory, executable, "distribution executable");
                        AppInstaller.EnsureContained(RootDirectory, workingDirectory, "distribution working directory");
                        install = new LauncherInstallDefinition(LauncherInstallType.SharedPayload, null, []);
                        installedRoot = Path.Combine(_appsDirectory, id);
                        relativeExecutable = Path.GetRelativePath(RootDirectory, executable);
                        relativeWorking = Path.GetRelativePath(Path.GetDirectoryName(executable)!, workingDirectory);
                        installSourceDirectory = RootDirectory;
                        installedSharedDirectory = _sharedPayloadDirectory;
                        installShortcutArguments = ReadArguments(json);
                    }
                    else if (hasInstallConfiguration
                        && string.Equals(configuredInstallType, "command", StringComparison.Ordinal))
                    {
                        install = new LauncherInstallDefinition(LauncherInstallType.Command,
                            ResolveInstallerExecutable(directory, RequiredString(installation, "executable")), ReadArguments(installation));
                    }
                    else if (hasInstallConfiguration
                        && !string.Equals(configuredInstallType, "portable", StringComparison.Ordinal))
                    {
                        throw new FormatException($"Unsupported install.type for {id}: {configuredInstallType}");
                    }
                    else if (build is not null || (hasInstallConfiguration && File.Exists(executable)))
                    {
                        string sourceDirectory = Path.GetDirectoryName(executable)!;
                        bool sourceDirectoryIsContained = IsPathContained(directory, sourceDirectory);
                        bool workingDirectoryIsContained = IsPathContained(sourceDirectory, workingDirectory);
                        if (hasInstallConfiguration)
                        {
                            ValidateInstallId(id);
                            AppInstaller.EnsureContained(directory, sourceDirectory, "portable publish directory");
                            AppInstaller.EnsureContained(sourceDirectory, workingDirectory, "portable install working directory");
                        }
                        if (sourceDirectoryIsContained && workingDirectoryIsContained)
                        {
                            ValidateInstallId(id);
                            relativeWorking = Path.GetRelativePath(sourceDirectory, workingDirectory);
                            installedRoot = Path.Combine(_appsDirectory, id);
                            relativeExecutable = Path.GetFileName(executable);
                            install = new LauncherInstallDefinition(LauncherInstallType.Portable, null, []);
                        }
                    }
                    var entry = new LauncherEntry
                    {
                        Id = id, Name = name, Description = OptionalString(json, "description"),
                        IsDistribution = IsDistribution,
                        Category = OptionalString(json, "category", "앱"), ExecutablePath = executable,
                        IconPath = string.IsNullOrWhiteSpace(icon) ? "" : ResolvePath(directory, icon),
                        WorkingDirectory = workingDirectory, Arguments = ReadArguments(json),
                        InstallDefinition = install, BuildDefinition = build, InstalledAppDirectory = installedRoot,
                        InstallSourceDirectory = installSourceDirectory,
                        InstallIconSourceDirectory = directory,
                        InstalledSharedPayloadDirectory = installedSharedDirectory,
                        InstallShortcutArguments = installShortcutArguments,
                        InstalledExecutableRelativePath = relativeExecutable,
                        InstalledWorkingDirectoryRelativePath = relativeWorking,
                        BundledVersion = LauncherEntry.ReadBundledVersion(executable)
                    };
                    if (!ids.Add(id)) throw new FormatException($"중복된 앱 id입니다: {id}");
                    entries.Add((order, entry));
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException
                    or ArgumentException or InvalidOperationException or FormatException or OverflowException)
                {
                    errors.Add($"{Path.GetFileName(directory)}: {ex.Message}");
                }
            }

            if (!IsDistribution)
            {
                SourceProjectDiscoveryResult discovered = SourceProjectDiscovery.Discover(RootDirectory, appDirectories);
                errors.AddRange(discovered.Errors);
                foreach (DiscoveredSourceProject project in discovered.Projects)
                {
                    if (!ids.Add(project.Id))
                    {
                        errors.Add($"{Path.GetRelativePath(RootDirectory, project.AppDirectory)}: duplicate app id '{project.Id}'; add launcher.app.json with a unique id.");
                        continue;
                    }
                    string relativeOutput = Path.Combine("dist", "apps", project.Id, project.AssemblyName + ".exe");
                    string executable = Path.GetFullPath(relativeOutput, RootDirectory);
                    string workingDirectory = Path.GetDirectoryName(executable)!;
                    ValidateInstallId(project.Id);
                    var entry = new LauncherEntry
                    {
                        Id = project.Id,
                        Name = project.Name,
                        Description = "",
                        Category = "Apps",
                        IsDistribution = false,
                        ExecutablePath = executable,
                        BundledVersion = LauncherEntry.ReadBundledVersion(executable),
                        WorkingDirectory = workingDirectory,
                        BuildDefinition = new LauncherBuildDefinition(project.ProjectPath, null),
                        IsAutoDiscoveredSourceProject = true,
                        SourceProjectDirectory = project.AppDirectory,
                        InstallDefinition = new LauncherInstallDefinition(LauncherInstallType.Portable, null, []),
                        InstalledAppDirectory = Path.Combine(_appsDirectory, project.Id),
                        InstalledExecutableRelativePath = Path.GetFileName(executable),
                        InstalledWorkingDirectoryRelativePath = "."
                    };
                    entries.Add((100, entry));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"앱 폴더를 읽을 수 없습니다: {ex.Message}");
        }
        var orderedEntries = entries.OrderBy(item => item.Order).ThenBy(item => item.Entry.Name)
            .Select(item => item.Entry).ToArray();
        var allEntries = orderedEntries.ToList();
        if (_onlineSetupError is not null) errors.Add(_onlineSetupError);
        ApplyOnlineStatuses(allEntries, errors, null);
        _loadedEntries.Clear();
        foreach (var entry in allEntries) _loadedEntries[entry.Id] = entry;
        return new CatalogResult(allEntries, errors);
    }

    public async Task<LauncherUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (_onlineEngine is null || _onlineSource is null)
            throw new InvalidOperationException("This launcher distribution has no trusted online update source.");
        BootstrapperCatalog catalog = await _onlineEngine.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var errors = new List<string>();
        ApplyOnlineStatuses(_loadedEntries.Values.ToList(), errors, catalog);
        var apps = catalog.Apps.Select(app => new LauncherAppUpdate(app.Id, app.Version, app.VersionSource, app.Asset,
            app.Sha256, app.Bytes, app.Executable, app.Description, app.Category, app.IconBase64)).ToArray();
        return new LauncherUpdateCheckResult(catalog.Version, catalog.Source.TagName, apps, catalog.IsFromCache);
    }

    public async Task<string> UpdateAsync(LauncherEntry entry, CancellationToken cancellationToken = default)
    {
        if (_onlineEngine is null || !entry.OnlineManaged)
            throw new InvalidOperationException($"{entry.Name} is not managed by the online update catalog.");
        if (!entry.UpdateAvailable)
            throw new InvalidOperationException($"{entry.Name} has no newer package available.");
        BootstrapperInstallResult result = await _onlineEngine.UpdateAsync(entry.Id, cancellationToken)
            .ConfigureAwait(false);
        ApplyOnlineStatuses(_loadedEntries.Values.ToList(), [], null);
        return $"{entry.Name} updated. Shortcut: {result.ShortcutPath}";
    }

    private void ApplyOnlineStatuses(List<LauncherEntry> entries, List<string> errors,
        BootstrapperCatalog? catalog)
    {
        if (_onlineEngine is null) return;
        try
        {
            catalog ??= _onlineEngine.LoadCatalogAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (_onlineSource is not null &&
                (!SameConfiguredSource(catalog.Source.ServerUrl, _onlineSource.ServerUrl)
                 || !SameConfiguredSource(catalog.Source.ApiUrl, _onlineSource.ApiUrl)
                 || !string.Equals(catalog.Source.Repository, _onlineSource.Repository, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The update catalog source does not match the trusted distribution source.");
            var remoteApps = catalog.Apps.ToDictionary(app => app.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!remoteApps.TryGetValue(entry.Id, out var app)) continue;
                entry.OnlineManaged = true;
                entry.AvailableVersion = app.Version;
                var status = _onlineEngine.GetStatus(entry.Id);
                entry.UpdateAvailable = status.UpdateAvailable;
                entry.OnlineInstalled = status.IsInstalled;
                entry.OnlineInstalledVersion = status.InstalledVersion;
                entry.OnlineExecutablePath = status.IsInstalled ? status.ExecutablePath : null;
            }

            foreach (var app in catalog.Apps)
            {
                if (entries.Any(entry => string.Equals(entry.Id, app.Id, StringComparison.OrdinalIgnoreCase))) continue;
                string placeholderExecutable = Path.GetFullPath(app.Executable, RootDirectory);
                var entry = new LauncherEntry
                {
                    Id = app.Id, Name = app.Name, Description = app.Description, Category = app.Category,
                    IsDistribution = true, ExecutablePath = placeholderExecutable,
                    WorkingDirectory = Path.GetDirectoryName(placeholderExecutable)!,
                    OnlineManaged = true, AvailableVersion = app.Version
                };
                var status = _onlineEngine.GetStatus(app.Id);
                entry.UpdateAvailable = status.UpdateAvailable;
                entry.OnlineInstalled = status.IsInstalled;
                entry.OnlineInstalledVersion = status.InstalledVersion;
                entry.OnlineExecutablePath = status.IsInstalled ? status.ExecutablePath : null;
                entries.Add(entry);
                _loadedEntries[entry.Id] = entry;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                   or InvalidDataException or FormatException or ArgumentException)
        {
            errors.Add($"Online app status could not be loaded: {ex.Message}");
        }
    }

    private static bool SameConfiguredSource(string actual, string expected)
    {
        var left = new Uri(actual, UriKind.Absolute);
        var right = new Uri(expected, UriKind.Absolute);
        return left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
            && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port
            && left.AbsolutePath.TrimEnd('/').Equals(right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    private static OnlineUpdateManifest ReadUpdateSource(string path, string distributionRoot)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("The online update sidecar is outside the allowed size range.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1
            || !root.TryGetProperty("catalog", out var catalog)
            || !root.TryGetProperty("source", out var source))
            throw new InvalidDataException("The online update sidecar has an unsupported schema.");
        string catalogJson = catalog.GetProperty("json").GetString()
            ?? throw new InvalidDataException("The sidecar catalog JSON is missing.");
        string catalogSha256 = catalog.GetProperty("sha256").GetString() ?? string.Empty;
        byte[] catalogBytes = new UTF8Encoding(false, true).GetBytes(catalogJson);
        if (catalogBytes.Length is <= 0 or > 2 * 1024 * 1024 || catalogSha256.Length != 64
            || catalogSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The sidecar catalog is outside its allowed size or hash format.");
        string actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(catalogBytes)).ToLowerInvariant();
        if (!actualHash.Equals(catalogSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The sidecar catalog failed its SHA-256 integrity check.");
        string serverUrl = source.GetProperty("serverUrl").GetString() ?? string.Empty;
        string apiUrl = source.GetProperty("apiUrl").GetString() ?? string.Empty;
        string repository = source.GetProperty("repository").GetString() ?? string.Empty;
        string starterExecutable = root.GetProperty("starterExecutable").GetString() ?? string.Empty;
        string starterSha256 = root.GetProperty("starterSha256").GetString() ?? string.Empty;
        ValidateUpdateSourceUri(serverUrl, "serverUrl");
        ValidateUpdateSourceUri(apiUrl, "apiUrl");
        if (!System.Text.RegularExpressions.Regex.IsMatch(repository,
                "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("The update source repository is invalid.");
        using (var catalogDocument = JsonDocument.Parse(catalogJson, new JsonDocumentOptions { MaxDepth = 16 }))
        {
            var catalogSource = catalogDocument.RootElement.GetProperty("source");
            string catalogServer = catalogSource.GetProperty("serverUrl").GetString() ?? string.Empty;
            string catalogApi = catalogSource.GetProperty("apiUrl").GetString() ?? string.Empty;
            string catalogRepository = catalogSource.GetProperty("repository").GetString() ?? string.Empty;
            ValidateUpdateSourceUri(catalogServer, "catalog.serverUrl");
            ValidateUpdateSourceUri(catalogApi, "catalog.apiUrl");
            if (!SameConfiguredSource(catalogServer, serverUrl) || !SameConfiguredSource(catalogApi, apiUrl)
                || !string.Equals(catalogRepository, repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The embedded catalog source does not match the distribution trust source.");
        }
        if (starterSha256.Length != 64 || starterSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The online starter hash is invalid.");
        if (Path.IsPathRooted(starterExecutable))
            throw new InvalidDataException("The online starter path must be relative to the distribution root.");
        string starterPath = Path.GetFullPath(starterExecutable, distributionRoot);
        string relativeStarter = Path.GetRelativePath(distributionRoot, starterPath);
        if (relativeStarter == ".." || relativeStarter.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relativeStarter))
            throw new InvalidDataException("The online starter path escapes the distribution root.");
        var starterInfo = new FileInfo(starterPath);
        if (!starterInfo.Exists || starterInfo.Length is <= 0 or > 64 * 1024 * 1024 || HasReparsePoint(distributionRoot, starterPath))
            throw new InvalidDataException("The online starter is missing, oversized, or redirected through a reparse point.");
        string actualStarterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(starterPath))).ToLowerInvariant();
        if (!actualStarterHash.Equals(starterSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The online starter failed its SHA-256 integrity check.");
        return new OnlineUpdateManifest(catalogJson, catalogSha256,
            new LauncherUpdateSource(serverUrl.TrimEnd('/'), apiUrl.TrimEnd('/'), repository), starterPath);
    }

    private static bool HasReparsePoint(string root, string path)
    {
        string canonicalRoot = Path.GetFullPath(root);
        string canonicalPath = Path.GetFullPath(path);
        for (DirectoryInfo? directory = new DirectoryInfo(canonicalRoot); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        string current = canonicalRoot;
        foreach (string segment in Path.GetRelativePath(canonicalRoot, canonicalPath)
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or ".") continue;
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) return false;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static void ValidateUpdateSourceUri(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException($"The update source {name} must be a trusted HTTPS base URI.");
    }

    private sealed record OnlineUpdateManifest(string CatalogJson, string CatalogSha256,
        LauncherUpdateSource Source, string StarterPath);

    public async Task<string> InstallAsync(LauncherEntry entry)
    {
        if (entry.OnlineManaged && _onlineEngine is not null)
        {
            BootstrapperInstallResult onlineInstall = await _onlineEngine.InstallAsync(entry.Id, CancellationToken.None)
                .ConfigureAwait(false);
            ApplyOnlineStatuses(_loadedEntries.Values.ToList(), [], null);
            return $"{entry.Name} installed. Shortcut: {onlineInstall.ShortcutPath}";
        }
        var installLock = _launchLocks.GetOrAdd(entry.Id, static _ => new SemaphoreSlim(1, 1));
        await installLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.InstallDefinition?.Type is LauncherInstallType.Command or LauncherInstallType.SharedPayload)
                return await _installer.InstallAsync(entry).ConfigureAwait(false);

            AppBuildOutput? output = null;
            if (entry.BuildDefinition is not null)
            {
                output = await Task.Run(() => _builder.TryGetExistingOutput(entry)).ConfigureAwait(false);
                if (output is null && entry.CanBuild)
                    output = await _builder.BuildAsync(entry).ConfigureAwait(false);
                if (output is null && entry.TryGetInstalledExecutablePath() is not null)
                    return _installer.RefreshInstalledShortcut(entry);
                if (output is null)
                    throw new FileNotFoundException($"No publish output is available for {entry.Name}.", entry.ExecutablePath);
            }

            return await _installer.InstallAsync(entry, output?.ExecutablePath ?? entry.ExecutablePath).ConfigureAwait(false);
        }
        finally
        {
            installLock.Release();
        }
    }

    public Task<string> UninstallAsync(LauncherEntry entry) => UninstallAsync(entry, false);

    public async Task<string> UninstallAsync(LauncherEntry entry, bool deleteFiles)
    {
        if (entry.OnlineManaged && _onlineEngine is not null)
        {
            try
            {
                BootstrapperUninstallResult result = await _onlineEngine.UninstallAsync(entry.Id, deleteFiles, CancellationToken.None)
                    .ConfigureAwait(false);
                return result.Removed ? $"{entry.Name} removed." : $"{entry.Name} is already removed.";
            }
            finally { ApplyOnlineStatuses(_loadedEntries.Values.ToList(), [], null); }
        }
        var installLock = _launchLocks.GetOrAdd(entry.Id, static _ => new SemaphoreSlim(1, 1));
        await installLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _installer.UninstallAsync(entry, deleteFiles).ConfigureAwait(false);
        }
        finally
        {
            installLock.Release();
        }
    }

    public void Launch(LauncherEntry entry) => LaunchAsync(entry).GetAwaiter().GetResult();

    public async Task LaunchAsync(LauncherEntry entry)
    {
        if (entry.OnlineManaged && _onlineEngine is not null)
        {
            if (!_onlineEngine.GetStatus(entry.Id).IsInstalled)
                throw new InvalidOperationException("설치 후 실행할 수 있습니다.");
            await _onlineEngine.LaunchAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        var launchLock = _launchLocks.GetOrAdd(entry.Id, static _ => new SemaphoreSlim(1, 1));
        await launchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.TryGetInstalledExecutablePath() is null)
                throw new InvalidOperationException("설치 후 실행할 수 있습니다.");

            if (entry.IsIntegrated)
            {
                if (_integratedApps is null || !_integratedApps.Entries.Any(item =>
                        string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"통합 앱 실행 공급자를 찾을 수 없습니다: {entry.Id}");
                await LaunchWithStoreLockAsync(entry, () => _integratedApps.LaunchAsync(entry.Id)).ConfigureAwait(false);
                return;
            }

            AppBuildOutput? cachedSourceOutput = entry.IsAutoDiscoveredSourceProject
                ? await Task.Run(() => _builder.TryGetExistingOutput(entry)).ConfigureAwait(false)
                : null;
            if (cachedSourceOutput is not null)
            {
                await StartWithStoreLockAsync(entry, cachedSourceOutput.ExecutablePath, cachedSourceOutput.WorkingDirectory, false).ConfigureAwait(false);
                return;
            }

            if (TryResolveLaunch(entry, out var executable, out var workingDirectory, out var isInstalled,
                    skipAutoSourceOutput: entry.IsAutoDiscoveredSourceProject))
            {
                await StartWithStoreLockAsync(entry, executable, workingDirectory, isInstalled).ConfigureAwait(false);
                return;
            }

            if (!entry.CanBuild)
                throw new FileNotFoundException(entry.IsDistribution
                    ? $"앱 파일이 없습니다. 배포 압축 파일을 다시 풀거나 제품을 다시 설치하세요.\n{entry.ExecutablePath}"
                    : $"실행 파일이 없습니다. 앱을 빌드하거나 실행 파일 경로를 확인하세요.\n{entry.ExecutablePath}");

            AppBuildOutput output = await _builder.BuildAsync(entry).ConfigureAwait(false);
            executable = output.ExecutablePath;
            workingDirectory = output.WorkingDirectory;
            isInstalled = false;
            await StartWithStoreLockAsync(entry, executable, workingDirectory, isInstalled).ConfigureAwait(false);
        }
        finally
        {
            launchLock.Release();
        }
    }

    private async Task LaunchWithStoreLockAsync(LauncherEntry entry, Func<Task> handoff)
    {
        await ManagedPayloadRemoval.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using (await ManagedPayloadRemoval.AcquireStoreLockAsync(Path.GetDirectoryName(_appsDirectory)!, CancellationToken.None).ConfigureAwait(false))
            {
                if (entry.TryGetInstalledExecutablePath() is null)
                    throw new InvalidOperationException("The app was removed. Install it before launching.");
                await handoff().ConfigureAwait(false);
            }
        }
        finally { ManagedPayloadRemoval.Gate.Release(); }
    }

    private Task StartWithStoreLockAsync(LauncherEntry entry, string executable, string workingDirectory, bool isInstalled) =>
        LaunchWithStoreLockAsync(entry, () =>
        {
            if (isInstalled && !Path.GetFullPath(executable).Equals(
                    Path.GetFullPath(entry.TryGetInstalledExecutablePath()!), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The installed version changed. Retry launching the app.");
            Start(entry, executable, workingDirectory, isInstalled);
            return Task.CompletedTask;
        });

    private bool TryResolveLaunch(LauncherEntry entry, out string executable, out string workingDirectory,
        out bool isInstalled, bool skipAutoSourceOutput = false)
    {
        string? installed = entry.TryGetInstalledExecutablePath();
        if (installed is not null)
        {
            executable = installed;
            workingDirectory = Path.GetFullPath(entry.InstalledWorkingDirectoryRelativePath ?? ".",
                Path.GetDirectoryName(installed)!);
            isInstalled = true;
            return true;
        }

        if (entry.IsAutoDiscoveredSourceProject && !skipAutoSourceOutput)
        {
            AppBuildOutput? sourceOutput = _builder.TryGetExistingOutput(entry);
            if (sourceOutput is not null)
            {
                executable = sourceOutput.ExecutablePath;
                workingDirectory = sourceOutput.WorkingDirectory;
                isInstalled = false;
                return true;
            }
        }

        if (!entry.IsAutoDiscoveredSourceProject && File.Exists(entry.ExecutablePath))
        {
            executable = entry.ExecutablePath;
            workingDirectory = entry.WorkingDirectory;
            isInstalled = false;
            return true;
        }

        AppBuildOutput? externalOutput = entry.CanBuild && !entry.IsAutoDiscoveredSourceProject
            ? _builder.TryGetExistingOutput(entry) : null;
        if (externalOutput is not null)
        {
            executable = externalOutput.ExecutablePath;
            workingDirectory = externalOutput.WorkingDirectory;
            isInstalled = false;
            return true;
        }

        executable = entry.ExecutablePath;
        workingDirectory = entry.WorkingDirectory;
        isInstalled = false;
        return false;
    }

    private static void Start(LauncherEntry entry, string executable, string workingDirectory, bool isInstalled)
    {
        if (!File.Exists(executable))
            throw new FileNotFoundException($"실행 파일이 없습니다. 앱을 빌드하거나 실행 파일 경로를 확인하세요.\n{executable}");
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"작업 폴더가 없습니다: {workingDirectory}");
        if (isInstalled)
        {
            string installedPayloadRoot = entry.InstallDefinition?.Type == LauncherInstallType.SharedPayload
                ? entry.InstalledSharedPayloadDirectory! : entry.InstalledAppDirectory!;
            AppInstaller.EnsureNoReparsePoints(installedPayloadRoot, executable);
            AppInstaller.EnsureNoReparsePoints(installedPayloadRoot, workingDirectory);
        }
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = true };
        foreach (var argument in entry.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) throw new InvalidOperationException("앱 프로세스를 시작하지 못했습니다.");
    }

    private static IReadOnlyList<string> ReadArguments(JsonElement json)
    {
        if (!json.TryGetProperty("arguments", out var array)) return [];
        var arguments = new List<string>();
        foreach (var argument in array.EnumerateArray())
            arguments.Add(argument.GetString() ?? throw new FormatException("arguments에는 문자열만 넣을 수 있습니다."));
        return arguments;
    }

    private static bool IsReservedDeviceName(string id) =>
        new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(id, StringComparer.OrdinalIgnoreCase);

    private static string ResolveExecutable(string directory, string path)
    {
        string resolved = ResolvePath(directory, path);
        if (!Path.GetExtension(resolved).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("executable은 .exe 실행 파일이어야 합니다.");
        return resolved;
    }
    private static string ResolveInstallerExecutable(string directory, string path)
    {
        string resolved = ResolveExecutable(directory, path);
        AppInstaller.EnsureContained(directory, resolved, "설치 프로그램");
        AppInstaller.EnsureNoReparsePoints(directory, resolved);
        return resolved;
    }
    private static string ResolveBuildProject(string directory, string path)
    {
        if (Path.IsPathFullyQualified(path))
            throw new FormatException("build 프로젝트 경로는 앱 폴더 기준 상대 경로여야 합니다.");
        string resolved = ResolvePath(directory, path);
        AppInstaller.EnsureContained(directory, resolved, "빌드 프로젝트");
        if (!Path.GetExtension(resolved).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("build 프로젝트는 .csproj 파일이어야 합니다.");
        return resolved;
    }
    private static string RequiredString(JsonElement json, string name)
    {
        string value = OptionalString(json, name);
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException($"{name} 값이 필요합니다.");
        return value;
    }
    private static string OptionalString(JsonElement json, string name, string fallback = "") =>
        json.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
    private static string ResolvePath(string directory, string path) => Path.GetFullPath(path, directory);
    private static bool IsPathContained(string baseDirectory, string path)
    {
        string root = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
