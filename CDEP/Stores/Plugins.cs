using System.Text.Json.Nodes;

namespace IApi;

/// <summary>
/// OpenCode 플러그인: 하나마다 .ts 또는 .js 파일 하나 (앱이 OpenCode 의 플러그인 폴더에 넣고 OpenCode 가 코드로 돌림).
/// 마스터 계정만 공유 (MASTER_USERS 가 있으면 여기서 확인), 모든 버전을 남기고 최신을 내려 줌
/// </summary>
internal sealed class Plugins
{
    public const int CodeMax = 300_000;
    public static readonly string[] Extensions = [".ts", ".js"];

    private readonly string _directory;
    private readonly Lock _lock = new();

    public Plugins(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string Folder(string name) =>
        SkillText.IsName(name) ? Path.Combine(_directory, name) : throw new RelayError(404, "플러그인을 찾지 못했습니다.");

    private static string? CodeFile(string folder) =>
        Extensions.Select(extension => Path.Combine(folder, "plugin" + extension)).FirstOrDefault(File.Exists);

    public JsonObject? Read(string name)
    {
        string folder = Folder(name);
        var meta = Util.ReadJsonObject(Path.Combine(folder, "meta.json"));
        string? code = CodeFile(folder);
        if (meta is null || meta.Count == 0 || code is null) return null;
        return new JsonObject
        {
            ["name"] = name, ["title"] = Util.Or(Util.Field(meta, "Title"), name), ["description"] = Util.Or(Util.Field(meta, "Description"), ""),
            ["author"] = Util.Or(Util.Field(meta, "Author"), ""), ["createdAt"] = Skills.OrNull(Util.Field(meta, "CreatedAt")), ["updatedAt"] = Skills.OrNull(Util.Field(meta, "UpdatedAt")),
            ["sha256"] = Util.Or(Util.Field(meta, "Sha256"), ""), ["downloads"] = Util.AsInt(Util.Field(meta, "Downloads")), ["version"] = Util.AsInt(Util.Field(meta, "Version"), 1),
            ["note"] = Util.Or(Util.Field(meta, "Note"), ""), ["extension"] = Path.GetExtension(code),
            ["source"] = Util.Or(Util.Field(meta, "Source"), ""), ["sourceError"] = Util.Or(Util.Field(meta, "SourceError"), ""),
        };
    }

    public List<JsonObject> All()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateDirectories(_directory).Select(Path.GetFileName).OfType<string>().Where(SkillText.IsName)
            .Select(Read).OfType<JsonObject>().OrderBy(plugin => Util.Text(plugin["title"]), StringComparer.Ordinal).ToList();
    }

    public JsonObject Get(string name, bool countDownload = false)
    {
        lock (_lock)
        {
            var info = Read(name) ?? throw new RelayError(404, "플러그인을 찾지 못했습니다.");
            string folder = Folder(name);
            if (countDownload)
            {
                var meta = Util.ReadJsonObject(Path.Combine(folder, "meta.json")) ?? [];
                long downloads = Util.AsInt(Util.Field(meta, "Downloads")) + 1;
                meta["Downloads"] = downloads;
                Util.WriteJson(Path.Combine(folder, "meta.json"), meta);
                info["downloads"] = downloads;
            }
            info["code"] = Util.ReadText(CodeFile(folder)!, sig: false, lines: true);
            return info;
        }
    }

    private static string Extension(string link)
    {
        string path = GitHubLinks.SplitUrl(link.Trim()).Path;
        return Path.GetExtension(path).ToLowerInvariant();
    }

    public static string LinkExtension(string link) => Extension(link);

