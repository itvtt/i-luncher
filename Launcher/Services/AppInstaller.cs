using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using ILauncher.Models;
using Microsoft.Win32;

namespace ILauncher.Services;

/// <summary>이 PC 에 설치된 앱</summary>
public sealed record InstalledApp(
    string InstallName,
    string Directory,
    string? ExecutablePath,
    ReleaseVersion? Version,
    string? VersionSource,
    string? ExecutableVersion,
    bool ManagedByLauncher,
    string? UninstallCommand,
    string? QuietUninstallCommand);

public sealed class AppRunningException(string name)
    : InvalidOperationException($"{name}이(가) 실행 중이라 파일을 바꿀 수 없습니다. {name}을(를) 종료한 뒤 다시 시도하세요.");

public sealed class RuntimeMissingException(string framework, string version)
    : InvalidOperationException($"이 앱을 실행하려면 {FriendlyName(framework)} {version} 이상이 필요한데 이 PC에 없습니다.")
{
    public string Framework { get; } = framework;
    public string RequiredVersion { get; } = version;
    public string DownloadUrl => $"https://dotnet.microsoft.com/download/dotnet/{RequiredVersion.Split('.')[0]}.0";

    private static string FriendlyName(string framework) => framework switch
    {
        "Microsoft.WindowsDesktop.App" => ".NET Desktop Runtime",
        "Microsoft.AspNetCore.App" => "ASP.NET Core Runtime",
        _ => ".NET Runtime"
    };
}

/// <summary>
/// 앱을 사용자 폴더에 설치·업데이트·제거. 앱들의 자체 설치 프로그램(DeskCal-Setup.exe 등)과 같은 규칙을 씀:
///   설치 폴더   %LOCALAPPDATA%\Programs\&lt;InstallName&gt;
///   "앱" 목록   HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\&lt;InstallName&gt; (InstallLocation, DisplayVersion)
///   바로 가기   시작 메뉴\프로그램\&lt;이름&gt;.lnk
/// 그래서 런처로 설치한 앱을 따로 실행해도 앱의 자체 업데이터가 "설치된 사본"으로 알아보고 스스로 업데이트함.
/// 관리자 권한은 필요 없음.
/// </summary>
public sealed class AppInstaller
{
    public const string ManifestFileName = ".i-luncher.json";
    public const string DefaultUninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly string _programsRoot;
    private readonly string _uninstallRoot;
    private readonly string? _shortcutDirectory;
    private readonly string _launcherCommand;
    private readonly string? _launcherRuntime;
    private readonly bool _useGlobalRuntimes;
    /// <summary>지운 앱이 Windows 에 남긴 등록(시작 프로그램 · 트레이 아이콘 설정 · 바탕화면 바로 가기 …)을 지움. 시험(다른 "앱" 목록 키)에서는 넘긴 것만</summary>
    private readonly SystemLeftovers? _leftovers;

