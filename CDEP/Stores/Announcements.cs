using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>Terminal Notes 공지 (Markdown 하나). revision = UTF-8 바이트의 SHA-256 (앱과 같은 규칙)</summary>
internal sealed partial class Announcements
{
    public const int MaxBytes = 262_144, MaxLines = 2048, HistoryKeep = 50;

    [GeneratedRegex("^[0-9]{8}-[0-9]{6}-[0-9]{6}(?:-[A-F0-9]{8})?$")] private static partial Regex StampText();

    private readonly string _directory;
    private readonly Lock _lock = new();

    public List<string> Editors { get; }

    public Announcements(string directory, IEnumerable<string> editors)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        Editors = editors.Select(user => user.ToLowerInvariant()).ToList();
    }

    private string CurrentPath => Path.Combine(_directory, "current.md");

    public JsonObject Read()
    {
        var meta = Util.ReadJsonObject(Path.Combine(_directory, "meta.json")) ?? [];
        if (!File.Exists(CurrentPath)) return new JsonObject { ["text"] = "", ["revision"] = "missing", ["exists"] = false, ["updatedAt"] = "", ["updatedBy"] = "" };
        byte[] data = Util.ReadBytes(CurrentPath);
        return new JsonObject
        {
            ["text"] = System.Text.Encoding.UTF8.GetString(data), ["revision"] = Util.Sha256Hex(data), ["exists"] = true,
            ["updatedAt"] = Util.Or(Util.Field(meta, "UpdatedAt"), ""), ["updatedBy"] = Util.Or(Util.Field(meta, "UpdatedBy"), ""),
        };
    }

    /// <summary>서버에 목록이 없으면 앱이 자기 목록의 계정만 저장하게 함</summary>
    public bool IsEditor(string author) => Editors.Count == 0 || Editors.Contains(Util.AccountName(author).ToLowerInvariant());

    public JsonObject Save(JsonNode? text, JsonNode? expectedRevision, JsonNode? expectedExists, string author, bool admin = false)
    {
        if (Util.AsString(text) is not { } content) throw new RelayError(400, "공지 내용이 없습니다.");
        if (!admin && !IsEditor(author)) throw new RelayError(403, "이 Windows 계정은 공지를 작성할 수 없습니다.");
        byte[] data = Util.Utf8.GetBytes(content);
        if (data.Length > MaxBytes) throw new RelayError(400, "공지는 UTF-8 기준 256KB까지 저장할 수 있습니다.");
        if (content.Count(c => c == '\n') + 1 > MaxLines) throw new RelayError(400, "공지는 2,048줄까지 표시하고 저장할 수 있습니다.");
        lock (_lock)
        {
            var current = Read();
            bool exists = Util.IsTrue(current["exists"]);
            if (exists != Util.Truthy(expectedExists) || exists && Util.Text(current["revision"]) != (Util.AsString(expectedRevision) ?? Util.Text(expectedRevision)))
                throw new RelayError(409, "공지가 다른 곳에서 변경되었습니다. 내용을 복사한 뒤 창을 다시 열어 주세요.");
            if (exists)
            {
                string history = Path.Combine(_directory, "history");
                Directory.CreateDirectory(history);
                File.Copy(CurrentPath, Path.Combine(history, $"{Util.Stamp()}-{Util.Text(current["revision"])[..8]}.md"), overwrite: true);
                var files = Directory.EnumerateFileSystemEntries(history).Order(StringComparer.Ordinal).ToList();
                foreach (string old in files.Take(Math.Max(0, files.Count - HistoryKeep)))
                {
                    try { File.Delete(old); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            Util.AtomicWrite(CurrentPath, data);
            Util.WriteJson(Path.Combine(_directory, "meta.json"), new JsonObject { ["UpdatedAt"] = Util.Iso(Util.Now()), ["UpdatedBy"] = author });
            return Read();
        }
    }

    public List<string> History()
    {
        string directory = Path.Combine(_directory, "history");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.md").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).Reverse()
            .Select(name => name[..^3]).ToList();
    }

    public string VersionText(string version)
    {
        if (!StampText().IsMatch(version ?? "")) throw new RelayError(404, "그 버전을 찾지 못했습니다.");
        string path = Path.Combine(_directory, "history", version + ".md");
        if (!File.Exists(path)) throw new RelayError(404, "그 버전을 찾지 못했습니다.");
        return Util.ReadText(path, sig: false, lines: true);
    }
}
