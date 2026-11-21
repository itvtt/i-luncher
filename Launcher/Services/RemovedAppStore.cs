using System.IO;
using System.Text.Json;
using ILauncher.Models;

namespace ILauncher.Services;

/// <summary>사용자가 목록에서 삭제한 기본 목록(또는 서버 목록) 앱 하나. Name 은 다시 추가 메뉴에 보이는 이름</summary>
public sealed record RemovedApp(string Id, string Name, string Repository);

/// <summary>
/// 목록에서 삭제한 기본 목록 · 서버 목록 앱 (%LOCALAPPDATA%\i-luncher\removed-apps.json, 이 PC · 사용자만).
/// 런처를 업데이트하거나 다시 설치해도 이 폴더는 남으므로, 사용자가 독의 + 로 다시 추가하기 전까지 스토어 · 설치됨에 보이지 않음.
/// 기본 목록에 새로 들어온 앱은 여기에 없으니 그대로 보임
/// </summary>
public sealed class RemovedAppStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>파일이 없거나 깨졌으면 빈 목록</summary>
    public List<RemovedApp> Load()
    {
        try
        {
            if (!File.Exists(filePath)) return [];
            var apps = JsonSerializer.Deserialize<List<RemovedApp>>(File.ReadAllText(filePath), Options) ?? [];
            return [.. apps.Where(app => app is { Id.Length: > 0 }).Select(app => app with { Name = app.Name ?? app.Id, Repository = app.Repository ?? "" })];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<RemovedApp> apps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string temp = filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(apps.ToList(), Options));
        File.Move(temp, filePath, overwrite: true);
    }

    /// <summary>삭제한 그 앱인지: id(예전 id 포함) 또는 같은 저장소</summary>
    public static bool Matches(RemovedApp removed, AppDefinition app, string? defaultHost) =>
        app.HasId(removed.Id) || removed.Repository.Length > 0 && MyAppStore.SameRepository(app.Repository, removed.Repository, defaultHost);
}
