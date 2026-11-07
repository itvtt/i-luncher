using System.Text.RegularExpressions;

namespace ILauncher.Services;

/// <summary>
/// GitHub 저장소 주소 하나에서 웹 주소와 REST API 주소를 정함.
/// github.com → https://api.github.com, GitHub Enterprise Server → https://&lt;호스트&gt;/api/v3,
/// GitHub Enterprise Cloud 데이터 상주(*.ghe.com) → https://api.&lt;호스트&gt;
/// </summary>
public sealed partial record GitHubRepository(string Host, string Owner, string Name, string Scheme = "https")
{
    public string WebUrl => $"{Scheme}://{Host}/{Owner}/{Name}";
    public string FullName => $"{Owner}/{Name}";

    public string ApiBase =>
        Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? "https://api.github.com"
        : Host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase) ? $"https://api.{Host}"
        : $"{Scheme}://{Host}/api/v3";

    public string ApiRepositoryUrl => $"{ApiBase}/repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Name)}";

    /// <summary>토큰을 고르는 기준: API 호스트가 아닌 웹 호스트 (github.com, ghe.example.com)</summary>
    public string TokenHost => Host.ToLowerInvariant();

    public override string ToString() => WebUrl;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^(?:ssh://)?git@(?<host>[^:/]+)[:/](?<path>.+)$")]
    private static partial Regex SshPattern();

    /// <summary>
    /// 받는 형식: https://host/owner/repo(.git)(/releases/...), http://host/owner/repo (http 서버),
    /// git@host:owner/repo.git, https://api.github.com/repos/owner/repo, https://host/api/v3/repos/owner/repo,
    /// 그리고 owner/repo (defaultHost 가 있을 때. 없으면 github.com)
    /// </summary>
    public static bool TryParse(string? text, out GitHubRepository repository, string? defaultHost = null)
    {
        repository = null!;
        string value = (text ?? "").Trim();
        if (value.Length == 0 || value.Length > 512) return false;

        string host, scheme = "https", path;
        Match ssh = SshPattern().Match(value);
        if (ssh.Success)
        {
            host = ssh.Groups["host"].Value;
            path = ssh.Groups["path"].Value;
        }
        else if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                return false;
            if (!string.IsNullOrEmpty(uri.UserInfo)) return false;  // 주소에 토큰을 넣지 않음
            scheme = uri.Scheme;
            host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            host = string.IsNullOrWhiteSpace(defaultHost) ? "github.com" : NormalizeHost(defaultHost!);
            path = value;
            // "ghe.example.com/owner/repo" 처럼 스킴 없이 붙여 넣은 주소
            string first = value.Split('/')[0];
            if (first.Contains('.') && value.Count(c => c == '/') >= 2)
            {
                host = first;
                path = value[(first.Length + 1)..];
            }
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        host = host.ToLowerInvariant();
        if (host is "www.github.com") host = "github.com";
        // API 주소를 붙여 넣은 경우
        if (host == "api.github.com" && segments.Length >= 3 && segments[0] == "repos")
        {
            host = "github.com";
            segments = segments[1..];
        }
        else if (host.StartsWith("api.", StringComparison.Ordinal) && host.EndsWith(".ghe.com", StringComparison.Ordinal) && segments.Length >= 3 && segments[0] == "repos")
        {
            host = host[4..];
            segments = segments[1..];
        }
        else if (segments.Length >= 5 && segments[0] == "api" && segments[1] == "v3" && segments[2] == "repos")
        {
            segments = segments[3..];
        }
        if (segments.Length < 2) return false;

        string owner = segments[0];
        string name = segments[1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!OwnerPattern().IsMatch(owner) || !NamePattern().IsMatch(name) || name is "." or "..") return false;
        if (host.Length == 0 || host.Any(char.IsWhiteSpace)) return false;
        repository = new GitHubRepository(host, owner, name, scheme);
        return true;
    }

    public static GitHubRepository? Parse(string? text, string? defaultHost = null) =>
        TryParse(text, out var repository, defaultHost) ? repository : null;

    public static string NormalizeHost(string host)
    {
        string value = host.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            value = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        value = value.TrimEnd('/').ToLowerInvariant();
        return value == "www.github.com" ? "github.com" : value;
    }

    public bool SameAs(GitHubRepository? other) => other is not null &&
        string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
}
