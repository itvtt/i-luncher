using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ILauncher.Services;

/// <summary>
/// 앱을 지운 뒤 Windows 에 남는 시스템 등록과, 예전 런처가 캐시 폴더에 남긴 시스템 파일을 지움
/// (사용자 결정 2026-11-26: "시스템파일 같은건 제대로 제거되어야해 단 데이터 류는 제거되지 않도록해").
///   지우는 것: 지운 설치 폴더 안을 가리키는 것만 — 바로 가기(시작 메뉴 · 바탕화면 · 시작 프로그램 폴더), 시작 프로그램 등록(Run · RunOnce · StartupApproved),
///             트레이 아이콘 설정(설정 → 작업 표시줄 → 기타 시스템 트레이 아이콘 = NotifyIconSettings, 예전 아이콘 그림이 여기 남음)과 그 알림 등록,
///             프로그램 이름 캐시(MuiCache), "앱" 목록 기록, 그 앱이 시작 프로그램 폴더에 둔 자기 실행 스크립트(이름이 앱 실행 파일과 같고 그 폴더를 가리키는 .vbs · .cmd · .bat)
///   지우지 않는 것: 설치 폴더 밖의 모든 폴더(앱 설정 · 메모 · 내보낸 파일 같은 사용자 데이터), 런처 데이터(C:\i-luncher), 다른 프로그램의 등록
/// 모두 HKCU (관리자 권한 없음). Setup(uninstall.exe, .NET Framework 4.8)도 이 파일을 함께 빌드하므로 두 쪽에서 되는 코드만 씀
/// </summary>
public sealed class SystemLeftovers
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string StartupApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string StartupApprovedFolderKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    public const string TrayIconsKey = @"Control Panel\NotifyIconSettings";
    public const string NotificationsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    public const string AppUserModelIdKey = @"Software\Classes\AppUserModelId";
    public const string MuiCacheKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    /// <summary>트레이 아이콘마다 Windows 가 만드는 알림 등록 이름 (뒤는 NotifyIconSettings 의 번호)</summary>
    public const string GeneratedAumid = "NotifyIconGeneratedAumid_";

    /// <summary>
    /// 예전 런처(2026-11-07 전)가 캐시 폴더(%LOCALAPPDATA%\i-luncher)에 만든 것: 앱 실행 사본(Apps\&lt;앱&gt;\&lt;해시&gt;, _shared-payloads) · 실행 환경 ·
    /// 버전 상태 · 빌드 작업 폴더 · 예전 런처 버전 · 미리 보기 빌드 · 예전 공지 캐시 · 받은 설치 파일. 지금 런처는 쓰지 않음.
    /// 데이터(settings.json · favorites.json 같은 사용자 파일, usage.json), 이 PC 의 Actions 러너 폴더, 그 밖의 이름은 건드리지 않음
    /// </summary>
    public static readonly string[] LegacyCacheFolders =
        { "Apps", "runtime", "VersionState", "Workspaces", "Releases", "Previews", "notices", "DiskAtlas-Setup", "DiskScan-Setup" };
    public static readonly string[] LegacyCacheFiles = { ".managed-payload.lock", "store-group.txt" };

    private readonly string _prefix;
    private readonly (string Folder, bool Recursive)[] _shortcutFolders;
    private readonly string? _startupFolder;
    private readonly bool _notifyShell;

    /// <param name="shortcutFolders">바로 가기를 찾을 폴더 (true = 하위 폴더까지)</param>
    /// <param name="startupFolder">시작 프로그램 폴더 (앱이 둔 실행 스크립트)</param>
    /// <param name="registryPrefix">시험용: HKCU 의 이 키 아래에서만 (실제 등록을 건드리지 않음). 보통은 ""</param>
    /// <param name="notifyShell">지운 뒤 탐색기에 아이콘을 다시 읽으라고 알림</param>
    public SystemLeftovers(IEnumerable<(string Folder, bool Recursive)> shortcutFolders, string? startupFolder, string registryPrefix = "", bool notifyShell = true)
    {
        _shortcutFolders = shortcutFolders.Where(item => !string.IsNullOrEmpty(item.Folder)).ToArray();
        _startupFolder = string.IsNullOrEmpty(startupFolder) ? null : startupFolder;
        _prefix = registryPrefix.Length == 0 ? "" : registryPrefix.TrimEnd('\\') + "\\";
        _notifyShell = notifyShell;
    }

    /// <summary>이 Windows 사용자의 실제 위치 (시작 메뉴는 하위 폴더까지, 바탕화면 · 시작 프로그램은 그 폴더만)</summary>
    public static SystemLeftovers ForCurrentUser()
    {
        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return new SystemLeftovers(new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            (startup, false)
        }, startup);
    }

    // ───────────────────────── 지운 앱의 등록 ─────────────────────────

    /// <summary>
    /// 이 폴더(지웠거나 지울 설치 폴더) 안을 가리키는 등록을 모두 지움. executableNames = 그 폴더에 있던 실행 파일 이름(확장자 없이),
    /// 시작 프로그램 폴더의 스크립트를 알아볼 때 씀 (폴더가 아직 있으면 null 로 두면 읽음). 지운 항목 수
    /// </summary>
    public int RemoveFor(string directory, IEnumerable<string>? executableNames = null)
    {
        string folder;
        try { folder = Path.GetFullPath(directory).TrimEnd('\\'); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return 0; }
        if (folder.Length <= 3) return 0;  // 드라이브 전체는 아님
        var names = new HashSet<string>(executableNames ?? ExecutableNames(folder), StringComparer.OrdinalIgnoreCase);
        int removed = RemoveShortcuts(folder, names)
            + RemoveRunEntries(folder)
            + RemoveTrayIcons(path => IsInside(path, folder))
            + RemoveMuiCache(folder)
            + RemoveUninstallEntries(folder);
        if (removed > 0) NotifyShellChanged();
        return removed;
    }

    /// <summary>폴더 안(하위 폴더 포함)의 실행 파일 이름 (확장자 없이). 지우기 전에 읽어 둠</summary>
    public static List<string> ExecutableNames(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories).Select(file => Path.GetFileNameWithoutExtension(file) ?? "").Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new List<string>(); }
    }

    private int RemoveShortcuts(string folder, HashSet<string> executableNames)
    {
        int removed = 0;
        object? shell = null;
        try { if (Type.GetTypeFromProgID("WScript.Shell") is { } shellType) shell = Activator.CreateInstance(shellType); }
        catch (Exception error) when (error is COMException or TargetInvocationException or MemberAccessException) { }  // 바로 가기만 건너뛰고 나머지 등록은 지움
        try
        {
            foreach (var (root, recursive) in _shortcutFolders)
            {
                foreach (string link in Files(root, "*.lnk", recursive))
                {
                    if (shell is null || ShortcutTarget(shell, link) is not { } target || !IsInside(target, folder)) continue;
                    if (!TryDeleteFile(link)) continue;
                    removed++;
                    DeleteValue(StartupApprovedFolderKey, Path.GetFileName(link));
                    RemoveEmptyFolders(Path.GetDirectoryName(link), root);
                }
            }
        }
        finally { if (shell is not null) Marshal.FinalReleaseComObject(shell); }

        // 앱이 시작 프로그램 폴더에 둔 자기 실행 스크립트 (예: i-api.vbs → i-api.exe): 이름이 그 앱의 실행 파일과 같고 그 폴더를 가리킬 때만
        if (_startupFolder is not null && executableNames.Count > 0)
            foreach (string script in Files(_startupFolder, "*", recursive: false))
            {
                string extension = Path.GetExtension(script);
                if (!(extension.Equals(".vbs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                      || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))) continue;
                if (!executableNames.Contains(Path.GetFileNameWithoutExtension(script))) continue;
                try
                {
                    if (new FileInfo(script).Length > 16 * 1024) continue;
                    if (!MentionsFolder(script, folder)) continue;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                if (!TryDeleteFile(script)) continue;
                removed++;
                DeleteValue(StartupApprovedFolderKey, Path.GetFileName(script));
            }
        return removed;
    }

    private int RemoveRunEntries(string folder)
    {
        int removed = 0;
        foreach (string path in new[] { RunKey, RunOnceKey })
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_prefix + path, writable: true);
                if (key is null) continue;
                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not string command || CommandExecutable(command) is not { } exe || !IsInside(exe, folder)) continue;
                    key.DeleteValue(name, throwOnMissingValue: false);
                    removed++;
                    if (path == RunKey) DeleteValue(StartupApprovedRunKey, name);
                }
            }
            catch (Exception error) when (IsRegistryError(error)) { }
        }
        return removed;
    }

    /// <summary>
    /// 트레이 아이콘 설정에서 match 에 맞는 실행 파일의 항목과 그 알림 등록을 지움. 설정 앱의 "기타 시스템 트레이 아이콘" 목록은 여기 남은
    /// 그림(IconSnapshot)을 보여 주므로, 지운 앱 · 예전 위치의 항목이 남으면 예전 아이콘으로 보임. 앱을 다시 실행하면 Windows 가 새로 만듦
    /// </summary>
    private int RemoveTrayIcons(Func<string, bool> match)
    {
        int removed = 0;
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(_prefix + TrayIconsKey, writable: true);
            if (root is null) return 0;
            foreach (string id in root.GetSubKeyNames())
            {
                string? path;
                using (RegistryKey? entry = root.OpenSubKey(id)) path = entry?.GetValue("ExecutablePath") as string;
                if (path is null || ResolveKnownFolder(path) is not { } full || !match(full)) continue;
                root.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
                removed++;
                DeleteKey(NotificationsKey + "\\" + GeneratedAumid + id);
                DeleteKey(AppUserModelIdKey + "\\" + GeneratedAumid + id);
            }
        }
        catch (Exception error) when (IsRegistryError(error)) { }
        return removed;
    }

    private int RemoveMuiCache(string folder)
    {
        int removed = 0;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_prefix + MuiCacheKey, writable: true);
            if (key is null) return 0;
            foreach (string name in key.GetValueNames())
            {
                // 이름 = "<실행 파일 경로>.FriendlyAppName" · ".ApplicationCompany"
                int end = name.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end <= 0 || !IsInside(name.Substring(0, end + 4), folder)) continue;
                key.DeleteValue(name, throwOnMissingValue: false);
                removed++;
            }
        }
        catch (Exception error) when (IsRegistryError(error)) { }
        return removed;
    }

    private int RemoveUninstallEntries(string folder)
    {
        int removed = 0;
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(_prefix + UninstallKey, writable: true);
            if (root is null) return 0;
            foreach (string name in root.GetSubKeyNames())
            {
                string? location;
                using (RegistryKey? entry = root.OpenSubKey(name)) location = entry?.GetValue("InstallLocation") as string;
                if (string.IsNullOrWhiteSpace(location)) continue;
                string trimmed = location!.Trim().Trim('"');
                if (!SamePath(trimmed, folder) && !IsInside(trimmed, folder)) continue;
                root.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                removed++;
            }
        }
        catch (Exception error) when (IsRegistryError(error)) { }
        return removed;
    }

    // ───────────────────────── 남은 것 정리 (런처 시작 때) ─────────────────────────

    /// <summary>
    /// 트레이 아이콘 설정 중 roots(런처가 설치하는 폴더들) 안의 실행 파일인데 그 파일이 이제 없는 항목을 지움
    /// (예전 런처 · 이름을 바꾸기 전 위치 · 지운 앱). 실행 파일이 있는 항목은 그대로. 지운 항목 수
    /// </summary>
    public int CleanStaleTrayIcons(IEnumerable<string> roots)
    {
        var folders = roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root =>
        {
            try { return Path.GetFullPath(root).TrimEnd('\\'); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
        }).Where(root => root.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count == 0) return 0;
        int removed = RemoveTrayIcons(path => folders.Any(folder => IsInside(path, folder)) && !File.Exists(path));
        if (removed > 0) NotifyShellChanged();
        return removed;
    }

    /// <summary>
    /// 예전 런처의 시스템 파일(LegacyCacheFolders · LegacyCacheFiles)을 캐시 폴더에서 지움. 그 안을 가리키는 바로 가기 · 트레이 아이콘 등록도 함께.
    /// 그 안의 프로그램이 아직 실행 중인 폴더는 그대로 두고 다음에 다시 함. 지운 이름 목록
    /// </summary>
    public List<string> CleanLegacyLauncherFiles(string cacheFolder)
    {
        var removed = new List<string>();
        if (!Directory.Exists(cacheFolder)) return removed;
        foreach (string name in LegacyCacheFolders)
        {
            string path = Path.Combine(cacheFolder, name);
            if (!Directory.Exists(path) || AnyProcessRunsFrom(path)) continue;
            RemoveFor(path);
            if (DeleteTree(path)) removed.Add(name);
        }
        foreach (string name in LegacyCacheFiles)
            if (File.Exists(Path.Combine(cacheFolder, name)) && TryDeleteFile(Path.Combine(cacheFolder, name))) removed.Add(name);
        return removed;
    }

    /// <summary>
    /// 폴더를 지움. 읽기 전용 파일(예: git 개체)도 지우고, 연결(정션 · 심볼릭 링크)은 링크만 지워 가리키는 곳은 절대 건드리지 않음.
    /// 못 지운 파일이 있으면 나머지는 지우고 false
    /// </summary>
    public static bool DeleteTree(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) return true;
        bool complete = true;
        try
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos())
                {
                    try
                    {
                        if (entry is DirectoryInfo directory)
                        {
                            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) directory.Delete();  // 링크만
                            else if (!DeleteTree(directory.FullName)) complete = false;
                            continue;
                        }
                        if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes = FileAttributes.Normal;
                        entry.Delete();
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { complete = false; }
                }
            if (complete) info.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { complete = false; }
        return complete;
    }

    /// <summary>그 폴더 안의 실행 파일로 도는 프로세스가 있는지 (경로를 못 읽는 프로세스는 셈하지 않음)</summary>
    private static bool AnyProcessRunsFrom(string folder)
    {
        int self;
        using (Process current = Process.GetCurrentProcess()) self = current.Id;
        foreach (Process process in Process.GetProcesses())
            using (process)
            {
                try
                {
                    if (process.Id == self) continue;
                    if (process.MainModule?.FileName is { } file && IsInside(file, folder)) return true;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        return false;
    }

    // ───────────────────────── 아이콘 새로 고침 ─────────────────────────

    /// <summary>
    /// 앱을 설치 · 업데이트한 뒤: 탐색기 · 시작 메뉴가 같은 경로의 예전 아이콘을 계속 보이지 않게 그 실행 파일 · 바로 가기를 다시 읽게 함
    /// </summary>
    public void RefreshIcons(IEnumerable<string> paths)
    {
        if (!_notifyShell) return;
        try
        {
            foreach (string path in paths.Where(path => !string.IsNullOrEmpty(path)))
                SHChangeNotify(ShcneUpdateItem, ShcnfPathW | ShcnfFlush, path, null);
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlush, null, null);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private void NotifyShellChanged()
    {
        if (!_notifyShell) return;
        try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlush, null, null); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private const int ShcneUpdateItem = 0x00002000, ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000, ShcnfPathW = 0x0005, ShcnfFlush = 0x1000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string? item1, string? item2);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    // ───────────────────────── 도움 ─────────────────────────

    /// <summary>트레이 아이콘 설정의 경로: "{알려진 폴더 GUID}\나머지" 또는 전체 경로</summary>
    public static string? ResolveKnownFolder(string path)
    {
        string text = path.Trim();
        if (text.StartsWith("{", StringComparison.Ordinal))
        {
            int close = text.IndexOf('}');
            if (close < 0 || !Guid.TryParse(text.Substring(0, close + 1), out Guid id)) return null;
            string? folder = KnownFolder(id);
            if (folder is null) return null;
            text = folder.TrimEnd('\\') + "\\" + text.Substring(close + 1).TrimStart('\\');
        }
        try { return Path.IsPathRooted(text) ? Path.GetFullPath(text) : null; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? KnownFolder(Guid id)
    {
        IntPtr buffer = IntPtr.Zero;
        try { return SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out buffer) == 0 ? Marshal.PtrToStringUni(buffer) : null; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer); }
    }

    /// <summary>시작 프로그램 명령의 실행 파일 ("C:\a b\x.exe" --tray → C:\a b\x.exe)</summary>
    public static string? CommandExecutable(string command)
    {
        string text = Environment.ExpandEnvironmentVariables(command).Trim();
        if (text.Length == 0) return null;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text.Substring(1, end - 1) : null;
        }
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return text.Substring(0, exe + 4);
        int space = text.IndexOf(' ');
        return space > 0 ? text.Substring(0, space) : text;
    }

    private static IEnumerable<string> Files(string folder, string pattern, bool recursive)
    {
        if (!Directory.Exists(folder)) return Array.Empty<string>();
        try { return Directory.GetFiles(folder, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { return Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly); }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
    }

    /// <summary>스크립트 글에 그 폴더 경로가 있는지 (UTF-8 · UTF-16 · 이 PC 의 ANSI 코드 페이지)</summary>
    private static bool MentionsFolder(string script, string folder)
    {
        byte[] bytes = File.ReadAllBytes(script);
        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.Default })
            if (encoding.GetString(bytes).IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string? ShortcutTarget(object shell, string link)
    {
        try
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(link);
            try { return shortcut.TargetPath as string; }
            finally { Marshal.FinalReleaseComObject((object)shortcut); }
        }
        catch (Exception) { return null; }  // 깨진 바로 가기 · COM 오류: 이 바로 가기는 건너뜀
    }

    /// <summary>바로 가기를 지운 뒤 빈 하위 폴더(예전 런처의 시작 메뉴\i-luncher)도 지움. root 자체는 남김</summary>
    private static void RemoveEmptyFolders(string? folder, string root)
    {
        string top = Path.GetFullPath(root).TrimEnd('\\');
        for (string? current = folder; current is not null && IsInside(current, top); current = Path.GetDirectoryName(current))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any()) return;
                Directory.Delete(current);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        }
    }

    private void DeleteValue(string keyPath, string name)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_prefix + keyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception error) when (IsRegistryError(error)) { }
    }

    private void DeleteKey(string keyPath)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_prefix + keyPath, throwOnMissingSubKey: false); }
        catch (Exception error) when (IsRegistryError(error)) { }
    }

    private static bool IsRegistryError(Exception error) =>
        error is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException;

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool IsInside(string path, string folder)
    {
        try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
