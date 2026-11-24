using System.Text.Json.Nodes;

namespace IApi;

/// <summary>API 처리기 (예전 relay.py 의 Handler 와 같은 규칙 · 같은 응답)</summary>
internal sealed partial class RelayServer
{
    // ── i-luncher ───────────────────────────────────────────────────────────────────────────────

    private void LauncherCatalog(Request request)
    {
        var catalog = Launcher.Catalog();
        string etag = $"\"{Util.Text(catalog["revision"])}\"";
        if (request.Header("If-None-Match") == etag)
        {
            request.NotModified(etag);
            return;
        }
        request.Json(200, catalog, [new("ETag", etag)]);
    }

    private void LauncherSave(Request request)
    {
        RequireAdmin(request);
        var body = request.JsonBody() as JsonObject ?? throw new RelayError(400, "JSON 객체가 필요합니다.");
        string revision = Util.Or(body["revision"], "");
        if (request.Parts is [_, "catalog"])
        {
            var saved = Launcher.SaveCatalog(body["apps"], revision);
            Log.Info($"launcher catalog saved from {request.RemoteAddress}: {saved["apps"]!.AsArray().Count} apps");
            request.Json(200, saved);
            return;
        }
        request.Json(200, Launcher.SaveNotices(body["text"], revision));
    }

    private void LauncherMedia(Request request)
    {
        // 앱 페이지의 스크린샷: 앱 목록처럼 누구나 읽음, 이름이 내용이라 바뀌지 않음
        string path = Launcher.MediaFile(request.Value("name"));
        string kind = Path.GetExtension(path)[1..];
        request.Send(200, Util.ReadBytes(path), LauncherStore.MediaTypes[kind], [new("ETag", $"\"{Path.GetFileNameWithoutExtension(path)}\"")]);
    }

    private void LauncherUpload(Request request)
    {
        RequireAdmin(request);
        string name = Launcher.AddMedia(request.Body());
        Log.Info($"launcher image {name} uploaded from {request.RemoteAddress}");
        request.Json(201, new JsonObject { ["name"] = name });
    }

    private void UsageReport(Request request)
    {
        // 앱의 사용 기록 (AppKit AppUsage): 앱 목록처럼 열려 있음, 작고 수만
        Limit(request, "usage", 1200, 3600);
        string? length = request.Header("Content-Length");
        if (length is { Length: > 0 } && length.All(char.IsAsciiDigit) && (!long.TryParse(length, out long size) || size > Usage.BodyMax))
            throw new RelayError(413, "사용량은 한 번에 64KB까지 받습니다.");
        byte[] body = request.Body();
        if (body.Length > Usage.BodyMax) throw new RelayError(413, "사용량은 한 번에 64KB까지 받습니다.");
        request.Json(200, new JsonObject { ["days"] = Usage.AddReport(Util.ParseJson(body)) });
    }

    private void UsageSummary(Request request)
    {
        string today = Util.LocalDay(Util.Now());
        request.Json(200, Usage.Summary(request.QueryValue("from") ?? today[..4] + "-01-01", request.QueryValue("to") ?? today));
    }

    // ── Terminal Notes ──────────────────────────────────────────────────────────────────────────

    private void ReportSend(Request request)
    {
        Limit(request, "report", Config.UploadsPerHour, 3600);
        string contentType = request.HeaderOr("Content-Type");
        if (!contentType.Contains("multipart/form-data", StringComparison.Ordinal)) throw new RelayError(400, "multipart/form-data가 필요합니다.");
        var (fields, files) = Util.ParseMultipart(contentType, request.Body());
        string report = fields.GetValueOrDefault("report", "");
        if (report.Trim().Length == 0 || report.Length > Reports.MaxChars) throw new RelayError(400, "보고서가 비었거나 너무 깁니다.");
        byte[]? logs = files.TryGetValue("logs", out var file) && file.Data.Length > 0 ? file.Data : null;
        var meta = Reports.Add(report, logs, request.RemoteAddress);
        string version = Util.Text(meta["AppVersion"]);
        Log.Info($"report {Util.Text(meta["Id"])} received ({version})");
        Notifier.Send($"[Terminal Notes] 새 문제 보고 · {(version.Length > 0 ? version : "버전 미상")} · {Util.Text(meta["Summary"])}\n{Config.PublicUrl}/reports/{Util.Text(meta["Id"])}");
        request.Json(200, new JsonObject { ["id"] = Util.Text(meta["Id"]) });
    }

