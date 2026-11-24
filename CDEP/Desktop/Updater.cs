using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;

namespace IApi.Manager;

/// <summary>
/// 관리 창의 [업데이트]: i-api 를 릴리스하는 저장소(i-luncher)의 최신 GitHub 릴리스에서 i-api-win-x64.zip 을 받아, 그 안 i-api.exe 가 더 새 버전이면
/// 이 폴더의 서버를 바꿈 (서버를 멈추고, 지금 파일은 backup\i-api-&lt;버전&gt;\, settings.txt · 데이터는 그대로). 저장소는 settings.txt 의 RELAY_UPDATE_REPOSITORY,
/// 없으면 이 i-api 를 빌드한 저장소 (릴리스 러너의 GITHUB_SERVER_URL/GITHUB_REPOSITORY). 공개 저장소면 토큰 없이, 아니면 RELAY_GITHUB_TOKEN 또는 이 PC 의 git 로그인
/// </summary>
internal static class Updater
{
    public const string AssetName = "i-api-win-x64.zip";

    /// <summary>받은 새 버전 (임시 폴더의 zip)</summary>
    internal sealed record Found(Version Version, string ZipFile);

    private static string BuiltRepository =>
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item => item.Key == "UpdateRepository")?.Value ?? "";

    /// <summary>업데이트를 받는 저장소 주소 (https://호스트/소유자/저장소). 모르면 ""</summary>
    public static string Repository => ServerManager.Setting("RELAY_UPDATE_REPOSITORY") is { Length: > 0 } chosen ? chosen.Trim().TrimEnd('/') : BuiltRepository.TrimEnd('/');

    public static Version Current => Version.TryParse(Program.Version, out var version) ? version : new Version(0, 0, 0);

    /// <summary>저장소 주소 → (API 주소, 소유자/저장소, 호스트). github.com 이면 api.github.com, 아니면 GitHub Enterprise 의 /api/v3</summary>
    internal static (string ApiBase, string FullName, string Host)? Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        string[] parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
        string name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        string api = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? "https://api.github.com" : $"{uri.Scheme}://{uri.Authority}/api/v3";
        return (api, parts[0] + "/" + name, uri.Host);
    }

    /// <summary>최신 릴리스에 더 새 i-api 가 있으면 받아 둔 zip, 같거나 낮으면 null. 실패는 InvalidOperationException (보여 줄 이유)</summary>
    public static async Task<Found?> CheckAsync(CancellationToken cancel = default)
    {
        if (Parse(Repository) is not { } target)
            throw new InvalidOperationException("업데이트를 받을 저장소를 모릅니다: settings.txt 의 RELAY_UPDATE_REPOSITORY 에 https://<호스트>/<소유자>/<저장소> 를 쓰세요.");
        var (apiBase, fullName, host) = target;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("i-api/" + Program.Version);
        string token = ServerManager.Setting("RELAY_GITHUB_TOKEN");
        bool triedCredential = false;
        async Task<HttpResponseMessage> Send(string url, string accept)
        {
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd(accept);
                if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
                // 비공개 저장소: 토큰이 없으면 이 PC 의 git 로그인으로 한 번 더
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && (token.Length == 0 && !triedCredential))
                {
                    triedCredential = true;
                    token = await Task.Run(() => GitHubLinks.GitCredentialToken(host), cancel);
                    if (token.Length > 0) { response.Dispose(); continue; }
                }
                return response;
            }
        }
        JsonObject release;
        using (var response = await Send($"{apiBase}/repos/{fullName}/releases/latest", "application/vnd.github+json"))
        {
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{fullName} 의 최신 릴리스를 읽지 못했습니다 ({(int)response.StatusCode}).");
            release = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel))?.AsObject() ?? throw new InvalidOperationException("릴리스 내용을 읽지 못했습니다.");
        }
        var asset = (release["assets"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => (string?)item["name"] == AssetName)
                    ?? throw new InvalidOperationException($"최신 릴리스 {(string?)release["tag_name"]} 에 {AssetName} 이 없습니다.");
        string zip = Path.Combine(Path.GetTempPath(), $"i-api-update-{Guid.NewGuid():N}.zip");
        using (var response = await Send((string)asset["url"]!, "application/octet-stream"))
        {
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{AssetName} 을 받지 못했습니다 ({(int)response.StatusCode}).");
            await using var file = File.Create(zip);
            await response.Content.CopyToAsync(file, cancel);
        }
        Version? version = null;
        string probe = Path.Combine(Path.GetTempPath(), $"i-api-update-{Guid.NewGuid():N}.exe");
        try
        {
            using (var archive = ZipFile.OpenRead(zip))
                (archive.GetEntry("i-api.exe") ?? throw new InvalidOperationException($"{AssetName} 안에 i-api.exe 가 없습니다.")).ExtractToFile(probe);
            version = ServerManager.ReadVersion(probe);
        }
        catch (InvalidDataException) { throw new InvalidOperationException($"{AssetName} 이 올바른 zip 이 아닙니다."); }
        finally { try { File.Delete(probe); } catch (IOException) { } }
        if (version is null || version <= Current)
        {
            try { File.Delete(zip); } catch (IOException) { }
            return null;
        }
        return new Found(version, zip);
    }

    /// <summary>
    /// 받은 zip 으로 이 폴더의 서버를 바꿈: 서버를 멈추고, 바뀌는 파일은 backup\i-api-&lt;지금 버전&gt;\ 로 옮긴 뒤 (돌고 있는 exe 도 이름은 바꿀 수 있음) 새 파일을 풂.
    /// settings.txt · 로그 · 데이터는 그대로. 서버는 부른 쪽이 다시 시작. 예전 파일을 둔 폴더
    /// </summary>
    public static string Apply(Found found)
    {
        string folder = ServerManager.Folder;
        if (Setup.StopRunning(ServerManager.Exe) > 0) Thread.Sleep(1000);
        string backup = Path.Combine(folder, "backup", $"i-api-{Current}");
        if (Directory.Exists(backup)) backup += "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        using (var archive = ZipFile.OpenRead(found.ZipFile))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Length == 0 || entry.FullName.Equals(Settings.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                if (!target.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    string kept = Path.Combine(backup, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                    File.Move(target, kept, overwrite: true);
                }
                entry.ExtractToFile(target);
            }
        }
        try { File.Delete(found.ZipFile); } catch (IOException) { }
        return backup;
    }

    /// <summary>새 i-api.exe 의 관리 창을 엶 (이 창이 닫힌 뒤: 창은 하나만)</summary>
    public static void OpenNewWindow()
    {
        var start = new ProcessStartInfo(ServerManager.Exe) { UseShellExecute = false, WorkingDirectory = ServerManager.Folder };
        start.ArgumentList.Add("--setup");
        Process.Start(start)?.Dispose();
    }
}
