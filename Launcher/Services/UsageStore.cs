using System.Text.Json;

namespace ILauncher.Services;

/// <summary>
/// 앱 · 내 PC 프로그램을 연 횟수 (트레이 창의 자주 쓰는 앱 순서). 이 PC 에만: %LOCALAPPDATA%\i-luncher\usage.json.
/// 키: "app:&lt;앱 id&gt;", "program:&lt;즐겨찾기 id&gt;". 저장에 실패해도 조용히 넘어감 (순서만 바뀜)
/// </summary>
public sealed class UsageStore(string filePath)
{
    private Dictionary<string, int>? _counts;

    private Dictionary<string, int> Counts => _counts ??= Load();

    public static string AppKey(string appId) => "app:" + appId.ToLowerInvariant();
    public static string ProgramKey(string favoriteId) => "program:" + favoriteId;

    public int Count(string key) => Counts.TryGetValue(key, out int count) ? count : 0;

    public void Add(string key)
    {
        Counts[key] = Count(key) + 1;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            string temporary = filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Counts));
            File.Move(temporary, filePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private Dictionary<string, int> Load()
    {
        try
        {
            if (File.Exists(filePath) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(filePath)) is { } counts)
                return new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }
}
