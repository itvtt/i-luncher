using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace IApi;

/// <summary>Windows 에서 서버를 켜고 끄기: 로그인 자동 실행, 시작 · 다시 시작 · 멈추기, 방화벽</summary>
internal static class Setup
{
    public const string StartupScript = "i-api.vbs";
    /// <summary>예전 이름의 로그인 자동 실행 (2.23 전: Terminal Notes Relay), 그리고 그 전 .NET 중계 서버의 바로 가기</summary>
    public static readonly string[] OldStartupScripts = ["Terminal Notes Relay.vbs"];
    public const string OldStartupShortcut = "Terminal Notes Relay.lnk";

    /// <summary>확인 코드가 바꿈 (이 PC 의 실제 시작 폴더를 건드리지 않게)</summary>
    internal static Func<string> StartupFolder = () =>
        Path.Combine(Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } roaming ? roaming : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

    /// <summary>확인 코드가 netsh 등을 흉내 냄: (명령, 인자, 초) → 종료 코드 (못 돌리면 null)</summary>
    internal static Func<string, string[], int, int?> RunHidden = (program, arguments, seconds) => Native.Run(program, arguments, seconds).Code;
    internal static Func<bool> IsAdministrator = Native.IsAdministrator;

    // ── 한 서버 (같은 실행 파일) ─────────────────────────────────────────────────────────────────

