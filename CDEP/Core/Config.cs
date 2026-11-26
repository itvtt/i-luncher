using System.Net;
using System.Net.Sockets;

namespace IApi;

/// <summary>명령 줄의 서버 설정 (--set 키=값, --port, --bind 등)</summary>
internal sealed class Options
{
    public List<string> Sets { get; } = [];
    public string? SettingsFile, Port, Bind, SkillsDir;
}

/// <summary>
/// 서버 설정: 설정 파일(settings.txt), 같은 이름의 환경 변수(컨테이너 · 배포 환경), 이번 실행의 --set 키=값 (시험 · 한 번만 다른 포트), 그다음 다른 옵션.
/// 예전 relay.py 의 Config 와 같은 규칙
/// </summary>
internal sealed class Config
{
    public const string DataFolder = "i-api-data";
    /// <summary>2.12–2.22 기본 데이터 폴더 (2.23 이 처음 시작할 때 DataFolder 로 이름을 바꿈)</summary>
    public const string RenamedDataFolder = "TerminalNotesRelayData";
    /// <summary>2.10–2.11 기본 데이터 폴더 (서버 폴더와 같은 이름)</summary>
    public const string OldDataFolder = "TerminalNotesRelay";
    public static readonly string[] GitHubHosts = ["github.com", "www.github.com"];

    /// <summary>확인 코드가 이 PC 의 실제 데이터 폴더를 건드리지 않게 바꿈</summary>
    internal static Func<string> DefaultDataRoot = () =>
    {
        if (OperatingSystem.IsWindows())
        {
            string machine = Environment.GetEnvironmentVariable("ProgramData") ?? Environment.GetEnvironmentVariable("ALLUSERSPROFILE") ?? "";
            if (machine.Length > 0) return Path.Combine(machine, DataFolder);
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            if (local.Length > 0) return Path.Combine(local, DataFolder);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", DataFolder);
    };

    /// <summary>2.10–2.11 의 기본 데이터 폴더 (그 스토어를 새 데이터 폴더로 복사해 옴)</summary>
    internal static Func<IReadOnlyList<string>> LegacyDataRoots = () =>
    {
        var roots = new List<string>();
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        if (local.Length > 0) roots.Add(Path.Combine(local, OldDataFolder));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", OldDataFolder));
        return roots;
    };

    /// <summary>서버 프로그램 폴더 (2.10 전에는 스토어가 이 옆에 있었음)</summary>
    internal static string ProgramFolder = AppContext.BaseDirectory;

    public string SettingsFile { get; }
    public IReadOnlyList<string> SetArgs { get; }
    public int Overrides => SetArgs.Count;
    public string DataRoot { get; }
    public string Host { get; }
    public int Port { get; set; }
    public string Bind { get; }
    public string BindSource { get; }
    public string IngestKey { get; set; }
    public string AdminPassword { get; set; }
    public string SkillsDir { get; }
    public string PluginsDir { get; }
    public string AnnouncementsDir { get; }
    public string BackupDir { get; }
    public int BackupKeep { get; }
    public int UploadsPerHour { get; set; }
    public string WebhookUrl { get; }
    public string PublicUrl { get; }
    public string TlsCert { get; }
    public string TlsKey { get; }
    public string LogFile { get; }
    public List<string> Editors { get; }
    public List<string> SkillAdmins { get; set; }
    public string GitHubToken { get; }
    public int GitHubSyncMinutes { get; }
    public int GitHubSyncSeconds { get; }
    public string LauncherDir { get; }
    /// <summary>[업데이트]가 새 버전을 받는 저장소 (RELAY_UPDATE_REPOSITORY, 없으면 이 i-api 를 릴리스한 저장소). 비면 모름</summary>
    public string UpdateRepository { get; }
    public string UsageDir { get; }
    public string ApiDir { get; }
    public List<string> GitHubProxyRepos { get; }
    public bool GitHubGitCredential { get; }
    public string GitHubApi { get; }
    public string GitHubWebHost { get; }
    public string[] GitHubHostsAll { get; }
    public string EquipmentDbHost { get; set; }
    public int EquipmentDbPort { get; set; }
    public string EquipmentDbUser { get; set; }
    public string EquipmentDbPassword { get; set; }
    public string EquipmentDbName { get; set; }
    public string EquipmentTable { get; set; }
    public string EquipmentApiKey { get; set; }
    public int EquipmentRefreshSeconds { get; set; }
    public int EquipmentPort { get; set; }
    public string? RunnersRoot { get; }
    public string? RunnersEnvDir { get; }
    public string? RunnerPackage { get; }

    /// <summary>릴리스 러너가 빌드할 때 넣은 저장소 주소 (https://호스트/소유자/저장소). PC 에서 빌드하면 비어 있음</summary>
    public static string BuiltUpdateRepository =>
        typeof(Config).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "UpdateRepository")?.Value ?? "";

