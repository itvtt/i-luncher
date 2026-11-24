using System.Text;
using System.Text.Json.Nodes;

namespace IApi;

/// <summary>브라우저 관리 페이지 (관리자 비밀번호): / (상태)  /skills  /announcements  /apps</summary>
internal static class AdminPages
{
    private static string E(string? value) => Util.HtmlEscape(value);

    public static string Page(string title, string body) => $$"""
        <!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{E(title)}} · API 서버</title><style>
        :root{color-scheme:dark}
        body{font-family:"Pretendard","Malgun Gothic",sans-serif;background:#161616;color:#F2F2F2;margin:0}
        header{display:flex;gap:18px;align-items:center;padding:14px 24px;border-bottom:1px solid #2C2C2C;background:#1A1A1A}
        header b{color:#E5C07B} header a{color:#BDBDBD;text-decoration:none} header a:hover{color:#E5C07B}
        main{padding:22px 24px;max-width:1180px} h1{font-size:20px;margin:0 0 14px} h2{font-size:16px;margin:22px 0 10px;color:#E5C07B}
        a{color:#E5C07B} table{border-collapse:collapse;width:100%} th,td{border-bottom:1px solid #2C2C2C;padding:8px;text-align:left;vertical-align:top}
        th{color:#8C8C8C;font-weight:600} small,.muted{color:#8C8C8C}
        pre{white-space:pre-wrap;background:#101010;border:1px solid #2C2C2C;border-radius:6px;padding:14px;overflow-x:auto}
        form.inline{display:inline} button,input,select,textarea{font:inherit;background:#222;color:#F2F2F2;border:1px solid #3A3A3A;border-radius:6px;padding:5px 10px}
        button:hover{border-color:#E5C07B} textarea{width:100%;box-sizing:border-box} .cards{display:flex;gap:14px;flex-wrap:wrap}
        .card{background:#1C1C1C;border:1px solid #2C2C2C;border-radius:8px;padding:12px 16px;min-width:150px} .card b{font-size:22px;color:#E5C07B}
        </style></head><body><header><b>API 서버</b><a href="/">상태</a><a href="/skills">스킬</a>
        <a href="/announcements">공지</a><a href="/apps">i-luncher</a></header><main><h1>{{E(title)}}</h1>{{body}}</main></body></html>
        """;

    public static (string, string) SkillsPage(RelayServer server)
    {
        string rows = string.Concat(server.Skills.All().Select(skill =>
            $"<tr><td><a href=\"/skills/{Util.Text(skill["name"])}\">{E(Util.Text(skill["title"]))}</a><br><small>{E(Util.Text(skill["name"]))}</small></td>" +
            $"<td>{E(Util.Text(skill["description"]))}</td><td>{E(Util.Text(skill["author"]))}</td><td>v{Util.Text(skill["version"])}</td>" +
            $"<td>{E(Util.LocalText(Util.ParseTime(skill["updatedAt"])))}</td><td>{Util.Text(skill["downloads"])}</td></tr>"));
        string table = rows.Length > 0 ? $"<table><tr><th>제목</th><th>설명</th><th>작성자</th><th>최신</th><th>업데이트</th><th>받은 수</th></tr>{rows}</table>"
            : "<p class='muted'>아직 공유된 스킬이 없습니다.</p>";
        return ("공유된 스킬", table);
    }

