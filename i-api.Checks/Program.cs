// API 서버 i-api 확인 (예전 CDEP\test_relay.py): dotnet run --project i-api.Checks
// 서버마다 루프백의 빈 포트 · 임시 폴더. 이 PC 의 실제 데이터 폴더 · 예전 폴더 · 시작 폴더 · 작업 스케줄러 · 방화벽은 건드리지 않음.
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using IApi;
using IApi.Checks;
using static IApi.Checks.Expect;

string sandbox = Harness.NewFolder("i-api checks");
Config.DefaultDataRoot = () => Path.Combine(sandbox, "default-data");
Config.LegacyDataRoots = () => [];
Config.ProgramFolder = Path.Combine(sandbox, "program");
Setup.StartupFolder = () => Path.Combine(sandbox, "Startup");
Setup.RunHidden = (_, _, _) => null;  // 방화벽: 묻지 않음 (확인 하나가 따로 흉내 냄)
ProcessTable.Source = () => [];
RelayServer.IsThisPc = address => address is "127.0.0.1" or "::1";

int passed = 0, failed = 0;
// 이름 일부로 골라 돌림 (dotnet run 이 넘기는 -m:1 같은 빌드 옵션은 고르기가 아님: 그것 때문에 하나도 돌지 않으면 안 됨)
var picked = args.Where(argument => !argument.StartsWith('-')).ToArray();
var only = picked.Length > 0 ? picked : null;

void Check(string name, Action action)
{
    if (only is not null && !only.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) return;
    try
    {
        action();
        passed++;
        Console.WriteLine("ok   " + name);
    }
    catch (Exception error)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {(error is CheckFailed ? error.Message : error.ToString())}");
    }
}

JsonNode J(string text) => JsonNode.Parse(text)!;
(string, string)[] User(string name) => [("X-TerminalNotes-User", name)];

Check("health and keys", () =>
{
    using var h = new Harness();
    Equal("ok", h.Call("GET", "/health", key: false).Text);
    var refused = h.Call("GET", "/api/skills", key: false);
    Equal(401, refused.Status);
    Equal("키가 맞지 않습니다.", Str(refused.Json));
    Equal(401, h.Call("GET", "/", key: false).Status);
    Equal(200, h.Call("GET", "/", key: false, admin: Harness.Admin).Status);
    Equal(501, h.Call("PATCH", "/health", key: false).Status, "unknown method");
});

Check("upload limits and size", () =>
{
    using var h = new Harness();
    for (int index = 0; index < 5; index++)
        Equal(200, h.Json("POST", "/api/skills", J($$"""{"name":"limit-{{index}}","title":"t","description":"d","body":"b","author":"a"}""")).Status);
    Equal(429, h.Json("POST", "/api/skills", J("""{"name":"limit-over","title":"t","description":"d","body":"b","author":"a"}""")).Status, "an hour's shares per address");
    // 25 MB 가 넘는 길이는 본문을 읽기 전에 거절
    using var socket = new System.Net.Sockets.TcpClient("127.0.0.1", h.Server.Port);
    var stream = socket.GetStream();
    stream.Write(Encoding.ASCII.GetBytes($"PUT /api/announcements HTTP/1.1\r\nHost: x\r\nX-TerminalNotes-Key: {Harness.Key}\r\nContent-Length: {Request.MaxBody + 1}\r\n\r\nxxxxxxxxxx"));
    var reply = new StreamReader(stream).ReadLine() ?? "";
    Contains(" 413 ", reply);
});

Check("skill store versions", () =>
{
    using var h = new Harness();
    var upload = J("""{"name":"impala-query","title":"Impala 조회","description":"데이터레이크: \"Impala\"","body":"- Impala로 조회한다.","author":"alice"}""").AsObject();
    var result = h.Json("POST", "/api/skills", upload).Json!;
    Equal("1 true", $"{result["version"]} {result["changed"]}");
    Equal(1L, Long(h.Json("POST", "/api/skills", upload).Json!["version"]), "the same content is no new version");
    var listed = h.Json("GET", "/api/skills").Json!.AsArray();
    Equal("impala-query alice 1 0", $"{Str(listed[0]!["name"])} {Str(listed[0]!["author"])} {listed[0]!["version"]} {listed[0]!["downloads"]}");
    Equal("Impala로 조회한다.", Str(listed[0]!["summary"]));
    var skill = h.Json("GET", "/api/skills/impala-query").Json!;
    True(Str(skill["content"]).StartsWith("---\nname: impala-query\ndescription: \"데이터레이크: \\\"Impala\\\"\"\n---\n\n# Impala 조회\n\n- Impala로 조회한다."), Str(skill["content"]));
    Equal($"{Str(result["sha256"])}|- Impala로 조회한다.|1", $"{Str(skill["sha256"])}|{Str(skill["body"])}|{skill["version"]}");
    Equal(1L, Long(h.Json("GET", "/api/skills").Json![0]!["downloads"]));
    var taken = h.Json("POST", "/api/skills", Merge(upload, """{"author":"bob"}"""));
    Equal(409, taken.Status);
    Equal("같은 영문 이름의 스킬이 이미 있습니다. 다른 이름을 쓰세요.", Str(taken.Json));
    Equal(400, h.Json("POST", "/api/skills", Merge(upload, """{"name":"Bad Name"}""")).Status);
    var second = h.Json("POST", "/api/skills", Merge(upload, """{"body":"- Impala로만 조회한다.","note":"Hive 금지 추가","author":"ALICE"}"""));
    Equal("200 2", $"{second.Status} {second.Json!["version"]}");
    listed = h.Json("GET", "/api/skills").Json!.AsArray();
    Equal("2 2 Hive 금지 추가", $"{listed[0]!["version"]} {listed[0]!["versionCount"]} {Str(listed[0]!["note"])}");
    var versions = h.Json("GET", "/api/skills/impala-query/versions").Json!.AsArray();
    Equal("2:Hive 금지 추가,1:", string.Join(",", versions.Select(item => $"{item!["version"]}:{Str(item["note"])}")));
    var old = h.Json("GET", "/api/skills/impala-query/versions/1").Json!;
    Equal("1 2 - Impala로 조회한다.", $"{old["version"]} {old["latestVersion"]} {Str(old["body"])}");
    long downloads = Long(h.Json("GET", "/api/skills").Json![0]!["downloads"]);
    h.Json("GET", "/api/skills/impala-query/versions/1?apply=1");
    Equal(downloads + 1, Long(h.Json("GET", "/api/skills").Json![0]!["downloads"]));
    Equal(404, h.Json("GET", "/api/skills/impala-query/versions/9").Status);
    Equal(404, h.Json("GET", "/api/skills/impala-query/versions/x").Status);
    Contains("v2", h.Call("GET", "/skills/impala-query", key: false, admin: Harness.Admin).Text);
    Equal(303, h.Form("/skills/impala-query/restore", [("version", "1")], h.Base).Status);
    var latest = h.Json("GET", "/api/skills/impala-query").Json!;
    Equal("3 - Impala로 조회한다.", $"{latest["version"]} {Str(latest["body"])}");
    Equal(1, h.Call("GET", "/skills/impala-query/versions/2/SKILL.md", key: false, admin: Harness.Admin).Text.Split("Impala로만").Length - 1);
    Equal(303, h.Form("/skills/impala-query/delete", [], h.Base).Status);
    Equal("[]", h.Json("GET", "/api/skills").Json!.ToJsonString());
    Equal(1, Directory.GetFileSystemEntries(Path.Combine(h.Root, "skills", ".trash")).Length);
});

Check("chunked json body", () =>
{
    using var h = new Harness();
    using var http = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Post, h.Base + "/api/skills")
    {
        Content = System.Net.Http.Json.JsonContent.Create(new { name = "chunked-skill", title = "t", description = "d", body = "b", author = "a" }),
    };
    request.Headers.Add("X-TerminalNotes-Key", Harness.Key);
    using var response = http.Send(request);
    True(request.Content.Headers.ContentLength is null || response.IsSuccessStatusCode, "sent");
    Equal(1L, Long(JsonNode.Parse(response.Content.ReadAsStringAsync().Result)!["version"]));
    Equal("chunked-skill", Str(h.Json("GET", "/api/skills").Json![0]!["name"]));
});

Check("skill from the earlier relay becomes version one", () =>
{
    using var h = new Harness();
    string skill = Path.Combine(h.Root, "skills", "dotnet-skill");
    Directory.CreateDirectory(skill);
    File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: dotnet-skill\ndescription: x\n---\n\n# 닷넷\n\n- 원래 내용\n");
    File.WriteAllText(Path.Combine(skill, "meta.json"), """{"Name": "dotnet-skill", "Title": "닷넷", "Description": "x", "Author": "a", "CreatedAt": "2026-10-22T00:00:00.1234567+00:00", "UpdatedAt": "2026-10-22T00:00:00.1234567+00:00", "Sha256": "AB"}""");
    Equal("dotnet-skill:1", string.Join(",", h.Json("GET", "/api/skills").Json!.AsArray().Select(item => $"{Str(item!["name"])}:{item["version"]}")));
    Equal("1", string.Join(",", h.Json("GET", "/api/skills/dotnet-skill/versions").Json!.AsArray().Select(item => item!["version"]!.ToJsonString())));
    Equal(2L, Long(h.Json("POST", "/api/skills", J("""{"name":"dotnet-skill","title":"닷넷","description":"x","body":"- 고친 내용","author":"a"}""")).Json!["version"]));
    Equal("- 원래 내용", Str(h.Json("GET", "/api/skills/dotnet-skill/versions/1").Json!["body"]));
});

void UseEquipment(Harness h, List<string?[]> rows, List<string> queries)
{
    h.Config.EquipmentDbHost = "db.example";
    (h.Config.EquipmentDbUser, h.Config.EquipmentDbPassword) = ("reader", "secret");
    (h.Config.EquipmentDbName, h.Config.EquipmentTable) = ("plant", "eqp_status");
    h.Config.EquipmentApiKey = "eq-key";
    h.Server.Equipment.Connect = (host, port, user, password, database) =>
    {
        queries.Add($"{host}|{port}|{user}|{password}|{database}");
        return new FakeDb(rows, queries);
    };
}

Check("equipment status", () =>
{
    using var h = new Harness();
    const string path = "/api/v1/equipment-status";
    Equal(404, h.Call("GET", path, key: false).Status, "off until EQUIPMENT_DB_HOST is set");
    var queries = new List<string>();
    UseEquipment(h, [["S1", "S1DPN", "DPN", "EQ-1", "down ", "1.5", "2026-01-01 10:00:00", "alarm"], ["S3", null, "ETH", "EQ-2", "RUN", "x", null, null]], queries);
    Equal(401, h.Call("GET", path).Status, "its own key");
    (string, string)[] key = [("X-EQStatus-API-Key", "eq-key")];
    var answer = h.Call("GET", path, headers: key, key: false);
    Equal(200, answer.Status);
    var items = answer.Json!["items"]!.AsArray();
    Equal("""{"line":"S1","sdwt":"S1DPN","process":"DPN","equipmentId":"EQ-1","status":"DOWN","hours":1.5,"downTime":"2026-01-01 10:00:00","comment":"alarm"}""",
        items[0]!.ToJsonString(Util.Compact));
    Equal("|0||", $"{Str(items[1]!["sdwt"])}|{items[1]!["hours"]}|{Str(items[1]!["downTime"])}|");
    Equal("db.example|3306|reader|secret|plant", queries[0]);
    Contains("FROM `eqp_status`", queries[1]);
    Contains("'hide'", queries[1]);
    Equal(304, h.Call("GET", path, headers: [.. key, ("If-None-Match", answer.Header("ETag"))], key: false).Status);
    Equal(2, queries.Count, "not asked again within the refresh time");
    Contains("설비 상태: 2대", Str(h.AdminJson("GET", "/admin/api/status").Json!["settings"]!.AsArray()[^1]));
});

Check("equipment status db failure", () =>
{
    using var h = new Harness();
    UseEquipment(h, [], []);
    h.Server.Equipment.Connect = (_, _, _, _, _) => throw new System.Net.Sockets.SocketException(10061);
    var answer = h.Call("GET", "/api/v1/equipment-status", headers: [("X-EQStatus-API-Key", "eq-key")], key: false);
    Equal(503, answer.Status);
    Contains("설비 상태 DB를 읽지 못했습니다", Str(answer.Json));
});

Check("equipment port answers only the equipment table", () =>
{
    using var h = new Harness();
    Equal(2004, h.Config.EquipmentPort);
    True(!h.Server.StartEquipmentPort(), "no EQUIPMENT_DB_HOST: not opened");
    UseEquipment(h, [["S1", "S1DPN", "DPN", "EQ-1", "RUN", "1", null, null]], []);
    h.Config.EquipmentPort = Harness.FreePort();
    True(h.Server.StartEquipmentPort(), "opened");
    using var http = new HttpClient();
    string root = $"http://127.0.0.1:{h.Config.EquipmentPort}";
    var answer = Harness.Send(http, root, "GET", "/api/v1/equipment-status", headers: [("X-EQStatus-API-Key", "eq-key")], key: false);
    Equal("200 EQ-1", $"{answer.Status} {Str(answer.Json!["items"]![0]!["equipmentId"])}");
    Equal(401, Harness.Send(http, root, "GET", "/api/v1/equipment-status", key: false).Status);
    Equal(200, Harness.Send(http, root, "GET", "/health", key: false).Status);
    Equal(404, Harness.Send(http, root, "GET", "/launcher/catalog", key: false).Status, "nothing else on this port");
    Equal(404, Harness.Send(http, root, "GET", "/admin/api/status", key: false).Status);
    Equal(200, h.Call("GET", "/api/v1/equipment-status", headers: [("X-EQStatus-API-Key", "eq-key")], key: false).Status, "the main port keeps answering it");
    True(!h.Server.StartEquipmentPort(), "port taken: logged, the server keeps running");
    h.Config.EquipmentPort = h.Config.Port;
    True(!h.Config.UsesEquipmentPort, "the same port as the server: nothing extra");
});

