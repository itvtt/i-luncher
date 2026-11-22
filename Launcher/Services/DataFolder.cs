using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ILauncher.Services;

/// <summary>
/// 런처가 파일을 두는 곳 (사용자 결정: 백업해 새 PC 에서 다시 쓸 것만 C:\i-luncher 에).
///   C:\i-luncher              = 사용자 데이터: 설정 · 토큰 · 즐겨찾기 · 내 앱 · 뺀 앱 · 고른 아이콘 (이 폴더만 백업하면 됨)
///   %LOCALAPPDATA%\i-luncher  = 캐시: 받아 둔 목록 · 아이콘 · 스크린샷 · 받은 파일 · 읽은 공지 (지워도 다시 받음)
/// C:\i-luncher 를 만들거나 쓸 수 없는 PC 면 둘 다 %LOCALAPPDATA%\i-luncher.
/// 예전 런처가 %LOCALAPPDATA%\i-luncher 에 둔 사용자 데이터는 처음 시작할 때 옮기고, 설정 → 재설정이 지우는 것도 여기서 정함
/// </summary>
public static class DataFolder
{
    public const string Fixed = @"C:\i-luncher";

    /// <summary>캐시 폴더 (예전 런처는 여기에 모두 두었음)</summary>
    public static string Cache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-luncher");

    /// <summary>사용자 데이터 파일 · 폴더 (C:\i-luncher). 즐겨찾기 · 내 앱은 custom-icons · my-media 의 그림을 전체 경로로 가리킴</summary>
    public static readonly string[] UserFiles = ["settings.json", "favorites.json", "my-apps.json", "removed-apps.json"];
    public static readonly string[] UserFolders = ["custom-icons", "my-media"];

    /// <summary>캐시 파일 · 폴더 (%LOCALAPPDATA%\i-luncher)</summary>
    public static readonly string[] CacheFiles = ["basic-apps-cache.json", "catalog-cache.json", "notice-state.json"];
    public static readonly string[] CacheFolders = ["icons", "screenshots", "site-icons", "downloads"];

    /// <summary>재설정 → 목록: 목록에서 뺀 앱, 받아 둔 기본 · 서버 목록 (다음 시작 때 다시 받음)</summary>
    public static readonly string[] ListFiles = ["removed-apps.json", "basic-apps-cache.json", "catalog-cache.json"];

    /// <summary>예전 위치에서 옮길 것: 사용자 데이터 + 저장소 없는 빌드의 이 PC 목록 · 공지 (재설정은 이 둘을 지우지 않음)</summary>
    private static readonly string[] MovedFiles = [.. UserFiles, "catalog.json", LauncherSettings.DefaultNoticesPath];

    private static readonly Lazy<string> Chosen = new(Choose);

    /// <summary>사용자 데이터 폴더 (한 번 정하면 그대로)</summary>
    public static string Default => Chosen.Value;

    private static string Choose()
    {
        try
        {
            Directory.CreateDirectory(Fixed);
            string probe = Path.Combine(Fixed, ".write-check-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return Fixed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Cache;
        }
    }

    /// <summary>
    /// 예전 위치의 사용자 데이터를 새 폴더로 옮김 (새 폴더에 같은 이름이 있으면 그 항목은 그대로 둠, 캐시는 옮기지 않음).
    /// 즐겨찾기 · 내 앱에 적힌 아이콘 · 그림 경로도 새 폴더로 바꿈. 옮긴 항목 수
    /// </summary>
    public static int MoveFromLegacy(string target, string? legacy = null)
    {
        string from = Path.GetFullPath(legacy ?? Cache);
        string to = Path.GetFullPath(target);
        if (string.Equals(from.TrimEnd('\\'), to.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(from)) return 0;
        Directory.CreateDirectory(to);
        int moved = 0;
        foreach (string name in MovedFiles)
        {
            string source = Path.Combine(from, name), destination = Path.Combine(to, name);
            if (!File.Exists(source) || File.Exists(destination)) continue;
            try
            {
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(destination, ReplaceFolder(File.ReadAllText(source, Encoding.UTF8), from, to), new UTF8Encoding(false));
                    File.Delete(source);
                }
                else File.Move(source, destination);
                moved++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        foreach (string name in UserFolders)
        {
            string source = Path.Combine(from, name), destination = Path.Combine(to, name);
            if (!Directory.Exists(source) || Directory.Exists(destination)) continue;
            try
            {
                try { Directory.Move(source, destination); }
                catch (IOException)
                {
                    // 다른 드라이브면 복사한 뒤 지움
                    CopyFolder(source, destination);
                    Directory.Delete(source, recursive: true);
                }
                moved++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return moved;
    }

    /// <summary>JSON 글 안의 예전 폴더 경로(…\i-luncher\…)를 새 폴더로 (System.Text.Json 의 두 가지 이스케이프 모두)</summary>
    public static string ReplaceFolder(string json, string from, string to)
    {
        string oldPrefix = from.TrimEnd('\\') + "\\", newPrefix = to.TrimEnd('\\') + "\\";
        foreach (var encoder in new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var options = new JsonSerializerOptions { Encoder = encoder };
            json = json.Replace(JsonSerializer.Serialize(oldPrefix, options)[1..^1], JsonSerializer.Serialize(newPrefix, options)[1..^1], StringComparison.OrdinalIgnoreCase);
        }
        return json;
    }

    public const string ResetList = "list", ResetAll = "all";

    /// <summary>
    /// 재설정: list = 목록에서 뺀 앱 · 받아 둔 목록, all = 사용자 데이터와 캐시 모두 (위 이름들만, 두 폴더 어디에 있든).
    /// 설치한 앱 · 이 PC 목록(catalog.json) · 공지.txt · 그 밖의 파일은 건드리지 않음. 지우지 못한 항목 이름 (없으면 빈 목록)
    /// </summary>
    public static List<string> Reset(string dataFolder, string cacheFolder, string scope)
    {
        var failed = new List<string>();
        bool all = scope == ResetAll;
        string[] folders = string.Equals(dataFolder, cacheFolder, StringComparison.OrdinalIgnoreCase) ? [dataFolder] : [dataFolder, cacheFolder];
        foreach (string folder in folders)
        {
            foreach (string name in all ? [.. UserFiles, .. CacheFiles] : ListFiles)
            {
                try { File.Delete(Path.Combine(folder, name)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed.Add(name); }
            }
            if (!all) continue;
            foreach (string name in (string[])[.. UserFolders, .. CacheFolders])
            {
                string path = Path.Combine(folder, name);
                try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed.Add(name); }
            }
        }
        return failed;
    }

    private static void CopyFolder(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (string folder in Directory.GetDirectories(source)) CopyFolder(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }
}
