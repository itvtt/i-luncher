using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using ILauncher.Bootstrapper;
using ILauncher.Models;
using ILauncher.Services;

if (args.Length >= 2 && args[0] == "--launch-probe")
{
    var outputPath = Path.GetFullPath(args[1]);
    var temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
    var payload = JsonSerializer.Serialize(new
    {
        arguments = args.Skip(2).ToArray(),
        workingDirectory = Environment.CurrentDirectory
    });
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(temporaryPath, payload);
    File.Move(temporaryPath, outputPath);
    return;
}

if (args.Length >= 4 && args[0] == "--install-probe")
{
    var outputPath = Path.GetFullPath(args[1]);
    var exitCode = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    var delayMilliseconds = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    await Task.Delay(delayMilliseconds);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(new
    {
        arguments = args.Skip(4).ToArray(),
        workingDirectory = Environment.CurrentDirectory
    }));
    Environment.ExitCode = exitCode;
    return;
}

var workspace = Path.Combine(Path.GetTempPath(), "Launcher-Checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);
var passed = 0;
var failed = 0;

string CreateDirectory(string relativePath)
{
    var path = Path.Combine(workspace, relativePath);
    Directory.CreateDirectory(path);
    return path;
}

string WriteFile(string relativePath, string contents = "")
{
    var path = Path.Combine(workspace, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, contents);
    return path;
}

string CreateRoot(string relativePath)
{
    var root = CreateDirectory(relativePath);
    File.WriteAllText(Path.Combine(root, "i-luncher.json"), "{\"schemaVersion\":1}");
    return root;
}

void WriteManifest(string appDirectory, object manifest)
{
    Directory.CreateDirectory(appDirectory);
    File.WriteAllText(
        Path.Combine(appDirectory, "launcher.app.json"),
        JsonSerializer.Serialize(manifest));
}

void WriteOnlineUpdateSidecar(string root, string starterPath, string catalogJson)
{
    byte[] catalogBytes = new System.Text.UTF8Encoding(false, true).GetBytes(catalogJson);
    string catalogHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(catalogBytes)).ToLowerInvariant();
    string starterName = Path.GetFileName(starterPath);
    string starterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(starterPath))).ToLowerInvariant();
    File.WriteAllText(Path.Combine(root, "i-luncher-updates.json"), JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        starterExecutable = starterName,
        starterSha256 = starterHash,
        catalog = new { json = catalogJson, sha256 = catalogHash },
        source = new
        {
            serverUrl = "https://github.com", apiUrl = "https://api.github.com", repository = "example/iluncher"
        }
    }));
}

(string TargetPath, string Arguments, string IconLocation) ReadShortcut(string shortcutPath)
{
    object? shell = null;
    object? shortcut = null;
    try
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
            ?? throw new PlatformNotSupportedException("Windows Script Host is unavailable.");
        shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create Windows Script Host.");
        shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
        var shortcutType = shortcut?.GetType() ?? throw new InvalidOperationException("Could not open the shortcut.");
        string target = (string?)shortcutType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) ?? "";
        string arguments = (string?)shortcutType.InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null) ?? "";
        string iconLocation = (string?)shortcutType.InvokeMember("IconLocation", BindingFlags.GetProperty, null, shortcut, null) ?? "";
        return (target, arguments, iconLocation);
    }
    finally
    {
        if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
        if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
    }
}

void SetShortcutTarget(string shortcutPath, string targetPath)
{
    object? shell = null;
    object? shortcut = null;
    try
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
            ?? throw new PlatformNotSupportedException("Windows Script Host is unavailable.");
        shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create Windows Script Host.");
        shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
        var shortcutType = shortcut?.GetType() ?? throw new InvalidOperationException("Could not open the shortcut.");
        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }
    finally
    {
        if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
        if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
    }
}

string CopyCheckHost(string destinationDirectory)
{
    Directory.CreateDirectory(destinationDirectory);
    foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
        File.Copy(source, Path.Combine(destinationDirectory, Path.GetFileName(source)), overwrite: true);
    var executable = Path.Combine(destinationDirectory, "Launcher.Checks.exe");
    Assert(File.Exists(executable), $"Check apphost is unavailable at '{executable}'.");
    return executable;
}

void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

void AssertPath(string expected, string actual, string message)
{
    Assert(
        string.Equals(Path.GetFullPath(expected), Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase),
        $"{message}. Expected '{expected}', got '{actual}'.");
}

async Task Check(string name, Func<Task> action)
{
    var timer = Stopwatch.StartNew();
    try
    {
        await action();
        passed++;
        Console.WriteLine($"PASS {name} ({timer.Elapsed.TotalMilliseconds:F0} ms)");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {name}\n{ex}");
    }
}

