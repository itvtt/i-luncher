using System.Text.Json.Nodes;

namespace IApi;

/// <summary>
/// 서버의 모든 API (관리 창 → API 의 목록). 새 API 는 여기에 한 줄 더하고 처리기를 씀:
/// id(바꾸지 않음) · 묶음 · 방법 · 주소 · 이름 · 설명. 끄지 못하는 것(Switchable=false)은 관리 창이 쓰는 API 와 상태 확인뿐
/// </summary>
internal sealed partial class RelayServer
{
    private const string GroupServer = "서버", GroupLauncher = "i-luncher", GroupUsage = "앱 사용 기록",
        GroupSkills = "Terminal Notes 스킬", GroupPlugins = "Terminal Notes 플러그인", GroupAnnouncements = "Terminal Notes 공지", GroupGitHub = "GitHub 중계",
        GroupEquipment = "EQ Status", GroupPages = "관리 페이지", GroupAdmin = "관리 창";

    private List<ApiFunction> BuildFunctions()
    {
        ApiFunction Make(string id, string area, string group, string method, string pattern, string title, string description, Action<Request> handle, bool switchable = true) =>
            new() { Id = id, Area = area, Group = group, Method = method, Pattern = pattern, Title = title, Description = description, Handle = handle, Switchable = switchable };

        return
        [
            Make("health", "health", GroupServer, "GET", "/health", "상태 확인", "서버가 살아 있는지 (ok). 관리 창 · 시작 스크립트가 씀", request => request.Text(200, "ok"), switchable: false),

            // i-luncher (마스터 계정의 런처, 관리 창)
            Make("launcher.catalog.read", "launcher", GroupLauncher, "GET", "/launcher/catalog", "서버 앱 목록 읽기",
                "마스터 계정의 런처가 스토어에 보이는 서버 목록을 읽음 (ETag 가 같으면 304)", LauncherCatalog),
            Make("launcher.catalog.save", "launcher", GroupLauncher, "PUT", "/launcher/catalog", "서버 앱 목록 저장",
                "관리 창 → 앱 목록의 [저장] (관리자 비밀번호, 다른 곳에서 먼저 바꿨으면 409)", LauncherSave),
            Make("launcher.notices.read", "launcher", GroupLauncher, "GET", "/launcher/notices", "런처 공지 읽기 (예전)", "예전 런처의 공지. 지금 런처는 GitHub 공지 저장소를 읽음",
                request => request.Json(200, Launcher.Notices())),
            Make("launcher.notices.save", "launcher", GroupLauncher, "PUT", "/launcher/notices", "런처 공지 저장 (예전)", "예전 런처의 공지 저장 (관리자 비밀번호)", LauncherSave),
            Make("launcher.media.read", "launcher", GroupLauncher, "GET", "/launcher/media/{name}", "스크린샷 읽기", "앱 페이지의 스크린샷 (PNG · JPG, 이름 = 내용의 해시)", LauncherMedia),
            Make("launcher.media.upload", "launcher", GroupLauncher, "POST", "/launcher/media", "스크린샷 올리기", "관리 창 → 앱 목록의 스크린샷 추가 (관리자 비밀번호, 8 MB 까지)", LauncherUpload),
            Make("usage.report", "launcher", GroupUsage, "POST", "/launcher/usage", "사용 기록 보내기",
                "마스터 PC 의 런처 · 앱(AppKit AppUsage)이 사용 시간 · 실행 · 작업 수를 보냄", UsageReport),
            Make("usage.summary", "launcher", GroupUsage, "GET", "/launcher/usage", "사용 기록 읽기", "재무성과(ImPerformance)가 앱별 합계를 읽음 (from · to)", UsageSummary),

            // Terminal Notes (앱 키)
            Make("skills.list", "app", GroupSkills, "GET", "/api/skills", "스킬 목록", "스킬 스토어 목록 (비공개 스킬은 작성자에게만)",
                request => request.Json(200, new JsonArray(Skills.All(Viewer(request)).Select(skill => (JsonNode)skill).ToArray()))),
            Make("skills.source", "app", GroupSkills, "POST", "/api/skills/source", "스킬 링크 읽기", "공유 창이 깃허브 링크의 지시를 미리 읽음", SkillSource),
            Make("skills.get", "app", GroupSkills, "GET", "/api/skills/{name}", "스킬 받기", "최신 버전 (받은 수를 셈)",
                request => request.Json(200, Skills.Version(request.Value("name"), null, countDownload: true, viewer: Viewer(request)))),
            Make("skills.withdrawn", "app", GroupSkills, "POST", "/api/skills/withdrawn", "회수된 스킬 확인", "PC 에 적용한 스킬 중 더 쓸 수 없는 것 (다른 사람의 비공개)", SkillWithdrawn),
            Make("skills.private", "app", GroupSkills, "PUT", "/api/skills/{name}/private", "스킬 공개 범위", "작성자가 모두에게 보이거나 비공개로", SkillPrivate),
            Make("skills.delete", "app", GroupSkills, "DELETE", "/api/skills/{name}", "스킬 삭제", "작성자나 마스터 계정 (30일 동안 .trash 에 보관)", SkillDelete),
            Make("skills.versions", "app", GroupSkills, "GET", "/api/skills/{name}/versions", "스킬 버전 목록", "남긴 모든 버전, 최신부터",
                request => request.Json(200, Skills.Versions(request.Value("name"), Viewer(request)))),
            Make("skills.version", "app", GroupSkills, "GET", "/api/skills/{name}/versions/{version}", "스킬 버전 받기", "버전 하나 (?apply=1 이면 받은 수를 셈)", SkillVersion),
            Make("skills.publish", "app", GroupSkills, "POST", "/api/skills", "스킬 공유", "스킬이나 새 버전을 올림 (주소마다 시간당 제한)", SkillPublish),
            Make("plugins.source", "app", GroupPlugins, "POST", "/api/plugins/source", "플러그인 링크 읽기", "공유 창이 깃허브 링크의 .ts · .js 를 미리 읽음", PluginSource),
            Make("plugins.list", "app", GroupPlugins, "GET", "/api/plugins", "플러그인 목록", "OpenCode 플러그인 목록",
                request => request.Json(200, new JsonArray(Plugins.All().Select(plugin => (JsonNode)plugin).ToArray()))),
            Make("plugins.get", "app", GroupPlugins, "GET", "/api/plugins/{name}", "플러그인 받기", "코드 (?install=1 이면 설치 수를 셈)",
                request => request.Json(200, Plugins.Get(request.Value("name"), countDownload: request.Query.TryGetValue("install", out var install) && install is ["1"]))),
            Make("plugins.publish", "app", GroupPlugins, "POST", "/api/plugins", "플러그인 공유", "마스터 계정만 올림", PluginPublish),
            Make("plugins.delete", "app", GroupPlugins, "DELETE", "/api/plugins/{name}", "플러그인 삭제", "작성자나 마스터 계정", PluginDelete),
            Make("announcements.read", "app", GroupAnnouncements, "GET", "/api/announcements", "공지 읽기", "앱이 1분마다 확인 (ETag 가 같으면 304)", AnnouncementRead),
            Make("announcements.save", "app", GroupAnnouncements, "PUT", "/api/announcements", "공지 저장", "공지 작성 계정(MASTER_USERS)만, 다른 곳에서 먼저 고쳤으면 409", AnnouncementSave),

            Make("github.repos", "github", GroupGitHub, "GET", "/api/v3/repos/**", "저장소 읽기",
                "예전에 빌드한 앱 · 런처가 토큰 없이 비공개 저장소의 릴리스 · 파일을 받음 (열어 준 저장소만)", GitHubRelay),
            Make("eqstatus.status", "equipment", GroupEquipment, "GET", "/api/v1/equipment-status", "설비 상태", "EQ Status 의 설비 표 (설정의 EQUIPMENT_DB_*, 포트 2003 · 2004)", EquipmentRead),
            Make("pages", "page", GroupPages, "GET|POST", "/**", "브라우저 관리 페이지", "관리 창 → 서버 → ⋯ → 브라우저로 열기 (관리자 비밀번호)", AdminPage),

            // 관리 창 (관리자 비밀번호, 끄지 않음)
            Make("admin.status", "admin", GroupAdmin, "GET", "/admin/api/status", "서버 상태", "버전 · 실행 시간 · 설정 · 접속한 PC",
                request => request.Json(200, StatusData()), switchable: false),
            Make("admin.functions", "admin", GroupAdmin, "GET", "/admin/api/functions", "API 목록", "이 화면: API 마다 켜짐 · 호출 · 실패 · 걸린 시간", FunctionsList, switchable: false),
            Make("admin.functions.switch", "admin", GroupAdmin, "PUT", "/admin/api/functions/{id}", "API 켜고 끄기", "이 화면의 스위치", FunctionSwitch, switchable: false),
            Make("admin.custom.add", "admin", GroupAdmin, "POST", "/admin/api/custom", "사용자 API 더하기", "[+ API 추가]: 저장하면 바로 동작", CustomCreate, switchable: false),
            Make("admin.custom.change", "admin", GroupAdmin, "PUT|DELETE", "/admin/api/custom/{id}", "사용자 API 고치기 · 지우기", "사용자 API 의 [저장] · [삭제]", CustomChange, switchable: false),
            Make("admin.skills", "admin", GroupAdmin, "GET", "/admin/api/skills", "스킬 목록 (관리)", "",
                request => request.Json(200, new JsonArray(Skills.All().Select(skill => (JsonNode)skill).ToArray())), switchable: false),
            Make("admin.skill", "admin", GroupAdmin, "GET|DELETE", "/admin/api/skills/{name}", "스킬 읽기 · 지우기", "", AdminSkill, switchable: false),
            Make("admin.skill.version", "admin", GroupAdmin, "GET", "/admin/api/skills/{name}/versions/{version}", "스킬 버전 읽기", "", AdminSkillVersion, switchable: false),
            Make("admin.skill.restore", "admin", GroupAdmin, "POST", "/admin/api/skills/{name}/restore", "스킬 되돌리기", "", AdminSkillRestore, switchable: false),
            Make("admin.announcements", "admin", GroupAdmin, "GET|PUT", "/admin/api/announcements", "Terminal Notes 공지 (관리)", "", AdminAnnouncements, switchable: false),
            Make("admin.announcements.history", "admin", GroupAdmin, "GET", "/admin/api/announcements/history/{version}", "이전 공지", "",
                request => request.Json(200, new JsonObject { ["version"] = request.Value("version"), ["text"] = Announcements.VersionText(request.Value("version")) }), switchable: false),
            Make("admin.releases", "admin", GroupAdmin, "GET|POST", "/admin/api/releases", "릴리스", "목록과 릴리스 시작 (저장소의 release.yml)", AdminReleases, switchable: false),
            Make("admin.runners", "admin", GroupAdmin, "GET", "/admin/api/runners", "러너 목록", "", request => request.Json(200, Runners.List()), switchable: false),
            Make("admin.runners.template", "admin", GroupAdmin, "GET", "/admin/api/runners/template", "새 러너 채우기", "",
                request => request.Json(200, Runners.Template(request.QueryValue("repository") ?? "")), switchable: false),
            Make("admin.runners.add", "admin", GroupAdmin, "POST", "/admin/api/runners", "러너 추가", "서버 PC 에서만", RunnerAdd, switchable: false),
            Make("admin.runners.task", "admin", GroupAdmin, "POST", "/admin/api/runners/task", "로그온 작업 정리", "서버 PC 에서만", RunnerTask, switchable: false),
            Make("admin.runner", "admin", GroupAdmin, "PUT|DELETE", "/admin/api/runners/{id}", "러너 .env 저장 · 지우기", "서버 PC 에서만", RunnerChange, switchable: false),
            Make("admin.runner.control", "admin", GroupAdmin, "POST", "/admin/api/runners/{id}/{action}", "러너 시작 · 멈추기 · 다시 시작", "서버 PC 에서만", RunnerControl, switchable: false),
        ];
    }

