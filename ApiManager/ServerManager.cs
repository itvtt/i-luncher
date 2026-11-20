using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ApiManager;

/// <summary>서버가 듣는 주소 (relay.py 맨 위 SETTINGS 에서 읽음). ApiBase 는 이 PC에서 관리 기능을 부를 주소</summary>
internal sealed record ServerAddress(string HealthUrl, string AdminUrl, string DataFolder, string LogFile, string ApiBase);

/// <summary>
/// 이 PC의 API 서버(relay.py)를 다룸. 서버는 %LOCALAPPDATA%\TerminalNotesRelay\relay.py 에서 pythonw 로 창 없이 돌고
/// (로그인 때는 시작 폴더의 "Terminal Notes Relay.vbs"), 설정·비밀값은 그 파일 맨 위 SETTINGS 에 있음 (.env 를 쓰지 않음).
/// 이 프로그램에 든 relay.py 가 더 새 버전이면: 예전 파일을 backup\ 에 두고, 새 파일에 예전 SETTINGS 를 옮긴 뒤 바꾸고 다시 시작.
/// 데이터(스킬·공지·보고·앱 목록)는 C:\ProgramData\TerminalNotesRelayData 에 따로 있어 바꿔도 남음.
/// </summary>
internal static partial class ServerManager
{
    public static string DeployFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotesRelay");
    public static string DeployedScript => Path.Combine(DeployFolder, "relay.py");
    public static string BundledFolder => Path.Combine(AppContext.BaseDirectory, "server");
    public static string BundledScript => Path.Combine(BundledFolder, "relay.py");

