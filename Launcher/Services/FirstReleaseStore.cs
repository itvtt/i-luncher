using System.IO;
using System.Text.Json;

namespace ILauncher.Services;

/// <summary>
/// 저장소마다 처음 낸 릴리스 날짜 (%LOCALAPPDATA%\i-luncher\first-releases.json, 이 PC · 사용자만).
/// 바뀌지 않는 값이라 앱마다 한 번만 GitHub 에 묻고 여기 둠: 스토어 · 설치됨의 NEW 태그(처음 출시 14일)와 앱 페이지의 "최초 출시"
/// </summary>
public sealed class FirstReleaseStore(string filePath)
{
    private readonly object _gate = new();
    private Dictionary<string, DateTimeOffset>? _dates;

    private static string Key(GitHubRepository repository) => repository.WebUrl.ToLowerInvariant();

    public bool TryGet(GitHubRepository repository, out DateTimeOffset date)
    {
        lock (_gate)
            return Dates().TryGetValue(Key(repository), out date);
    }

    public void Set(GitHubRepository repository, DateTimeOffset date)
    {
        lock (_gate)
        {
            var dates = Dates();
            if (dates.TryGetValue(Key(repository), out var existing) && existing == date) return;
            dates[Key(repository)] = date;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.WriteAllText(filePath, JsonSerializer.Serialize(dates, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 못 쓰면 다음에 다시 물음
            }
        }
    }

    /// <summary>파일이 없거나 깨졌으면 빈 목록</summary>
    private Dictionary<string, DateTimeOffset> Dates()
    {
        if (_dates is not null) return _dates;
        try
        {
            _dates = File.Exists(filePath)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(filePath)) ?? []
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _dates = [];
        }
        _dates = new Dictionary<string, DateTimeOffset>(_dates, StringComparer.OrdinalIgnoreCase);
        return _dates;
    }
}
