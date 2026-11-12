using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ILauncher.Models;
using ILauncher.Services;
using ILuncher.AppKit;
using Microsoft.Win32;

// 런처 핵심 동작 확인. 실패하면 종료 코드 1.
if (args.Length >= 3 && args[0] == "--ui-snapshot")
    return UiSnapshot.Run(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 8);

var root = Path.Combine(Path.GetTempPath(), "i-luncher-checks-" + Guid.NewGuid().ToString("N")[..8]);
string registryRoot = @"Software\i-luncher-checks\" + Path.GetFileName(root);
Directory.CreateDirectory(root);
int passed = 0, failed = 0;

async Task Check(string name, Func<Task> body)
{
    try
    {
        await body();
        passed++;
        Console.WriteLine("  ok   " + name);
    }
    catch (Exception error)
    {
        failed++;
        Console.WriteLine("  FAIL " + name + Environment.NewLine + "       " + error.GetType().Name + ": " + error.Message.Replace("\n", "\n       "));
    }
}
Task CheckSync(string name, Action body) => Check(name, () => { body(); return Task.CompletedTask; });
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual, string what) { if (!Equals(expected, actual)) throw new Exception($"{what}: expected <{expected}> but was <{actual}>"); }

try
{
    Console.WriteLine("ReleaseVersion");
    await CheckSync("tags and file versions parse to the same numbers", () =>
    {
        Equal("1.2.3", ReleaseVersion.Parse("v1.2.3")!.ToString(), "v prefix");
        Equal("1.2.3", ReleaseVersion.Parse("1.2.3.0")!.ToString(), "zero revision");
        Equal("1.2.3.4", ReleaseVersion.Parse("1.2.3.4")!.ToString(), "revision kept");
        Equal("1.1.3", ReleaseVersion.Parse("1.1.3+7892841c7f27")!.ToString(), "build metadata dropped");
        Equal("2.0.0-beta.2", ReleaseVersion.Parse("deskcal-v2.0.0-beta.2")!.ToString(), "name prefix and prerelease");
        Equal("3.0.0", ReleaseVersion.Parse("Release 3.0 (stable)")!.ToString(), "version inside a title");
        Assert(ReleaseVersion.Parse("latest") is null && ReleaseVersion.Parse("") is null, "no version text");
    });
    await CheckSync("ordering: numbers, then release above prerelease", () =>
    {
        Assert(ReleaseVersion.Parse("1.0.10") > ReleaseVersion.Parse("1.0.9"), "numeric not lexical");
        Assert(ReleaseVersion.Parse("1.0.0") > ReleaseVersion.Parse("1.0.0-rc.1"), "release above prerelease");
        Assert(ReleaseVersion.Parse("1.0.0-rc.10") > ReleaseVersion.Parse("1.0.0-rc.2"), "numeric prerelease ids");
        Assert(ReleaseVersion.Parse("1.0.0-beta") < ReleaseVersion.Parse("1.0.0-rc"), "alphabetic prerelease ids");
        Assert(ReleaseVersion.Parse("1.2.3.0")!.Equals(ReleaseVersion.Parse("v1.2.3")), "1.2.3.0 == v1.2.3");
        Assert(!(ReleaseVersion.Parse("1.2.3") > ReleaseVersion.Parse("1.2.3+other")), "build metadata does not make an update");
    });

    Console.WriteLine("GitHubRepository");
    await CheckSync("github.com, enterprise, ssh, api and shorthand addresses", () =>
    {
        var cloud = GitHubRepository.Parse("https://github.com/owner/i-luncher.git")!;
        Equal("https://api.github.com/repos/owner/i-luncher", cloud.ApiRepositoryUrl, "github.com api");
        var ghes = GitHubRepository.Parse("https://GHE.Example.com/team/DeskCal/releases/tag/v1.0.0")!;
        Equal("https://ghe.example.com/api/v3/repos/team/DeskCal", ghes.ApiRepositoryUrl, "GHES api");
        Equal("https://ghe.example.com/team/DeskCal", ghes.WebUrl, "GHES web");
        Equal("ghe.example.com", GitHubRepository.Parse("git@ghe.example.com:team/DeskCal.git")!.Host, "ssh host");
        Equal("team/DeskCal", GitHubRepository.Parse("https://ghe.example.com/api/v3/repos/team/DeskCal")!.FullName, "GHES api url");
        Equal("github.com", GitHubRepository.Parse("https://api.github.com/repos/a/b")!.Host, "api.github.com url");
        Equal("ghe.example.com", GitHubRepository.Parse("team/DeskCal", "ghe.example.com")!.Host, "shorthand uses default host");
        Equal("ghe.example.com", GitHubRepository.Parse("ghe.example.com/team/DeskCal")!.Host, "no scheme");
        Equal("https://api.acme.ghe.com", GitHubRepository.Parse("https://acme.ghe.com/o/r")!.ApiBase, "ghe.com data residency");
        Equal("http://10.0.0.5:8080/api/v3", GitHubRepository.Parse("http://10.0.0.5:8080/o/r")!.ApiBase, "http with port");
        Assert(GitHubRepository.Parse("https://token@github.com/o/r") is null, "credentials in URL are refused");
        Assert(GitHubRepository.Parse("https://github.com/only-owner") is null, "owner without repository");
        Assert(GitHubRepository.Parse("ftp://github.com/o/r") is null, "non-http scheme");
    });

    Console.WriteLine("Release assets");
    GitHubRelease Release(string tag, params string[] assets) => new(tag, tag, ReleaseVersion.Parse(tag), false, DateTimeOffset.Now, "https://example/r", "",
        assets.Select(name => new GitHubAsset(name, "https://api.example/assets/" + name, "https://example/dl/" + name, 10, null)).ToList());
    await CheckSync("zip, setup and Velopack selection", () =>
    {
        var app = new AppDefinition { Id = "deskcal", Name = "DeskCal", Repository = "https://github.com/a/DeskCal" };
        var release = Release("v1.1.4", "DeskCal-Setup.exe", "DeskCal-win-x64.zip", "DeskCal-src.zip");
        Equal("DeskCal-win-x64.zip", AppManager.SelectZipAsset(app, release)?.Name, "win-x64 zip");
        Equal("DeskCal-Setup.exe", AppManager.SelectSetupAsset(app, release)?.Name, "setup exe");
        Equal(InstallKinds.Zip, AppManager.ResolveKind(app, release), "zip preferred");
        Equal("/S", AppManager.DefaultSetupArguments(app, release), "default silent switch");

        var velopack = Release("v1.0.501", "ATOZ-win-Setup.exe", "ATOZ-win-Portable.zip", "ATOZ-1.0.501-full.nupkg", "releases.win.json", "RELEASES");
        var atoz = new AppDefinition { Id = "atoz", Name = "ATOZ", Repository = "https://github.com/a/status" };
        Equal(InstallKinds.Setup, AppManager.ResolveKind(atoz, velopack), "Velopack apps use their installer");
        Equal("--silent", AppManager.DefaultSetupArguments(atoz, velopack), "Velopack silent switch");

        var pinned = new AppDefinition { Id = "x", Name = "X", Repository = "https://github.com/a/x", Asset = "X-*-portable.zip" };
        Equal("X-1.2.0-portable.zip", AppManager.SelectZipAsset(pinned, Release("v1.2.0", "X-1.2.0-portable.zip", "X-win-x64.zip"))?.Name, "wildcard asset");
        Assert(AppManager.SelectZipAsset(app, Release("v1", "symbols.zip", "app-linux-x64.zip", "a.zip", "b.zip")) is null, "ambiguous zips are not guessed");
    });
    await CheckSync("install folder name is guessed from the package name", () =>
    {
        Equal("DeskCal", AppManager.GuessInstallName("DeskCal-win-x64.zip"), "DeskCal");
        Equal("TerminalNotes", AppManager.GuessInstallName("TerminalNotes-win-x64.zip"), "TerminalNotes");
        Equal("ATOZ", AppManager.GuessInstallName("ATOZ-win-Setup.exe"), "Velopack setup");
        Equal("MyApp", AppManager.GuessInstallName("MyApp-1.2.3.zip"), "version suffix");
    });

    Console.WriteLine("Icons");
    await CheckSync("repository icon discovery: csproj ApplicationIcon first, then names", () =>
    {
        GitHubTreeEntry F(string path, long size = 5000) => new(path, true, size);
        var entries = new[]
        {
            F("SwiftCopy.App/SwiftCopy.App.csproj"), F("SwiftCopy.Core/SwiftCopy.Core.csproj"), F("tests/SwiftCopy.Tests/SwiftCopy.Tests.csproj"),
            F("SwiftCopy.App/Assets/SwiftCopy.ico"), F("SwiftCopy.App/Assets/SwiftCopy-128.png"), F("docs/logo.png"), F("SwiftCopy.App/bin/Release/icon.ico")
        };
        var names = IconDiscovery.Names("SwiftCopy", null, "SwiftCopy");
        Equal("SwiftCopy.App/SwiftCopy.App.csproj", IconDiscovery.ProjectCandidates(entries, names).FirstOrDefault(), "app project first, tests skipped");
        Assert(!IconDiscovery.ProjectCandidates(entries, names).Any(path => path.Contains("Tests")), "test projects are not read");
        Equal("SwiftCopy.App/Assets/SwiftCopy.ico", IconDiscovery.ParseApplicationIcon("<Project><PropertyGroup><ApplicationIcon>Assets/SwiftCopy.ico</ApplicationIcon></PropertyGroup></Project>", "SwiftCopy.App/SwiftCopy.App.csproj"), "relative to the project");
        Equal("cal.ico", IconDiscovery.ParseApplicationIcon("<ApplicationIcon>cal.ico</ApplicationIcon>", "DeskCal.csproj"), "root project");
        Equal("DiskAtlas.App/Assets/DiskAtlas.ico", IconDiscovery.ParseApplicationIcon(@"<ApplicationIcon>..\DiskAtlas.App\Assets\DiskAtlas.ico</ApplicationIcon>", "DiskAtlas.Start/DiskAtlas.Start.csproj"), "parent folder");
        Assert(IconDiscovery.ParseApplicationIcon("<ApplicationIcon>$(IconPath)</ApplicationIcon>", "a.csproj") is null, "MSBuild properties are not guessed");
        Equal("SwiftCopy.App/Assets/SwiftCopy.ico", IconDiscovery.ChooseByName(entries, names), "named .ico in Assets, not docs or bin");
        Equal("Assets/AppIcon.png", IconDiscovery.ChooseByName([F("Assets/AppIcon.png"), F("Assets/splash-icon.png"), F("README.png")], IconDiscovery.Names("Tool")), "icon name wins over splash");
        Assert(IconDiscovery.ChooseByName([F("images/photo.png"), F("screenshots/main.png")], IconDiscovery.Names("Tool")) is null, "no weak guesses");
    });
    await CheckSync("icon files decode to the largest frame", () =>
    {
        string ico = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Launcher", "Assets", "ILauncher.ico");
        var image = ILauncher.AppIconService.Decode(File.ReadAllBytes(ico));
        Assert(image is not null && image.PixelWidth >= 48, $"decoded {image?.PixelWidth}px");
        Assert(ILauncher.AppIconService.Decode([1, 2, 3]) is null, "garbage is rejected");
    });

    Console.WriteLine("Catalog");
    await Check("local catalog keeps Korean text readable and round-trips", async () =>
    {
        var store = new LocalCatalogStore(Path.Combine(root, "catalog", "catalog.json"));
        await store.UpdateAsync(apps => { apps.Add(new AppDefinition { Id = "deskcal", Name = "DeskCal", Repository = "https://github.com/a/DeskCal", Category = "일정", Description = "바탕화면 달력" }); return "add"; });
        string json = File.ReadAllText(Path.Combine(root, "catalog", "catalog.json"));
        Assert(json.Contains("바탕화면 달력") && json.Contains("\"apps\""), "Korean is written as text: " + json);
        var loaded = (await store.LoadAsync()).Apps;
        Equal(1, loaded.Count, "count");
        Equal("일정", loaded[0].Category, "category");
        Assert(!json.Contains("includePrerelease") && !json.Contains("\"order\""), "defaults are omitted");
    });
    await CheckSync("catalog parser accepts arrays, fills ids and drops duplicates", () =>
    {
        var apps = CatalogSerializer.Parse("""
            // comment
            [ { "name": "Folder Compare", "repository": "o/FolderCompare" }, { "id": "folder-compare", "name": "dup", "repository": "o/x" }, { "name": "no repo" }, ]
            """);
        Equal(1, apps.Count, "count");
        Equal("folder-compare", apps[0].Id, "generated id");
    });

    var github = new FakeGitHub();
    using var client = new GitHubClient(host => host == "ghe.test" ? "secret-token" : null, "checks", github);
    var repo = GitHubRepository.Parse("https://ghe.test/team/i-luncher")!;

    await Check("repository catalog: read, write with sha, retry after a concurrent change, offline cache", async () =>
    {
        string path = "catalog/apps.json";
        github.Files[path] = (CatalogSerializer.Serialize([new AppDefinition { Id = "a", Name = "A", Repository = "https://ghe.test/team/a" }]), "sha1");
        var cache = Path.Combine(root, "cache", "catalog-cache.json");
        var store = new GitHubCatalogStore(client, repo, path, "main", cache);
        Equal(1, (await store.LoadAsync()).Apps.Count, "initial load");
        Assert(github.Requests.Any(r => r.Contains("/api/v3/repos/team/i-luncher/contents/catalog/apps.json?ref=main") && r.Contains("Bearer secret-token")), "contents API with token");

        github.ConflictOnce = true;  // 다른 사람이 그 사이에 저장
        int calls = 0;
        var saved = await store.UpdateAsync(apps => { calls++; apps.Add(new AppDefinition { Id = "b", Name = "B", Repository = "https://ghe.test/team/b" }); return "add b"; });
        Equal(2, calls, "change re-applied to the latest list");
        Equal("a,other,b", string.Join(",", saved.Select(app => app.Id)), "the other person's addition is kept");
        Assert(github.LastPut?.Contains("\"sha\":\"sha2\"") == true && github.LastPut.Contains("\"branch\":\"main\""), "put uses the latest sha and branch: " + github.LastPut);
        Equal(3, CatalogSerializer.Parse(github.Files[path].Content).Count, "server content");

        github.Offline = true;
        var offline = await store.LoadAsync();
        github.Offline = false;
        Assert(offline.FromCache && offline.Apps.Count == 3 && offline.Warning is not null, "cache used when the server is unreachable");
    });

    Console.WriteLine("API server");
    await Check("API server catalog: open reads, admin-password saves, retry after a concurrent change, offline cache", async () =>
    {
        var api = new FakeApiServer();
        string cache = Path.Combine(root, "api-cache", "catalog-cache.json");
        string? password = null;
        using var store = new ApiCatalogStore("http://api.test:2003/", () => password, cache, api);
        Equal(0, (await store.LoadAsync()).Apps.Count, "empty list");
        try { await store.UpdateAsync(apps => { apps.Add(new AppDefinition { Id = "a", Name = "A", Repository = "https://github.com/o/a" }); return "add"; }); throw new Exception("expected 401"); }
        catch (GitHubApiException error) { Assert(error.Status == 401 && error.Message.Contains("관리자 비밀번호"), error.Message); }
        password = "pw";
        api.ConflictOnce = true;
        int calls = 0;
        var saved = await store.UpdateAsync(apps => { calls++; apps.Add(new AppDefinition { Id = "a", Name = "A", Repository = "https://github.com/o/a", Category = "도구" }); return "add"; });
        Equal(2, calls, "re-applied after 409");
        Equal("other,a", string.Join(",", saved.Select(app => app.Id)), "the other admin's addition is kept");
        Assert(api.LastAuthorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:pw")), "basic auth with the admin password");
        Assert(api.LastBody!.Contains("\"revision\":") && api.LastBody.Contains("도구"), "sends the revision it read and readable text: " + api.LastBody);
        api.Offline = true;
        var offline = await store.LoadAsync();
        Assert(offline.FromCache && offline.Apps.Count == 2, "cache when the server is down");
    });
    await Check("GitHub relay: github.com repositories go through the API server without the GitHub token", async () =>
    {
        github.AddRelease("team/Relayed", "v2.0.1", ("Relayed-win-x64.zip", [1, 2]));
        using var relayed = new GitHubClient(host => host == "github.com" ? "gh-personal-token" : null, "checks", github,
            repository => repository.Host == "github.com" ? "https://ghe.test/api/v3" : null);
        int before = github.Requests.Count;
        var release = await relayed.GetLatestReleaseAsync(GitHubRepository.Parse("https://github.com/team/Relayed")!, false);
        Equal("2.0.1", release?.Version?.ToString(), "release through the relay");
        var sent = github.Requests.Skip(before).ToList();
        Assert(sent.All(r => r.Contains("https://ghe.test/api/v3/repos/team/Relayed") && !r.Contains("gh-personal-token")), string.Join(" | ", sent));
    });
    await CheckSync("settings: API_SERVER makes the API server the app list and the GitHub relay", () =>
    {
        string buildDir = Path.Combine(root, "api-settings");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, BuildSettings.FileName), "API_SERVER=http://10.0.0.5:2003/\nLAUNCHER_REPOSITORY=https://github.com/owner/i-luncher\n");
        var settings = LauncherSettings.Load(Path.Combine(root, "api-data"), buildDir);
        Equal(LauncherSettings.ModeApi, settings.CatalogMode, "API server first");
        Equal("http://10.0.0.5:2003/api/v3", settings.ApiBaseFor(GitHubRepository.Parse("https://github.com/owner/DeskCal")!), "github.com via the relay");
        Assert(settings.ApiBaseFor(GitHubRepository.Parse("https://ghe.example.com/team/x")!) is null, "an Enterprise server is asked directly");
        // GITHUB_HOST 가 GitHub Enterprise 면 그 호스트도 중계로 (서버의 RELAY_GITHUB_API 가 같은 호스트)
        string enterpriseDir = Path.Combine(root, "api-settings-enterprise");
        Directory.CreateDirectory(enterpriseDir);
        File.WriteAllText(Path.Combine(enterpriseDir, BuildSettings.FileName), "API_SERVER=http://10.0.0.5:2003\nGITHUB_HOST=https://GHE.example.com/\nLAUNCHER_REPOSITORY=https://ghe.example.com/team/i-luncher\n");
        var enterprise = LauncherSettings.Load(Path.Combine(root, "api-data-enterprise"), enterpriseDir);
        Equal("http://10.0.0.5:2003/api/v3", enterprise.ApiBaseFor(GitHubRepository.Parse("https://ghe.example.com/team/x")!), "GITHUB_HOST via the relay");
        Assert(enterprise.ApiBaseFor(GitHubRepository.Parse("https://other.example.com/team/x")!) is null, "other hosts are asked directly");
        Assert(settings.CatalogRepository is null, "no repository catalog in API mode");
        settings.User.SetAdminPassword("secret");
        Equal("secret", settings.User.GetAdminPassword(), "admin password round-trips");
        Assert(settings.User.AdminPassword != "secret", "kept encrypted");
        settings.User.CatalogMode = LauncherSettings.ModeLocal;
        Equal(LauncherSettings.ModeLocal, settings.CatalogMode, "user choice wins");
    });

    Console.WriteLine("GitHub client");
    await Check("latest release, missing release and inaccessible repository", async () =>
    {
        var appRepo = GitHubRepository.Parse("https://ghe.test/team/DeskCal")!;
        github.AddRelease("team/DeskCal", "v1.1.4", ("DeskCal-win-x64.zip", new byte[] { 1, 2, 3 }));
        var release = await client.GetLatestReleaseAsync(appRepo, includePrerelease: false);
        Equal("1.1.4", release?.Version?.ToString(), "latest version");
        Equal("DeskCal-win-x64.zip", release?.Assets.Single().Name, "asset");

        github.Repositories.Add("team/Empty");
        Assert(await client.GetLatestReleaseAsync(GitHubRepository.Parse("https://ghe.test/team/Empty")!, false) is null, "no releases yet");
        try
        {
            await client.GetLatestReleaseAsync(GitHubRepository.Parse("https://ghe.test/team/Secret")!, false);
            throw new Exception("expected an access error");
        }
        catch (GitHubApiException error) { Equal(404, error.Status, "status"); }
    });
    await Check("download verifies size and published SHA-256", async () =>
    {
        var appRepo = GitHubRepository.Parse("https://ghe.test/team/DeskCal")!;
        byte[] data = Encoding.UTF8.GetBytes("payload");
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        github.Assets["good.bin"] = data;
        string target = Path.Combine(root, "dl", "good.bin");
        Equal(sha, await client.DownloadAssetAsync(appRepo, new GitHubAsset("good.bin", "https://ghe.test/api/v3/assets/good.bin", "", data.Length, sha), target), "hash");
        try
        {
            await client.DownloadAssetAsync(appRepo, new GitHubAsset("good.bin", "https://ghe.test/api/v3/assets/good.bin", "", data.Length, new string('0', 64)), target + "2");
            throw new Exception("expected hash mismatch");
        }
        catch (InvalidDataException) { Assert(!File.Exists(target + "2"), "bad download is not kept"); }
    });

    Console.WriteLine("Installer");
    string programs = Path.Combine(root, "Programs");
    var installer = new AppInstaller(programs, registryRoot, launcherCommand: @"C:\i-luncher\i-luncher.exe", launcherRuntime: Path.Combine(root, "no-runtime"),
        createShortcuts: false);
    var sample = new AppDefinition { Id = "sample", Name = "Sample App", InstallName = "SampleApp", Repository = "https://ghe.test/team/SampleApp" };
    string MakeZip(string name, Dictionary<string, string> files, string? runtimeConfig = null)
    {
        string folder = Path.Combine(root, "pkg-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var (relative, content) in files)
        {
            string path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        if (runtimeConfig is not null) File.WriteAllText(Path.Combine(folder, "SampleApp.runtimeconfig.json"), runtimeConfig);
        string zip = Path.Combine(root, name);
        ZipFile.CreateFromDirectory(folder, zip);
        return zip;
    }

    await Check("install, detect the release version, update, keep user files, uninstall", async () =>
    {
        string v1 = MakeZip("v1.zip", new() { ["SampleApp.exe"] = "exe v1", ["lib/old.dll"] = "old", ["data.txt"] = "1", ["createdump.exe"] = "x" });
        var installed = await installer.InstallZipAsync(sample, v1, Release("v1.0.0"));
        string dir = Path.Combine(programs, "SampleApp");
        Equal(dir, installed.Directory, "install folder");
        Equal(Path.Combine(dir, "SampleApp.exe"), installed.ExecutablePath, "named exe preferred over helpers");
        Equal("1.0.0", installed.Version?.ToString(), "version from the installed release");
        Assert(installed.ManagedByLauncher, "managed");
        using (var key = Registry.CurrentUser.OpenSubKey(registryRoot + @"\SampleApp"))
        {
            Equal(dir, key?.GetValue("InstallLocation") as string, "InstallLocation (apps' own updaters look for this)");
            Equal("1.0.0", key?.GetValue("DisplayVersion") as string, "DisplayVersion");
            Assert((key?.GetValue("UninstallString") as string)?.Contains("--uninstall \"SampleApp\"") == true, "uninstall command");
        }

        File.WriteAllText(Path.Combine(dir, "user-settings.json"), "mine");
        string v2 = MakeZip("v2.zip", new() { ["SampleApp/SampleApp.exe"] = "exe v2", ["SampleApp/data.txt"] = "2" });  // 폴더 하나로 감싼 zip
        installed = await installer.InstallZipAsync(sample, v2, Release("v1.1.0"));
        Equal("1.1.0", installed.Version?.ToString(), "updated version");
        Equal("exe v2", File.ReadAllText(Path.Combine(dir, "SampleApp.exe")), "exe replaced");
        Assert(!File.Exists(Path.Combine(dir, "lib", "old.dll")) && !Directory.Exists(Path.Combine(dir, "lib")), "files only in the old package removed");
        Equal("mine", File.ReadAllText(Path.Combine(dir, "user-settings.json")), "user file kept");
        Assert(!Directory.EnumerateDirectories(programs, ".*").Any(), "no staging or backup folders left");

        // 앱이 스스로 업데이트해 실행 파일이 바뀌면 그 실행 파일의 버전을 읽음 (여기서는 버전 정보 없는 가짜 파일 → "앱" 목록 버전)
        File.WriteAllText(Path.Combine(dir, "SampleApp.exe"), "self-updated exe");
        using (var key = Registry.CurrentUser.OpenSubKey(registryRoot + @"\SampleApp", writable: true)) key!.SetValue("DisplayVersion", "1.2.0");
        Equal("1.2.0", installer.Detect(sample)?.Version?.ToString(), "self-updated version from Windows apps list");

        await installer.UninstallAsync(sample, installer.Detect(sample)!);
        Assert(!Directory.Exists(dir) && installer.Detect(sample) is null, "uninstalled");
        Assert(Registry.CurrentUser.OpenSubKey(registryRoot + @"\SampleApp") is null, "registry entry removed");
    });

    await Check("a failed update restores the previous files", async () =>
    {
        string v1 = MakeZip("r1.zip", new() { ["SampleApp.exe"] = "good", ["a.txt"] = "a1" });
        await installer.InstallZipAsync(sample, v1, Release("v1.0.0"));
        string dir = Path.Combine(programs, "SampleApp");
        string bad = Path.Combine(root, "broken.zip");
        File.WriteAllBytes(bad, [1, 2, 3, 4]);
        try { await installer.InstallZipAsync(sample, bad, Release("v2.0.0")); throw new Exception("expected failure"); }
        catch (InvalidDataException) { }
        Equal("good", File.ReadAllText(Path.Combine(dir, "SampleApp.exe")), "old exe intact");
        Equal("1.0.0", installer.Detect(sample)?.Version?.ToString(), "old version kept");
        // 바꾸는 도중 파일 하나가 잠겨 있으면 (다른 프로그램이 열고 있음) 이미 옮긴 파일까지 모두 되돌림
        string v2 = MakeZip("r2.zip", new() { ["SampleApp.exe"] = "new", ["a.txt"] = "a2", ["z.txt"] = "z2" });
        File.WriteAllText(Path.Combine(dir, "z.txt"), "z1");
        using (new FileStream(Path.Combine(dir, "z.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { await installer.InstallZipAsync(sample, v2, Release("v2.0.0")); throw new Exception("expected a locked-file failure"); }
            catch (IOException) { }
        }
        Equal("good", File.ReadAllText(Path.Combine(dir, "SampleApp.exe")), "exe restored after a locked file");
        Equal("a1", File.ReadAllText(Path.Combine(dir, "a.txt")), "moved file restored");
        Equal("1.0.0", installer.Detect(sample)?.Version?.ToString(), "version unchanged after rollback");
        Assert(!Directory.EnumerateDirectories(programs, ".*").Any(), "no staging or backup folders left after rollback");
        string noExe = MakeZip("noexe.zip", new() { ["readme.txt"] = "x" , ["other.txt"] = "y" });
        try { await installer.InstallZipAsync(sample, noExe, Release("v2.0.0")); throw new Exception("expected missing exe"); }
        catch (InvalidDataException) { }
        Equal("good", File.ReadAllText(Path.Combine(dir, "SampleApp.exe")), "old exe intact after a package without the app");
        installer.UninstallByName("SampleApp");
    });

    await Check(".NET runtime requirement: available, missing, or copied from the launcher", async () =>
    {
        string config = """{ "runtimeOptions": { "framework": { "name": "Microsoft.WindowsDesktop.App", "version": "99.0.0" } } }""";
        string zip = MakeZip("fdd.zip", new() { ["SampleApp.exe"] = "x" }, config);
        try { await installer.InstallZipAsync(sample, zip, Release("v1.0.0")); throw new Exception("expected missing runtime"); }
        catch (RuntimeMissingException missing) { Assert(missing.DownloadUrl.EndsWith("/99.0"), missing.DownloadUrl); }
        Assert(!Directory.Exists(Path.Combine(programs, "SampleApp")), "nothing installed without a runtime");

        string runtime = Path.Combine(root, "launcher-runtime");
        foreach (string part in new[] { @"host\fxr\99.0.1", @"shared\Microsoft.NETCore.App\99.0.1", @"shared\Microsoft.WindowsDesktop.App\99.0.1" })
            Directory.CreateDirectory(Path.Combine(runtime, part));
        File.WriteAllText(Path.Combine(runtime, @"host\fxr\99.0.1\hostfxr.dll"), "fxr");
        var withRuntime = new AppInstaller(programs, registryRoot, launcherRuntime: runtime, useGlobalRuntimes: false, createShortcuts: false);
        await withRuntime.InstallZipAsync(sample, zip, Release("v1.0.0"));
        Assert(File.Exists(Path.Combine(programs, "SampleApp", "dotnet", "host", "fxr", "99.0.1", "hostfxr.dll")), "launcher runtime copied next to the app");
        withRuntime.UninstallByName("SampleApp");

        string current = """{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" } } }""";
        await installer.InstallZipAsync(sample, MakeZip("net10.zip", new() { ["SampleApp.exe"] = "x" }, current), Release("v1.0.0"));
        Assert(!Directory.Exists(Path.Combine(programs, "SampleApp", "dotnet")), "installed .NET 10 is used as is");
        installer.UninstallByName("SampleApp");
    });

    await Check("AppManager installs the latest release end to end through the GitHub API", async () =>
    {
        byte[] package = File.ReadAllBytes(MakeZip("e2e.zip", new() { ["SampleApp.exe"] = "from release" }));
        github.AddRelease("team/SampleApp", "v3.2.1", ("SampleApp-win-x64.zip", package), ("SampleApp-src.zip", [9]));
        var manager = new AppManager(client, installer, Path.Combine(root, "downloads"), () => null);
        var release = (await manager.GetLatestAsync(sample))!;
        var messages = new List<string>();
        var installed = await manager.InstallAsync(sample, release, new SyncProgress<AppProgress>(p => messages.Add(p.Message)));
        Equal("3.2.1", installed.Version?.ToString(), "installed version");
        Equal("from release", File.ReadAllText(installed.ExecutablePath!), "content");
        Assert(messages.Any(m => m.Contains("SampleApp-win-x64.zip")), "download progress reported");
        Assert(github.Requests.Any(r => r.Contains("assets/SampleApp-win-x64.zip") && r.Contains("octet-stream")), "asset downloaded via API");
        Assert(!Directory.Exists(Path.Combine(root, "downloads", "sample")), "download folder cleaned");
        installer.UninstallByName("SampleApp");
    });

    Console.WriteLine("AppKit self-update");
    await CheckSync("version text comparison matches the launcher", () =>
    {
        Assert(VersionText.TryParse("v1.2.10", out var a) && VersionText.TryParse("1.2.9.0", out var b) && VersionText.Compare(a, b) > 0, "1.2.10 > 1.2.9");
        Assert(VersionText.TryParse("1.0.0", out var c) && VersionText.TryParse("1.0.0-rc.1", out var d) && VersionText.Compare(c, d) > 0, "release > rc");
        Assert(VersionText.TryParse("1.0.0+abc", out var e) && VersionText.Compare(c, e) == 0, "build metadata ignored");
    });
    await Check("updater script waits for the app, copies the package and updates the apps list", async () =>
    {
        string install = Path.Combine(root, "SelfUpdate");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "Tool.exe"), "old");
        File.WriteAllText(Path.Combine(install, "keep.json"), "user");
        Directory.CreateDirectory(Path.Combine(install, "dotnet"));
        string zip = Path.Combine(root, "self.zip");
        string pkg = Path.Combine(root, "self-pkg");
        Directory.CreateDirectory(Path.Combine(pkg, "sub"));
        File.WriteAllText(Path.Combine(pkg, "Tool.exe"), "new");
        File.WriteAllText(Path.Combine(pkg, "sub", "lib.dll"), "lib");
        ZipFile.CreateFromDirectory(pkg, zip);
        string key = registryRoot + @"\Tool";
        using (var created = Registry.CurrentUser.CreateSubKey(key)) created.SetValue("DisplayVersion", "1.0.0");

        using var sleeper = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Milliseconds 1500") { CreateNoWindow = true, UseShellExecute = false })!;
        string log = Path.Combine(root, "self-update.log");
        string script = AppUpdater.BuildUpdaterScript(zip, install, "Tool.exe", sleeper.Id, log, "1.4.0", key, restart: false);
        using var updater = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            { CreateNoWindow = true, UseShellExecute = false })!;
        await updater.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
        Assert(sleeper.HasExited, "updater waited for the process");
        string logText = File.Exists(log) ? File.ReadAllText(log) : "(no log)";
        Equal("new", File.ReadAllText(Path.Combine(install, "Tool.exe")), "exe replaced; log: " + logText);
        Equal("lib", File.ReadAllText(Path.Combine(install, "sub", "lib.dll")), "subfolder copied");
        Equal("user", File.ReadAllText(Path.Combine(install, "keep.json")), "user file kept");
        Assert(Directory.Exists(Path.Combine(install, "dotnet")), "runtime folder kept");
        using (var updated = Registry.CurrentUser.OpenSubKey(key)) Equal("1.4.0", updated?.GetValue("DisplayVersion") as string, "DisplayVersion");
        Assert(!File.Exists(zip) && logText.Contains("updated to 1.4.0"), "package removed and logged");
    });

    Console.WriteLine("Notices");
    await CheckSync("notice text: title line, body, --- separators, edits re-notify", () =>
    {
        var state = new NoticeService.NoticeState();
        var notices = NoticeService.Parse("첫 공지\n본문 1\n줄 2\n---\n\n둘째 공지\n본문\n---\n첫 공지\n중복 제목은 무시", state);
        Equal(2, notices.Count, "count");
        Equal("본문 1\n줄 2", notices.Single(n => n.Title == "첫 공지").Body, "body");
        var edited = NoticeService.Parse("첫 공지\n고친 본문", state);
        Equal(notices.Single(n => n.Title == "첫 공지").Id, edited[0].Id, "same title, same id");
        Assert(notices.Single(n => n.Title == "첫 공지").Revision != edited[0].Revision, "edited body changes the revision");
    });

    Console.WriteLine("Settings");
    await CheckSync("build settings, catalog defaults and DPAPI tokens", () =>
    {
        string buildDir = Path.Combine(root, "build-settings");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, BuildSettings.FileName), "# c\nLAUNCHER_REPOSITORY=https://ghe.test/team/i-luncher\nLAUNCHER_TOKEN=\"read-only\"\n");
        var settings = LauncherSettings.Load(Path.Combine(root, "data"), buildDir);
        Equal("team/i-luncher", settings.CatalogRepository?.FullName, "catalog defaults to the launcher repository");
        Equal("catalog/apps.json", settings.CatalogPath, "default path");
        Equal("read-only", settings.GetToken("ghe.test"), "build token for the launcher host");
        Assert(settings.GetToken("github.com") is null, "build token is not sent to other hosts");
        settings.User.SetToken("https://GHE.test/", "write-token");
        settings.SaveUser();
        string raw = File.ReadAllText(settings.UserSettingsPath);
        Assert(!raw.Contains("write-token"), "token is encrypted on disk");
        Equal("write-token", LauncherSettings.Load(Path.Combine(root, "data"), buildDir).GetToken("ghe.test"), "user token wins after reload");
        settings.User.UseLocalCatalog = true;
        Assert(settings.CatalogRepository is null, "local catalog switch");
    });

    await CheckSync("MASTER_USERS: master-only apps show only to the listed accounts, everyone without the line", () =>
    {
        string buildDir = Path.Combine(root, "master-settings");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, BuildSettings.FileName), "MASTER_USERS=dm, CORP\\Admin2;\n");
        var build = BuildSettings.Load(buildDir);
        Assert(build.IsMaster("dm") && build.IsMaster("DM") && build.IsMaster(@"OTHERPC\admin2") && !build.IsMaster("user01"), "listed accounts only, any domain, any case");
        Assert(new BuildSettings().IsMaster("user01"), "no MASTER_USERS: no restriction");
        var app = CatalogSerializer.Parse("{\"apps\":[{\"id\":\"api-server\",\"name\":\"API\",\"repository\":\"https://github.com/o/a\",\"masterOnly\":true}]}")[0];
        Assert(app.MasterOnly && app.Clone().MasterOnly, "masterOnly is read and kept");
    });

    await CheckSync("Beta and NEW badges come from the API server's app list and stay through a save", () =>
    {
        var apps = CatalogSerializer.Parse("{\"apps\":[{\"id\":\"swift\",\"name\":\"Swift\",\"repository\":\"https://github.com/o/s\",\"beta\":true,\"new\":true}," +
            "{\"id\":\"plain\",\"name\":\"Plain\",\"repository\":\"https://github.com/o/p\"}]}");
        Assert(apps[0].Beta && apps[0].New && !apps[1].Beta && !apps[1].New, "beta and new are read; missing means off");
        var card = new ILauncher.AppCard(apps[0]);
        Assert(card.BetaVisibility == System.Windows.Visibility.Visible && card.NewVisibility == System.Windows.Visibility.Visible &&
            new ILauncher.AppCard(apps[1]).BetaVisibility == System.Windows.Visibility.Collapsed, "the card shows the badges only when set");
        string saved = CatalogSerializer.Serialize(apps);
        var again = CatalogSerializer.Parse(saved);
        Assert(again[0].Beta && again[0].New && !saved.Contains("\"beta\": false") && !again[1].Beta, "a saved list keeps them (and writes nothing for off)");
    });
}
finally
{
    try { Registry.CurrentUser.DeleteSubKeyTree(registryRoot, throwOnMissingSubKey: false); } catch (Exception) { }
    try { using var parent = Registry.CurrentUser.OpenSubKey(@"Software\i-luncher-checks", true); if (parent?.SubKeyCount == 0) Registry.CurrentUser.DeleteSubKey(@"Software\i-luncher-checks", false); } catch (Exception) { }
    try { Directory.Delete(root, recursive: true); } catch (Exception) { }
}