Check("settings move from an old relay.py and new keys are added", () =>
{
    const string old = "# ── 설정 시작 ──\r\nSETTINGS = {\r\n    \"RELAY_HOST\": \"10.0.0.1\",   # \"RELAY_PORT\": 9 in a comment\r\n    \"RELAY_PORT\": 2003,\r\n" +
                       "    \"RELAY_ADMIN_PASSWORD\": \"a\\\"b\\\\c\",\r\n    \"RELAY_GITHUB_API\": \"https://ghe.example/api/v3\",\r\n}\r\n# ── 설정 끝 ──";
    var values = Settings.FromRelayScript(old);
    Equal("10.0.0.1|2003|a\"b\\c|https://ghe.example/api/v3", $"{values["RELAY_HOST"]}|{values["RELAY_PORT"]}|{values["RELAY_ADMIN_PASSWORD"]}|{values["RELAY_GITHUB_API"]}");
    Throws<ConfigError>(() => Settings.FromRelayScript("no settings here"), "a file without SETTINGS is refused");
    string folder = Harness.NewFolder("i-api settings");
    string file = Path.Combine(folder, "settings.txt");
    File.WriteAllText(file, Settings.Compose(new Dictionary<string, string> { ["RELAY_HOST"] = "10.0.0.1", ["RELAY_ADMIN_PASSWORD"] = " spaced ", ["EXTRA_KEY"] = "x" }));
    var read = Settings.Read(file);
    Equal("10.0.0.1| spaced |x|3306", $"{read["RELAY_HOST"]}|{read["RELAY_ADMIN_PASSWORD"]}|{read["EXTRA_KEY"]}|{read["EQUIPMENT_DB_PORT"]}");
    File.WriteAllText(file, "# 손으로 줄인 파일\r\nRELAY_HOST=10.0.0.2\r\n");
    var added = Settings.EnsureComplete(file);
    True(added.Contains("EQUIPMENT_STATUS_TABLE") && !added.Contains("RELAY_HOST"), string.Join(",", added));
    Equal("10.0.0.2", Settings.Read(file)["RELAY_HOST"], "a value written by hand stays");
    Equal(0, Settings.EnsureComplete(file).Count, "nothing missing: unchanged");
    var config = new Config(new Options { SettingsFile = file }, null, Harness.NoEnvironment);
    Equal("10.0.0.2", config.Host, "the settings file is read");
    Directory.Delete(folder, true);
});

Check("announcements", () =>
{
    using var h = new Harness();
    var board = h.Json("GET", "/api/announcements").Json!;
    Equal("False missing", $"{board["exists"]!.GetValue<bool>()} {Str(board["revision"])}");
    var save = J("""{"text":"## 2026-10-22\n새 공지","expectedRevision":"missing","expectedExists":false,"author":"someone"}""").AsObject();
    Equal(403, h.Json("PUT", "/api/announcements", save).Status);
    var saved = h.Json("PUT", "/api/announcements", Merge(save, """{"author":"DOMAIN\\editor_two"}"""));
    Equal("200 True ## 2026-10-22\n새 공지", $"{saved.Status} {saved.Json!["exists"]!.GetValue<bool>()} {Str(saved.Json["text"])}");
    var fresh = h.Json("GET", "/api/announcements");
    Equal(Util.Sha256Hex(Encoding.UTF8.GetBytes("## 2026-10-22\n새 공지")), Str(fresh.Json!["revision"]));
    Equal(304, h.Call("GET", "/api/announcements", headers: [("If-None-Match", fresh.Header("ETag"))]).Status);
    var stale = Merge(save, """{"author":"editor_one","expectedExists":true,"expectedRevision":"0000000000000000000000000000000000000000000000000000000000000000","text":"덮어쓰기"}""");
    Equal(409, h.Json("PUT", "/api/announcements", stale).Status);
    var good = Merge(stale, $$"""{"expectedRevision":"{{Str(fresh.Json["revision"])}}","text":"## 2026-10-23\n다음 공지"}""");
    Equal(200, h.Json("PUT", "/api/announcements", good).Status);
    Equal(1, h.Server.Announcements.History().Count);
    string revision = Str(h.Server.Announcements.Read()["revision"]);
    Equal(303, h.Form("/announcements", [("text", "관리자 공지"), ("revision", revision), ("exists", "1")], h.Base).Status);
    Equal("관리자 공지", Str(h.Json("GET", "/api/announcements").Json!["text"]));
    Contains("관리자 공지", h.Call("GET", "/announcements", key: false, admin: Harness.Admin).Text);
});

Check("an announcement reaches other PCs", () =>
{
    using var h = new Harness();
    var save = J("""{"text":"## 2026-10-22\n모두 보세요","expectedRevision":"missing","expectedExists":false,"author":"editor_one"}""").AsObject();
    var saved = h.Json("PUT", "/api/announcements", save);
    Equal("200 1", $"{saved.Status} {saved.Header("X-TerminalNotes-Seen-By")}", "the editor's PC has it");
    var other = h.CallFrom("127.0.0.2", "GET", "/api/announcements", [("User-Agent", "TerminalNotes/1.0.519")]);
    Equal("200 ## 2026-10-22\n모두 보세요 2", $"{other.Status} {Str(other.Json!["text"])} {other.Header("X-TerminalNotes-Seen-By")}");
    var again = h.CallFrom("127.0.0.2", "GET", "/api/announcements", [("If-None-Match", other.Header("ETag"))]);
    Equal("304 2", $"{again.Status} {again.Header("X-TerminalNotes-Seen-By")}", "an unchanged board still reports the count");
    h.CallFrom("127.0.0.3", "GET", "/api/skills", key: "wrong");
    string page = h.Call("GET", "/status", key: false, admin: Harness.Admin).Text;
    foreach (string expected in new[] { "127.0.0.2", "1.0.519", "1.0.518 이하", "127.0.0.3", "키 틀림(거절)", "최신", "방화벽" }) Contains(expected, page);
    var newer = Merge(save, $$"""{"expectedExists":true,"expectedRevision":"{{Str(saved.Json!["revision"])}}","text":"## 2026-10-23\n다음 공지"}""");
    Equal("1", h.Json("PUT", "/api/announcements", newer).Header("X-TerminalNotes-Seen-By"), "PCs with the old board no longer count");
    Contains("이전 공지", h.Call("GET", "/status", key: false, admin: Harness.Admin).Text);
});

Check("routine polls stay out of the log", () =>
{
    var lines = new List<string>();
    void Capture(string line) { lock (lines) lines.Add(line); }
    using var h = new Harness();
    IApi.Log.Written += Capture;
    try
    {
        for (int index = 0; index < 3; index++) h.Call("GET", "/api/announcements");
        h.Call("GET", "/api/skills");
        for (int wait = 0; wait < 100 && !lines.ToList().Any(line => line.Contains("GET /api/skills 200")); wait++) Thread.Sleep(20);
    }
    finally { IApi.Log.Written -= Capture; }
    var seen = lines.ToList();
    Equal(1, seen.Count(line => line.Contains("새 PC 접속: 127.0.0.1")));
    True(seen.Any(line => line.Contains("GET /api/skills 200")), "a request line");
    True(!seen.Any(line => line.Contains("/api/announcements")), "no announcement polls");
});

Check("firewall rule for the port", () =>
{
    var commands = new List<string>();
    var answers = new Queue<int?>([1, 1, 0, 0]);
    var originalRun = Setup.RunHidden;
    var originalAdmin = Setup.IsAdministrator;
    var output = new StringWriter();
    var console = Console.Out;
    try
    {
        Setup.RunHidden = (program, arguments, _) => { commands.Add(program + " " + string.Join(" ", arguments)); return answers.Dequeue(); };
        Setup.IsAdministrator = () => false;
        Console.SetOut(output);
        True(Setup.AllowFirewall(2003), "allowed");
        Contains("관리자 승인", output.ToString());
        Equal("netsh advfirewall firewall show rule name=i-api-2003", commands[0]);
        Equal("netsh advfirewall firewall show rule name=TerminalNotesRelay-2003", commands[1]);
        foreach (string expected in new[] { "-Verb RunAs", "name=i-api-2003", "dir=in", "action=allow", "protocol=TCP", "localport=2003", "profile=any" }) Contains(expected, commands[2]);
        commands.Clear();
        answers = new Queue<int?>([1, 1, 0, 0]);
        Setup.IsAdministrator = () => true;
        True(Setup.AllowFirewall(2003), "as administrator");
        True(commands[2].StartsWith("netsh advfirewall firewall add rule"), commands[2]);
        answers = new Queue<int?>([1, 1, 1223, 1, 1]);
        Setup.IsAdministrator = () => false;
        output.GetStringBuilder().Clear();
        True(!Setup.AllowFirewall(2003), "nobody answered");
        Contains("'예'를 누르지 않았습니다", output.ToString());
        Contains("netsh advfirewall firewall add rule name=i-api-2003", output.ToString());
        commands.Clear();
        answers = new Queue<int?>([1, 0]);
        output.GetStringBuilder().Clear();
        True(Setup.AllowFirewall(2003), "the rule before 2.23 still counts");
        Equal(2, commands.Count);
        Contains("TerminalNotesRelay-2003", output.ToString());
    }
    finally
    {
        Console.SetOut(console);
        Setup.RunHidden = originalRun;
        Setup.IsAdministrator = originalAdmin;
    }
    using var h = new Harness();
    Contains("이 PC 안에서만", Setup.FirewallText(h.Config), "the checks bind 127.0.0.1");
});

Check("admin api for the admin menu", () =>
{
    using var h = new Harness();
    Equal(401, h.Call("GET", "/admin/api/status", key: false).Status);
    var info = h.AdminJson("GET", "/admin/api/status").Json!;
    Equal($"{IApi.Program.Version} 0 0 0", $"{Str(info["version"])} {info["counts"]!["skills"]} {info["counts"]!["plugins"]} {info["counts"]!["apps"]}");
    True(!h.Server.Functions.Any(function => function.Pattern.Contains("report", StringComparison.OrdinalIgnoreCase)), "no problem reports any more");
    True(info["settings"] is JsonArray && Str(info["runtime"]).StartsWith(".NET"), "settings and runtime");
    Equal(200, h.Call("GET", "/api/announcements").Status);
    var clients = h.AdminJson("GET", "/admin/api/status").Json!["clients"]!.AsArray();
    True(clients.Count > 0 && Str(clients[0]!["address"]) == "127.0.0.1" && clients[0]!["features"]!.AsArray().Any(feature => Str(feature) == "공지"), clients.ToJsonString());
    foreach (string body in new[] { "첫 버전", "둘째 버전" })
        Equal(200, h.Json("POST", "/api/skills", J($$"""{"name":"admin-window","title":"관리 창","description":"d","body":"{{body}}","author":"editor_one"}"""), User("editor_one")).Status);
    Equal("admin-window", Str(h.AdminJson("GET", "/admin/api/skills").Json![0]!["name"]));
    var detail = h.AdminJson("GET", "/admin/api/skills/admin-window").Json!;
    Equal("2 [2,1]", $"{detail["skill"]!["latestVersion"]} [{string.Join(",", detail["versions"]!.AsArray().Select(item => item!["version"]))}]");
    Contains("첫 버전", Str(h.AdminJson("GET", "/admin/api/skills/admin-window/versions/1").Json!["content"]));
    Equal(3L, Long(h.AdminJson("POST", "/admin/api/skills/admin-window/restore", J("""{"version":1}""")).Json!["version"]));
    Equal(400, h.AdminJson("POST", "/admin/api/skills/admin-window/restore", J("""{"version":"1"}""")).Status);
    Equal(200, h.AdminJson("DELETE", "/admin/api/skills/admin-window").Status);
    Equal("[]", h.AdminJson("GET", "/admin/api/skills").Json!.ToJsonString());
    var board = h.AdminJson("GET", "/admin/api/announcements").Json!;
    var saved = h.AdminJson("PUT", "/admin/api/announcements", new JsonObject { ["text"] = "점검 안내\r\n내일", ["revision"] = board["revision"]!.DeepClone(), ["exists"] = board["exists"]!.DeepClone() });
    Equal("200 점검 안내\n내일 관리자", $"{saved.Status} {Str(saved.Json!["text"])} {Str(saved.Json["updatedBy"])}");
    Equal(409, h.AdminJson("PUT", "/admin/api/announcements", new JsonObject { ["text"] = "x", ["revision"] = board["revision"]!.DeepClone(), ["exists"] = board["exists"]!.DeepClone() }).Status);
    Equal(404, h.AdminJson("GET", "/admin/api/nothing").Status);
});

Check("admin lockout", () =>
{
    using var h = new Harness();
    for (int index = 0; index < 10; index++) Equal(401, h.Call("GET", "/status", key: false, admin: "wrong").Status);
    Equal(429, h.Call("GET", "/status", key: false, admin: Harness.Admin).Status);
});

