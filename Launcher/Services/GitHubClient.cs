using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ILauncher.Services;

public sealed record GitHubAsset(string Name, string ApiUrl, string BrowserUrl, long Size, string? Sha256);

public sealed record GitHubRelease(
    string Tag,
    string Title,
    ReleaseVersion? Version,
    bool Prerelease,
    DateTimeOffset? PublishedAt,
    string HtmlUrl,
    string Body,
    IReadOnlyList<GitHubAsset> Assets);

/// <summary>CanPush: 토큰으로 이 저장소에 쓸 수 있는지 (토큰 없이 읽으면 null). SizeKb: 저장소 크기</summary>
public sealed record GitHubRepositoryInfo(string Name, string FullName, string Description, string DefaultBranch, string HtmlUrl, bool Private,
    bool? CanPush = null, long SizeKb = 0);

public sealed record GitHubTreeEntry(string Path, bool IsFile, long Size);

public sealed record GitHubFile(string Content, string Sha);

/// <summary>GitHub API 실패. 상태 코드와 사용자에게 보일 문장</summary>
public sealed class GitHubApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
    public bool IsConflict => Status is 409 or 422;
    public bool IsAccessProblem => Status is 401 or 403 or 404;
}

/// <summary>
/// github.com 과 GitHub Enterprise 에 같은 방식으로 접근 (REST API v3).
/// 토큰은 저장소마다 tokenProvider 가 줌 (프로젝트 토큰 → 호스트 토큰). 공개 저장소는 토큰 없이도 동작.
/// </summary>
public sealed class GitHubClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<GitHubRepository, string?> _tokenProvider;
    private readonly string _userAgent, _userAgentVersion;
    private readonly HttpMessageHandler? _handler;

    public GitHubClient(Func<GitHubRepository, string?> tokenProvider, string userAgentVersion, HttpMessageHandler? handler = null)
    {
        _tokenProvider = tokenProvider;
        _userAgentVersion = userAgentVersion;
        _handler = handler;
        _userAgent = "i-luncher/" + userAgentVersion;
        // 리디렉션(릴리스 파일 → 저장소 서버)은 HttpClient 가 따라가며 Authorization 헤더는 넘기지 않음
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>모든 저장소를 이 토큰으로 읽는 클라이언트 (저장하기 전에 프로젝트 토큰을 확인할 때). 쓴 뒤 Dispose</summary>
    public GitHubClient WithToken(string token) => new(_ => token, _userAgentVersion, _handler);

    private static string Target(GitHubRepository repository) => repository.Host;

    private static string RepositoryUrl(GitHubRepository repository) => repository.ApiRepositoryUrl;

    private HttpRequestMessage Request(HttpMethod method, GitHubRepository repository, string url, string accept = "application/vnd.github+json")
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (_tokenProvider(repository) is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, GitHubRepository repository, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException(0, $"{Target(repository)}에 연결하지 못했습니다 (30초 초과).");
        }
        catch (HttpRequestException error)
        {
            throw new GitHubApiException(0, $"{Target(repository)}에 연결하지 못했습니다: {error.Message}");
        }
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateError(response, text, repository);
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { throw new GitHubApiException((int)response.StatusCode, $"{repository.Host}의 응답이 GitHub API 형식이 아닙니다. 주소를 확인하세요."); }
        }
    }

    private GitHubApiException CreateError(HttpResponseMessage response, string body, GitHubRepository repository)
    {
        int status = (int)response.StatusCode;
        string detail = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            // GitHub: {"message": "..."}, API 서버: "..." (JSON 문자열)
            if (document.RootElement.ValueKind == JsonValueKind.String) detail = document.RootElement.GetString() ?? "";
            else if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out var message)) detail = message.GetString() ?? "";
        }
        catch (JsonException) { }
        bool hasToken = !string.IsNullOrEmpty(_tokenProvider(repository));
        bool rateLimited = status is 403 or 429 &&
            (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0"
             || detail.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
        string text = status switch
        {
            401 => $"{repository.FullName} 토큰이 올바르지 않거나 만료되었습니다 (앱 우클릭 → 토큰…).",
            _ when rateLimited => hasToken
                ? $"{repository.Host} API 사용 한도를 넘었습니다. 잠시 후 다시 시도하세요."
                : $"{repository.Host} API 사용 한도(토큰 없이 시간당 60번)를 넘었습니다. 잠시 후 다시 시도하세요.",
            403 => hasToken ? $"{repository.FullName}에 대한 권한이 없습니다. 이 프로젝트의 토큰 권한을 확인하세요 (앱 우클릭 → 토큰…)."
                : $"{repository.FullName}에 접근하려면 이 프로젝트의 토큰이 필요합니다 (앱 우클릭 → 토큰…).",
            404 => hasToken ? $"{repository.FullName}을(를) 찾을 수 없거나 이 토큰으로 읽을 수 없습니다 (앱 우클릭 → 토큰…)."
                : $"{repository.FullName}을(를) 찾을 수 없습니다. 비공개 저장소면 이 프로젝트의 토큰을 넣으세요 (앱 우클릭 → 토큰…).",
            409 or 422 => "다른 사람이 먼저 목록을 바꿨습니다. 새로 고친 뒤 다시 시도하세요.",
            _ => $"{repository.Host} 응답 {status}" + (detail.Length > 0 ? $": {detail}" : "")
        };
        return new GitHubApiException(status, text);
    }

    /// <summary>설정에 넣을 토큰이 그 호스트에서 통하는지: 토큰 주인의 계정 이름 (GET /user). 틀리면 GitHubApiException</summary>
    public async Task<string> GetLoginAsync(string host, string token, CancellationToken cancellationToken = default)
    {
        var account = new GitHubRepository(GitHubRepository.NormalizeHost(host), "user", "token");
        using var request = new HttpRequestMessage(HttpMethod.Get, account.ApiBase + "/user");
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var document = await SendJsonAsync(request, account, cancellationToken).ConfigureAwait(false);
        return String(document.RootElement, "login") ?? "";
    }

    public async Task<GitHubRepositoryInfo> GetRepositoryAsync(GitHubRepository repository, CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(Request(HttpMethod.Get, repository, RepositoryUrl(repository)), repository, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new GitHubRepositoryInfo(
            String(root, "name") ?? repository.Name,
            String(root, "full_name") ?? repository.FullName,
            String(root, "description") ?? "",
            String(root, "default_branch") ?? "main",
            String(root, "html_url") ?? repository.WebUrl,
            root.TryGetProperty("private", out var isPrivate) && isPrivate.ValueKind == JsonValueKind.True,
            root.TryGetProperty("permissions", out var permissions) && permissions.TryGetProperty("push", out var push)
                ? push.ValueKind == JsonValueKind.True : null,
            root.TryGetProperty("size", out var size) && size.TryGetInt64(out long kb) ? kb : 0);
    }

    /// <summary>저장소 파일 목록 (Git Trees API). recursive 면 하위 폴더까지. 너무 큰 저장소는 GitHub 가 잘라서 줌 (Truncated)</summary>
    public async Task<(IReadOnlyList<GitHubTreeEntry> Entries, bool Truncated)> GetTreeAsync(GitHubRepository repository, string reference, bool recursive,
        CancellationToken cancellationToken = default)
    {
        string url = RepositoryUrl(repository) + "/git/trees/" + Uri.EscapeDataString(reference) + (recursive ? "?recursive=1" : "");
        using var document = await SendJsonAsync(Request(HttpMethod.Get, repository, url), repository, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var entries = new List<GitHubTreeEntry>();
        if (root.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Array)
            foreach (var item in tree.EnumerateArray())
            {
                string? path = String(item, "path");
                if (path is null) continue;
                entries.Add(new GitHubTreeEntry(path, String(item, "type") == "blob",
                    item.TryGetProperty("size", out var size) && size.TryGetInt64(out long length) ? length : 0));
            }
        return (entries, root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True);
    }

    /// <summary>저장소 파일의 원래 바이트 (Contents API raw). maxBytes 보다 크면 실패</summary>
    public async Task<byte[]> GetRawFileAsync(GitHubRepository repository, string path, string? reference, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        string url = ContentsUrl(repository, path) + (string.IsNullOrWhiteSpace(reference) ? "" : "?ref=" + Uri.EscapeDataString(reference!));
        using var request = Request(HttpMethod.Get, repository, url, "application/vnd.github.v3.raw");
        return await SendBytesAsync(request, repository, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 주소에서 바이트를 받음 (카탈로그에 아이콘 주소를 직접 적은 경우). 토큰은 그 주소가 저장소와 같은 호스트일 때만 붙임
    /// </summary>
    public async Task<byte[]> GetUrlBytesAsync(GitHubRepository repository, Uri url, long maxBytes, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        if (string.Equals(url.Authority, repository.Host, StringComparison.OrdinalIgnoreCase) && _tokenProvider(repository) is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendBytesAsync(request, repository, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> SendBytesAsync(HttpRequestMessage request, GitHubRepository repository, long maxBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException(0, $"{Target(repository)}에 연결하지 못했습니다 (30초 초과).");
        }
        catch (HttpRequestException error)
        {
            throw new GitHubApiException(0, $"{Target(repository)}에 연결하지 못했습니다: {error.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw CreateError(response, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), repository);
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("파일이 너무 큽니다.");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes) throw new InvalidDataException("파일이 너무 큽니다.");
            }
            return buffer.ToArray();
        }
    }

    /// <summary>최신 릴리스. 릴리스가 하나도 없으면 null. 프리릴리스 포함이면 버전이 가장 높은 것</summary>
    public async Task<GitHubRelease?> GetLatestReleaseAsync(GitHubRepository repository, bool includePrerelease, CancellationToken cancellationToken = default)
    {
        if (includePrerelease)
        {
            using var list = await SendJsonAsync(Request(HttpMethod.Get, repository, RepositoryUrl(repository) + "/releases?per_page=30"),
                repository, cancellationToken).ConfigureAwait(false);
            return list.RootElement.EnumerateArray()
                .Where(item => !(item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True))
                .Select(ReadRelease)
                .OrderByDescending(release => release.Version)
                .ThenByDescending(release => release.PublishedAt)
                .FirstOrDefault();
        }
        try
        {
            using var document = await SendJsonAsync(Request(HttpMethod.Get, repository, RepositoryUrl(repository) + "/releases/latest"),
                repository, cancellationToken).ConfigureAwait(false);
            return ReadRelease(document.RootElement);
        }
        catch (GitHubApiException error) when (error.Status == 404)
        {
            // GitHub 는 "릴리스 없음"과 "읽을 수 없는 저장소" 모두 404: 저장소를 읽을 수 있으면 릴리스가 없는 것
            await GetRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>처음 낸 릴리스의 날짜 (앱 페이지의 "최초 출시"). 초안은 빼고, 100개씩 5쪽까지 봄</summary>
    public async Task<DateTimeOffset?> GetFirstReleaseDateAsync(GitHubRepository repository, CancellationToken cancellationToken = default)
    {
        DateTimeOffset? first = null;
        for (int page = 1; page <= 5; page++)
        {
            using var list = await SendJsonAsync(Request(HttpMethod.Get, repository, RepositoryUrl(repository) + $"/releases?per_page=100&page={page}"),
                repository, cancellationToken).ConfigureAwait(false);
            int count = 0;
            foreach (var item in list.RootElement.EnumerateArray())
            {
                count++;
                if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                if (ReadRelease(item).PublishedAt is { } published && (first is null || published < first)) first = published;
            }
            if (count < 100) break;
        }
        return first;
    }

    private static GitHubRelease ReadRelease(JsonElement root)
    {
        string tag = String(root, "tag_name") ?? "";
        string title = String(root, "name") ?? "";
        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var asset in items.EnumerateArray())
            {
                string? digest = String(asset, "digest");
                assets.Add(new GitHubAsset(
                    String(asset, "name") ?? "",
                    String(asset, "url") ?? "",
                    String(asset, "browser_download_url") ?? "",
                    asset.TryGetProperty("size", out var size) && size.TryGetInt64(out long length) ? length : 0,
                    digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null));
            }
        DateTimeOffset? published = root.TryGetProperty("published_at", out var date) && date.ValueKind == JsonValueKind.String &&
            date.TryGetDateTimeOffset(out var value) ? value : null;
        return new GitHubRelease(tag, title, ReleaseVersion.Parse(tag) ?? ReleaseVersion.Parse(title),
            root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
            published, String(root, "html_url") ?? "", String(root, "body") ?? "", assets);
    }

    /// <summary>릴리스 파일을 받음. API 주소(Accept: octet-stream)라 비공개·내부(internal) 저장소에서도 받아짐. SHA-256 을 돌려줌</summary>
    public async Task<string> DownloadAssetAsync(GitHubRepository repository, GitHubAsset asset, string destination,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + ".part";
        string url = asset.ApiUrl.Length > 0 ? asset.ApiUrl : asset.BrowserUrl;
        using var request = Request(HttpMethod.Get, repository, url, "application/octet-stream");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new GitHubApiException(0, $"{asset.Name}을(를) 받지 못했습니다: {error.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw CreateError(response, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), repository);
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                // 멈춘 연결이 영원히 붙잡지 않게: 2분 동안 한 바이트도 오지 않으면 실패
                while ((read = await ReadWithStallTimeoutAsync(source, buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)received / total));
                }
                if (asset.Size > 0 && received != asset.Size)
                    throw new IOException($"{asset.Name}이(가) 끝까지 받아지지 않았습니다 ({received:N0}/{asset.Size:N0} 바이트).");
            }
            string sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (asset.Sha256 is { Length: 64 } expected && !string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partial);
                throw new InvalidDataException($"{asset.Name}의 SHA-256이 릴리스 정보와 다릅니다. 다시 시도하세요.");
            }
            File.Move(partial, destination, overwrite: true);
            return sha256;
        }
    }

    private static async Task<int> ReadWithStallTimeoutAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(TimeSpan.FromMinutes(2));
        try { return await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("다운로드가 2분 동안 멈춰 중단했습니다.");
        }
    }

    private string ContentsUrl(GitHubRepository repository, string path) =>
        RepositoryUrl(repository) + "/contents/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    /// <summary>저장소 파일 (Contents API). 파일이 없으면 null</summary>
    public async Task<GitHubFile?> GetFileAsync(GitHubRepository repository, string path, string? branch, CancellationToken cancellationToken = default)
    {
        string url = ContentsUrl(repository, path) + (string.IsNullOrWhiteSpace(branch) ? "" : "?ref=" + Uri.EscapeDataString(branch!));
        try
        {
            using var document = await SendJsonAsync(Request(HttpMethod.Get, repository, url), repository, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || String(root, "type") is not "file")
                throw new GitHubApiException(0, $"{path}은(는) 파일이 아닙니다.");
            string encoded = (String(root, "content") ?? "").Replace("\n", "").Replace("\r", "");
            string content = encoded.Length == 0 ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            if (content.Length > 0 && content[0] == '﻿') content = content[1..];
            return new GitHubFile(content, String(root, "sha") ?? "");
        }
        catch (GitHubApiException error) when (error.Status == 404)
        {
            // 파일이 없는 것인지 저장소를 못 읽는 것인지 구분
            await GetRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>저장소 폴더 안의 파일: 이름 → git blob sha (내용이 바뀌면 sha 도 바뀜). 폴더가 없으면 빈 목록</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetFolderFilesAsync(GitHubRepository repository, string path, string? branch,
        CancellationToken cancellationToken = default)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string url = ContentsUrl(repository, path) + (string.IsNullOrWhiteSpace(branch) ? "" : "?ref=" + Uri.EscapeDataString(branch!));
        try
        {
            using var document = await SendJsonAsync(Request(HttpMethod.Get, repository, url), repository, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return files;
            foreach (var item in document.RootElement.EnumerateArray())
                if (String(item, "type") is "file" && String(item, "name") is { Length: > 0 } name && String(item, "sha") is { Length: > 0 } sha)
                    files[name] = sha;
            return files;
        }
        catch (GitHubApiException error) when (error.Status == 404) { return files; }
    }

    /// <summary>저장소 파일을 만들거나 바꿈 (커밋 1개). sha 는 읽을 때 받은 값: 그 사이 바뀌었으면 409/422</summary>
    public async Task<string> PutFileAsync(GitHubRepository repository, string path, string content, string? sha, string message,
        string? branch, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, string> { ["message"] = message, ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) };
        if (!string.IsNullOrEmpty(sha)) body["sha"] = sha!;
        if (!string.IsNullOrWhiteSpace(branch)) body["branch"] = branch!;
        using var request = Request(HttpMethod.Put, repository, ContentsUrl(repository, path));
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var document = await SendJsonAsync(request, repository, cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("content", out var file) && file.ValueKind == JsonValueKind.Object
            ? String(file, "sha") ?? "" : "";
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
