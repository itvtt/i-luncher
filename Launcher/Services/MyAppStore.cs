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
