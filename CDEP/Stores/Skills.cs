using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>SKILL.md 를 읽고 만드는 규칙 (앱과 같게)</summary>
internal static partial class SkillText
{
    public const int BodyMax = 100_000;
    public const int VersionsKeep = 100;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")] public static partial Regex Name();
    [GeneratedRegex("^[1-9][0-9]{0,5}$")] public static partial Regex VersionNumber();
    [GeneratedRegex(@"\A(?:[ \t]*\n)+")] private static partial Regex LeadingBlankLines();
    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+")] private static partial Regex ListMark();
    [GeneratedRegex("[*_`]+")] private static partial Regex Emphasis();

    public static bool IsName(string? name) => name is { Length: > 0 and <= 64 } && Name().IsMatch(name);

    /// <summary>작성자가 쓴 지시: SKILL.md 에서 앞머리와 서버가 붙인 '# 제목' 줄을 뺀 것</summary>
    public static string Body(string markdown, string title)
    {
        string text = markdown.Replace("\r\n", "\n");
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            int end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end >= 0) text = text[(end + 5)..];
        }
        text = text.TrimStart('\n');
        string heading = "# " + title + "\n";
        if (title.Length > 0 && text.StartsWith(heading, StringComparison.Ordinal)) text = text[heading.Length..].TrimStart('\n');
        return text.TrimEnd('\n');
    }

    public static byte[] Compose(string name, string title, string description, string body) =>
        Util.Utf8.GetBytes($"---\nname: {name}\ndescription: {Util.YamlString(description)}\n---\n\n# {title}\n\n{body}\n");

    private static string FrontmatterValue(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try { return JsonNode.Parse(value)?.GetValue<string>() ?? value[1..^1]; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { return value[1..^1]; }
        }
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1].Replace("''", "'");
        return value;
    }

    /// <summary>SKILL.md 나 그냥 지시를 앱처럼 읽음: 앞머리의 name · description, 첫 '# 제목', 앞머리를 뺀 지시</summary>
    public static (string? Name, string? Description, string? Heading, string Body) Split(string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimStart('﻿').Split('\n').ToList();
        string? name = null, description = null;
        if (lines.Count > 2 && lines[0].Trim() == "---")
        {
            int close = -1;
            for (int index = 1; index < lines.Count; index++)
                if (lines[index].Trim() == "---") { close = index; break; }
            if (close > 0)
            {
                foreach (string line in lines.GetRange(1, close - 1))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    string key = line[..colon].Trim();
                    if (key is not ("name" or "description")) continue;
                    string value = FrontmatterValue(line[(colon + 1)..]);
                    if (key == "name") name = value.Length > 0 ? value : null;
                    else description = value.Length > 0 ? value : null;
                }
                lines = lines.GetRange(close + 1, lines.Count - close - 1);
            }
        }
        string body = LeadingBlankLines().Replace(string.Join("\n", lines), "").TrimEnd();
        string first = body.Split('\n', 2)[0].Trim();
        return (name, description, first.StartsWith("# ", StringComparison.Ordinal) ? first[2..].Trim() : null, body);
    }

    /// <summary>남길 지시: '# 제목' 줄은 서버가 쓰므로 맨 위의 같은 줄은 뺌</summary>
    public static string BodyFor(string body, string title)
    {
        string first = body.Split('\n', 2)[0];
        return first.Trim() == "# " + title.Trim() ? LeadingBlankLines().Replace(body[first.Length..], "") : body;
    }

    /// <summary>스토어 카드의 짧은 줄: 지시의 첫 줄 (앞머리 · 제목 · 코드 울타리 · 주석 · 구분선은 빼고, 목록 표시 · 강조 없이)</summary>
    public static string Excerpt(string markdown, int limit = 120)
    {
        string text = markdown.Replace("\r\n", "\n");
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            int end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end >= 0) text = text[(end + 4)..];
        }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("<!--", StringComparison.Ordinal)
                || line.StartsWith('>') || line.All(c => "-*_=|:+ ".Contains(c))) continue;
            line = ListMark().Replace(line, "");
            line = Emphasis().Replace(line, "").Trim();
            if (line.Length > 0) return line.Length <= limit ? line : line[..(limit - 1)].TrimEnd() + "…";
        }
        return "";
    }
}

/// <summary>스킬 스토어: 스킬마다 폴더 (맨 위 SKILL.md · meta.json = 최신, versions\&lt;번호&gt;\ = 모든 버전)</summary>
internal sealed class Skills
{
    private readonly string _directory;
    private readonly Lock _lock = new();