    /// <summary>앱이 도는 Windows 계정 (비공개 스킬은 작성자에게만)</summary>
    private static string Viewer(Request request) => Util.ViewerName(request.Header("X-TerminalNotes-User"));

    private List<string> Masters => Config.SkillAdmins.Select(user => user.ToLowerInvariant()).ToList();

    // ── 관리 창 → API ──────────────────────────────────────────────────────────────────────────

    private JsonObject FunctionJson(ApiFunction function, List<(string Day, long Calls, long Failures, double Milliseconds)>? days, string today)
    {
        days ??= [];
        var todayEntry = days.FirstOrDefault(day => day.Day == today);
        long calls = days.Sum(day => day.Calls), failures = days.Sum(day => day.Failures);
        double milliseconds = days.Sum(day => day.Milliseconds);
        var last = Stats.LastCall(function.Id);
        var failure = Stats.LastFailure(function.Id);
        return new JsonObject
        {
            ["id"] = function.Id, ["group"] = function.Group, ["method"] = function.Method.Replace("|", " · "), ["path"] = function.Pattern,
            ["title"] = function.Title, ["description"] = function.Description, ["switchable"] = function.Switchable,
            ["on"] = !function.Switchable || Switches.IsOn(function.Id),
            ["today"] = new JsonObject
            {
                ["calls"] = todayEntry.Calls, ["failures"] = todayEntry.Failures,
                ["averageMs"] = todayEntry.Calls > 0 ? Math.Round(todayEntry.Milliseconds / todayEntry.Calls, 1) : 0,
            },
            ["period"] = new JsonObject { ["calls"] = calls, ["failures"] = failures, ["averageMs"] = calls > 0 ? Math.Round(milliseconds / calls, 1) : 0 },
            ["perDay"] = new JsonArray(days.Select(day => (JsonNode)new JsonObject { ["day"] = day.Day, ["calls"] = day.Calls, ["failures"] = day.Failures }).ToArray()),
            ["lastCall"] = last is { } moment ? Util.Iso(moment) : null,
            ["lastFailure"] = failure is { } failed ? new JsonObject { ["at"] = Util.Iso(failed.At), ["status"] = failed.Status, ["message"] = failed.Message } : null,
            // 사용자 API: 관리 창의 편집 칸
            ["custom"] = _customById.TryGetValue(function.Id, out var custom) ? custom.ToJson() : null,
        };
    }

