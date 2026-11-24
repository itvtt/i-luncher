using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ApiManager;

/// <summary>서버가 듣는 주소 (설정 파일에서 읽음). ApiBase 는 이 PC에서 관리 기능을 부를 주소</summary>
internal sealed record ServerAddress(string HealthUrl, string AdminUrl, string DataFolder, string LogFile, string ApiBase);

/// <summary>
/// 이 PC의 API 서버 i-api (C#, i-api.exe)를 다룸. 서버는 %LOCALAPPDATA%\i-api\i-api.exe 로 창 없이 돌고 (로그인 때는 시작 폴더의 "i-api.vbs"),
/// 설정 · 비밀값은 그 옆의 settings.txt 에 있음. 런처에 든 서버(server\i-api.exe)가 더 새 버전이면 멈추고 바꾼 뒤 다시 시작 (settings.txt 는 그대로).
/// 데이터(스킬·공지·보고·앱 목록·사용 기록)는 C:\ProgramData\i-api-data 에 따로 있어 바꿔도 남음.
/// 예전 Python 서버(relay.py: %LOCALAPPDATA%\i-api 또는 그 전 이름 TerminalNotesRelay)는 한 번 옮김: SETTINGS → settings.txt, 자동 실행, 러너 로그온 작업
/// </summary>
internal static partial class ServerManager
{
    /// <summary>확인 코드가 바꿈 (이 PC 의 실제 서버를 건드리지 않게)</summary>
    internal static string Root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    internal static string? BundledOverride;
    internal static string? StartupOverride;
    /// <summary>확인 코드가 서버 프로그램에 주는 환경 (APPDATA · LOCALAPPDATA · ProgramData 를 임시 폴더로)</summary>
    internal static Dictionary<string, string> ChildEnvironment = [];

    public static string DeployFolder => Path.Combine(Root, "i-api");
    public static string DeployedExe => Path.Combine(DeployFolder, "i-api.exe");
    public static string SettingsFile => Path.Combine(DeployFolder, "settings.txt");
    /// <summary>2.23 의 Python 서버 (이름만 i-api 로 바꾼 relay.py)</summary>
    public static string PythonScript => Path.Combine(DeployFolder, "relay.py");
    /// <summary>relay 2.22 까지의 서버 폴더 (이름이 Terminal Notes 중계 서버였을 때)</summary>
    public static string OldDeployFolder => Path.Combine(Root, "TerminalNotesRelay");
    public static string OldDeployedScript => Path.Combine(OldDeployFolder, "relay.py");
    private static readonly string[] StartupScripts = ["i-api.vbs", "Terminal Notes Relay.vbs"];
    private static string StartupFolder => StartupOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    /// <summary>이 PC 에 서버가 있음 (C#, 아직 옮기지 않은 Python, 예전 이름)</summary>
    public static bool IsDeployed => File.Exists(DeployedExe) || File.Exists(PythonScript) || File.Exists(OldDeployedScript);
    /// <summary>아직 Python 서버 (런처가 켜질 때 C# 서버로 옮김)</summary>
    public static bool IsPython => !File.Exists(DeployedExe) && (File.Exists(PythonScript) || File.Exists(OldDeployedScript));
    public static string BundledFolder => BundledOverride ?? Path.Combine(AppContext.BaseDirectory, "server");
    public static string BundledExe => Path.Combine(BundledFolder, "i-api.exe");
    /// <summary>이 PC 에서 서버를 켜고 끌 수 있음 (런처에 서버가 들어 있음)</summary>
    public static bool CanControl => File.Exists(BundledExe);

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

    /// <summary>이 PC 에 있는 서버의 버전 (C#, 없으면 아직 옮기지 않은 Python)</summary>
    public static Version? DeployedVersion =>
        ReadVersion(DeployedExe) ?? ReadVersion(PythonScript) ?? ReadVersion(OldDeployedScript);

    /// <summary>관리자 메뉴의 "설정 파일 열기": C# 서버면 settings.txt, 아직 Python 이면 그 relay.py</summary>
    public static string SettingsPath => File.Exists(DeployedExe) || !IsPython ? SettingsFile : File.Exists(PythonScript) ? PythonScript : OldDeployedScript;