Check("times keep their fraction of a second", () =>
{
    foreach (var (value, micro) in new[] { ("2026-10-20T14:31:37.2427995+00:00", 242799), ("2026-10-20T14:31:37.24279+00:00", 242790), ("2026-10-20T14:31:37.5Z", 500000), ("2026-10-20T14:31:37+09:00", 0) })
        Equal(micro, (int)(Util.ParseTime(value).Ticks % TimeSpan.TicksPerSecond / 10), value);
});

Check("skill version chosen by its author", () =>
{
    using var h = new Harness();
    h.Config.UploadsPerHour = 100;
    Reply Share(JsonNode? version, string body = "- Trino로 조회한다.", string author = "alice")
    {
        var upload = new JsonObject { ["name"] = "trino-guide-alice", ["title"] = "Trino 가이드", ["description"] = "Trino 가이드", ["body"] = body, ["author"] = author };
        if (version is not null) upload["version"] = version;
        return h.Json("POST", "/api/skills", upload);
    }
    Equal(1L, Long(Share(null).Json!["version"]));
    Equal(5L, Long(Share(5, "- Trino로만 조회한다.").Json!["version"]));
    var lower = Share(3, "- 검증한다.");
    Equal(400, lower.Status);
    Contains("v5", Str(lower.Json));
    Equal(400, Share("x", "- 검증한다.").Status);
    Equal(400, Share(true, "- 검증한다.").Status);
    var unchanged = Share(9, "- Trino로만 조회한다.").Json!;
    Equal("5 False", $"{unchanged["version"]} {unchanged["changed"]!.GetValue<bool>()}");
    Equal(7L, Long(Share("7", "- 검증한다.").Json!["version"]));
    Equal(8L, Long(Share(null, "- 결과를 남긴다.").Json!["version"]));
    Equal(409, Share(20, "- 남의 것", "bob").Status);
    Equal("8,7,5,1", string.Join(",", h.Json("GET", "/api/skills/trino-guide-alice/versions").Json!.AsArray().Select(item => item!["version"])));
});

Check("service ip from settings", () =>
{
    Config Make(params string[] lines) => Harness.Make(sandbox, ["RELAY_PORT=5099", $"RELAY_INGEST_KEY={Harness.Key}", $"RELAY_ADMIN_PASSWORD={Harness.Admin}", .. lines]);
    Equal("127.0.0.1", Make("RELAY_HOST=127.0.0.1").Bind);
    Equal("127.0.0.1", Make("RELAY_HOST=localhost").Bind);
    Equal("0.0.0.0", Make("RELAY_HOST=relay.example.com").Bind);
    Equal("0.0.0.0", Make().Bind);
    Equal("0.0.0.0", Make("RELAY_HOST=127.0.0.1", "RELAY_BIND=0.0.0.0").Bind);
    Make("RELAY_HOST=127.0.0.1").Validate();
    Contains("이 IP로 받습니다", Make("RELAY_HOST=127.0.0.1").AddressText());
    try
    {
        Make("RELAY_HOST=192.0.2.10").Validate();
        throw new CheckFailed("192.0.2.10 is never this PC's");
    }
    catch (ConfigError refused)
    {
        Contains("이 PC의 IP가 아닙니다", refused.Message);
        Contains("RELAY_HOST", refused.Message);
    }
    var everywhere = Make("RELAY_HOST=192.0.2.10", "RELAY_BIND=0.0.0.0");
    everywhere.Validate();
    Contains("이 PC의 IP가 아닙니다", everywhere.AddressText());
    Contains("RELAY_HOST가 비어 있습니다", Make().AddressText());
    Throws<ConfigError>(() => Make("no-equals-sign"), "--set needs key=value");
    var portless = Harness.Make(sandbox, []);
    Equal(0, portless.Port, "no default port");
    try
    {
        portless.Validate();
        throw new CheckFailed("a server without a port must not start");
    }
    catch (ConfigError refused) { Contains("포트가 정해지지 않았습니다", refused.Message); }
    string file = Path.Combine(sandbox, "port-settings.txt");
    Settings.EnsureComplete(file);
    Equal("", Settings.Read(file)["RELAY_PORT"], "a new settings file has no port");
    Settings.SetValue(file, "RELAY_PORT", "2005");
    Settings.SetValue(file, "RELAY_PORT", "2006");
    Equal("2006", Settings.Read(file)["RELAY_PORT"], "the chosen port is written once, in place");
    Equal(1, File.ReadAllLines(file).Count(line => line.StartsWith("RELAY_PORT=")));
    var bare = Harness.Make(sandbox, ["RELAY_PORT=5099"]);
    bare.Validate();
    Equal("0.0.0.0||0", $"{bare.Bind}|{bare.IngestKey}{bare.AdminPassword}|{bare.Editors.Count}");
});

Check("environment values replace settings and --set still wins", () =>
{
    var environment = new System.Collections.Hashtable { ["RELAY_PORT"] = "5099", ["RELAY_ADMIN_PASSWORD"] = "", ["RUNNERS_ROOT"] = "C:/runners", ["UNRELATED"] = "1", ["RELAY_GITHUB_API"] = "https://github.example.com/api/v3" };
    var values = Settings.FromEnvironment(environment);
    Equal("https://github.example.com/api/v3|5099|", $"{values["RELAY_GITHUB_API"]}|{values["RELAY_PORT"]}|{values["RELAY_ADMIN_PASSWORD"]}");
    True(!values.ContainsKey("RUNNERS_ROOT") && !values.ContainsKey("UNRELATED"), "this PC's runner settings stay in the file");
    Equal(5099, new Config(new Options { SettingsFile = Path.Combine(sandbox, "none.txt") }, new Dictionary<string, string>(), environment).Port);
    var options = new Options { SettingsFile = Path.Combine(sandbox, "none.txt") };
    options.Sets.Add("RELAY_PORT=5100");
    Equal(5100, new Config(options, new Dictionary<string, string>(), environment).Port, "--set wins");
});

Check("no app key takes any app", () =>
{
    string root = Harness.NewFolder("i-api nokey");
    var config = Harness.Make(root, [$"RELAY_PORT={Harness.FreePort()}", $"RELAY_ADMIN_PASSWORD={Harness.Admin}", $"RELAY_DATA_ROOT={root}"], bind: "127.0.0.1");
    config.Validate();
    using var server = new RelayServer(config);
    server.Start();
    using var http = new HttpClient();
    var skills = Harness.Send(http, $"http://127.0.0.1:{server.Port}", "GET", "/api/skills", key: false);
    Equal("200 []", $"{skills.Status} {skills.Text}");
    Equal(401, Harness.Send(http, $"http://127.0.0.1:{server.Port}", "GET", "/status", key: false).Status, "the admin pages still ask for the password");
    True(new Announcements(Path.Combine(root, "bare-announcements"), []).IsEditor("anyone"), "no editors: the app decides");
});

Check("private skill is for its author only", () =>
{
    using var h = new Harness();
    var upload = J("""{"name":"mine-alice","title":"내 방식","description":"d","body":"- 나만 쓴다.","author":"alice","private":true,"category":"데이터"}""").AsObject();
    Equal(200, h.Json("POST", "/api/skills", upload, User("alice")).Status);
    var mine = h.Json("GET", "/api/skills", headers: User("DOMAIN\\Alice")).Json!.AsArray();
    Equal("mine-alice True 데이터", $"{Str(mine[0]!["name"])} {mine[0]!["private"]!.GetValue<bool>()} {Str(mine[0]!["category"])}");
    Equal("[]", h.Json("GET", "/api/skills", headers: User("bob")).Json!.ToJsonString());
    Equal("[]", h.Json("GET", "/api/skills").Json!.ToJsonString(), "an app that sends no account sees no private skill");
    foreach (string path in new[] { "/api/skills/mine-alice", "/api/skills/mine-alice/versions", "/api/skills/mine-alice/versions/1" })
    {
        Equal(404, h.Json("GET", path, headers: User("bob")).Status, path);
        Equal(200, h.Json("GET", path, headers: User("alice")).Status, path);
    }
    h.Json("POST", "/api/skills", Merge(upload, """{"body":"- 나만 쓴다. 표로.","private":null,"category":null}"""));
    Equal("2", string.Join(",", h.Server.Skills.All().Where(skill => skill["private"]!.GetValue<bool>() && Str(skill["category"]) == "데이터").Select(skill => skill["version"])));
    h.Json("POST", "/api/skills", Merge(upload, """{"body":"- 나만 쓴다. 표로.","private":false}"""));
    Equal("mine-alice", Str(h.Json("GET", "/api/skills", headers: User("bob")).Json![0]!["name"]));
});

Check("skill origin stays with the skill", () =>
{
    using var h = new Harness();
    var upload = J("""{"name":"from-bob","title":"받은 스킬","description":"d","body":"- 따라 한다.","author":"alice","origin":" https://github.com/bob/skills\n"}""").AsObject();
    Equal(200, h.Json("POST", "/api/skills", upload).Status);
    string Latest() { var skill = h.Json("GET", "/api/skills").Json![0]!; return $"{Str(skill["origin"])} {skill["version"]}"; }
    Equal("https://github.com/bob/skills 1", Latest());
    Equal("https://github.com/bob/skills", Str(h.Json("GET", "/api/skills/from-bob").Json!["origin"]));
    h.Json("POST", "/api/skills", Merge(upload, """{"origin":"bob"}"""));
    Equal("bob 1", Latest(), "changing it alone makes no new version");
    var changed = Merge(upload, """{"body":"- 따라 하고 고친다.","origin":null}""");
    h.Json("POST", "/api/skills", changed);
    Equal("bob 2", Latest());
    Equal(3, h.Server.Skills.Restore("from-bob", 1));
    Equal("bob 3", Latest());
    Equal(400, h.Json("POST", "/api/skills", Merge(changed, $$"""{"origin":"{{new string('x', 301)}}"}""")).Status);
    h.Json("POST", "/api/skills", Merge(upload, """{"origin":""}"""));
    Equal(" 3", Latest(), "\"\" removes it");
});

Check("the author decides who sees a skill", () =>
{
    using var h = new Harness();
    var sent = new List<string>();
    h.Server.Notifier.Sent = sent.Add;
    var upload = J("""{"name":"note-alice","title":"메모","description":"d","body":"- 짧게.","author":"alice","private":true}""").AsObject();
    Equal(200, h.Json("POST", "/api/skills", upload, User("alice")).Status);
    Equal(0, sent.Count, "nothing goes out about a private skill");
    var names = J("""{"names":["note-alice","no-such-skill",7,"BAD NAME"]}""");
    Equal("""{"withdrawn":["note-alice"]}""", h.Json("POST", "/api/skills/withdrawn", names, User("bob")).Json!.ToJsonString());
    Equal("""{"withdrawn":["note-alice"]}""", h.Json("POST", "/api/skills/withdrawn", names, User("editor_one")).Json!.ToJsonString());
    Equal("""{"withdrawn":[]}""", h.Json("POST", "/api/skills/withdrawn", names, User("alice")).Json!.ToJsonString());
    Equal(400, h.Json("POST", "/api/skills/withdrawn", J("""{"names":"x"}"""), User("bob")).Status);
    Equal(400, h.Json("POST", "/api/skills/withdrawn", new JsonObject { ["names"] = new JsonArray(Enumerable.Repeat<JsonNode?>("a", 501).Select(item => item?.DeepClone()).ToArray()) }, User("bob")).Status);
    Equal(404, h.Json("PUT", "/api/skills/note-alice/private", J("""{"private":false}"""), User("bob")).Status);
    Equal(400, h.Json("PUT", "/api/skills/note-alice/private", J("""{"private":"no"}"""), User("alice")).Status);
    Equal("""{"name":"note-alice","private":false}""", h.Json("PUT", "/api/skills/note-alice/private", J("""{"private":false}"""), User("alice")).Json!.ToJsonString());
    var listed = h.Json("GET", "/api/skills", headers: User("bob")).Json![0]!;
    Equal("note-alice 1 1 False", $"{Str(listed["name"])} {listed["version"]} {listed["versionCount"]} {listed["private"]!.GetValue<bool>()}");
    Equal("""{"withdrawn":[]}""", h.Json("POST", "/api/skills/withdrawn", names, User("bob")).Json!.ToJsonString());
    Equal(403, h.Json("PUT", "/api/skills/note-alice/private", J("""{"private":true}"""), User("editor_one")).Status);
    Equal(403, h.Json("PUT", "/api/skills/note-alice/private", J("""{"private":true}"""), User("bob")).Status);
    var edit = Merge(upload, """{"body":"- 표로.","author":"editor_one","edit":true}""");
    Equal(403, h.Json("POST", "/api/skills", edit, User("editor_one")).Status, "a master's new version may not hide it");
    Equal(2L, Long(h.Json("POST", "/api/skills", Merge(edit, """{"private":false}"""), User("editor_one")).Json!["version"]));
    Equal(1, sent.Count, "a public skill's new version is announced");
    Equal(403, h.Json("POST", "/api/skills", Merge(edit, """{"author":"bob","private":null}"""), User("bob")).Status);
    Equal(200, h.Json("PUT", "/api/skills/note-alice/private", J("""{"private":true}"""), User("alice")).Status);
    Equal("[]", h.Json("GET", "/api/skills", headers: User("editor_one")).Json!.ToJsonString());
    Equal(404, h.Json("DELETE", "/api/skills/note-alice", headers: User("editor_one")).Status);
    Equal("note-alice", Str(h.Server.Skills.All()[0]["name"]));
    Equal(200, h.Json("DELETE", "/api/skills/note-alice", headers: User("alice")).Status);
    Equal("""{"withdrawn":[]}""", h.Json("POST", "/api/skills/withdrawn", names, User("bob")).Json!.ToJsonString(), "a deleted skill is not withdrawn");
});