    private void SkillSource(Request request)
    {
        // 깃허브 링크의 지시: 공유 창이 공유하기 전에 읽음 (제목, 앞머리)
        Limit(request, "source", 120, 3600);
        var upload = request.JsonBody();
        string link = upload is JsonObject value ? Util.AsString(value["source"]) ?? "" : "";
        request.Json(200, new JsonObject { ["content"] = GitHubLinks.Fetch(link, Config.GitHubToken) });
    }

    private void SkillWithdrawn(Request request)
    {
        var upload = request.JsonBody();
        if (upload is not JsonObject value || value["names"] is not JsonArray names || names.Count > 500) throw new RelayError(400, "형식이 올바르지 않습니다.");
        request.Json(200, new JsonObject { ["withdrawn"] = new JsonArray(Skills.Withdrawn(names, Viewer(request)).Select(name => (JsonNode)name).ToArray()) });
    }

    private void SkillPrivate(Request request)
    {
        var upload = request.JsonBody();
        if (upload is not JsonObject value || !Util.IsBool(value["private"])) throw new RelayError(400, "형식이 올바르지 않습니다.");
        bool hidden = Util.IsTrue(value["private"]);
        string name = request.Value("name"), viewer = Viewer(request);
        Skills.SetPrivate(name, hidden, viewer);
        Log.Info($"skill {name} made {(hidden ? "private" : "public")} by {(viewer.Length > 0 ? viewer : "?")}");
        request.Json(200, new JsonObject { ["name"] = name, ["private"] = hidden });
    }

    private void SkillDelete(Request request)
    {
        // 작성자나 스토어 관리 계정
        string name = request.Value("name"), viewer = Viewer(request);
        Skills.Existing(name, viewer);
        if (!Skills.MayManage(Skills.Read(name)!, viewer, Masters)) throw new RelayError(403, "작성자나 스킬 관리 계정만 삭제할 수 있습니다.");
        Skills.Delete(name);
        Log.Info($"skill {name} deleted by {(viewer.Length > 0 ? viewer : "?")}");
        request.Json(200, new JsonObject { ["deleted"] = name });
    }

    private void SkillVersion(Request request)
    {
        string version = request.Value("version");
        if (!SkillText.VersionNumber().IsMatch(version)) throw new RelayError(404, "그 버전을 찾지 못했습니다.");
        // ?apply=1 이면 받은 수를 셈 (보기만 하면 세지 않음)
        bool apply = request.Query.TryGetValue("apply", out var values) && values is ["1"];
        request.Json(200, Skills.Version(request.Value("name"), int.Parse(version), countDownload: apply, viewer: Viewer(request)));
    }

    private void SkillPublish(Request request)
    {
        Limit(request, "skill", Config.UploadsPerHour, 3600);
        var result = Skills.Publish(request.JsonBody(), Viewer(request), Masters);
        if (Util.IsTrue(result["changed"]))
        {
            string name = Util.Text(result["name"]);
            Log.Info($"skill {name} v{Util.Text(result["version"])} shared by {Util.Text(result["author"])}");
            // 비공개 스킬은 작성자의 것: 아무 알림도 나가지 않음
            if (!Util.IsTrue(Skills.Read(name)?["private"]))
            {
                string kind = Util.IsTrue(result["updated"]) ? $"새 버전 v{Util.Text(result["version"])}" : "공유";
                Notifier.Send($"[Terminal Notes] 스킬 {kind} · {Util.Text(result["title"])} · {Util.Text(result["author"])}\n{Config.PublicUrl}/skills/{name}");
            }
        }
        request.Json(200, new JsonObject
        {
            ["name"] = result["name"]?.DeepClone(), ["sha256"] = result["sha256"]?.DeepClone(), ["version"] = result["version"]?.DeepClone(), ["changed"] = result["changed"]?.DeepClone(),
        });
    }