    public Skills(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string Folder(string name) =>
        SkillText.IsName(name) ? Path.Combine(_directory, name) : throw new RelayError(404, "스킬을 찾지 못했습니다.");

    /// <summary>최신 버전의 카드 (스토어 목록에 보이는 것)</summary>
    public JsonObject? Read(string name)
    {
        string folder = Folder(name);
        var meta = Util.ReadJsonObject(Path.Combine(folder, "meta.json"));
        if (meta is null || meta.Count == 0 || !File.Exists(Path.Combine(folder, "SKILL.md"))) return null;
        var numbers = Numbers(folder);
        return new JsonObject
        {
            ["name"] = name, ["title"] = Util.Or(Util.Field(meta, "Title"), name), ["description"] = Util.Or(Util.Field(meta, "Description"), ""),
            ["author"] = Util.Or(Util.Field(meta, "Author"), ""), ["createdAt"] = OrNull(Util.Field(meta, "CreatedAt")), ["updatedAt"] = OrNull(Util.Field(meta, "UpdatedAt")),
            ["sha256"] = Util.Or(Util.Field(meta, "Sha256"), ""), ["downloads"] = Util.AsInt(Util.Field(meta, "Downloads")),
            ["version"] = Util.AsInt(Util.Field(meta, "Version"), numbers.Count > 0 ? numbers[^1] : 1), ["versionCount"] = numbers.Count > 0 ? numbers.Count : 1,
            ["note"] = Util.Or(Util.Field(meta, "Note"), ""), ["private"] = Util.IsTrue(meta["Private"]), ["source"] = Util.Or(Util.Field(meta, "Source"), ""),
            ["category"] = Util.Or(Util.Field(meta, "Category"), ""), ["origin"] = Util.Or(Util.Field(meta, "Origin"), ""),
            ["sourceError"] = Util.Or(Util.Field(meta, "SourceError"), ""),
        };
    }

    internal static JsonNode? OrNull(JsonNode? value) => Util.Truthy(value) ? value!.DeepClone() : null;

    /// <summary>비공개 스킬은 작성자에게만. null 은 관리 기능 (모든 스킬을 봄)</summary>
    public static bool Visible(JsonObject skill, string? viewer) =>
        viewer is null || !Util.IsTrue(skill["private"]) || Util.ViewerName(skill["author"]) == Util.ViewerName(viewer);

    public List<JsonObject> All(string? viewer = null)
    {
        var items = new List<JsonObject>();
        if (!Directory.Exists(_directory)) return items;
        foreach (string folder in Directory.EnumerateDirectories(_directory))
        {
            string name = Path.GetFileName(folder);
            if (!SkillText.IsName(name)) continue;
            var skill = Read(name);
            if (skill is null || !Visible(skill, viewer)) continue;
            try { skill["summary"] = SkillText.Excerpt(Util.ReadText(Path.Combine(folder, "SKILL.md"), sig: false, lines: true)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skill["summary"] = ""; }
            items.Add(skill);
        }
        return items.OrderBy(skill => Util.Text(skill["title"]), StringComparer.Ordinal).ToList();
    }

    public string Existing(string name, string? viewer = null)
    {
        var skill = Read(name);
        if (skill is null || !Visible(skill, viewer)) throw new RelayError(404, "스킬을 찾지 못했습니다.");
        return Folder(name);
    }

    private static List<int> Numbers(string folder)
    {
        string directory = Path.Combine(folder, "versions");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>()
            .Where(item => SkillText.VersionNumber().IsMatch(item) && File.Exists(Path.Combine(directory, item, "SKILL.md")))
            .Select(int.Parse).Order().ToList();
    }

    /// <summary>예전 서버로 공유한 스킬은 버전이 없음: 지금 내용이 버전 1</summary>
    private void Migrate(string folder)
    {
        if (Numbers(folder).Count > 0 || !File.Exists(Path.Combine(folder, "SKILL.md"))) return;
        var meta = Util.ReadJsonObject(Path.Combine(folder, "meta.json")) ?? [];
        WriteVersion(folder, 1, Util.ReadBytes(Path.Combine(folder, "SKILL.md")), Util.Or(Util.Field(meta, "Title"), Path.GetFileName(folder)),
            Util.Or(Util.Field(meta, "Description"), ""), Util.Or(Util.Field(meta, "Author"), ""), "",
            Util.Or(Util.Field(meta, "UpdatedAt"), Util.Or(Util.Field(meta, "CreatedAt"), Util.Iso(Util.Now()))));
        meta["Version"] = 1;
        Util.WriteJson(Path.Combine(folder, "meta.json"), meta);
    }

    private static void WriteVersion(string folder, int number, byte[] data, string title, string description, string author, string note, string moment)
    {
        string target = Path.Combine(folder, "versions", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Util.AtomicWrite(Path.Combine(target, "SKILL.md"), data);
        Util.WriteJson(Path.Combine(target, "meta.json"), new JsonObject
        {
            ["Version"] = number, ["Title"] = title, ["Description"] = description, ["Author"] = author, ["PublishedAt"] = moment, ["Sha256"] = Util.Sha256Hex(data), ["Note"] = note,
        });
    }

    /// <summary>맨 위 SKILL.md · meta.json 은 늘 최신 (예전 서버와 앱이 읽는 모양). 비공개 · 깃허브 링크는 이 버전이 따로 말하지 않으면 앞 버전 그대로</summary>
    private static void SetLatest(string folder, int number, byte[] data, string title, string description, string author, string note, string moment, JsonObject? extra = null)
    {
        var old = Util.ReadJsonObject(Path.Combine(folder, "meta.json")) ?? [];
        Util.AtomicWrite(Path.Combine(folder, "SKILL.md"), data);
        var meta = new JsonObject
        {
            ["Name"] = Path.GetFileName(folder), ["Title"] = title, ["Description"] = description, ["Author"] = author,
            ["CreatedAt"] = Util.Or(Util.Field(old, "CreatedAt"), moment), ["UpdatedAt"] = moment, ["Sha256"] = Util.Sha256Hex(data),
            ["Downloads"] = Util.AsInt(Util.Field(old, "Downloads")), ["Version"] = number, ["Note"] = note,
        };
        foreach (string key in new[] { "Private", "Source", "SourceCheckedAt", "SourceError", "Category", "Origin" })
            if (old.TryGetPropertyValue(key, out var value)) meta[key] = value?.DeepClone();
        foreach (var (key, value) in extra ?? []) meta[key] = value?.DeepClone();
        Util.WriteJson(Path.Combine(folder, "meta.json"), meta);
        var numbers = Numbers(folder);
        foreach (int oldNumber in numbers.Take(Math.Max(0, numbers.Count - SkillText.VersionsKeep)))
        {
            try { Util.DeleteTree(Path.Combine(folder, "versions", oldNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static JsonObject VersionInfo(int number, JsonObject meta) => new()
    {
        ["version"] = number, ["title"] = Util.Or(Util.Field(meta, "Title"), ""), ["description"] = Util.Or(Util.Field(meta, "Description"), ""),
        ["author"] = Util.Or(Util.Field(meta, "Author"), ""), ["publishedAt"] = OrNull(Util.Field(meta, "PublishedAt")),
        ["sha256"] = Util.Or(Util.Field(meta, "Sha256"), ""), ["note"] = Util.Or(Util.Field(meta, "Note"), ""),
    };

    /// <summary>남긴 모든 버전, 최신부터</summary>
    public JsonArray Versions(string name, string? viewer = null)
    {
        lock (_lock)
        {
            string folder = Existing(name, viewer);
            Migrate(folder);
            return new JsonArray(Numbers(folder).AsEnumerable().Reverse()
                .Select(number => (JsonNode)VersionInfo(number, Util.ReadJsonObject(Path.Combine(folder, "versions", number.ToString(), "meta.json")) ?? [])).ToArray());
        }
    }

    /// <summary>버전 하나 (번호가 없으면 최신)와 그 SKILL.md, 작성자가 쓴 지시</summary>
    public JsonObject Version(string name, int? number = null, bool countDownload = false, string? viewer = null)
    {
        lock (_lock)
        {
            string folder = Existing(name, viewer);
            Migrate(folder);
            var numbers = Numbers(folder);
            int chosen = number ?? numbers[^1];
            if (!numbers.Contains(chosen)) throw new RelayError(404, "그 버전을 찾지 못했습니다.");
            string source = Path.Combine(folder, "versions", chosen.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string content = Util.ReadText(Path.Combine(source, "SKILL.md"), sig: false, lines: true);
            var meta = Util.ReadJsonObject(Path.Combine(folder, "meta.json")) ?? [];
            long downloads = Util.AsInt(Util.Field(meta, "Downloads"));
            if (countDownload)
            {
                downloads++;
                meta["Downloads"] = downloads;
                Util.WriteJson(Path.Combine(folder, "meta.json"), meta);
            }
            var info = Read(name) ?? [];
            foreach (var (key, value) in VersionInfo(chosen, Util.ReadJsonObject(Path.Combine(source, "meta.json")) ?? [])) info[key] = value?.DeepClone();
            info["latestVersion"] = numbers[^1];
            info["downloads"] = downloads;
            info["content"] = content;
            info["body"] = SkillText.Body(content, Util.Text(info["title"]));
            return info;
        }
    }

    /// <summary>작성자 또는 마스터 계정(MASTER_USERS, 비면 앱이 정함)이 스킬을 고치거나 지움</summary>
    public static bool MayManage(JsonObject skill, string viewer, IReadOnlyCollection<string> admins) =>
        Util.ViewerName(skill["author"]) == viewer || admins.Count == 0 || admins.Contains(viewer);

    /// <summary>작성자만 언제든 모두에게 보이거나 비공개로 (새 버전 없이)</summary>
    public void SetPrivate(string name, bool hidden, string viewer)
    {
        lock (_lock)
        {
            var skill = Read(name);
            if (skill is null || !Visible(skill, viewer)) throw new RelayError(404, "스킬을 찾지 못했습니다.");
            if (Util.ViewerName(skill["author"]) != viewer) throw new RelayError(403, "공개 범위는 작성자만 바꿀 수 있습니다.");
            string file = Path.Combine(Folder(name), "meta.json");
            var meta = Util.ReadJsonObject(file) ?? [];
            meta["Private"] = hidden;
            Util.WriteJson(file, meta);
        }
    }

    /// <summary>이 이름들 중 이 계정이 더 쓸 수 없는 스킬 (다른 사람의 비공개): 앱이 PC 에서 뺌. 없어진 이름은 아님 (지워도 PC 에 적용한 것은 남음)</summary>
    public List<string> Withdrawn(JsonArray names, string viewer)
    {
        var found = new List<string>();
        foreach (var node in names)
        {
            if (Util.AsString(node) is not { } name || !SkillText.IsName(name)) continue;
            var skill = Read(name);
            if (skill is not null && !Visible(skill, viewer)) found.Add(name);
        }
        return found;
    }

    /// <summary>
    /// 스킬이나 새 버전을 공유: 이름을 처음 공유한 Windows 계정이 버전을 더하고, 스토어 관리 계정도 고친다고 말하면("edit") 더함. 스킬은 작성자의 것으로 남음
    /// </summary>
    public JsonObject Publish(JsonNode? body, string viewer = "", IReadOnlyCollection<string>? admins = null)
    {
        if (body is not JsonObject upload) throw new RelayError(400, "형식이 올바르지 않습니다.");
        string name = Util.AsString(upload["name"])?.Trim() ?? "";
        string title = Util.OneLine(upload["title"]), description = Util.OneLine(upload["description"]), author = Util.OneLine(upload["author"]);
        string note = Util.OneLine(upload["note"]);
        string text = (Util.AsString(upload["body"]) ?? "").Replace("\r\n", "\n").Trim();
        // 작성자가 새 버전의 번호를 정할 수 있음 (지금보다 커야 함). 없으면 다음 번호
        int? wanted;
        var version = upload["version"];
        if (version is null || Util.AsString(version) == "") wanted = null;
        else if (Util.IsInteger(version, out long given) && given is > 0 and <= 999999) wanted = (int)given;
        else if (Util.AsString(version) is { } versionText && SkillText.VersionNumber().IsMatch(versionText.Trim())) wanted = int.Parse(versionText.Trim());
        else throw new RelayError(400, "버전은 1 이상의 숫자로 씁니다.");
        if (!SkillText.IsName(name)) throw new RelayError(400, "영문 이름은 소문자·숫자·하이픈(-)으로 64자까지 씁니다.");
        if (title.Length is 0 or > 80) throw new RelayError(400, "제목은 80자까지 씁니다.");
        if (description.Length is 0 or > 500) throw new RelayError(400, "설명은 500자까지 씁니다.");
        if (text.Length is 0 or > SkillText.BodyMax) throw new RelayError(400, "내용이 비었거나 너무 깁니다.");
        if (author.Length is 0 or > 64) throw new RelayError(400, "작성자를 확인할 수 없습니다.");
        if (note.Length > 200) throw new RelayError(400, "바뀐 점은 200자까지 씁니다.");
        // 비공개 스킬은 작성자만 봄. 깃허브 링크면 그 파일을 따라감 (SyncSource)
        bool? hidden = Util.IsBool(upload["private"]) ? Util.IsTrue(upload["private"]) : null;
        string? source = Util.AsString(upload["source"])?.Trim();
        // 작성자가 공유할 때 고르거나 지어낸 카테고리: 스토어가 묶고 거름
        string? category = Util.IsString(upload["category"]) ? Util.OneLine(upload["category"]) : null;
        if (category is { Length: > 30 }) throw new RelayError(400, "카테고리는 30자까지 씁니다.");
        // 작성자가 쓴 출처 (사용자 id 나 링크). 따라가는 파일(source)이 아니고, "" 은 지움
        string? origin = Util.IsString(upload["origin"]) ? Util.OneLine(upload["origin"]) : null;
        if (origin is { Length: > 300 }) throw new RelayError(400, "출처는 300자까지 씁니다.");
        if (!string.IsNullOrEmpty(source)) GitHubLinks.Request(source);
        byte[] data = SkillText.Compose(name, title, description, text);
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
                if (!Util.IsTrue(upload["edit"])) throw new RelayError(409, "같은 영문 이름의 스킬이 이미 있습니다. 다른 이름을 쓰세요.");
                if (!Visible(existing, viewer) || !MayManage(existing, viewer, admins ?? [])) throw new RelayError(403, "다른 사람의 스킬은 수정할 수 없습니다.");
                if (hidden is not null && hidden != Util.IsTrue(existing["private"])) throw new RelayError(403, "공개 범위는 작성자만 바꿀 수 있습니다.");
                // 버전은 관리자의 것, 스킬은 작성자의 것
                owner = Util.Text(existing["author"]);
            }
            var extra = new JsonObject();
            if (hidden is not null) extra["Private"] = hidden;
            if (source is not null) extra["Source"] = source;
            if (category is not null) extra["Category"] = category;
            if (origin is not null) extra["Origin"] = origin;
            if (existing is not null)
            {
                Migrate(folder);
                if (Util.Text(existing["sha256"]).ToUpperInvariant() == digest)
                {
                    if (extra.Count > 0)
                    {
                        var meta = Util.ReadJsonObject(Path.Combine(folder, "meta.json")) ?? [];
                        foreach (var (key, value) in extra) meta[key] = value?.DeepClone();
                        Util.WriteJson(Path.Combine(folder, "meta.json"), meta);
                    }
                    return new JsonObject
                    {
                        ["name"] = name, ["title"] = title, ["author"] = owner, ["sha256"] = digest, ["version"] = existing["version"]?.DeepClone(), ["changed"] = false, ["updated"] = true,
                    };
                }
            }
            var numbers = Numbers(folder);
            int latest = numbers.Count > 0 ? numbers[^1] : 0;
            if (wanted is not null && wanted <= latest) throw new RelayError(400, $"버전은 지금 버전(v{latest})보다 큰 숫자로 씁니다.");
            number = wanted ?? latest + 1;
            string moment = Util.Iso(Util.Now());
            WriteVersion(folder, number, data, title, description, author, note, moment);
            SetLatest(folder, number, data, title, description, owner, note, moment, extra);
        }
        return new JsonObject { ["name"] = name, ["title"] = title, ["author"] = author, ["sha256"] = digest, ["version"] = number, ["changed"] = true, ["updated"] = existed };
    }

    /// <summary>깃허브 파일을 따라가는 스킬의 이름</summary>
    public List<string> Linked()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateDirectories(_directory).Select(Path.GetFileName).OfType<string>()
            .Where(name => SkillText.IsName(name) && Util.Truthy(Util.Field(Util.ReadJsonObject(Path.Combine(_directory, name, "meta.json")), "Source")))
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 링크한 스킬의 깃허브 파일을 다시 읽음: 바뀌었으면 작성자의 새 버전("깃허브 변경 반영"). 새 버전 번호, 바뀐 것이 없거나 읽지 못하면 null (이유는 SourceError)
    /// </summary>
    public int? SyncSource(string name, string token = "")
    {
        string link = Util.Text(Util.Field(Util.ReadJsonObject(Path.Combine(Folder(name), "meta.json")), "Source"));
        if (link.Length == 0) return null;
        string? text;
        string problem = "";
        try { text = GitHubLinks.Fetch(link, token); }
        catch (RelayError error) { text = null; problem = error.Message; }
        lock (_lock)
        {
            string folder = Folder(name);
            var meta = Util.ReadJsonObject(Path.Combine(folder, "meta.json")) ?? [];
            if (Util.Text(Util.Field(meta, "Source")) != link || !File.Exists(Path.Combine(folder, "SKILL.md"))) return null;
            string moment = Util.Iso(Util.Now());
            meta["SourceCheckedAt"] = moment;
            meta["SourceError"] = problem;
            string title = Util.Or(Util.Field(meta, "Title"), name), author = Util.Or(Util.Field(meta, "Author"), "");
            if (text is not null)
            {
                var (_, sourceDescription, _, body) = SkillText.Split(text);
                string description = Util.OneLine(sourceDescription is null ? null : JsonValue.Create(sourceDescription)) is { Length: > 0 } line ? line
                    : Util.Or(Util.Field(meta, "Description"), title);
                body = SkillText.BodyFor(body, title).Trim();
                if (body.Length is 0 or > SkillText.BodyMax || description.Length > 500) meta["SourceError"] = "깃허브 파일이 비었거나 너무 깁니다.";
                else
                {
                    byte[] data = SkillText.Compose(name, title, description, body);
                    if (Util.Sha256Hex(data) != Util.Text(Util.Field(meta, "Sha256")).ToUpperInvariant())
                    {
                        Migrate(folder);
                        var numbers = Numbers(folder);
                        int number = (numbers.Count > 0 ? numbers[^1] : 0) + 1;
                        const string note = "깃허브 변경 반영";
                        WriteVersion(folder, number, data, title, description, author, note, moment);
                        SetLatest(folder, number, data, title, description, author, note, moment, new JsonObject { ["SourceCheckedAt"] = moment, ["SourceError"] = "" });
                        return number;
                    }
                }
            }
            Util.WriteJson(Path.Combine(folder, "meta.json"), meta);
            return null;
        }
    }

    /// <summary>예전 버전을 다시 최신으로 (새 버전으로: 어느 버전의 앱이든 업데이트로 봄)</summary>
    public int Restore(string name, int number)
    {
        lock (_lock)
        {
            string folder = Existing(name);
            Migrate(folder);
            var numbers = Numbers(folder);
            if (!numbers.Contains(number)) throw new RelayError(404, "그 버전을 찾지 못했습니다.");
            string source = Path.Combine(folder, "versions", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            byte[] data = Util.ReadBytes(Path.Combine(source, "SKILL.md"));
            var current = Read(name)!;
            if (Util.Text(current["sha256"]).ToUpperInvariant() == Util.Sha256Hex(data)) return (int)Util.AsInt(current["version"]);
            var meta = Util.ReadJsonObject(Path.Combine(source, "meta.json")) ?? [];
            string title = Util.Or(Util.Field(meta, "Title"), Util.Text(current["title"])), description = Util.Or(Util.Field(meta, "Description"), Util.Text(current["description"]));
            int newer = numbers[^1] + 1;
            string moment = Util.Iso(Util.Now()), note = $"v{number}로 되돌림(관리자)";
            WriteVersion(folder, newer, data, title, description, Util.Text(current["author"]), note, moment);
            SetLatest(folder, newer, data, title, description, Util.Text(current["author"]), note, moment);
            return newer;
        }
    }

    /// <summary>.trash 로 옮김 (30일 보관): 잘못 지운 것을 폴더에서 되살릴 수 있게</summary>
    public void Delete(string name)
    {
        lock (_lock) MoveToTrash(_directory, Folder(name), name);
    }

    internal static void MoveToTrash(string directory, string folder, string name)
    {
        if (!Directory.Exists(folder)) return;
        string trash = Path.Combine(directory, ".trash");
        Directory.CreateDirectory(trash);
        Util.Retry(() => Directory.Move(folder, Path.Combine(trash, $"{name}-{Util.Stamp()}")));
    }

    public void Housekeep() => CleanTrash(_directory);

    internal static void CleanTrash(string directory)
    {
        string trash = Path.Combine(directory, ".trash");
        if (!Directory.Exists(trash)) return;
        var limit = DateTime.UtcNow.AddDays(-30);
        foreach (var item in new DirectoryInfo(trash).EnumerateFileSystemInfos())
        {
            try
            {
                if (item.LastWriteTimeUtc >= limit) continue;
                if (item is DirectoryInfo folder) Util.DeleteTree(folder.FullName);
                else item.Delete();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
