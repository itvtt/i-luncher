using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILauncher.Models;

namespace ILauncher.Services;

public sealed record CatalogSnapshot(IReadOnlyList<AppDefinition> Apps, bool FromCache, string? Warning);

/// <summary>앱 목록을 읽고 바꾸는 곳: GitHub 저장소의 파일(모두가 같은 목록) 또는 이 PC 의 파일</summary>
public interface ICatalogStore
{
    /// <summary>화면에 보일 위치 설명 (team/i-luncher · catalog/apps.json)</summary>
    string Location { get; }
    string? WebUrl { get; }
    Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    /// <summary>최신 목록을 읽어 change 를 적용한 뒤 저장. 그 사이 다른 사람이 바꿨으면 최신 목록에 다시 적용</summary>
    Task<IReadOnlyList<AppDefinition>> UpdateAsync(Func<List<AppDefinition>, string> change, CancellationToken cancellationToken = default);
}

public static class CatalogSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 한글 이름·설명을 \uXXXX 가 아닌 글자 그대로 저장 (GitHub 에서 사람이 읽고 고칠 수 있게)
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static List<AppDefinition> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        // { "apps": [...] } 와 [...] 둘 다 받음
        var apps = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.Deserialize<List<AppDefinition>>(Options)
            : document.RootElement.Deserialize<AppCatalogDocument>(Options)?.Apps;
        var result = new List<AppDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps ?? [])
        {
            if (app is null || string.IsNullOrWhiteSpace(app.Repository)) continue;
            if (string.IsNullOrWhiteSpace(app.Id)) app.Id = AppDefinition.CreateId(app.Name.Length > 0 ? app.Name : app.Repository.Split('/').Last());
            if (string.IsNullOrWhiteSpace(app.Name)) app.Name = app.Id;
            if (ids.Add(app.Id)) result.Add(app);
        }
        return result;
    }

    public static string Serialize(IEnumerable<AppDefinition> apps) =>
        JsonSerializer.Serialize(new AppCatalogDocument { Apps = apps.ToList() }, Options).Replace("\r\n", "\n") + "\n";
}

public sealed class LocalCatalogStore(string path) : ICatalogStore
{
    public string Location => "이 PC · " + path;
    public string? WebUrl => null;

    public Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var apps = File.Exists(path) ? CatalogSerializer.Parse(File.ReadAllText(path, Encoding.UTF8)) : [];
            return Task.FromResult(new CatalogSnapshot(apps, false, null));
        }
        catch (JsonException error)
        {
            return Task.FromResult(new CatalogSnapshot([], false, $"앱 목록 파일을 읽지 못했습니다: {error.Message}"));
        }
    }

    public async Task<IReadOnlyList<AppDefinition>> UpdateAsync(Func<List<AppDefinition>, string> change, CancellationToken cancellationToken = default)
    {
        var current = (await LoadAsync(cancellationToken).ConfigureAwait(false)).Apps.Select(app => app.Clone()).ToList();
        change(current);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, CatalogSerializer.Serialize(current), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        return current;
    }
}

/// <summary>
/// API 서버(&lt;서버&gt;/launcher/catalog)의 앱 목록. 누구나 읽고, 바꿀 때는 서버 관리자 비밀번호(Basic 인증).
/// 서버는 저장할 때 읽은 판(revision)을 확인하므로 그 사이 다른 사람이 바꿨으면 최신 목록에 다시 적용해 저장.
/// 마지막으로 읽은 목록을 캐시해 두어 서버가 꺼져 있어도 설치된 앱을 실행할 수 있음.
/// </summary>
public sealed class ApiCatalogStore : ICatalogStore, IDisposable
{
    private readonly string _server;
    private readonly Func<string?> _adminPassword;
    private readonly string _cachePath;
    private readonly HttpClient _http;

    public ApiCatalogStore(string server, Func<string?> adminPassword, string cachePath, HttpMessageHandler? handler = null)
    {
        _server = server.TrimEnd('/');
        _adminPassword = adminPassword;
        _cachePath = cachePath;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("i-luncher/" + ILuncher.AppKit.AppUpdater.CurrentVersion);
    }

    public string Location => "API 서버 · " + _server;
    public string? WebUrl => _server + "/apps";
    public string Server => _server;

    public void Dispose() => _http.Dispose();