Check("data outlives the server folder", () =>
{
    string code = Path.Combine(sandbox, "code"), data = Path.Combine(sandbox, "data-outlives");
    Directory.CreateDirectory(Path.Combine(code, "skills", "old-skill"));
    File.WriteAllText(Path.Combine(code, "skills", "old-skill", "SKILL.md"), "old");
    Directory.CreateDirectory(Path.Combine(code, "announcements"));
    string saved = Config.ProgramFolder;
    Config.ProgramFolder = code;
    try
    {
        var config = Harness.Make(sandbox, [$"RELAY_DATA_ROOT={data}"]);
        Equal($"{Path.Combine(data, "skills")}|{Path.Combine(data, "relay.log")}", $"{config.SkillsDir}|{config.LogFile}");
        DataFolders.AdoptOldData(config);
        Equal("old", File.ReadAllText(Path.Combine(config.SkillsDir, "old-skill", "SKILL.md")));
        True(File.Exists(Path.Combine(code, "skills", "old-skill", "SKILL.md")), "copied, never moved");
        True(!Directory.Exists(Path.Combine(data, "skills.copying")), "no staging left");
        True(!Directory.Exists(config.AnnouncementsDir), "an empty old folder is nothing to copy");
        Directory.CreateDirectory(Path.Combine(config.SkillsDir, "new-skill"));
        Directory.CreateDirectory(Path.Combine(code, "skills", "another"));
        DataFolders.AdoptOldData(config);
        Equal("new-skill,old-skill", string.Join(",", Directory.GetDirectories(config.SkillsDir).Select(Path.GetFileName).Order()));
        var own = Harness.Make(sandbox, [$"RELAY_DATA_ROOT={data}", $"RELAY_SKILLS_DIR={Path.Combine(sandbox, "own")}"]);
        DataFolders.AdoptOldData(own);
        True(!Directory.Exists(Path.Combine(sandbox, "own")), "a folder of its own is left alone");
    }
    finally { Config.ProgramFolder = saved; }
});

Check("the 2.10-2.11 store is copied forward, never moved or overwritten", () =>
{
    string legacy = Path.Combine(sandbox, "Local", "TerminalNotesRelay"), data = Path.Combine(sandbox, "data-legacy");
    Directory.CreateDirectory(Path.Combine(legacy, "skills", "kept-skill"));
    File.WriteAllText(Path.Combine(legacy, "skills", "kept-skill", "SKILL.md"), "kept");
    Directory.CreateDirectory(Path.Combine(legacy, "announcements"));
    File.WriteAllText(Path.Combine(legacy, "announcements", "current.json"), "{}");
    var config = Harness.Make(sandbox, [$"RELAY_DATA_ROOT={data}"]);
    var saved = Config.LegacyDataRoots;
    Config.LegacyDataRoots = () => [legacy];
    try
    {
        Equal(2, DataFolders.AdoptOldData(config).Count);
        Equal("kept", File.ReadAllText(Path.Combine(data, "skills", "kept-skill", "SKILL.md")));
        True(File.Exists(Path.Combine(legacy, "skills", "kept-skill", "SKILL.md")), "the old folder stays");
        True(File.Exists(Path.Combine(legacy, DataFolders.MovedNote)), "the old folder says where the data went");
        Directory.CreateDirectory(Path.Combine(legacy, "skills", "stale"));
        Equal(0, DataFolders.AdoptOldData(config).Count, "a data folder with content is never overwritten");
    }
    finally { Config.LegacyDataRoots = saved; }
    string savedProgram = Config.ProgramFolder;
    Config.ProgramFolder = data;
    True(config.DataBesideCode && config.Summary().Any(line => line.Contains("주의")), "a store inside the server folder is called out");
    Config.ProgramFolder = savedProgram;
    True(!config.DataBesideCode && !config.Summary().Any(line => line.Contains("주의")), "apart: no warning");
});

Check("the renamed data folder moves once", () =>
{
    string machine = Path.Combine(sandbox, "ProgramData-rename");
    string old = Path.Combine(machine, Config.RenamedDataFolder), fresh = Path.Combine(machine, Config.DataFolder);
    Directory.CreateDirectory(Path.Combine(old, "launcher"));
    File.WriteAllText(Path.Combine(old, "launcher", "catalog.json"), """{"apps": []}""");
    Directory.CreateDirectory(Path.Combine(old, "usage"));
    Directory.CreateDirectory(Path.Combine(fresh, "launcher"));
    string legacy = Path.Combine(sandbox, "Local-rename", "TerminalNotesRelay");
    Directory.CreateDirectory(Path.Combine(legacy, "skills", "stale"));
    File.WriteAllText(Path.Combine(legacy, "skills", "stale", "SKILL.md"), "2.11");
    var savedRoot = Config.DefaultDataRoot;
    var savedLegacy = Config.LegacyDataRoots;
    Config.DefaultDataRoot = () => fresh;
    Config.LegacyDataRoots = () => [legacy];
    try
    {
        var config = new Config(new Options { SettingsFile = Path.Combine(sandbox, "none.txt") }, new Dictionary<string, string>(), Harness.NoEnvironment);
        Equal(Path.GetFullPath(fresh), config.DataRoot);
        Equal(0, DataFolders.AdoptOldData(config).Count, "no older copy while the real store waits to be renamed");
        Contains("옮겼습니다", DataFolders.MoveRenamedData(config) ?? "");
        Equal("""{"apps": []}""", File.ReadAllText(Path.Combine(fresh, "launcher", "catalog.json")));
        True(Directory.Exists(Path.Combine(fresh, "usage")) && !Directory.Exists(old), "renamed, not copied");
        Equal<string?>(null, DataFolders.MoveRenamedData(config), "only once");
        Directory.CreateDirectory(Path.Combine(old, "skills"));
        Equal<string?>(null, DataFolders.MoveRenamedData(Harness.Make(sandbox, [$"RELAY_DATA_ROOT={Path.Combine(sandbox, "own-rename")}"])), "a folder of its own is never touched");
        Equal<string?>(null, DataFolders.MoveRenamedData(config), "an old folder never overwrites a filled new one");
        True(Directory.Exists(Path.Combine(old, "skills")), "left alone");
    }
    finally
    {
        Config.DefaultDataRoot = savedRoot;
        Config.LegacyDataRoots = savedLegacy;
    }
});

Check("the start at login replaces the old names", () =>
{
    string startup = Setup.StartupFolder();
    Directory.CreateDirectory(startup);
    File.WriteAllText(Path.Combine(startup, "Terminal Notes Relay.vbs"), "old", Encoding.Unicode);
    var console = Console.Out;
    Console.SetOut(TextWriter.Null);
    try
    {
        Setup.InstallStartup(["RELAY_PORT=5081"]);
        Equal("i-api.vbs", string.Join(",", Directory.GetFiles(startup).Select(Path.GetFileName)));
        string script = File.ReadAllText(Path.Combine(startup, "i-api.vbs"), Encoding.Unicode);
        Contains($"shell.Run \"\"\"{IApi.Program.ExecutablePath}\"\" --set \"\"RELAY_PORT=5081\"\"\", 0, False", script);
        Setup.UninstallStartup(onlyThis: true);
        True(!File.Exists(Path.Combine(startup, "i-api.vbs")), "removed");
        File.WriteAllText(Path.Combine(startup, "i-api.vbs"), "Set shell = CreateObject(\"WScript.Shell\")\r\nshell.Run \"\"\"C:\\Elsewhere\\i-api.exe\"\"\", 0, False\r\n", Encoding.Unicode);
        Setup.UninstallStartup(onlyThis: true);
        True(File.Exists(Path.Combine(startup, "i-api.vbs")), "another folder's server keeps its start at login");
    }
    finally { Console.SetOut(console); }
});

Check("store backups and restore", () =>
{
    string data = Path.Combine(sandbox, "data-backups");
    var config = Harness.Make(sandbox, [$"RELAY_DATA_ROOT={data}", "RELAY_BACKUP_KEEP=2"]);
    Equal(Path.Combine(data, "backups"), config.BackupDir);
    Equal<string?>(null, DataFolders.BackupStore(config), "an empty store is never backed up");
    string skill = Path.Combine(config.SkillsDir, "one", "SKILL.md");
    Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
    File.WriteAllText(skill, "v1");
    Directory.CreateDirectory(config.AnnouncementsDir);
    File.WriteAllText(Path.Combine(config.AnnouncementsDir, "current.json"), """{"text": "공지"}""");
    var saved = Util.Clock;
    int second = 0;
    Util.Clock = () => new DateTimeOffset(2026, 11, 5, 1, 0, second++, TimeSpan.Zero);
    string? first;
    try
    {
        first = DataFolders.BackupStore(config);
        True(first is not null, "made");
        Equal<string?>(null, DataFolders.BackupStore(config), "nothing changed: no new backup");
        foreach (var (version, seconds) in new[] { ("v2", 10), ("v3", 20) })
        {
            File.WriteAllText(skill, version);
            File.SetLastWriteTimeUtc(skill, DateTime.UtcNow.AddSeconds(seconds));
            True(DataFolders.BackupStore(config) is not null, version);
        }
    }
    finally { Util.Clock = saved; }
    var backups = DataFolders.StoreBackups(config);
    Equal(2, backups.Count, "only RELAY_BACKUP_KEEP backups are kept");
    True(!backups.Contains(first!), "the oldest goes first");
    Directory.Delete(config.SkillsDir, true);
    var lines = DataFolders.RestoreBackup(config);
    Equal("v3", File.ReadAllText(skill));
    True(lines.Any(line => line.StartsWith("skills:") && line.Contains("되살렸습니다")), string.Join(" / ", lines));
    True(lines.Any(line => line.StartsWith("announcements:") && line.Contains("그대로 두었습니다")), string.Join(" / ", lines));
});

Check("skill store admin deletes from the app; author and admins manage", () =>
{
    using var h = new Harness();
    h.Json("POST", "/api/skills", J("""{"name":"gone","title":"지울 스킬","description":"d","body":"- x","author":"alice"}"""));
    h.Config.SkillAdmins = ["test_08"];
    Equal(403, h.Json("DELETE", "/api/skills/gone", headers: User("bob")).Status);
    Equal(200, h.Json("DELETE", "/api/skills/gone", headers: User("DOMAIN\\test_08")).Status);
    Equal("[]", h.Json("GET", "/api/skills").Json!.ToJsonString());
    True(Directory.GetFileSystemEntries(Path.Combine(h.Root, "skills", ".trash")).Length > 0, "kept 30 days in .trash");
    Equal(404, h.Json("DELETE", "/api/skills/gone", headers: User("test_08")).Status);
    h.Config.SkillAdmins = ["test_07"];
    var upload = J("""{"name":"notes","title":"메모","description":"d","body":"- x","author":"alice"}""").AsObject();
    Equal(200, h.Json("POST", "/api/skills", upload, User("alice")).Status);
    var other = Merge(upload, """{"body":"- y","author":"bob"}""");
    Equal(409, h.Json("POST", "/api/skills", other, User("bob")).Status);
    Equal(403, h.Json("POST", "/api/skills", Merge(other, """{"edit":true}"""), User("bob")).Status);
    var admin = h.Json("POST", "/api/skills", Merge(upload, """{"body":"- z","author":"test_07","edit":true}"""), User("DOMAIN\\test_07"));
    Equal("200 2", $"{admin.Status} {admin.Json!["version"]}");
    var skill = h.Json("GET", "/api/skills").Json![0]!;
    Equal("alice 2", $"{Str(skill["author"])} {skill["version"]}");
    Equal("test_07,alice", string.Join(",", h.Json("GET", "/api/skills/notes/versions").Json!.AsArray().Select(item => Str(item!["author"]))));
    Equal(403, h.Json("DELETE", "/api/skills/notes", headers: User("bob")).Status);
    Equal(200, h.Json("DELETE", "/api/skills/notes", headers: User("alice")).Status);
});