    public Config(Options options, IReadOnlyDictionary<string, string>? fileValues = null, System.Collections.IDictionary? environment = null)
    {
        SettingsFile = Path.GetFullPath(options.SettingsFile ?? Settings.DefaultFile);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in Settings.Defaults) values[setting.Key] = setting.Value;
        foreach (var pair in fileValues ?? Settings.Read(SettingsFile)) values[pair.Key] = pair.Value;
        foreach (var pair in Settings.FromEnvironment(environment)) values[pair.Key] = pair.Value;
        // 로그인 자동 실행도 같은 --set 으로 돌림
        SetArgs = options.Sets.ToList();
        foreach (string item in SetArgs)
        {
            int equals = item.IndexOf('=');
            if (equals <= 0 || item[..equals].Trim().Length == 0) throw new ConfigError($"--set은 키=값으로 씁니다: {item}");
            values[item[..equals].Trim()] = item[(equals + 1)..].Trim();
        }

        string Text(string key, string fallback = "") => values.TryGetValue(key, out var value) && value.Trim().Length > 0 ? value.Trim() : fallback;
        int Number(string key, int fallback)
        {
            string text = Text(key, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int number)
                ? number : throw new ConfigError($"설정의 {key} 값은 숫자여야 합니다.");
        }

        // 서버가 남기는 것은 서버 폴더 밖에 둠: 그 폴더를 새로 복사하거나 지워도 스토어가 남게
        DataRoot = Path.GetFullPath(ExpandUser(Text("RELAY_DATA_ROOT") is { Length: > 0 } root ? root : DefaultDataRoot()));
        string Directory(string key, string fallback, string? option)
        {
            string value = ExpandUser(option ?? Text(key, fallback));
            return Path.IsPathRooted(value) && !IsDriveRelative(value) ? value : Path.GetFullPath(Path.Combine(DataRoot, value));
        }