try
{
    if (args.Contains("--full-removal-checks", StringComparer.Ordinal))
    {
        await Check("full removal ownership, sharing, and rollback", FullRemovalChecks.RunAsync);
        Environment.ExitCode = failed == 0 ? 0 : 1;
        return;
    }
    await Check("notice feed, cache, revisions, and transport checks", NoticeChecks.RunAsync);
    if (args.Contains("--notice-checks", StringComparer.Ordinal))
    {
        Environment.ExitCode = failed == 0 ? 0 : 1;
        return;
    }
    await Check("full removal ownership, sharing, and rollback", FullRemovalChecks.RunAsync);
    await Check("online install receipts keep legacy metadata valid and unknown versions nullable", () =>
    {
        var legacyReceipt = new Dictionary<string, string>
        {
            ["appId"] = "bundled-app", ["catalogSha256"] = new string('a', 64),
            ["packageSha256"] = new string('b', 64), ["runtimeSha256"] = new string('c', 64),
            ["executable"] = "BundledApp.exe", ["launcher"] = "launcher.exe"
        };
        Assert(BootstrapperEngine.IsValidReceiptVersionMetadata(legacyReceipt),
            "A valid six-field receipt without app-version metadata was rejected.");

        legacyReceipt["runtimeVersion"] = "10.0.1";
        legacyReceipt["argumentsBase64"] = "";
        legacyReceipt["appVersion"] = "7.4.2";
        Assert(BootstrapperEngine.IsValidReceiptVersionMetadata(legacyReceipt),
            "A legacy receipt with its historical release fallback was rejected.");

        legacyReceipt["appVersionSource"] = "unknown";
        legacyReceipt["appVersion"] = "";
        Assert(BootstrapperEngine.IsValidReceiptVersionMetadata(legacyReceipt),
            "A new receipt with an explicitly unknown app version was rejected.");
        legacyReceipt["appVersionSource"] = "manifest";
        legacyReceipt["appVersion"] = "2.3.1";
        Assert(BootstrapperEngine.IsValidReceiptVersionMetadata(legacyReceipt),
            "A new receipt with an authoritative app version was rejected.");
        legacyReceipt["appVersion"] = "";
        Assert(!BootstrapperEngine.IsValidReceiptVersionMetadata(legacyReceipt),
            "A sourced receipt with a blank app version was accepted.");
        return Task.CompletedTask;
    });

    await Check("legacy package versions require the exact app ID and package hash", () =>
    {
        const string deskcalHash = "296821d47fce35206c22864049d462fd4668cfd87c50d6f3ed9b585c9d0a6a15";
        BootstrapperCatalog Parse(string appId, string packageHash, string versionFields)
        {
            string catalogJson = "{\"schemaVersion\":1,\"version\":\"7.4.3\",\"source\":{\"serverUrl\":\"https://github.com\",\"apiUrl\":\"https://api.github.com\",\"repository\":\"example/iluncher\",\"tagName\":\"v7.4.3\"},\"runtime\":{\"version\":\"10.0.1\",\"asset\":\"runtime.zip\",\"sha256\":\"" + new string('c', 64) + "\",\"bytes\":1},\"apps\":[{\"id\":\"" + appId + "\",\"name\":\"Fixture\",\"description\":\"Fixture\",\"category\":\"Tools\",\"executable\":\"App.exe\",\"asset\":\"app.zip\",\"sha256\":\"" + packageHash + "\",\"bytes\":1" + versionFields + "}]}";
            byte[] bytes = new System.Text.UTF8Encoding(false, true).GetBytes(catalogJson);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            return CatalogParser.Parse(catalogJson, hash,
                new BootstrapperOptions(catalogJson, hash, CreateDirectory("legacy version index cache"), Path.Combine(workspace, "launcher.exe")));
        }

        var indexed = Parse("deskcal", deskcalHash, "").Apps.Single();
        Assert(indexed.Version == "1.0.0" && indexed.VersionSource == "package",
            "A verified legacy package did not resolve with package provenance.");
        var wrongHash = Parse("deskcal", new string('d', 64), "").Apps.Single();
        Assert(wrongHash.Version is null && wrongHash.VersionSource is null,
            "An unmatched package hash inherited a compatibility version.");
        var wrongId = Parse("other-app", deskcalHash, "").Apps.Single();
        Assert(wrongId.Version is null && wrongId.VersionSource is null,
            "A package hash was accepted under a different app ID.");
        var explicitVersion = Parse("deskcal", deskcalHash, ",\"version\":\"4.2.0\",\"versionSource\":\"manifest\"").Apps.Single();
        Assert(explicitVersion.Version == "4.2.0" && explicitVersion.VersionSource == "manifest",
            "An explicit catalog app version did not take precedence over the compatibility index.");

        var receipt = new Dictionary<string, string>
        {
            ["appId"] = "deskcal", ["catalogSha256"] = new string('a', 64),
            ["packageSha256"] = deskcalHash, ["runtimeSha256"] = new string('c', 64),
            ["executable"] = "App.exe", ["launcher"] = "launcher.exe",
            ["appVersion"] = "1.0.0", ["appVersionSource"] = "package",
            ["runtimeVersion"] = "10.0.1", ["argumentsBase64"] = ""
        };
        Assert(BootstrapperEngine.IsValidReceiptVersionMetadata(receipt),
            "A new receipt with package-derived version provenance was rejected.");
        var options = new BootstrapperOptions("{}", new string('a', 64), CreateDirectory("package receipt cache"),
            Path.Combine(workspace, "launcher.exe")) { ReadOnlyStatus = true };
        using var engine = new BootstrapperEngine(options, NullBootstrapperCredentialProvider.Instance);
        var resolve = typeof(BootstrapperEngine).GetMethod("ResolveInstalledVersion",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert((string?)resolve.Invoke(engine, [indexed, receipt]) == "1.0.0",
            "A package-provenance receipt did not resolve from its exact package identity.");
        receipt["appVersion"] = "1.0.1";
        Assert(resolve.Invoke(engine, [indexed, receipt]) is null,
            "A package-provenance receipt with a version outside the compatibility index was trusted.");
        return Task.CompletedTask;
    });

    await Check("owned installed legacy executable exposes its binary version when sidecar metadata is absent", () =>
    {
        string appsDirectory = CreateDirectory("owned binary version apps");
        const string installHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string installRoot = Path.Combine(appsDirectory, "fixture-app");
        string payload = Path.Combine(installRoot, installHash);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(installRoot, "current.version"), installHash);
        File.WriteAllText(Path.Combine(payload, ".install-complete"), "complete");
        string installedExecutable = Path.Combine(payload, "fixture.exe");
        File.WriteAllText(installedExecutable, "non-executable fixture; never launched");
        string installedAssembly = Path.Combine(payload, "fixture.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Launcher.Checks.dll"), installedAssembly);
        var expectedVersionInfo = FileVersionInfo.GetVersionInfo(installedAssembly);
        string? expected = expectedVersionInfo.ProductVersion;
        if (string.IsNullOrWhiteSpace(expected)) expected = expectedVersionInfo.FileVersion;
        var entry = new LauncherEntry
        {
            Id = "fixture-app", Name = "Fixture", ExecutablePath = installedExecutable,
            WorkingDirectory = payload, InstalledAppDirectory = installRoot,
            InstalledExecutableRelativePath = "fixture.exe",
            InstallDefinition = new LauncherInstallDefinition(LauncherInstallType.Portable, null, [])
        };
        Assert(entry.TryGetInstalledExecutablePath() == installedExecutable,
            "The owned fixture target was not accepted by the install-marker checks.");
        Assert(!string.IsNullOrWhiteSpace(expected) && entry.InstalledVersion == expected,
            "A verified owned executable did not supply its product/file version metadata.");
        return Task.CompletedTask;
    });

    await Check("legacy receipt versions resolve only from matching per-app catalog metadata", async () =>
    {
        async Task<string?> Resolve(string appVersionJson, string appVersionFallback)
        {
            string catalogJson = "{\"schemaVersion\":1,\"version\":\"7.4.3\",\"source\":{\"serverUrl\":\"https://github.com\",\"apiUrl\":\"https://api.github.com\",\"repository\":\"example/iluncher\",\"tagName\":\"v7.4.3\"},\"runtime\":{\"version\":\"10.0.1\",\"asset\":\"runtime.zip\",\"sha256\":\"" + new string('c', 64) + "\",\"bytes\":1},\"apps\":[{\"id\":\"bundled-app\",\"name\":\"Bundled App\",\"description\":\"Fixture\",\"category\":\"Tools\",\"executable\":\"BundledApp.exe\",\"asset\":\"bundled-app.zip\",\"sha256\":\"" + new string('b', 64) + "\",\"bytes\":1" + appVersionJson + "}]}";
            byte[] bytes = new System.Text.UTF8Encoding(false, true).GetBytes(catalogJson);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var options = new BootstrapperOptions(catalogJson, hash, CreateDirectory("legacy receipt resolution cache"),
                Path.Combine(workspace, "launcher.exe")) { ReadOnlyStatus = true };
            using var engine = new BootstrapperEngine(options, NullBootstrapperCredentialProvider.Instance);
            var catalog = await engine.LoadCatalogAsync(CancellationToken.None);
            var fields = new Dictionary<string, string>
            {
                ["catalogSha256"] = hash, ["packageSha256"] = new string('b', 64),
                ["runtimeSha256"] = new string('c', 64), ["appVersion"] = appVersionFallback
            };
            var resolve = typeof(BootstrapperEngine).GetMethod("ResolveInstalledVersion",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (string?)resolve.Invoke(engine, [catalog.Apps.Single(), fields]);
        }

        Assert(await Resolve(",\"version\":\"2.3.1\"", "7.4.2") == "2.3.1",
            "A legacy receipt trusted its catalog-release fallback instead of its matching app version.");
        Assert(await Resolve("", "7.4.2") is null,
            "A legacy receipt with no catalog app version was assigned the catalog release version.");
    });

    await Check("distribution update sidecar exposes per-app release version without implying install", () =>
    {
        string root = CreateRoot("online update sidecar");
        File.WriteAllText(Path.Combine(root, "i-luncher.json"),
            "{\"schemaVersion\":1,\"distribution\":{\"integrated\":true}}");
        string appDirectory = Path.Combine(root, "Bundled App");
        WriteManifest(appDirectory, new
        {
            id = "bundled-app", name = "Bundled App", executable = "BundledApp.exe",
            category = "Tools", install = new { type = "portable" }
        });
        string catalogJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version = "7.4.2",
            source = new
            {
                serverUrl = "https://github.com", apiUrl = "https://api.github.com",
                repository = "example/iluncher", tagName = "v7.4.2"
            },
            runtime = new
            {
                version = "10.0.1", asset = "i-luncher-runtime.zip", sha256 = new string('a', 64), bytes = 1
            },
            apps = new[]
            {
                new
                {
                    id = "bundled-app", name = "Bundled App", description = "Fixture app", category = "Tools",
                    version = "2.3.1", versionSource = "manifest", executable = "BundledApp.exe", asset = "bundled-app.zip",
                    sha256 = new string('b', 64), bytes = 1
                }
            }
        });
        byte[] catalogBytes = new System.Text.UTF8Encoding(false, true).GetBytes(catalogJson);
        string catalogHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(catalogBytes)).ToLowerInvariant();
        BootstrapperCatalog parsedCatalog = CatalogParser.Parse(catalogJson, catalogHash,
            new BootstrapperOptions(catalogJson, catalogHash, root, Path.Combine(root, "i-luncher-start.exe")));
        Assert(parsedCatalog.Apps.Single().VersionSource == "manifest",
            "Catalog app version provenance was not retained by the parser.");
        string starterName = "i-luncher-start.exe";
        string starterPath = Path.Combine(root, starterName);
        File.WriteAllBytes(starterPath, [0x4d, 0x5a, 0x00, 0x01]);
        string starterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(starterPath))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(root, "i-luncher-updates.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            starterExecutable = starterName,
            starterSha256 = starterHash,
            catalog = new { json = catalogJson, sha256 = catalogHash },
            source = new
            {
                serverUrl = "https://github.com", apiUrl = "https://api.github.com", repository = "example/iluncher"
            }
        }));
        string appsBase = CreateDirectory("online update sidecar apps");
        string staleHash = new string('c', 64);
        string staleAppRoot = Path.Combine(appsBase, "bundled-app");
        string stalePayload = Path.Combine(appsBase, "_shared-payloads", staleHash);
        Directory.CreateDirectory(staleAppRoot);
        Directory.CreateDirectory(stalePayload);
        File.WriteAllText(Path.Combine(staleAppRoot, "current.version"), staleHash);
        File.WriteAllText(Path.Combine(stalePayload, ".install-complete"), "complete");
        File.WriteAllText(Path.Combine(stalePayload, "BundledApp.exe"), "stale legacy payload");
        var catalog = new LauncherCatalog(root, appsBase, CreateDirectory("online update sidecar menu"));
        var result = catalog.Load();
        var entry = result.Entries.Single(candidate => candidate.Id == "bundled-app");
        Assert(entry.AvailableVersion == "2.3.1", "Per-app version was not sourced from the catalog.");
        var onlineEngine = typeof(LauncherCatalog).GetField("_onlineEngine",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(catalog);
        var engineOptions = onlineEngine?.GetType().GetField("_options",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(onlineEngine);
        string? configuredStartMenu = engineOptions?.GetType().GetProperty("StartMenuRoot")?.GetValue(engineOptions) as string;
        Assert(configuredStartMenu == Path.GetFullPath(Path.Combine(root, "..", "online update sidecar menu")),
            "The online engine ignored the injected Start Menu root.");
        Assert(entry.CanInstall && entry.HasInstallFiles, "A catalog-backed app was not installable.");
        Assert(entry.InstalledVersion is null && !entry.UpdateAvailable && !entry.CanUninstall,
            "An uninstalled app with a stale legacy receipt was shown as installed or updatable.");
        Assert(entry.TryGetInstalledExecutablePath() is null,
            "An online-managed app fell through to a stale legacy current.version marker.");

        var newerReleaseCatalog = System.Text.Json.Nodes.JsonNode.Parse(catalogJson)!.AsObject();
        newerReleaseCatalog["version"] = "7.4.3";
        newerReleaseCatalog["source"]!["tagName"] = "v7.4.3";
        string newerReleaseJson = newerReleaseCatalog.ToJsonString();
        WriteOnlineUpdateSidecar(root, starterPath, newerReleaseJson);
        var newerReleaseResult = new LauncherCatalog(root, appsBase, CreateDirectory("online update newer release menu")).Load();
        Assert(newerReleaseResult.Entries.Single(candidate => candidate.Id == "bundled-app").AvailableVersion == "2.3.1",
            "Changing the catalog release changed an unchanged app's available version.");

        newerReleaseCatalog["apps"]![0]!.AsObject().Remove("version");
        newerReleaseCatalog["apps"]![0]!.AsObject().Remove("versionSource");
        string legacyCatalogJson = newerReleaseCatalog.ToJsonString();
        WriteOnlineUpdateSidecar(root, starterPath, legacyCatalogJson);
        var legacyResult = new LauncherCatalog(root, appsBase, CreateDirectory("online update legacy version menu")).Load();
        Assert(legacyResult.Entries.Single(candidate => candidate.Id == "bundled-app").AvailableVersion is null,
            "A catalog without app.version was relabeled with the global release version.");

        string sidecarPath = Path.Combine(root, "i-luncher-updates.json");
        string validSidecar = File.ReadAllText(sidecarPath);
        var sourceMismatchSidecar = System.Text.Json.Nodes.JsonNode.Parse(validSidecar)!.AsObject();
        var mismatchedCatalog = System.Text.Json.Nodes.JsonNode.Parse(
            sourceMismatchSidecar["catalog"]!["json"]!.GetValue<string>())!.AsObject();
        mismatchedCatalog["source"]!["serverUrl"] = "https://untrusted.example";
        string mismatchedCatalogJson = mismatchedCatalog.ToJsonString();
        byte[] mismatchedCatalogBytes = new System.Text.UTF8Encoding(false, true).GetBytes(mismatchedCatalogJson);
        sourceMismatchSidecar["catalog"]!["json"] = mismatchedCatalogJson;
        sourceMismatchSidecar["catalog"]!["sha256"] = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(mismatchedCatalogBytes)).ToLowerInvariant();
        File.WriteAllText(sidecarPath, sourceMismatchSidecar.ToJsonString());
        var mismatchedCatalogResult = new LauncherCatalog(root, appsBase,
            CreateDirectory("online update mismatch menu")).Load();
        Assert(mismatchedCatalogResult.Errors.Any(error => error.Contains("Online app updates are unavailable", StringComparison.Ordinal)),
            "A catalog that changed the sidecar's pinned source was not rejected before engine creation.");

        File.WriteAllText(sidecarPath, validSidecar);
        var unsafeSidecar = System.Text.Json.Nodes.JsonNode.Parse(validSidecar)!.AsObject();
        unsafeSidecar["starterExecutable"] = "..\\outside.exe";
        File.WriteAllText(sidecarPath, unsafeSidecar.ToJsonString());
        var noStarterCatalog = new LauncherCatalog(root, appsBase, CreateDirectory("online update invalid menu"));
        var noStarterResult = noStarterCatalog.Load();
        Assert(noStarterResult.Errors.Any(error => error.Contains("Online app updates are unavailable", StringComparison.Ordinal)),
            "An escaping starter path did not disable online-managed installation with a catalog error.");
        Assert(noStarterResult.Entries.Single(candidate => candidate.Id == "bundled-app").AvailableVersion is null,
            "The invalid starter sidecar still exposed the online app version.");
        return Task.CompletedTask;
    });

    await Check("file_shared source manifest is cataloged as a portable buildable app", () =>
    {
        var productionRoot = new LauncherCatalog().RootDirectory;
        var catalog = new LauncherCatalog(productionRoot,
            CreateDirectory("file_shared installed apps"), CreateDirectory("file_shared start menu"));
        var result = catalog.Load();
        var entry = result.Entries.SingleOrDefault(candidate => candidate.Id == "file_shared");
        Assert(entry is not null, "The real file_shared manifest is missing from the source catalog: " + string.Join(" | ", result.Errors));
        Assert(entry!.Name == "File Shared" && entry.Category == "네트워크"
               && entry.Arguments.Count == 0 && entry.CanBuild && entry.CanInstall,
            "The file_shared catalog entry has unexpected metadata or is not buildable/installable.");
        Assert(entry.BuildDefinition?.ProjectPath == Path.Combine(productionRoot, "file_shared", "file_shared.csproj"),
            "The file_shared manifest does not resolve its legacy project path.");
        return Task.CompletedTask;
    });

    await Check("source discovery finds safe runnable projects from supported roots without building", () =>
    {
        var root = CreateRoot("source discovery contract");
        string directApp = Path.Combine(root, "Direct App");
        string nestedApp = Path.Combine(root, "Projects", "Nested App");
        string unicodeApp = Path.Combine(root, "应用程序");
        string libraryApp = Path.Combine(root, "Library App");
        string explicitApp = Path.Combine(root, "Explicit App");
        string ambiguousApp = Path.Combine(root, "Ambiguous App");
        string excludedApp = Path.Combine(root, "Tests");

        WriteFile(Path.Combine("source discovery contract", "Direct App", "Direct.csproj"),
            "<Project><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>DirectRunner</AssemblyName></PropertyGroup></Project>");
        WriteFile(Path.Combine("source discovery contract", "Projects", "Nested App", "Nested.csproj"),
            "<Project><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        WriteFile(Path.Combine("source discovery contract", "应用程序", "Unicode.csproj"),
            "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        WriteFile(Path.Combine("source discovery contract", "Library App", "Library.csproj"),
            "<Project><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>");
        WriteFile(Path.Combine("source discovery contract", "Explicit App", "launcher.app.json"), "{}");
        WriteFile(Path.Combine("source discovery contract", "Explicit App", "Explicit.csproj"),
            "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        WriteFile(Path.Combine("source discovery contract", "Ambiguous App", "One.csproj"),
            "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        WriteFile(Path.Combine("source discovery contract", "Ambiguous App", "Two.csproj"),
            "<Project><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        WriteFile(Path.Combine("source discovery contract", "Tests", "Ignored.csproj"),
            "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        WriteFile(Path.Combine("source discovery contract", "Direct App", "bin", "Release", "Ignored.csproj"),
            "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        WriteFile(Path.Combine("source discovery contract", "Direct App", "obj", "Ignored.csproj"),
            "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        WriteFile(Path.Combine("source discovery contract", "Direct App", "checks", "Ignored.csproj"),
            "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");

        bool reparseCreated = false;
        string reparseTarget = CreateDirectory("source discovery reparse target");
        WriteFile(Path.Combine("source discovery reparse target", "Linked.csproj"),
            "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(directApp, "linked-source"), reparseTarget);
            reparseCreated = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                                   or System.ComponentModel.Win32Exception)
        {
            // Reparse point creation can be disabled by the host; all other exclusion cases stay mandatory.
        }

        var appDirectories = SourceProjectDiscovery.GetAppDirectories(root);
        Assert(appDirectories.Contains(directApp, StringComparer.OrdinalIgnoreCase)
               && appDirectories.Contains(nestedApp, StringComparer.OrdinalIgnoreCase),
            "Both root/<folder> and root/Projects/<folder> app roots must be discovered.");
        Assert(!appDirectories.Contains(excludedApp, StringComparer.OrdinalIgnoreCase),
            "Test/check app folders must be excluded.");

        var discovery = SourceProjectDiscovery.Discover(root, appDirectories);
        Assert(discovery.Projects.Count == 3,
            "Exactly the direct, nested, and Unicode runnable apps should be discovered; got " + discovery.Projects.Count);
        Assert(discovery.Projects.Single(project => project.AppDirectory == directApp).AssemblyName == "DirectRunner",
            "Explicit AssemblyName must be preserved for lazy launch builds.");
        Assert(discovery.Projects.Single(project => project.AppDirectory == nestedApp).AssemblyName == "Nested",
            "AssemblyName should fall back to the project file name.");
        var unicodeProject = discovery.Projects.Single(project => project.AppDirectory == unicodeApp);
        Assert(unicodeProject.Id.StartsWith("app-", StringComparison.Ordinal)
               && SourceProjectDiscovery.IsValidId(unicodeProject.Id),
            "Unicode-only folder names must receive a safe hash fallback id.");
        Assert(discovery.Errors.Any(error => error.Contains("found 2 runnable .NET projects", StringComparison.Ordinal)),
            "Ambiguous runnable projects must produce a diagnostic.");
        Assert(!discovery.Projects.Any(project => project.AppDirectory == explicitApp
                    || project.AppDirectory == libraryApp || project.AppDirectory == excludedApp),
            "Explicit manifests, library projects, and excluded test folders must not be auto-discovered.");
        if (reparseCreated)
            Assert(!discovery.Projects.Any(project => project.ProjectPath.Contains("Linked.csproj", StringComparison.OrdinalIgnoreCase)),
                "Projects reached through reparse directories must be ignored.");

        var catalog = new LauncherCatalog(root, CreateDirectory("source discovery installed apps"),
            CreateDirectory("source discovery start menu"));
        CatalogResult loaded = catalog.Load();
        var discoveredEntries = loaded.Entries.Where(entry => entry.IsAutoDiscoveredSourceProject).ToArray();
        Assert(discoveredEntries.Length == 3 && discoveredEntries.All(entry => entry.CanBuild),
            "Catalog.Load must expose discovered projects as buildable entries without invoking a build.");
        Assert(discoveredEntries.All(entry => !File.Exists(entry.ExecutablePath)),
            "Catalog.Load must leave missing app executables unbuilt until launch.");
        string sourceFingerprint = SourceProjectDiscovery.CreateSourceFingerprint(directApp);
        WriteFile(Path.Combine("source discovery contract", "Direct App", "NewSource.cs"), "class NewSource { }");
        Assert(sourceFingerprint != SourceProjectDiscovery.CreateSourceFingerprint(directApp),
            "Adding a source file must invalidate the source build fingerprint.");
        return Task.CompletedTask;
    });

    await Check("automatic source build cache reuses unchanged output and invalidates changed inputs", async () =>
    {
        var root = CreateRoot("automatic source cache");
        string projectPath = WriteFile(Path.Combine("automatic source cache", "Automatic Source App", "Runner.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>Runner</AssemblyName></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup>
              <Import Project="../Custom.targets" />
            </Project>
            """);
        string sourcePath = WriteFile(Path.Combine("automatic source cache", "Automatic Source App", "Program.cs"), "class Program { }");
        WriteFile(Path.Combine("automatic source cache", "Shared", "Shared.csproj"), "<Project><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>");
        string sharedSource = WriteFile(Path.Combine("automatic source cache", "Shared", "Shared.cs"), "class Shared { }");
        string customTargets = WriteFile(Path.Combine("automatic source cache", "Custom.targets"), "<Project><PropertyGroup><FixtureTargetValue>1</FixtureTargetValue></PropertyGroup></Project>");
        string buildCountPath = Path.Combine(root, ".fixture-build-count");
        string mutatePath = Path.Combine(root, ".mutate-source-on-build");
        File.WriteAllText(Path.Combine(root, "Build.ps1"), """
            param([string]$App, [string]$Project)
            $root = [IO.Path]::GetFullPath((Get-Location).Path)
            if ($Project -ne 'Automatic Source App\Runner.csproj') { throw "Unexpected project path: $Project" }
            $countPath = Join-Path $root '.fixture-build-count'
            $count = if (Test-Path -LiteralPath $countPath) { [int][IO.File]::ReadAllText($countPath) } else { 0 }
            [IO.File]::WriteAllText($countPath, [string]($count + 1))
            $normalized = $root.ToUpperInvariant()
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $sourcePathHash = ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($normalized)))).Replace('-', '').ToLowerInvariant() }
            finally { $algorithm.Dispose() }
            $workspaceBase = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'i-luncher\Workspaces'
            $workspace = Join-Path $workspaceBase $sourcePathHash.Substring(0, 24)
            [IO.Directory]::CreateDirectory($workspace) | Out-Null
            $marker = @{ version = 1; sourcePath = $root; normalizedSourcePath = $normalized; sourcePathHash = $sourcePathHash } | ConvertTo-Json -Compress
            [IO.File]::WriteAllText((Join-Path $workspace '.i-luncher-workspace.json'), $marker)
            $outputDirectory = Join-Path $workspace (Join-Path 'dist\apps' $App)
            [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
            [IO.File]::WriteAllText((Join-Path $outputDirectory 'Runner.exe'), 'fixture output')
            if (Test-Path -LiteralPath (Join-Path $root '.mutate-source-on-build')) {
                [IO.File]::AppendAllText((Join-Path $root 'Automatic Source App\Program.cs'), "`n// changed while building")
            }
            """);

        var entry = new LauncherCatalog(root).Load().Entries.Single();
        Assert(entry.IsAutoDiscoveredSourceProject && entry.BuildDefinition?.ProjectPath == projectPath,
            "The fixture must exercise a catalog-discovered source entry.");
        var builder = new AppBuilder(root);
        string normalizedRoot = Path.GetFullPath(root).ToUpperInvariant();
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedRoot))).ToLowerInvariant();
        string workspaceBasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-luncher", "Workspaces");
        string cache = Path.Combine(workspaceBasePath, sourceHash[..24]);
        try
        {
            await builder.BuildAsync(entry);
            Assert(File.ReadAllText(buildCountPath) == "1", "The first launch must build once.");
            Assert(builder.TryGetExistingOutput(entry) is not null, "An unchanged build must be reusable.");
            Assert(File.ReadAllText(buildCountPath) == "1", "Reusing a valid output must not invoke the build script again.");

            WriteFile(Path.Combine("automatic source cache", "Fresh Pasted App", "Fresh.csproj"),
                "<Project><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            var workspaceCatalog = new LauncherCatalog(cache);
            Assert(workspaceCatalog.Load().Entries.Any(candidate => candidate.Id == "fresh-pasted-app"),
                "Refreshing a launcher running from its workspace must discover projects newly pasted into the source root.");

            File.AppendAllText(sourcePath, "\nclass ChangedSource { }");
            Assert(builder.TryGetExistingOutput(entry) is null, "Changing app source must invalidate the cached output.");
            await builder.BuildAsync(entry);
            Assert(File.ReadAllText(buildCountPath) == "2", "Changed app source must cause a rebuild.");

            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project><PropertyGroup><FixtureValue>1</FixtureValue></PropertyGroup></Project>");
            Assert(builder.TryGetExistingOutput(entry) is null, "Changing a root shared props file must invalidate the cached output.");
            await builder.BuildAsync(entry);
            Assert(File.ReadAllText(buildCountPath) == "3", "Changed shared props must cause a rebuild.");

            File.AppendAllText(sharedSource, "\nclass ChangedSharedSource { }");
            Assert(builder.TryGetExistingOutput(entry) is null, "Changing a referenced project source must invalidate the cached output.");
            await builder.BuildAsync(entry);
            Assert(File.ReadAllText(buildCountPath) == "4", "Changed referenced project source must cause a rebuild.");

            File.AppendAllText(customTargets, "\n<!-- updated imported target -->");
            Assert(builder.TryGetExistingOutput(entry) is null, "Changing an explicitly imported root-level target must invalidate the cached output.");
            await builder.BuildAsync(entry);
            Assert(File.ReadAllText(buildCountPath) == "5", "Changed imported target must cause a rebuild.");

            File.WriteAllText(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>Runner</AssemblyName></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup>
                  <Import Project="$(UnresolvedTargetPath)" />
                </Project>
                """);
            await builder.BuildAsync(entry);
            Assert(File.ReadAllText(buildCountPath) == "6" && builder.TryGetExistingOutput(entry) is null,
                "A source project with an unresolved import must build without being cached.");
            await builder.BuildAsync(entry);
            Assert(File.ReadAllText(buildCountPath) == "7" && builder.TryGetExistingOutput(entry) is null,
                "An unresolved import must conservatively disable cache reuse without blocking builds.");

            File.WriteAllText(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>Runner</AssemblyName></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup>
                  <Import Project="../Custom.targets" />
                </Project>
                """);
            await builder.BuildAsync(entry);
            Assert(File.ReadAllText(buildCountPath) == "8" && builder.TryGetExistingOutput(entry) is not null,
                "Restoring a supported import must resume cache reuse before the mutation-during-build check.");

            File.WriteAllText(mutatePath, "mutate");
            try
            {
                await builder.BuildAsync(entry);
                throw new Exception("A source mutation during build was stamped as a current output.");
            }
            catch (LauncherBuildException ex)
            {
                Assert(ex.Message.Contains("changed while", StringComparison.Ordinal), "Mid-build source mutation should explain that the source changed.");
            }
            Assert(File.ReadAllText(buildCountPath) == "9" && builder.TryGetExistingOutput(entry) is null,
                "A build with mid-build source changes must not save a reusable cache stamp.");
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Assert(cache.StartsWith(Path.GetFullPath(workspaceBasePath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "Refusing cleanup outside the test workspace base.");
                using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(cache, ".i-luncher-workspace.json")));
                Assert(marker.RootElement.GetProperty("normalizedSourcePath").GetString() == normalizedRoot,
                    "Refusing cleanup of another source's workspace.");
                Directory.Delete(cache, recursive: true);
            }
        }
    });

    await Check("integrated provider wins over stale external definitions without files", async () =>
    {
        var root = CreateRoot("integrated provider");
        var appsBase = CreateDirectory("integrated provider apps");
        var startMenu = CreateDirectory("integrated provider menu");
        var providerEntries = new[] { "folder-compare", "swift-copy", "disk-atlas", "deskcal" }
            .Select(id => new LauncherEntry
        {
            Id = id,
            Name = id == "folder-compare" ? "Built in" : id,
            Description = "Provided in process",
            Category = "Test",
            IsIntegrated = true,
            ExecutablePath = Path.Combine(AppContext.BaseDirectory, "Launcher.Checks.exe"),
            WorkingDirectory = AppContext.BaseDirectory
        }).ToArray();
        var provider = new FakeIntegratedAppProvider(providerEntries);

        var staleDirectory = Path.Combine(root, "stale external definition");
        WriteManifest(staleDirectory, new
        {
            id = "FOLDER-COMPARE",
            name = "Stale external copy",
            executable = "dist/stale.exe",
            build = new { project = "Missing.csproj" },
            install = new { type = "portable" }
        });
        var staleInstalledRoot = Path.Combine(appsBase, "folder-compare");
        var staleVersion = new string('a', 64);
        var staleVersionDirectory = Path.Combine(staleInstalledRoot, staleVersion);
        Directory.CreateDirectory(staleVersionDirectory);
        File.WriteAllText(Path.Combine(staleInstalledRoot, "current.version"), staleVersion);
        File.WriteAllText(Path.Combine(staleVersionDirectory, ".install-complete"), "complete");
        File.WriteAllText(Path.Combine(staleVersionDirectory, "stale.exe"), "stale");

        var catalog = new LauncherCatalog(root, appsBase, startMenu, provider);
        var result = catalog.Load();
        Assert(result.Errors.Count == 0, "A stale duplicate manifest produced a catalog error: " + string.Join(" | ", result.Errors));
        var entries = result.Entries.ToArray();
        Assert(entries.Length == 4 && entries.All(entry => entry.IsIntegrated && entry.CanInstall && !entry.CanBuild),
            "All four integrated apps must expose shared-payload install without a per-app build.");
        Assert(entries.Single(entry => entry.Id == "folder-compare").Name == "Built in",
            "The stale external definition replaced the integrated provider entry.");
        Assert(entries.All(entry => entry.IsAvailable), "Integrated entries must remain launchable through their provider.");
        Assert(entries.All(entry => entry.InstallShortcutArguments.SequenceEqual(["--app", entry.Id])),
            "Integrated shortcut arguments did not target each provider app ID.");
        Assert(entries.All(entry => entry.TryGetInstalledExecutablePath() is null),
            "An integrated receipt was treated as installed before its own install completed.");

        var firstEntry = entries.Single(entry => entry.Id == "folder-compare");
        try
        {
            await catalog.LaunchAsync(firstEntry);
            throw new InvalidOperationException("An uninstalled integrated app launched through its provider.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "설치 후 실행할 수 있습니다.")
        {
            Assert(provider.Launches.Count == 0,
                "An uninstalled integrated app reached the fake launcher provider.");
        }
        await catalog.InstallAsync(firstEntry);
        Assert(firstEntry.TryGetInstalledExecutablePath() is not null,
            "Integrated install did not create its per-app receipt.");
        Assert(entries.Where(entry => entry.Id != firstEntry.Id).All(entry => entry.TryGetInstalledExecutablePath() is null),
            "Installing one integrated app marked another app installed.");
        foreach (var entry in entries.Where(entry => entry.Id != firstEntry.Id))
            await catalog.InstallAsync(entry);

        var versions = entries.Select(entry => File.ReadAllText(Path.Combine(appsBase, entry.Id, "current.version")).Trim()).ToArray();
        Assert(versions.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
            "Integrated apps did not reference the same immutable host snapshot.");
        var sharedDirectory = Path.Combine(appsBase, "_shared-payloads");
        Assert(Directory.EnumerateDirectories(sharedDirectory, "*", SearchOption.TopDirectoryOnly).Count() == 1,
            "Integrated installs created duplicate host snapshots.");
        foreach (var entry in entries)
            Assert(File.Exists(Path.Combine(startMenu, entry.Name + " (" + entry.Id + ").lnk")),
                $"Integrated install did not create the '{entry.Name}' shortcut.");

        var sharedSnapshot = Path.Combine(sharedDirectory, versions[0]);
        var hostFileName = Path.GetFileName(Environment.ProcessPath
            ?? throw new InvalidOperationException("The check process path is unavailable."));
        foreach (var entry in entries)
        {
            var shortcut = ReadShortcut(Path.Combine(startMenu, entry.Name + " (" + entry.Id + ").lnk"));
            AssertPath(Path.Combine(sharedSnapshot, hostFileName), shortcut.TargetPath,
                $"Integrated shortcut for '{entry.Id}' did not target the shared host snapshot");
            Assert(shortcut.Arguments == "--app " + entry.Id,
                $"Integrated shortcut for '{entry.Id}' omitted its app selector.");
        }
        var fileCountBeforeRepeat = Directory.EnumerateFiles(sharedSnapshot, "*", SearchOption.AllDirectories).Count();
        await catalog.InstallAsync(firstEntry);
        Assert(Directory.EnumerateDirectories(sharedDirectory, "*", SearchOption.TopDirectoryOnly).Count() == 1
               && Directory.EnumerateFiles(sharedSnapshot, "*", SearchOption.AllDirectories).Count() == fileCountBeforeRepeat,
            "Repeating an integrated install duplicated or mutated the shared snapshot.");

        await catalog.LaunchAsync(firstEntry);
        catalog.Launch(firstEntry);
        Assert(provider.Launches.SequenceEqual(["folder-compare", "folder-compare"]),
            "Integrated launch did not route exclusively through the provider.");

        Assert(firstEntry.CanUninstall, "Installed integrated app did not expose managed uninstall.");
        await catalog.UninstallAsync(firstEntry);
        Assert(!File.Exists(Path.Combine(appsBase, firstEntry.Id, "current.version")) &&
               !File.Exists(Path.Combine(startMenu, firstEntry.Name + " (" + firstEntry.Id + ").lnk")),
            "Uninstall did not remove the selected integrated app's receipt and shortcut.");
        Assert(entries.Where(entry => entry.Id != firstEntry.Id)
                .All(entry => File.Exists(Path.Combine(appsBase, entry.Id, "current.version")) && entry.CanUninstall),
            "Uninstalling one integrated app removed another app's per-app installation state.");
        Assert(Directory.Exists(sharedSnapshot) && File.Exists(Path.Combine(sharedSnapshot, hostFileName)),
            "Uninstall removed the shared host payload needed by other apps or future launches.");
        Assert(!firstEntry.CanUninstall, "Removed integrated app still reports a managed installation.");
        await catalog.UninstallAsync(firstEntry);
    });

    await Check("offline bundles read app versions from their own executable metadata", () =>
    {
        var root = CreateRoot("offline bundle app version");
        File.WriteAllText(Path.Combine(root, "i-luncher.json"),
            "{\"schemaVersion\":1,\"distribution\":{\"version\":\"test-release-tag\",\"runtime\":\"runtime\",\"framework\":\"net10.0\"}}");
        var app = Path.Combine(root, "Bundled Version App");
        string executable = CopyCheckHost(app);
        WriteManifest(app, new { id = "bundled-version-app", name = "Bundled Version App", executable = "Launcher.Checks.exe" });
        var entry = new LauncherCatalog(root, CreateDirectory("offline version installed apps"),
            CreateDirectory("offline version menu")).Load().Entries.Single();
        var binary = FileVersionInfo.GetVersionInfo(executable);
        string? expectedVersion = !string.IsNullOrWhiteSpace(binary.ProductVersion)
            ? binary.ProductVersion.Trim() : binary.FileVersion?.Trim();
        Assert(expectedVersion is not null && entry.BundledVersion == expectedVersion
               && entry.BundledVersion != "test-release-tag",
            "Offline bundle version did not come from the bundled executable's ProductVersion/FileVersion.");
        Assert(entry.InstalledVersion is null && entry.AvailableVersion is null,
            "A bundled local version was incorrectly treated as an installed receipt or online version.");
        return Task.CompletedTask;
    });

    await Check("distribution runs bundled apps and installs without source builds", async () =>
    {
        var root = CreateRoot("distribution 한글");
        File.WriteAllText(Path.Combine(root, "i-luncher.json"),
            """{"schemaVersion":1,"distribution":{"version":"test","runtime":"runtime","framework":"net10.0"}}""");
        var app = Path.Combine(root, "Bundled App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project />");
        var secondApp = Path.Combine(root, "Second Bundled App");
        Directory.CreateDirectory(secondApp);
        CopyCheckHost(secondApp);
        var runtimeFile = Path.Combine(root, "runtime", "host", "runtime-probe.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeFile)!);
        File.WriteAllText(runtimeFile, "shared runtime");
        var resultPath = Path.Combine(workspace, "distribution-result.json");
        WriteManifest(app, new
        {
            id = "bundled", name = "Bundled", executable = "Launcher.Checks.exe",
            build = new { project = "App.csproj" }, install = new { type = "portable" },
            arguments = new[] { "--launch-probe", resultPath, "bundle-smoke" }
        });
        WriteManifest(secondApp, new
        {
            id = "second-bundled", name = "Second Bundled", executable = "Launcher.Checks.exe",
            install = new { type = "portable" }
        });
        var appsBase = CreateDirectory("distribution installed apps");
        var startMenu = CreateDirectory("distribution installed menu");
        var catalog = new LauncherCatalog(root, appsBase, startMenu);
        var entries = catalog.Load().Entries;
        var entry = entries.Single(candidate => candidate.Id == "bundled");
        Assert(catalog.IsDistribution && entry.IsDistribution, "Distribution marker was ignored.");
        Assert(entries.Count == 2 && entries.All(candidate => !candidate.CanBuild && candidate.CanInstall)
               && !entry.IsAvailable,
            "Distribution should expose all bundle apps for install without source builds.");
        try
        {
            await catalog.LaunchAsync(entry);
            throw new Exception("Missing distribution app launched.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "설치 후 실행할 수 있습니다.")
        {
            Assert(!File.Exists(resultPath), "Rejected distribution launch started the fixture app.");
        }
        CopyCheckHost(app);
        await catalog.InstallAsync(entry);
        var secondEntry = entries.Single(candidate => candidate.Id == "second-bundled");
        await catalog.InstallAsync(secondEntry);
        await catalog.LaunchAsync(entry);
        var timer = Stopwatch.StartNew();
        while (!File.Exists(resultPath) && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        Assert(File.Exists(resultPath), "Installed bundled distribution app did not launch.");
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        var receipt = Path.Combine(appsBase, entry.Id, "current.version");
        Assert(File.Exists(receipt), "Distribution install did not write an app receipt.");
        var version = File.ReadAllText(receipt).Trim();
        Assert(File.ReadAllText(Path.Combine(appsBase, secondEntry.Id, "current.version")).Trim() == version,
            "Distribution apps did not reuse one immutable bundle snapshot.");
        var installedPayload = Path.Combine(appsBase, "_shared-payloads", version);
        Assert(File.Exists(Path.Combine(installedPayload, "Bundled App", "Launcher.Checks.exe")),
            "Distribution install did not copy the app from the shared bundle snapshot.");
        AssertPath(Path.Combine(installedPayload, "Bundled App"),
            result.RootElement.GetProperty("workingDirectory").GetString()!, "Distribution did not launch the installed copy");
        Assert(File.Exists(Path.Combine(installedPayload, "Second Bundled App", "Launcher.Checks.exe")),
            "Distribution install omitted the second app's relative executable path.");
        Assert(File.Exists(Path.Combine(installedPayload, Path.GetRelativePath(root, runtimeFile))),
            "Distribution install omitted the common runtime from the bundle snapshot.");
        Assert(Directory.EnumerateDirectories(Path.Combine(appsBase, "_shared-payloads"), "*", SearchOption.TopDirectoryOnly).Count() == 1,
            "Distribution apps created duplicate bundle snapshots.");
        Assert(File.Exists(Path.Combine(startMenu, "Bundled (bundled).lnk")) &&
               File.Exists(Path.Combine(startMenu, "Second Bundled (second-bundled).lnk")),
            "Distribution install did not create shortcuts for every app.");
        foreach (var installedEntry in entries)
        {
            var shortcut = ReadShortcut(Path.Combine(startMenu, installedEntry.Name + " (" + installedEntry.Id + ").lnk"));
            var expectedTarget = Path.GetFullPath(Path.GetRelativePath(root, installedEntry.ExecutablePath), installedPayload);
            AssertPath(expectedTarget, shortcut.TargetPath,
                $"Distribution shortcut for '{installedEntry.Id}' did not target its app in the shared bundle.");
            Assert(shortcut.Arguments == string.Join(" ", installedEntry.Arguments),
                $"Distribution shortcut for '{installedEntry.Id}' did not preserve its manifest arguments.");
        }
    });

    await Check("same-name distribution apps keep separate installed shortcuts", async () =>
    {
        var root = CreateRoot("duplicate shortcut bundle");
        File.WriteAllText(Path.Combine(root, "i-luncher.json"),
            "{\"schemaVersion\":1,\"distribution\":{\"version\":\"test\",\"runtime\":\"runtime\",\"framework\":\"net10.0\"}}");
        foreach (var (id, folder, argument) in new[]
        {
            ("same-one", "One", "--one"),
            ("same-two", "Two", "--two")
        })
        {
            var app = Path.Combine(root, folder);
            CopyCheckHost(app);
            WriteManifest(app, new
            {
                id,
                name = "Same Display Name",
                executable = "Launcher.Checks.exe",
                arguments = new[] { argument }
            });
        }

        var appsBase = CreateDirectory("duplicate shortcut apps");
        var startMenu = CreateDirectory("duplicate shortcut menu");
        var catalog = new LauncherCatalog(root, appsBase, startMenu);
        var entries = catalog.Load().Entries;
        Assert(entries.Count == 2 && entries.All(entry => entry.CanInstall),
            "Duplicate-name fixture did not load as two installable apps.");
        foreach (var entry in entries) await catalog.InstallAsync(entry);

        var shortcuts = Directory.EnumerateFiles(startMenu, "*.lnk", SearchOption.TopDirectoryOnly)
            .Select(ReadShortcut).ToArray();
        Assert(shortcuts.Length == 2, "Installing same-name apps did not retain two shortcuts.");
        Assert(shortcuts.Select(shortcut => Path.GetFullPath(shortcut.TargetPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "Same-name shortcuts do not target both distinct app executables.");
        foreach (var entry in entries)
        {
            var shortcut = shortcuts.Single(candidate => candidate.Arguments == string.Join(" ", entry.Arguments));
            var expected = Path.GetFullPath(Path.Combine(appsBase, "_shared-payloads",
                File.ReadAllText(Path.Combine(appsBase, entry.Id, "current.version")).Trim(),
                Path.GetRelativePath(root, entry.ExecutablePath)));
            AssertPath(expected, shortcut.TargetPath, $"Shortcut for '{entry.Id}' targets the wrong executable.");
        }
    });

    await Check("installed shortcut icon survives removal of its source tree", async () =>
    {
        var root = CreateRoot("shortcut icon source");
        var app = Path.Combine(root, "Icon App");
        var publish = Path.Combine(app, "dist", "win-x64");
        CopyCheckHost(publish);
        var icon = Path.Combine(app, "assets", "app.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null
               && !File.Exists(Path.Combine(repository.FullName, "Launcher", "Assets", "ILauncher.ico")))
            repository = repository.Parent;
        Assert(repository is not null, "Could not locate the checked-in icon fixture.");
        File.Copy(Path.Combine(repository!.FullName, "Launcher", "Assets", "ILauncher.ico"), icon);
        WriteManifest(app, new
        {
            id = "icon-app",
            name = "Icon App",
            executable = "dist/win-x64/Launcher.Checks.exe",
            icon = "assets/app.ico",
            install = new { type = "portable" }
        });
        var appsBase = CreateDirectory("shortcut icon apps");
        var startMenu = CreateDirectory("shortcut icon menu");
        var catalog = new LauncherCatalog(root, appsBase, startMenu);
        var entry = catalog.Load().Entries.Single();
        await catalog.InstallAsync(entry);

        var shortcut = ReadShortcut(Path.Combine(startMenu, "Icon App (icon-app).lnk"));
        var iconPathText = shortcut.IconLocation.Split(',')[0].Trim().Trim('"');
        Assert(!string.IsNullOrWhiteSpace(iconPathText), "Installed shortcut has no icon location.");
        var installedIcon = Path.GetFullPath(iconPathText);
        Assert(!installedIcon.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            "Installed shortcut icon still depends on the source tree.");
        Assert(File.Exists(installedIcon), "Installed shortcut icon file is missing before source removal.");
        Directory.Delete(root, recursive: true);
        Assert(File.Exists(installedIcon), "Installed shortcut icon stopped resolving after source removal.");
    });

    await Check("launch rejects an installed working-directory junction", async () =>
    {
        var root = CreateRoot("installed cwd junction source");
        var app = Path.Combine(root, "Junction App");
        var publish = Path.Combine(app, "dist", "win-x64");
        CopyCheckHost(publish);
        var workingDirectory = Path.Combine(publish, "work");
        Directory.CreateDirectory(workingDirectory);
        var outside = CreateDirectory("installed cwd junction target");
        var probe = Path.Combine(outside, "launch-probe.json");
        WriteManifest(app, new
        {
            id = "junction-app",
            name = "Junction App",
            executable = "dist/win-x64/Launcher.Checks.exe",
            workingDirectory = "dist/win-x64/work",
            arguments = new[] { "--launch-probe", probe },
            install = new { type = "portable" }
        });
        var appsBase = CreateDirectory("installed cwd junction apps");
        var startMenu = CreateDirectory("installed cwd junction menu");
        var catalog = new LauncherCatalog(root, appsBase, startMenu);
        var entry = catalog.Load().Entries.Single();
        await catalog.InstallAsync(entry);
        var receipt = Path.Combine(appsBase, entry.Id);
        var version = File.ReadAllText(Path.Combine(receipt, "current.version")).Trim();
        var installedWorkingDirectory = Path.Combine(receipt, version, "work");
        Directory.Delete(installedWorkingDirectory);
        var junctionCommand = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{installedWorkingDirectory}\" \"{outside}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (var junctionProcess = Process.Start(junctionCommand)
               ?? throw new InvalidOperationException("Could not start the junction fixture helper."))
        {
            var output = junctionProcess.StandardOutput.ReadToEnd();
            var error = junctionProcess.StandardError.ReadToEnd();
            junctionProcess.WaitForExit();
            Assert(junctionProcess.ExitCode == 0,
                $"Could not create the temp working-directory junction: {output} {error}");
        }
        Assert((File.GetAttributes(installedWorkingDirectory) & FileAttributes.ReparsePoint) != 0,
            "The test fixture did not create a working-directory reparse point.");

        try
        {
            await catalog.LaunchAsync(entry);
            throw new InvalidOperationException("Launch followed an installed working-directory junction.");
        }
        catch (InvalidDataException)
        {
        }
        Assert(!File.Exists(probe), "The app started before its installed working-directory junction was rejected.");
    });

    await Check("shared install writes receipts only after shortcut completion and rejects reparse sources", async () =>
    {
        var source = CreateDirectory("shared atomic source");
        WriteFile(Path.Combine("shared atomic source", "host.exe"), "host payload");
        var appsBase = CreateDirectory("shared atomic apps");
        var startMenuBlocker = WriteFile("shared atomic start menu blocker", "not a directory");
        var entry = new LauncherEntry
        {
            Id = "shared-atomic",
            Name = "Shared Atomic",
            IsIntegrated = true,
            ExecutablePath = Path.Combine(source, "host.exe"),
            WorkingDirectory = source,
            InstallDefinition = new LauncherInstallDefinition(LauncherInstallType.SharedPayload, null, []),
            InstallSourceDirectory = source,
            InstalledSharedPayloadDirectory = Path.Combine(appsBase, "_shared-payloads"),
            InstalledAppDirectory = Path.Combine(appsBase, "shared-atomic"),
            InstalledExecutableRelativePath = "host.exe",
            InstalledWorkingDirectoryRelativePath = ".",
            InstallShortcutArguments = ["--app", "shared-atomic"]
        };
        var failingCatalog = new LauncherCatalog(source, appsBase, startMenuBlocker);
        try
        {
            await failingCatalog.InstallAsync(entry);
            throw new InvalidOperationException("Shared install unexpectedly succeeded with an unusable Start Menu path.");
        }
        catch (IOException)
        {
        }

        var sharedStore = Path.Combine(appsBase, "_shared-payloads");
        var completedSnapshots = Directory.EnumerateDirectories(sharedStore, "*", SearchOption.TopDirectoryOnly).ToArray();
        Assert(completedSnapshots.Length == 1
               && File.Exists(Path.Combine(completedSnapshots[0], ".install-complete"))
               && File.Exists(Path.Combine(completedSnapshots[0], "host.exe")),
            "Shared snapshot was marked complete before its full payload had been promoted.");
        Assert(!File.Exists(Path.Combine(appsBase, entry.Id, "current.version")),
            "A failed shared install wrote the per-app receipt before its shortcut completed.");
        Assert(!Directory.EnumerateDirectories(sharedStore, ".staging-*", SearchOption.TopDirectoryOnly).Any(),
            "Failed shared install left a staging directory behind.");

        var startMenu = CreateDirectory("shared atomic start menu");
        await new LauncherCatalog(source, appsBase, startMenu).InstallAsync(entry);
        Assert(File.Exists(Path.Combine(appsBase, entry.Id, "current.version"))
               && Directory.EnumerateDirectories(sharedStore, "*", SearchOption.TopDirectoryOnly).Count() == 1,
            "Retry did not reuse the completed shared payload and write its receipt.");

        var reparseSource = CreateDirectory("shared reparse source");
        WriteFile(Path.Combine("shared reparse source", "host.exe"), "host payload");
        var outsidePayload = WriteFile("shared reparse target/secret.txt", "must not be copied");
        bool reparseCreated = false;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(reparseSource, "linked"), Path.GetDirectoryName(outsidePayload)!);
            reparseCreated = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                                   or System.ComponentModel.Win32Exception)
        {
            // Reparse point creation may be disabled by the host; the atomic receipt assertions remain mandatory.
        }
        if (reparseCreated)
        {
            var reparseEntry = new LauncherEntry
            {
                Id = "shared-reparse",
                Name = "Shared Reparse",
                IsIntegrated = true,
                ExecutablePath = Path.Combine(reparseSource, "host.exe"),
                WorkingDirectory = reparseSource,
                InstallDefinition = new LauncherInstallDefinition(LauncherInstallType.SharedPayload, null, []),
                InstallSourceDirectory = reparseSource,
                InstalledSharedPayloadDirectory = Path.Combine(appsBase, "_shared-payloads"),
                InstalledAppDirectory = Path.Combine(appsBase, "shared-reparse"),
                InstalledExecutableRelativePath = "host.exe",
                InstalledWorkingDirectoryRelativePath = ".",
                InstallShortcutArguments = ["--app", "shared-reparse"]
            };
            try
            {
                await new LauncherCatalog(reparseSource, appsBase, startMenu).InstallAsync(reparseEntry);
                throw new InvalidOperationException("Shared install followed a reparse point in its source tree.");
            }
            catch (IOException)
            {
            }
            Assert(!File.Exists(Path.Combine(appsBase, reparseEntry.Id, "current.version"))
                   && !Directory.EnumerateFiles(sharedStore, "secret.txt", SearchOption.AllDirectories).Any(),
                "Reparse source created an app receipt or copied external files.");
        }
    });

    await Check("marker root discovery and immediate-child manifests", () =>
    {
        var root = CreateRoot("root discovery 한글");
        var start = CreateDirectory(Path.Combine("root discovery 한글", "deep", "deeper"));
        var findRoot = typeof(LauncherCatalog).GetMethod("FindRoot", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(LauncherCatalog).FullName, "FindRoot");
        var discovered = (string?)findRoot.Invoke(null, [start]);
        Assert(discovered is not null, "FindRoot returned null.");
        AssertPath(root, discovered!, "FindRoot did not locate the ancestor marker");

        WriteManifest(Path.Combine(root, "Direct App"), new
        {
            id = "direct",
            name = "Direct",
            executable = "Direct.exe"
        });
        WriteManifest(Path.Combine(root, "Container", "Nested App"), new
        {
            id = "nested",
            name = "Nested",
            executable = "Nested.exe"
        });

        var result = new LauncherCatalog(discovered!).Load();
        Assert(result.Errors.Count == 0, "Unexpected catalog errors: " + string.Join(" | ", result.Errors));
        Assert(result.Entries.Select(entry => entry.Id).SequenceEqual(["direct"]),
            "A nested manifest was discovered, or the direct manifest was missed.");
        return Task.CompletedTask;
    });

    await Check("valid entries sort by order and then name", () =>
    {
        var root = CreateRoot("sorting");
        WriteManifest(Path.Combine(root, "one"), new { id = "zulu", name = "Zulu", executable = "z.exe", order = 20 });
        WriteManifest(Path.Combine(root, "two"), new { id = "alpha", name = "Alpha", executable = "a.exe", order = 20 });
        WriteManifest(Path.Combine(root, "three"), new { id = "first", name = "First", executable = "f.exe", order = 1 });

        var result = new LauncherCatalog(root).Load();
        Assert(result.Errors.Count == 0, "Unexpected catalog errors: " + string.Join(" | ", result.Errors));
        Assert(result.Entries.Select(entry => entry.Id).SequenceEqual(["first", "alpha", "zulu"]),
            "Catalog order differs from order/name sorting.");
        return Task.CompletedTask;
    });

    await Check("malformed and type-invalid manifests do not hide valid apps", () =>
    {
        var root = CreateRoot("malformed isolation");
        WriteManifest(Path.Combine(root, "00-valid"), new { id = "valid", name = "Still visible", executable = "valid.exe" });
        WriteFile(Path.Combine("malformed isolation", "01-bad-json", "launcher.app.json"), "{ this is not json");
        WriteManifest(Path.Combine(root, "02-arguments-type"), new { id = "bad-args", name = "Bad args", executable = "bad.exe", arguments = "not-an-array" });
        WriteManifest(Path.Combine(root, "03-order-type"), new { id = "bad-order", name = "Bad order", executable = "bad.exe", order = "first" });
        WriteManifest(Path.Combine(root, "04-name-type"), new { id = "bad-name", name = 123, executable = "bad.exe" });

        var result = new LauncherCatalog(root).Load();
        Assert(result.Entries.Select(entry => entry.Id).SequenceEqual(["valid"]), "The valid app was hidden or an invalid app was accepted.");
        Assert(result.Errors.Count == 4, $"Expected four isolated errors, got {result.Errors.Count}: {string.Join(" | ", result.Errors)}");
        foreach (var directory in new[] { "01-bad-json", "02-arguments-type", "03-order-type", "04-name-type" })
            Assert(result.Errors.Any(error => error.StartsWith(directory + ":", StringComparison.Ordinal)), $"No error identified {directory}.");
        return Task.CompletedTask;
    });

    await Check("duplicate ids are rejected case-insensitively", () =>
    {
        var root = CreateRoot("duplicate ids");
        WriteManifest(Path.Combine(root, "01-first"), new { id = "Example.App", name = "First", executable = "first.exe" });
        WriteManifest(Path.Combine(root, "02-second"), new { id = "example.app", name = "Second", executable = "second.exe" });

        var result = new LauncherCatalog(root).Load();
        Assert(result.Entries.Count == 1 && result.Entries[0].Name == "First", "The first duplicate was not retained exclusively.");
        Assert(result.Errors.Count == 1 && result.Errors[0].StartsWith("02-second:", StringComparison.Ordinal),
            "The duplicate manifest did not produce a directory-specific error.");
        return Task.CompletedTask;
    });

    await Check("missing executable reports availability and a descriptive launch failure", () =>
    {
        var root = CreateRoot("missing executable");
        WriteManifest(Path.Combine(root, "Missing App"), new { id = "missing", name = "Missing", executable = "bin/missing.exe" });
        var entry = new LauncherCatalog(root).Load().Entries.Single();
        Assert(!entry.IsAvailable, "A missing executable was reported as available.");

        try
        {
            new LauncherCatalog(root).Launch(entry);
            throw new InvalidOperationException("Launch accepted a missing executable.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "설치 후 실행할 수 있습니다.")
        {
            Assert(!File.Exists(entry.ExecutablePath),
                "The missing-app rejection unexpectedly created a launchable executable.");
        }
        return Task.CompletedTask;
    });

    await Check("source-only apps can run and invalid build manifests are isolated", () =>
    {
        var root = CreateRoot("build configuration");
        WriteFile("build configuration/01-source/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        WriteManifest(Path.Combine(root, "01-source"), new
        {
            id = "source", name = "Source", executable = "dist/App.exe",
            build = new { project = "App.csproj" }
        });
        WriteManifest(Path.Combine(root, "02-no-source"), new
        {
            id = "no-source", name = "No source", executable = "dist/App.exe",
            build = new { project = "Missing.csproj" }
        });
        WriteManifest(Path.Combine(root, "03-malformed"), new
        {
            id = "malformed", name = "Bad", executable = "dist/App.exe", build = "bad"
        });
        WriteManifest(Path.Combine(root, "04-traversal"), new
        {
            id = "traversal", name = "Bad", executable = "dist/App.exe",
            build = new { project = "../outside.csproj" }
        });
        var result = new LauncherCatalog(root).Load();
        Assert(result.Errors.Count == 2, "Malformed build settings were not isolated: " + string.Join(" | ", result.Errors));
        var source = result.Entries.Single(entry => entry.Id == "source");
        Assert(source.CanBuild && source.CanInstall && source.IsAvailable && !File.Exists(source.ExecutablePath),
            "Source-only app without an install section must offer build and install actions without building during catalog load.");
        Assert(!result.Entries.Single(entry => entry.Id == "no-source").IsAvailable,
            "App with neither source nor executable is shown as runnable.");
        Assert(!Directory.Exists(Path.Combine(root, "01-source", "dist")), "Catalog loading triggered a build.");
        return Task.CompletedTask;
    });

    if (args.Contains("--auto-build", StringComparer.OrdinalIgnoreCase))
    await Check("missing exe builds outside source, launches with arguments, reuses output and reports failure", async () =>
    {
        var root = CreateRoot("automatic build 한글 space");
        var productionRoot = new LauncherCatalog().RootDirectory;
        foreach (var script in new[] { "Build.ps1", "BuildWorkspace.ps1" })
            File.Copy(Path.Combine(productionRoot, script), Path.Combine(root, script));
        var toolsDirectory = Path.Combine(productionRoot, "Tools");
        foreach (var sourceTool in Directory.EnumerateFiles(toolsDirectory, "*", SearchOption.AllDirectories))
        {
            var relativeTool = Path.GetRelativePath(toolsDirectory, sourceTool);
            var destinationTool = Path.Combine(root, "Tools", relativeTool);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationTool)!);
            File.Copy(sourceTool, destinationTool);
        }
        var app = Path.Combine(root, "Probe App");
        var resultPath = Path.Combine(root, "probe-result.json");
        WriteFile("automatic build 한글 space/Probe App/Probe.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        var programPath = WriteFile("automatic build 한글 space/Probe App/Program.cs", """
            using System.Text.Json;
            File.WriteAllText(args[0], JsonSerializer.Serialize(new { arguments = args.Skip(1).ToArray(), workingDirectory = Environment.CurrentDirectory }));
            """);
        WriteManifest(app, new
        {
            id = "probe", name = "Automatic Probe", executable = "dist/win-x64/Probe.exe",
            arguments = new[] { resultPath, "두 단어", "quoted \"value\"" },
            build = new { project = "Probe.csproj" }
        });
        var normalized = Path.GetFullPath(root).ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        var cacheBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-luncher", "Workspaces");
        var cache = Path.GetFullPath(Path.Combine(cacheBase, hash[..24]));
        try
        {
            var appsBase = CreateDirectory("automatic build installed apps");
            var startMenu = CreateDirectory("automatic build installed menu");
            var catalog = new LauncherCatalog(root + Path.DirectorySeparatorChar, appsBase, startMenu);
            var entry = catalog.Load().Entries.Single();
            try
            {
                await catalog.LaunchAsync(entry);
                throw new InvalidOperationException("An uninstalled source app launched or built.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "설치 후 실행할 수 있습니다.")
            {
                Assert(!Directory.Exists(cache) && !File.Exists(resultPath),
                    "Rejected source launch built or started the app before installation.");
            }

            await catalog.InstallAsync(entry);
            await catalog.LaunchAsync(entry);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(resultPath) && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
            Assert(File.Exists(resultPath), "Installed, automatically built app did not run.");
            using (var result = JsonDocument.Parse(File.ReadAllText(resultPath)))
            {
                Assert(result.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString())
                    .SequenceEqual(["두 단어", "quoted \"value\""]), "Automatic launch changed argument boundaries.");
                var launchedInstallRoot = Path.Combine(appsBase, entry.Id);
                var launchedInstallVersion = File.ReadAllText(Path.Combine(launchedInstallRoot, "current.version")).Trim();
                AssertPath(Path.Combine(launchedInstallRoot, launchedInstallVersion),
                    result.RootElement.GetProperty("workingDirectory").GetString()!, "Auto-build launch did not use installed files");
            }
            Assert(!Directory.Exists(Path.Combine(app, "bin")) && !Directory.Exists(Path.Combine(app, "obj"))
                && !Directory.Exists(Path.Combine(app, "dist")), "Auto-build wrote generated files into source.");

            var cachedExecutable = Path.Combine(cache, "Probe App", "dist", "win-x64", "Probe.exe");
            Assert(File.Exists(cachedExecutable), "Automatic build did not leave reusable output in the external workspace.");
            var installEntry = entry;
            Assert(installEntry.CanInstall && installEntry.HasInstallFiles && installEntry.CanBuild,
                "A source-only app without an install section must offer portable install and build its output first.");
            await catalog.InstallAsync(installEntry);
            var installedApp = Path.Combine(appsBase, installEntry.Id);
            var installedVersion = File.ReadAllText(Path.Combine(installedApp, "current.version")).Trim();
            var installedDirectory = Path.Combine(installedApp, installedVersion);
            Assert(File.Exists(Path.Combine(installedDirectory, "Probe.exe"))
                   && File.Exists(Path.Combine(installedDirectory, ".install-complete")),
                "Source-only install did not build and promote the publish output.");
            Assert(!Directory.EnumerateFiles(installedDirectory, "*.csproj", SearchOption.AllDirectories).Any()
                   && !Directory.EnumerateFiles(installedDirectory, "Program.cs", SearchOption.AllDirectories).Any(),
                "Source-only install copied source files into the portable version.");
            Assert(!Directory.Exists(Path.Combine(app, "bin")) && !Directory.Exists(Path.Combine(app, "obj"))
                   && !Directory.Exists(Path.Combine(app, "dist")),
                "Source-only install wrote build output into the external source tree.");

            var cachedBuildTimestamp = File.GetLastWriteTimeUtc(cachedExecutable);
            var validSourceBeforeRepeat = File.ReadAllText(programPath);
            File.WriteAllText(programPath, "this deliberately cannot compile");
            await catalog.InstallAsync(installEntry);
            Assert(File.GetLastWriteTimeUtc(cachedExecutable) == cachedBuildTimestamp,
                "Repeat source-only install rebuilt an already cached output.");
            Assert(File.ReadAllText(Path.Combine(installedApp, "current.version")).Trim() == installedVersion,
                "Repeat source-only install did not reuse the completed portable version.");
            File.WriteAllText(programPath, validSourceBeforeRepeat);

            await Task.Delay(300);
            File.Delete(resultPath);
            var validSource = File.ReadAllText(programPath);
            File.WriteAllText(programPath, "this deliberately cannot compile");
            await catalog.LaunchAsync(entry);
            timer.Restart();
            while (!File.Exists(resultPath) && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
            Assert(File.Exists(resultPath), "Second launch rebuilt instead of using the cached executable.");
            await Task.Delay(300);
            File.Delete(Path.Combine(cache, "Probe App", "dist", "win-x64", "Probe.exe"));
            try
            {
                await catalog.InstallAsync(entry);
                throw new Exception("Invalid source was reported as a successful build.");
            }
            catch (LauncherBuildException ex)
            {
                Assert(ex.Message.Contains("실패", StringComparison.Ordinal) && ex.Message.Length < 13000,
                    "Build failure did not provide bounded diagnostics.");
            }
            File.WriteAllText(programPath, validSource);
            File.Delete(resultPath);
            var managedApps = CreateDirectory("managed workspace installed apps");
            var managedMenu = CreateDirectory("managed workspace installed menu");
            var managedCatalog = new LauncherCatalog(cache, managedApps, managedMenu);
            var managedEntry = managedCatalog.Load().Entries.Single();
            try
            {
                await managedCatalog.LaunchAsync(managedEntry);
                throw new InvalidOperationException("An uninstalled managed workspace app launched.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "설치 후 실행할 수 있습니다.")
            {
                Assert(!File.Exists(resultPath), "Rejected managed workspace launch started the app.");
            }
            await managedCatalog.InstallAsync(managedEntry);
            await managedCatalog.LaunchAsync(managedEntry);
            timer.Restart();
            while (!File.Exists(resultPath) && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
            Assert(File.Exists(resultPath), "Launch from the managed workspace did not rebuild and run.");
            Assert(!Directory.Exists(Path.Combine(app, "obj")), "Managed launch built in the original source.");
            await Task.Delay(300);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Assert(cache.StartsWith(Path.GetFullPath(cacheBase) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "Refusing cleanup outside test cache base.");
                using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(cache, ".i-luncher-workspace.json")));
                Assert(marker.RootElement.GetProperty("normalizedSourcePath").GetString() == normalized,
                    "Refusing cleanup of another source's workspace.");
                Directory.Delete(cache, recursive: true);
            }
        }
    });

    await Check("spaces, Unicode, and relative icon/working-directory paths resolve correctly", () =>
    {
        var root = CreateRoot("경로 root space");
        var app = Path.Combine(root, "한글 App Folder");
        WriteManifest(app, new
        {
            id = "unicode-paths",
            name = "유니코드 앱",
            executable = "bin 폴더/도구.exe",
            icon = "assets 아이콘/앱 그림.ico",
            workingDirectory = "작업 폴더/하위",
            arguments = new[] { "plain", "두 단어" }
        });
        var executable = WriteFile(Path.GetRelativePath(workspace, Path.Combine(app, "bin 폴더", "도구.exe")));
        var icon = WriteFile(Path.GetRelativePath(workspace, Path.Combine(app, "assets 아이콘", "앱 그림.ico")));
        var workingDirectory = CreateDirectory(Path.GetRelativePath(workspace, Path.Combine(app, "작업 폴더", "하위")));

        LauncherEntry entry = new LauncherCatalog(root).Load().Entries.Single();
        AssertPath(executable, entry.ExecutablePath, "Executable path resolution failed");
        AssertPath(icon, entry.IconPath, "Icon path resolution failed");
        AssertPath(workingDirectory, entry.WorkingDirectory, "Working-directory resolution failed");
        Assert(entry.IsAvailable, "The existing Unicode executable path was reported missing.");
        Assert(entry.Arguments.SequenceEqual(["plain", "두 단어"]), "Manifest arguments changed during loading.");
        return Task.CompletedTask;
    });

    await Check("process launch preserves argument boundaries and working directory", async () =>
    {
        var root = CreateRoot("launch boundary");
        var app = Path.Combine(root, "프로브 App space");
        CopyCheckHost(app);

        var helperExecutable = Path.Combine(app, "Launcher.Checks.exe");
        Assert(File.Exists(helperExecutable), $"Check apphost is unavailable at '{helperExecutable}'.");
        var workingDirectory = Path.Combine(app, "작업 폴더 with space", "하위");
        Directory.CreateDirectory(workingDirectory);
        var probeResult = Path.Combine(app, "결과 파일.json");
        var expectedArguments = new[] { "plain", "two words", "quote\"inside", "한글 인자 값", "trailing\\" };
        WriteManifest(app, new
        {
            id = "launch-probe",
            name = "Launch probe",
            executable = "Launcher.Checks.exe",
            workingDirectory = "작업 폴더 with space/하위",
            install = new { type = "portable" },
            arguments = new[] { "--launch-probe", probeResult }.Concat(expectedArguments).ToArray()
        });

        var appsBase = CreateDirectory("launch boundary installed apps");
        var startMenu = CreateDirectory("launch boundary installed menu");
        var catalog = new LauncherCatalog(root, appsBase, startMenu);
        var load = catalog.Load();
        Assert(load.Errors.Count == 0, "Unexpected launch-probe catalog error: " + string.Join(" | ", load.Errors));
        var entry = load.Entries.Single();
        try
        {
            await catalog.LaunchAsync(entry);
            throw new InvalidOperationException("An uninstalled loose executable launched.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "설치 후 실행할 수 있습니다.")
        {
            Assert(!File.Exists(probeResult), "Rejected launch started the fixture process.");
        }
        await catalog.InstallAsync(entry);
        catalog.Launch(entry);

        var timeout = Stopwatch.StartNew();
        while (!File.Exists(probeResult) && timeout.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(25);
        Assert(File.Exists(probeResult), "The launched helper did not write its result within ten seconds.");

        using var document = JsonDocument.Parse(File.ReadAllText(probeResult));
        var actualArguments = document.RootElement.GetProperty("arguments").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        var actualWorkingDirectory = document.RootElement.GetProperty("workingDirectory").GetString()
            ?? throw new InvalidOperationException("The helper omitted its working directory.");
        Assert(actualArguments.SequenceEqual(expectedArguments),
            $"Argument boundaries changed. Actual: {JsonSerializer.Serialize(actualArguments)}");
        var installedVersion = File.ReadAllText(Path.Combine(appsBase, entry.Id, "current.version")).Trim();
        AssertPath(Path.Combine(appsBase, entry.Id, installedVersion, Path.GetRelativePath(app, workingDirectory)),
            actualWorkingDirectory, "The installed process received the wrong working directory");
    });

    await Check("portable install copies an immutable version, creates a shortcut, and launches installed copy", async () =>
    {
        var root = CreateRoot("portable install");
        var app = Path.Combine(root, "Portable App");
        var publish = Path.Combine(app, "dist", "win-x64");
        var sourceExecutable = CopyCheckHost(publish);
        var companion = Path.Combine(publish, "payload.txt");
        File.WriteAllText(companion, "original payload");
        File.WriteAllText(Path.Combine(publish, "deskcal.db"), "must not be copied");
        File.WriteAllText(Path.Combine(publish, "deskcal.db-wal"), "must not be copied");
        File.WriteAllText(Path.Combine(publish, "record.migrating-old"), "must not be copied");
        var probeResult = Path.Combine(workspace, "portable-launch-result.json");
        WriteManifest(app, new
        {
            id = "portable-probe",
            name = "Portable Probe",
            executable = "dist/win-x64/Launcher.Checks.exe",
            arguments = new[] { "--launch-probe", probeResult, "installed argument" },
            install = new { type = "portable" }
        });

        var appsBase = CreateDirectory("portable user apps");
        var startMenu = Path.Combine(workspace, "portable start menu");
        var catalog = new LauncherCatalog(root, appsBase, startMenu);
        var load = catalog.Load();
        Assert(load.Errors.Count == 0, "Unexpected portable manifest error: " + string.Join(" | ", load.Errors));
        var entry = load.Entries.Single();
        Assert(entry.CanInstall, "Portable install configuration was not exposed through CanInstall.");
        var sourceBytes = File.ReadAllBytes(sourceExecutable);

        var message = await catalog.InstallAsync(entry);
        Assert(message.Contains(entry.Name, StringComparison.Ordinal), "Portable success message did not identify the app.");
        Assert(File.Exists(sourceExecutable) && File.ReadAllBytes(sourceExecutable).SequenceEqual(sourceBytes),
            "Portable installation changed or removed the bundled executable.");
        Assert(File.ReadAllText(companion) == "original payload", "Portable installation changed the bundled payload.");

        var installedApp = Path.Combine(appsBase, entry.Id);
        var version = File.ReadAllText(Path.Combine(installedApp, "current.version")).Trim();
        var versionDirectory = Path.Combine(installedApp, version);
        Assert(version.Length == 64, "Installed version pointer is not a SHA-256 value.");
        Assert(File.Exists(Path.Combine(versionDirectory, ".install-complete")), "Completed portable install has no completion marker.");
        Assert(File.Exists(Path.Combine(versionDirectory, "Launcher.Checks.exe")), "Portable executable was not copied.");
        Assert(!File.Exists(Path.Combine(versionDirectory, "deskcal.db")) &&
               !File.Exists(Path.Combine(versionDirectory, "deskcal.db-wal")) &&
               !File.Exists(Path.Combine(versionDirectory, "record.migrating-old")),
            "Runtime database or migration artifacts were copied into the portable install.");
        Assert(File.Exists(Path.Combine(startMenu, "Portable Probe (portable-probe).lnk")), "Start Menu shortcut was not created.");
        Assert(entry.IsAvailable, "Completed portable installation was not reported as available.");

        catalog.Launch(entry);
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(probeResult) && timeout.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
        Assert(File.Exists(probeResult), "Installed portable executable did not launch.");
        using var document = JsonDocument.Parse(File.ReadAllText(probeResult));
        var launchedWorkingDirectory = document.RootElement.GetProperty("workingDirectory").GetString()!;
        AssertPath(versionDirectory, launchedWorkingDirectory, "Launch did not prefer the installed portable version");

        var currentBeforeFailedUpdate = File.ReadAllText(Path.Combine(installedApp, "current.version"));
        var lockedUpdate = Path.Combine(publish, "locked-update.txt");
        File.WriteAllText(lockedUpdate, "new version");
        using (File.Open(lockedUpdate, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                await catalog.InstallAsync(entry);
                throw new InvalidOperationException("Portable update unexpectedly copied an exclusively locked source file.");
            }
            catch (IOException)
            {
            }
        }
        Assert(File.ReadAllText(Path.Combine(installedApp, "current.version")) == currentBeforeFailedUpdate,
            "Failed portable update changed the active version pointer.");
        Assert(File.Exists(Path.Combine(versionDirectory, ".install-complete")),
            "Failed portable update damaged the previous completed version.");
        Assert(!Directory.EnumerateDirectories(installedApp, ".staging-*", SearchOption.TopDirectoryOnly).Any(),
            "Failed portable update left a staging directory behind.");
    });

    await Check("portable uninstall rejects tampering and preserves payload, data, and other apps", async () =>
    {
        var root = CreateRoot("portable uninstall");
        var firstApp = Path.Combine(root, "First App");
        var firstPublish = Path.Combine(firstApp, "dist", "win-x64");
        CopyCheckHost(firstPublish);
        WriteManifest(firstApp, new
        {
            id = "uninstall-first",
            name = "Uninstall First",
            executable = "dist/win-x64/Launcher.Checks.exe",
            install = new { type = "portable" }
        });
        var secondApp = Path.Combine(root, "Second App");
        var secondPublish = Path.Combine(secondApp, "dist", "win-x64");
        CopyCheckHost(secondPublish);
        WriteManifest(secondApp, new
        {
            id = "uninstall-second",
            name = "Uninstall Second",
            executable = "dist/win-x64/Launcher.Checks.exe",
            install = new { type = "portable" }
        });

        var appsBase = CreateDirectory("portable uninstall apps");
        var startMenu = CreateDirectory("portable uninstall menu");
        var catalog = new LauncherCatalog(root, appsBase, startMenu);
        var entries = catalog.Load().Entries.ToDictionary(entry => entry.Id);
        var first = entries["uninstall-first"];
        var second = entries["uninstall-second"];
        Assert(!first.CanUninstall && !second.CanUninstall, "Uninstalled portable apps reported uninstall readiness.");
        await catalog.InstallAsync(first);
        await catalog.InstallAsync(second);
        Assert(first.CanUninstall && second.CanUninstall, "Installed portable apps did not expose uninstall readiness.");

        string firstInstall = Path.Combine(appsBase, first.Id);
        string secondInstall = Path.Combine(appsBase, second.Id);
        string firstVersion = File.ReadAllText(Path.Combine(firstInstall, "current.version")).Trim();
        string secondVersion = File.ReadAllText(Path.Combine(secondInstall, "current.version")).Trim();
        string firstPayload = Path.Combine(firstInstall, firstVersion);
        string preservedData = Path.Combine(firstPayload, "user-data.txt");
        File.WriteAllText(preservedData, "keep user data");
        string firstShortcut = Path.Combine(startMenu, "Uninstall First (uninstall-first).lnk");
        string secondShortcut = Path.Combine(startMenu, "Uninstall Second (uninstall-second).lnk");

        string shortcutReceipt = Path.Combine(firstInstall, "shortcut.name");
        string safeReceipt = File.ReadAllText(shortcutReceipt);
        File.WriteAllText(shortcutReceipt, "..\\outside.lnk");
        try
        {
            await catalog.UninstallAsync(first);
            throw new InvalidOperationException("Uninstall accepted a path-traversing shortcut receipt.");
        }
        catch (InvalidDataException)
        {
        }
        Assert(File.Exists(Path.Combine(firstInstall, "current.version")) && File.Exists(firstShortcut),
            "Rejected shortcut receipt tampering changed the installation.");
        File.WriteAllText(shortcutReceipt, safeReceipt);

        SetShortcutTarget(firstShortcut, Path.Combine(workspace, "unowned-target.exe"));
        try
        {
            await catalog.UninstallAsync(first);
            throw new InvalidOperationException("Uninstall accepted a shortcut that targets another file.");
        }
        catch (InvalidDataException)
        {
        }
        Assert(File.Exists(Path.Combine(firstInstall, "current.version")) && File.Exists(firstShortcut),
            "Rejected shortcut tampering changed the installation.");

        SetShortcutTarget(firstShortcut, first.TryGetInstalledExecutablePath()
            ?? throw new InvalidOperationException("The installed executable disappeared during tamper checks."));
        using (File.Open(Path.Combine(firstInstall, "current.version"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                await catalog.UninstallAsync(first);
                throw new InvalidOperationException("Uninstall unexpectedly removed a locked receipt.");
            }
            catch (IOException)
            {
            }
        }
        Assert(File.Exists(Path.Combine(firstInstall, "current.version")) && File.Exists(firstShortcut),
            "A failed uninstall did not restore its receipt and shortcut.");
        await catalog.UninstallAsync(first);
        Assert(!File.Exists(Path.Combine(firstInstall, "current.version")) && !File.Exists(firstShortcut),
            "Portable uninstall left the selected app receipt or shortcut behind.");
        Assert(Directory.Exists(firstPayload) && File.Exists(preservedData) &&
               File.ReadAllText(preservedData) == "keep user data",
            "Portable uninstall removed installed payload files or user data.");
        Assert(File.Exists(Path.Combine(secondInstall, "current.version")) && File.Exists(secondShortcut) &&
               Directory.Exists(Path.Combine(secondInstall, secondVersion)),
            "Uninstalling one portable app affected another app's files or shortcut.");
        Assert(!first.CanUninstall && second.CanUninstall, "Uninstall readiness did not update per app.");
        await catalog.UninstallAsync(first);
    });

    await Check("failed portable receipt commit preserves the previous shortcut and version", async () =>
    {
        var root = CreateRoot("portable receipt rollback source");
        var app = Path.Combine(root, "Receipt Rollback App");
        var publish = Path.Combine(app, "dist", "win-x64");
        var sourceExecutable = CopyCheckHost(publish);
        WriteManifest(app, new
        {
            id = "receipt-rollback",
            name = "Receipt Rollback",
            executable = "dist/win-x64/Launcher.Checks.exe",
            install = new { type = "portable" }
        });
        var appsBase = CreateDirectory("portable receipt rollback apps");
        var startMenu = CreateDirectory("portable receipt rollback menu");
        var catalog = new LauncherCatalog(root, appsBase, startMenu);
        var entry = catalog.Load().Entries.Single();
        await catalog.InstallAsync(entry);

        var receipt = Path.Combine(appsBase, entry.Id, "current.version");
        var oldVersion = File.ReadAllText(receipt).Trim();
        var shortcutPath = Path.Combine(startMenu, "Receipt Rollback (receipt-rollback).lnk");
        var oldShortcut = ReadShortcut(shortcutPath);
        File.WriteAllText(Path.Combine(publish, "updated-payload.txt"), "version two");
        var updateFailed = false;
        using (File.Open(receipt, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                await catalog.InstallAsync(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                updateFailed = true;
            }
        }

        Assert(updateFailed, "Receipt lock did not force the version-pointer commit to fail.");
        Assert(File.ReadAllText(receipt).Trim() == oldVersion,
            "Failed update changed the previously active version receipt.");
        Assert(Directory.Exists(Path.Combine(appsBase, entry.Id, oldVersion)),
            "Failed update removed the previously active payload version.");
        var restoredShortcut = ReadShortcut(shortcutPath);
        AssertPath(oldShortcut.TargetPath, restoredShortcut.TargetPath,
            "Failed update did not restore the previous shortcut target");
        Assert(oldShortcut.Arguments == restoredShortcut.Arguments
               && oldShortcut.IconLocation == restoredShortcut.IconLocation,
            "Failed update did not preserve the previous shortcut arguments and icon.");
        Assert(File.Exists(sourceExecutable), "The rollback fixture unexpectedly changed its source executable.");
    });

    await Check("portable shortcut failure leaves a complete version without an active receipt", async () =>
    {
        var root = CreateRoot("portable failure");
        var app = Path.Combine(root, "Portable Failure App");
        var publish = Path.Combine(app, "dist", "win-x64");
        var sourceExecutable = CopyCheckHost(publish);
        WriteManifest(app, new
        {
            id = "portable-failure",
            name = "Portable Failure",
            executable = "dist/win-x64/Launcher.Checks.exe",
            install = new { type = "portable" }
        });
        var appsBase = CreateDirectory("portable failure apps");
        var startMenuFile = WriteFile("portable failure start menu blocker", "not a directory");
        var catalog = new LauncherCatalog(root, appsBase, startMenuFile);
        var entry = catalog.Load().Entries.Single();

        try
        {
            await catalog.InstallAsync(entry);
            throw new InvalidOperationException("Portable install unexpectedly succeeded with an unusable Start Menu path.");
        }
        catch (IOException)
        {
        }

        var installedApp = Path.Combine(appsBase, entry.Id);
        Assert(!File.Exists(Path.Combine(installedApp, "current.version")), "Failed install wrote an active version pointer.");
        var completedVersions = Directory.EnumerateDirectories(installedApp, "*", SearchOption.TopDirectoryOnly)
            .Where(directory => File.Exists(Path.Combine(directory, ".install-complete"))).ToArray();
        Assert(completedVersions.Length == 1
               && File.Exists(Path.Combine(completedVersions[0], "Launcher.Checks.exe")),
            "The failed shortcut install did not preserve its complete, reusable payload version.");
        Assert(File.Exists(sourceExecutable), "Failed install removed the bundled executable.");
        Assert(!entry.IsAvailable || File.Exists(entry.ExecutablePath), "Failed install created false installed availability.");
    });

    await Check("source install without explicit settings defaults to portable and malformed settings are isolated", () =>
    {
        var root = CreateRoot("install manifest validation");
        WriteManifest(Path.Combine(root, "00-none"), new
        {
            id = "no-install", name = "No install", executable = "app.exe", build = new { project = "App.csproj" }
        });
        WriteFile(Path.Combine("install manifest validation", "00-none", "App.csproj"), "<Project />");
        WriteFile(Path.Combine("install manifest validation", "00-none", "app.exe"), "portable host");
        WriteManifest(Path.Combine(root, "01-scalar"), new { id = "bad-scalar", name = "Bad", executable = "app.exe", install = "portable" });
        WriteManifest(Path.Combine(root, "02-type"), new { id = "bad-type", name = "Bad", executable = "app.exe", install = new { type = "archive" } });
        WriteManifest(Path.Combine(root, "03-arguments"), new
        {
            id = "bad-arguments", name = "Bad", executable = "app.exe",
            install = new { type = "command", executable = "setup.exe", arguments = "--install" }
        });
        WriteManifest(Path.Combine(root, "04-traversal"), new
        {
            id = "bad-traversal", name = "Bad", executable = "app.exe",
            install = new { type = "command", executable = "../outside.exe", arguments = Array.Empty<string>() }
        });

        var result = new LauncherCatalog(root).Load();
        Assert(result.Entries.Count == 1 && result.Entries[0].Id == "no-install", "Invalid install manifests were accepted or valid app was hidden.");
        Assert(result.Entries[0].CanBuild && result.Entries[0].CanInstall && result.Entries[0].HasInstallFiles
               && result.Entries[0].InstallDefinition?.Type == LauncherInstallType.Portable,
            "A source app with no install configuration must default to portable.");
        Assert(result.Errors.Count == 4, $"Expected four install manifest errors, got {result.Errors.Count}: {string.Join(" | ", result.Errors)}");
        return Task.CompletedTask;
    });

    await Check("command installer waits, preserves arguments, and reports nonzero exit", async () =>
    {
        var root = CreateRoot("command install");
        var app = Path.Combine(root, "Command App");
        var helperDirectory = Path.Combine(app, "dist");
        CopyCheckHost(helperDirectory);
        var successResult = Path.Combine(workspace, "command-success.json");
        WriteManifest(app, new
        {
            id = "command-probe",
            name = "Command Probe",
            executable = "dist/Launcher.Checks.exe",
            install = new
            {
                type = "command",
                executable = "dist/Launcher.Checks.exe",
                arguments = new[] { "--install-probe", successResult, "0", "300", "two words" }
            }
        });
        var catalog = new LauncherCatalog(root, CreateDirectory("command apps"), Path.Combine(workspace, "command menu"));
        var entry = catalog.Load().Entries.Single();
        var timer = Stopwatch.StartNew();
        await catalog.InstallAsync(entry);
        Assert(timer.ElapsedMilliseconds >= 250, "Command installer returned before its child process exited.");
        using (var document = JsonDocument.Parse(File.ReadAllText(successResult)))
            Assert(document.RootElement.GetProperty("arguments")[0].GetString() == "two words", "Command installer changed argument boundaries.");

        var failureResult = Path.Combine(workspace, "command-failure.json");
        WriteManifest(app, new
        {
            id = "command-probe",
            name = "Command Probe",
            executable = "dist/Launcher.Checks.exe",
            install = new
            {
                type = "command",
                executable = "dist/Launcher.Checks.exe",
                arguments = new[] { "--install-probe", failureResult, "7", "150" }
            }
        });
        entry = catalog.Load().Entries.Single();
        timer.Restart();
        try
        {
            await catalog.InstallAsync(entry);
            throw new InvalidOperationException("Nonzero command installer exit was reported as success.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("7", StringComparison.Ordinal))
        {
        }
        Assert(timer.ElapsedMilliseconds >= 100 && File.Exists(failureResult), "Command installer did not wait for the failing child process.");

        var cancelResult = Path.Combine(workspace, "command-cancel.json");
        WriteManifest(app, new
        {
            id = "command-probe",
            name = "Command Probe",
            executable = "dist/Launcher.Checks.exe",
            install = new
            {
                type = "command",
                executable = "dist/Launcher.Checks.exe",
                arguments = new[] { "--install-probe", cancelResult, "1223", "0" }
            }
        });
        entry = catalog.Load().Entries.Single();
        try
        {
            await catalog.InstallAsync(entry);
            throw new InvalidOperationException("Exit code 1223 was reported as installation success.");
        }
        catch (OperationCanceledException ex)
        {
            Assert(ex.Message.Contains("취소", StringComparison.Ordinal), "Cancellation did not return cancellation text.");
        }
    });
}
finally
{
    try
    {
        Directory.Delete(workspace, recursive: true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WARN Could not remove owned temporary workspace '{workspace}': {ex.Message}");
    }
}

Console.WriteLine($"{passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

internal sealed class FakeIntegratedAppProvider(params LauncherEntry[] entries) : IIntegratedAppProvider
{
    public IReadOnlyList<LauncherEntry> Entries { get; } = entries;
    public List<string> Launches { get; } = [];

    public Task LaunchAsync(string appId)
    {
        Launches.Add(appId);
        return Task.CompletedTask;
    }
}