    [GeneratedRegex(@"^VERSION\s*=\s*""(\d+\.\d+\.\d+)""", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    public static Version? ReadVersion(string script)
    {
        try
        {
            if (!File.Exists(script)) return null;
            Match match = VersionLine().Match(File.ReadAllText(script, Encoding.UTF8));
            return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    // "KEY": "문자열" (\\ 와 \" 이스케이프 포함) 또는 "KEY": 숫자
    private static string Setting(string text, string key)
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
        string text = File.Exists(script) ? File.ReadAllText(script, Encoding.UTF8) : "";
        int start = text.IndexOf("SETTINGS = {", StringComparison.Ordinal), end = text.IndexOf("# ── 설정 끝 ──", StringComparison.Ordinal);
        return start >= 0 && end > start ? text[start..end] : "";
    }

    /// <summary>관리 기능(관리 페이지·이 프로그램의 창)의 비밀번호. 비어 있으면 비밀번호 없이 열림</summary>
    public static string ReadAdminPassword(string script) => Setting(SettingsBlock(script), "RELAY_ADMIN_PASSWORD");

    /// <summary>
    /// 관리자 메뉴가 서버에 쓰는 비밀번호: 이 PC 에 설치된 relay.py 의 SETTINGS, 없으면(서버가 배포 환경 C-DEP 등에 있을 때)
    /// 관리자 PC 의 환경 변수 API_ADMIN_PASSWORD 또는 프로필 폴더(APPS_ENV_DIR)의 i-luncher.env 의 API_ADMIN_PASSWORD.
    /// 런처 화면에서 묻거나 저장하지 않음
    /// </summary>
    public static string AdminPassword()
    {
        string local = ReadAdminPassword(DeployedScript);
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
        return "";
    }

    /// <summary>SETTINGS 의 RELAY_HOST·RELAY_PORT·RELAY_BIND 로 정한 주소 (relay.py 의 listen_address·health_url 과 같은 규칙)</summary>
    public static ServerAddress ReadAddress(string script)
    {
        string block = SettingsBlock(script);
        string host = Setting(block, "RELAY_HOST"), bind = Setting(block, "RELAY_BIND");
        int port = int.TryParse(Setting(block, "RELAY_PORT"), out int value) && value is > 0 and < 65536 ? value : 2003;
        string scheme = Setting(block, "RELAY_TLS_CERT").Length > 0 ? "https" : "http";
        string listen = bind.Length > 0 ? bind : IPAddress.TryParse(host, out _) ? host : host is "localhost" ? "127.0.0.1" : "0.0.0.0";
        string healthHost = listen is "0.0.0.0" or "" ? "127.0.0.1" : listen;
        string adminHost = host.Length > 0 ? host : healthHost;
        string dataRoot = Setting(block, "RELAY_DATA_ROOT");
        if (dataRoot.Length == 0) dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TerminalNotesRelayData");
        string log = Setting(block, "RELAY_LOG_FILE");
        log = log.Length == 0 ? Path.Combine(dataRoot, "relay.log") : Path.IsPathRooted(log) ? log : Path.Combine(dataRoot, log);
        // .env API_SERVER: 서버를 다른 주소로 부를 때 (예: 다른 망의 주소·https). 상태 확인과 관리 기능이 그 주소로 감
        if (ManagerHost.ApiServer.Length > 0)
            return new ServerAddress(ManagerHost.ApiServer + "/health", ManagerHost.ApiServer + "/", dataRoot, log, ManagerHost.ApiServer);
        return new ServerAddress($"{scheme}://{healthHost}:{port}/health", $"{scheme}://{adminHost}:{port}/", dataRoot, log, $"{scheme}://{healthHost}:{port}");
    }

    /// <summary>python.exe: 지금 서버를 돌리는 pythonw 옆, 등록된 Python, py 런처 순</summary>
    public static string? FindPython()
    {
        foreach (var process in Process.GetProcessesByName("pythonw").Concat(Process.GetProcessesByName("python")))
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    string? candidate = path is null ? null : Path.Combine(Path.GetDirectoryName(path)!, "python.exe");
                    if (candidate is not null && File.Exists(candidate)) return candidate;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var core = hive.OpenSubKey(@"Software\Python\PythonCore");
                foreach (string name in (core?.GetSubKeyNames() ?? []).OrderByDescending(name => Version.TryParse(name.Split('-')[0], out var version) ? version : new Version(0, 0)))
                {
                    using var install = core!.OpenSubKey(name + @"\InstallPath");
                    string? executable = install?.GetValue("ExecutablePath") as string
                        ?? (install?.GetValue(null) is string folder ? Path.Combine(folder, "python.exe") : null);
                    if (executable is not null && File.Exists(executable)) return executable;
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        string launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
        return File.Exists(launcher) ? launcher : null;
    }

    public static async Task<(int ExitCode, string Output)> RunPythonAsync(string python, params string[] arguments)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = DeployFolder
        };
        if (Path.GetFileName(python).Equals("py.exe", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add("-3");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        Directory.CreateDirectory(DeployFolder);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python을 시작하지 못했습니다.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, ((await output) + (await error)).Trim());
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

    /// <summary>
    /// 이 프로그램에 든 relay.py 가 배포된 것보다 새 버전이면 바꿈 (예전 파일은 backup\relay-&lt;버전&gt;.py, SETTINGS 는 새 파일로 옮김).
    /// 바꿨으면 설명을, 이미 최신이면 null. 처음이면 복사만 하고 SETTINGS 를 채우라고 알림
    /// </summary>
    public static async Task<string?> DeployIfNewerAsync(string python)
    {
        Version bundled = ReadVersion(BundledScript) ?? throw new FileNotFoundException("이 프로그램 폴더에 server\\relay.py가 없습니다. 다시 설치하세요.");
        Directory.CreateDirectory(DeployFolder);
        if (!File.Exists(DeployedScript))
        {
            CopyCompanions();
            File.Copy(BundledScript, DeployedScript);
            return $"서버 {bundled}을(를) 처음 설치했습니다. 설정 파일(relay.py 맨 위 SETTINGS)에서 RELAY_HOST·관리자 비밀번호를 채운 뒤 다시 시작하세요.";
        }
        Version? deployed = ReadVersion(DeployedScript);
        if (deployed is not null && deployed >= bundled) return null;

        string staged = Path.Combine(DeployFolder, "relay.new.py");
        File.Copy(BundledScript, staged, overwrite: true);
        var (exitCode, output) = await RunPythonAsync(python, staged, "--settings-from", DeployedScript);
        if (exitCode != 0)
        {
            File.Delete(staged);
            throw new InvalidOperationException("예전 relay.py의 설정을 옮기지 못했습니다: " + output);
        }
        string backups = Path.Combine(DeployFolder, "backup");
        Directory.CreateDirectory(backups);
        string backup = Path.Combine(backups, $"relay-{deployed?.ToString() ?? "unknown"}.py");
        if (File.Exists(backup)) backup = Path.Combine(backups, $"relay-{deployed?.ToString() ?? "unknown"}-{DateTime.Now:yyyyMMdd-HHmmss}.py");
        File.Copy(DeployedScript, backup);
        File.Move(staged, DeployedScript, overwrite: true);
        CopyCompanions();
        return $"서버를 {deployed?.ToString() ?? "이전 버전"} → {bundled}(으)로 바꿨습니다 (예전 파일: backup\\{Path.GetFileName(backup)}).";
    }

    // 시작·중지 스크립트와 README 도 같은 버전으로
    private static void CopyCompanions()
    {
        foreach (string file in Directory.EnumerateFiles(BundledFolder))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("relay.py", StringComparison.OrdinalIgnoreCase) || name.StartsWith("test_", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Copy(file, Path.Combine(DeployFolder, name), overwrite: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static Task<(int ExitCode, string Output)> RestartAsync(string python) => RunPythonAsync(python, DeployedScript, "--restart");
    public static Task<(int ExitCode, string Output)> HaltAsync(string python) => RunPythonAsync(python, DeployedScript, "--halt");
}