Check("plugin store", () =>
{
    using var h = new Harness();
    h.Config.SkillAdmins = ["test_07"];
    const string code = "export const Hello = async () => ({})\n";
    var upload = new JsonObject { ["name"] = "hello-alice", ["title"] = "인사", ["description"] = "d", ["code"] = code, ["extension"] = ".ts", ["author"] = "alice" };
    Equal(403, h.Json("POST", "/api/plugins", upload, User("alice")).Status, "only masters share code");
    (string, string)[] master = User("DOMAIN\\test_07");
    var first = h.Json("POST", "/api/plugins", Merge(upload, """{"author":"test_07"}"""), master);
    Equal("200 1", $"{first.Status} {first.Json!["version"]}");
    var listed = h.Json("GET", "/api/plugins").Json![0]!;
    Equal("hello-alice 1 .ts test_07", $"{Str(listed["name"])} {listed["version"]} {Str(listed["extension"])} {Str(listed["author"])}");
    Equal(false, h.Json("POST", "/api/plugins", Merge(upload, """{"author":"test_07"}"""), master).Json!["changed"]!.GetValue<bool>());
    var js = Merge(upload, """{"author":"test_07","code":"export default async () => ({})\n","extension":".js","edit":true}""");
    Equal(2L, Long(h.Json("POST", "/api/plugins", js, master).Json!["version"]));
    var got = h.Json("GET", "/api/plugins/hello-alice?install=1").Json!;
    Equal($"2 .js {Str(js["code"])} 1", $"{got["version"]} {Str(got["extension"])} {Str(got["code"])} {got["downloads"]}");
    True(!File.Exists(Path.Combine(h.Root, "plugins", "hello-alice", "plugin.ts")), "the old .ts file is gone");
    Equal("1,2", string.Join(",", Directory.GetDirectories(Path.Combine(h.Root, "plugins", "hello-alice", "versions")).Select(Path.GetFileName).Order()));
    Equal(403, h.Json("DELETE", "/api/plugins/hello-alice", headers: User("bob")).Status);
    Equal(200, h.Json("DELETE", "/api/plugins/hello-alice", headers: master).Status);
    Equal("[]", h.Json("GET", "/api/plugins").Json!.ToJsonString());
    Equal(404, h.Json("GET", "/api/plugins/hello-alice").Status);
});

Check("github links and a linked skill follows its file", () =>
{
    var cases = new Dictionary<string, string>
    {
        ["https://github.com/team/skills/blob/main/impala/SKILL.md"] = "https://api.github.com/repos/team/skills/contents/impala/SKILL.md?ref=main",
        ["https://github.corp.example/team/skills/tree/dev/impala"] = "https://github.corp.example/api/v3/repos/team/skills/contents/impala/SKILL.md?ref=dev",
        ["https://github.corp.example/team/skills"] = "https://github.corp.example/api/v3/repos/team/skills/contents/SKILL.md",
        ["https://raw.githubusercontent.com/team/skills/main/a/b.md"] = "https://api.github.com/repos/team/skills/contents/a/b.md?ref=main",
        ["https://github.corp.example/raw/team/skills/main/a.md"] = "https://github.corp.example/api/v3/repos/team/skills/contents/a.md?ref=main",
        ["https://raw.github.corp.example/team/skills/main/a.md"] = "https://github.corp.example/api/v3/repos/team/skills/contents/a.md?ref=main",
    };
    foreach (var (given, api) in cases) Equal(api, GitHubLinks.Request(given).Url, given);
    Equal("token secret", GitHubLinks.Request(cases.Keys.First(), "secret").Headers["Authorization"]);
    foreach (string bad in new[] { "", "github.com/x", "https://github.com/team" }) Throws<RelayError>(() => GitHubLinks.Request(bad), bad);
    Equal("https://github.corp.example/api/v3/repos/team/plugins/contents/notify.ts?ref=main", GitHubLinks.Request("https://github.corp.example/team/plugins/blob/main/notify.ts", plugin: true).Url);
    foreach (string bad in new[] { "https://github.corp.example/team/plugins", "https://github.com/team/plugins/blob/main/README.md" })
        Throws<RelayError>(() => GitHubLinks.Request(bad, plugin: true), bad);
    using var github = new FakeGitHub();
    const string file = "/api/v3/repos/team/skills/contents/impala/SKILL.md";
    github.Files[file] = "---\nname: impala-query\ndescription: \"데이터레이크: Impala\"\n---\n\n# Impala 조회\n\n- Impala로 조회한다.\n";
    using var h = new Harness();
    string link = $"{github.Base}/team/skills/blob/main/impala/SKILL.md";
    var fetched = h.Json("POST", "/api/skills/source", new JsonObject { ["source"] = link });
    Equal(200, fetched.Status, fetched.Text);
    var (name, description, heading, body) = SkillText.Split(Str(fetched.Json!["content"]));
    Equal("impala-query|데이터레이크: Impala|Impala 조회", $"{name}|{description}|{heading}");
    var upload = new JsonObject { ["name"] = "impala-query", ["title"] = heading, ["description"] = description, ["body"] = SkillText.BodyFor(body, heading!), ["author"] = "alice", ["source"] = link };
    Equal(1L, Long(h.Json("POST", "/api/skills", upload).Json!["version"]));
    Equal<int?>(null, h.Server.Skills.SyncSource("impala-query"), "unchanged on GitHub: no new version");
    github.Files[file] = github.Files[file].Replace("조회한다.", "조회하고 검증한다.");
    Equal<int?>(2, h.Server.Skills.SyncSource("impala-query"));
    var latest = h.Json("GET", "/api/skills/impala-query").Json!;
    Equal($"2|깃허브 변경 반영|{link}|alice|- Impala로 조회하고 검증한다.", $"{latest["version"]}|{Str(latest["note"])}|{Str(latest["source"])}|{Str(latest["author"])}|{Str(latest["body"])}");
    Equal("impala-query", string.Join(",", h.Server.Skills.Linked()));
    github.Files.Remove(file);
    Equal<int?>(null, h.Server.Skills.SyncSource("impala-query"));
    Contains("찾지 못했습니다", Str(h.Json("GET", "/api/skills").Json![0]!["sourceError"]));
    Equal(400, h.Json("POST", "/api/skills/source", new JsonObject { ["source"] = link }).Status);
});

Check("a plugin follows its GitHub file", () =>
{
    using var github = new FakeGitHub();
    const string file = "/api/v3/repos/team/plugins/contents/notify.ts";
    github.Files[file] = "export const Notify = async () => ({})\n";
    using var h = new Harness();
    string link = $"{github.Base}/team/plugins/blob/main/notify.ts";
    var fetched = h.Json("POST", "/api/plugins/source", new JsonObject { ["source"] = link });
    Equal("200 .ts", $"{fetched.Status} {Str(fetched.Json!["extension"])}");
    var upload = new JsonObject { ["name"] = "notify-alice", ["title"] = "알림", ["description"] = "", ["code"] = fetched.Json["content"]!.DeepClone(), ["extension"] = ".ts", ["author"] = "alice", ["source"] = link };
    (string, string)[] master = User("editor_one");
    Equal(1L, Long(h.Json("POST", "/api/plugins", upload, master).Json!["version"]));
    Equal<int?>(null, h.Server.Plugins.SyncSource("notify-alice"), "unchanged on GitHub: no new version");
    github.Files[file] = "export const Notify = async () => ({ event: async () => {} })\n";
    Equal<int?>(2, h.Server.Plugins.SyncSource("notify-alice"));
    var latest = h.Json("GET", "/api/plugins/notify-alice").Json!;
    Equal($"2|깃허브 변경 반영|{link}|alice", $"{latest["version"]}|{Str(latest["note"])}|{Str(latest["source"])}|{Str(latest["author"])}");
    Contains("event", Str(latest["code"]));
    Equal("notify-alice", string.Join(",", h.Server.Plugins.Linked()));
    h.Json("POST", "/api/plugins", Merge(upload, """{"code":"// mine\n","source":"","edit":true}"""), master);
    Equal("|", $"{Str(h.Json("GET", "/api/plugins").Json![0]!["source"])}|{string.Join(",", h.Server.Plugins.Linked())}", "\"\" stops following the link");
    github.Files.Remove(file);
    Equal(400, h.Json("POST", "/api/plugins/source", new JsonObject { ["source"] = link }).Status);
});

Check("card excerpt", () =>
{
    Equal("결과는 CSV로 저장한다.", SkillText.Excerpt("---\nname: x\ndescription: d\n---\n\n# 제목\n\n<!-- note -->\n---\n1. **결과는** `CSV`로 저장한다.\n- 다음"));
    Equal("code", SkillText.Excerpt("# 제목만\n\n```\ncode\n```\n"));
    Equal("", SkillText.Excerpt("# 제목만\n"));
    Equal(new string('가', 9) + "…", SkillText.Excerpt(new string('가', 200), limit: 10));
});

Check("stop signal reaches the running server", () =>
{
    string executable = Path.Combine(sandbox, "stop-check", "i-api.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
    using var instance = new Setup.Instance(executable);
    True(!instance.Stop.WaitOne(0), "not yet");
    var stopping = Task.Run(() => Setup.StopRunning(executable));
    True(instance.Stop.WaitOne(5000), "the server is told to stop");
    instance.Dispose();
    Equal(1, stopping.Result);
    Equal(0, Setup.StopRunning(executable), "nothing runs any more");
});

// ── i-luncher 의 앱 목록 · 공지 · GitHub 중계 · 사용 기록 · 러너 · 릴리스 (예전 LauncherApiTest) ─────────────────────

Harness LauncherHarness(FakeGitHub github) => new($"RELAY_GITHUB_TOKEN=gh-token", $"RELAY_GITHUB_API=http://127.0.0.1:{github.Port}",
    $"RUNNERS_ROOT={Path.Combine(sandbox, "runners-" + Guid.NewGuid().ToString("N")[..6])}", $"RUNNERS_ENV_DIR={Path.Combine(sandbox, "profiles")}");

Check("catalog needs the admin password to change and refuses stale saves", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    var read = h.Json("GET", "/launcher/catalog", key: false);
    Equal("""{"schemaVersion":1,"apps":[],"revision":""}""", read.Json!.ToJsonString(), "read without the app key or a password");
    var app = J("""{"id":"deskcal","name":"DeskCal","repository":"https://github.com/owner1/DeskCal","category":"일정"}""");
    Equal(401, h.Json("PUT", "/launcher/catalog", new JsonObject { ["apps"] = new JsonArray(app.DeepClone()), ["revision"] = "" }, key: false).Status);
    var saved = h.Json("PUT", "/launcher/catalog", new JsonObject { ["apps"] = new JsonArray(app.DeepClone()), ["revision"] = "" }, key: false, admin: Harness.Admin);
    Equal(200, saved.Status);
    Equal($"[{app.ToJsonString()}]", saved.Json!["apps"]!.ToJsonString());
    var again = h.Json("GET", "/launcher/catalog", key: false);
    Equal("일정", Str(again.Json!["apps"]![0]!["category"]));
    Equal($"\"{Str(saved.Json["revision"])}\"", again.Header("ETag"));
    Equal(304, h.Call("GET", "/launcher/catalog", headers: [("If-None-Match", again.Header("ETag"))], key: false).Status);
    Equal(409, h.Json("PUT", "/launcher/catalog", J("""{"apps":[],"revision":""}"""), key: false, admin: Harness.Admin).Status, "a save from an older list is refused");
    foreach (JsonNode bad in new JsonNode[] { J("""[{"id":"Bad Id","name":"x","repository":"r"}]"""), new JsonArray(app.DeepClone(), app.DeepClone()), JsonValue.Create("apps") })
        Equal(400, h.Json("PUT", "/launcher/catalog", new JsonObject { ["apps"] = bad, ["revision"] = saved.Json["revision"]!.DeepClone() }, key: false, admin: Harness.Admin).Status, bad.ToJsonString());
    True(File.Exists(Path.Combine(h.Root, "launcher", "catalog.json")), "kept in the data folder");
});

Check("app page screenshots and details", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, .. Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("pixels", 10)))];
    Equal(401, h.Call("POST", "/launcher/media", png, key: false, contentType: "image/png").Status, "uploading needs the admin password");
    Equal(415, h.Call("POST", "/launcher/media", "GIF89a...."u8.ToArray(), key: false, admin: Harness.Admin).Status, "PNG and JPG only");
    var posted = h.Call("POST", "/launcher/media", png, key: false, admin: Harness.Admin);
    Equal(201, posted.Status);
    string name = Str(posted.Json!["name"]);
    True(System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-f0-9]{16}\\.png$"), name);
    Equal(name, Str(h.Call("POST", "/launcher/media", png, key: false, admin: Harness.Admin).Json!["name"]), "the same image is one file");
    var image = h.Call("GET", "/launcher/media/" + name, key: false);
    True(image.Status == 200 && image.Body.SequenceEqual(png) && image.Header("Content-Type") == "image/png", "read without a password");
    Equal(404, h.Call("GET", "/launcher/media/..%2Fcatalog.json", key: false).Status);
    var app = new JsonObject { ["id"] = "app", ["name"] = "App", ["repository"] = "https://github.com/owner1/app", ["details"] = "긴 설명\n둘째 줄",
        ["screenshots"] = new JsonArray(name, "https://example.com/shot.png") };
    foreach (string bad in new[] { """{"screenshots":["../x.png"]}""", $$"""{"screenshots":[{{string.Join(",", Enumerable.Repeat($"\"{name}\"", 13))}}]}""", """{"details":5}""" })
        Equal(400, h.Json("PUT", "/launcher/catalog", new JsonObject { ["apps"] = new JsonArray(Merge(app, bad)), ["revision"] = "" }, key: false, admin: Harness.Admin).Status, bad);
    Equal(200, h.Json("PUT", "/launcher/catalog", new JsonObject { ["apps"] = new JsonArray(app.DeepClone()), ["revision"] = "" }, key: false, admin: Harness.Admin).Status);
    Equal($"[\"{name}\",\"https://example.com/shot.png\"]", h.Json("GET", "/launcher/catalog", key: false).Json!["apps"]![0]!["screenshots"]!.ToJsonString());
    string unused = Path.Combine(h.Root, "launcher", "media", Str(h.Call("POST", "/launcher/media", [0xFF, 0xD8, 0xFF, .. "jpegjpegjpeg"u8.ToArray()], key: false, admin: Harness.Admin).Json!["name"]));
    Equal(0, h.Server.Launcher.CleanMedia(), "a fresh upload waits for its app list");
    foreach (string path in new[] { unused, Path.Combine(h.Root, "launcher", "media", name) }) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
    Equal(1, h.Server.Launcher.CleanMedia());
    True(!File.Exists(unused) && File.Exists(Path.Combine(h.Root, "launcher", "media", name)), "a named one stays");
});