Console.WriteLine();
Console.WriteLine(failed == 0 ? $"All {passed} checks passed." : $"{failed} of {passed + failed} checks FAILED.");
return failed == 0 ? 0 : 1;

/// <summary>Progress&lt;T&gt; 는 SynchronizationContext 로 넘겨 늦게 불릴 수 있어, 확인용으로는 바로 부르는 것</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>API 서버의 /launcher/catalog (relay.py 와 같은 규칙: revision 확인, 관리자 비밀번호)</summary>
sealed class FakeApiServer : HttpMessageHandler
{
    private string _catalog = "{\"schemaVersion\":1,\"apps\":[]}";
    public bool ConflictOnce, Offline;
    public string? LastAuthorization, LastBody;

    private string Revision => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_catalog))).ToLowerInvariant();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline) throw new HttpRequestException("offline");
        if (request.RequestUri!.AbsolutePath != "/launcher/catalog") return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (request.Method == HttpMethod.Get)
        {
            using var doc = JsonDocument.Parse(_catalog);
            return Json(new { schemaVersion = 1, apps = doc.RootElement.GetProperty("apps"), revision = Revision });
        }
        LastAuthorization = request.Headers.Authorization?.ToString();
        LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (LastAuthorization != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:pw")))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("관리자 비밀번호가 필요합니다.") };
        using var body = JsonDocument.Parse(LastBody);
        if (ConflictOnce)
        {
            ConflictOnce = false;
            _catalog = "{\"schemaVersion\":1,\"apps\":[{\"id\":\"other\",\"name\":\"Other\",\"repository\":\"https://github.com/o/other\"}]}";
            return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("\"다른 곳에서 먼저 바꿨습니다.\"") };
        }
        if (body.RootElement.GetProperty("revision").GetString() != Revision) return new HttpResponseMessage(HttpStatusCode.Conflict);
        _catalog = JsonSerializer.Serialize(new { schemaVersion = 1, apps = body.RootElement.GetProperty("apps") });
        using var saved = JsonDocument.Parse(_catalog);
        return Json(new { schemaVersion = 1, apps = saved.RootElement.GetProperty("apps"), revision = Revision });
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
}