    private void PluginSource(Request request)
    {
        Limit(request, "source", 120, 3600);
        var upload = request.JsonBody();
        string link = upload is JsonObject value ? Util.AsString(value["source"]) ?? "" : "";
        string content = GitHubLinks.Fetch(link, Config.GitHubToken, plugin: true);
        request.Json(200, new JsonObject { ["content"] = content, ["extension"] = Plugins.LinkExtension(link) });
    }

    private void PluginPublish(Request request)
    {
        Limit(request, "plugin", Config.UploadsPerHour, 3600);
        var result = Plugins.Publish(request.JsonBody(), Viewer(request), Masters);
        if (Util.IsTrue(result["changed"]))
        {
            Log.Info($"plugin {Util.Text(result["name"])} v{Util.Text(result["version"])} shared by {Util.Text(result["author"])}");
            Notifier.Send($"[Terminal Notes] 플러그인 {(Util.IsTrue(result["updated"]) ? "새 버전 v" + Util.Text(result["version"]) : "공유")} · " +
                          $"{Util.Text(result["title"])} · {Util.Text(result["author"])}");
        }
        request.Json(200, new JsonObject { ["name"] = result["name"]?.DeepClone(), ["version"] = result["version"]?.DeepClone(), ["changed"] = result["changed"]?.DeepClone() });
    }

    private void PluginDelete(Request request)
    {
        string name = request.Value("name"), viewer = Viewer(request);
        var plugin = Plugins.Read(name) ?? throw new RelayError(404, "플러그인을 찾지 못했습니다.");
        var masters = Masters;
        if (!(Util.ViewerName(plugin["author"]) == viewer || masters.Count == 0 || masters.Contains(viewer))) throw new RelayError(403, "작성자나 마스터 계정만 삭제할 수 있습니다.");
        Plugins.Delete(name);
        Log.Info($"plugin {name} deleted by {(viewer.Length > 0 ? viewer : "?")}");
        request.Json(200, new JsonObject { ["deleted"] = name });
    }

    private void AnnouncementRead(Request request)
    {
        var board = Announcements.Read();
        string revision = Util.Text(board["revision"]), etag = "\"" + revision + "\"";
        Clients.Received(request.RemoteAddress, revision);
        // 이 공지를 받은 PC 수: 작성자가 다른 PC 가 실제로 받는지 봄
        string having = Clients.Having(revision).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (request.Header("If-None-Match") == etag)
        {
            request.NotModified(etag, ("X-TerminalNotes-Seen-By", having));
            return;
        }
        request.Json(200, board, [new("ETag", etag), new("X-TerminalNotes-Seen-By", having)]);
    }

    private void AnnouncementSave(Request request)
    {
        Limit(request, "announce", 30, 3600);
        if (request.JsonBody() is not JsonObject upload) throw new RelayError(400, "형식이 올바르지 않습니다.");
        var board = Announcements.Save(upload["text"], upload["expectedRevision"], upload["expectedExists"], Util.OneLine(upload["author"]));
        string revision = Util.Text(board["revision"]);
        Clients.Received(request.RemoteAddress, revision);
        Notifier.Send($"[Terminal Notes] 공지 변경 · {Util.OneLine(upload["author"])}\n{Config.PublicUrl}/announcements");
        request.Json(200, board, [new("X-TerminalNotes-Seen-By", Clients.Having(revision).ToString(System.Globalization.CultureInfo.InvariantCulture))]);
    }

    // ── GitHub 중계 · 설비 상태 ──────────────────────────────────────────────────────────────────

    private void GitHubRelay(Request request)
    {
        Limit(request, "github", 3000, 3600);
        string agent = request.HeaderOr("User-Agent");
        Clients.Touch(request.RemoteAddress, agent, "github");
        var parts = request.Parts.Skip(2).ToList();
        Usage.ObserveGitHub(parts, agent, request.HeaderOr("Accept"), request.RemoteAddress);
        GitHub.Serve(request, parts, PublicBase(request));
    }