    /// <summary>플러그인이나 새 버전을 공유: MASTER_USERS 가 있으면 마스터만, 새 버전은 작성자나 마스터 (플러그인은 작성자의 것으로 남음)</summary>
    public JsonObject Publish(JsonNode? body, string viewer = "", IReadOnlyCollection<string>? admins = null)
    {
        if (body is not JsonObject upload) throw new RelayError(400, "형식이 올바르지 않습니다.");
        admins ??= [];
        if (admins.Count > 0 && !admins.Contains(viewer)) throw new RelayError(403, "플러그인은 마스터 계정만 올립니다.");
        string name = Util.AsString(upload["name"])?.Trim() ?? "";
        string title = Util.OneLine(upload["title"]), description = Util.OneLine(upload["description"]), author = Util.OneLine(upload["author"]);
        string note = Util.OneLine(upload["note"]);
        string extension = Util.AsString(upload["extension"]) is { } given && Extensions.Contains(given) ? given : ".ts";
        string code = (Util.AsString(upload["code"]) ?? "").Replace("\r\n", "\n");
        if (!SkillText.IsName(name)) throw new RelayError(400, "영문 이름은 소문자·숫자·하이픈(-)으로 64자까지 씁니다.");
        if (title.Length is 0 or > 80) throw new RelayError(400, "제목은 80자까지 씁니다.");
        if (description.Length > 500) throw new RelayError(400, "설명은 500자까지 씁니다.");
        if (code.Trim().Length == 0 || code.Length > CodeMax) throw new RelayError(400, "코드가 비었거나 너무 깁니다.");
        if (author.Length is 0 or > 64) throw new RelayError(400, "작성자를 확인할 수 없습니다.");
        if (note.Length > 200) throw new RelayError(400, "바뀐 점은 200자까지 씁니다.");
        // 깃허브 링크면 그 파일을 따라감 (SyncSource). "" 은 따라가기를 멈춤
        string? source = Util.AsString(upload["source"])?.Trim();
        if (!string.IsNullOrEmpty(source))
        {
            GitHubLinks.Request(source, plugin: true);
            extension = Extension(source);
        }
        byte[] data = Util.Utf8.GetBytes(code);
        string digest = Util.Sha256Hex(data);
        int number;
        bool existed;
        lock (_lock)
        {
            string folder = Folder(name);
            var existing = Read(name);
            existed = existing is not null;
            string owner = author;
            if (existing is not null && Util.Text(existing["author"]).ToLowerInvariant() != author.ToLowerInvariant())
            {
                if (!Util.IsTrue(upload["edit"])) throw new RelayError(409, "같은 영문 이름의 플러그인이 이미 있습니다. 다른 이름을 쓰세요.");
                if (!(Util.ViewerName(existing["author"]) == viewer || admins.Count == 0 || admins.Contains(viewer)))
                    throw new RelayError(403, "다른 사람의 플러그인은 수정할 수 없습니다.");
                owner = Util.Text(existing["author"]);
            }
            if (existing is not null && Util.Text(existing["sha256"]).ToUpperInvariant() == digest && Util.Text(existing["extension"]) == extension)
            {
                if (source is not null && source != Util.Text(existing["source"]))
                {
                    var meta = Util.ReadJsonObject(Path.Combine(folder, "meta.json")) ?? [];
                    meta["Source"] = source;
                    meta["SourceError"] = "";
                    Util.WriteJson(Path.Combine(folder, "meta.json"), meta);
                }
                return new JsonObject { ["name"] = name, ["title"] = title, ["author"] = owner, ["version"] = existing["version"]?.DeepClone(), ["changed"] = false, ["updated"] = true };
            }
            string versions = Path.Combine(folder, "versions");
            var numbers = Directory.Exists(versions)
                ? Directory.EnumerateDirectories(versions).Select(Path.GetFileName).OfType<string>().Where(item => SkillText.VersionNumber().IsMatch(item)).Select(int.Parse).Order().ToList()
                : [];
            number = (numbers.Count > 0 ? numbers[^1] : 0) + 1;
            string moment = Util.Iso(Util.Now());
            string target = Path.Combine(versions, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Util.AtomicWrite(Path.Combine(target, "plugin" + extension), data);
            Util.WriteJson(Path.Combine(target, "meta.json"), new JsonObject
            {
                ["Version"] = number, ["Title"] = title, ["Description"] = description, ["Author"] = author, ["PublishedAt"] = moment, ["Sha256"] = digest, ["Note"] = note,
            });
            var old = Util.ReadJsonObject(Path.Combine(folder, "meta.json")) ?? [];
            foreach (string other in Extensions)
                if (other != extension && File.Exists(Path.Combine(folder, "plugin" + other))) File.Delete(Path.Combine(folder, "plugin" + other));
            Util.AtomicWrite(Path.Combine(folder, "plugin" + extension), data);
            var latest = new JsonObject
            {
                ["Name"] = name, ["Title"] = title, ["Description"] = description, ["Author"] = owner, ["CreatedAt"] = Util.Or(Util.Field(old, "CreatedAt"), moment),
                ["UpdatedAt"] = moment, ["Sha256"] = digest, ["Downloads"] = Util.AsInt(Util.Field(old, "Downloads")), ["Version"] = number, ["Note"] = note,
            };
            foreach (string key in new[] { "Source", "SourceCheckedAt", "SourceError" })
                if (old.TryGetPropertyValue(key, out var value)) latest[key] = value?.DeepClone();
            if (source is not null)
            {
                latest["Source"] = source;
                latest["SourceError"] = "";
            }
            Util.WriteJson(Path.Combine(folder, "meta.json"), latest);
        }
        return new JsonObject { ["name"] = name, ["title"] = title, ["author"] = author, ["version"] = number, ["changed"] = true, ["updated"] = existed };
    }

    public List<string> Linked()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateDirectories(_directory).Select(Path.GetFileName).OfType<string>()
            .Where(name => SkillText.IsName(name) && Util.Truthy(Util.Field(Util.ReadJsonObject(Path.Combine(_directory, name, "meta.json")), "Source")))
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>링크한 플러그인의 깃허브 파일을 다시 읽음: 바뀌었으면 작성자의 새 버전("깃허브 변경 반영"). 새 버전 번호, 아니면 null (이유는 SourceError)</summary>
    public int? SyncSource(string name, string token = "")
    {
        var meta = Util.ReadJsonObject(Path.Combine(Folder(name), "meta.json")) ?? [];
        string link = Util.Text(Util.Field(meta, "Source"));
        if (link.Length == 0) return null;
        string? text;
        string problem = "";
        try { text = GitHubLinks.Fetch(link, token, plugin: true); }
        catch (RelayError error) { text = null; problem = error.Message; }
        if (text is not null && text.Length > CodeMax) { text = null; problem = "깃허브 파일이 너무 깁니다."; }
        if (text is not null)
        {
            string title = Util.Or(Util.Field(meta, "Title"), name), author = Util.Or(Util.Field(meta, "Author"), "");
            var current = Read(name);
            string code = text.Replace("\r\n", "\n");
            string extension = Extension(link);
            if (current is not null && (Util.Text(current["sha256"]).ToUpperInvariant() != Util.Sha256Hex(Util.Utf8.GetBytes(code)) || Util.Text(current["extension"]) != extension))
            {
                var result = Publish(new JsonObject
                {
                    ["name"] = name, ["title"] = title, ["description"] = Util.Or(Util.Field(meta, "Description"), ""), ["code"] = code, ["extension"] = extension,
                    ["author"] = author, ["note"] = "깃허브 변경 반영", ["source"] = link,
                });
                lock (_lock)
                {
                    string file = Path.Combine(Folder(name), "meta.json");
                    var fresh = Util.ReadJsonObject(file) ?? [];
                    fresh["SourceCheckedAt"] = Util.Iso(Util.Now());
                    fresh["SourceError"] = "";
                    Util.WriteJson(file, fresh);
                }
                return Util.IsTrue(result["changed"]) ? (int)Util.AsInt(result["version"]) : null;
            }
        }
        lock (_lock)
        {
            string file = Path.Combine(Folder(name), "meta.json");
            var fresh = Util.ReadJsonObject(file) ?? [];
            if (Util.Text(Util.Field(fresh, "Source")) == link)
            {
                fresh["SourceCheckedAt"] = Util.Iso(Util.Now());
                fresh["SourceError"] = problem;
                Util.WriteJson(file, fresh);
            }
        }
        return null;
    }

    public void Delete(string name)
    {
        lock (_lock) Skills.MoveToTrash(_directory, Folder(name), name);
    }

    public void Housekeep() => Skills.CleanTrash(_directory);
}