    private void FunctionsList(Request request)
    {
        int days = (int)Math.Clamp(Util.AsInt(JsonValue.Create(request.QueryValue("days") ?? "30"), 30), 1, 365);
        var perDay = Stats.PerDay(days);
        string today = Util.LocalDay(Util.Now());
        request.Json(200, new JsonObject
        {
            ["since"] = Util.Iso(Started), ["days"] = days, ["today"] = today,
            ["functions"] = new JsonArray(Functions.Select(function => (JsonNode)FunctionJson(function, perDay.GetValueOrDefault(function.Id), today)).ToArray()),
        });
    }

    private void FunctionSwitch(Request request)
    {
        var function = Functions.FirstOrDefault(item => item.Id == request.Value("id")) ?? throw new RelayError(404, "그 API를 찾지 못했습니다.");
        var body = request.JsonObjectBody();
        if (!Util.IsBool(body["on"])) throw new RelayError(400, "on 은 true 나 false 입니다.");
        if (!function.Switchable) throw new RelayError(409, $"{function.Title}은(는) 끌 수 없습니다 (관리 창이 씀).");
        bool on = Util.IsTrue(body["on"]);
        Switches.Set(function.Id, on);
        Log.Info($"API {function.Id} {(on ? "켬" : "끔")} ({request.RemoteAddress})");
        int days = 30;
        request.Json(200, FunctionJson(function, Stats.PerDay(days).GetValueOrDefault(function.Id), Util.LocalDay(Util.Now())));
    }
}