    public AppInstaller(string? programsRoot = null, string? uninstallRoot = null, string? shortcutDirectory = null,
        string? launcherCommand = null, string? launcherRuntime = null, bool useGlobalRuntimes = true, bool createShortcuts = true,
        SystemLeftovers? leftovers = null)
    {
        _leftovers = leftovers ?? (uninstallRoot is null ? SystemLeftovers.ForCurrentUser() : null);
        _programsRoot = programsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        _uninstallRoot = uninstallRoot ?? DefaultUninstallRoot;
        _shortcutDirectory = createShortcuts ? shortcutDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs) : null;
        _launcherCommand = launcherCommand ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "i-luncher.exe");
        _launcherRuntime = launcherRuntime ?? Path.Combine(AppContext.BaseDirectory, "dotnet");
        _useGlobalRuntimes = useGlobalRuntimes;
    }

    public string ProgramsRoot => _programsRoot;
    public string GetInstallDirectory(AppDefinition app) => Path.Combine(_programsRoot, app.EffectiveInstallName);
    private string KeyPath(string installName) => _uninstallRoot + "\\" + installName;

    // ───────────────────────── 확인 ─────────────────────────

    /// <summary>지금 설치 폴더 이름으로, 없으면 예전 이름(FormerInstallNames)으로 설치된 것. 예전 이름이면 InstalledApp.InstallName 이 그 이름</summary>
    public InstalledApp? Detect(AppDefinition app)
    {
        if (DetectAs(app, app.EffectiveInstallName) is { } current) return current;
        foreach (string former in app.EffectiveFormerInstallNames)
            if (DetectAs(app, former) is { } old) return old;
        return null;
    }

    private InstalledApp? DetectAs(AppDefinition app, string installName)
    {
        string? directory = null, displayVersion = null, uninstall = null, quietUninstall = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath(installName));
            if (key?.GetValue("InstallLocation") is string location && location.Trim().Length > 0)
                directory = Path.GetFullPath(location.Trim().Trim('"'));
            displayVersion = key?.GetValue("DisplayVersion") as string;
            uninstall = key?.GetValue("UninstallString") as string;
            quietUninstall = key?.GetValue("QuietUninstallString") as string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }

        // "앱" 목록 기록이 지워졌어도 런처가 관리하는 폴더면 설치된 것으로 봄
        string defaultDirectory = Path.Combine(_programsRoot, installName);
        if ((directory is null || !Directory.Exists(directory)) && File.Exists(Path.Combine(defaultDirectory, ManifestFileName)))
            directory = defaultDirectory;
        if (directory is null || !Directory.Exists(directory)) return null;

        InstallManifest? manifest = ReadManifest(directory);
        string? executable = ResolveExecutable(app, directory, manifest?.Executable);
        string? executableVersion = ReadFileVersion(executable);

        // 버전: 런처가 설치한 그대로의 실행 파일이면 설치한 릴리스 버전 (실행 파일에 버전이 잘못 찍혀도 업데이트가 반복되지 않게).
        // 앱이 스스로 업데이트해 실행 파일이 바뀌었으면 실행 파일의 버전, 그것도 없으면 "앱" 목록의 DisplayVersion
        ReleaseVersion? version = null;
        string? source = null;
        if (manifest is not null && executable is not null && manifest.MatchesExecutable(directory, executable) &&
            ReleaseVersion.Parse(manifest.Version) is { } recorded)
        {
            version = recorded;
            source = "설치한 릴리스";
        }
        else if (ReleaseVersion.Parse(executableVersion) is { } fileVersion)
        {
            version = fileVersion;
            source = "실행 파일";
        }
        else if (ReleaseVersion.Parse(displayVersion) is { } registered)
        {
            version = registered;
            source = "Windows 앱 목록";
        }
        bool managed = manifest is not null || (uninstall?.Contains("--uninstall", StringComparison.OrdinalIgnoreCase) ?? false);
        return new InstalledApp(installName, directory, executable, version, source, executableVersion, managed, uninstall, quietUninstall);
    }

    /// <summary>실행 파일 찾기: 지정한 경로 → 설치 기록 → 앱 이름과 같은 exe → 하나뿐인 exe. Velopack 처럼 current\ 아래에 있는 것도 찾음</summary>
    public static string? ResolveExecutable(AppDefinition app, string directory, string? recorded = null)
    {
        foreach (string? relative in new[] { app.Executable, recorded })
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) continue;
            foreach (string folder in new[] { directory, Path.Combine(directory, "current") })
            {
                string candidate = Path.GetFullPath(Path.Combine(folder, relative!));
                if (IsInside(candidate, directory) && File.Exists(candidate)) return candidate;
            }
        }
        foreach (string folder in new[] { directory, Path.Combine(directory, "current") })
        {
            if (!Directory.Exists(folder)) continue;
            string[] executables = Directory.GetFiles(folder, "*.exe").Where(path => !IsHelperExecutable(path)).ToArray();
            string[] names = new[] { app.EffectiveInstallName, app.Name, app.Name.Replace(" ", ""), Repositoryname(app) }
                .Where(name => name.Length > 0).ToArray();
            string? named = executables.FirstOrDefault(path => names.Any(name =>
                string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase)));
            if (named is not null) return named;
            if (executables.Length == 1) return executables[0];
        }
        return null;
    }

    private static string Repositoryname(AppDefinition app) => app.Repository.TrimEnd('/').Split('/').LastOrDefault()?.Replace(".git", "") ?? "";

    private static bool IsHelperExecutable(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("createdump", StringComparison.OrdinalIgnoreCase)
            || name.Equals("uninstall", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Update", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Squirrel", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
            || name.Contains("crashpad", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ReadFileVersion(string? executable)
    {
        if (executable is null || !File.Exists(executable)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executable);
            string? product = info.ProductVersion?.Trim();
            if (ReleaseVersion.Parse(product) is not null) return product;
            string? file = info.FileVersion?.Trim();
            return ReleaseVersion.Parse(file) is not null ? file : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    // ───────────────────────── 설치 (zip) ─────────────────────────

    /// <summary>
    /// 릴리스 zip 을 설치 폴더에 풀어 설치하거나 업데이트. 바뀌는 파일은 먼저 옆으로 옮겨 두었다가
    /// 실패하면 되돌리므로 중간에 멈춰도 이전 버전이 남음. 이전 버전에만 있던 파일은 지우고,
    /// 런처가 설치하지 않은 파일(사용자 데이터, dotnet\ 실행 환경)은 그대로 둠.
    /// </summary>
    public async Task<InstalledApp> InstallZipAsync(AppDefinition app, string zipPath, GitHubRelease? release, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string target = GetInstallDirectory(app);
        bool update = Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any();
        Directory.CreateDirectory(_programsRoot);
        // 같은 드라이브의 임시 폴더: 옮기기가 빠르고, 설치 폴더 이름과 겹치지 않음
        string staging = Path.Combine(_programsRoot, $".{app.EffectiveInstallName}.new-{Guid.NewGuid():N}");
        string backup = Path.Combine(_programsRoot, $".{app.EffectiveInstallName}.old-{Guid.NewGuid():N}");
        try
        {
            progress?.Report("압축 푸는 중…");
            await Task.Run(() => ExtractSafely(zipPath, staging), cancellationToken).ConfigureAwait(false);
            string content = UnwrapSingleFolder(staging);
            string? executable = ResolveExecutable(app, content)
                ?? throw new InvalidDataException("패키지에서 실행 파일을 찾지 못했습니다. 앱 편집에서 실행 파일 이름을 지정하세요.");
            string executableRelative = Path.GetRelativePath(content, executable);

            var requirement = ReadRuntimeRequirement(executable);
            bool copyLauncherRuntime = false;
            if (requirement is { } needed && !IsRuntimeAvailable(needed, Path.Combine(target, "dotnet")))
            {
                if (_launcherRuntime is not null && IsRuntimeRoot(_launcherRuntime, needed)) copyLauncherRuntime = true;
                else throw new RuntimeMissingException(needed.Name, needed.Version);
            }

            if (Directory.Exists(target) && IsRunning(target))
                throw new AppRunningException(app.Name);

            progress?.Report(Directory.Exists(target) ? "파일 바꾸는 중…" : "파일 복사 중…");
            InstallManifest? previous = ReadManifest(target);
            var newFiles = Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(content, path)).ToList();
            await Task.Run(() => ApplyFiles(content, target, backup, newFiles, previous?.Files ?? []), cancellationToken).ConfigureAwait(false);

            if (copyLauncherRuntime)
            {
                progress?.Report(".NET 실행 환경 복사 중…");
                await Task.Run(() => CopyDirectory(_launcherRuntime!, Path.Combine(target, "dotnet")), cancellationToken).ConfigureAwait(false);
            }

            string installedExecutable = Path.Combine(target, executableRelative);
            var file = new FileInfo(installedExecutable);
            // 스스로 업데이트하는 앱은 릴리스 태그가 그 앱의 번호가 아님 (같은 저장소의 다른 제품): 실행 파일의 버전
            string versionText = (app.SelfUpdate ? ReadFileVersion(installedExecutable) : null) ?? release?.Version?.ToString() ?? ReadFileVersion(installedExecutable) ?? "";
            var manifest = new InstallManifest
            {
                AppId = app.Id,
                Repository = app.Repository,
                Tag = release?.Tag ?? "",
                Version = versionText,
                Executable = executableRelative,
                ExecutableSize = file.Length,
                ExecutableWriteTimeUtc = file.LastWriteTimeUtc,
                InstalledAt = DateTimeOffset.Now,
                Files = newFiles
            };
            WriteManifest(target, manifest);

            progress?.Report("시작 메뉴에 등록 중…");
            Register(app, target, installedExecutable, versionText);
            TryCreateShortcut(app, installedExecutable, target);
            RemoveFormerInstalls(app);
            // 같은 경로의 새 실행 파일: 탐색기 · 시작 메뉴가 예전 아이콘을 계속 보이지 않게
            _leftovers?.RefreshIcons(_shortcutDirectory is null ? [installedExecutable]
                : [installedExecutable, Path.Combine(_shortcutDirectory, AppDefinition.SafeFolderName(app.Name) + ".lnk")]);
            ILuncher.AppKit.AppUsage.Count(update ? "update" : "install");
            return Detect(app) ?? throw new IOException("설치한 앱을 확인하지 못했습니다: " + target);
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteDirectory(backup);
        }
    }

    private static void ExtractSafely(string zipPath, string destination)
    {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0) throw new InvalidDataException("패키지가 비어 있습니다.");
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("패키지에 설치 폴더 밖을 가리키는 파일이 있습니다: " + entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    // zip 안에 폴더 하나만 있으면 (MyApp-1.2.3\...) 그 안이 설치 내용
    private static string UnwrapSingleFolder(string staging)
    {
        string current = staging;
        for (int depth = 0; depth < 2; depth++)
        {
            if (Directory.EnumerateFiles(current).Any()) break;
            string[] folders = Directory.GetDirectories(current);
            if (folders.Length != 1) break;
            current = folders[0];
        }
        return current;
    }

    private static void ApplyFiles(string source, string target, string backup, IReadOnlyList<string> newFiles, IReadOnlyList<string> previousFiles)
    {
        var moved = new List<string>();      // 옆으로 옮긴 기존 파일 (되돌릴 때 다시 넣음)
        var created = new List<string>();    // 새로 쓴 파일 (되돌릴 때 지움)
        try
        {
            Directory.CreateDirectory(target);
            var newSet = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
            // 이전 버전에만 있던 파일 (런처가 설치한 것만)
            var obsolete = previousFiles.Where(file => !newSet.Contains(file) && IsSafeRelative(file)).ToList();
            foreach (string relative in newFiles.Concat(obsolete))
            {
                string destination = Path.Combine(target, relative);
                if (!File.Exists(destination)) continue;
                string saved = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                MoveWithRetry(destination, saved);
                moved.Add(relative);
            }
            foreach (string relative in newFiles)
            {
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(source, relative), destination, overwrite: true);
                created.Add(relative);
            }
            foreach (string relative in obsolete) RemoveEmptyParents(Path.Combine(target, relative), target);
        }
        catch
        {
            foreach (string relative in created)
                try { File.Delete(Path.Combine(target, relative)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            foreach (string relative in moved)
                try
                {
                    string destination = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(Path.Combine(backup, relative), destination, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static bool IsSafeRelative(string relative) =>
        !Path.IsPathRooted(relative) && !relative.Split('\\', '/').Contains("..") && !relative.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase);

    private static void MoveWithRetry(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(source, destination, overwrite: true); return; }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < 9) { Thread.Sleep(300); }
        }
    }

    private static void RemoveEmptyParents(string file, string root)
    {
        string? folder = Path.GetDirectoryName(file);
        string stop = Path.GetFullPath(root).TrimEnd('\\');
        while (folder is not null && !string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), stop, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) return;
                Directory.Delete(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            folder = Path.GetDirectoryName(folder);
        }
    }

    // ───────────────────────── 설치 (설치 프로그램) ─────────────────────────

    /// <summary>릴리스의 설치 프로그램을 실행하고 끝나길 기다림. .msi 는 msiexec 로</summary>
    public async Task<InstalledApp?> RunSetupAsync(AppDefinition app, string setupPath, string? arguments, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        InstalledApp? before = Detect(app);
        if (before is not null && IsRunning(before.Directory)) throw new AppRunningException(app.Name);
        progress?.Report("설치 프로그램 실행 중…");
        bool msi = setupPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        var start = msi
            ? new ProcessStartInfo("msiexec.exe", $"/i \"{setupPath}\" {arguments ?? "/qn /norestart"}")
            : new ProcessStartInfo(setupPath, arguments ?? "/S");
        start.UseShellExecute = true;
        start.WorkingDirectory = Path.GetDirectoryName(setupPath)!;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("설치 프로그램을 시작하지 못했습니다.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode is not (0 or 3010 or 1641))
            throw new InvalidOperationException($"설치 프로그램이 오류로 끝났습니다 (종료 코드 {process.ExitCode}).");
        // Velopack 처럼 설치를 마친 뒤 앱을 띄우는 설치 프로그램도 있음: 등록될 때까지 잠깐 기다림
        // 이름을 바꾼 앱이면 새 이름으로 설치된 것을 기다린 뒤 예전 이름의 설치를 지움
        for (int i = 0; i < 20; i++)
        {
            if (DetectAs(app, app.EffectiveInstallName) is { } installed)
            {
                RemoveFormerInstalls(app);
                if (installed.ExecutablePath is { } executable) _leftovers?.RefreshIcons([executable]);
                return installed;
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    // ───────────────────────── 실행 · 제거 ─────────────────────────

    public void Launch(AppDefinition app, InstalledApp installed)
    {
        string executable = installed.ExecutablePath ?? throw new FileNotFoundException("실행 파일을 찾지 못했습니다. 앱 편집에서 실행 파일 이름을 지정하세요.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,  // 관리자 권한을 요구하는 앱(DiskScan)도 UAC 확인으로 시작
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        if (!string.IsNullOrWhiteSpace(app.Arguments)) start.Arguments = app.Arguments;
        Process.Start(start)?.Dispose();
    }

    /// <summary>설치 폴더 안의 실행 파일로 돌고 있는 프로세스 번호 (창을 앞으로 가져올 때)</summary>
    public static List<int> RunningProcessIds(string directory)
    {
        var running = FindRunning(directory);
        var ids = running.Select(process => process.Id).ToList();
        foreach (Process process in running) process.Dispose();
        return ids;
    }

    /// <summary>설치 폴더 안의 실행 파일로 돌고 있는 프로세스가 있는지</summary>
    public static bool IsRunning(string directory)
    {
        var running = FindRunning(directory);
        foreach (Process process in running) process.Dispose();
        return running.Count > 0;
    }

    /// <summary>
    /// 여러 설치 폴더 중 지금 실행 중인 폴더 (홈 화면 · 독의 "실행 중" 표시용). 프로세스 목록은 한 번만 읽고,
    /// 이름이 맞는 프로세스만 경로를 확인함. 경로를 못 읽는 프로세스(관리자 권한)는 그 이름의 폴더가 하나일 때만, Windows 서비스(세션 0)는 셈하지 않음
    /// </summary>
    public static HashSet<string> RunningDirectories(IEnumerable<string> directories)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string folder in new[] { directory, Path.Combine(directory, "current") })
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    foreach (string exe in Directory.EnumerateFiles(folder, "*.exe"))
                    {
                        string name = Path.GetFileNameWithoutExtension(exe);
                        if (!byName.TryGetValue(name, out var owners)) byName[name] = owners = [];
                        if (!owners.Contains(directory, StringComparer.OrdinalIgnoreCase)) owners.Add(directory);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        if (byName.Count == 0) return running;
        foreach (Process process in Process.GetProcesses())
            using (process)
            {
                if (!byName.TryGetValue(process.ProcessName, out var owners)) continue;
                try { if (process.SessionId == 0) continue; } catch (InvalidOperationException) { continue; }
                string? path;
                try { path = process.MainModule?.FileName; }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { path = null; }
                foreach (string directory in owners)
                    if (path is null ? owners.Count == 1 : IsInside(path, directory)) running.Add(directory);
            }
        return running;
    }

    /// <summary>설치 폴더 안의 실행 파일로 도는 프로세스 (부른 쪽이 Dispose)</summary>
    private static List<Process> FindRunning(string directory)
    {
        var found = new List<Process>();
        if (!Directory.Exists(directory)) return found;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in new[] { directory, Path.Combine(directory, "current") })
            if (Directory.Exists(folder))
                foreach (string exe in Directory.EnumerateFiles(folder, "*.exe")) names.Add(Path.GetFileNameWithoutExtension(exe));
        foreach (string name in names)
            foreach (Process process in Process.GetProcessesByName(name))
            {
                bool inside;
                try { inside = process.MainModule?.FileName is { } path && IsInside(path, directory); }
                // 관리자 권한으로 도는 프로세스는 경로를 못 읽음: 같은 이름이면 실행 중으로 봄 (파일을 망가뜨리지 않게).
                // 단 Windows 서비스(세션 0)는 등록된 실행 파일이 이 폴더 밖이면 아님 (예: File Shared 서비스 사본)
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    inside = !IsService(process) || ServiceRunsFrom(directory);
                }
                if (inside) found.Add(process);
                else process.Dispose();
            }
        return found;
    }

    /// <summary>
    /// 설치 폴더에서 도는 앱을 닫음. Windows 다시 시작 관리자(Restart Manager)로 Windows를 끌 때와 같은 종료 요청을 보내
    /// 앱이 스스로 정리하고 닫히게 함 (트레이로 숨는 앱도 닫힘, 앱이 거부하면 닫히지 않음). force 면 남은 프로세스를 강제로 끝냄.
    /// 모두 끝났으면 true
    /// </summary>
    public static async Task<bool> CloseRunningAsync(string directory, bool force)
    {
        var running = FindRunning(directory);
        try
        {
            if (running.Count == 0) return true;
            var targets = new List<RmUniqueProcess>();
            foreach (Process process in running)
                try
                {
                    long started = process.StartTime.ToFileTime();
                    targets.Add(new RmUniqueProcess { ProcessId = process.Id, StartLow = (uint)started, StartHigh = (uint)(started >> 32) });
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            if (targets.Count > 0) await Task.Run(() => RestartManagerShutdown(targets.ToArray(), force)).ConfigureAwait(false);
            // 종료 요청을 받은 앱이 정리를 마치고 끝날 때까지 기다림
            for (int wait = 0; wait < 150 && IsRunning(directory); wait++) await Task.Delay(100).ConfigureAwait(false);
            return !IsRunning(directory);
        }
        finally
        {
            foreach (Process process in running) process.Dispose();
        }
    }

    private static void RestartManagerShutdown(RmUniqueProcess[] targets, bool force)
    {
        if (RmStartSession(out uint session, 0, new System.Text.StringBuilder(64)) != 0) return;
        try
        {
            if (RmRegisterResources(session, 0, null, (uint)targets.Length, targets, 0, null) != 0) return;
            RmShutdown(session, force ? RmForceShutdown : 0, IntPtr.Zero);
        }
        finally { RmEndSession(session); }
    }

    private const uint RmForceShutdown = 0x1;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RmUniqueProcess { public int ProcessId; public uint StartLow, StartHigh; }  // RM_UNIQUE_PROCESS (FILETIME = 두 DWORD)

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, System.Text.StringBuilder sessionKey);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[]? files, uint processCount, RmUniqueProcess[] processes,
        uint serviceCount, string[]? services);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint session, uint flags, IntPtr status);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    /// <summary>
    /// 런처가 설치한 앱은 바로 가기·"앱" 목록·설치 폴더를 지움. 앱의 자체 설치 프로그램으로 설치된 앱은 그 제거 프로그램을 실행.
    /// 둘 다 끝나면 그 앱이 Windows 에 남긴 시스템 등록(시작 프로그램 · 트레이 아이콘 설정 · 바탕화면 바로 가기 …)도 지움 (SystemLeftovers).
    /// 설치 폴더 밖의 사용자 데이터는 건드리지 않음.
    /// </summary>
    public async Task UninstallAsync(AppDefinition app, InstalledApp installed, CancellationToken cancellationToken = default)
    {
        if (IsRunning(installed.Directory)) throw new AppRunningException(app.Name);
        if (!installed.ManagedByLauncher && (installed.QuietUninstallCommand ?? installed.UninstallCommand) is { Length: > 0 } command)
        {
            var names = SystemLeftovers.ExecutableNames(installed.Directory);
            var (file, arguments) = SplitCommand(command);
            using Process process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })
                ?? throw new InvalidOperationException("제거 프로그램을 시작하지 못했습니다.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            // 제거 프로그램이 앱을 실제로 지웠을 때만 (사용자가 취소했으면 등록을 그대로 둠)
            if (installed.ExecutablePath is not { } executable || !File.Exists(executable))
                _leftovers?.RemoveFor(installed.Directory, names);
            return;
        }
        RemoveManaged(installed.InstallName, installed.Directory, app.Name);
    }

    /// <summary>i-luncher.exe --uninstall &lt;InstallName&gt; (Windows "앱" 목록의 제거 버튼)</summary>
    public void UninstallByName(string installName)
    {
        string? directory;
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath(installName)))
            directory = key?.GetValue("InstallLocation") as string;
        directory ??= Path.Combine(_programsRoot, installName);
        if (Directory.Exists(directory) && IsRunning(directory)) throw new AppRunningException(installName);
        RemoveManaged(installName, directory, installName);
    }

    /// <summary>
    /// 이름을 바꾼 앱: 새 이름으로 설치한 뒤 예전 이름의 설치(폴더 · "앱" 목록 기록 · 바로 가기)를 지움.
    /// 예전 앱이 실행 중이거나 지우지 못하면 그대로 두고 다음 설치 때 다시 함 (새 설치는 이미 끝났으므로 실패로 치지 않음)
    /// </summary>
    public void RemoveFormerInstalls(AppDefinition app)
    {
        string current = Path.GetFullPath(GetInstallDirectory(app)).TrimEnd('\\');
        foreach (string former in app.EffectiveFormerInstallNames)
        {
            if (DetectAs(app, former) is not { } old || !old.ManagedByLauncher) continue;
            if (string.Equals(Path.GetFullPath(old.Directory).TrimEnd('\\'), current, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (IsRunning(old.Directory)) continue;
                RemoveManaged(former, old.Directory, app.Name);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    private void RemoveManaged(string installName, string directory, string displayName)
    {
        string full = Path.GetFullPath(directory);
        // 안전장치: 런처가 관리하는 폴더(기록 파일이 있거나 Programs 아래의 같은 이름)만 통째로 지움
        bool owned = File.Exists(Path.Combine(full, ManifestFileName)) ||
                     string.Equals(full.TrimEnd('\\'), Path.Combine(_programsRoot, installName).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (_shortcutDirectory is not null)
            foreach (string link in Directory.Exists(_shortcutDirectory) ? Directory.GetFiles(_shortcutDirectory, "*.lnk") : [])
                if (ShortcutTarget(link) is { } linked && IsInside(linked, full))
                    TryDeleteFile(link);
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath(installName), throwOnMissingSubKey: false);
        var names = SystemLeftovers.ExecutableNames(full);
        if (owned && Directory.Exists(full))
        {
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Delete(full, recursive: true); break; }
                catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < 5) { Thread.Sleep(400); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"{displayName} 설치 폴더의 일부 파일을 지우지 못했습니다: {full}\n{error.Message}");
                }
            }
        }
        // 앱 파일이 없어졌으면 그 앱이 Windows 에 남긴 등록도 (런처가 관리하지 않는 폴더라 남겨 둔 앱은 그대로)
        if (!Directory.Exists(full)) _leftovers?.RemoveFor(full, names);
    }

    private static (string File, string Arguments) SplitCommand(string command)
    {
        string text = command.Trim();
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            if (end > 0) return (text[1..end], text[(end + 1)..].Trim());
        }
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? (text[..(exe + 4)], text[(exe + 4)..].Trim()) : (text, "");
    }

    // ───────────────────────── 등록 ─────────────────────────

    private void Register(AppDefinition app, string target, string executable, string version)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath(app.EffectiveInstallName));
        // 앱의 자체 설치 프로그램이 둔 제거 프로그램이 폴더에 있으면 그것을 유지
        bool keepUninstaller = key.GetValue("UninstallString") is string existing && !existing.Contains("--uninstall", StringComparison.OrdinalIgnoreCase) &&
            SplitCommand(existing).File is { } file && File.Exists(file) && IsInside(file, target);
        long sizeKb = 0;
        try { sizeKb = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) / 1024; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        key.SetValue("DisplayName", app.Name);
        if (version.Length > 0) key.SetValue("DisplayVersion", version);
        if (key.GetValue("Publisher") is null) key.SetValue("Publisher", "i-luncher");
        key.SetValue("InstallLocation", target);
        key.SetValue("DisplayIcon", executable);
        if (!keepUninstaller)
        {
            key.SetValue("UninstallString", $"\"{_launcherCommand}\" --uninstall \"{app.EffectiveInstallName}\"");
            key.SetValue("QuietUninstallString", $"\"{_launcherCommand}\" --uninstall \"{app.EffectiveInstallName}\" --quiet");
        }
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("ILuncherAppId", app.Id);
        key.SetValue("ILuncherRepository", app.Repository);
        if (!string.IsNullOrEmpty(app.Description)) key.SetValue("Comments", app.Description);
    }

    private void TryCreateShortcut(AppDefinition app, string executable, string workingDirectory)
    {
        if (_shortcutDirectory is null) return;
        try
        {
            Directory.CreateDirectory(_shortcutDirectory);
            string path = Path.Combine(_shortcutDirectory, AppDefinition.SafeFolderName(app.Name) + ".lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell 없음");
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = executable;
                link.WorkingDirectory = Path.GetDirectoryName(executable) ?? workingDirectory;
                link.IconLocation = executable + ",0";
                link.Description = string.IsNullOrWhiteSpace(app.Description) ? app.Name : app.Description;
                if (!string.IsNullOrWhiteSpace(app.Arguments)) link.Arguments = app.Arguments;
                link.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Debug.WriteLine("Shortcut failed: " + error.Message);  // 바로 가기는 없어도 런처에서 실행 가능
        }
    }

    private static string? ShortcutTarget(string path)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try { return (string)shell.CreateShortcut(path).TargetPath; }
        catch (Exception) { return null; }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    // ───────────────────────── .NET 실행 환경 ─────────────────────────

    public sealed record RuntimeRequirement(string Name, string Version);

    /// <summary>프레임워크 의존 .NET 앱이면 필요한 공유 프레임워크 (Desktop 이 있으면 그것). 자체 포함·네이티브 앱은 null</summary>
    public static RuntimeRequirement? ReadRuntimeRequirement(string executable)
    {
        string config = Path.ChangeExtension(executable, ".runtimeconfig.json");
        if (!File.Exists(config)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(config), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var options)) return null;
            if (options.TryGetProperty("includedFrameworks", out _)) return null;
            var frameworks = new List<RuntimeRequirement>();
            if (options.TryGetProperty("framework", out var single)) frameworks.Add(Read(single));
            if (options.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array)
                frameworks.AddRange(many.EnumerateArray().Select(Read));
            frameworks.RemoveAll(item => item.Name.Length == 0);
            return frameworks.FirstOrDefault(item => item.Name == "Microsoft.WindowsDesktop.App")
                ?? frameworks.FirstOrDefault(item => item.Name == "Microsoft.AspNetCore.App")
                ?? frameworks.FirstOrDefault();
        }
        catch (JsonException) { return null; }

        static RuntimeRequirement Read(JsonElement item) => new(
            item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            item.TryGetProperty("version", out var version) ? version.GetString() ?? "0.0.0" : "0.0.0");
    }

    public bool IsRuntimeAvailable(RuntimeRequirement requirement, string appRuntime)
    {
        if (IsRuntimeRoot(appRuntime, requirement)) return true;
        return _useGlobalRuntimes && GlobalDotNetRoots().Any(root => IsRuntimeRoot(root, requirement));
    }

    private static IEnumerable<string> GlobalDotNetRoots()
    {
        string? registered = null;
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? setup = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            registered = setup?.GetValue("InstallLocation") as string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            registered,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
        }.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => root!).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>root 가 requirement 를 실행할 수 있는 dotnet 루트인지: 같은 주 버전에서 요구 버전 이상 + host\fxr</summary>
    public static bool IsRuntimeRoot(string root, RuntimeRequirement requirement)
    {
        try
        {
            if (!Version.TryParse(requirement.Version.Split('-')[0], out Version? needed)) needed = new Version(0, 0);
            string shared = Path.Combine(root, "shared", requirement.Name);
            bool frameworkPresent = Directory.Exists(shared) && Directory.EnumerateDirectories(shared).Any(folder =>
                Version.TryParse(Path.GetFileName(folder).Split('-')[0], out Version? available) &&
                available.Major == needed.Major && available >= needed);
            bool coreNeeded = requirement.Name != "Microsoft.NETCore.App";
            bool corePresent = !coreNeeded || IsRuntimeRoot(root, requirement with { Name = "Microsoft.NETCore.App" });
            string fxr = Path.Combine(root, "host", "fxr");
            return frameworkPresent && corePresent && Directory.Exists(fxr) && Directory.EnumerateDirectories(fxr).Any();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // ───────────────────────── 설치 기록 ─────────────────────────

    public sealed class InstallManifest
    {
        public string AppId { get; set; } = "";
        public string Repository { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Version { get; set; } = "";
        public string Executable { get; set; } = "";
        public long ExecutableSize { get; set; }
        public DateTime ExecutableWriteTimeUtc { get; set; }
        public DateTimeOffset InstalledAt { get; set; }
        public List<string> Files { get; set; } = [];

        public bool MatchesExecutable(string directory, string executable)
        {
            if (!string.Equals(Path.GetFullPath(Path.Combine(directory, Executable)), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return false;
            var file = new FileInfo(executable);
            return file.Exists && file.Length == ExecutableSize && Math.Abs((file.LastWriteTimeUtc - ExecutableWriteTimeUtc).TotalSeconds) < 2;
        }
    }

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static InstallManifest? ReadManifest(string directory)
    {
        string path = Path.Combine(directory, ManifestFileName);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), ManifestJson) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void WriteManifest(string directory, InstallManifest manifest)
    {
        string path = Path.Combine(directory, ManifestFileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(manifest, ManifestJson));
        File.Move(path + ".tmp", path, overwrite: true);
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); } catch (IOException) { }
    }

    // ───────────────────────── 도우미 ─────────────────────────

    private static bool IsService(Process process)
    {
        try { return process.SessionId == 0; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>등록된 Windows 서비스 중 실행 파일이 이 폴더 안에 있는 것이 있는지</summary>
    private static bool ServiceRunsFrom(string directory)
    {
        using RegistryKey? services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return true;
        foreach (string name in services.GetSubKeyNames())
        {
            try
            {
                using RegistryKey? service = services.OpenSubKey(name);
                if (service?.GetValue("ImagePath") is not string image || image.Length == 0) continue;
                image = Environment.ExpandEnvironmentVariables(image.Trim());
                string file = image.StartsWith('"') ? image[1..Math.Max(1, image.IndexOf('"', 1))]
                    : image.Contains(".exe", StringComparison.OrdinalIgnoreCase) ? image[..(image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4)]
                    : image;
                if (IsInside(file, directory)) return true;
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return false;
    }

    private static bool IsInside(string path, string folder)
    {
        try
        {
            string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