Check("launcher notices", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    Equal("""{"text":"","revision":""}""", h.Json("GET", "/launcher/notices", key: false).Json!.ToJsonString());
    var saved = h.Json("PUT", "/launcher/notices", J("""{"text":"제목\r\n본문","revision":""}"""), key: false, admin: Harness.Admin);
    Equal("200 제목\n본문", $"{saved.Status} {Str(saved.Json!["text"])}");
    Equal(409, h.Json("PUT", "/launcher/notices", J("""{"text":"x","revision":"old"}"""), key: false, admin: Harness.Admin).Status);
});

Check("GitHub relay serves the token account and rewrites addresses", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    var latest = h.Call("GET", "/api/v3/repos/owner1/app/releases/latest", key: false);
    Equal(200, latest.Status, latest.Text);
    Equal("v1.2.3", Str(latest.Json!["tag_name"]));
    Equal($"{h.Base}/api/v3/repos/owner1/app/releases/assets/7", Str(latest.Json["assets"]![0]!["url"]), "asset urls point back here");
    var asset = h.Call("GET", "/api/v3/repos/owner1/app/releases/assets/7", headers: [("Accept", "application/octet-stream")], key: false);
    Equal("200 release-bytes", $"{asset.Status} {asset.Text}", "the file is streamed through");
    var storage = github.Seen.Where(entry => entry.Path.StartsWith("/storage/")).ToList();
    True(storage.Count > 0 && storage[0].Authorization is null, "the token is not sent to the file storage");
    var notice = h.Call("GET", "/api/v3/repos/owner1/app/contents/" + Uri.EscapeDataString("공지.txt") + "?ref=main", headers: [("Accept", "application/vnd.github.v3.raw")], key: false);
    Equal("200 공지", $"{notice.Status} {notice.Text}");
});

Check("usage reports add up per day and app and survive a restart", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    string today = DateTime.Now.ToString("yyyy-MM-dd"), yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
    var first = J(("{\"app\":\"Capture\",\"version\":\"1.0.9\",\"user\":\"a1b2c3d4e5f6\",\"days\":{\"" + (yesterday) + "\":{\"active\":[0,100.5],\"launch\":[1,0]},\"" + (today) + "\":{\"active\":[0,90000],\"capture\":[3,0]}}}"));
    var second = J(("{\"app\":\"Capture\",\"version\":\"1.0.9\",\"user\":\"0f0f0f0f0f0f\",\"days\":{\"" + (today) + "\":{\"capture\":[2,0],\"record\":[1,42.5]}}}"));
    foreach (var report in new[] { first, second }) Equal(200, h.Json("POST", "/launcher/usage", report, key: false).Status);
    var summary = h.Json("GET", $"/launcher/usage?from={yesterday}&to={today}", key: false);
    Equal(200, summary.Status, summary.Text);
    var capture = summary.Json!["apps"]!["Capture"]!;
    Equal("2 3", $"{capture["users"]} {capture["userDays"]}", "two people, three person-days");
    Equal("5,0", $"{Double(capture["metrics"]!["capture"]![0])},{Double(capture["metrics"]!["capture"]![1])}");
    Equal(100.5 + 86400, Double(capture["metrics"]!["active"]![1]), "a day holds at most 86,400 seconds per report");
    Equal("1,42.5", $"{Double(capture["metrics"]!["record"]![0])},{Double(capture["metrics"]!["record"]![1])}");
    Equal(yesterday, Str(summary.Json["since"]));
    True(!summary.Text.Contains("a1b2c3d4e5f6"), "counts only, no user ids");
    Equal(5.0, capture["months"]!.AsObject().Sum(month => Double(month.Value!["metrics"]!["capture"]![0])));
    True(h.Json("GET", $"/launcher/usage?from={today}&to={today}", key: false).Json!["apps"]!["Capture"]!["metrics"]!["launch"] is null, "only the asked days");
    h.Server.Usage.Flush();
    True(Directory.GetFiles(Path.Combine(h.Root, "usage"), "*.json").Length > 0, "kept in the data folder");
    var again = new Usage(Path.Combine(h.Root, "usage"));
    Equal(5.0, Double(again.Summary(yesterday, today)["apps"]!["Capture"]!["metrics"]!["capture"]![0]), "read back after a restart");
});

Check("usage refuses bad reports and drops days out of range", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    string today = DateTime.Now.ToString("yyyy-MM-dd");
    string good = ("{\"app\":\"Capture\",\"user\":\"a1b2c3d4e5f6\",\"days\":{\"" + (today) + "\":{\"capture\":[1,0]}}}");
    foreach (string bad in new[] { """{"app":"bad name"}""", """{"user":"Kim"}""", ("{\"days\":{\"" + (today) + "\":{\"Capture!\":[1,0]}}}"), ("{\"days\":{\"" + (today) + "\":{\"capture\":[1]}}}"),
                 ("{\"days\":{\"" + (today) + "\":{\"capture\":[-1,0]}}}"), """{"days":{"2026-13-40":{"capture":[1,0]}}}""" })
        Equal(400, h.Json("POST", "/launcher/usage", Merge(J(good).AsObject(), bad), key: false).Status, bad);
    Equal(400, h.Json("POST", "/launcher/usage", J("""["not","an","object"]"""), key: false).Status);
    var old = h.Json("POST", "/launcher/usage", Merge(J(good).AsObject(), ("{\"days\":{\"" + DateTime.Now.AddDays(-400).ToString("yyyy-MM-dd") + "\":{\"capture\":[1,0]}}}")), key: false);
    Equal("200 0", $"{old.Status} {old.Json!["days"]}", "a year-old day is dropped, not refused");
    var huge = J(good).AsObject();
    huge["pad"] = new string('x', 70000);
    Equal(413, h.Json("POST", "/launcher/usage", huge, key: false).Status);
    Equal(400, h.Call("GET", "/launcher/usage?from=yesterday", key: false).Status);
});

Check("usage counts app starts and downloads seen by the GitHub relay", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    Equal(200, h.Call("GET", "/api/v3/repos/owner1/app/releases/latest", headers: [("User-Agent", "App/1.2.0")], key: false).Status);
    h.Call("GET", "/api/v3/repos/owner1/app/releases/latest", headers: [("User-Agent", "i-luncher/1.4.0")], key: false);
    h.Call("GET", "/api/v3/repos/owner1/app/releases/assets/7", key: false);
    h.Call("GET", "/api/v3/repos/owner1/app/releases/assets/7", headers: [("Accept", "application/octet-stream"), ("User-Agent", "i-luncher/1.4.0")], key: false);
    var app = h.Json("GET", "/launcher/usage", key: false).Json!["apps"]!["app"]!;
    Equal(1.0, Double(app["metrics"]!["check"]![0]), "the app's own update check is a start; the launcher's check of it is not");
    Equal(1.0, Double(app["metrics"]!["download"]![0]), "only the file itself counts, not its JSON");
    Equal("1 0", $"{app["pcs"]} {app["users"]}", "relay-seen PCs are kept apart from the apps' users");
});

Check("GitHub relay opens only allowed repositories and reads", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    Equal(404, h.Call("GET", "/api/v3/repos/other/tool", key: false).Status, "another account's repository is closed");
    h.Json("PUT", "/launcher/catalog", J("""{"apps":[{"id":"tool","name":"Tool","repository":"https://github.com/other/tool.git"}],"revision":""}"""), key: false, admin: Harness.Admin);
    Equal(200, h.Call("GET", "/api/v3/repos/other/tool", key: false).Status, "a repository in the app list is open");
    Equal(404, h.Call("GET", "/api/v3/user", key: false).Status, "only repository reads");
    Equal(404, h.Call("GET", "/api/v3/repos/owner1/app/issues", key: false).Status, "only releases, files and trees");
    Equal(405, h.Json("PUT", "/api/v3/repos/owner1/app", new JsonObject(), key: false).Status, "never writes");
    True(github.Seen.All(entry => entry.Path != "/repos/owner1/app/issues"), "refused paths never reach GitHub");
});

Check("the GitHub Enterprise host comes from the API address", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    Equal($"127.0.0.1:{github.Port}|github.com", $"{h.Config.GitHubWebHost}|{Harness.Make(sandbox, []).GitHubWebHost}");
    var enterprise = Harness.Make(sandbox, [$"RELAY_DATA_ROOT={h.Root}", "RELAY_GITHUB_API=https://github.example.com/api/v3/"]);
    Equal("github.example.com", enterprise.GitHubWebHost);
    var store = new LauncherStore(Path.Combine(h.Root, "enterprise"), enterprise.GitHubHostsAll);
    store.SaveCatalog(J("""[{"id":"a","name":"A","repository":"https://github.example.com/Team/App"},{"id":"b","name":"B","repository":"https://github.com/owner/b.git"},{"id":"c","name":"C","repository":"https://gitlab.example.com/x/c"}]"""), "");
    Equal("owner/b,team/app", string.Join(",", store.Repositories().Order()));
});

string RunnerFolder(Harness h, string name, string agent, string repository)
{
    string folder = Path.Combine(h.Config.RunnersRoot!, name);
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, ".runner"), $$"""{"agentId": 7, "agentName": "{{agent}}", "gitHubUrl": "https://github.com/{{repository}}"}""", new UTF8Encoding(true));
    foreach (string file in new[] { "config.cmd", "run.cmd" }) File.WriteAllText(Path.Combine(folder, file), "@echo off\r\n");
    return folder;
}

Check("runners are listed, edited, started and stopped", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    Directory.CreateDirectory(h.Config.RunnersRoot!);
    string folder = RunnerFolder(h, "app", "PC-app", "owner1/app");
    File.WriteAllBytes(Path.Combine(folder, ".env"), "APP_ENV_PATH=C:\\profiles\\app.env\r\n"u8.ToArray());
    Directory.CreateDirectory(Path.Combine(h.Config.RunnersRoot!, "env"));
    var launched = new List<string>();
    var stopped = new List<(string, bool)>();
    var runners = h.Server.Runners;
    runners.ScheduledTasks = () => ["\\GitHub runner app", "\\Microsoft\\Windows\\GitHub runner x", "\\Other"];
    runners.Launch = path => launched.Add(Path.GetFileName(path));
    runners.Await = (_, _) => [];
    runners.StopRunner = (path, force) => { stopped.Add((Path.GetFileName(path), force)); return ""; };
    Equal(401, h.Call("GET", "/admin/api/runners", key: false).Status, "admin only");
    var page = h.AdminJson("GET", "/admin/api/runners").Json!;
    True(page["available"]!.GetValue<bool>(), Str(page["message"]));
    var runner = page["runners"]![0]!;
    Equal("app", string.Join(",", page["runners"]!.AsArray().Select(item => Str(item!["id"]))));
    Equal("PC-app|owner1/app|True|False|APP_ENV_PATH=C:\\profiles\\app.env\n",
        $"{Str(runner["name"])}|{Str(runner["repository"])}|{runner["on"]!.GetValue<bool>()}|{runner["running"]!.GetValue<bool>()}|{Str(runner["env"])}");
    Equal("7 online", $"{runner["github"]!["id"]} {Str(runner["github"]!["status"])}", "its state on GitHub, matched by name");
    Equal("""{"name":"GitHub Runners","exists":false,"current":false,"legacy":["GitHub runner app"]}""", page["task"]!.ToJsonString(Util.Compact), "only the top folder's per-runner tasks count");
    Equal("[]", page["repositories"]!.ToJsonString(), "the app list is empty here");
    Equal(400, h.AdminJson("PUT", "/admin/api/runners/app", J("""{"env":"not a variable"}""")).Status);
    Equal(400, h.AdminJson("PUT", "/admin/api/runners/app", J("""{"env":5}""")).Status);
    var saved = h.AdminJson("PUT", "/admin/api/runners/app", J("""{"env":"A_ENV_PATH=C:\\x.env\n# note\nDOTNET_CLI_TELEMETRY_OPTOUT=1"}"""));
    Equal("200 저장했습니다.", $"{saved.Status} {Str(saved.Json!["message"])}", "not running: nothing to restart");
    Equal("A_ENV_PATH=C:\\x.env\r\n# note\r\nDOTNET_CLI_TELEMETRY_OPTOUT=1\r\n", File.ReadAllText(Path.Combine(folder, ".env")));
    Equal(200, h.AdminJson("POST", "/admin/api/runners/app/stop", new JsonObject()).Status);
    Equal("app:False off", $"{stopped[0].Item1}:{stopped[0].Item2} {(h.AdminJson("GET", "/admin/api/runners").Json!["runners"]![0]!["on"]!.GetValue<bool>() ? "on" : "off")}", "stopped means off");
    Equal(200, h.AdminJson("POST", "/admin/api/runners/app/start", new JsonObject()).Status);
    Equal("app on", $"{string.Join(",", launched)} {(h.AdminJson("GET", "/admin/api/runners").Json!["runners"]![0]!["on"]!.GetValue<bool>() ? "on" : "off")}");
    Equal(200, h.AdminJson("POST", "/admin/api/runners/app/restart", J("""{"force":true}""")).Status);
    Equal(("app", true), stopped[^1]);
    foreach (string path in new[] { "/admin/api/runners/env/start", "/admin/api/runners/..%5Cx/start", "/admin/api/runners/missing/start" })
        Equal(404, h.AdminJson("POST", path, new JsonObject()).Status, path);
    var savedThisPc = RelayServer.IsThisPc;
    RelayServer.IsThisPc = _ => false;
    try
    {
        Equal(403, h.AdminJson("POST", "/admin/api/runners/app/start", new JsonObject()).Status, "changes only from the server PC");
        Equal(200, h.AdminJson("GET", "/admin/api/runners").Status, "looking is fine from anywhere");
    }
    finally { RelayServer.IsThisPc = savedThisPc; }
    launched.Clear();
    ProcessTable.UnderOverride = _ => [(1, "Runner.Listener.exe")];
    try { Equal(0, runners.StartOn().Count, "the logon task starts nothing that runs already"); }
    finally { ProcessTable.UnderOverride = null; }
    Equal("app 시작", string.Join(",", runners.StartOn()));
    Equal("app", string.Join(",", launched));
});

