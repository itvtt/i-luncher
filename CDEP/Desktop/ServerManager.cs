using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace IApi.Manager;

/// <summary>서버가 듣는 주소 (설정 파일에서 읽음). ApiBase 는 이 PC에서 관리 기능을 부를 주소</summary>
internal sealed record ServerAddress(string HealthUrl, string AdminUrl, string DataFolder, string LogFile, string ApiBase);

/// <summary>
/// 관리 창이 이 i-api.exe 의 서버를 다룸: 서버는 이 exe 가 창 없이 돌고 (로그인 때는 시작 폴더의 "i-api.vbs"), 설정 · 비밀값은 그 옆의 settings.txt.
/// 시작 · 멈추기 · 포트는 이 exe 를 명령으로 부름 (--restart · --halt · --port). 데이터는 C:\ProgramData\i-api-data 에 따로 있음.
/// 예전 Python 서버(relay.py: 이 폴더, %LOCALAPPDATA%\i-api, 그 전 이름 TerminalNotesRelay)가 있으면 한 번 옮김: SETTINGS → settings.txt, 자동 실행
/// </summary>
internal static partial class ServerManager
{
    /// <summary>확인 코드가 바꿈 (이 PC 의 실제 서버 · 시작 폴더를 건드리지 않게)</summary>
    internal static string? FolderOverride;
    internal static string? StartupOverride;
    // 서버와 같은 규칙: 환경 변수 LOCALAPPDATA (없으면 Windows 가 정한 폴더)
    internal static string LocalRoot = Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local ? local : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    /// <summary>확인 코드가 서버 프로그램에 주는 환경 (APPDATA · LOCALAPPDATA · ProgramData 를 임시 폴더로)</summary>
    internal static Dictionary<string, string> ChildEnvironment = [];

    /// <summary>이 서버의 폴더 (이 i-api.exe 가 있는 곳)</summary>
    public static string Folder => FolderOverride ?? Path.GetDirectoryName(Program.ExecutablePath)!;
    public static string Exe => Path.Combine(Folder, "i-api.exe");
    public static string SettingsFile => Path.Combine(Folder, "settings.txt");
    private static readonly string[] StartupScripts = ["i-api.vbs", "Terminal Notes Relay.vbs"];
    // 서버(Setup.StartupFolder)와 같은 규칙: APPDATA 아래의 시작 폴더
    private static string StartupFolder => StartupOverride ?? Path.Combine(
        Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } roaming ? roaming : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

    /// <summary>예전 Python 서버가 있을 수 있는 곳: 이 폴더, 2.23 의 %LOCALAPPDATA%\i-api, 2.22 까지의 %LOCALAPPDATA%\TerminalNotesRelay</summary>
    private static IEnumerable<string> PythonScripts =>
        new[] { Folder, Path.Combine(LocalRoot, "i-api"), Path.Combine(LocalRoot, "TerminalNotesRelay") }.Distinct(StringComparer.OrdinalIgnoreCase).Select(folder => Path.Combine(folder, "relay.py"));

    /// <summary>아직 옮기지 않은 예전 Python 서버 (없으면 null). 옮긴 뒤 안내 파일(MOVED-TO-i-api.txt)만 남긴 예전 폴더는 빼고</summary>
    public static string? PythonScript =>
        PythonScripts.FirstOrDefault(script => File.Exists(script) && !File.Exists(Path.Combine(Path.GetDirectoryName(script)!, "MOVED-TO-i-api.txt")));