/// <summary>GitHub REST API 의 필요한 부분만 흉내 내는 HTTP 처리기 (호스트 ghe.test)</summary>
sealed class FakeGitHub : HttpMessageHandler
{
    public readonly List<string> Requests = [];
    public readonly HashSet<string> Repositories = ["team/i-luncher"];
    public readonly Dictionary<string, (string Content, string Sha)> Files = [];
    public readonly Dictionary<string, byte[]> Assets = [];
    private readonly Dictionary<string, string> _releases = [];
    public bool ConflictOnce, Offline;
    public string? LastPut;
    private int _sha = 1;

    public void AddRelease(string repository, string tag, params (string Name, byte[] Data)[] assets)
    {
        Repositories.Add(repository);
        foreach (var (name, data) in assets) Assets[name] = data;
        _releases[repository] = JsonSerializer.Serialize(new
        {
            tag_name = tag, name = tag, prerelease = false, draft = false, html_url = "https://ghe.test/" + repository + "/releases/" + tag,
            published_at = "2026-11-01T00:00:00Z", body = "notes",
            assets = assets.Select(a => new { name = a.Name, url = "https://ghe.test/api/v3/assets/" + a.Name, browser_download_url = "", size = a.Data.Length })
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.ToString();
        Requests.Add($"{request.Method} {url} {request.Headers.Authorization} {string.Join(",", request.Headers.Accept)}");
        if (Offline) throw new HttpRequestException("offline");
        const string api = "https://ghe.test/api/v3/";
        if (!url.StartsWith(api)) return Status(HttpStatusCode.NotFound);
        string path = Uri.UnescapeDataString(url[api.Length..].Split('?')[0]);
        if (path.StartsWith("assets/")) return Assets.TryGetValue(path[7..], out var bytes) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : Status(HttpStatusCode.NotFound);
        string[] parts = path.Split('/', 4);  // repos/owner/name/rest
        if (parts.Length < 3 || parts[0] != "repos") return Status(HttpStatusCode.NotFound);
        string repository = parts[1] + "/" + parts[2];
        if (!Repositories.Contains(repository)) return Status(HttpStatusCode.NotFound);
        string rest = parts.Length > 3 ? parts[3] : "";
        if (rest == "") return Json(new { name = parts[2], full_name = repository, description = "", default_branch = "main", html_url = "https://ghe.test/" + repository, @private = true, permissions = new { push = true } });
        if (rest == "releases/latest") return _releases.TryGetValue(repository, out var release) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release) } : Status(HttpStatusCode.NotFound);
        if (rest.StartsWith("contents/"))
        {
            string file = rest[9..];
            if (request.Method == HttpMethod.Get)
                return Files.TryGetValue(file, out var entry)
                    ? Json(new { type = "file", sha = entry.Sha, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Content)), encoding = "base64" })
                    : Status(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Put)
            {
                LastPut = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var body = JsonDocument.Parse(LastPut);
                string? sha = body.RootElement.TryGetProperty("sha", out var s) ? s.GetString() : null;
                if (ConflictOnce)
                {
                    ConflictOnce = false;
                    var current = Files[file];
                    var others = CatalogSerializer.Parse(current.Content);
                    others.Add(new AppDefinition { Id = "other", Name = "Other", Repository = "https://ghe.test/team/other" });
                    Files[file] = (CatalogSerializer.Serialize(others), "sha" + ++_sha);
                    return Status(HttpStatusCode.Conflict);
                }
                if (Files.TryGetValue(file, out var existing) && existing.Sha != sha) return Status(HttpStatusCode.Conflict);
                string content = Encoding.UTF8.GetString(Convert.FromBase64String(body.RootElement.GetProperty("content").GetString()!));
                Files[file] = (content, "sha" + ++_sha);
                return Json(new { content = new { sha = Files[file].Sha } });
            }
        }
        return Status(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("{\"message\":\"" + code + "\"}") };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
}
