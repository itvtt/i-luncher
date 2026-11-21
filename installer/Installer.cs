using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace ILuncher.Setup;

/// <summary>
/// i-luncher 사용자별 설치: %LOCALAPPDATA%\Programs\i-luncher, 시작 메뉴·바탕화면 바로 가기, Windows "앱" 목록(HKCU) 등록.
/// 설치 폴더는 사용자가 쓸 수 있으므로 런처의 GitHub 업데이트가 계속 파일을 바꿀 수 있음 (Uninstall\i-luncher 의 InstallLocation 으로 알아봄).
/// .NET 10 Desktop Runtime 이 없는 PC 는 함께 들어 있는 실행 환경을 &lt;설치 폴더&gt;\dotnet 에 받음: 런처 실행 파일이 거기를 먼저 찾고,
/// 런처가 설치하는 .NET 앱도 이 실행 환경을 복사해 씀. 관리자 권한도 다운로드도 필요 없음.
/// 런처로 설치한 앱들은 각자 따로 설치된 것이라 런처를 지워도 남음.
/// </summary>
internal static class Installer
{
    internal const string AppName = "i-luncher";
    internal const string ExecutableName = "i-luncher.exe";
    internal const string UninstallerName = "uninstall.exe";
    internal const string RuntimeFolder = "dotnet";
    private const int RuntimeMajor = 10;
    // 테스트용 (사용자에게는 설정되지 않음): 실제 "앱" 목록·시작 메뉴·바탕화면을 건드리지 않게 하고, .NET 이 없는 PC 처럼 동작
    private static readonly string UninstallKey = Environment.GetEnvironmentVariable("ILUNCHER_SETUP_UNINSTALL_KEY") is { Length: > 0 } testKey
        ? @"Software\" + testKey : @"Software\Microsoft\Windows\CurrentVersion\Uninstall\i-luncher";
    private static readonly string? ShortcutDirectory =
        Environment.GetEnvironmentVariable("ILUNCHER_SETUP_SHORTCUT_DIR") is { Length: > 0 } testDirectory ? testDirectory : null;
    private static readonly bool TestRun = Environment.GetEnvironmentVariable("ILUNCHER_SETUP_UNINSTALL_KEY") is { Length: > 0 };
    private static readonly bool IgnoreInstalledDotNet = Environment.GetEnvironmentVariable("ILUNCHER_SETUP_IGNORE_INSTALLED_DOTNET") == "1";

