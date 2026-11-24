using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ILauncher.Services;

/// <summary>
/// 빌드할 때 .env 에서 만들어져 i-luncher.exe 옆에 놓이는 launcher.env (KEY=VALUE).
/// Actions 러너에서 빌드하면 LAUNCHER_REPOSITORY 는 그 저장소로 채워짐. .env.sample 참고.
/// </summary>
public sealed class BuildSettings
{
    public const string FileName = "launcher.env";

    /// <summary>API 서버의 기본 주소 (예: http://<서버 IP>:2003): 마스터의 서버 목록 · 앱 사용 기록. 마스터가 설정 → API 서버에서 바꿀 수 있음</summary>
    public string ApiServer { get; init; } = "";
    public string LauncherRepository { get; init; } = "";
    public string CatalogRepository { get; init; } = "";
    public string CatalogPath { get; init; } = "";
    public string CatalogBranch { get; init; } = "";
    public string NoticesPath { get; init; } = "";
    /// <summary>공지 저장소 (NOTICES_REPOSITORY): 이름만이면 런처 저장소와 같은 호스트 · 소유자. 비우면 i-luncher-notices</summary>
    public string NoticesRepository { get; init; } = "";
    /// <summary>기본 목록 파일 (BASIC_APPS_PATH): 공지 저장소 안의 경로. 비우면 basic-apps.json (스크린샷은 그 옆 basic-media\)</summary>
    public string BasicAppsPath { get; init; } = "";
    public string DefaultHost { get; init; } = "";
    public string Token { get; init; } = "";
    /// <summary>마스터 계정 (MASTER_USERS, 쉼표로 Windows 계정 이름): 앱 목록의 "마스터만" 앱은 이 계정에만 보임. 비우면 제한 없음</summary>
    public IReadOnlyList<string> MasterUsers { get; init; } = [];
    /// <summary>일반 화면(홈)에 늘 보이는 기본 앱 id (GENERAL_APPS, 쉼표). 비우면 deskcal. 그 밖에는 이 PC 에 설치한 앱만 보임</summary>
    public IReadOnlyList<string> GeneralApps { get; init; } = [DefaultGeneralApp];
    public const string DefaultGeneralApp = "deskcal";