    public static (string, string) SkillPage(RelayServer server, string name)
    {
        var skill = server.Skills.Version(name);
        long latest = Util.AsInt(skill["latestVersion"]);
        var rows = new StringBuilder();
        foreach (var node in server.Skills.Versions(name))
        {
            var item = (JsonObject)node!;
            long number = Util.AsInt(item["version"]);
            string action = number == latest ? "<small>최신</small>"
                : $"<form class=\"inline\" method=\"post\" action=\"/skills/{name}/restore\"><input type=\"hidden\" name=\"version\" value=\"{number}\">" +
                  "<button>이 버전으로 되돌리기</button></form>";
            rows.Append($"<tr><td>v{number}</td><td>{E(Util.LocalText(Util.ParseTime(item["publishedAt"])))}</td><td>{E(Util.Text(item["author"]))}</td>" +
                        $"<td>{E(Util.Text(item["note"]))}</td><td><a href=\"/skills/{name}/versions/{number}/SKILL.md\">원문</a></td><td>{action}</td></tr>");
        }
        string body = $"<p><a href=\"/skills\">← 목록</a> · 작성자 {E(Util.Text(skill["author"]))} · 최신 v{latest} · 받은 수 {Util.Text(skill["downloads"])}</p>" +
                      $"<pre>{E(Util.Text(skill["content"]))}</pre><h2>버전 기록</h2>" +
                      $"<table><tr><th>버전</th><th>올린 시각</th><th>작성자</th><th>바뀐 점</th><th></th><th></th></tr>{rows}</table>" +
                      $"<p><form class=\"inline\" method=\"post\" action=\"/skills/{name}/delete\"><button>스토어에서 삭제</button></form> " +
                      "<small>삭제한 스킬은 30일 동안 skills\\.trash에 남습니다. 되돌리기는 그 버전을 새 최신 버전으로 올립니다.</small></p>";
        return (Util.Text(skill["title"]), body);
    }

    public static (string, string) AnnouncementsPage(RelayServer server)
    {
        var board = server.Announcements.Read();
        bool exists = Util.IsTrue(board["exists"]);
        string history = string.Concat(server.Announcements.History().Take(20).Select(version => $"<li><a href=\"/announcements/history/{E(version)}\">{E(version[..Math.Min(15, version.Length)])}</a></li>"));
        string changed = exists ? $" 마지막 변경: {E(Util.LocalText(Util.ParseTime(board["updatedAt"])))} · {E(Util.Text(board["updatedBy"]))}" : "";
        string editors = E(server.Announcements.Editors.Count > 0 ? string.Join(", ", server.Announcements.Editors) : "앱에서 정한 계정");
        string body = $"<p class=\"muted\">앱의 공지 버튼에 보이는 공지입니다. 앱에서는 공지 작성 계정({editors})만 고칠 수 있습니다.{changed}</p>" +
                      $"<form method=\"post\" action=\"/announcements\"><input type=\"hidden\" name=\"revision\" value=\"{E(Util.Text(board["revision"]))}\">" +
                      $"<input type=\"hidden\" name=\"exists\" value=\"{(exists ? "1" : "0")}\">" +
                      $"<textarea name=\"text\" rows=\"22\">{E(Util.Text(board["text"]))}</textarea><p><button>공지 저장</button></p></form>" +
                      (history.Length > 0 ? $"<h2>이전 공지</h2><ul>{history}</ul>" : "");
        return ("공지", body);
    }

