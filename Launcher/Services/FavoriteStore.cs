using System.Text.Json;

namespace ILauncher.Services;

/// <summary>즐겨찾기: 이 PC 에 설치된 일반 프로그램(실행 파일·바로 가기·파일). 이 PC 에만 저장 (경로가 PC 마다 다름)</summary>
public sealed class FavoriteProgram
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
}

/// <summary>%LOCALAPPDATA%\i-luncher\favorites.json</summary>
public sealed class FavoriteStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public List<FavoriteProgram> Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<FavoriteProgram>>(File.ReadAllText(FilePath), Options) is { } list)
                return list.Where(item => item.Path.Length > 0).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    public void Save(IEnumerable<FavoriteProgram> favorites)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(favorites.ToList(), Options));
        File.Move(temporary, FilePath, overwrite: true);
    }
}

/// <summary>설치된 프로그램 목록 (시작 메뉴의 바로 가기)과 바로 가기 풀기</summary>
public static class InstalledPrograms
{
    private static readonly string[] Skip = ["uninstall", "제거", "readme", "read me", "help", "도움말", "license", "release notes", "website", "웹 사이트"];

    /// <summary>시작 메뉴(모든 사용자 + 현재 사용자)의 .lnk. 이름 → 경로, 이름순</summary>
    public static List<(string Name, string Path)> Scan()
    {
        var found = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var folder in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            string root = Environment.GetFolderPath(folder);
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (string link in links)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(link);
                if (Skip.Any(word => name.Contains(word, StringComparison.CurrentCultureIgnoreCase))) continue;
                found.TryAdd(name, link);
            }
        }
        return found.Select(pair => (pair.Key, pair.Value)).OrderBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>바로 가기의 대상과 아이콘 위치 (풀지 못하면 null)</summary>
    public static (string Target, string IconFile, int IconIndex)? ResolveShortcut(string link)
    {
        if (!link.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(link)) return null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(link);
                string target = shortcut.TargetPath ?? "";
                string iconLocation = shortcut.IconLocation ?? "";
                string iconFile = target;
                int iconIndex = 0;
                int comma = iconLocation.LastIndexOf(',');
                string iconPath = comma > 0 ? iconLocation[..comma] : iconLocation;
                if (iconPath.Trim().Length > 0)
                {
                    iconFile = Environment.ExpandEnvironmentVariables(iconPath.Trim());
                    if (comma > 0) int.TryParse(iconLocation[(comma + 1)..], out iconIndex);
                }
                return (target, iconFile, iconIndex);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException
                                          or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException) { return null; }
    }
}