    /// <summary>목록이 비어 있으면 누구나. 계정은 도메인(DOMAIN\이름)을 떼고 대소문자 없이 비교</summary>
    public bool IsMaster(string account)
    {
        string name = account[(account.LastIndexOf('\\') + 1)..];
        return MasterUsers.Count == 0 || MasterUsers.Any(master => string.Equals(master, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>MASTER_USERS 에 이름이 있는 계정만 (목록이 비면 아무도 아님): 마스터 화면 (서버 목록)</summary>
    public bool IsListedMaster(string account)
    {
        string name = account[(account.LastIndexOf('\\') + 1)..];
        return MasterUsers.Any(master => string.Equals(master, name, StringComparison.OrdinalIgnoreCase));
    }

    public static BuildSettings Load(string? directory = null)
    {
        string path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;
                    string value = line[(split + 1)..].Trim();
                    if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
                    values[line[..split].Trim()] = value;
                }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        string Read(string key) => values.TryGetValue(key, out string? value) ? value : "";
        return new BuildSettings
        {
            ApiServer = Read("API_SERVER"),
            LauncherRepository = Read("LAUNCHER_REPOSITORY"),
            CatalogRepository = Read("CATALOG_REPOSITORY"),
            CatalogPath = Read("CATALOG_PATH"),
            CatalogBranch = Read("CATALOG_BRANCH"),
            NoticesPath = Read("NOTICES_PATH"),
            NoticesRepository = Read("NOTICES_REPOSITORY"),
            BasicAppsPath = Read("BASIC_APPS_PATH"),
            DefaultHost = Read("GITHUB_HOST"),
            Token = Read("LAUNCHER_TOKEN"),
            GeneralApps = Read("GENERAL_APPS").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } general
                ? general : [DefaultGeneralApp],
            MasterUsers = Read("MASTER_USERS").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(account => account[(account.LastIndexOf('\\') + 1)..]).Where(account => account.Length > 0).ToArray()
        };
    }
}

/// <summary>사용자별 설정 (C:\i-luncher\settings.json). 토큰은 현재 Windows 사용자만 풀 수 있게 DPAPI 로 보호</summary>
public sealed class UserSettings
{
    /// <summary>비우면 빌드 설정(launcher.env)의 값</summary>
    public string? CatalogRepository { get; set; }
    public string? CatalogPath { get; set; }
    public string? CatalogBranch { get; set; }
    /// <summary>true 면 저장소 대신 이 PC 의 목록(catalog.json)만 씀 (예전 설정, CatalogMode 가 우선)</summary>
    public bool UseLocalCatalog { get; set; }
    /// <summary>앱 목록 위치: api(API 서버) · github(GitHub 저장소 파일) · local(이 PC). 비우면 빌드 설정에 따라</summary>
    public string? CatalogMode { get; set; }
    public bool AutoUpdateLauncher { get; set; } = true;
    /// <summary>
    /// 마스터가 설정 → API 서버에서 정한 API 서버 주소. 비우면 빌드 설정(launcher.env)의 API_SERVER.
    /// 설치된 앱의 사용 기록(AppKit AppUsage)도 이 값을 읽음 (이름을 바꾸지 않음)
    /// </summary>
    public string? ApiServer { get; set; }
    /// <summary>마스터 계정이 제목 표시줄의 일반/마스터 전환으로 일반 화면을 고름 (일반 사용자와 똑같이: 기본 목록만, 서버에 묻지 않음)</summary>
    public bool GeneralView { get; set; }
    /// <summary>호스트 → DPAPI 로 보호한 토큰(base64)</summary>
    public Dictionary<string, string> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>마스터 PC 가 이 PC 의 git 자격 증명에서 자동으로 넣은 토큰의 호스트 (사용자가 설정에 직접 넣으면 빠짐)</summary>
    public List<string> AutoTokenHosts { get; set; } = [];
    /// <summary>프로젝트(저장소)마다의 토큰: "호스트/소유자/이름" → DPAPI 로 보호한 토큰(base64). 앱 우클릭 → 토큰… 으로 넣음</summary>
    public Dictionary<string, string> RepositoryTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("i-luncher/github-token/v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static UserSettings Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), JsonOptions) is { } settings)
            {
                settings.Tokens = new Dictionary<string, string>(settings.Tokens ?? [], StringComparer.OrdinalIgnoreCase);
                settings.AutoTokenHosts ??= [];
                settings.RepositoryTokens = new Dictionary<string, string>(settings.RepositoryTokens ?? [], StringComparer.OrdinalIgnoreCase);
                return settings;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new UserSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    public string? GetToken(string host)
    {
        if (!Tokens.TryGetValue(GitHubRepository.NormalizeHost(host), out string? protectedToken) || protectedToken.Length == 0) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>저장소 토큰의 열쇠 (AppKit LauncherToken 과 같음): "호스트/소유자/이름" 소문자</summary>
    public static string RepositoryKey(GitHubRepository repository) => $"{repository.TokenHost}/{repository.Owner}/{repository.Name}".ToLowerInvariant();

    /// <summary>이 프로젝트에 넣은 토큰 (없으면 null)</summary>
    public string? GetRepositoryToken(GitHubRepository repository) =>
        RepositoryTokens.TryGetValue(RepositoryKey(repository), out string? protectedToken) ? Unprotect(protectedToken) : null;

    /// <summary>이 프로젝트의 토큰을 넣거나 (null 이면) 지움</summary>
    public void SetRepositoryToken(GitHubRepository repository, string? token)
    {
        string key = RepositoryKey(repository);
        if (string.IsNullOrWhiteSpace(token)) { RepositoryTokens.Remove(key); return; }
        RepositoryTokens[key] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }

    private static string? Unprotect(string protectedToken)
    {
        if (protectedToken.Length == 0) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
    }

    /// <summary>호스트 전체에 쓰는 토큰 (마스터 PC 의 git 로그인 자동 토큰) (자동 토큰 표시도 지움)</summary>
    public void SetToken(string host, string? token)
    {
        string key = GitHubRepository.NormalizeHost(host);
        AutoTokenHosts.RemoveAll(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(token)) { Tokens.Remove(key); return; }
        Tokens[key] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }
}

/// <summary>빌드 설정과 사용자 설정을 합친 실제 값</summary>
public sealed class LauncherSettings
{
    public const string DefaultCatalogPath = "catalog/apps.json";
    public const string DefaultNoticesPath = "공지.txt";

    public LauncherSettings(BuildSettings build, UserSettings user, string dataDirectory, string? cacheDirectory = null)
    {
        Build = build;
        User = user;
        DataDirectory = dataDirectory;
        CacheDirectory = cacheDirectory ?? dataDirectory;
    }

    public BuildSettings Build { get; }
    public UserSettings User { get; private set; }
    /// <summary>백업해 새 PC 에서 다시 쓰는 것 (설정 · 즐겨찾기 · 내 앱 · 뺀 앱 · 고른 아이콘): C:\i-luncher</summary>
    public string DataDirectory { get; }
    /// <summary>지워도 다시 받는 것 (받아 둔 목록 · 아이콘 · 스크린샷 · 받은 파일 · 읽은 공지): %LOCALAPPDATA%\i-luncher</summary>
    public string CacheDirectory { get; }
    public string UserSettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>C:\i-luncher (만들 수 없는 PC 면 %LOCALAPPDATA%\i-luncher). DataFolder 참고</summary>
    public static string DefaultDataDirectory => DataFolder.Default;

    /// <summary>dataDirectory 를 주면(시험 · ILUNCHER_DATA_DIR) 캐시도 그 폴더, 아니면 C:\i-luncher + 캐시 %LOCALAPPDATA%\i-luncher</summary>
    public static LauncherSettings Load(string? dataDirectory = null, string? buildDirectory = null, string? cacheDirectory = null)
    {
        string data = dataDirectory ?? DefaultDataDirectory;
        string cache = cacheDirectory ?? (dataDirectory is null ? DataFolder.Cache : data);
        return new LauncherSettings(BuildSettings.Load(buildDirectory), UserSettings.Load(Path.Combine(data, "settings.json")), data, cache);
    }

    public void SaveUser() => User.Save(UserSettingsPath);

    public string? DefaultHost => Build.DefaultHost.Length > 0 ? GitHubRepository.NormalizeHost(Build.DefaultHost)
        : LauncherRepository?.Host;

    public GitHubRepository? LauncherRepository => GitHubRepository.Parse(Build.LauncherRepository);

    public const string ModeApi = "api", ModeGitHub = "github", ModeLocal = "local";

    /// <summary>API 서버 주소 (끝의 / 없음): 마스터가 설정 → API 서버에서 바꾼 주소, 없으면 빌드 설정(launcher.env)의 API_SERVER</summary>
    public string? ApiServerUrl => NormalizeServer(User.ApiServer) ?? BuildApiServerUrl;

    /// <summary>빌드 설정의 API_SERVER (마스터가 주소 칸을 비우면 이것)</summary>
    public string? BuildApiServerUrl => NormalizeServer(Build.ApiServer);

    public const int DefaultApiPort = 2003;

    /// <summary>
    /// API 서버 주소를 http(s)://호스트:포트 로 정리 (192.168.0.10:2003 → http://192.168.0.10:2003). 서버에 기본 포트가 없으므로 IP · 이름만 쓰면
    /// 포트를 함께 써야 함 (없으면 null). http(s):// 로 쓴 주소는 그대로 (포트가 없으면 80 · 443). 비었거나 틀리면 null
    /// </summary>
    public static string? NormalizeServer(string? text)
    {
        string value = (text ?? "").Trim().Trim('"');
        if (value.Length == 0) return null;
        bool bare = !value.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(bare ? "http://" + value : value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0) return null;
        string authority = value[(bare ? 0 : value.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        if (bare && !System.Text.RegularExpressions.Regex.IsMatch(authority, @":\d+$")) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>앱 목록 위치. 정하지 않았으면 API 서버가 있으면 api, 저장소가 있으면 github, 아니면 local</summary>
    public string CatalogMode
    {
        get
        {
            if (User.CatalogMode is ModeApi or ModeGitHub or ModeLocal) return User.CatalogMode == ModeApi && ApiServerUrl is null ? ModeLocal : User.CatalogMode;
            if (User.UseLocalCatalog) return ModeLocal;
            if (ApiServerUrl is not null) return ModeApi;
            return RepositoryCatalog is not null ? ModeGitHub : ModeLocal;
        }
    }

    /// <summary>앱 목록을 둔 저장소. 따로 정하지 않으면 런처 저장소</summary>
    public GitHubRepository? CatalogRepository => CatalogMode == ModeGitHub ? RepositoryCatalog : null;

    private GitHubRepository? RepositoryCatalog
    {
        get
        {
            string text = !string.IsNullOrWhiteSpace(User.CatalogRepository) ? User.CatalogRepository!
                : Build.CatalogRepository.Length > 0 ? Build.CatalogRepository : Build.LauncherRepository;
            return GitHubRepository.Parse(text, Build.DefaultHost.Length > 0 ? Build.DefaultHost : null);
        }
    }

    public string CatalogPath => Clean(User.CatalogPath) ?? Clean(Build.CatalogPath) ?? DefaultCatalogPath;
    public string? CatalogBranch => Clean(User.CatalogBranch) ?? Clean(Build.CatalogBranch);
    public string NoticesPath => Clean(Build.NoticesPath) ?? DefaultNoticesPath;

    public const string DefaultNoticesRepository = "i-luncher-notices";

    /// <summary>
    /// 런처 공지를 읽는 GitHub 저장소 (공개라 토큰 없이 모두 읽음): 빌드 설정 NOTICES_REPOSITORY, 비우면 i-luncher-notices.
    /// 이름만이면 런처 저장소와 같은 호스트 · 소유자. 런처 저장소를 모르면 null (이 PC 의 공지.txt)
    /// </summary>
    public GitHubRepository? NoticesRepository =>
        GitHubRepository.Parse(BasicApps.ResolveRepository(Clean(Build.NoticesRepository) ?? DefaultNoticesRepository, LauncherRepository), DefaultHost);

    /// <summary>
    /// 기본 목록(모든 런처의 스토어)을 읽는 GitHub 저장소: 공지 저장소와 같음. GitHub 에서 파일만 고치면 런처 릴리스 없이 반영.
    /// null 이면(런처 저장소가 없는 개발 빌드) 런처에 든 목록만
    /// </summary>
    public GitHubRepository? BasicAppsRepository => NoticesRepository;

    public string BasicAppsPath => Clean(Build.BasicAppsPath) ?? BasicApps.DefaultPath;

    /// <summary>사용자가 등록한 토큰이 먼저, 없으면 빌드에 들어간 읽기 전용 토큰 (런처 저장소와 같은 호스트일 때만)</summary>
    public string? GetToken(string host)
    {
        if (User.GetToken(host) is { Length: > 0 } token) return token;
        if (Build.Token.Length == 0) return null;
        string? buildHost = Build.DefaultHost.Length > 0 ? GitHubRepository.NormalizeHost(Build.DefaultHost) : LauncherRepository?.TokenHost;
        return buildHost is null || string.Equals(buildHost, GitHubRepository.NormalizeHost(host), StringComparison.OrdinalIgnoreCase)
            ? Build.Token : null;
    }

    public bool HasUserToken(string host) => User.GetToken(host) is { Length: > 0 };

    /// <summary>
    /// 이 저장소를 읽을 토큰: 그 프로젝트에 넣은 토큰 → 호스트 토큰(마스터 PC 의 git 로그인) → 빌드 설정 LAUNCHER_TOKEN (같은 호스트만)
    /// </summary>
    public string? GetToken(GitHubRepository repository) => User.GetRepositoryToken(repository) ?? GetToken(repository.TokenHost);

    /// <summary>
    /// 마스터 PC: 사용자가 설정에 토큰을 넣지 않았으면 이 PC 의 git 자격 증명(저장소를 push 하는 그 계정)을 그 호스트의 토큰으로 씀.
    /// 설정에 넣어 두므로 설치된 앱의 자체 업데이터도 같은 토큰으로 비공개 저장소를 읽음. 사용자가 직접 넣은 토큰은 건드리지 않음.
    /// 바뀌었으면 true (저장함)
    /// </summary>
    public bool AdoptGitCredential(Func<string, string?>? fill = null)
    {
        if (DefaultHost is not { Length: > 0 } host) return false;
        string key = GitHubRepository.NormalizeHost(host);
        bool auto = User.AutoTokenHosts.Any(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
        string? current = User.GetToken(key);
        if (current is { Length: > 0 } && !auto) return false;
        string? token = (fill ?? (name => GitCredential.Fill(name)))(key);
        if (token is not { Length: > 0 } || token == current) return false;
        User.SetToken(key, token);
        User.AutoTokenHosts.Add(key);
        SaveUser();
        return true;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('/');
}
