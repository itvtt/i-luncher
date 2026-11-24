using System.Collections;
using System.Text;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>설정 하나: 이름, 기본값, 설정 파일에서 그 줄 위에 붙는 설명</summary>
internal sealed record SettingDefault(string Key, string Value, params string[] Comment);

/// <summary>
/// 서버 설정 파일 settings.txt (i-api.exe 옆, 서버 PC 에서는 %LOCALAPPDATA%\i-api\settings.txt). 줄마다 이름=값, # 로 시작하는 줄은 설명.
/// 순서: 이 파일 &lt; 같은 이름의 환경 변수 &lt; --set. 서버가 시작할 때 없는 설정은 기본값과 설명을 덧붙임 (고친 값은 그대로).
/// 예전 relay.py 의 SETTINGS (맨 위 dict) 는 --settings-from 으로 옮김.
/// </summary>
internal static partial class Settings
{
    public const string FileName = "settings.txt";

    public static string DefaultFile => Path.Combine(AppContext.BaseDirectory, FileName);

    public static readonly SettingDefault[] Defaults =
    [
        new("RELAY_HOST", "", "이 서버 PC의 IP(앱 .env 의 RELAY_HOST 와 같게). 비우면 이 PC의 모든 IP에서 받습니다."),
        new("RELAY_PORT", "", "(필수) 이 서버가 받을 포트. 기본값은 없습니다: i-api.exe 를 실행하면 묻고 여기에 씁니다 (또는 i-api.exe --port <포트>)."),
        new("RELAY_BIND", "", "(선택) 받을 주소를 따로 정할 때. 0.0.0.0 = 이 PC의 모든 IP"),
        new("RELAY_INGEST_KEY", "", "(선택) 앱 키. 쓰면 앱 .env 의 RELAY_INGEST_KEY 와 같아야 받습니다."),
        new("RELAY_ADMIN_PASSWORD", "", "(선택) 관리자 비밀번호 (관리자 메뉴 · 관리 페이지). 비우면 누구나 고칩니다."),
        new("MASTER_USERS", "", "(선택) 마스터 계정(쉼표로, 앱 .env 의 MASTER_USERS 와 같게): 공지 작성, 모든 스킬 수정·삭제.",
            "비우면 앱이 정한 계정을 믿습니다. 작성자는 늘 자기 스킬을 고치고 지웁니다."),
        new("RELAY_GITHUB_TOKEN", "", "(선택) 비공개·내부 GitHub 저장소의 스킬 링크와 GitHub 중계(/api/v3)에 쓰는 토큰.",
            "비우면 GitHub 중계는 이 PC의 git 자격 증명(git credential)을 씁니다"),
        new("RELAY_GITHUB_SYNC_MINUTES", "10", "깃허브 링크 스킬을 다시 읽는 간격(분)"),
        new("LAUNCHER_GITHUB_REPOS", "", "(선택) GitHub 중계로 열어 줄 저장소(쉼표, 소유자/저장소 또는 소유자/*).",
            "비우면 런처 앱 목록에 있는 저장소와 토큰 계정의 저장소만"),
        new("LAUNCHER_DIR", "launcher", "i-luncher 앱 목록·공지 폴더(RELAY_DATA_ROOT 기준)"),
        new("RELAY_DATA_ROOT", "", "보고서·스킬·공지·앱 목록을 둘 폴더. 비우면 C:\\ProgramData\\i-api-data (이 PC의 모든 계정 공용)",
            "서버 폴더(i-api)와 다른 곳이라 그 폴더를 새로 복사하거나 지워도 남습니다"),
        new("RELAY_DATA_DIR", "reports", "폴더·파일은 RELAY_DATA_ROOT 기준 상대 경로 또는 절대 경로"),
        new("RELAY_SKILLS_DIR", "skills"),
        new("RELAY_PLUGINS_DIR", "plugins"),
        new("RELAY_ANNOUNCEMENTS_DIR", "announcements"),
        new("RELAY_BACKUP_DIR", "backups", "스킬·플러그인·공지·앱 목록 자동 백업(ZIP). 다른 드라이브의 절대 경로를 쓰면 데이터 폴더가 지워져도 남습니다"),
        new("RELAY_BACKUP_KEEP", "30", "남길 백업 수(시작할 때와 하루에 한 번, 바뀐 것이 있을 때만 만듦)"),
        new("RELAY_LOG_FILE", "relay.log"),
        new("RELAY_REPORT_RETENTION_DAYS", "90"),
        new("RELAY_REPORTS_MAX_MB", "2048"),
        new("RELAY_UPLOADS_PER_HOUR", "60", "주소당 시간당 보고·스킬 공유 수"),
        new("RELAY_WEBHOOK_URL", "", "(선택) 새 보고·스킬·공지 알림 웹훅"),
        new("RELAY_PUBLIC_URL", "", "(선택) 알림 링크의 주소"),
        new("RELAY_TLS_CERT", "", "(선택) HTTPS 인증서·키 파일 (PEM)"),
        new("RELAY_TLS_KEY", ""),
        new("EQUIPMENT_DB_HOST", "", "(선택) EQ Status 설비 상태 DB(MySQL 8) 주소. 비우면 /api/v1/equipment-status 를 열지 않습니다"),
        new("EQUIPMENT_DB_PORT", "3306"),
        new("EQUIPMENT_DB_USER", "", "읽기 권한만 있는 계정을 쓰세요"),
        new("EQUIPMENT_DB_PASSWORD", ""),
        new("EQUIPMENT_DB_NAME", "", "데이터베이스 이름"),
        new("EQUIPMENT_STATUS_TABLE", "", "설비 상태 표(열: line, prc, eqpid, status, Hr, Down_time, view, comment, sdwt)"),
        new("EQUIPMENT_API_KEY", "", "(선택) EQ Status 앱에 내장된 키(X-EQStatus-API-Key). 비우면 키 없이 받습니다"),
        new("EQUIPMENT_REFRESH_SECONDS", "10", "설비 상태 DB를 다시 읽는 간격(초)"),
        new("EQUIPMENT_PORT", "2004", "설비 상태를 내려 주는 포트(여기 · 사내 모두 2004, 이 포트는 설비 상태만). RELAY_PORT 에서도 계속 받음. 0 = 끔"),
        new("RUNNERS_ROOT", "", "(선택) 이 PC의 GitHub Actions 러너 폴더(러너마다 하위 폴더). 쓰면 i-luncher 관리자 메뉴의 러너 화면에서",
            "추가·편집·시작·중지·삭제하고, 로그온 작업 하나(GitHub Runners)가 켜 둔 러너를 시작합니다"),
        new("RUNNERS_ENV_DIR", "", "새 러너가 빌드에 쓰는 설정 파일(<저장소>.env) 폴더. 비우면 환경 변수 APPS_ENV_DIR, 없으면 RUNNERS_ROOT\\env"),
        new("RUNNER_PACKAGE", "", "새 러너 파일(actions-runner-win-x64-*.zip). 비우면 RUNNERS_ROOT 의 다른 러너 파일을 씁니다"),
    ];

