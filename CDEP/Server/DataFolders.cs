using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>데이터 폴더: 예전 위치에서 옮기기 · 복사해 오기, 자동 백업과 되살리기</summary>
internal static partial class DataFolders
{
    public const string MovedNote = "DATA-MOVED.txt";

    /// <summary>
    /// 2.23 이 기본 데이터 폴더 이름을 바꿈 (TerminalNotesRelayData → i-api-data). RELAY_DATA_ROOT 를 쓰지 않고 새 폴더가 없거나 비었으면 예전 폴더를
    /// 한 번에 이름만 바꿔 옮김 (Windows 가 막으면 복사하고 원본은 안내 파일과 함께 남김). 로그를 열기 전에 (로그가 새 폴더를 만듦). 한 일 또는 null
    /// </summary>
    public static string? MoveRenamedData(Config config)
    {
        string fallback = Path.GetFullPath(Config.DefaultDataRoot());
        if (!Util.SamePath(config.DataRoot, fallback)) return null;
        string old = Path.Combine(Path.GetDirectoryName(fallback)!, Config.RenamedDataFolder);
        // 빈 폴더는 치지 않음 (저장소가 열 때 자기 폴더를 만듦), 파일은 침
        if (!Directory.Exists(old) || !Util.HasFiles(old) || Directory.Exists(fallback) && Util.HasFiles(fallback)) return null;
        try
        {
            if (Directory.Exists(fallback)) Directory.Delete(fallback, true);
            Directory.Move(old, fallback);
            return $"데이터 폴더를 옮겼습니다: {old} → {fallback}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        string staging = fallback + ".copying";
        try
        {
            if (Directory.Exists(staging)) Util.DeleteTree(staging);
            Util.CopyTree(old, staging);
            Directory.Move(staging, fallback);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { if (Directory.Exists(staging)) Util.DeleteTree(staging); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
            return $"데이터 폴더 {old} 를 {fallback} 로 옮기지 못했습니다({error.Message}). 다음 시작 때 다시 합니다.";
        }
        try
        {
            File.WriteAllText(Path.Combine(old, MovedNote), $"이 폴더의 데이터는 {DateTime.Now:yyyy-MM-dd HH:mm}에 {fallback}(으)로 복사되었습니다.\n" +
                "API 서버(i-api)는 이제 그 폴더를 씁니다. 이 폴더에 남은 것은 예전 사본입니다.\n", Util.Utf8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return $"데이터 폴더를 복사했습니다(원본은 그대로): {old} → {fallback}";
    }

    /// <summary>기본 데이터 폴더가 아직 2.23 전 이름 (서버가 시작할 때 옮김): 그동안 더 예전 것을 새 폴더에 복사하면 옮길 자리를 차지함</summary>
    public static bool RenamedDataWaiting(Config config)
    {
        string fallback = Path.GetFullPath(Config.DefaultDataRoot());
        string old = Path.Combine(Path.GetDirectoryName(fallback)!, Config.RenamedDataFolder);
        return Util.SamePath(config.DataRoot, fallback) && Directory.Exists(old) && Util.HasFiles(old);
    }

    /// <summary>
    /// 예전 자리에 남은 스토어를 데이터 폴더로 복사 (옮기지 않음), 폴더마다, 데이터 폴더에 자기 것이 없을 때만. 자리 (새 모양부터): 2.10–2.11 기본 폴더
    /// (%LOCALAPPDATA%\TerminalNotesRelay), --settings-from 한 relay.py 의 폴더, 이 서버 프로그램 옆 (2.10 전). 복사한 것들.
    /// 2.23 전 데이터 폴더가 이름 바꾸기를 기다리는 동안은 아무것도 (그것이 진짜 스토어)
    /// </summary>
    public static List<string> AdoptOldData(Config config, IEnumerable<string>? extraSources = null, bool includeDefaults = true)
    {
        if (RenamedDataWaiting(config)) return [];
        var messages = new List<string>();
        var sources = (includeDefaults ? Config.LegacyDataRoots() : []).Concat(extraSources ?? []).Append(Config.ProgramFolder).ToList();
        foreach (string target in new[] { config.SkillsDir, config.PluginsDir, config.AnnouncementsDir })
        {
            if (!Util.IsInside(target, config.DataRoot) || Util.SamePath(target, config.DataRoot)) continue;  // 설정이 정한 따로의 폴더
            string relative = Path.GetRelativePath(config.DataRoot, target);
            if (Util.HasEntries(target)) continue;
            foreach (string root in sources)
            {
                string old = Path.Combine(root, relative);
                try
                {
                    if (Util.SamePath(old, target) || !Directory.Exists(old) || !Util.HasEntries(old)) continue;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
                string staging = target + ".copying";
                try
                {
                    if (Directory.Exists(staging)) Util.DeleteTree(staging);
                    Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                    Util.CopyTree(old, staging);
                    if (Directory.Exists(target)) Directory.Delete(target);
                    Directory.Move(staging, target);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Log.Exception($"could not copy the data in {old}; it stays there", error);
                    continue;
                }
                Log.Info($"data copied from {old} to {target}");
                messages.Add($"{old}의 데이터를 {target}(으)로 복사했습니다(원본은 그대로 둡니다).");
                try
                {
                    File.WriteAllText(Path.Combine(root, MovedNote), $"이 폴더의 스킬·공지는 {DateTime.Now:yyyy-MM-dd HH:mm}에 {config.DataRoot}(으)로 복사되었습니다.\r\n" +
                        "API 서버는 이제 그 폴더를 씁니다. 이 폴더에 남은 것은 예전 사본이라 고쳐도 반영되지 않습니다.\r\n", Util.Utf8);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                break;
            }
        }
        return messages;
    }

    // ── 자동 백업 ───────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^store-[0-9]{8}-[0-9]{6}\.zip$")] private static partial Regex BackupName();

    public static List<(string Name, string Folder)> StoreFolders(Config config) =>
    [
        ("skills", config.SkillsDir), ("plugins", config.PluginsDir), ("announcements", config.AnnouncementsDir), ("launcher", config.LauncherDir),
        ("usage", config.UsageDir), ("api", config.ApiDir),
    ];

    /// <summary>백업 폴더의 백업, 최신부터</summary>
    public static List<string> StoreBackups(Config config)
    {
        try
        {
            return Directory.EnumerateFiles(config.BackupDir).Where(path => BackupName().IsMatch(Path.GetFileName(path)))
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// 스킬 · 플러그인 · 공지 · 앱 목록 · 사용 기록의 ZIP . 가장 최근 백업 뒤로 바뀐 것이 있을 때만 만들어,
    /// 남긴 백업은 서로 다른 상태. RELAY_BACKUP_KEEP 보다 많은 오래된 것은 지움
    /// </summary>
    public static string? BackupStore(Config config)
    {
        var files = new List<(string Name, string Folder, string Path)>();
        foreach (var (name, folder) in StoreFolders(config))
        {
            try
            {
                if (Directory.Exists(folder))
                    files.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => (name, folder, path)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (files.Count == 0) return null;  // 빈 스토어는 백업하지 않음: 좋은 백업을 밀어내지 않게
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, folder, path) in files)
        {
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            long nanoseconds = (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100;
            digest.AppendData(Encoding.UTF8.GetBytes($"{name}/{Path.GetRelativePath(folder, path).Replace('\\', '/')}\0{info.Length}\0{nanoseconds}\n"));
        }
        string fingerprint = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        var backups = StoreBackups(config);
        if (backups.Count > 0)
        {
            try
            {
                using var latest = ZipFile.OpenRead(backups[0]);
                if (latest.Comment == fingerprint) return null;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        Directory.CreateDirectory(config.BackupDir);
        string target = Path.Combine(config.BackupDir, $"store-{Util.Now().ToLocalTime():yyyyMMdd-HHmmss}.zip");
        if (File.Exists(target)) return null;
        string temporary = target + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, folder, path) in files)
            {
                try
                {
                    var entry = archive.CreateEntry($"{name}/{Path.GetRelativePath(folder, path).Replace('\\', '/')}", CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(File.GetLastWriteTime(path).Year < 1980 ? new DateTime(1980, 1, 1) : File.GetLastWriteTime(path));
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = entry.Open();
                    input.CopyTo(output);
                }
                catch (FileNotFoundException) { }
            }
            archive.Comment = fingerprint;
        }
        File.Move(temporary, target, overwrite: true);
        foreach (string old in StoreBackups(config).Skip(config.BackupKeep))
        {
            try { File.Delete(old); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return target;
    }

    /// <summary>백업의 폴더를 비었거나 없는 곳에만 되살림 (내용이 있는 폴더는 그대로). 한 일, 한 줄씩</summary>
    public static List<string> RestoreBackup(Config config, string? source = null)
    {
        if (source is null)
        {
            var backups = StoreBackups(config);
            if (backups.Count == 0) throw new ConfigError($"백업이 없습니다: {config.BackupDir}");
            source = backups[0];
        }
        var lines = new List<string>();
        using var archive = ZipFile.OpenRead(source);
        var members = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
        foreach (var (name, folder) in StoreFolders(config))
        {
            var own = members.Where(entry => entry.FullName.StartsWith(name + "/", StringComparison.Ordinal)).ToList();
            if (own.Count == 0) continue;
            try
            {
                if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    lines.Add($"{name}: 지금 폴더({folder})에 내용이 있어 그대로 두었습니다. 덮어쓰려면 그 폴더를 다른 곳으로 옮긴 뒤 다시 실행하세요.");
                    continue;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lines.Add($"{name}: 폴더를 확인하지 못했습니다 · {error.Message}");
                continue;
            }
            string staging = folder + ".restoring";
            if (Directory.Exists(staging)) Util.DeleteTree(staging);
            foreach (var entry in own)
            {
                string relative = entry.FullName[(name.Length + 1)..];
                if (relative.Length == 0 || relative.StartsWith('/') || Path.IsPathRooted(relative) || relative.Split('/').Contains("..")) continue;  // 폴더 밖으로는 안 됨
                string destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
            if (Directory.Exists(folder)) Directory.Delete(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            Directory.Move(staging, folder);
            lines.Add($"{name}: {Path.GetFileName(source)}에서 {own.Count}개 파일을 되살렸습니다.");
        }
        return lines;
    }
}
