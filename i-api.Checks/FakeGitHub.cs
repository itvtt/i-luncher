using System.Text;
using System.Text.Json.Nodes;

namespace IApi.Checks;

/// <summary>
/// 확인용 api.github.com: 계정, 저장소 둘, 릴리스와 파일 저장소의 그 파일, owner1/app 의 릴리스 워크플로(프로젝트 파일 · 최근 실행 · 태그), 워크플로 시작, 러너
/// (예전 test_relay 의 FakeGitHub). 이 서버의 HttpServer 로 돎
/// </summary>
internal sealed class FakeGitHub : IDisposable
{
    public readonly List<(string Path, string? Authorization, string? Accept)> Seen = [];
    public readonly List<(string Path, JsonNode? Body, string? Authorization)> Dispatched = [];
    public string RunStatus = "completed";
    public bool ChooseVersion = true;
    /// <summary>파일처럼 내려 주는 경로 (스킬 · 플러그인 링크): /api/v3/repos/… → 글</summary>
    public readonly Dictionary<string, string> Files = [];
    private readonly HttpServer _server;

    public int Port => _server.Port;
    public string Base => $"http://127.0.0.1:{Port}";

    public FakeGitHub()
    {
        _server = new HttpServer("127.0.0.1", 0, null, Handle);
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    private void Handle(Request request)
    {
        try { Route(request); }
        catch (Exception error) when (error is not IOException) { request.Error(500, error.Message); }
    }

    private static void Json(Request request, int status, JsonNode value) => request.Send(status, Encoding.UTF8.GetBytes(value.ToJsonString()), "application/json");

    private static void Raw(Request request, string text) => request.Send(200, Encoding.UTF8.GetBytes(text), "text/plain");

    private void Route(Request request)
    {
        string path = request.Target;
        string? authorization = request.Header("Authorization");
        bool authorized = authorization == "Bearer gh-token";
        lock (this)
        {
            if (request.Method == "GET") Seen.Add((path, authorization, request.Header("Accept")));
        }
        if (request.Method == "POST")
        {
            byte[] body = request.Header("Content-Length") is null && request.Header("Transfer-Encoding") is null ? [] : request.Body();
            JsonNode? value = body.Length > 0 ? JsonNode.Parse(body) : null;
            lock (this) Dispatched.Add((path, value, authorization));
            if (path == "/repos/owner1/app/actions/runners/registration-token" && authorized)
            {
                Json(request, 201, new JsonObject { ["token"] = "REG-TOKEN", ["expires_at"] = "2026-01-01T01:00:00Z" });
                return;
            }
            if (path == "/repos/owner1/app/actions/workflows/release.yml/dispatches")
            {
                if (!ChooseVersion && value is JsonObject sent && sent.ContainsKey("inputs"))
                {
                    Json(request, 422, new JsonObject { ["message"] = "Unexpected inputs provided: [\"version\"]" });
                    return;
                }
                request.Send(204, [], "application/json");
                return;
            }
            Json(request, 404, new JsonObject { ["message"] = "Not Found" });
            return;
        }
        if (request.Method == "DELETE")
        {
            lock (this) Dispatched.Add((path, null, authorization));
            if (path == "/repos/owner1/app/actions/runners/7" && authorized)
            {
                request.Send(204, [], "application/json");
                return;
            }
            Json(request, 404, new JsonObject { ["message"] = "Not Found" });
            return;
        }
        // 스킬 · 플러그인 링크가 읽는 파일 (토큰 없이)
        string plain = path.Split('?')[0];
        if (plain.StartsWith("/api/v3/", StringComparison.Ordinal))
        {
            if (Files.TryGetValue(Uri.UnescapeDataString(plain), out var file)) Raw(request, file);
            else Json(request, 404, new JsonObject { ["message"] = "Not Found" });
            return;
        }
        if (path == "/user")
        {
            Json(request, authorized ? 200 : 401, authorized ? new JsonObject { ["login"] = "Owner1" } : new JsonObject { ["message"] = "Bad credentials" });
            return;
        }
        if (path.StartsWith("/storage/", StringComparison.Ordinal))
        {
            if (authorization is not null)
            {
                Json(request, 400, new JsonObject { ["message"] = "Only one auth mechanism allowed" });
                return;
            }
            request.Send(200, "release-bytes"u8.ToArray(), "application/octet-stream");
            return;
        }
        if (!authorized)
        {
            Json(request, 404, new JsonObject { ["message"] = "Not Found" });
            return;
        }
        switch (path)
        {
            case "/repos/owner1/app" or "/repos/other/tool":
                Json(request, 200, new JsonObject { ["name"] = path.Split('/')[^1], ["full_name"] = path[7..], ["url"] = Base + path, ["default_branch"] = "main" });
                return;
            case "/repos/owner1/app/contents/.github/workflows/release.yml":
                Raw(request, "on:\n  workflow_dispatch:\n" + (ChooseVersion ? "REQUESTED_VERSION: ${{ github.event.inputs.version }}\n" : "") +
                             "run: build\\Get-NextVersion.ps1 -Project 'App\\App.csproj'\n" + "run: if ($env:OWNER1_ENV_PATH) { Copy-Item $env:OWNER1_ENV_PATH .env }\n");
                return;
            case "/repos/owner1/app/actions/runners?per_page=100":
                Json(request, 200, JsonNode.Parse("""{"total_count": 1, "runners": [{"id": 7, "name": "PC-app", "status": "online", "busy": false, "labels": [{"name": "self-hosted"}, {"name": "Windows"}]}]}""")!);
                return;
            case "/repos/owner1/app/contents/App/App.csproj":
                Raw(request, "<Project><PropertyGroup><Version>1.2.0</Version></PropertyGroup></Project>");
                return;
            case "/repos/owner1/app/actions/workflows/release.yml/runs?per_page=1":
                Json(request, 200, new JsonObject
                {
                    ["workflow_runs"] = new JsonArray(new JsonObject
                    {
                        ["status"] = RunStatus, ["conclusion"] = RunStatus == "completed" ? "success" : null, ["event"] = "push", ["created_at"] = "2026-01-01T00:00:00Z",
                        ["run_number"] = 7, ["html_url"] = "https://github.example.com/owner1/app/actions/runs/7",
                    }),
                });
                return;
            case "/repos/owner1/app/git/ref/tags/v1.2.3" or "/repos/owner1/app/git/ref/tags/v1.2.5":
                Json(request, 200, new JsonObject { ["ref"] = "refs/tags/" + path.Split('/')[^1] });
                return;
            case "/repos/owner1/app/releases/latest":
                Json(request, 200, new JsonObject
                {
                    ["tag_name"] = "v1.2.3",
                    ["assets"] = new JsonArray(new JsonObject { ["name"] = "App-win-x64.zip", ["size"] = 13, ["url"] = $"{Base}/repos/owner1/app/releases/assets/7" }),
                });
                return;
            case "/repos/owner1/app/releases/assets/7":
                if (request.Header("Accept") != "application/octet-stream")
                {
                    Json(request, 200, new JsonObject { ["name"] = "App-win-x64.zip" });
                    return;
                }
                request.Redirect($"{Base}/storage/app.zip?signature=x");
                return;
            case "/repos/owner1/app/contents/%EA%B3%B5%EC%A7%80.txt?ref=main":
                Raw(request, "공지");
                return;
        }
        Json(request, 404, new JsonObject { ["message"] = "Not Found" });
    }
}