        Host = Text("RELAY_HOST");
        // 포트는 기본값이 없음: 정하지 않았으면 0 (Validate 가 거절)
        Port = options.Port is { } port ? (int.TryParse(port, out int given) ? given : throw new ConfigError($"--port 는 숫자로 씁니다: {port}"))
            : Text("RELAY_PORT").Length > 0 ? Number("RELAY_PORT", 0) : 0;
        // 서비스의 IP 는 앱이 빌드된 주소인 RELAY_HOST: 서버는 그 주소로 받음. RELAY_BIND 가 대신함 (0.0.0.0: 이 PC 의 모든 주소)
        Bind = options.Bind ?? (Text("RELAY_BIND") is { Length: > 0 } bind ? bind : ListenAddress(Host));
        BindSource = options.Bind is not null ? "--bind" : Text("RELAY_BIND").Length > 0 ? "RELAY_BIND" : "RELAY_HOST";
        IngestKey = Text("RELAY_INGEST_KEY");
        AdminPassword = Text("RELAY_ADMIN_PASSWORD");
        SkillsDir = Directory("RELAY_SKILLS_DIR", "skills", options.SkillsDir);
        PluginsDir = Directory("RELAY_PLUGINS_DIR", "plugins", null);
        AnnouncementsDir = Directory("RELAY_ANNOUNCEMENTS_DIR", "announcements", null);
        BackupDir = Directory("RELAY_BACKUP_DIR", "backups", null);
        BackupKeep = Math.Max(1, Number("RELAY_BACKUP_KEEP", 30));
        // 주소 · 시간마다 보고와 스킬 공유를 따로 셈. 한 NAT · 프록시 뒤의 사무실은 주소를 나눠 쓰므로 넉넉하게
        UploadsPerHour = Number("RELAY_UPLOADS_PER_HOUR", 60);
        WebhookUrl = Text("RELAY_WEBHOOK_URL");
        PublicUrl = Text("RELAY_PUBLIC_URL").TrimEnd('/') is { Length: > 0 } publicUrl ? publicUrl : $"http://{(Host.Length > 0 ? Host : "localhost")}:{Port}";
        TlsCert = Text("RELAY_TLS_CERT");
        TlsKey = Text("RELAY_TLS_KEY");
        LogFile = Directory("RELAY_LOG_FILE", "relay.log", null);
        // 앱이 빌드된 목록과 같음. 서버가 공지를 저장할 때마다 다시 확인. 비우면 앱이 정함
        Editors = Util.AccountList(values.GetValueOrDefault("MASTER_USERS", ""));
        SkillAdmins = Util.AccountList(values.GetValueOrDefault("MASTER_USERS", ""));
        GitHubToken = Text("RELAY_GITHUB_TOKEN");
        GitHubSyncMinutes = Math.Max(1, Number("RELAY_GITHUB_SYNC_MINUTES", 10));
        GitHubSyncSeconds = Number("RELAY_GITHUB_SYNC_SECONDS", 0) is > 0 and var seconds ? seconds : GitHubSyncMinutes * 60;
        LauncherDir = Directory("LAUNCHER_DIR", "launcher", null);
        UpdateRepository = (Text("RELAY_UPDATE_REPOSITORY") is { Length: > 0 } update ? update : BuiltUpdateRepository).TrimEnd('/');
        UsageDir = Path.Combine(DataRoot, "usage");
        ApiDir = Path.Combine(DataRoot, "api");
        GitHubProxyRepos = Text("LAUNCHER_GITHUB_REPOS").Split(',').Select(item => item.Trim().ToLowerInvariant()).Where(item => item.Length > 0).ToList();
        GitHubGitCredential = Text("RELAY_GITHUB_GIT_CREDENTIAL", "1") != "0";
        GitHubApi = Text("RELAY_GITHUB_API", "https://api.github.com").TrimEnd('/');
        string apiHost = Uri.TryCreate(GitHubApi, UriKind.Absolute, out var api) ? api.Authority.ToLowerInvariant() : "";
        GitHubWebHost = apiHost is "api.github.com" or "" ? "github.com" : apiHost;
        GitHubHostsAll = GitHubHosts.Append(GitHubWebHost).Distinct().ToArray();
        EquipmentDbHost = Text("EQUIPMENT_DB_HOST");
        EquipmentDbPort = Number("EQUIPMENT_DB_PORT", 3306);
        EquipmentDbUser = Text("EQUIPMENT_DB_USER");
        EquipmentDbPassword = values.GetValueOrDefault("EQUIPMENT_DB_PASSWORD", "");
        EquipmentDbName = Text("EQUIPMENT_DB_NAME");
        EquipmentTable = Text("EQUIPMENT_STATUS_TABLE");
        EquipmentApiKey = Text("EQUIPMENT_API_KEY");
        EquipmentRefreshSeconds = Math.Max(1, Number("EQUIPMENT_REFRESH_SECONDS", 10));
        EquipmentPort = Number("EQUIPMENT_PORT", 2004);
        RunnersRoot = Text("RUNNERS_ROOT") is { Length: > 0 } runners ? ExpandUser(runners) : null;
        string envDir = Text("RUNNERS_ENV_DIR") is { Length: > 0 } dir ? dir : (Environment.GetEnvironmentVariable("APPS_ENV_DIR") ?? "").Trim();
        RunnersEnvDir = envDir.Length > 0 ? ExpandUser(envDir) : RunnersRoot is not null ? Path.Combine(RunnersRoot, "env") : null;
        RunnerPackage = Text("RUNNER_PACKAGE") is { Length: > 0 } package ? ExpandUser(package) : null;
    }

    private static bool IsDriveRelative(string path) => OperatingSystem.IsWindows() && (path.StartsWith('\\') || path.StartsWith('/')) && !path.StartsWith(@"\\");

    private static string ExpandUser(string path) =>
        path == "~" || path.StartsWith("~/") || path.StartsWith(@"~\") ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;

    /// <summary>RELAY_BIND 가 없을 때 받을 주소: RELAY_HOST 가 IPv4 면 그 주소 (localhost: 127.0.0.1), 아니면 이 PC 의 모든 주소</summary>
    public static string ListenAddress(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : IsIPv4(host) ? host : "0.0.0.0";

    public static bool IsIPv4(string value) => IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork && value.Count(c => c == '.') == 3;

    /// <summary>이 PC 가 그 주소로 받을 수 있음 (이 PC 의 IP)</summary>
    public static bool IsLocalAddress(string address)
    {
        try
        {
            if (!IPAddress.TryParse(address, out var ip))
                ip = Dns.GetHostAddresses(address).FirstOrDefault(found => found.AddressFamily == AddressFamily.InterNetwork);
            if (ip is null) return false;
            using var probe = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(ip, 0));
            return true;
        }
        catch (Exception error) when (error is SocketException or ArgumentException) { return false; }
    }

    /// <summary>이 PC 의 루프백이 아닌 IPv4 주소 (알림 글에)</summary>
    public static List<string> LocalAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName()).Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString()).Where(address => !address.StartsWith("127.", StringComparison.Ordinal))
                .Distinct().OrderBy(address => address, StringComparer.Ordinal).ToList();
        }
        catch (SocketException) { return []; }
    }

    public static bool IsLoopback(string bind) => bind is "localhost" or "::1" || bind.StartsWith("127.", StringComparison.Ordinal);

    /// <summary>필요한 것은 없음: 키가 없으면 IP 로 닿은 앱을 모두 받고, 비밀번호가 없으면 누구나 고치며, RELAY_HOST 가 없으면 이 PC 의 모든 IP 에서 받음</summary>
    public void Validate()
    {
        if (Port is < 1 or > 65535)
            throw new ConfigError("포트가 정해지지 않았습니다. i-api.exe 를 실행해 포트를 정하거나(또는 i-api.exe --port <포트>) 설정 파일의 RELAY_PORT 를 쓰세요.");
        if ((TlsCert.Length > 0) != (TlsKey.Length > 0)) throw new ConfigError("HTTPS를 쓰려면 RELAY_TLS_CERT와 RELAY_TLS_KEY를 함께 설정하세요.");
        if (Bind != "0.0.0.0" && !IsLocalAddress(Bind))
        {
            string here = LocalAddresses() is { Count: > 0 } addresses ? string.Join(", ", addresses) : "확인 못함";
            throw new ConfigError($"{BindSource}의 {Bind}은(는) 이 PC의 IP가 아닙니다(이 PC: {here}). " + (BindSource == "RELAY_HOST"
                ? "설정 파일의 RELAY_HOST를 이 PC의 IP로 고치고 앱 .env도 같게 해 다시 게시하세요. 이 PC의 모든 IP에서 받으려면 RELAY_BIND를 0.0.0.0으로 씁니다."
                : "이 PC의 IP나 0.0.0.0(모든 IP)을 쓰세요."));
        }
    }

    /// <summary>EQUIPMENT_PORT 를 엶: 설비 상태가 켜져 있고(EQUIPMENT_DB_HOST) 포트가 따로임</summary>
    public bool UsesEquipmentPort => EquipmentDbHost.Length > 0 && EquipmentPort > 0 && EquipmentPort != Port;

    /// <summary>스토어가 서버 프로그램 폴더 안: 그 폴더를 새로 복사하면 함께 바뀜</summary>
    public bool DataBesideCode => new[] { DataRoot, SkillsDir, PluginsDir, AnnouncementsDir }.Any(folder => Util.IsInside(folder, ProgramFolder));

    public string AddressText()
    {
        string here = LocalAddresses() is { Count: > 0 } addresses ? string.Join(", ", addresses) : "확인 못함";
        string listening = Bind == Host ? "이 IP로 받습니다" : Bind == "0.0.0.0" ? "이 PC의 모든 IP에서 받습니다" : $"받는 주소 {Bind}";
        if (Host.Length == 0) return $"서버 IP: RELAY_HOST가 비어 있습니다 — 앱 .env의 RELAY_HOST에 이 PC의 IP({here})를 쓰세요 · {listening}";
        string line = $"서버 IP: {(TlsCert.Length > 0 ? "https" : "http")}://{Host}:{Port} (RELAY_HOST) · {listening}";
        if (IsIPv4(Host) && !IsLocalAddress(Host)) line += $" — {Host}은(는) 이 PC의 IP가 아닙니다(이 PC: {here}). 앱은 이 주소로 접속합니다.";
        return line;
    }

    public List<string> Summary() =>
    [
        $"설정: 설정 파일 ({SettingsFile})" + (Overrides > 0 ? $" · 이번 실행 --set {Overrides}개" : ""),
        AddressText(),
        $"앱 키: {(IngestKey.Length > 0 ? "설정됨(앱의 RELAY_INGEST_KEY와 같아야 받음)" : "없음(IP로 들어온 앱 요청을 모두 받음)")}",
        $"관리 페이지: {(AdminPassword.Length > 0 ? "비밀번호 있음" : "비밀번호 없음(누구나 열 수 있음)")}",
        $"데이터 폴더: {DataRoot}  " + (DataBesideCode
            ? $"— 주의: 서버 폴더({ProgramFolder}) 안이라 그 폴더를 바꾸거나 지우면 스킬·공지가 함께 사라집니다. 설정의 RELAY_DATA_ROOT를 비우거나 다른 폴더로 쓰세요."
            : "(서버 폴더를 바꾸거나 지워도 남습니다)"),
        $"자동 백업: {BackupDir}  (스킬·플러그인·공지·앱 목록 ZIP, 시작할 때와 하루에 한 번 바뀐 것이 있으면, {BackupKeep}개 보관 · 복원: i-api.exe --restore-backup)",
        $"올리기 제한: 주소당 시간당 스킬 공유 {UploadsPerHour}건",
        $"스킬 폴더: {SkillsDir}",
        $"플러그인 폴더: {PluginsDir}  (올리기: 마스터 계정)",
        $"공지 폴더: {AnnouncementsDir}",
        $"마스터 계정(공지 작성·모든 스킬 수정·삭제): {(Editors.Count > 0 ? string.Join(", ", Editors) : "앱에서 정함")}",
        $"깃허브 연결 스킬: {(GitHubSyncSeconds / 60 is > 0 and var minutes ? minutes : GitHubSyncSeconds)}{(GitHubSyncSeconds >= 60 ? "분" : "초")}마다 확인" +
            $" · 토큰 {(GitHubToken.Length > 0 ? "있음" : "없음(공개 저장소만)")}",
        $"i-luncher 앱 목록·공지: {LauncherDir}  (읽기 /launcher/catalog, 고치기는 관리자 비밀번호)",
        "GitHub 중계(/api/v3): 토큰 " + (GitHubToken.Length > 0 ? "RELAY_GITHUB_TOKEN" : GitHubGitCredential ? "이 PC의 git 자격 증명" : "없음(공개 저장소만)") +
            $" · 저장소 {(GitHubProxyRepos.Count > 0 ? string.Join(", ", GitHubProxyRepos) : "앱 목록과 토큰 계정의 저장소")}",
        "EQ Status 설비 상태(/api/v1/equipment-status): " + (EquipmentDbHost.Length > 0
            ? $"{EquipmentDbHost}:{EquipmentDbPort}/{EquipmentDbName} · 표 {(EquipmentTable.Length > 0 ? EquipmentTable : "(없음)")} · {EquipmentRefreshSeconds}초마다 읽음 · " +
              $"앱 키 {(EquipmentApiKey.Length > 0 ? "확인" : "없음")} · 포트 {(UsesEquipmentPort ? EquipmentPort.ToString() : "(없음)")} (와 {Port})"
            : "꺼짐(설정의 EQUIPMENT_DB_HOST가 비어 있음)"),
        "GitHub 러너: " + (RunnersRoot is not null ? $"{RunnersRoot} · 설정 파일 {RunnersEnvDir} · 로그온 작업 {Runners.TaskName}" : "꺼짐(설정의 RUNNERS_ROOT가 비어 있음)"),
        $"알림 웹훅: {(WebhookUrl.Length > 0 ? "설정됨 · 링크 " + PublicUrl : "없음")}",
        $"로그: {LogFile}",
    ];

    public string HealthUrl
    {
        get
        {
            string host = Bind is "" or "0.0.0.0" ? "127.0.0.1" : Bind == "::" ? "::1" : Bind;
            return $"{(TlsCert.Length > 0 ? "https" : "http")}://{(host.Contains(':') ? "[" + host + "]" : host)}:{Port}/health";
        }
    }
}