    // ── 설정 읽기 ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>설정 값 (settings.txt 의 이름=값, 아직 Python 이면 relay.py 의 SETTINGS)</summary>
    public static string Setting(string key)
    {
        if (IsPython) return PythonSetting(SettingsBlock(SettingsPath), key);
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

    // "KEY": "문자열" (\\ 와 \" 이스케이프 포함) 또는 "KEY": 숫자
    private static string PythonSetting(string text, string key)
    {
        Match match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?:\"(?<text>(?:[^\"\\\\]|\\\\.)*)\"|(?<number>\\d+))");
        if (!match.Success) return "";
        if (!match.Groups["text"].Success) return match.Groups["number"].Value.Trim();
        return Regex.Replace(match.Groups["text"].Value, @"\\(.)", escaped => escaped.Groups[1].Value switch
        {
            "n" => "\n", "t" => "\t", "r" => "\r", var other => other
        }).Trim();
    }

    private static string SettingsBlock(string script)
    {
        try
        {
            string text = File.Exists(script) ? File.ReadAllText(script, Encoding.UTF8) : "";
            int start = text.IndexOf("SETTINGS = {", StringComparison.Ordinal), end = text.IndexOf("# ── 설정 끝 ──", StringComparison.Ordinal);
            return start >= 0 && end > start ? text[start..end] : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>관리 기능(관리자 메뉴 · 관리 페이지)의 비밀번호. 비어 있으면 비밀번호 없이 열림</summary>
    public static string ReadAdminPassword() => Setting("RELAY_ADMIN_PASSWORD");

    /// <summary>
    /// 관리자 메뉴가 서버에 쓰는 비밀번호: 이 PC 의 서버면 그 설정 파일, 다른 PC 의 서버면(또는 설정이 비면)
    /// 관리자 PC 의 환경 변수 API_ADMIN_PASSWORD 또는 프로필 폴더(APPS_ENV_DIR)의 i-luncher.env 의 API_ADMIN_PASSWORD.
    /// 런처 화면에서 묻거나 저장하지 않음
    /// </summary>
    public static string AdminPassword()
    {
        string local = ManagerHost.IsLocalServer ? ReadAdminPassword() : "";
        if (local.Length > 0) return local;
        if (Environment.GetEnvironmentVariable("API_ADMIN_PASSWORD") is { Length: > 0 } variable) return variable.Trim();
        if (Environment.GetEnvironmentVariable("APPS_ENV_DIR") is { Length: > 0 } folder)
        {
            string file = Path.Combine(folder.Trim(), "i-luncher.env");
            try
            {
                if (File.Exists(file))
                    foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                    {
                        string text = line.Trim();
                        if (text.StartsWith("API_ADMIN_PASSWORD=", StringComparison.Ordinal))
                            return text["API_ADMIN_PASSWORD=".Length..].Trim().Trim('"');
                    }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        // 다른 PC 의 서버인데 따로 넣은 비밀번호가 없으면: 이 PC 에도 서버가 있으면 그 비밀번호 (두 서버를 같은 비밀번호로 둔 경우)
        return ManagerHost.IsLocalServer ? "" : ReadAdminPassword();
    }

    /// <summary>설정의 RELAY_HOST·RELAY_PORT·RELAY_BIND 로 정한 주소 (서버의 받는 주소 · 상태 확인과 같은 규칙)</summary>
    public static ServerAddress ReadAddress()
    {
        string host = Setting("RELAY_HOST"), bind = Setting("RELAY_BIND");
        // 서버에 기본 포트가 없음: 정하지 않았으면 0 (응답 없음으로 보이고, 관리자 메뉴가 포트 창을 엶)
        int port = int.TryParse(Setting("RELAY_PORT"), out int value) && value is > 0 and < 65536 ? value : 0;
        string scheme = Setting("RELAY_TLS_CERT").Length > 0 ? "https" : "http";
        string listen = bind.Length > 0 ? bind : IPAddress.TryParse(host, out _) ? host : host is "localhost" ? "127.0.0.1" : "0.0.0.0";
        string healthHost = listen is "0.0.0.0" or "" ? "127.0.0.1" : listen;
        string adminHost = host.Length > 0 ? host : healthHost;
        string dataRoot = Setting("RELAY_DATA_ROOT");
        if (dataRoot.Length == 0) dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "i-api-data");
        string log = Setting("RELAY_LOG_FILE");
        log = log.Length == 0 ? Path.Combine(dataRoot, "relay.log") : Path.IsPathRooted(log) ? log : Path.Combine(dataRoot, log);
        // 다른 PC 의 서버 (런처의 API 서버 주소): 상태 확인과 관리 기능이 그 주소로 감
        if (!ManagerHost.IsLocalServer)
            return new ServerAddress(ManagerHost.ApiServer + "/health", ManagerHost.ApiServer + "/", dataRoot, log, ManagerHost.ApiServer);
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

    // ── 서버 프로그램 ──────────────────────────────────────────────────────────────────────────────

    /// <summary>런처가 쓰는 .NET 이 이 PC 전체에 설치된 것이 아니면 (런처 안의 dotnet) 그 위치: 서버도 같은 것을 쓰게</summary>
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

    /// <summary>i-api.exe 를 명령으로 돌림 (--restart · --halt · --settings-from …): 종료 코드와 출력</summary>
    public static async Task<(int ExitCode, string Output)> RunServerAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(DeployedExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = DeployFolder
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (PrivateRuntime() is { } runtime) start.Environment["DOTNET_ROOT"] = runtime;
        foreach (var (key, value) in ChildEnvironment) start.Environment[key] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("서버 프로그램을 시작하지 못했습니다.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, ((await output) + (await error)).Trim());
    }

    /// <summary>
    /// 런처에 든 서버가 이 PC 의 것보다 새 버전이면 바꿈 (돌고 있으면 멈춘 뒤, 예전 파일은 backup\i-api-&lt;버전&gt;\, settings.txt 는 그대로).
    /// 바꿨으면 설명, 이미 최신이면 null. 처음이면 설치하고 로그인 자동 실행을 등록. 예전 Python 서버면 한 번 옮김. 호출한 쪽이 서버를 시작함
    /// </summary>
    public static async Task<string?> DeployIfNewerAsync()
    {
        Version bundled = ReadVersion(BundledExe) ?? throw new FileNotFoundException("이 프로그램 폴더에 server\\i-api.exe가 없습니다. 다시 설치하세요.");
        if (IsPython) return await MoveFromPythonAsync(bundled);
        if (!File.Exists(DeployedExe))
        {
            Directory.CreateDirectory(DeployFolder);
            CopyBundled(null);
            OpenSetupWindow();
            return $"서버 {bundled}을(를) 이 PC 에 설치했습니다. 열린 창에서 포트를 정하고 [실행]하세요 (그다음 설정 파일 settings.txt 에 관리자 비밀번호).";
        }
        Version? deployed = ReadVersion(DeployedExe);
        if (deployed is not null && deployed >= bundled) return null;
        await RunServerAsync("--halt");
        string backup = CopyBundled(deployed);
        return $"서버를 {deployed?.ToString() ?? "이전 버전"} → {bundled}(으)로 바꿨습니다 (예전 파일: backup\\{Path.GetFileName(backup)}).";
    }

    /// <summary>
    /// 런처에 든 서버 파일을 서버 폴더로. 이미 있는 파일은 먼저 backup\i-api-&lt;버전&gt;\ 로 옮김 (멈추지 못해 잡혀 있어도 이름은 바꿀 수 있음).
    /// settings.txt · 로그 같은 서버 폴더의 다른 파일은 그대로. 예전 파일을 둔 폴더
    /// </summary>
    private static string CopyBundled(Version? previous)
    {
        string backup = Path.Combine(DeployFolder, "backup", $"i-api-{previous?.ToString() ?? "unknown"}");
        if (Directory.Exists(backup)) backup += "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (string file in Directory.EnumerateFiles(BundledFolder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(BundledFolder, file);
            if (relative.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(relative).StartsWith("test_", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(DeployFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && previous is not null)
            {
                string kept = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Move(target, kept, overwrite: true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(file, target, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 10) { Thread.Sleep(300); }
            }
        }
        return backup;
    }

    /// <summary>
    /// 예전 Python 서버(relay.py)를 C# 서버로 한 번 옮김: 그 서버를 멈추고, 서버 파일을 넣고, SETTINGS 를 settings.txt 로 (--settings-from),
    /// 로그인 자동 실행이 있었으면 i-api.exe 로 다시 등록. relay.py 는 backup\ 으로 (예전 이름의 폴더면 그대로 두고 안내 파일).
    /// 데이터 폴더 이름(TerminalNotesRelayData → i-api-data)과 러너 로그온 작업은 새 서버가 처음 시작할 때 옮김
    /// </summary>
    private static async Task<string> MoveFromPythonAsync(Version bundled)
    {
        string source = File.Exists(PythonScript) ? PythonScript : OldDeployedScript;
        Version? old = ReadVersion(source);
        bool atLogon = StartupScripts.Any(name => File.Exists(Path.Combine(StartupFolder, name)));
        StopPython(source);
        Directory.CreateDirectory(DeployFolder);
        CopyBundled(null);
        var (exitCode, output) = await RunServerAsync("--settings-from", source);
        if (exitCode != 0) throw new InvalidOperationException("예전 relay.py의 설정을 옮기지 못했습니다: " + output);
        if (atLogon) await RunServerAsync("--install-startup", "--no-firewall");
        if (source == PythonScript)
        {
            string backups = Path.Combine(DeployFolder, "backup");
            Directory.CreateDirectory(backups);
            string backup = Path.Combine(backups, $"relay-{old?.ToString() ?? "unknown"}.py");
            if (File.Exists(backup)) backup = Path.Combine(backups, $"relay-{old?.ToString() ?? "unknown"}-{DateTime.Now:yyyyMMdd-HHmmss}.py");
            File.Move(PythonScript, backup);
        }
        else
        {
            try
            {
                File.WriteAllText(Path.Combine(OldDeployFolder, "MOVED-TO-i-api.txt"),
                    $"API 서버를 {DeployFolder} 로 옮겼습니다 ({DateTime.Now:yyyy-MM-dd HH:mm}). 설정(SETTINGS)도 settings.txt 로 옮겼으므로 이 폴더는 쓰지 않습니다.\r\n" +
                    "데이터 C:\\ProgramData\\TerminalNotesRelayData 는 새 서버가 처음 시작할 때 C:\\ProgramData\\i-api-data 로 옮깁니다.\r\n", new UTF8Encoding(true));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        // 예전 시작 · 중지 스크립트는 Python 을 부름: 새 것으로 (CopyBundled 가 덮음). 남은 __pycache__ 는 지움
        try { if (Directory.Exists(Path.Combine(DeployFolder, "__pycache__"))) Directory.Delete(Path.Combine(DeployFolder, "__pycache__"), true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return $"서버를 C# 판 {bundled}(으)로 바꿨습니다 (예전 Python 서버 {old?.ToString() ?? ""}, 설정은 settings.txt 로 옮김).";
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

    /// <summary>포트가 정해져 있음 (서버에 기본 포트가 없음)</summary>
    public static bool HasPort => int.TryParse(Setting("RELAY_PORT"), out int port) && port is > 0 and < 65536;

    /// <summary>포트를 묻는 서버 창 (i-api.exe --setup): 포트를 쓰고 자동 실행 · 방화벽 · 시작까지 그 창이 함</summary>
    public static void OpenSetupWindow()
    {
        var start = new ProcessStartInfo(DeployedExe) { UseShellExecute = false, WorkingDirectory = DeployFolder };
        start.ArgumentList.Add("--setup");
        if (PrivateRuntime() is { } runtime) start.Environment["DOTNET_ROOT"] = runtime;
        foreach (var (key, value) in ChildEnvironment) start.Environment[key] = value;
        Process.Start(start)?.Dispose();
    }

    public static Task<(int ExitCode, string Output)> RestartAsync() => RunServerAsync("--restart");
    public static Task<(int ExitCode, string Output)> HaltAsync() => RunServerAsync("--halt");
}
