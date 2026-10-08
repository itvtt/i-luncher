using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TerminalNotes.Services;

public sealed record ExternalTerminalLaunch(string Directory, string SessionId, ProcessStartInfo StartInfo)
{
    public string Project { get; init; } = string.Empty;
    public Uri? Endpoint { get; init; }
    public int Port { get; init; }
}

/// <summary>Starts a standalone CMD/OpenCode process. No ConPTY job or app lifetime owns it.</summary>
public static class ExternalTerminalLauncher
{
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private const int MaximumSessionResponseBytes = 256 * 1024;
    private static string Root => Path.Combine(OpenCodeProjectContext.DataDirectory, "CmdSessions");
    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string ProjectFolder(string project) => Path.Combine(Root, Key(Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)).ToUpperInvariant()));
    private static string LeasePath(string project, string session) => Path.Combine(ProjectFolder(project), Key(session) + ".lock");

    public static bool IsProjectActive(string project) => System.IO.Directory.Exists(ProjectFolder(project)) &&
        System.IO.Directory.EnumerateFiles(ProjectFolder(project), "*.lock").Any(IsLocked);
    public static bool IsSessionActive(string project, string session) => IsLocked(LeasePath(project, session));
    private static bool IsLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static async Task<string> ResolveRootSessionAsync(Uri endpoint, string project, string candidate, CancellationToken token = default)
    {
        if (!endpoint.IsAbsoluteUri || !endpoint.IsLoopback || endpoint.Scheme != "http" || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidOperationException("로컬 OpenCode 세션만 이어 열 수 있습니다.");
        using var client = CreateHttpClient(TimeSpan.FromSeconds(8));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int depth = 0; depth < 16 && seen.Add(candidate); depth++)
        {
            ValidateSession(candidate);
            var uri = new Uri(endpoint, "/session/" + Uri.EscapeDataString(candidate) + "?directory=" + Uri.EscapeDataString(project));
            using var response = await client.GetAsync(uri, token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = json.RootElement;
            if (root.GetProperty("id").GetString() != candidate || root.GetProperty("directory").GetString() is not { } directory ||
                !DirectoriesEqual(directory, project))
                throw new InvalidOperationException("현재 프로젝트와 세션의 연결을 확인하지 못했습니다.");
            if (!root.TryGetProperty("parentID", out var parent) || parent.ValueKind == JsonValueKind.Null) return candidate;
            candidate = parent.GetString() ?? throw new InvalidDataException("세션 정보가 올바르지 않습니다.");
        }
        throw new InvalidDataException("이어 열 원본 세션을 확인하지 못했습니다.");
    }

    public static ExternalTerminalLaunch Prepare(string command, string project, string session, string? plan, string title, bool autoMode)
    {
        ValidateSession(session);
        project = Path.GetFullPath(project);
        if (!System.IO.Directory.Exists(project) || !File.Exists(command)) throw new FileNotFoundException("프로젝트 또는 OpenCode 실행 파일을 찾지 못했습니다.");
        if (IsSessionActive(project, session)) throw new InvalidOperationException("이 세션은 이미 CMD에서 열려 있습니다.");
        int port = ReserveLoopbackPort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        string folder = Path.Combine(ProjectFolder(project), Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        string contextPath = Path.Combine(folder, "instructions.md");
        using var context = new OpenCodeProjectContext(project, plan, title);
        File.Copy(context.InstructionFilePath, contextPath);
        var config = JsonNode.Parse(context.CreateProcessConfigContent(Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT")))!.AsObject();
        var instructions = config["instructions"]!.AsArray();
        for (int i = 0; i < instructions.Count; i++)
            if (instructions[i]?.GetValue<string>() == context.InstructionFilePath) instructions[i] = contextPath;
        File.WriteAllText(Path.Combine(folder, "launch.json"), JsonSerializer.Serialize(new
        {
            Command = Path.GetFullPath(command), Project = project, Session = session, Title = title,
            Config = config.ToJsonString(), AutoMode = autoMode, Lease = LeasePath(project, session), Port = port
        }), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "launch.ps1"), Script, new UTF8Encoding(true));
        string invocation = "& '" + Path.Combine(folder, "launch.ps1").Replace("'", "''") + "'";
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = true, WorkingDirectory = project, WindowStyle = ProcessWindowStyle.Normal,
            // Only fixed tokens and Base64 enter cmd.exe's parser. Paths/titles/config never become shell code.
            Arguments = "/d /v:off /k powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(invocation))
        };
        return new(folder, session, start) { Project = project, Endpoint = endpoint, Port = port };
    }

    public static async Task LaunchAsync(ExternalTerminalLaunch launch, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        launch = RestoreMetadata(launch);
        if (string.IsNullOrWhiteSpace(launch.Project) || launch.Endpoint is not { IsLoopback: true, Scheme: "http" } endpoint ||
            launch.Port is < 1 or > 65535 || endpoint.Port != launch.Port)
            throw new InvalidOperationException("CMD launch에는 프로젝트와 loopback 시작 확인 주소가 필요합니다.");
        TimeSpan startupTimeout = timeout ?? DefaultStartupTimeout;
        if (startupTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var timeoutSource = new CancellationTokenSource(startupTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Process? process = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            process = Process.Start(launch.StartInfo) ?? throw new IOException("CMD 창을 열지 못했습니다.");
            await File.WriteAllTextAsync(Path.Combine(launch.Directory, "cmd.pid"), process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            using var client = CreateHttpClient();
            Uri sessionUri = new(endpoint, "/session/" + Uri.EscapeDataString(launch.SessionId) +
                "?directory=" + Uri.EscapeDataString(launch.Project));
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                ThrowIfLaunchEnded(launch, process);
                bool started = File.Exists(Path.Combine(launch.Directory, "started"));
                if (started && IsSessionActive(launch.Project, launch.SessionId))
                {
                    bool verified = await VerifyExactSessionAsync(client, sessionUri, launch.SessionId, launch.Project, linked.Token);
                    ThrowIfLaunchEnded(launch, process);
                    if (verified && IsSessionActive(launch.Project, launch.SessionId)) return;
                }
                await Task.Delay(PollInterval, linked.Token);
            }
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try { await StopOwnedProcessTreeAsync(process); }
            catch (Exception cleanup) { throw new IOException("CMD 시작 시간이 초과됐고 자체 프로세스 종료도 확인하지 못했습니다.", new AggregateException(ex, cleanup)); }
            throw new TimeoutException("CMD에서 OpenCode 세션 시작 확인이 시간 초과되어 자체 실행 트리를 종료했습니다.", ex);
        }
        catch (Exception startupError)
        {
            try { await StopOwnedProcessTreeAsync(process); }
            catch (Exception cleanup) { throw new IOException("CMD 시작에 실패했고 자체 프로세스 종료도 확인하지 못했습니다.", new AggregateException(startupError, cleanup)); }
            throw;
        }
        finally { process?.Dispose(); }
    }

    private static ExternalTerminalLaunch RestoreMetadata(ExternalTerminalLaunch launch)
    {
        if (!string.IsNullOrWhiteSpace(launch.Project) && launch.Endpoint is not null && launch.Port > 0)
            return launch;
        string jsonPath = Path.Combine(launch.Directory, "launch.json");
        using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement root = json.RootElement;
        string project = root.GetProperty("Project").GetString() ?? throw new InvalidDataException("CMD launch project is missing.");
        string session = root.GetProperty("Session").GetString() ?? throw new InvalidDataException("CMD launch session is missing.");
        int port = root.GetProperty("Port").GetInt32();
        if (!string.Equals(session, launch.SessionId, StringComparison.Ordinal) || port is < 1 or > 65535)
            throw new InvalidDataException("CMD launch metadata does not match the requested session.");
        return launch with { Project = Path.GetFullPath(project), Port = port, Endpoint = new Uri($"http://127.0.0.1:{port}/") };
    }
    private static void ThrowIfLaunchEnded(ExternalTerminalLaunch launch, Process process)
    {
        string failurePath = Path.Combine(launch.Directory, "failed.txt");
        if (File.Exists(failurePath))
        {
            string detail;
            try { detail = File.ReadAllText(failurePath); }
            catch (IOException) { detail = "실패 정보를 읽을 수 없습니다."; }
            throw new IOException("CMD 이어가기 실패 · " + detail);
        }
        if (File.Exists(Path.Combine(launch.Directory, "completed")))
            throw new IOException("OpenCode 세션이 시작 확인 전에 종료되었습니다.");
        if (process.HasExited)
            throw new IOException("CMD가 OpenCode 세션 시작을 확인하기 전에 종료되었습니다.");
    }

    private static async Task<bool> VerifyExactSessionAsync(HttpClient client, Uri uri, string session, string project, CancellationToken token)
    {
        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, requestTimeout.Token);
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable || (int)response.StatusCode >= 500)
                return false;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("OpenCode 시작 확인 API가 HTTP " + (int)response.StatusCode + "을 반환했습니다.", null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(requestCancellation.Token);
            using var limited = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, requestCancellation.Token);
                if (read == 0) break;
                if (limited.Length + read > MaximumSessionResponseBytes)
                    throw new InvalidDataException("OpenCode 세션 시작 응답이 너무 큽니다.");
                limited.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(limited.ToArray());
            JsonElement root = json.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.GetString() != session ||
                !root.TryGetProperty("directory", out var directory) || directory.GetString() is not { } value || !DirectoriesEqual(value, project))
                throw new InvalidDataException("OpenCode 시작 응답이 요청한 세션 또는 프로젝트와 일치하지 않습니다.");
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && requestTimeout.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null or >= HttpStatusCode.InternalServerError)
        {
            return false;
        }
    }
    private static async Task StopOwnedProcessTreeAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }

        try { await process.WaitForExitAsync().WaitAsync(CleanupTimeout); }
        catch (TimeoutException ex)
        {
            throw new IOException("시작에 사용한 CMD 프로세스 트리 종료를 확인하지 못했습니다.", ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException("시작에 사용한 CMD 프로세스 트리 종료 상태를 확인하지 못했습니다.", ex);
        }
        if (!process.HasExited)
            throw new IOException("시작에 사용한 CMD 프로세스가 여전히 실행 중입니다.");
    }
    private static HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = timeout ?? Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaximumSessionResponseBytes };
        if (Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD") is { Length: > 0 } password)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                (Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode") + ":" + password)));
        return client;
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool DirectoriesEqual(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    private static void ValidateSession(string session)
    {
        if (!Regex.IsMatch(session, "\\Ases_[A-Za-z0-9_]{1,200}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("이어 열 세션 ID가 올바르지 않습니다.");
    }

    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $cmdSessionLease = $null
        try {
            $cmdSession = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'launch.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $cmdSessionLease = [System.IO.File]::Open($cmdSession.Lease, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            Set-Location -LiteralPath $cmdSession.Project
            $Host.UI.RawUI.WindowTitle = $cmdSession.Title + ' - OpenCode CMD'
            $env:OPENCODE_CONFIG_CONTENT = $cmdSession.Config
            $cmdSessionArgs = @('--hostname', '127.0.0.1', '--port', [string]$cmdSession.Port, '--session', $cmdSession.Session)
            if ($cmdSession.AutoMode) { $cmdSessionArgs += '--auto' }
            Write-Host 'CMD에서 기존 대화를 이어갑니다. TerminalNotes를 종료해도 이 창은 유지됩니다.'
            Write-Host '다음 요청은 이 창에 입력하세요. 지휘/검토/구현 자동 순환은 앱에서 중단되었습니다.'
            [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'started'), 'started')
            & $cmdSession.Command @cmdSessionArgs
            if ($LASTEXITCODE -ne 0) { throw ('OpenCode exit code: ' + $LASTEXITCODE) }
        } catch {
            [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'failed.txt'), $_.Exception.Message)
            Write-Host ('CMD 이어가기 실패: ' + $_.Exception.Message) -ForegroundColor Red
        } finally {
            if ($null -ne $cmdSessionLease) { $cmdSessionLease.Dispose() }
            [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'completed'), 'completed')
        }
        """;
}