    public static (string, string) AppsPage(RelayServer server)
    {
        var catalog = server.Launcher.Catalog();
        var notices = server.Launcher.Notices();
        var apps = catalog["apps"]!.AsArray().OfType<JsonObject>().ToList();
        string rows = string.Concat(apps.Select(app =>
            $"<tr><td>{E(Util.Text(app["name"]))}</td><td>{E(Util.Text(app["id"]))}</td><td>{E(Util.Or(app["category"], ""))}</td>" +
            $"<td><a href=\"{E(Util.Text(app["repository"]))}\">{E(Util.Text(app["repository"]))}</a></td></tr>"));
        string document = new JsonObject { ["schemaVersion"] = 1, ["apps"] = catalog["apps"]!.DeepClone() }.ToJsonString(Util.Indent2);
        string repositories = server.Launcher.Repositories() is { Count: > 0 } found ? string.Join(", ", found.Order(StringComparer.Ordinal)) : "없음";
        string body = "<p class=\"muted\">i-luncher가 보여 주는 앱 목록과 공지입니다. i-api 관리 창 → 앱 목록이나 여기서 고칩니다.</p>"
            + (rows.Length > 0 ? $"<table><tr><th>이름</th><th>id</th><th>분류</th><th>저장소</th></tr>{rows}</table>" : "<p class=\"muted\">아직 앱이 없습니다.</p>")
            + "<h2>앱 목록 JSON</h2><form method=\"post\" action=\"/apps\"><input type=\"hidden\" name=\"kind\" value=\"catalog\">"
            + $"<input type=\"hidden\" name=\"revision\" value=\"{E(Util.Text(catalog["revision"]))}\"><textarea name=\"json\" rows=\"18\">{E(document)}</textarea>"
            + "<p><button>앱 목록 저장</button></p></form>"
            + "<h2>런처 공지</h2><p class=\"muted\">첫 줄은 제목, 다음 줄부터 본문, 공지 사이는 --- 만 있는 줄입니다. 런처가 시작할 때와 30분마다 읽습니다.</p>"
            + $"<form method=\"post\" action=\"/apps\"><input type=\"hidden\" name=\"kind\" value=\"notices\"><input type=\"hidden\" name=\"revision\" value=\"{E(Util.Text(notices["revision"]))}\">"
            + $"<textarea name=\"text\" rows=\"12\">{E(Util.Text(notices["text"]))}</textarea><p><button>공지 저장</button></p></form>"
            + $"<h2>GitHub 중계</h2><p class=\"muted\">런처와 앱은 <code>{E(server.Config.PublicUrl)}/api/v3</code>로 비공개 저장소의 릴리스를 토큰 없이 받습니다. "
            + $"열린 저장소: 앱 목록({E(repositories)}), LAUNCHER_GITHUB_REPOS({E(server.Config.GitHubProxyRepos.Count > 0 ? string.Join(", ", server.Config.GitHubProxyRepos) : "없음")}), 토큰 계정의 저장소.</p>";
        return ("i-luncher", body);
    }

    public static (string, string) StatusPage(RelayServer server)
    {
        var config = server.Config;
        string cards = string.Concat(new (string Label, string Value)[]
        {
            ("공유된 스킬", server.Skills.All().Count.ToString()), ("플러그인", server.Plugins.All().Count.ToString()),
            ("서버 목록 앱", ((server.Launcher.Catalog()["apps"] as JsonArray)?.Count ?? 0).ToString()),
        }.Select(card => $"<div class=\"card\"><small>{card.Label}</small><br><b>{card.Value}</b></div>"));
        var uptime = Util.Now() - server.Started;
        string lines = string.Concat(config.Summary().Append(server.Equipment.State()).Append(Setup.FirewallText(config)).Select(line => $"<li>{E(line)}</li>"));
        var board = server.Announcements.Read();
        string rows = string.Concat(server.Clients.All().Select(entry =>
            $"<tr><td>{E(entry.Address)}</td><td>{E(entry.Version.Length > 0 ? entry.Version : "1.0.518 이하")}</td><td>{E(Util.LocalText(entry.Last))}</td>" +
            $"<td>{(Util.IsTrue(board["exists"]) && entry.Revision == Util.Text(board["revision"]) ? "최신" : entry.Revision is not null ? "이전 공지" : "—")}</td>" +
            $"<td>{E(string.Join(" · ", entry.Used.Order(StringComparer.Ordinal).Select(feature => Clients.Features[feature])))}</td></tr>"));
        string clients = rows.Length > 0 ? $"<table><tr><th>PC 주소</th><th>앱 버전</th><th>마지막 접속</th><th>받은 공지</th><th>쓴 기능</th></tr>{rows}</table>"
            : "<p class='muted'>아직 접속한 PC가 없습니다.</p>";
        return ("상태", $"<div class=\"cards\">{cards}</div><h2>접속한 PC</h2>" +
                       "<p class=\"muted\">서버를 켠 뒤 앱이 접속한 PC입니다(앱은 1분마다 공지를 확인). " +
                       "여기에 없는 PC는 이 서버에 접속하지 못하는 것입니다: 방화벽(아래)·앱의 서버 주소·앱 버전을 확인하세요.</p>" +
                       $"{clients}<h2>설정</h2><ul>{lines}</ul>" +
                       $"<p class=\"muted\">버전 {Program.Version} · .NET {Environment.Version.ToString(3)} · 실행 {uptime.Days}일 {uptime.Hours}시간</p>");
    }
}