    private static string Key(string executable) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable).ToLowerInvariant())))[..16];

    public static string StopEventName(string executable) => @"Local\i-api-stop-" + Key(executable);
    public static string RunningMutexName(string executable) => @"Local\i-api-run-" + Key(executable);
    /// <summary>관리 창 (손으로 실행한 i-api.exe) 하나만: 두 번째로 실행하면 이 신호로 떠 있는 창을 앞으로</summary>
    public static string WindowEventName(string executable) => @"Local\i-api-window-" + Key(executable);
    private static string PidFile(string executable) => Path.Combine(Path.GetDirectoryName(executable)!, "i-api.pid");

    /// <summary>서버로 도는 동안 잡아 두는 것: 멈추라는 신호 · 돌고 있다는 표시 · pid 파일</summary>
    public sealed class Instance : IDisposable
    {
        private readonly Mutex? _running;
        private readonly bool _owned;
        private readonly string _executable;
        public EventWaitHandle Stop { get; }

        public Instance(string executable)
        {
            _executable = executable;
            if (!OperatingSystem.IsWindows())
            {
                Stop = new EventWaitHandle(false, EventResetMode.ManualReset);
                return;
            }
            Stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName(executable));
            Stop.Reset();
            _running = new Mutex(false, RunningMutexName(executable));
            try { _owned = _running.WaitOne(0); }
            catch (AbandonedMutexException) { _owned = true; }
            try { File.WriteAllText(PidFile(executable), Environment.ProcessId.ToString()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_owned)
            {
                try { _running!.ReleaseMutex(); } catch (ApplicationException) { }
            }
            _running?.Dispose();
            Stop.Dispose();
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                if (File.Exists(PidFile(_executable)) && File.ReadAllText(PidFile(_executable)).Trim() == Environment.ProcessId.ToString()) File.Delete(PidFile(_executable));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>이 실행 파일로 도는 서버를 멈춤 (먼저 멈추라는 신호: 사용 기록을 저장하고 끝남, 15초 안에 끝나지 않으면 강제로). 멈춘 수</summary>
    public static int StopRunning(string? executable = null)
    {
        executable ??= Program.ExecutablePath;
        int stopped = 0;
        if (OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting(StopEventName(executable), out var stop))
        {
            using (stop) stop.Set();
            stopped = 1;
            if (Mutex.TryOpenExisting(RunningMutexName(executable), out var running))
            {
                using (running)
                {
                    bool owned;
                    try { owned = running.WaitOne(TimeSpan.FromSeconds(15)); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (owned) running.ReleaseMutex();
                }
            }
        }
        // 신호에 답하지 않은 서버 (멈춘 채 걸림): pid 파일의 프로세스가 이 실행 파일이면 끝냄
        try
        {
            string pidFile = PidFile(executable);
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid) && pid != Environment.ProcessId)
            {
                using var process = Process.GetProcessById(pid);
                if (Util.SamePath(Native.ProcessImage(pid) is { Length: > 0 } image ? image : "", executable))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                    stopped = Math.Max(stopped, 1);
                }
                File.Delete(pidFile);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        return stopped;
    }

    // ── 로그인 자동 실행 ─────────────────────────────────────────────────────────────────────────

    /// <summary>창 없는 로그인 자동 실행: 이 서버를 설정 파일대로 (이번 실행의 --set 과 함께) 돌림</summary>
    public static void InstallStartup(IReadOnlyList<string> setArgs, string? settingsFile = null)
    {
        if (!OperatingSystem.IsWindows()) throw new ConfigError("자동 실행 등록은 Windows에서만 합니다. Linux에서는 systemd 서비스로 등록하세요.");
        string executable = Program.ExecutablePath;
        string command = $"\"\"{executable}\"\"" + (settingsFile is null ? "" : $" --settings \"\"{settingsFile}\"\"")
                         + string.Concat(setArgs.Select(item => $" --set \"\"{item}\"\""));
        var script = new StringBuilder();
        script.Append("Set shell = CreateObject(\"WScript.Shell\")\r\n");
        script.Append($"shell.CurrentDirectory = \"{Path.GetDirectoryName(executable)}\"\r\n");
        // .NET 이 PC 전체가 아니라 런처 안에만 있으면 (DOTNET_ROOT) 그 위치를 알려 줌
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            script.Append($"shell.Environment(\"Process\").Item(\"DOTNET_ROOT\") = \"{root}\"\r\n");
        script.Append($"shell.Run \"{command}\", 0, False\r\n");
        string folder = StartupFolder();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, StartupScript), script.ToString(), Encoding.Unicode);
        string shortcut = Path.Combine(folder, OldStartupShortcut);
        if (File.Exists(shortcut))
        {
            File.Delete(shortcut);
            Console.WriteLine("이전 중계 서버(.NET) 자동 실행 바로 가기를 지웠습니다.");
        }
        foreach (string name in OldStartupScripts)
        {
            if (!File.Exists(Path.Combine(folder, name))) continue;
            File.Delete(Path.Combine(folder, name));
            Console.WriteLine($"예전 이름의 자동 실행({name})을 지웠습니다.");
        }
        Console.WriteLine($"로그인 때 창 없이 자동 실행합니다: {Path.Combine(folder, StartupScript)}");
    }

    /// <summary>자동 실행을 지움. onlyThis 면 이 서버를 시작할 때만 (다른 폴더의 서버는 그대로)</summary>
    public static void UninstallStartup(bool onlyThis = false)
    {
        string path = Path.Combine(StartupFolder(), StartupScript);
        if (!File.Exists(path))
        {
            Console.WriteLine("등록된 자동 실행이 없습니다.");
            return;
        }
        if (onlyThis)
        {
            string script;
            try { script = Encoding.Unicode.GetString(File.ReadAllBytes(path)); }
            catch (IOException) { script = ""; }
            if (!script.Contains(Program.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("등록된 자동 실행은 다른 폴더의 서버라 그대로 두었습니다.");
                return;
            }
        }
        File.Delete(path);
        Console.WriteLine("자동 실행을 해제했습니다.");
    }

    // ── 시작 · 다시 시작 ───────────────────────────────────────────────────────────────────────────

    /// <summary>서버가 /health 에 답함 (wait 초 동안, 프록시 없이 직접)</summary>
    public static bool Answers(Config config, double wait = 15)
    {
        using var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(wait);
        while (true)
        {
            try
            {
                using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, config.HealthUrl));
                if ((int)response.StatusCode == 200) return true;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(500);
        }
    }

    private static string ServerCommand(Config config) =>
        $"\"{Program.ExecutablePath}\"" + (Util.SamePath(config.SettingsFile, Settings.DefaultFile) ? "" : $" --settings \"{config.SettingsFile}\"")
        + string.Concat(config.SetArgs.Select(item => $" --set \"{item.Replace("\"", "\\\"")}\""));

    /// <summary>서버 시작.cmd: 로그인 자동 실행 · 방화벽, 돌고 있으면 멈춘 뒤 (복사한 새 버전이 쓰이게), 로그인 때와 같이 시작하고 답을 기다림</summary>
    public static int StartServer(Config config)
    {
        if (!OperatingSystem.IsWindows()) throw new ConfigError("--start는 Windows에서 씁니다. Linux에서는 i-api 를 서비스로 등록하세요.");
        InstallStartup(config.SetArgs, Util.SamePath(config.SettingsFile, Settings.DefaultFile) ? null : config.SettingsFile);
        if (!Config.IsLoopback(config.Bind)) AllowFirewall(config.Port);
        if (StopRunning() > 0)
        {
            Console.WriteLine("실행 중이던 서버를 멈추고 다시 시작합니다.");
            Thread.Sleep(1000);
        }
        Native.Start($"wscript.exe \"{Path.Combine(StartupFolder(), StartupScript)}\"", Path.GetDirectoryName(Program.ExecutablePath), Native.CreateNoWindow);
        if (Answers(config))
        {
            string scheme = config.TlsCert.Length > 0 ? "https" : "http";
            var addresses = config.Host.Length > 0 ? [config.Host] : Config.LocalAddresses() is { Count: > 0 } found ? found : ["이 PC의 IP"];
            Console.WriteLine($"\nAPI 서버 i-api {Program.Version}이(가) 실행 중입니다: " + string.Join(", ", addresses.Select(address => $"{scheme}://{address}:{config.Port}")));
            if (config.Host.Length == 0) Console.WriteLine("앱 .env의 RELAY_HOST에 위 IP(이 PC의 내부 IP)를 쓰면 됩니다.");
            return 0;
        }
        Console.WriteLine($"\nAPI 서버가 응답하지 않습니다. 로그를 확인하세요: {config.LogFile}");
        try
        {
            foreach (string line in File.ReadAllLines(config.LogFile, Encoding.UTF8).TakeLast(5)) Console.WriteLine("  " + line);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return 1;
    }

    /// <summary>관리 창이 씀: 돌고 있으면 멈추고 창 없이 다시 시작 (로그인 자동 실행 · 방화벽은 그대로, Windows 확인 창 없음). 답하면 0</summary>
    public static int RestartServer(Config config)
    {
        if (!OperatingSystem.IsWindows()) throw new ConfigError("--restart는 Windows에서 씁니다.");
        if (StopRunning() > 0) Thread.Sleep(1000);
        Native.Start(ServerCommand(config), Path.GetDirectoryName(Program.ExecutablePath), Native.DetachedProcess | Native.CreateNewProcessGroup | Native.CreateNoWindow);
        if (Answers(config))
        {
            Console.WriteLine($"API 서버 {Program.Version} 실행 중: {config.HealthUrl[..^"/health".Length]}");
            return 0;
        }
        Console.WriteLine($"API 서버가 응답하지 않습니다. 로그: {config.LogFile}");
        return 1;
    }

    // ── 방화벽 (Windows 는 프로그램마다 들어오는 연결을 허용: 서버 자신의 규칙은 어느 프로그램이든 그 포트를 엶) ─────────────

    public static string FirewallRuleName(int port) => $"i-api-{port}";

    /// <summary>2.23 전에 만든 규칙. 그대로 인정 (이름을 바꾸려면 관리자 권한 = Windows 확인 창)</summary>
    public static string OldFirewallRuleName(int port) => $"TerminalNotesRelay-{port}";

    /// <summary>포트의 규칙 이름 (새 이름, 없으면 2.23 전 이름), 둘 다 없으면 "", Windows 가 아니거나 netsh 에 묻지 못하면 null</summary>
    public static string? FirewallRule(int port)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (string name in new[] { FirewallRuleName(port), OldFirewallRuleName(port) })
        {
            int? code = RunHidden("netsh", ["advfirewall", "firewall", "show", "rule", $"name={name}"], 20);
            if (code is null) return null;
            if (code == 0) return name;
        }
        return "";
    }

    public static bool? FirewallAllowed(int port) => FirewallRule(port) is { } rule ? rule.Length > 0 : null;

    public static string FirewallText(Config config)
    {
        if (Config.IsLoopback(config.Bind)) return "방화벽: 이 PC 안에서만 받습니다(RELAY_BIND).";
        string? rule = FirewallRule(config.Port);
        if (rule is { Length: > 0 }) return $"방화벽: 다른 PC의 TCP {config.Port} 접속 허용({rule})";
        if (rule is null) return "방화벽: 확인하지 못했습니다(Windows가 아니면 운영체제 방화벽을 직접 확인하세요).";
        return $"방화벽: TCP {config.Port} 허용 규칙 없음 — 다른 PC가 접속하지 못할 수 있습니다(i-api.exe --allow-firewall 또는 서버 시작.cmd)";
    }

    /// <summary>서버 포트에 들어오는 TCP 규칙을 모든 네트워크 프로필에. 관리자 권한이 없으면 Windows 가 한 번 물음 (UAC)</summary>
    public static bool AllowFirewall(int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine($"Windows가 아니므로 운영체제 방화벽에서 TCP {port}를 직접 여세요(예: sudo ufw allow {port}/tcp).");
            return false;
        }
        string name = FirewallRuleName(port);
        if (FirewallRule(port) is { Length: > 0 } rule)
        {
            Console.WriteLine($"방화벽: 이미 다른 PC의 TCP {port} 접속을 허용했습니다({rule}).");
            return true;
        }
        string[] arguments = ["advfirewall", "firewall", "add", "rule", $"name={name}", "dir=in", "action=allow", "protocol=TCP", $"localport={port}", "profile=any"];
        int? code = null;
        if (IsAdministrator()) RunHidden("netsh", arguments, 60);
        else
        {
            Console.WriteLine("방화벽에 포트를 열려면 관리자 승인이 필요합니다. Windows 확인 창에서 '예'를 누르세요(2분 안에).");
            // 1223 (ERROR_CANCELLED): 창에서 거절했거나 2분쯤 지나 Windows 가 취소함
            string command = "try { $p = Start-Process -FilePath netsh -ArgumentList '" + string.Join(" ", arguments) +
                             "' -Verb RunAs -Wait -PassThru -WindowStyle Hidden; exit $p.ExitCode } catch { exit 1223 }";
            code = RunHidden("powershell", ["-NoProfile", "-NonInteractive", "-Command", command], 300);
        }
        if (FirewallAllowed(port) == true)
        {
            Console.WriteLine($"방화벽: 다른 PC가 TCP {port}로 접속할 수 있게 허용했습니다({name}).");
            return true;
        }
        if (code == 1223) Console.WriteLine("Windows 확인 창에서 '예'를 누르지 않았습니다(2분 동안 응답이 없으면 Windows가 취소합니다). 다시 실행하고 '예'를 누르세요.");
        Console.WriteLine("방화벽을 열지 못했습니다. 관리자 PowerShell에서 실행해도 됩니다(그룹 정책이 막으면 PC 관리자에게 요청):\n  netsh " + string.Join(" ", arguments));
        return false;
    }
}
