using System.IO;
using System.Text.Json;
using ILauncher.Models;

namespace ILauncher.Services;

/// <summary>
/// 내 앱: 사용자가 큰 창 아래 독의 + → GitHub 프로젝트로 더한 앱. 이 PC 에만 저장 (%LOCALAPPDATA%\i-luncher\my-apps.json).
/// 앱 목록(API 서버)의 앱처럼 릴리스로 설치 · 업데이트하고, 사용자가 삭제할 수 있음 (설치한 앱도 함께 제거할지 고름)
/// </summary>
public sealed class MyAppStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>저장한 내 앱 (모두 Personal). 파일이 없거나 깨졌으면 빈 목록</summary>
    public List<AppDefinition> Load()
    {
        try
        {
            if (!File.Exists(filePath)) return [];
            var apps = JsonSerializer.Deserialize<List<AppDefinition>>(File.ReadAllText(filePath), Options) ?? [];
            foreach (var app in apps) app.Personal = true;
            return [.. apps.Where(app => app.Id.Length > 0 && app.Repository.Length > 0)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<AppDefinition> apps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string temp = filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(apps.ToList(), Options));
        File.Move(temp, filePath, overwrite: true);
    }

    /// <summary>내 앱의 스크린샷을 복사해 둘 폴더 (런처가 시작할 때 정함)</summary>
    public static string MediaFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-luncher", "my-media");

    /// <summary>스크린샷 그림을 MediaFolder 에 복사하고 그 경로 (PNG · JPG · BMP · GIF, 20 MB 까지). 그림이 아니면 InvalidDataException</summary>
    public static string ImportScreenshot(string sourceFile)
    {
        var info = new FileInfo(sourceFile);
        if (info.Length > 20_000_000) throw new InvalidDataException("20 MB 보다 큰 그림은 넣을 수 없습니다.");
        string extension = info.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" ? info.Extension.ToLowerInvariant()
            : throw new InvalidDataException("PNG · JPG · BMP · GIF 그림만 넣을 수 있습니다.");
        Directory.CreateDirectory(MediaFolder);
        string copy = Path.Combine(MediaFolder, $"shot-{Guid.NewGuid():N}{extension}");
        File.Copy(sourceFile, copy);
        return copy;
    }

    /// <summary>MediaFolder 에 복사해 둔 그림이면 지움 (다른 곳의 파일은 건드리지 않음)</summary>
    public static void DeleteMedia(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return;
        try
        {
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(MediaFolder)) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) File.Delete(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    /// <summary>같은 저장소인지 (호스트 · 소유자 · 이름, 대소문자 무시)</summary>
    public static bool SameRepository(string a, string b, string? defaultHost)
    {
        var left = GitHubRepository.Parse(a, defaultHost);
        var right = GitHubRepository.Parse(b, defaultHost);
        return left is not null && right is not null
               && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase)
               && left.FullName.Equals(right.FullName, StringComparison.OrdinalIgnoreCase);
    }
}
