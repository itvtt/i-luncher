using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ILauncher.Bootstrapper;
using Microsoft.CSharp;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Linq;

internal static class Program
{
    private static int _passed;
    private static int Main(string[] args)
    {
        FrameworkCompatibility.Initialize();
        if (args != null && args.Length == 4 && String.Equals(args[0], "--payload-smoke", StringComparison.OrdinalIgnoreCase))
            return RunPayloadSmoke(args[1], args[2], args[3]).GetAwaiter().GetResult();
        string root = Path.Combine(Path.GetTempPath(), "iluncher-bootstrap-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestZipRejections(root);
            TestInstallCacheAndLaunch(root).GetAwaiter().GetResult();
            Console.WriteLine("Bootstrapper checks passed: " + _passed);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex);
            return 1;
        }
        finally
        {
            try { DeleteFixtureRoot(root); }
            catch { Console.Error.WriteLine("Fixture cleanup was incomplete: " + root); }
        }
    }

    private static async Task<int> RunPayloadSmoke(string catalogPath, string appId, string mirror)
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.Combine(tempRoot, "iluncher-payload-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string fullCatalog = Path.GetFullPath(catalogPath);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(fullCatalog, false);
            byte[] catalogBytes = File.ReadAllBytes(fullCatalog);
            if (catalogBytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Smoke catalog exceeds the configured bound.");
            string json = new UTF8Encoding(false, true).GetString(catalogBytes);
            BootstrapperOptions options = new BootstrapperOptions(json, Hash(catalogBytes), root,
                System.Reflection.Assembly.GetExecutingAssembly().Location)
            {
                PackageMirrorBase = mirror,
                StartMenuRoot = Path.Combine(root, "start-menu")
            };
            using (BootstrapperEngine engine = new BootstrapperEngine(options, new EnvironmentCredentialProvider()))
            {
                BootstrapperCatalog catalog = await engine.LoadCatalogAsync(CancellationToken.None);
                BootstrapperApp app = catalog.FindApp(appId);
                if (app == null) throw new ArgumentException("Selected app ID is absent from the catalog.");
                BootstrapperReadyApp first = await engine.EnsureReadyAsync(app.Id, CancellationToken.None);
                BootstrapperAppStatus firstStatus = engine.GetStatus(app.Id);
                if (!firstStatus.IsReady || firstStatus.IsInstalled || !File.Exists(first.ExecutablePath))
                    throw new InvalidDataException("Selected payload failed its ready-state smoke check.");
                string[] cachedApps = Directory.GetDirectories(Path.Combine(root, "apps"));
                string[] cachedRuntimes = Directory.GetDirectories(Path.Combine(root, "runtime"));
                if (cachedApps.Length != 1 || !String.Equals(Path.GetFileName(cachedApps[0]), app.Id, StringComparison.OrdinalIgnoreCase) ||
                    cachedRuntimes.Length != 1 || !String.Equals(Path.GetFileName(cachedRuntimes[0]), catalog.Runtime.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Payload smoke cached unrelated app or runtime packages.");
                BootstrapperInstallResult installed = await engine.InstallAsync(app.Id, CancellationToken.None);
                if (!File.Exists(installed.ReceiptPath) || !File.Exists(installed.ShortcutPath) ||
                    !engine.GetStatus(app.Id).IsInstalled)
                    throw new InvalidDataException("Selected payload failed its isolated install receipt smoke check.");
                options.PackageMirrorBase = Path.Combine(root, "disabled-mirror");
                BootstrapperReadyApp repeat = await engine.EnsureReadyAsync(app.Id, CancellationToken.None);
                if (repeat.ExecutablePath != first.ExecutablePath || !engine.GetStatus(app.Id).IsReady)
                    throw new InvalidDataException("Repeated cached payload smoke check failed.");
                Console.WriteLine("Payload smoke passed: " + app.Id + "; one app + shared runtime cached; installed under TEMP; repeat cache hit; no app launched.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Payload smoke failed: " + ex.GetType().Name);
            return 1;
        }
        finally
        {
            try { DeleteFixtureRoot(root); }
            catch { Console.Error.WriteLine("Payload smoke cleanup was incomplete: " + root); }
        }
    }

    private static void TestZipRejections(string root)
    {
        Expect<InvalidDataException>(delegate { LongPathFileSystem.ForIo("\\\\?\\C:\\temp\\fixture"); },
            "extended device paths are rejected at the filesystem boundary");
        Expect<InvalidDataException>(delegate { LongPathFileSystem.ForIo("C:\\temp\\fixture:stream"); },
            "drive alternate data streams are rejected at the filesystem boundary");
        Expect<InvalidDataException>(delegate { LongPathFileSystem.ForIo("\\\\server\\share\\fixture:stream"); },
            "UNC alternate data streams are rejected at the filesystem boundary");
        string cache = Path.Combine(root, "zip-checks");
        Directory.CreateDirectory(cache);
        BootstrapperOptions options = Limits(cache);
        string[] hostile = { "../escape.exe", "folder\\..\\escape.exe", "C:/escape.exe", "/rooted.exe", "CON.txt" };
        foreach (string entry in hostile)
        {
            string zip = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".zip");
            CreateZip(zip, new Dictionary<string, byte[]> { { entry, new byte[] { 1 } } }, false);
            string destination = Path.Combine(cache, "out-" + Guid.NewGuid().ToString("N"));
            Expect<InvalidDataException>(delegate
            {
                SafeZipExtractor.ExtractApp(zip, destination, cache, options, App("a", "a.exe", "0"), null);
            }, "unsafe ZIP path rejected: " + entry);
        }

        string duplicate = Path.Combine(cache, "duplicate.zip");
        CreateZip(duplicate, new Dictionary<string, byte[]> { { "Tool.exe", new byte[] { 1 } }, { "tool.exe", new byte[] { 2 } } }, false);
        Expect<InvalidDataException>(delegate
        {
            SafeZipExtractor.ExtractApp(duplicate, Path.Combine(cache, "duplicate-out"), cache, options,
                App("a", "Tool.exe", "0"), null);
        }, "case-insensitive duplicate ZIP paths rejected");

        string link = Path.Combine(cache, "link.zip");
        using (FileStream stream = new FileStream(link, FileMode.CreateNew))
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("linked.exe");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using (Stream output = entry.Open()) output.WriteByte(1);
        }
        Expect<InvalidDataException>(delegate
        {
            SafeZipExtractor.ExtractApp(link, Path.Combine(cache, "link-out"), cache, options,
                App("a", "linked.exe", "0"), null);
        }, "symlink ZIP entries rejected");
    }

    private static async Task TestInstallCacheAndLaunch(string root)
    {
        string fixture = Path.Combine(root, "flow");
        string mirror = Path.Combine(fixture, "mirror");
        string cache = Path.Combine(fixture, "cache");
        string menu = Path.Combine(fixture, "start-menu");
        Directory.CreateDirectory(mirror);
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(menu);

        string probeExe = BuildProbe(root);
        string runtimeZip = Path.Combine(mirror, "runtime-test.zip");
        string appOneZip = Path.Combine(mirror, "module-one.zip");
        string appTwoZip = Path.Combine(mirror, "module-two.zip");
        CreateZip(runtimeZip, RuntimeFiles(), false);
        CreateZip(appOneZip, new Dictionary<string, byte[]> { { "probe.exe", File.ReadAllBytes(probeExe) }, { "data.txt", Bytes("payload") } }, false);
        CreateZip(appTwoZip, new Dictionary<string, byte[]> { { "probe.exe", File.ReadAllBytes(probeExe) } }, false);

        byte[] runtimeBytes = File.ReadAllBytes(runtimeZip);
        byte[] oneBytes = File.ReadAllBytes(appOneZip);
        byte[] twoBytes = File.ReadAllBytes(appTwoZip);
        string json = CatalogJson(runtimeBytes, oneBytes, twoBytes);
        byte[] catalogBytes = Encoding.UTF8.GetBytes(json);
        string catalogHash = Hash(catalogBytes);
        string id80 = "a" + new string('b', 79);
        string boundaryJson = json.Replace("\"id\":\"module-two\"", "\"id\":\"" + id80 + "\"");
        BootstrapperOptions boundaryOptions = Limits(Path.Combine(fixture, "boundary-cache"));
        boundaryOptions.EmbeddedCatalogJson = boundaryJson;
        boundaryOptions.ExpectedCatalogSha256 = Hash(Encoding.UTF8.GetBytes(boundaryJson));
        using (BootstrapperEngine boundaryEngine = new BootstrapperEngine(boundaryOptions,
            NullBootstrapperCredentialProvider.Instance))
            Check(boundaryEngine.LoadCatalogAsync(CancellationToken.None).GetAwaiter().GetResult().FindApp(id80) != null,
                "catalog accepts publisher's 80-character app ID limit");
        string id81 = id80 + "c";
        string oversizedIdJson = json.Replace("\"id\":\"module-two\"", "\"id\":\"" + id81 + "\"");
        BootstrapperOptions oversizedIdOptions = Limits(Path.Combine(fixture, "oversized-id-cache"));
        oversizedIdOptions.EmbeddedCatalogJson = oversizedIdJson;
        oversizedIdOptions.ExpectedCatalogSha256 = Hash(Encoding.UTF8.GetBytes(oversizedIdJson));
        using (BootstrapperEngine oversizedIdEngine = new BootstrapperEngine(oversizedIdOptions,
            NullBootstrapperCredentialProvider.Instance))
            Expect<FormatException>(delegate { oversizedIdEngine.LoadCatalogAsync(CancellationToken.None).GetAwaiter().GetResult(); },
                "catalog rejects IDs longer than the publisher limit");

        BootstrapperOptions badHashOptions = Limits(Path.Combine(fixture, "bad-hash-cache"));
        badHashOptions.EmbeddedCatalogJson = json;
        badHashOptions.ExpectedCatalogSha256 = new string('0', 64);
        using (BootstrapperEngine badHashEngine = new BootstrapperEngine(badHashOptions,
            NullBootstrapperCredentialProvider.Instance))
            Expect<InvalidDataException>(delegate { badHashEngine.LoadCatalogAsync(CancellationToken.None).GetAwaiter().GetResult(); },
                "catalog bytes must match the pinned hash");

        string invalidApiJson = json.Replace("https://api.github.com", "http://api.github.com");
        BootstrapperOptions invalidApiOptions = Limits(Path.Combine(fixture, "invalid-api-cache"));
        invalidApiOptions.EmbeddedCatalogJson = invalidApiJson;
        invalidApiOptions.ExpectedCatalogSha256 = Hash(Encoding.UTF8.GetBytes(invalidApiJson));
        using (BootstrapperEngine invalidApiEngine = new BootstrapperEngine(invalidApiOptions,
            NullBootstrapperCredentialProvider.Instance))
            Expect<FormatException>(delegate { invalidApiEngine.LoadCatalogAsync(CancellationToken.None).GetAwaiter().GetResult(); },
                "catalog API origin must be HTTPS");

        BootstrapperOptions options = Limits(cache);
        options.EmbeddedCatalogJson = json;
        options.ExpectedCatalogSha256 = catalogHash;
        options.BootstrapperExecutablePath = probeExe;
        options.StartMenuRoot = menu;
        options.PackageMirrorBase = mirror;

        int transportCreations = 0;
        BootstrapperOptions httpMirrorOptions = Limits(Path.Combine(fixture, "http-mirror-cache"));
        httpMirrorOptions.EmbeddedCatalogJson = json;
        httpMirrorOptions.ExpectedCatalogSha256 = catalogHash;
        httpMirrorOptions.BootstrapperExecutablePath = probeExe;
        httpMirrorOptions.StartMenuRoot = Path.Combine(fixture, "http-mirror-menu");
        httpMirrorOptions.PackageMirrorBase = "http://127.0.0.1/packages";
        using (BootstrapperEngine httpMirrorEngine = new BootstrapperEngine(httpMirrorOptions,
            NullBootstrapperCredentialProvider.Instance,
            delegate { Interlocked.Increment(ref transportCreations); return new NoNetworkTransport(); },
            delegate { Interlocked.Increment(ref transportCreations); return new NoNetworkTransport(); }))
            ExpectAsync<InvalidDataException>(async delegate
            {
                await httpMirrorEngine.EnsureReadyAsync("module-one", CancellationToken.None);
            }, "package mirror must not allow cleartext HTTP").GetAwaiter().GetResult();
        Check(transportCreations == 1, "cleartext mirror was rejected before any request");
        transportCreations = 0;

        BootstrapperEngine engine = new BootstrapperEngine(options, NullBootstrapperCredentialProvider.Instance,
            delegate { Interlocked.Increment(ref transportCreations); return new NoNetworkTransport(); },
            delegate { Interlocked.Increment(ref transportCreations); return new NoNetworkTransport(); });
        BootstrapperCatalog catalog = await engine.LoadCatalogAsync(CancellationToken.None);
        Check(catalog.Apps.Count == 2, "embedded catalog parses two apps");
        Check(transportCreations == 0, "loading catalog performs no network activity");

        BootstrapperReadyApp one = await engine.EnsureReadyAsync("module-one", CancellationToken.None);
        Check(File.Exists(one.ExecutablePath), "first app is extracted");
        Check(Directory.Exists(Path.Combine(cache, "runtime", catalog.Runtime.Sha256)), "shared runtime is cached once");
        BootstrapperReadyApp two = await engine.EnsureReadyAsync("module-two", CancellationToken.None);
        Check(File.Exists(two.ExecutablePath), "second app is extracted");
        Check(Path.GetDirectoryName(one.RuntimeDirectory) == Path.GetDirectoryName(two.RuntimeDirectory),
            "apps point at one shared runtime");

        BootstrapperReadyApp cached = await engine.EnsureReadyAsync("module-one", CancellationToken.None);
        Check(cached.ExecutablePath == one.ExecutablePath, "repeat ensure reuses the app cache");
        Check(transportCreations == 3, "only runtime and two app packages were downloaded");

        BootstrapperInstallResult installedOne = await engine.InstallAsync("module-one", CancellationToken.None);
        BootstrapperInstallResult installedTwo = await engine.InstallAsync("module-two", CancellationToken.None);
        Check(installedOne.ShortcutPath != installedTwo.ShortcutPath && File.Exists(installedOne.ShortcutPath) && File.Exists(installedTwo.ShortcutPath),
            "same-name apps retain distinct shortcuts");
        Check(installedOne.InstalledBootstrapperPath == installedTwo.InstalledBootstrapperPath && File.Exists(installedOne.InstalledBootstrapperPath),
            "shortcuts share the stable bootstrapper");
        Check(engine.GetStatus("module-one").IsInstalled && engine.GetStatus("module-two").IsInstalled,
            "installed status uses independent receipts");
        CheckShortcut(installedOne.ShortcutPath, installedOne.InstalledBootstrapperPath, "module-one");
        string validReceipt = File.ReadAllText(installedOne.ReceiptPath);
        File.WriteAllText(installedOne.ReceiptPath, "appId=module-one\r\n");
        Check(!engine.GetStatus("module-one").IsInstalled, "truncated install receipt is not reported as installed");
        File.WriteAllText(installedOne.ReceiptPath, validReceipt);
        Check(engine.GetStatus("module-one").IsInstalled, "valid receipt and shortcut restore installed status");

        string longEntry = "deep";
        for (int i = 0; i < 6; i++) longEntry += "/" + new string('\u754c', 36);
        longEntry += "/payload.txt";
        string longPathZip = Path.Combine(mirror, "module-one-long.zip");
        CreateZip(longPathZip, new Dictionary<string, byte[]>
        {
            { "probe.exe", File.ReadAllBytes(probeExe) },
            { longEntry, Bytes("unicode long-path payload") }
        }, false);
        byte[] longPathZipBytes = File.ReadAllBytes(longPathZip);
        string longCache = Path.Combine(fixture, "unicode-cache");
        string longCatalogJson = CatalogJson(runtimeBytes, longPathZipBytes, twoBytes).Replace(
            "\"asset\":\"module-one.zip\"", "\"asset\":\"module-one-long.zip\"");
        string longDataPath = Path.Combine(Path.Combine(Path.Combine(longCache, "apps"), "module-one"),
            Hash(longPathZipBytes) + Path.DirectorySeparatorChar + longEntry.Replace('/', Path.DirectorySeparatorChar));
        Check(Path.GetFullPath(longDataPath).Length > 300, "Unicode cache payload path exceeds 300 characters");
        BootstrapperOptions longPathOptions = Limits(longCache);
        longPathOptions.EmbeddedCatalogJson = longCatalogJson;
        longPathOptions.ExpectedCatalogSha256 = Hash(Encoding.UTF8.GetBytes(longCatalogJson));
        longPathOptions.BootstrapperExecutablePath = probeExe;
        longPathOptions.StartMenuRoot = menu;
        longPathOptions.PackageMirrorBase = mirror;
        string longPathStage = "engine construction";
        try
        {
            using (BootstrapperEngine longPathEngine = new BootstrapperEngine(longPathOptions,
                NullBootstrapperCredentialProvider.Instance,
                delegate { return new NoNetworkTransport(); },
                delegate { return new NoNetworkTransport(); }))
            {
                longPathStage = "EnsureReadyAsync extraction";
                BootstrapperReadyApp longPathReady = await longPathEngine.EnsureReadyAsync("module-one", CancellationToken.None);
                Check(longPathReady.ExecutablePath.Length < 260 && File.Exists(longPathReady.ExecutablePath),
                    "app executable stays shell-compatible under a long Unicode payload path");
                Check(LongPathFile.Exists(longDataPath),
                    "package extracts Unicode payload beneath a path longer than 300 characters");
                Check(longPathEngine.GetStatus("module-one").IsReady,
                    "ready status resolves files under a long Unicode cache path");
                longPathStage = "InstallAsync receipt and shortcut";
                BootstrapperInstallResult longPathInstall = await longPathEngine.InstallAsync("module-one", CancellationToken.None);
                Check(longPathInstall.ReceiptPath.Length < 260 && File.Exists(longPathInstall.ReceiptPath) &&
                    longPathInstall.InstalledBootstrapperPath.Length < 260 &&
                    longPathEngine.GetStatus("module-one").IsInstalled,
                    "install receipt, launcher target, and status stay valid with a long payload path");
                Check(File.Exists(longPathInstall.ShortcutPath) && longPathInstall.ShortcutPath.Length < 260,
                    "long-payload install creates a normal Start Menu shortcut");
            }
            longPathStage = "long-path cleanup";
            DeleteFixtureRoot(longCache);
            Check(!Directory.Exists(longCache), "long Unicode cache staging and receipts clean up safely");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Long-path fixture failed during " + longPathStage + ": " +
                ex.GetType().Name + " (HRESULT 0x" + ex.HResult.ToString("X8") + ", cache path length " +
                Path.GetFullPath(longCache).Length + "): " + ex.Message, ex);
        }

        string output = Path.Combine(fixture, "child-env.txt");
        Environment.SetEnvironmentVariable("BOOTSTRAPPER_ENV_PROBE_OUT", output);
        try
        {
            BootstrapperLaunchResult launched = await engine.LaunchAsync("module-one", CancellationToken.None);
            Check(launched.ProcessId > 0, "fixture child started via ShellExecute");
            WaitForFile(output, 15000);
            string childText = File.ReadAllText(output);
            string expectedRuntime = one.RuntimeDirectory;
            Check(ContainsLine(childText, "DOTNET_ROOT=" + expectedRuntime), "child inherited verified DOTNET_ROOT");
            Check(ContainsLine(childText, "DOTNET_ROOT_X64=" + expectedRuntime), "child inherited verified DOTNET_ROOT_X64");
            Check(Environment.GetEnvironmentVariable("DOTNET_ROOT") != expectedRuntime,
                "parent environment was restored after process start");
        }
        finally { Environment.SetEnvironmentVariable("BOOTSTRAPPER_ENV_PROBE_OUT", null); }

        File.Delete(mirror + "\\runtime-test.zip");
        File.Delete(appOneZip);
        File.Delete(appTwoZip);
        BootstrapperReadyApp offline = await engine.EnsureReadyAsync("module-one", CancellationToken.None);
        Check(offline.ExecutablePath == one.ExecutablePath, "valid cached app launches without its package source");

        string nextJson = json.Replace("\"version\":\"1.0.0\",\"source\"", "\"version\":\"1.0.1\",\"source\"")
            .Replace("\"tagName\":\"v1.0.0\"", "\"tagName\":\"v1.0.1\"");
        BootstrapperOptions nextOptions = Limits(cache);
        nextOptions.EmbeddedCatalogJson = nextJson;
        nextOptions.ExpectedCatalogSha256 = Hash(Encoding.UTF8.GetBytes(nextJson));
        nextOptions.BootstrapperExecutablePath = probeExe;
        nextOptions.StartMenuRoot = menu;
        nextOptions.PackageMirrorBase = mirror;
        int nextTransportCreations = 0;
        using (BootstrapperEngine nextEngine = new BootstrapperEngine(nextOptions,
            NullBootstrapperCredentialProvider.Instance,
            delegate { Interlocked.Increment(ref nextTransportCreations); return new NoNetworkTransport(); },
            delegate { Interlocked.Increment(ref nextTransportCreations); return new NoNetworkTransport(); }))
        {
            BootstrapperReadyApp samePayload = await nextEngine.EnsureReadyAsync("module-one", CancellationToken.None);
            Check(samePayload.ExecutablePath == one.ExecutablePath, "same package hash reuses app cache across catalog versions");
            Check(nextTransportCreations == 0, "unchanged app and runtime hashes need no downloads in a new catalog");
        }

        string readyMarker = Path.Combine(Path.GetDirectoryName(one.ExecutablePath), ".ready");
        byte[] oldMarker = File.ReadAllBytes(readyMarker);
        File.WriteAllText(readyMarker, "tampered\n");
        ExpectAsync<InvalidDataException>(async delegate
        {
            await engine.EnsureReadyAsync("module-one", CancellationToken.None);
        }, "corrupt cache marker is rejected without replacement").GetAwaiter().GetResult();
        Check(File.ReadAllText(readyMarker) == "tampered\n", "invalid cache remains untouched for inspection");
        File.WriteAllBytes(readyMarker, oldMarker);

        string stableDirectory = Path.GetDirectoryName(installedOne.InstalledBootstrapperPath);
        string externalDirectory = Path.Combine(fixture, "junction-target");
        Directory.CreateDirectory(externalDirectory);
        string sentinel = Path.Combine(externalDirectory, "keep.txt");
        File.WriteAllText(sentinel, "owned outside cache");
        File.Delete(installedOne.InstalledBootstrapperPath);
        Directory.Delete(stableDirectory, false);
        CreateJunction(stableDirectory, externalDirectory);
        try
        {
            ExpectAsync<InvalidDataException>(async delegate
            {
                await engine.InstallAsync("module-one", CancellationToken.None);
            }, "install rejects a reparse-point launcher cache directory").GetAwaiter().GetResult();
            Check(File.ReadAllText(sentinel) == "owned outside cache", "reparse-point rejection preserves the external target");
        }
        finally { Directory.Delete(stableDirectory, false); }
    }

    private static string BuildProbe(string root)
    {
        string source = Path.Combine(root, "probe-source.cs");
        string output = Path.Combine(root, "probe.exe");
        File.WriteAllText(source,
            "using System; using System.IO; class P { static int Main() { " +
            "string p=Environment.GetEnvironmentVariable(\"BOOTSTRAPPER_ENV_PROBE_OUT\"); " +
            "File.WriteAllText(p, \"DOTNET_ROOT=\"+Environment.GetEnvironmentVariable(\"DOTNET_ROOT\")+\"\\nDOTNET_ROOT_X64=\"+Environment.GetEnvironmentVariable(\"DOTNET_ROOT_X64\")); return 0; } }");
        CSharpCodeProvider provider = new CSharpCodeProvider();
        CompilerParameters parameters = new CompilerParameters();
        parameters.GenerateExecutable = true;
        parameters.OutputAssembly = output;
        parameters.CompilerOptions = "/nologo /optimize+";
        CompilerResults result = provider.CompileAssemblyFromFile(parameters, source);
        if (result.Errors.HasErrors) throw new InvalidOperationException("Fixture child compilation failed.");
        return output;
    }

    private static Dictionary<string, byte[]> RuntimeFiles()
    {
        return new Dictionary<string, byte[]>
        {
            { "dotnet.exe", Bytes("dotnet") },
            { "host/fxr/10.0.4/hostfxr.dll", Bytes("hostfxr") },
            { "shared/Microsoft.NETCore.App/10.0.4/coreclr.dll", Bytes("coreclr") },
            { "shared/Microsoft.WindowsDesktop.App/10.0.4/PresentationFramework.dll", Bytes("desktop") }
        };
    }

    private static string CatalogJson(byte[] runtime, byte[] one, byte[] two)
    {
        string hash = "\"sha256\":\"";
        return "{\"schemaVersion\":1,\"version\":\"1.0.0\",\"source\":{" +
            "\"serverUrl\":\"https://github.com\",\"apiUrl\":\"https://api.github.com\",\"repository\":\"fixture/repo\",\"tagName\":\"v1.0.0\"}," +
            "\"runtime\":{\"version\":\"10.0.4\",\"asset\":\"runtime-test.zip\"," + hash + Hash(runtime) + "\",\"bytes\":" + runtime.Length + "}," +
            "\"apps\":[" + AppJson("module-one", "Same Name", "module-one.zip", one) + "," +
            AppJson("module-two", "Same Name", "module-two.zip", two) + "]}";
    }

    private static string AppJson(string id, string name, string asset, byte[] zip)
    {
        return "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"description\":\"fixture\",\"category\":\"Tests\",\"executable\":\"probe.exe\",\"asset\":\"" + asset + "\",\"sha256\":\"" + Hash(zip) + "\",\"bytes\":" + zip.Length + "}";
    }

    private static BootstrapperApp App(string id, string exe, string hash)
    {
        return (BootstrapperApp)Activator.CreateInstance(typeof(BootstrapperApp),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new object[] { id, id, "", "Tests", exe, null, id + ".zip", new string('0', 64), 1L }, null);
    }

    private static BootstrapperOptions Limits(string cache)
    {
        return new BootstrapperOptions("", new string('0', 64), cache, Path.Combine(cache, "launcher.exe"));
    }

    private static void CheckShortcut(string path, string target, string appId)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        object link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null,
            shell, new object[] { path });
        try
        {
            Type type = link.GetType();
            string actualTarget = (string)type.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, link, null);
            string args = (string)type.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, link, null);
            Check(String.Equals(actualTarget, target, StringComparison.OrdinalIgnoreCase), "shortcut targets the stable bootstrapper");
            Check(args.Contains("--app \"" + appId + "\"") && args.Contains("--noUI"), "shortcut carries app identity");
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(link)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            if (System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void CreateZip(string path, IDictionary<string, byte[]> files, bool useBackslashes)
    {
        using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (KeyValuePair<string, byte[]> pair in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(useBackslashes ? pair.Key.Replace('/', '\\') : pair.Key);
                using (Stream output = entry.Open()) output.Write(pair.Value, 0, pair.Value.Length);
            }
    }

    private static byte[] Bytes(string value) { return Encoding.UTF8.GetBytes(value); }
    private static string Hash(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private static bool ContainsLine(string text, string line) { return text.Replace("\r", "").Split('\n').Contains(line, StringComparer.Ordinal); }
    private static void WaitForFile(string path, int milliseconds)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            if (File.Exists(path)) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException("Fixture child did not write its environment report.");
    }
    private static void CreateJunction(string link, string target)
    {
        ProcessStartInfo info = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec"),
            Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using (Process process = Process.Start(info))
        {
            if (process == null) throw new InvalidOperationException("Could not create the fixture junction.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("Could not create the fixture junction.");
        }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Check failed: " + name);
        _passed++;
    }
    private static void Expect<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { _passed++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
    }
    private static async Task ExpectAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { _passed++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
    }
    private static void DeleteFixtureRoot(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) return;
        string extendedRoot = ToExtendedPath(fullRoot);
        Stack<string> verify = new Stack<string>();
        verify.Push(extendedRoot);
        while (verify.Count > 0)
        {
            string path = verify.Pop();
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to remove a fixture reparse point.");
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (string child in Directory.GetFileSystemEntries(path)) verify.Push(child);
        }
        Stack<KeyValuePair<string, bool>> pending = new Stack<KeyValuePair<string, bool>>();
        pending.Push(new KeyValuePair<string, bool>(extendedRoot, false));
        while (pending.Count > 0)
        {
            KeyValuePair<string, bool> item = pending.Pop();
            if (!Directory.Exists(item.Key) && !File.Exists(item.Key)) continue;
            FileAttributes attributes = File.GetAttributes(item.Key);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to remove a fixture reparse point.");
            if ((attributes & FileAttributes.Directory) == 0)
            {
                File.Delete(item.Key);
                continue;
            }
            if (item.Value)
            {
                Directory.Delete(item.Key, false);
                continue;
            }
            pending.Push(new KeyValuePair<string, bool>(item.Key, true));
            string[] children = Directory.GetFileSystemEntries(item.Key);
            for (int i = children.Length - 1; i >= 0; i--)
                pending.Push(new KeyValuePair<string, bool>(children[i], false));
        }
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path;
        return "\\\\?\\" + path;
    }

    private sealed class NoNetworkTransport : IBootstrapperHttpTransport
    {
        public Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Unexpected network request in local mirror fixture.");
        }
        public void Dispose() { }
    }

    private sealed class EnvironmentCredentialProvider : IBootstrapperCredentialProvider
    {
        public string GetToken(Uri apiOrigin) { return Environment.GetEnvironmentVariable("ILUNCHER_TEST_TOKEN"); }
    }
}