    [GeneratedRegex(@"^VERSION\s*=\s*""(\d+\.\d+\.\d+)""", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    /// <summary>서버 버전: i-api.exe 의 파일 버전, relay.py 의 VERSION</summary>
    public static Version? ReadVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                Match match = VersionLine().Match(File.ReadAllText(path, Encoding.UTF8));
                return match.Success && Version.TryParse(match.Groups[1].Value, out var script) ? script : null;
            }
            string text = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
            return Version.TryParse(text.Split('+')[0], out var version) ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    // ── 설정 읽기 ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>settings.txt 의 이름=값</summary>
    public static string Setting(string key)
    {
        try
        {
            if (!File.Exists(SettingsFile)) return "";
            foreach (string raw in File.ReadAllLines(SettingsFile, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.StartsWith('#') || !line.StartsWith(key + "=", StringComparison.Ordinal)) continue;
                string value = line[(key.Length + 1)..].Trim();
                return value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return "";
    }

    /// <summary>관리 기능의 비밀번호 (settings.txt). 비어 있으면 비밀번호 없이 열림</summary>
    public static string AdminPassword() => Setting("RELAY_ADMIN_PASSWORD");

    /// <summary>지금 정한 포트 (없으면 0: 서버에 기본 포트가 없음)</summary>
    public static int Port => int.TryParse(Setting("RELAY_PORT"), out int port) && port is > 0 and < 65536 ? port : 0;
    public static bool HasPort => Port > 0;

    /// <summary>설정의 RELAY_HOST·RELAY_PORT·RELAY_BIND 로 정한 주소 (서버의 받는 주소 · 상태 확인과 같은 규칙)</summary>
    public static ServerAddress ReadAddress()
    {
        string host = Setting("RELAY_HOST"), bind = Setting("RELAY_BIND");
        int port = Port;
        string scheme = Setting("RELAY_TLS_CERT").Length > 0 ? "https" : "http";
        string listen = bind.Length > 0 ? bind : IPAddress.TryParse(host, out _) ? host : host is "localhost" ? "127.0.0.1" : "0.0.0.0";
        string healthHost = listen is "0.0.0.0" or "" ? "127.0.0.1" : listen;
        string adminHost = host.Length > 0 ? host : healthHost;
        string dataRoot = Setting("RELAY_DATA_ROOT");
        if (dataRoot.Length == 0) dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "i-api-data");
        string log = Setting("RELAY_LOG_FILE");
        log = log.Length == 0 ? Path.Combine(dataRoot, "relay.log") : Path.IsPathRooted(log) ? log : Path.Combine(dataRoot, log);
        return new ServerAddress($"{scheme}://{healthHost}:{port}/health", $"{scheme}://{adminHost}:{port}/", dataRoot, log, $"{scheme}://{healthHost}:{port}");
    }

    public static async Task<bool> IsHealthyAsync(ServerAddress address)
    {
        using var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(address.HealthUrl);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return false; }
    }

    /// <summary>로그인 자동 실행(시작 폴더의 i-api.vbs)이 등록돼 있음</summary>
    public static bool StartsAtLogon => File.Exists(Path.Combine(StartupFolder, StartupScripts[0]));

    // ── 서버 프로그램 ──────────────────────────────────────────────────────────────────────────────

    /// <summary>이 창이 쓰는 .NET 이 이 PC 전체에 설치된 것이 아니면 그 위치: 서버도 같은 것을 쓰게</summary>
    private static string? PrivateRuntime()
    {
        try
        {
            string root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            string global = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            return string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(global), StringComparison.OrdinalIgnoreCase) ? null : root;
        }
        catch (Exception error) when (error is ArgumentException or IOException) { return null; }
    }

    /// <summary>i-api.exe 를 명령으로 돌림 (--restart · --halt · --port · --settings-from …): 종료 코드와 출력</summary>
    public static Task<(int ExitCode, string Output)> RunServerAsync(params string[] arguments) => RunServerAsync(TimeSpan.FromSeconds(120), arguments);

