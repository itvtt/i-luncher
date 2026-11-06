using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ILauncher.Models;

namespace ILauncher.Services;

/// <summary>
/// 공지: 카탈로그 저장소의 공지.txt 를 Contents API 로 읽음 (GitHub Pages·별도 워크플로 없이 사내 GitHub 에서도 동작).
/// 형식: 공지마다 첫 줄은 제목, 다음 줄부터 본문, 공지 사이는 --- 만 있는 줄. 제목이 같으면 같은 공지,
/// 본문을 고치면 읽은 사람에게도 다시 알림.
/// </summary>
public sealed class NoticeService
{
    private readonly Func<CancellationToken, Task<string?>> _fetch;
    private readonly string _statePath;
    private NoticeState _state;

    public NoticeService(Func<CancellationToken, Task<string?>> fetch, string stateDirectory)
    {
        _fetch = fetch;
        _statePath = Path.Combine(stateDirectory, "notice-state.json");
        _state = LoadState(_statePath);
        CachedNotices = Parse(_state.CachedText ?? "", _state);
    }

    public IReadOnlyList<Notice> CachedNotices { get; private set; }

    public async Task<IReadOnlyList<Notice>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        string text = await _fetch(cancellationToken).ConfigureAwait(false) ?? "";
        _state.CachedText = text;
        CachedNotices = Parse(text, _state);
        SaveState();
        return CachedNotices;
    }

    public bool IsRead(Notice notice) => _state.Read.TryGetValue(notice.Id, out string? revision) && revision == notice.Revision;

    public void MarkRead(Notice notice)
    {
        if (IsRead(notice)) return;
        _state.Read[notice.Id] = notice.Revision;
        SaveState();
    }

    public static IReadOnlyList<Notice> Parse(string text, NoticeState? state = null)
    {
        var notices = new List<Notice>();
        var block = new List<string>();
        void Flush()
        {
            var lines = block.SkipWhile(string.IsNullOrWhiteSpace).ToList();
            block.Clear();
            if (lines.Count == 0) return;
            string title = lines[0].Trim();
            string body = string.Join("\n", lines.Skip(1)).Trim();
            if (title.Length == 0) return;
            string id = Hash(title.ToLowerInvariant());
            if (notices.Any(notice => notice.Id == id)) return;
            string revision = Hash(body);
            DateTimeOffset seen = DateTimeOffset.Now;
            if (state is not null)
            {
                if (state.FirstSeen.TryGetValue(id, out var recorded)) seen = recorded;
                else state.FirstSeen[id] = seen;
            }
            notices.Add(new Notice(id, revision, title.Length > 120 ? title[..120] : title, body, seen, Important: true));
        }
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim() == "---") Flush();
            else block.Add(line);
        }
        Flush();
        return notices.OrderByDescending(notice => notice.PublishedAt).ToList();
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath + ".tmp", JsonSerializer.Serialize(_state));
            File.Move(_statePath + ".tmp", _statePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static NoticeState LoadState(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<NoticeState>(File.ReadAllText(path)) is { } state) return state;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new NoticeState();
    }

    public sealed class NoticeState
    {
        public string? CachedText { get; set; }
        public Dictionary<string, string> Read { get; set; } = [];
        public Dictionary<string, DateTimeOffset> FirstSeen { get; set; } = [];
    }
}