    /// <summary>런처 설정 폴더 (런처 업데이트 · 제거에도 남음). ILUNCHER_DATA_DIR 은 시험용</summary>
    internal static string DataDirectory => Environment.GetEnvironmentVariable("ILUNCHER_DATA_DIR") is { Length: > 0 } data
        ? data : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>
    /// 예전 일반 · 부서용 설치 파일이 남긴 표시(store-group.txt)를 지움: 이제 설치 파일은 하나이고 런처가 Windows 계정으로 마스터를 가림
    /// </summary>
    internal static void RemoveOldStoreGroup()
    {
        try { File.Delete(Path.Combine(DataDirectory, "store-group.txt")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    internal static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    internal static string SelfPath => Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetExecutingAssembly().Location;

    internal static string StartMenuShortcut => Path.Combine(ShortcutDirectory is null
        ? Environment.GetFolderPath(Environment.SpecialFolder.Programs) : Path.Combine(ShortcutDirectory, "Programs"), AppName + ".lnk");

    internal static string DesktopShortcut => Path.Combine(ShortcutDirectory is null
        ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : Path.Combine(ShortcutDirectory, "Desktop"), AppName + ".lnk");

    internal sealed class Options(string directory, bool shortcuts, bool desktop, bool register, bool launch)
    {
        public string Directory { get; } = directory;
        public bool Shortcuts { get; } = shortcuts;
        public bool Desktop { get; } = desktop;
        public bool Register { get; } = register;
        public bool Launch { get; } = launch;
    }

    /// <summary>품고 있는 릴리스를 설치 폴더에 복사. 실패하면 사용자에게 보일 메시지로 예외를 던짐</summary>
    internal static void Install(Options options, Action<string> progress)
    {
        string target = Path.GetFullPath(options.Directory);
        EnsureNotRunning();
        progress("파일 준비 중…");
        string staging = Path.Combine(Path.GetTempPath(), "i-luncher-Setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                ?? throw new InvalidOperationException("설치 파일에 앱 패키지가 없습니다. 설치 파일을 다시 받으세요."))
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
                archive.ExtractToDirectory(staging);
            if (!File.Exists(Path.Combine(staging, ExecutableName)))
                throw new InvalidOperationException("앱 패키지에 " + ExecutableName + "가 없습니다.");

            progress("파일 복사 중…");
            Directory.CreateDirectory(target);
            foreach (string source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                string relative = source.Substring(staging.Length).TrimStart('\\', '/');
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            WriteUninstaller(target);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        if (!HasDesktopRuntime(target))
        {
            progress(".NET 실행 환경 설치 중…");
            InstallBundledRuntime(target);
        }

        string exe = Path.Combine(target, ExecutableName);
        if (options.Shortcuts)
        {
            progress("바로 가기 만드는 중…");
            CreateShortcut(StartMenuShortcut, exe, target);
            if (options.Desktop) CreateShortcut(DesktopShortcut, exe, target);
        }
        if (options.Register)
        {
            progress("Windows 앱 목록에 등록 중…");
            Register(target);
        }
        RemoveOldStoreGroup();
        progress("설치 완료");
        if (options.Launch) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = target });
    }

    /// <summary>
    /// .NET 10 Desktop Runtime: 설치 폴더 옆(이전 설치가 넣어 둔 것), DOTNET_ROOT, 또는 PC 에 설치된 것.
    /// 공유 프레임워크 둘과 호스트 해석기(host\fxr)가 모두 있어야 함
    /// </summary>
    internal static bool HasDesktopRuntime(string target)
    {
        if (IsRuntimeRoot(Path.Combine(target, RuntimeFolder))) return true;
        if (IgnoreInstalledDotNet) return false;
        foreach (string? root in InstalledDotNetRoots())
            if (!string.IsNullOrWhiteSpace(root) && IsRuntimeRoot(root!)) return true;
        return false;
    }

    private static string?[] InstalledDotNetRoots()
    {
        string? registered = null;
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? setup = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            registered = setup?.GetValue("InstallLocation") as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return
        [
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            registered,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
        ];
    }

    private static bool IsRuntimeRoot(string root)
    {
        try
        {
            bool HasMajor(string path) => Directory.Exists(path) &&
                Directory.EnumerateDirectories(path).Any(folder => System.Version.TryParse(Path.GetFileName(folder).Split('-')[0], out var version) && version.Major == RuntimeMajor);
            return HasMajor(Path.Combine(root, "shared", "Microsoft.NETCore.App")) &&
                HasMajor(Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App")) &&
                Directory.Exists(Path.Combine(root, "host", "fxr")) && Directory.EnumerateDirectories(Path.Combine(root, "host", "fxr")).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>품고 있는 .NET 10 실행 환경을 &lt;target&gt;\dotnet 에 풂 (불완전한 사본은 바꿈)</summary>
    private static void InstallBundledRuntime(string target)
    {
        using Stream runtime = Assembly.GetExecutingAssembly().GetManifestResourceStream("runtime.zip")
            ?? throw new InvalidOperationException(
                "이 PC에 .NET 10 Desktop Runtime이 없고, 이 설치 파일에는 실행 환경이 들어 있지 않습니다. 릴리스의 i-luncher-Setup.exe로 다시 설치하세요.");
        string destination = Path.Combine(target, RuntimeFolder);
        // 짧은 이름: .NET Framework 는 260자 넘는 경로를 못 다룸 (실행 환경 안의 파일 이름이 이미 긺)
        string staging = destination + ".new";
        TryDeleteDirectory(staging);
        try
        {
            using (var archive = new ZipArchive(runtime, ZipArchiveMode.Read))
                archive.ExtractToDirectory(staging);
            if (!IsRuntimeRoot(staging))
                throw new InvalidDataException("설치 파일의 .NET 실행 환경이 손상되었습니다. 설치 파일을 다시 받으세요.");
            TryDeleteDirectory(destination);
            if (Directory.Exists(destination))
                throw new IOException("이전 .NET 실행 환경 폴더를 지우지 못했습니다: " + destination);
            Directory.Move(staging, destination);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    // Setup 과 함께 빌드한 작은 제거 프로그램 (패키지 없음). 없으면 Setup 자신을 복사
    private static void WriteUninstaller(string target)
    {
        string destination = Path.Combine(target, UninstallerName);
        using Stream? uninstaller = Assembly.GetExecutingAssembly().GetManifestResourceStream(UninstallerName);
        if (uninstaller is not null)
        {
            using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            uninstaller.CopyTo(file);
        }
        else if (!string.Equals(Path.GetFullPath(SelfPath), destination, StringComparison.OrdinalIgnoreCase))
            File.Copy(SelfPath, destination, overwrite: true);
    }

    /// <summary>바로 가기, "앱" 목록 항목, 설치 폴더를 지움. 런처 설정(%LOCALAPPDATA%\i-luncher)과 런처로 설치한 앱은 남김</summary>
    internal static void Uninstall(string directory, Action<string> progress)
    {
        string target = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(target, ExecutableName)))
            throw new InvalidOperationException("i-luncher 설치 폴더가 아닙니다: " + target);
        EnsureNotRunning();
        progress("바로 가기 삭제 중…");
        foreach (string link in new[] { StartMenuShortcut, DesktopShortcut })
            if (File.Exists(link) && ShortcutTarget(link) is { } linked && IsInside(linked, target))
                File.Delete(link);
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            if (key?.GetValue("InstallLocation") is string location &&
                string.Equals(Path.GetFullPath(location).TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        progress("파일 삭제 중…");
        string self = Path.GetFullPath(SelfPath);
        foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            if (!string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        foreach (string folder in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        // 실행 중인 제거 프로그램은 자신을 지울 수 없음: 분리된 cmd 가 끝난 뒤 그 파일과 폴더를 지움
        if (self.StartsWith(target.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\" & rmdir \"{target}\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        else if (!Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target);
        progress("제거 완료");
    }

    /// <summary>이 사용자에게 설치된 i-luncher 의 설치 위치 (Windows "앱" 목록에 기록된 것)</summary>
    internal static string? RegisteredInstallDirectory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") as string;
    }

    // 실행 중인 런처가 있으면 멈춤: 쓰는 중인 파일을 바꾸지 않게
    private static void EnsureNotRunning()
    {
        if (TestRun) return;  // 시험 설치는 임시 폴더에만 (이 PC 에서 실제로 실행 중인 런처와 상관없음)
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName));
        bool any = running.Length > 0;
        foreach (var process in running) process.Dispose();
        if (any) throw new InvalidOperationException("i-luncher가 실행 중입니다. 런처 창을 닫은 뒤 다시 실행하세요.");
    }

    private static bool IsInside(string path, string folder)
    {
        try { return Path.GetFullPath(path).StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static void Register(string target)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        string uninstaller = Path.Combine(target, UninstallerName);
        long sizeKb = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) / 1024;
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", AppName);
        key.SetValue("InstallLocation", target);
        key.SetValue("DisplayIcon", Path.Combine(target, ExecutableName));
        key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /S");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void CreateShortcut(string path, string exe, string workingDirectory)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("바로 가기를 만들 수 없습니다.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = exe;
            link.WorkingDirectory = workingDirectory;
            link.IconLocation = exe + ",0";
            link.Description = "앱 설치·업데이트";
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
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

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
