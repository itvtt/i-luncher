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

    /// <summary>API 서버 (예: http://192.168.0.32:2003): 앱 목록·공지와 GitHub 중계</summary>
    public string ApiServer { get; init; } = "";
    public string LauncherRepository { get; init; } = "";
    public string CatalogRepository { get; init; } = "";
    public string CatalogPath { get; init; } = "";
    public string CatalogBranch { get; init; } = "";
    public string NoticesPath { get; init; } = "";
    public string DefaultHost { get; init; } = "";
    public string Token { get; init; } = "";

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
            DefaultHost = Read("GITHUB_HOST"),
            Token = Read("LAUNCHER_TOKEN")
        };
    }
}

/// <summary>사용자별 설정 (%LOCALAPPDATA%\i-luncher\settings.json). 토큰은 현재 Windows 사용자만 풀 수 있게 DPAPI 로 보호</summary>
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
    /// <summary>비우면 빌드 설정(launcher.env)의 API_SERVER</summary>
    public string? ApiServer { get; set; }
    /// <summary>API 서버 관리자 비밀번호 (DPAPI 로 보호한 base64). 앱 목록을 바꿀 때만 씀</summary>
    public string? AdminPassword { get; set; }
    public bool AutoUpdateLauncher { get; set; } = true;
    /// <summary>호스트 → DPAPI 로 보호한 토큰(base64)</summary>
    public Dictionary<string, string> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("i-luncher/github-token/v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static UserSettings Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), JsonOptions) is { } settings)
            {
                settings.Tokens = new Dictionary<string, string>(settings.Tokens ?? [], StringComparer.OrdinalIgnoreCase);
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

    public string? GetAdminPassword()
    {
        if (string.IsNullOrEmpty(AdminPassword)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(AdminPassword), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
    }

    public void SetAdminPassword(string? password) => AdminPassword = string.IsNullOrEmpty(password) ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

    public void SetToken(string host, string? token)
    {
        string key = GitHubRepository.NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(token)) { Tokens.Remove(key); return; }
        Tokens[key] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }
}

/// <summary>빌드 설정과 사용자 설정을 합친 실제 값</summary>
public sealed class LauncherSettings
{
    public const string DefaultCatalogPath = "catalog/apps.json";
    public const string DefaultNoticesPath = "공지.txt";

    public LauncherSettings(BuildSettings build, UserSettings user, string dataDirectory)
    {
        Build = build;
        User = user;
        DataDirectory = dataDirectory;
    }

    public BuildSettings Build { get; }
    public UserSettings User { get; private set; }
    public string DataDirectory { get; }
    public string UserSettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-luncher");

    public static LauncherSettings Load(string? dataDirectory = null, string? buildDirectory = null)
    {
        string data = dataDirectory ?? DefaultDataDirectory;
        return new LauncherSettings(BuildSettings.Load(buildDirectory), UserSettings.Load(Path.Combine(data, "settings.json")), data);
    }

    public void SaveUser() => User.Save(UserSettingsPath);

    public string? DefaultHost => Build.DefaultHost.Length > 0 ? GitHubRepository.NormalizeHost(Build.DefaultHost)
        : LauncherRepository?.Host;

    public GitHubRepository? LauncherRepository => GitHubRepository.Parse(Build.LauncherRepository);

    public const string ModeApi = "api", ModeGitHub = "github", ModeLocal = "local";

    /// <summary>API 서버 주소 (끝의 / 없음). 사용자 설정이 먼저, 없으면 빌드 설정</summary>
    public string? ApiServerUrl
    {
        get
        {
            string text = !string.IsNullOrWhiteSpace(User.ApiServer) ? User.ApiServer! : Build.ApiServer;
            return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.GetLeftPart(UriPartial.Authority) : null;
        }
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

    /// <summary>
    /// github.com 저장소를 API 서버의 GitHub 중계(&lt;API 서버&gt;/api/v3)로 읽을 때 그 주소. 중계를 쓰면 토큰이 필요 없고
    /// 사용자 PC 에 GitHub 토큰을 두지 않아도 비공개 저장소의 릴리스를 받음
    /// </summary>
    public string? ApiBaseFor(GitHubRepository repository) =>
        ApiServerUrl is { } server && repository.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? server + "/api/v3" : null;

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

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('/');
}
