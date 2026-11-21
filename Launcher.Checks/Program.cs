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

// WPF 그림(아이콘 그리기 등)은 STA 스레드에서
static void Sta(Action body)
{
    Exception? failure = null;
    var thread = new Thread(() => { try { body(); } catch (Exception error) { failure = error; } });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
}

static string WritePng(System.Windows.Media.Imaging.BitmapSource image, string path)
{
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
    using var stream = File.Create(path);
    encoder.Save(stream);
    return path;
}
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

        var velopack = Release("v1.0.501", "EQStatus-win-Setup.exe", "EQStatus-win-Portable.zip", "EQStatus-1.0.501-full.nupkg", "releases.win.json", "RELEASES");
        var eqStatus = new AppDefinition { Id = "eqstatus", Name = "EQ Status", Repository = "https://github.com/a/EQStatus" };
        Equal(InstallKinds.Setup, AppManager.ResolveKind(eqStatus, velopack), "Velopack apps use their installer");
        Equal("--silent", AppManager.DefaultSetupArguments(eqStatus, velopack), "Velopack silent switch");

        var pinned = new AppDefinition { Id = "x", Name = "X", Repository = "https://github.com/a/x", Asset = "X-*-portable.zip" };
        Equal("X-1.2.0-portable.zip", AppManager.SelectZipAsset(pinned, Release("v1.2.0", "X-1.2.0-portable.zip", "X-win-x64.zip"))?.Name, "wildcard asset");
        Assert(AppManager.SelectZipAsset(app, Release("v1", "symbols.zip", "app-linux-x64.zip", "a.zip", "b.zip")) is null, "ambiguous zips are not guessed");
    });
    await CheckSync("install folder name is guessed from the package name", () =>
    {
        Equal("DeskCal", AppManager.GuessInstallName("DeskCal-win-x64.zip"), "DeskCal");
        Equal("TerminalNotes", AppManager.GuessInstallName("TerminalNotes-win-x64.zip"), "TerminalNotes");
        Equal("EQStatus", AppManager.GuessInstallName("EQStatus-win-Setup.exe"), "Velopack setup");
        Equal("MyApp", AppManager.GuessInstallName("MyApp-1.2.3.zip"), "version suffix");
    });

    Console.WriteLine("Icons");
    await CheckSync("repository icon discovery: csproj ApplicationIcon first, then names", () =>
    {
        GitHubTreeEntry F(string path, long size = 5000) => new(path, true, size);
        var entries = new[]
        {
            F("FastCopy.App/FastCopy.App.csproj"), F("FastCopy.Core/FastCopy.Core.csproj"), F("tests/FastCopy.Tests/FastCopy.Tests.csproj"),
            F("FastCopy.App/Assets/FastCopy.ico"), F("FastCopy.App/Assets/FastCopy-128.png"), F("docs/logo.png"), F("FastCopy.App/bin/Release/icon.ico")
        };
        var names = IconDiscovery.Names("FastCopy", null, "FastCopy");
        Equal("FastCopy.App/FastCopy.App.csproj", IconDiscovery.ProjectCandidates(entries, names).FirstOrDefault(), "app project first, tests skipped");
        Assert(!IconDiscovery.ProjectCandidates(entries, names).Any(path => path.Contains("Tests")), "test projects are not read");
        Equal("FastCopy.App/Assets/FastCopy.ico", IconDiscovery.ParseApplicationIcon("<Project><PropertyGroup><ApplicationIcon>Assets/FastCopy.ico</ApplicationIcon></PropertyGroup></Project>", "FastCopy.App/FastCopy.App.csproj"), "relative to the project");
        Equal("cal.ico", IconDiscovery.ParseApplicationIcon("<ApplicationIcon>cal.ico</ApplicationIcon>", "DeskCal.csproj"), "root project");
        Equal("DiskScan.App/Assets/DiskScan.ico", IconDiscovery.ParseApplicationIcon(@"<ApplicationIcon>..\DiskScan.App\Assets\DiskScan.ico</ApplicationIcon>", "DiskScan.Start/DiskScan.Start.csproj"), "parent folder");
        Assert(IconDiscovery.ParseApplicationIcon("<ApplicationIcon>$(IconPath)</ApplicationIcon>", "a.csproj") is null, "MSBuild properties are not guessed");
        Equal("FastCopy.App/Assets/FastCopy.ico", IconDiscovery.ChooseByName(entries, names), "named .ico in Assets, not docs or bin");
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
            [ { "name": "Disk Scan", "repository": "o/DiskScan" }, { "id": "disk-scan", "name": "dup", "repository": "o/x" }, { "name": "no repo" }, ]
            """);
        Equal(1, apps.Count, "count");
        Equal("disk-scan", apps[0].Id, "generated id");
        var renamed = CatalogSerializer.Parse("""
            [ { "id": "fastcopy", "name": "FastCopy", "repository": "o/FastCopy", "formerIds": ["swiftcopy"], "formerInstallNames": ["SwiftCopy"] } ]
            """)[0];
        Assert(renamed.HasId("swiftcopy") && renamed.HasId("fastcopy") && !renamed.HasId("other"), "former ids read");
        Equal("SwiftCopy", renamed.EffectiveFormerInstallNames.Single(), "former install names read");
    });
    await CheckSync("관리자 메뉴: changing an app's id or install folder name keeps the old value for the launcher", () =>
    {
        var data = new System.Text.Json.Nodes.JsonObject { ["id"] = "fastcopy", ["name"] = "FastCopy" };
        Equal("FastCopy", ApiManager.AppRow.EffectiveInstallName(data), "install name falls back to the name");
        ApiManager.ManagerView.KeepFormer(data, "formerIds", "swiftcopy", "fastcopy");
        ApiManager.ManagerView.KeepFormer(data, "formerIds", "swiftcopy", "fastcopy");
        Equal("[\"swiftcopy\"]", data["formerIds"]!.ToJsonString(), "old id kept once");
        ApiManager.ManagerView.KeepFormer(data, "formerInstallNames", "FastCopy", "FastCopy");
        Assert(data["formerInstallNames"] is null, "an unchanged name adds nothing");
        ApiManager.ManagerView.KeepFormer(data, "formerIds", "fastcopy", "swiftcopy");
        Equal("[\"fastcopy\"]", data["formerIds"]!.ToJsonString(), "renamed back: the current id leaves the list, the other one stays");
    });
    await CheckSync("running app: a click brings its window to the front (a minimized one is restored), no window means launch instead", () =>
    {
        Assert(!WindowFront.Activate([]), "no process: nothing to bring forward");
        Sta(() =>
        {
            var window = new System.Windows.Window { Title = "i-luncher checks front", Width = 300, Height = 200, ShowInTaskbar = false,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -2000, Top = 0 };
            window.Show();
            window.WindowState = System.Windows.WindowState.Minimized;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert(WindowFront.Activate([Environment.ProcessId]), "found the test window");
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert(window.WindowState == System.Windows.WindowState.Normal, "minimized window restored: " + window.WindowState);
            window.Close();
        });
        Assert(!WindowFront.ActivateProgram(Path.Combine(root, "missing.exe")), "missing program: launch instead");
    });
    await CheckSync("basic list is built in, takes host and owner from the launcher repository, and a master adds the API list without doubles", () =>
    {
        var launcher = GitHubRepository.Parse("https://ghe.example.com/team/i-luncher")!;
        Equal("https://ghe.example.com/team/DeskCal", BasicApps.ResolveRepository("DeskCal", launcher), "name only");
        Equal("https://ghe.example.com/team/Tool", BasicApps.ResolveRepository("Tool.git", launcher), ".git dropped");
        Equal("other/Tool", BasicApps.ResolveRepository("other/Tool", launcher), "owner/name kept");
        Equal("Tool", BasicApps.ResolveRepository("Tool", null), "no launcher repository: unchanged");
        var basic = BasicApps.Load(launcher);
        string ids = string.Join(",", basic.Select(app => app.Id));
        Equal("deskcal,compare,fastcopy,diskscan,capture,memopad", ids, "built-in basic-apps.json");
        Assert(basic.All(app => app.Repository.StartsWith("https://ghe.example.com/team/") && app.Description.Length > 0 && app.Category.Length > 0
                                && app.Details is { Length: > 0 }), "every basic app has a repository, a category, a description and details for its app page");
        var missing = basic.SelectMany(app => app.Screenshots ?? []).Where(shot => ScreenshotService.BuiltIn(shot) is null).ToList();
        Assert(missing.Count == 0, "screenshots named in basic-apps.json are built in (basic-media): missing " + string.Join(", ", missing));
        var fewShots = basic.Where(app => app.Screenshots is not { Count: >= 3 }).Select(app => app.Id).ToList();
        Assert(fewShots.Count == 0, "every basic app has at least 3 screenshots (user rule): " + string.Join(", ", fewShots));
        var api = new List<AppDefinition>
        {
            new() { Id = "folder-compare", Name = "File Compare", Repository = "team/Compare" },
            new() { Id = "terminal-notes", Name = "Terminal Notes", Repository = "team/TerminalNotes" }
        };
        var master = BasicApps.Combine(basic, api);
        Assert(master.Any(app => app.Id == "terminal-notes"), "API apps are added");
        Equal(1, master.Count(app => app.HasId("compare") || app.HasId("folder-compare")), "same app under its old id is not doubled");
        Assert(master.Any(app => app.Name == "File Compare"), "the API entry wins");
        Equal(basic.Count + 1, master.Count, "count");
    });
    await CheckSync("a basic app removed from the list stays hidden (file outside the install folder) until added again, by id, old id or repository", () =>
    {
        string path = Path.Combine(root, "removed", "removed-apps.json");
        var store = new RemovedAppStore(path);
        Equal(0, store.Load().Count, "nothing removed yet");
        store.Save([new RemovedApp("compare", "Compare", "https://github.com/team/Compare")]);
        var removed = store.Load().Single();
        Equal("Compare", removed.Name, "name kept for the add-again menu");
        var basic = BasicApps.Load(GitHubRepository.Parse("https://github.com/team/i-luncher")!);
        var shown = basic.Where(app => !RemovedAppStore.Matches(removed, app, null)).Select(app => app.Id).ToList();
        Assert(!shown.Contains("compare") && shown.Contains("deskcal") && shown.Count == basic.Count - 1, "only the removed app is hidden: " + string.Join(",", shown));
        Assert(RemovedAppStore.Matches(removed, new AppDefinition { Id = "compare-next", Name = "x", Repository = "team/Compare" }, "github.com"), "same repository under a new id");
        Assert(RemovedAppStore.Matches(removed, new AppDefinition { Id = "renamed", Name = "x", Repository = "https://github.com/team/Other", FormerIds = ["compare"] }, null), "old id");
        store.Save([]);
        Equal(0, store.Load().Count, "added again: shown again");
    });
    await CheckSync("app list keeps fields the launcher no longer shows (제작자 · 상세 설명 · 스크린샷), so saving does not lose them", () =>
    {
        var apps = CatalogSerializer.Parse("""
            [ { "id": "a", "name": "A", "repository": "o/a", "author": "팀", "details": "긴 설명", "screenshots": [ "0123456789abcdef.png" ] } ]
            """);
        string json = CatalogSerializer.Serialize(apps);
        Assert(json.Contains("\"details\": \"긴 설명\"") && json.Contains("0123456789abcdef.png") && json.Contains("\"author\": \"팀\""), json);
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
    await Check("GitHub token in settings: checked against GitHub before it is saved", async () =>
    {
        using var client = new GitHubClient(_ => null, "checks", github);
        Equal("me", await client.GetLoginAsync("ghe.test", "good-token"), "login of a good token");
        try
        {
            await client.GetLoginAsync("ghe.test", "bad-token");
            throw new Exception("a wrong token was accepted");
        }
        catch (GitHubApiException error) { Assert(error.Status == 401 && error.Message.Contains("토큰"), error.Message); }
    });
    await CheckSync("settings: API_SERVER makes the API server the app list (masters only); releases are always asked from GitHub", () =>
    {
        string buildDir = Path.Combine(root, "api-settings");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, BuildSettings.FileName), "API_SERVER=http://10.0.0.5:2003/\nLAUNCHER_REPOSITORY=https://github.com/owner/i-luncher\nMASTER_USERS=boss\n");
        var settings = LauncherSettings.Load(Path.Combine(root, "api-data"), buildDir);
        Equal(LauncherSettings.ModeApi, settings.CatalogMode, "API server first");
        Assert(settings.Build.IsListedMaster(@"CORP\Boss") && !settings.Build.IsListedMaster("someone"), "master by this PC's Windows account");
        Assert(settings.CatalogRepository is null, "no repository catalog in API mode");
        Equal("https://github.com/owner/i-luncher-notices", settings.NoticesRepository?.WebUrl, "notices from GitHub: i-luncher-notices of the launcher's owner by default");
        File.WriteAllText(Path.Combine(buildDir, BuildSettings.FileName), "LAUNCHER_REPOSITORY=https://ghe.example.com/team/i-luncher\nNOTICES_REPOSITORY=board\n");
        Equal("https://ghe.example.com/team/board", LauncherSettings.Load(Path.Combine(root, "api-data"), buildDir).NoticesRepository?.WebUrl, "a name only: same host and owner");
        File.WriteAllText(Path.Combine(buildDir, BuildSettings.FileName), "NOTICES_PATH=notes/공지.txt\n");
        Assert(LauncherSettings.Load(Path.Combine(root, "api-data"), buildDir).NoticesRepository is null, "no launcher repository: this PC's file");
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

    // 이름을 모두 바꾼 앱 (2026-11-16 FastCopy → FastCopy 등): 예전 설치 폴더 이름으로 깔린 것을 찾고, 다음 설치 때 새 이름으로 옮김
    await Check("a renamed app is found under its former install name and moved to the new one on the next install", async () =>
    {
        var before = new AppDefinition { Id = "old-copy", Name = "OldCopy", InstallName = "OldCopy", Repository = "https://ghe.test/team/OldCopy" };
        await installer.InstallZipAsync(before, MakeZip("old1.zip", new() { ["OldCopy.exe"] = "old exe" }), Release("v1.0.0"));
        string oldDir = Path.Combine(programs, "OldCopy");
        var renamed = new AppDefinition { Id = "new-copy", Name = "NewCopy", InstallName = "NewCopy", Repository = "https://ghe.test/team/NewCopy",
            FormerIds = ["old-copy"], FormerInstallNames = ["OldCopy"] };
        var found = installer.Detect(renamed);
        Assert(found is not null && found.InstallName == "OldCopy" && found.Directory == oldDir, "found under the former name");
        Equal("1.0.0", found!.Version?.ToString(), "with its version, so the new release shows as an update");
        var moved = await installer.InstallZipAsync(renamed, MakeZip("new2.zip", new() { ["NewCopy.exe"] = "new exe" }), Release("v1.1.0"));
        Equal(Path.Combine(programs, "NewCopy"), moved.Directory, "installed under the new name");
        Equal("NewCopy", moved.InstallName, "new install name");
        Assert(!Directory.Exists(oldDir), "old folder removed");
        Assert(Registry.CurrentUser.OpenSubKey(registryRoot + @"\OldCopy") is null, "old Windows apps entry removed");
        Equal("1.1.0", installer.Detect(renamed)?.Version?.ToString(), "detected under the new name afterwards");
        await installer.UninstallAsync(renamed, installer.Detect(renamed)!);
        Assert(installer.Detect(renamed) is null, "uninstalled");
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
    await CheckSync("master PC: this PC's git login becomes the token without typing it; a token the user saved stays", () =>
    {
        string buildDir = Path.Combine(root, "gitcred-build");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, BuildSettings.FileName), "LAUNCHER_REPOSITORY=https://github.com/owner/i-luncher\n");
        var settings = LauncherSettings.Load(Path.Combine(root, "gitcred-data"), buildDir);
        string? asked = null;
        Assert(settings.AdoptGitCredential(host => { asked = host; return "git-token"; }), "adopted");
        Equal("github.com", asked, "the launcher's host");
        Equal("git-token", settings.GetToken("github.com"), "used for GitHub");
        Equal("git-token", LauncherSettings.Load(Path.Combine(root, "gitcred-data"), buildDir).GetToken("github.com"), "saved for the apps too");
        Assert(settings.AdoptGitCredential(_ => "git-token-2") && settings.GetToken("github.com") == "git-token-2", "a renewed git login replaces the automatic one");
        Assert(!settings.AdoptGitCredential(_ => null) && settings.GetToken("github.com") == "git-token-2", "no git login: keeps what it has");
        settings.User.SetToken("github.com", "typed-token");
        Assert(!settings.AdoptGitCredential(_ => "git-token-3") && settings.GetToken("github.com") == "typed-token", "a token typed in settings wins");
    });
    await CheckSync("apps ask GitHub with the token this user saved in the launcher settings (only for that host)", () =>
    {
        string data = Path.Combine(root, "token-data");
        var user = new UserSettings();
        user.SetToken("github.com", "gh-user-token");
        user.SetToken("https://GHE.test/", "ghe-user-token");
        user.Save(Path.Combine(data, "settings.json"));
        string? before = Environment.GetEnvironmentVariable("ILUNCHER_DATA_DIR");
        Environment.SetEnvironmentVariable("ILUNCHER_DATA_DIR", data);
        try
        {
            Equal("gh-user-token", LauncherToken.For("https://api.github.com"), "github.com");
            Equal("ghe-user-token", LauncherToken.For("https://ghe.test/api/v3"), "GitHub Enterprise");
            Assert(LauncherToken.For("http://10.0.0.5:2003/api/v3") is null, "never to another server");
            string json = Path.Combine(data, "update.json");
            File.WriteAllText(json, "{\"Api\":\"\",\"Repository\":\"o/App\",\"InstallName\":\"App\"}");
            Equal("gh-user-token", UpdateSettings.Load(json)?.Token, "update.json without a token: the launcher user's");
            File.WriteAllText(json, "{\"Api\":\"https://api.github.com\",\"Repository\":\"o/App\",\"Token\":\"built\",\"InstallName\":\"App\"}");
            Equal("built", UpdateSettings.Load(json)?.Token, "a built token stays first");
        }
        finally { Environment.SetEnvironmentVariable("ILUNCHER_DATA_DIR", before); }
    });
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

        // 같은 앱 두 사본의 둘째 업데이터: 패키지가 이미 적용돼 지워졌으면 실패로 남기지도, 앱을 또 켜지도 않음
        string second = AppUpdater.BuildUpdaterScript(zip, install, "Tool.exe", sleeper.Id, log, "1.4.0", key, restart: true);
        using var again = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(second)))
            { CreateNoWindow = true, UseShellExecute = false })!;
        await again.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
        string secondLog = File.ReadAllText(log).Substring(logText.Length);
        Assert(secondLog.Contains("package already applied") && !secondLog.Contains("failed"), "second updater stops quietly; log: " + secondLog);

        // 업데이트를 받아 둔 채 앱을 제거: 설치 폴더를 다시 만들지 않음
        string removed = Path.Combine(root, "SelfUpdateRemoved");
        string removedZip = Path.Combine(root, "self-removed.zip");
        ZipFile.CreateFromDirectory(pkg, removedZip);
        string third = AppUpdater.BuildUpdaterScript(removedZip, removed, "Tool.exe", sleeper.Id, log, "1.4.0", key, restart: true);
        using var afterRemove = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(third)))
            { CreateNoWindow = true, UseShellExecute = false })!;
        await afterRemove.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
        string thirdLog = File.ReadAllText(log).Substring(logText.Length + secondLog.Length);
        Assert(!Directory.Exists(removed) && !File.Exists(removedZip) && thirdLog.Contains("app removed"), "removed app stays removed; log: " + thirdLog);
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

    await CheckSync("a Windows service with the same exe name running elsewhere does not block an update", () =>
    {
        // svchost runs as services in session 0 and its path cannot be read without admin rights
        string folder = Path.Combine(root, "service-name");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "svchost.exe"), []);
        Assert(Process.GetProcessesByName("svchost").Length > 0, "svchost is running");
        Assert(!AppInstaller.IsRunning(folder), "a folder holding svchost.exe is not 'running'");
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

    await CheckSync("My apps (GitHub projects added on this PC) are saved locally, read back as personal, and never written to the app list", () =>
    {
        var store = new MyAppStore(Path.Combine(root, "my-apps", "my-apps.json"));
        Equal(0, store.Load().Count, "nothing before saving");
        store.Save([new AppDefinition { Id = "my-tool", Name = "Tool", Repository = "https://github.com/someone/Tool", Category = "도구", InstallName = "Tool", Personal = true }]);
        var read = store.Load();
        Assert(read.Count == 1 && read[0].Personal && read[0].Name == "Tool" && read[0].Category == "도구", "read back as a personal app");
        Assert(!File.ReadAllText(Path.Combine(root, "my-apps", "my-apps.json")).Contains("personal", StringComparison.OrdinalIgnoreCase), "the personal flag is not stored");
        Assert(!CatalogSerializer.Serialize(read).Contains("personal", StringComparison.OrdinalIgnoreCase), "and never written into the app list");
        Assert(MyAppStore.SameRepository("https://github.com/Someone/tool", "someone/Tool", null), "same repository whatever the case or form");
        Assert(!MyAppStore.SameRepository("https://github.com/someone/tool", "https://github.com/someone/other", null), "different repositories differ");
        var card = new ILauncher.AppCard(read[0]);
        Assert(card.PersonalMenuVisibility == System.Windows.Visibility.Visible && card.AdminMenuVisibility == System.Windows.Visibility.Collapsed,
            "a personal app offers 삭제 and never 관리자 메뉴에서 편집");
    });

    await CheckSync("Website favorites: an address opens in the browser, keeps its chosen icon, and never offers 파일 위치 열기", () =>
    {
        Equal("https://example.com/", SiteInfo.Normalize("example.com")?.AbsoluteUri, "a bare host becomes https");
        Equal("http://intra:8080/wiki", SiteInfo.Normalize(" http://intra:8080/wiki ")?.AbsoluteUri, "http with a port and path stays");
        Assert(SiteInfo.Normalize("not an address") is null && SiteInfo.Normalize("ftp://files") is null, "spaces and other schemes are refused");
        var store = new FavoriteStore(Path.Combine(root, "favorites-web", "favorites.json"));
        store.Save([new FavoriteProgram { Name = "Wiki", Path = "https://example.com/", Icon = "builtin:web" },
                    new FavoriteProgram { Name = "Tool", Path = Path.Combine(root, "nothing", "tool.exe") }]);
        var read = store.Load();
        Assert(read[0].IsWeb && read[0].Icon == "builtin:web" && !read[1].IsWeb, "the address and the chosen icon are kept");
        Assert(!File.ReadAllText(store.FilePath).Contains("IsWeb"), "IsWeb is worked out, not stored");
        Sta(() =>
        {
            var web = new ILauncher.FavoriteCard(read[0]);
            Assert(web.Exists && web.CellStateText == "웹사이트" && web.RunMenuText == "열기" && web.FolderMenuVisibility == System.Windows.Visibility.Collapsed,
                "a website is always there, says 웹사이트 and 열기, and has no folder");
            Assert(web.Icon is { Width: > 0 }, "the chosen built-in icon shows");
            var gone = new ILauncher.FavoriteCard(read[1]);
            Assert(!gone.Exists && gone.CellStateText == "파일 없음" && gone.FolderMenuVisibility == System.Windows.Visibility.Visible, "a missing program stays a program");
        });
    });

    await CheckSync("Site icons: the page's largest PNG/ICO icon first (relative and <base> links resolved, SVG and mask icons skipped), the name from og:site_name or title", () =>
    {
        string html = """
            <html><head><title> Team &amp; Wiki
            </title>
            <link rel="mask-icon" href="/mask.svg"><link rel="icon" type="image/svg+xml" href="/icon.svg">
            <link rel="icon" href="/favicon-32.png" sizes="32x32"><link rel='apple-touch-icon' href='touch.png'>
            <link href="/big.png" rel="icon" sizes="192x192">
            </head></html>
            """;
        var links = SiteInfo.ParseIconLinks(html, new Uri("https://example.com/docs/page"));
        Equal("https://example.com/big.png|https://example.com/docs/touch.png|https://example.com/favicon-32.png",
            string.Join("|", links.Select(link => link.AbsoluteUri)), "icon order");
        Equal("Team & Wiki", SiteInfo.ParseTitle(html), "title cleaned");
        Equal("Example", SiteInfo.ParseTitle("""<meta property="og:site_name" content="Example"><title>Home | Example</title>"""), "og:site_name wins");
        var based = SiteInfo.ParseIconLinks("""<base href="https://cdn.example.com/assets/"><link rel="shortcut icon" href="fav.ico">""", new Uri("https://example.com/"));
        Equal("https://cdn.example.com/assets/fav.ico", based.Single().AbsoluteUri, "<base href> is honoured");
        Assert(SiteInfo.ParseTitle("<p>no title</p>") is null, "no title gives null");
    });

    await CheckSync("Chosen icons: 18 built-ins render as 128px tiles, image files are copied into the launcher's folder, and only those copies are ever deleted", () =>
    {
        ILauncher.CustomIcons.Folder = Path.Combine(root, "custom-icons");
        Equal(18, ILauncher.CustomIcons.Builtins.Select(item => item.Key).Distinct().Count(), "distinct built-ins");
        Assert(ILauncher.CustomIcons.IsCustom("builtin:web") && !ILauncher.CustomIcons.IsCustom("") && !ILauncher.CustomIcons.IsCustom("Assets/App.png"), "what counts as chosen");
        Assert(ILauncher.CustomIcons.FindBuiltin("builtin:STAR")?.Key == "star" && ILauncher.CustomIcons.FindBuiltin("builtin:nope") is null, "built-ins by name");
        Sta(() =>
        {
            var tile = (System.Windows.Media.Imaging.BitmapSource)ILauncher.CustomIcons.Load("builtin:web")!;
            Assert(tile.PixelWidth == 128 && tile.PixelHeight == 128, "a 128px tile");
            string source = WritePng(tile, Path.Combine(root, "picked.png"));
            string copy = ILauncher.CustomIcons.Import(source);
            Assert(copy.StartsWith(ILauncher.CustomIcons.Folder, StringComparison.OrdinalIgnoreCase) && ILauncher.CustomIcons.Load(copy) is not null, "copied and loadable");
            ILauncher.CustomIcons.DeleteImported(source);
            Assert(File.Exists(source), "a file outside the folder is never deleted");
            ILauncher.CustomIcons.DeleteImported(copy);
            Assert(!File.Exists(copy), "the copy is deleted");
            File.WriteAllText(Path.Combine(root, "not-image.png"), "text");
            try { ILauncher.CustomIcons.Import(Path.Combine(root, "not-image.png")); throw new Exception("a non-image was accepted"); }
            catch (InvalidDataException) { }
        });
    });

    await CheckSync("My app: older 제작자 · 상세 설명 · screenshots and a chosen icon are kept in my-apps.json, and deleting the app removes only its own pictures", () =>
    {
        MyAppStore.MediaFolder = Path.Combine(root, "my-media");
        Directory.CreateDirectory(MyAppStore.MediaFolder);
        string shot = Path.Combine(MyAppStore.MediaFolder, "shot-old.png");
        File.WriteAllBytes(shot, [1, 2, 3]);
        string outside = Path.Combine(root, "shot.png");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var store = new MyAppStore(Path.Combine(root, "my-apps-details", "my-apps.json"));
        store.Save([new AppDefinition { Id = "my-x", Name = "X", Repository = "https://github.com/someone/X", Author = "Team A", Details = "line 1\nline 2",
                                        Screenshots = [shot], Icon = "builtin:code", Personal = true }]);
        var read = store.Load().Single();
        Assert(read.Author == "Team A" && read.Details == "line 1\nline 2" && read.Screenshots?.Single() == shot && read.Icon == "builtin:code", "kept");
        MyAppStore.DeleteMedia(outside);
        Assert(File.Exists(outside), "a picture outside the folder is never deleted");
        MyAppStore.DeleteMedia(shot);
        Assert(!File.Exists(shot), "deleting the app removes its copied screenshot");
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
        if (path == "user")
            return request.Headers.Authorization?.Parameter == "good-token" ? Json(new { login = "me" }) : Status(HttpStatusCode.Unauthorized);
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