    /// <summary>EQ Status: 설비 표 {items, updatedAt}, 앱 키 대신 자기 키(EQUIPMENT_API_KEY)</summary>
    private void EquipmentRead(Request request)
    {
        string address = request.RemoteAddress, agent = request.HeaderOr("User-Agent");
        if (request.Method != "GET") throw new RelayError(405, "설비 상태는 읽기(GET)만 합니다.");
        string key = Config.EquipmentApiKey;
        if (key.Length > 0 && !Util.SecretEquals(request.HeaderOr("X-EQStatus-API-Key"), key))
        {
            Clients.Touch(address, agent, "badkey");
            throw new RelayError(401, "키가 맞지 않습니다.");
        }
        Limit(request, "equipment", 6000, 3600);
        Clients.Touch(address, agent, "equipment");
        var snapshot = Equipment.Snapshot();
        if (request.Header("If-None-Match") == snapshot.ETag)
        {
            request.NotModified(snapshot.ETag);
            return;
        }
        request.Json(200, new JsonObject { ["items"] = snapshot.Items.DeepClone(), ["updatedAt"] = Util.Iso(snapshot.At) }, [new("ETag", snapshot.ETag)]);
    }

    // ── 관리 창 ─────────────────────────────────────────────────────────────────────────────

    public JsonObject StatusData()
    {
        var reports = Reports.All();
        var board = Announcements.Read();
        string firewall = Setup.FirewallText(Config);
        var settings = new JsonArray(Config.Summary().Append(Equipment.State()).Select(line => (JsonNode)line).ToArray());
        return new JsonObject
        {
            ["version"] = Program.Version, ["runtime"] = ".NET " + Environment.Version.ToString(3), ["startedAt"] = Util.Iso(Started),
            ["uptimeSeconds"] = (long)(Util.Now() - Started).TotalSeconds, ["publicUrl"] = Config.PublicUrl,
            ["counts"] = new JsonObject
            {
                ["reports"] = reports.Count, ["newReports"] = reports.Count(item => item.Status == "new"), ["skills"] = Skills.All().Count,
                ["reportsBytes"] = Util.FolderSize(Config.ReportsDir),
            },
            ["settings"] = settings, ["firewall"] = firewall,
            ["githubRepositories"] = new JsonArray(Launcher.Repositories().Order(StringComparer.Ordinal).Select(item => (JsonNode)item).ToArray()),
            ["githubProxyRepositories"] = new JsonArray(Config.GitHubProxyRepos.Select(item => (JsonNode)item).ToArray()),
            ["functionsOff"] = Functions.Count(function => function.Switchable && !Switches.IsOn(function.Id)),
            ["clients"] = new JsonArray(Clients.All().Select(entry => (JsonNode)new JsonObject
            {
                ["address"] = entry.Address, ["version"] = entry.Version, ["first"] = Util.Iso(entry.First), ["last"] = Util.Iso(entry.Last), ["requests"] = entry.Requests,
                ["announcement"] = Util.IsTrue(board["exists"]) && entry.Revision == Util.Text(board["revision"]) ? "latest" : entry.Revision is not null ? "older" : "",
                ["features"] = new JsonArray(entry.Used.Order(StringComparer.Ordinal).Select(feature => (JsonNode)Clients.Features[feature]).ToArray()),
            }).ToArray()),
        };
    }

    private void AdminReport(Request request)
    {
        string id = request.Value("id");
        switch (request.Method)
        {
            case "GET":
                var detail = Reports.Meta(id).ToJson();
                detail["text"] = Reports.Text(id);
                request.Json(200, detail);
                return;
            case "PUT":
                var body = request.JsonObjectBody();
                var status = body["status"];
                var note = body["note"];
                if (status is not null && !Util.IsString(status) || note is not null && !Util.IsString(note)) throw new RelayError(400, "형식이 올바르지 않습니다.");
                Reports.Update(id, Util.AsString(status), Util.AsString(note));
                request.Json(200, Reports.Meta(id).ToJson());
                return;
            default:
                Reports.Delete(id);
                request.Json(200, new JsonObject { ["deleted"] = id });
                return;
        }
    }

