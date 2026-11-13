using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ILauncher.Models;

/// <summary>설치 방법: 릴리스 zip 을 런처가 풀어 설치(zip) 하거나, 릴리스의 설치 프로그램을 조용히 실행(setup)</summary>
public static class InstallKinds
{
    public const string Zip = "zip";
    public const string Setup = "setup";
}

/// <summary>
/// 카탈로그(catalog/apps.json)의 앱 한 개. 필수는 id·name·repository 뿐이고 나머지는 비우면 릴리스에서 자동으로 정함.
/// </summary>
public sealed partial class AppDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>영문 이름 (이름 옆에 작게 보임)</summary>
    public string? EnglishName { get; set; }
    /// <summary>GitHub 저장소 주소 (https://github.example.com/team/DeskCal)</summary>
    public string Repository { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>zip(기본) 또는 setup</summary>
    public string? Kind { get; set; }
    /// <summary>릴리스 zip 파일 이름. 비우면 win-x64 zip → 유일한 zip 순으로 고름. * 와일드카드 사용 가능</summary>
    public string? Asset { get; set; }
    /// <summary>설치 프로그램 파일 이름 (setup 방식, 또는 .NET 실행 환경이 없는 PC 에서 zip 대신 사용). 비우면 *Setup*.exe</summary>
    public string? SetupAsset { get; set; }
    /// <summary>설치 프로그램 인수. 비우면 /S</summary>
    public string? SetupArguments { get; set; }
    /// <summary>설치 폴더(%LOCALAPPDATA%\Programs\&lt;이름&gt;)와 Windows "앱" 목록 키 이름. 앱의 자체 설치 프로그램과 같아야 함. 비우면 Name</summary>
    public string? InstallName { get; set; }
    /// <summary>실행 파일 (설치 폴더 기준 상대 경로 또는 파일 이름). 비우면 패키지에서 찾음</summary>
    public string? Executable { get; set; }
    public string? Arguments { get; set; }
    /// <summary>카드 아이콘: 저장소 안 경로(Assets/App.png) 또는 https 주소. 비우면 저장소의 .csproj ApplicationIcon·Assets 에서 찾음</summary>
    public string? Icon { get; set; }
    /// <summary>프리릴리스도 최신 버전으로 볼지</summary>
    public bool IncludePrerelease { get; set; }
    public int Order { get; set; }
    /// <summary>빌드 설정 MASTER_USERS 의 계정에만 보임 (예: API 서버 관리 프로그램)</summary>
    public bool MasterOnly { get; set; }
    /// <summary>베타(정식 서비스 전): 카드에 "베타" 배지. 마스터가 API 서버 관리 프로그램의 앱 목록에서 정함</summary>
    public bool Beta { get; set; }
    /// <summary>새로 추가한 앱: 카드에 "NEW" 배지. 마스터가 API 서버 관리 프로그램의 앱 목록에서 켜고 끔</summary>
    public bool New { get; set; }

    [JsonIgnore]
    public string EffectiveKind => string.Equals(Kind, InstallKinds.Setup, StringComparison.OrdinalIgnoreCase) ? InstallKinds.Setup : InstallKinds.Zip;

    [JsonIgnore]
    public string EffectiveInstallName => SafeFolderName(string.IsNullOrWhiteSpace(InstallName) ? Name : InstallName!);

    public AppDefinition Clone() => (AppDefinition)MemberwiseClone();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonIdCharacters();

    /// <summary>이름이나 저장소 이름에서 id 를 만듦 (DeskCal → deskcal, Folder Compare → folder-compare)</summary>
    public static string CreateId(string text)
    {
        string id = NonIdCharacters().Replace(text.Trim().ToLowerInvariant(), "-").Trim('-');
        return id.Length == 0 ? "app" : id.Length > 64 ? id[..64].Trim('-') : id;
    }

    public static string SafeFolderName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new string(text.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return name.Length == 0 ? "App" : name;
    }

    /// <summary>저장 전에 확인. 문제가 있으면 사용자에게 보일 문장을 돌려줌</summary>
    public string? Validate(string? defaultHost)
    {
        if (string.IsNullOrWhiteSpace(Id)) return "앱 id가 비어 있습니다.";
        if (CreateId(Id) != Id) return "앱 id에는 영문 소문자·숫자·하이픈만 쓸 수 있습니다.";
        if (string.IsNullOrWhiteSpace(Name)) return "앱 이름을 입력하세요.";
        if (!Services.GitHubRepository.TryParse(Repository, out _, defaultHost)) return "GitHub 저장소 주소를 확인하세요. 예: https://github.example.com/team/App";
        if (Executable is { Length: > 0 } executable && (Path.IsPathRooted(executable) || executable.Split('\\', '/').Contains("..")))
            return "실행 파일은 설치 폴더 안의 상대 경로로 입력하세요.";
        return null;
    }
}

public sealed class AppCatalogDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<AppDefinition> Apps { get; set; } = [];
}