Check("a runner is added and removed", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    Directory.CreateDirectory(h.Config.RunnersRoot!);
    RunnerFolder(h, "app", "PC-app", "owner1/app");
    var configured = new List<string>();
    var runners = h.Server.Runners;
    runners.ScheduledTasks = () => [];
    runners.Prepare = target =>
    {
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "config.cmd"), "");
        File.WriteAllText(Path.Combine(target, "run.cmd"), "");
    };
    runners.Configure = (target, url, token, name) =>
    {
        configured.Add($"{Path.GetFileName(target)}|{url}|{token}|{name}");
        File.WriteAllText(Path.Combine(target, ".runner"), $$"""{"agentName": "{{name}}", "gitHubUrl": "{{url}}"}""");
    };
    runners.Launch = _ => { };
    runners.Await = (_, _) => [];
    runners.StopRunner = (_, _) => "";
    string computer = Environment.GetEnvironmentVariable("COMPUTERNAME") ?? "PC";
    var template = h.AdminJson("GET", "/admin/api/runners/template?repository=owner1/app");
    Equal(200, template.Status, template.Text);
    Equal($"owner1/app|app-2|{computer}-app|OWNER1_ENV_PATH={Path.Combine(sandbox, "profiles", "app.env")}\nDOTNET_CLI_TELEMETRY_OPTOUT=1\n",
        $"{Str(template.Json!["repository"])}|{Str(template.Json["folder"])}|{Str(template.Json["name"])}|{Str(template.Json["env"])}");
    var fresh = new JsonObject { ["repository"] = "owner1/app", ["folder"] = "app-2", ["name"] = "PC-app", ["env"] = "X=1" };
    Equal(409, h.AdminJson("POST", "/admin/api/runners", fresh).Status, "the name is taken on GitHub");
    Equal(400, h.AdminJson("POST", "/admin/api/runners", Merge(fresh, """{"name":"PC app"}""")).Status);
    Equal(409, h.AdminJson("POST", "/admin/api/runners", Merge(fresh, """{"folder":"app"}""")).Status, "the folder exists");
    Equal(404, h.AdminJson("POST", "/admin/api/runners", Merge(fresh, """{"repository":"owner1/none"}""")).Status);
    var added = h.AdminJson("POST", "/admin/api/runners", Merge(fresh, """{"name":"PC-app-2"}"""));
    Equal("201 app-2", $"{added.Status} {Str(added.Json!["id"])}", added.Text);
    Equal($"app-2|https://{h.Config.GitHubWebHost}/owner1/app|REG-TOKEN|PC-app-2", string.Join(",", configured), "registered with a fresh token");
    Equal("X=1\r\n", File.ReadAllText(Path.Combine(h.Config.RunnersRoot!, "app-2", ".env")));
    Equal("app,app-2", string.Join(",", h.AdminJson("GET", "/admin/api/runners").Json!["runners"]!.AsArray().Select(item => Str(item!["id"]))));
    runners.Configure = (_, _, _, _) => throw new RelayError(502, "러너를 등록하지 못했습니다: Http response code: NotFound");
    Equal(502, h.AdminJson("POST", "/admin/api/runners", Merge(fresh, """{"folder":"app-3","name":"PC-app-3"}""")).Status);
    True(!Directory.Exists(Path.Combine(h.Config.RunnersRoot!, "app-3")), "a failed registration leaves no folder");
    var removed = h.AdminJson("DELETE", "/admin/api/runners/app");
    Equal(200, removed.Status, removed.Text);
    True(github.Dispatched.Any(entry => entry.Path == "/repos/owner1/app/actions/runners/7" && entry.Authorization == "Bearer gh-token"), "removed on GitHub");
    True(!Directory.Exists(Path.Combine(h.Config.RunnersRoot!, "app")), "folder deleted");
});

Check("one logon task replaces the per-runner tasks", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    Directory.CreateDirectory(h.Config.RunnersRoot!);
    RunnerFolder(h, "app", "PC-app", "owner1/app");
    var registered = new List<(string Name, string Xml)>();
    var deleted = new List<string>();
    var runners = h.Server.Runners;
    runners.ScheduledTasks = () => ["\\GitHub runner app", "\\i-luncher build runner", "\\Keep me"];
    runners.TaskDefinition = name => $"<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task><Command>{name}</Command></Task>";
    runners.DeleteTask = name => { deleted.Add(name); return true; };
    runners.RegisterTask = (name, xml) => { registered.Add((name, xml)); return ""; };
    runners.Launch = _ => { };
    runners.Await = (_, _) => [];
    var task = h.AdminJson("POST", "/admin/api/runners/task", new JsonObject());
    Equal(200, task.Status, task.Text);
    Equal("GitHub runner app,i-luncher build runner", string.Join(",", deleted), "only earlier runner tasks go");
    Equal("GitHub runner app.xml,i-luncher build runner.xml", string.Join(",", Directory.GetFiles(Path.Combine(h.Config.RunnersRoot!, ".backup", "tasks")).Select(Path.GetFileName).Order()));
    Equal(1, registered.Count);
    var document = System.Xml.Linq.XDocument.Parse(registered[0].Xml);
    System.Xml.Linq.XNamespace t = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    Equal(Runners.TaskName, registered[0].Name);
    Equal("--start-runners" + string.Concat(h.Config.SetArgs.Select(item => $" --set \"{item}\"")), document.Descendants(t + "Arguments").Single().Value);
    Equal(IApi.Program.ExecutablePath, document.Descendants(t + "Command").Single().Value);
    True(document.Descendants(t + "LogonTrigger").Any(), "at logon");
    Equal("PT0S", document.Descendants(t + "ExecutionTimeLimit").Single().Value, "it never ends what it started");
});

Check("next release version keeps every part 0 to 9", () =>
{
    foreach (var (latest, floor, expected) in new (( int, int, int)?, (int, int, int)?, string)[]
             { ((1, 2, 3), (1, 0, 0), "1.2.4"), ((1, 0, 9), null, "1.1.0"), ((1, 9, 9), null, "2.0.0"), ((1, 0, 26), null, "1.1.0"), ((1, 0, 3), (2, 0, 0), "2.0.0") })
        Equal(expected, Releases.NextVersion(latest, floor), $"{latest} {floor}");
});

Check("releases show the next version and start the workflow with it", () =>
{
    using var github = new FakeGitHub();
    using var h = LauncherHarness(github);
    h.Json("PUT", "/launcher/catalog", J("""{"apps":[{"id":"tool","name":"Tool","repository":"https://github.com/other/tool","order":2},{"id":"app","name":"App","repository":"https://github.com/owner1/app.git","order":1}],"revision":""}"""), key: false, admin: Harness.Admin);
    Equal(401, h.Call("GET", "/admin/api/releases", key: false).Status, "admin only");
    JsonArray Releases() => h.AdminJson("GET", "/admin/api/releases").Json!["releases"]!.AsArray();
    var listed = Releases();
    var app = listed[0]!;
    Equal("owner1/app|App|1.2.3|1.2.0|1.2.4|True", $"{Str(app["repository"])}|{Str(app["name"])}|{Str(app["latest"])}|{Str(app["floor"])}|{Str(app["next"])}|{app["chooseVersion"]!.GetValue<bool>()}");
    Equal("completed success ", $"{Str(app["run"]!["status"])} {Str(app["run"]!["conclusion"])} {Str(app["error"])}");
    Contains("릴리스 워크플로", Str(listed[1]!["error"]), "a repository without the workflow says so");
    foreach (var (repository, version, code) in new[] { ("owner1/app", "1.3", 400), ("owner1/app", "1.10.0", 400), ("owner1/app", "1.2.3", 409), ("owner1/app", "0.9.0", 409),
                 ("owner1/app", "1.2.5", 409), ("owner1/unknown", "9.0.0", 404), ("other/tool", "1.0.0", 409) })
        Equal(code, h.AdminJson("POST", "/admin/api/releases", new JsonObject { ["repository"] = repository, ["version"] = version }).Status, $"{repository} {version}");
    github.RunStatus = "in_progress";
    Equal(409, h.AdminJson("POST", "/admin/api/releases", J("""{"repository":"owner1/app","version":"1.3.0"}""")).Status, "one release at a time");
    github.RunStatus = "completed";
    Equal(0, github.Dispatched.Count, "nothing refused reaches GitHub");
    var started = h.AdminJson("POST", "/admin/api/releases", J("""{"repository":"OWNER1/app","version":"v1.3.0"}"""));
    Equal("202 1.3.0 main", $"{started.Status} {Str(started.Json!["version"])} {Str(started.Json["ref"])}", started.Text);
    Equal("/repos/owner1/app/actions/workflows/release.yml/dispatches|{\"ref\":\"main\",\"inputs\":{\"version\":\"1.3.0\"}}|Bearer gh-token",
        string.Join(",", github.Dispatched.Select(entry => $"{entry.Path}|{entry.Body?.ToJsonString()}|{entry.Authorization}")));
    Equal(409, h.AdminJson("POST", "/admin/api/releases", J("""{"repository":"owner1/app","version":"1.3.1"}""")).Status, "a second start right after is refused");
    var shown = Releases()[0]!["run"]!;
    Equal("requested 1.3.0", $"{Str(shown["status"])} {Str(shown["version"])}", "shown as requested until GitHub lists the new run");
});

Check("a release workflow without a version input takes only the next version", () =>
{
    using var github = new FakeGitHub { ChooseVersion = false };
    using var h = LauncherHarness(github);
    h.Json("PUT", "/launcher/catalog", J("""{"apps":[{"id":"app","name":"App","repository":"https://github.com/owner1/app"}],"revision":""}"""), key: false, admin: Harness.Admin);
    True(!h.AdminJson("GET", "/admin/api/releases").Json!["releases"]![0]!["chooseVersion"]!.GetValue<bool>(), "no version input");
    Equal(409, h.AdminJson("POST", "/admin/api/releases", J("""{"repository":"owner1/app","version":"2.0.0"}""")).Status);
    Equal(202, h.AdminJson("POST", "/admin/api/releases", J("""{"repository":"owner1/app","version":"1.2.4"}""")).Status);
    Equal("{\"ref\":\"main\"}", github.Dispatched[^1].Body!.ToJsonString(), "started without inputs");
});

// ── 관리 창 (i-api.exe 를 손으로 실행하면 뜨는 창) ──────────────────────────────────────────────────────

Check("management window: changing an app's id or install folder name keeps the old value for the launcher", () =>
{
    var data = new JsonObject { ["id"] = "fastcopy", ["name"] = "FastCopy" };
    Equal("FastCopy", IApi.Manager.AppRow.EffectiveInstallName(data), "install name falls back to the name");
    IApi.Manager.ManagerView.KeepFormer(data, "formerIds", "swiftcopy", "fastcopy");
    IApi.Manager.ManagerView.KeepFormer(data, "formerIds", "swiftcopy", "fastcopy");
    Equal("[\"swiftcopy\"]", data["formerIds"]!.ToJsonString(), "old id kept once");
    IApi.Manager.ManagerView.KeepFormer(data, "formerInstallNames", "FastCopy", "FastCopy");
    True(data["formerInstallNames"] is null, "an unchanged name adds nothing");
    IApi.Manager.ManagerView.KeepFormer(data, "formerIds", "fastcopy", "swiftcopy");
    Equal("[\"fastcopy\"]", data["formerIds"]!.ToJsonString(), "renamed back: the current id leaves the list, the other one stays");
});

Check("management window: the update source is the repository that released this i-api (GitHub or Enterprise)", () =>
{
    Equal(("https://api.github.com", "o/i-luncher", "github.com"), IApi.Manager.Updater.Parse("https://github.com/o/i-luncher")!.Value, "github.com");
    Equal(("https://ghe.example.com/api/v3", "team/i-luncher", "ghe.example.com"), IApi.Manager.Updater.Parse("https://ghe.example.com/team/i-luncher.git/")!.Value, "Enterprise");
    True(IApi.Manager.Updater.Parse("") is null && IApi.Manager.Updater.Parse("https://github.com/only-owner") is null, "no repository");
});