    private static async Task<(int ExitCode, string Output)> RunServerAsync(TimeSpan limit, params string[] arguments)
    {
        var start = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Folder
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (PrivateRuntime() is { } runtime) start.Environment["DOTNET_ROOT"] = runtime;
        foreach (var (key, value) in ChildEnvironment) start.Environment[key] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("서버 프로그램을 시작하지 못했습니다.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(limit);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, ((await output) + (await error)).Trim());
    }

    /// <summary>
    /// i-api.exe --port: 그 포트를 설정 파일에 쓰고 로그인 자동 실행 · 방화벽(규칙이 없으면 Windows 확인 창, 2분까지 기다림)을 둔 뒤 서버를 (다시) 시작.
    /// 서버가 알려 준 글 (성공이면 받는 주소)
    /// </summary>
    public static Task<(int ExitCode, string Output)> ConfigureAsync(int port) =>
        RunServerAsync(TimeSpan.FromSeconds(330), "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static Task<(int ExitCode, string Output)> RestartAsync() => RunServerAsync("--restart");
    public static Task<(int ExitCode, string Output)> HaltAsync() => RunServerAsync("--halt");

    // ── 예전 Python 서버 옮기기 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 예전 Python 서버(relay.py)를 이 서버로 한 번 옮김: 그 서버를 멈추고, SETTINGS 를 settings.txt 로 (--settings-from),
    /// 로그인 자동 실행이 있었으면 이 i-api.exe 로 다시 등록. relay.py 는 그 폴더의 backup\ 으로 (예전 이름의 폴더면 그대로 두고 안내 파일).
    /// 데이터 폴더 이름(TerminalNotesRelayData → i-api-data)과 러너 로그온 작업은 이 서버가 처음 시작할 때 옮김. 옮겼으면 설명, 없으면 null
    /// </summary>
    public static async Task<string?> MoveFromPythonAsync()
    {
        if (PythonScript is not { } source) return null;
        Version? old = ReadVersion(source);
        string folder = Path.GetDirectoryName(source)!;
        bool atLogon = StartupScripts.Any(name => File.Exists(Path.Combine(StartupFolder, name)));
        StopPython(source);
        var (exitCode, output) = await RunServerAsync("--settings-from", source);
        if (exitCode != 0) throw new InvalidOperationException("예전 relay.py의 설정을 옮기지 못했습니다: " + output);
        if (atLogon) await RunServerAsync("--install-startup", "--no-firewall");
        if (!Path.GetFileName(folder).Equals("TerminalNotesRelay", StringComparison.OrdinalIgnoreCase))
        {
            string backups = Path.Combine(folder, "backup");
            Directory.CreateDirectory(backups);
            string backup = Path.Combine(backups, $"relay-{old?.ToString() ?? "unknown"}.py");
            if (File.Exists(backup)) backup = Path.Combine(backups, $"relay-{old?.ToString() ?? "unknown"}-{DateTime.Now:yyyyMMdd-HHmmss}.py");
            File.Move(source, backup);
            try { if (Directory.Exists(Path.Combine(folder, "__pycache__"))) Directory.Delete(Path.Combine(folder, "__pycache__"), true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        else
        {
            try
            {
                File.WriteAllText(Path.Combine(folder, "MOVED-TO-i-api.txt"),
                    $"API 서버를 {Folder} 의 i-api.exe 로 옮겼습니다 ({DateTime.Now:yyyy-MM-dd HH:mm}). 설정(SETTINGS)도 settings.txt 로 옮겼으므로 이 폴더는 쓰지 않습니다.\r\n" +
                    "데이터 C:\\ProgramData\\TerminalNotesRelayData 는 새 서버가 처음 시작할 때 C:\\ProgramData\\i-api-data 로 옮깁니다.\r\n", new UTF8Encoding(true));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return $"예전 Python 서버 {old?.ToString() ?? ""}을(를) 이 서버로 옮겼습니다 (설정은 settings.txt 로, relay.py 는 backup).";
    }

    /// <summary>그 relay.py 를 돌리는 Python 프로세스를 멈춤 (로그온 작업의 짧은 --start-runners 는 서버가 아니라 그대로). 멈춘 수</summary>
    internal static int StopPython(string script)
    {
        var start = new ProcessStartInfo("powershell")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     "[Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-CimInstance Win32_Process -Filter \"Name like 'python%'\" | ForEach-Object { \"$($_.ProcessId)`t$($_.CommandLine)\" }" })
            start.ArgumentList.Add(argument);
        string listing;
        try
        {
            using var process = Process.Start(start)!;
            listing = process.StandardOutput.ReadToEnd();
            process.WaitForExit(60_000);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return 0; }
        int stopped = 0;
        foreach (int pid in MatchingRelays(listing, script))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                process.WaitForExit(5000);
                stopped++;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return stopped;
    }

    /// <summary>'pid TAB 명령 줄' 줄 중 그 relay.py 를 전체 경로로 부르는 것 (--start-runners 는 빼고)</summary>
    internal static List<int> MatchingRelays(string listing, string script)
    {
        var found = new List<int>();
        foreach (string line in listing.Split('\n'))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0 || !int.TryParse(line[..tab].Trim(), out int pid) || pid == Environment.ProcessId) continue;
            string command = line[(tab + 1)..];
            if (command.Contains(script, StringComparison.OrdinalIgnoreCase) && !command.Contains("--start-runners", StringComparison.OrdinalIgnoreCase)) found.Add(pid);
        }
        return found;
    }
}