    private void ReportLogs(Request request)
    {
        string id = request.Value("id");
        string logs = Path.Combine(Reports.Folder(id), "logs.zip");
        if (!File.Exists(logs)) throw new RelayError(404, "로그가 없습니다.");
        request.File(logs, "application/zip", $"TerminalNotes-{id}.zip");
    }

    private void AdminSkill(Request request)
    {
        string name = request.Value("name");
        if (request.Method == "GET")
        {
            request.Json(200, new JsonObject { ["skill"] = Skills.Version(name), ["versions"] = Skills.Versions(name) });
            return;
        }
        Skills.Delete(name);
        Log.Info($"skill {name} deleted from the admin window");
        request.Json(200, new JsonObject { ["deleted"] = name });
    }

    private void AdminSkillVersion(Request request)
    {
        string version = request.Value("version");
        if (!SkillText.VersionNumber().IsMatch(version)) throw new RelayError(404, "그 버전을 찾지 못했습니다.");
        request.Json(200, Skills.Version(request.Value("name"), int.Parse(version)));
    }

    private void AdminSkillRestore(Request request)
    {
        var version = request.JsonObjectBody()["version"];
        if (!Util.IsInteger(version, out long number) || number < 1) throw new RelayError(400, "버전이 올바르지 않습니다.");
        request.Json(200, new JsonObject { ["name"] = request.Value("name"), ["version"] = Skills.Restore(request.Value("name"), (int)Math.Min(number, int.MaxValue)) });
    }

    private JsonObject BoardWithHistory(JsonObject board)
    {
        board["history"] = new JsonArray(Announcements.History().Take(50).Select(item => (JsonNode)item).ToArray());
        board["editors"] = new JsonArray(Announcements.Editors.Select(item => (JsonNode)item).ToArray());
        return board;
    }

    private void AdminAnnouncements(Request request)
    {
        if (request.Method == "GET")
        {
            request.Json(200, BoardWithHistory(Announcements.Read()));
            return;
        }
        var body = request.JsonObjectBody();
        if (!Util.IsString(body["text"]) || !Util.IsString(body["revision"]) || !Util.IsBool(body["exists"])) throw new RelayError(400, "형식이 올바르지 않습니다.");
        var board = Announcements.Save(JsonValue.Create(Util.AsString(body["text"])!.Replace("\r\n", "\n")), body["revision"], body["exists"], "관리자", admin: true);
        request.Json(200, BoardWithHistory(board));
    }

    private void AdminReleases(Request request)
    {
        if (request.Method == "GET")
        {
            request.Json(200, new JsonObject { ["releases"] = Releases.List() });
            return;
        }
        var body = request.JsonObjectBody();
        request.Json(202, Releases.Release(body["repository"], body["version"]));
    }

    // 러너: 서버 PC 에서 프로그램을 시작하고 작업 스케줄러를 바꿈 → 그 PC(그 PC 의 관리 창)에서만

    private void RunnerAdd(Request request)
    {
        RequireThisPc(request);
        var body = request.JsonObjectBody();
        request.Json(201, Runners.Add(body["repository"], body["folder"], body["name"], body["env"]));
    }

    private void RunnerTask(Request request)
    {
        RequireThisPc(request);
        request.Json(200, Runners.InstallTask());
    }

    private void RunnerChange(Request request)
    {
        RequireThisPc(request);
        string id = request.Value("id");
        if (request.Method == "PUT")
        {
            request.Json(200, Runners.Save(id, request.JsonObjectBody()["env"]));
            return;
        }
        request.Json(200, Runners.Remove(id, request.Query.TryGetValue("force", out var force) && force is ["1"]));
    }

    private void RunnerControl(Request request)
    {
        RequireThisPc(request);
        string id = request.Value("id"), action = request.Value("action");
        if (action is not ("start" or "stop" or "restart")) throw new RelayError(404, "찾을 수 없는 주소입니다.");
        bool force = Util.IsTrue(request.JsonObjectBody()["force"]);
        request.Json(200, action switch
        {
            "start" => Runners.Start(id, force),
            "stop" => Runners.Stop(id, force),
            _ => Runners.Restart(id, force),
        });
    }
}