Check("management window: an update replaces the program files, keeps settings.txt and the old files in backup", () =>
{
    string folder = Path.Combine(Path.GetTempPath(), "i-api-checks-update-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(folder);
    string zip = Path.Combine(folder, "..", Path.GetFileName(folder) + ".zip");
    try
    {
        File.WriteAllText(Path.Combine(folder, "i-api.exe"), "old exe");
        File.WriteAllText(Path.Combine(folder, "i-api.dll"), "old dll");
        File.WriteAllText(Path.Combine(folder, "settings.txt"), "RELAY_PORT=5081\r\n");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("i-api.exe", "new exe"), ("i-api.dll", "new dll"), ("서버 시작.cmd", "new cmd"), ("settings.txt", "RELAY_PORT=1"), ("../outside.txt", "no") })
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
        }
        IApi.Manager.ServerManager.FolderOverride = folder;
        string backup = IApi.Manager.Updater.Apply(new IApi.Manager.Updater.Found(new Version(99, 0, 0), zip));
        Equal("new exe|new dll|new cmd", string.Join("|", new[] { "i-api.exe", "i-api.dll", "서버 시작.cmd" }.Select(name => File.ReadAllText(Path.Combine(folder, name)))), "new files in place");
        Equal("RELAY_PORT=5081\r\n", File.ReadAllText(Path.Combine(folder, "settings.txt")), "settings.txt untouched");
        Equal("old exe|old dll", File.ReadAllText(Path.Combine(backup, "i-api.exe")) + "|" + File.ReadAllText(Path.Combine(backup, "i-api.dll")), "old files kept in " + backup);
        True(!File.Exists(Path.Combine(folder, "..", "outside.txt")) && !File.Exists(zip), "nothing written outside, the download is removed");
    }
    finally
    {
        IApi.Manager.ServerManager.FolderOverride = null;
        try { Directory.Delete(folder, true); File.Delete(zip); } catch (IOException) { }
    }
});

Check("management window: an i-api installed from the launcher store takes over the server in the old folder (settings, start at login)", () =>
{
    string sandbox = Path.Combine(Path.GetTempPath(), "i-api-checks-takeover-" + Guid.NewGuid().ToString("N")[..8]);
    string app = Path.Combine(sandbox, "Programs", "i-api"), local = Path.Combine(sandbox, "Local"), roaming = Path.Combine(sandbox, "Roaming");
    string startup = Path.Combine(roaming, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
    string old = Path.Combine(local, "i-api");
    Directory.CreateDirectory(startup);
    Directory.CreateDirectory(app);
    Directory.CreateDirectory(old);
    foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        if (Path.GetFileName(file).StartsWith("i-api.", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(file).StartsWith("i-api.Checks", StringComparison.OrdinalIgnoreCase))
            File.Copy(file, Path.Combine(app, Path.GetFileName(file)));
    File.WriteAllText(Path.Combine(old, "i-api.exe"), "old server");
    File.WriteAllText(Path.Combine(old, "settings.txt"), "RELAY_PORT=5099\r\nRELAY_ADMIN_PASSWORD=pw\r\nRELAY_BIND=127.0.0.1\r\n");
    File.WriteAllText(Path.Combine(startup, "i-api.vbs"), "old", Encoding.Unicode);
    IApi.Manager.ServerManager.FolderOverride = app;
    IApi.Manager.ServerManager.LocalRoot = local;
    IApi.Manager.ServerManager.StartupOverride = startup;
    IApi.Manager.ServerManager.ChildEnvironment = new() { ["APPDATA"] = roaming, ["LOCALAPPDATA"] = local, ["ProgramData"] = Path.Combine(sandbox, "ProgramData") };
    try
    {
        string? moved = IApi.Manager.ServerManager.MoveFromOldFolderAsync().GetAwaiter().GetResult();
        Contains("옮겼습니다", moved ?? "", "moved");
        Equal(5099, IApi.Manager.ServerManager.Port, "the port came along");
        Equal("pw", IApi.Manager.ServerManager.AdminPassword(), "and the password");
        True(File.ReadAllText(Path.Combine(startup, "i-api.vbs"), Encoding.Unicode).Contains(IApi.Manager.ServerManager.Exe, StringComparison.OrdinalIgnoreCase), "the start at login now starts this i-api.exe");
        True(File.Exists(Path.Combine(old, "MOVED-TO-i-api.txt")) && File.Exists(Path.Combine(old, "i-api.exe")), "the old folder stays, with a note");
        Equal<string?>(null, IApi.Manager.ServerManager.MoveFromOldFolderAsync().GetAwaiter().GetResult(), "only once");
        Equal<string?>(null, IApi.Manager.ServerManager.PythonScript, "no Python relay to move either");
    }
    finally
    {
        IApi.Manager.ServerManager.FolderOverride = null;
        IApi.Manager.ServerManager.LocalRoot = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        IApi.Manager.ServerManager.StartupOverride = null;
        IApi.Manager.ServerManager.ChildEnvironment = [];
        try { Directory.Delete(sandbox, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
});

Check("management window: an old Python relay moves into this i-api (settings, start at login), which then starts, answers and stops", () =>
{
    // 이 PC 의 실제 서버 · 시작 폴더 · 데이터를 건드리지 않게 모두 임시 폴더로 (서버 프로그램은 따로 도는 프로세스라 환경 변수로)
    string sandbox = Path.Combine(Path.GetTempPath(), "i-api-checks-move-" + Guid.NewGuid().ToString("N")[..8]);
    string app = Path.Combine(sandbox, "app"), local = Path.Combine(sandbox, "Local"), roaming = Path.Combine(sandbox, "Roaming"), machine = Path.Combine(sandbox, "ProgramData");
    string startup = Path.Combine(roaming, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
    string data = Path.Combine(sandbox, "data");
    Directory.CreateDirectory(startup);
    Directory.CreateDirectory(app);
    foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        if (Path.GetFileName(file).StartsWith("i-api.", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(file).StartsWith("i-api.Checks", StringComparison.OrdinalIgnoreCase))
            File.Copy(file, Path.Combine(app, Path.GetFileName(file)));
    int port;
    using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; }
    IApi.Manager.ServerManager.FolderOverride = app;
    IApi.Manager.ServerManager.LocalRoot = local;
    IApi.Manager.ServerManager.StartupOverride = startup;
    IApi.Manager.ServerManager.ChildEnvironment = new() { ["APPDATA"] = roaming, ["LOCALAPPDATA"] = local, ["ProgramData"] = machine };
    try
    {
        string old = Path.Combine(local, "TerminalNotesRelay", "relay.py");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old,
            "# ── 설정 시작 ──\nSETTINGS = {\n    \"RELAY_HOST\": \"\",\n    \"RELAY_PORT\": " + port + ",\n    \"RELAY_BIND\": \"127.0.0.1\",\n" +
            "    \"RELAY_ADMIN_PASSWORD\": \"pw\\\"x\",\n    \"RELAY_DATA_ROOT\": \"" + data.Replace("\\", "\\\\") + "\",\n}\n# ── 설정 끝 ──\nVERSION = \"2.22.0\"\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(startup, "Terminal Notes Relay.vbs"), "old", Encoding.Unicode);
        Equal(old, IApi.Manager.ServerManager.PythonScript, "an old Python relay is found");
        string? moved = IApi.Manager.ServerManager.MoveFromPythonAsync().GetAwaiter().GetResult();
        True(moved?.Contains("옮겼습니다") == true, "moved: " + moved);
        Equal<string?>(null, IApi.Manager.ServerManager.PythonScript, "nothing left to move");
        Equal("pw\"x", IApi.Manager.ServerManager.AdminPassword(), "the password moved to settings.txt");
        Equal(port, IApi.Manager.ServerManager.Port, "the port moved");
        Equal("i-api.vbs", string.Join(",", Directory.GetFiles(startup).Select(Path.GetFileName)), "the start at login now starts i-api.exe");
        True(File.ReadAllText(Path.Combine(startup, "i-api.vbs"), Encoding.Unicode).Contains(IApi.Manager.ServerManager.Exe, StringComparison.OrdinalIgnoreCase), "pointing at this i-api.exe");
        True(IApi.Manager.ServerManager.StartsAtLogon, "the window sees the start at login");
        True(File.Exists(Path.Combine(local, "TerminalNotesRelay", "MOVED-TO-i-api.txt")) && File.Exists(old), "the old folder stays, with a note");
        Equal<string?>(null, IApi.Manager.ServerManager.MoveFromPythonAsync().GetAwaiter().GetResult(), "moved once (the note marks the old folder)");
        var (code, output) = IApi.Manager.ServerManager.RestartAsync().GetAwaiter().GetResult();
        Equal(0, code, "started: " + output);
        var address = IApi.Manager.ServerManager.ReadAddress();
        True(IApi.Manager.ServerManager.IsHealthyAsync(address).GetAwaiter().GetResult(), "it answers at " + address.HealthUrl);
        IApi.Manager.ServerManager.HaltAsync().GetAwaiter().GetResult();
        True(!IApi.Manager.ServerManager.IsHealthyAsync(address).GetAwaiter().GetResult(), "stopped");
    }
    finally
    {
        try { IApi.Manager.ServerManager.HaltAsync().GetAwaiter().GetResult(); } catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        IApi.Manager.ServerManager.FolderOverride = null;
        IApi.Manager.ServerManager.LocalRoot = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        IApi.Manager.ServerManager.StartupOverride = null;
        IApi.Manager.ServerManager.ChildEnvironment = [];
        try { Directory.Delete(sandbox, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
});

// ── API 마다 켜고 끄기 · 호출 수 (관리 창 → API) ──────────────────────────────────────────────────────

Check("each API can be turned off and counts its calls", () =>
{
    using var h = new Harness();
    Equal(401, h.Call("GET", "/admin/api/functions", key: false).Status, "admin only");
    var listing = h.AdminJson("GET", "/admin/api/functions").Json!;
    var functions = listing["functions"]!.AsArray();
    True(functions.Count > 40, $"{functions.Count} functions");
    var ids = functions.Select(item => Str(item!["id"])).ToList();
    Equal(ids.Count, ids.Distinct().Count(), "ids are unique");
    var catalog = functions.First(item => Str(item!["id"]) == "launcher.catalog.read")!;
    Equal("i-luncher|GET /launcher/catalog|True|True", $"{Str(catalog["group"])}|{Str(catalog["method"])} {Str(catalog["path"])}|{catalog["switchable"]!.GetValue<bool>()}|{catalog["on"]!.GetValue<bool>()}");
    for (int index = 0; index < 3; index++) Equal(200, h.Call("GET", "/launcher/catalog", key: false).Status);
    Equal(404, h.Call("GET", "/launcher/media/nothing.png", key: false).Status);
    var turned = h.AdminJson("PUT", "/admin/api/functions/launcher.catalog.read", J("""{"on":false}"""));
    Equal("200 False", $"{turned.Status} {turned.Json!["on"]!.GetValue<bool>()}");
    var off = h.Call("GET", "/launcher/catalog", key: false);
    Equal(503, off.Status, "a function that is off answers 503");
    Contains("꺼져 있습니다", off.Text);
    Equal(200, h.Call("GET", "/launcher/notices", key: false).Status, "the others keep answering");
    Equal(409, h.AdminJson("PUT", "/admin/api/functions/admin.status", J("""{"on":false}""")).Status, "the admin menu's own APIs stay on");
    Equal(400, h.AdminJson("PUT", "/admin/api/functions/launcher.catalog.read", J("""{"on":"no"}""")).Status);
    Equal(404, h.AdminJson("PUT", "/admin/api/functions/nothing", J("""{"on":true}""")).Status);
    var after = h.AdminJson("GET", "/admin/api/functions").Json!["functions"]!.AsArray();
    var read = after.First(item => Str(item!["id"]) == "launcher.catalog.read")!;
    Equal("4 1", $"{read["today"]!["calls"]} {read["today"]!["failures"]}", "three answers and one refusal today");
    Contains("꺼져 있습니다", Str(read["lastFailure"]!["message"]));
    var media = after.First(item => Str(item!["id"]) == "launcher.media.read")!;
    Equal("1 1 404", $"{media["today"]!["calls"]} {media["period"]!["failures"]} {media["lastFailure"]!["status"]}");
    Equal(1L, Long(h.AdminJson("GET", "/admin/api/status").Json!["functionsOff"]));
    h.Server.Stats.Flush();
    // 서버를 다시 켜도 꺼 둔 API 와 오늘의 수는 그대로
    var again = new RelayServer(h.Config);
    True(!again.Switches.IsOn("launcher.catalog.read"), "still off after a restart");
    Equal(4L, again.Stats.PerDay(1).GetValueOrDefault("launcher.catalog.read")?.Sum(day => day.Calls) ?? 0);
    Equal(200, h.AdminJson("PUT", "/admin/api/functions/launcher.catalog.read", J("""{"on":true}""")).Status);
    Equal(200, h.Call("GET", "/launcher/catalog", key: false).Status, "on again");
});

Console.WriteLine();
if (passed + failed == 0)
{
    Console.WriteLine("No check ran (nothing matched: " + string.Join(" ", args) + ").");
    return 1;
}
Console.WriteLine(failed == 0 ? $"All {passed} checks passed." : $"{failed} of {passed + failed} checks FAILED.");
try { Directory.Delete(sandbox, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
return failed == 0 ? 0 : 1;

static JsonObject Merge(JsonObject source, string changes)
{
    var merged = (JsonObject)source.DeepClone();
    foreach (var (key, value) in JsonNode.Parse(changes)!.AsObject()) merged[key] = value?.DeepClone();
    return merged;
}