    private async Task<(List<AppDefinition> Apps, string Revision, string Raw)> FetchAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await _http.GetAsync(_server + "/launcher/catalog", cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException(0, $"API 서버({_server})에 연결하지 못했습니다: {error.Message}");
        }
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new GitHubApiException((int)response.StatusCode, $"API 서버 응답 {(int)response.StatusCode}: {text.Trim().Trim('"')}");
            using var document = JsonDocument.Parse(text);
            string revision = document.RootElement.TryGetProperty("revision", out var value) ? value.GetString() ?? "" : "";
            return (CatalogSerializer.Parse(text), revision, text);
        }
    }

    public async Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (apps, _, raw) = await FetchAsync(cancellationToken).ConfigureAwait(false);
            TryWriteCache(raw);
            return new CatalogSnapshot(apps, false, null);
        }
        catch (Exception error) when (error is GitHubApiException or JsonException)
        {
            try
            {
                if (File.Exists(_cachePath))
                    return new CatalogSnapshot(CatalogSerializer.Parse(await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false)),
                        true, error.Message + " 마지막으로 받은 목록을 보여 줍니다.");
            }
            catch (Exception cacheError) when (cacheError is IOException or JsonException) { }
            return new CatalogSnapshot([], true, error.Message);
        }
    }

    public async Task<IReadOnlyList<AppDefinition>> UpdateAsync(Func<List<AppDefinition>, string> change, CancellationToken cancellationToken = default)
    {
        string password = _adminPassword() ?? "";
        for (int attempt = 0; ; attempt++)
        {
            var (apps, revision, _) = await FetchAsync(cancellationToken).ConfigureAwait(false);
            change(apps);
            string body = JsonSerializer.Serialize(new { apps, revision }, CatalogSerializer.Options);
            using var request = new HttpRequestMessage(HttpMethod.Put, _server + "/launcher/catalog")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + password)));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                TryWriteCache(text);
                return CatalogSerializer.Parse(text);
            }
            int status = (int)response.StatusCode;
            if (status == 409 && attempt < 2) continue;  // 그 사이 다른 사람이 저장함: 최신 목록에 다시 적용
            throw new GitHubApiException(status, status switch
            {
                401 or 429 => password.Length == 0
                    ? "앱 목록을 바꾸려면 설정에서 API 서버 관리자 비밀번호를 입력하세요."
                    : "API 서버 관리자 비밀번호가 맞지 않습니다. 설정에서 확인하세요.",
                409 => "다른 사람이 계속 목록을 바꾸고 있습니다. 새로 고친 뒤 다시 시도하세요.",
                _ => $"API 서버가 목록을 저장하지 않았습니다 ({status}): {text.Trim().Trim('"')}"
            });
        }
    }

    private void TryWriteCache(string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, content, new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// 저장소의 catalog/apps.json 을 Contents API 로 읽고 커밋으로 바꿈. 읽기는 읽기 토큰(또는 공개 저장소면 토큰 없이),
/// 바꾸기는 contents: write 권한이 있는 토큰이 필요. 마지막으로 읽은 목록을 캐시해 두어 오프라인에서도 앱을 실행할 수 있음.
/// </summary>
public sealed class GitHubCatalogStore(GitHubClient client, GitHubRepository repository, string path, string? branch, string cachePath) : ICatalogStore
{
    public string Location => $"{repository.FullName} · {path}" + (branch is null ? "" : $" ({branch})");
    public string? WebUrl => $"{repository.WebUrl}/blob/{Uri.EscapeDataString(branch ?? "HEAD")}/{path}";
    public GitHubRepository Repository => repository;

    public async Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            GitHubFile? file = await client.GetFileAsync(repository, path, branch, cancellationToken).ConfigureAwait(false);
            string content = file?.Content ?? "";
            var apps = CatalogSerializer.Parse(content);
            TryWriteCache(content);
            return new CatalogSnapshot(apps, false, file is null ? $"{repository.FullName}에 {path}이(가) 아직 없습니다. 앱을 추가하면 만들어집니다." : null);
        }
        catch (Exception error) when (error is GitHubApiException or JsonException or FormatException)
        {
            string reason = error is GitHubApiException ? error.Message : $"{path} 형식 오류: {error.Message}";
            try
            {
                if (File.Exists(cachePath))
                    return new CatalogSnapshot(CatalogSerializer.Parse(await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false)),
                        true, reason + " 마지막으로 받은 목록을 보여 줍니다.");
            }
            catch (Exception cacheError) when (cacheError is IOException or JsonException) { }
            return new CatalogSnapshot([], true, reason);
        }
    }

    public async Task<IReadOnlyList<AppDefinition>> UpdateAsync(Func<List<AppDefinition>, string> change, CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            GitHubFile? file = await client.GetFileAsync(repository, path, branch, cancellationToken).ConfigureAwait(false);
            var apps = CatalogSerializer.Parse(file?.Content ?? "");
            string message = change(apps);
            string content = CatalogSerializer.Serialize(apps);
            try
            {
                await client.PutFileAsync(repository, path, content, file?.Sha, message, branch, cancellationToken).ConfigureAwait(false);
                TryWriteCache(content);
                return apps;
            }
            catch (GitHubApiException error) when (error.IsConflict && attempt < 2)
            {
                // 다른 사람이 그 사이에 저장함: 최신 목록을 다시 읽어 같은 변경을 적용
            }
        }
    }

    private void TryWriteCache(string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, content, new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