internal sealed partial class RelayServer
{
    /// <summary>브라우저 관리 페이지 (GET 은 화면, POST 는 같은 출처의 양식만)</summary>
    private void AdminPage(Request request)
    {
        var parts = request.Parts;
        if (request.Method == "POST")
        {
            RequireSameOrigin(request);
            var form = request.Form();
            if (parts is ["apps"])
            {
                if (form.GetValueOrDefault("kind") == "notices") Launcher.SaveNotices(JsonValue.Create(form.GetValueOrDefault("text", "")), form.GetValueOrDefault("revision", ""));
                else
                {
                    JsonNode? value;
                    try { value = System.Text.Json.Nodes.JsonNode.Parse(form.GetValueOrDefault("json", "")); }
                    catch (Exception error) when (error is System.Text.Json.JsonException or ArgumentException) { throw new RelayError(400, "앱 목록 JSON이 올바르지 않습니다."); }
                    Launcher.SaveCatalog(value is JsonObject document ? document["apps"] : value, form.GetValueOrDefault("revision", ""));
                }
                request.Redirect("/apps");
                return;
            }
            if (parts is ["skills", var skill, "delete"])
            {
                Skills.Delete(skill);
                request.Redirect("/skills");
                return;
            }
            if (parts is ["skills", var restored, "restore"])
            {
                string version = form.GetValueOrDefault("version", "");
                if (!SkillText.VersionNumber().IsMatch(version)) throw new RelayError(400, "버전이 올바르지 않습니다.");
                Skills.Restore(restored, int.Parse(version));
                request.Redirect($"/skills/{restored}");
                return;
            }
            if (parts is ["announcements"])
            {
                var board = Announcements.Read();
                bool exists = form.TryGetValue("exists", out var given) ? given == "1" : Util.IsTrue(board["exists"]);
                Announcements.Save(JsonValue.Create(form.GetValueOrDefault("text", "").Replace("\r\n", "\n")), JsonValue.Create(form.GetValueOrDefault("revision", "")),
                    JsonValue.Create(exists), "관리자", admin: true);
                request.Redirect("/announcements");
                return;
            }
            throw new RelayError(404, "찾을 수 없는 주소입니다.");
        }
        if (request.Method != "GET") throw new RelayError(405, "지원하지 않는 요청입니다.");
        (string Title, string Body)? page = parts switch
        {
            ["skills"] => AdminPages.SkillsPage(this),
            ["skills", var name] => AdminPages.SkillPage(this, name),
            ["announcements"] => AdminPages.AnnouncementsPage(this),
            [] or ["status"] => AdminPages.StatusPage(this),
            ["apps"] => AdminPages.AppsPage(this),
            _ => null,
        };
        if (page is { } shown)
        {
            request.Html(200, shown.Title, shown.Body);
            return;
        }
        switch (parts)
        {
            case ["skills", var name, "SKILL.md"]:
                request.Text(200, Util.Text(Skills.Version(name)["content"]));
                return;
            case ["skills", var name, "versions", var version, "SKILL.md"] when SkillText.VersionNumber().IsMatch(version):
                request.Text(200, Util.Text(Skills.Version(name, int.Parse(version))["content"]));
                return;
            case ["announcements", "history", var version]:
                request.Text(200, Announcements.VersionText(version));
                return;
        }
        throw new RelayError(404, "찾을 수 없는 주소입니다.");
    }
}