    /// <summary>환경 변수에서 읽지 않음: 이 PC 의 러너 · 작업 스케줄러 설정이라 배포 환경의 값이 아님</summary>
    public static readonly string[] EnvironmentExcluded = ["RUNNERS_ROOT", "RUNNER_PACKAGE", "RUNNERS_ENV_DIR"];

    /// <summary>설정 목록에 없어도 읽는 키 (필요할 때 손으로 더함, 예: GitHub Enterprise API)</summary>
    public static readonly string[] EnvironmentExtra = ["RELAY_GITHUB_API", "RELAY_GITHUB_GIT_CREDENTIAL", "RELAY_GITHUB_SYNC_SECONDS"];

    private static readonly string[] Header =
    [
        "# i-api(API 서버) 설정. 이 파일만 고칩니다. 비운 값(이름=)은 쓰지 않거나 기본값입니다.",
        "# 고친 뒤 i-luncher 관리자 메뉴 → 서버 → [다시 시작] (또는 서버 시작.cmd).",
        "# 순서: 이 파일 < 같은 이름의 환경 변수 < --set. 새 버전이 더한 설정은 서버가 시작할 때 기본값으로 덧붙입니다.",
    ];

    [GeneratedRegex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$")]
    private static partial Regex Line();

    /// <summary>설정 파일의 값 (없으면 빈 목록). 값 양쪽의 같은 따옴표 한 쌍은 뺌</summary>
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;
        foreach (string raw in Util.ReadText(path).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith('#')) continue;
            var match = Line().Match(line);
            if (!match.Success) continue;
            values[match.Groups[1].Value] = Unquote(match.Groups[2].Value.Trim());
        }
        return values;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;

