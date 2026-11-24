using System.Diagnostics;
using System.Net;
using System.Text;

namespace IApi;

/// <summary>GitHub · GitHub Enterprise 링크와 git 자격 증명</summary>
internal static class GitHubLinks
{
    public const int SourceMax = 1_000_000;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>주소를 나눔 (Python urlsplit 처럼: 경로를 고치지 않음)</summary>
    public static (string Scheme, string Netloc, string Path, string Query) SplitUrl(string url)
    {
        string scheme = "", rest = url;
        int colon = url.IndexOf(':');
        if (colon > 0 && url[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') && char.IsAsciiLetter(url[0]))
        {
            scheme = url[..colon].ToLowerInvariant();
            rest = url[(colon + 1)..];
        }
        string netloc = "";
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            int end = rest.IndexOfAny(['/', '?', '#'], 2);
            netloc = end < 0 ? rest[2..] : rest[2..end];
            rest = end < 0 ? "" : rest[end..];
        }
        int hash = rest.IndexOf('#');
        if (hash >= 0) rest = rest[..hash];
        int question = rest.IndexOf('?');
        return question >= 0 ? (scheme, netloc, rest[..question], rest[(question + 1)..]) : (scheme, netloc, rest, "");
    }

    /// <summary>소유자/이름 (앱 목록의 저장소 주소가 hosts 의 GitHub 이면), 아니면 null</summary>
    public static string? RepositoryOf(string url, IEnumerable<string> hosts)
    {
        var (_, netloc, path, _) = SplitUrl(url);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!hosts.Contains(netloc.ToLowerInvariant()) || parts.Length < 2) return null;
        string name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return parts[0] + "/" + name;
    }

    /// <summary>
    /// 링크가 가리키는 지시를 읽는 API 요청 주소와 머리: 파일(blob · raw 링크), 폴더(tree 링크: 그 SKILL.md), 저장소(기본 브랜치의 SKILL.md).
    /// github.com 은 api.github.com, Enterprise 는 &lt;호스트&gt;/api/v3. 플러그인 링크는 .ts · .js 파일 그대로
    /// </summary>
    public static (string Url, Dictionary<string, string> Headers) Request(string link, string token = "", bool plugin = false)
    {
        var (scheme, host, rawPath, _) = SplitUrl((link ?? "").Trim());
        if (scheme is not ("http" or "https") || host.Length == 0) throw new RelayError(400, "깃허브 링크(https://…)를 넣으세요.");
        var parts = rawPath.Split('/').Where(part => part.Length > 0).Select(Util.Unquote).ToList();
        string owner, repo;
        string? reference = null;
        List<string> path;
        if (host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || host.StartsWith("raw.", StringComparison.OrdinalIgnoreCase))
        {
            // raw.githubusercontent.com/<소유자>/<저장소>/<ref>/<경로>, 또는 하위 도메인으로 나눈 Enterprise 의 raw.<호스트>/...
            if (parts.Count < 4) throw new RelayError(400, "깃허브 파일 링크가 아닙니다.");
            (owner, repo, reference, path) = (parts[0], parts[1], parts[2], parts.Skip(3).ToList());
            host = host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ? "github.com" : host[4..];
        }
        else if (parts.Count >= 5 && parts[0] == "raw")
        {
            // 하위 도메인 없는 Enterprise: <호스트>/raw/<소유자>/<저장소>/<ref>/<경로>
            (owner, repo, reference, path) = (parts[1], parts[2], parts[3], parts.Skip(4).ToList());
        }
        else if (parts.Count >= 2)
        {
            (owner, repo) = (parts[0], parts[1]);
            var rest = parts.Skip(2).ToList();
            if (rest.Count >= 2 && rest[0] is "blob" or "tree" or "raw")
            {
                reference = rest[1];
                path = rest.Skip(2).ToList();
            }
            else if (rest.Count == 0) path = [];
            else throw new RelayError(400, "깃허브 파일·폴더·저장소 링크가 아닙니다.");
        }
        else throw new RelayError(400, "깃허브 파일·폴더·저장소 링크가 아닙니다.");
        if (repo.EndsWith(".git", StringComparison.Ordinal)) repo = repo[..^4];
        string file = string.Join("/", path);
        if (plugin)
        {
            if (!Plugins.Extensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                throw new RelayError(400, "플러그인 파일(.ts 또는 .js)의 깃허브 링크를 넣으세요.");
        }
        else if (!new[] { ".md", ".markdown", ".txt" }.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            file = (file.Length > 0 ? file + "/" : "") + "SKILL.md";
        string api = host.ToLowerInvariant() is "github.com" or "www.github.com" ? "https://api.github.com" : $"{scheme}://{host}/api/v3";
        string url = $"{api}/repos/{Util.Quote(owner, "")}/{Util.Quote(repo, "")}/contents/{Util.Quote(file)}" + (reference is not null ? "?ref=" + Util.Quote(reference, "") : "");
        var headers = new Dictionary<string, string> { ["Accept"] = "application/vnd.github.raw", ["User-Agent"] = "i-api/" + Program.Version };
        if (token.Length > 0) headers["Authorization"] = "token " + token;
        return (url, headers);
    }

    /// <summary>링크가 가리키는 파일의 글</summary>
    public static string Fetch(string link, string token = "", bool plugin = false)
    {
        var (url, headers) = Request(link, token, plugin);
        byte[] data;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                string reason = (int)response.StatusCode switch
                {
                    401 => "토큰이 없거나 맞지 않습니다(설정 파일의 RELAY_GITHUB_TOKEN)",
                    403 => "접근이 막혔습니다(토큰 권한 확인)",
                    404 => "파일을 찾지 못했습니다(링크·브랜치, 비공개 저장소면 RELAY_GITHUB_TOKEN 확인)",
                    var code => $"응답 {code}",
                };
                throw new RelayError(400, $"깃허브에서 읽지 못했습니다: {reason}.");
            }
            using var stream = response.Content.ReadAsStream(timeout.Token);
            data = ReadLimited(stream, SourceMax + 1);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or InvalidOperationException)
        {
            throw new RelayError(400, $"깃허브에 연결하지 못했습니다: {(error is TaskCanceledException or OperationCanceledException ? "timed out" : error.Message)}.");
        }
        if (data.Length > SourceMax) throw new RelayError(400, "깃허브 파일이 너무 큽니다(1 MB까지).");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(Util.StripBom(data)); }
        catch (DecoderFallbackException) { throw new RelayError(400, "깃허브 파일이 UTF-8 텍스트가 아닙니다."); }
        if (text.Trim().Length == 0) throw new RelayError(400, "깃허브 파일이 비어 있습니다.");
        return text;
    }

    public static byte[] ReadLimited(Stream stream, int limit)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[65_536];
        while (memory.Length < limit)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - memory.Length));
            if (read <= 0) break;
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>이 PC 의 git 자격 증명이 host 에 쓰는 토큰 (저장소를 push 하는 계정). 보여 주지 않음</summary>
    public static string GitCredentialToken(string host)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("credential");
            start.ArgumentList.Add("fill");
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GCM_INTERACTIVE"] = "never";
            using var process = Process.Start(start)!;
            process.StandardInput.Write($"protocol=https\nhost={host}\n\n");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return "";
            }
            foreach (string line in output.Result.Split('\n'))
                if (line.StartsWith("password=", StringComparison.Ordinal)) return line["password=".Length..].Trim();
            _ = errors;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warning($"git credential for {host} not available: {error.Message}");
        }
        return "";
    }
}