    /// <summary>앞뒤 공백 · 따옴표가 있는 값은 따옴표로 감쌈 (읽을 때 그대로 돌아오게)</summary>
    private static string Quote(string value) =>
        value.Length > 0 && (value != value.Trim() || value[0] is '"' or '\'') ? "\"" + value + "\"" : value;

    /// <summary>환경 변수에 있는 설정 키 (빈 값도: 배포 환경이 키를 비울 수 있게)</summary>
    public static Dictionary<string, string> FromEnvironment(IDictionary? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in Defaults.Select(setting => setting.Key).Concat(EnvironmentExtra))
            if (!EnvironmentExcluded.Contains(key) && environment.Contains(key))
                values[key] = (environment[key]?.ToString() ?? "").Trim();
        return values;
    }

    /// <summary>값들로 만든 설정 파일 전체 (설명 · 기본 순서, 목록에 없는 키는 맨 아래)</summary>
    public static string Compose(IReadOnlyDictionary<string, string> values)
    {
        var text = new StringBuilder();
        foreach (string line in Header) text.Append(line).Append("\r\n");
        foreach (var setting in Defaults)
        {
            text.Append("\r\n");
            foreach (string comment in setting.Comment) text.Append("# ").Append(comment).Append("\r\n");
            text.Append(setting.Key).Append('=').Append(Quote(values.TryGetValue(setting.Key, out var value) ? value : setting.Value)).Append("\r\n");
        }
        var extra = values.Keys.Where(key => Defaults.All(setting => setting.Key != key)).ToList();
        if (extra.Count > 0)
        {
            text.Append("\r\n# 손으로 더한 설정\r\n");
            foreach (string key in extra) text.Append(key).Append('=').Append(Quote(values[key])).Append("\r\n");
        }
        return text.ToString();
    }

    /// <summary>설정 하나를 파일에 씀 (그 줄을 바꾸고, 없으면 덧붙임). 다른 줄 · 설명은 그대로</summary>
    public static void SetValue(string path, string key, string value)
    {
        string text = File.Exists(path) ? Util.ReadText(path) : Compose(new Dictionary<string, string>());
        var pattern = new Regex("^" + Regex.Escape(key) + @"\s*=[^\r\n]*", RegexOptions.Multiline);
        string line = key + "=" + Quote(value);
        text = pattern.IsMatch(text) ? pattern.Replace(text, line.Replace("$", "$$"), 1) : text.TrimEnd('\r', '\n') + "\r\n" + line + "\r\n";
        Util.AtomicWrite(path, Util.Utf8.GetBytes(text));
    }

    /// <summary>파일에 없는 설정을 설명 · 기본값과 함께 덧붙인 글과 그 키들 (빠진 것이 없으면 그대로)</summary>
    public static (string Text, List<string> Added) WithNewSettings(string text)
    {
        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            var match = Line().Match(raw.TrimEnd('\r'));
            if (match.Success && !raw.TrimStart().StartsWith('#')) have.Add(match.Groups[1].Value);
        }
        var missing = Defaults.Where(setting => !have.Contains(setting.Key)).ToList();
        if (missing.Count == 0) return (text, []);
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var added = new StringBuilder(text.TrimEnd('\r', '\n'));
        if (added.Length == 0) foreach (string line in Header) added.Append(line).Append(newline);
        foreach (var setting in missing)
        {
            added.Append(newline).Append(newline);
            foreach (string comment in setting.Comment) added.Append("# ").Append(comment).Append(newline);
            added.Append(setting.Key).Append('=').Append(setting.Value);
        }
        return (added.Append(newline).ToString(), missing.Select(setting => setting.Key).ToList());
    }

    /// <summary>
    /// 서버가 시작할 때: 설정 파일이 없으면 기본값으로 만들고, 있으면 빠진 설정만 덧붙임. 쓸 수 없는 곳(컨테이너)이면 그냥 넘김
    /// </summary>
    public static List<string> EnsureComplete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Util.AtomicWrite(path, Util.Utf8.GetBytes(Compose(new Dictionary<string, string>())));
                return Defaults.Select(setting => setting.Key).ToList();
            }
            string text = Util.ReadText(path);
            var (merged, added) = WithNewSettings(text);
            if (added.Count > 0) Util.AtomicWrite(path, Util.Utf8.GetBytes(merged));
            return added;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    // ── 예전 relay.py 의 SETTINGS (Python dict: "KEY": "글자" 또는 숫자) ───────────────────────────────

    public const string RelaySettingsStart = "# ── 설정 시작 ──";
    public const string RelaySettingsEnd = "# ── 설정 끝 ──";

    [GeneratedRegex("\"(?<key>[A-Za-z0-9_]+)\"\\s*:\\s*(?:\"(?<text>(?:[^\"\\\\]|\\\\.)*)\"|'(?<single>(?:[^'\\\\]|\\\\.)*)'|(?<number>-?\\d+(?:\\.\\d+)?)|(?<constant>True|False|None))")]
    private static partial Regex RelayEntry();

    /// <summary>예전 relay.py 의 SETTINGS 값 (그 파일을 실행하지 않고 읽음). SETTINGS 부분이 없으면 ConfigError</summary>
    public static Dictionary<string, string> FromRelayScript(string text)
    {
        int start = text.IndexOf(RelaySettingsStart, StringComparison.Ordinal), end = text.IndexOf(RelaySettingsEnd, StringComparison.Ordinal);
        if (start < 0 || end < start) throw new ConfigError("SETTINGS 부분(설정 시작 ~ 설정 끝)을 찾지 못했습니다.");
        string block = text[start..end];
        int open = block.IndexOf('{'), close = block.LastIndexOf('}');
        if (open >= 0 && close > open) block = block[open..(close + 1)];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in block.Split('\n'))
        {
            if (raw.TrimStart().StartsWith('#')) continue;
            // 한 줄에 값 하나 (줄 끝의 # 설명은 정규식이 읽지 않음)
            var match = RelayEntry().Match(raw);
            if (match.Success)
            {
                string value = match.Groups["text"].Success ? PythonString(match.Groups["text"].Value)
                    : match.Groups["single"].Success ? PythonString(match.Groups["single"].Value)
                    : match.Groups["number"].Success ? match.Groups["number"].Value
                    : match.Groups["constant"].Value switch { "True" => "True", "False" => "False", _ => "None" };
                values[match.Groups["key"].Value] = value;
            }
        }
        return values;
    }

    private static string PythonString(string text) => Regex.Replace(text, @"\\(x[0-9A-Fa-f]{2}|u[0-9A-Fa-f]{4}|.)", escaped => escaped.Groups[1].Value switch
    {
        "n" => "\n", "t" => "\t", "r" => "\r", "\\" => "\\", "\"" => "\"", "'" => "'", "0" => "\0",
        var code when code.Length > 1 && (code[0] == 'x' || code[0] == 'u') => ((char)Convert.ToInt32(code[1..], 16)).ToString(),
        var other => "\\" + other,
    });
}
