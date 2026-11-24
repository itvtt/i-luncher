using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace IApi;

/// <summary>
/// i-api: API 서버 (예전 이름 Terminal Notes 중계 서버, 2.23 까지 Python relay.py). 이 PC 의 앱들이 쓰는 API 를 한 곳에서:
/// i-luncher 서버 목록 · 사용 기록 · 관리 창, Terminal Notes 스킬 · 플러그인 · 공지, GitHub 중계, (설정하면) EQ Status 설비 상태.
///
///     i-api.exe                         더블클릭: 관리 창 (포트 · 서버 · API 켜고 끄기 · 앱 목록 · 릴리스 · 러너 · 공지 · 스킬)
///     i-api.exe --port 2003             창 없이: 포트를 정하고 로그인 자동 실행 · 방화벽 · 서버 (다시) 시작
///     i-api.exe --set RELAY_PORT=5081   이번 실행만 설정 값을 바꿈
///     i-api.exe --settings-from D:\예전\relay.py   예전 relay.py 의 SETTINGS 를 설정 파일로 옮김
///     i-api.exe --check                 설정 · 방화벽만 확인하고 끝냄 (비밀값은 보이지 않음)
///     i-api.exe --start | --stop        자동 실행 등록 · 방화벽 후 창 없이 시작 / 멈추고 자동 실행 해제 (서버 시작.cmd · 서버 중지.cmd)
///     i-api.exe --restart | --halt      창 없이 다시 시작 / 멈추기만 (관리 창이 씀)
///     i-api.exe --allow-firewall        다른 PC 가 접속하도록 Windows 방화벽에 포트를 엶 (관리자 확인 창)
///     i-api.exe --install-startup [--no-firewall] | --uninstall-startup
///     i-api.exe --backup | --restore-backup [ZIP]
///     i-api.exe --start-runners         켜 둔 GitHub 러너를 시작하고 끝냄 (로그온 작업 GitHub Runners)
/// </summary>
internal static class Program
{
    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>이 서버 프로그램 (도우미 · 자동 실행 · 로그온 작업이 부름)</summary>
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "i-api.exe" : "i-api");

    private sealed class Arguments
    {
        public Options Options { get; } = new();
        public HashSet<string> Flags { get; } = [];
        public string? SettingsFrom, RestoreBackup, CtrlBreak;
    }

    private static readonly string[] FlagNames =
    [
        "--check", "--quiet", "--allow-firewall", "--install-startup", "--no-firewall", "--uninstall-startup", "--start", "--stop", "--restart", "--halt",
        "--version", "--start-runners", "--backup", "--setup", "--help", "-h",
    ];

    private static Arguments Parse(string[] argv)
    {
        var parsed = new Arguments();
        for (int index = 0; index < argv.Length; index++)
        {
            string argument = argv[index];
            string name = argument;
            string? value = null;
            int equals = argument.IndexOf('=');
            if (argument.StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                name = argument[..equals];
                value = argument[(equals + 1)..];
            }
            string Next() => value ?? (index + 1 < argv.Length ? argv[++index] : throw new ConfigError($"{name} 다음에 값을 쓰세요."));
            switch (name)
            {
                case "--set": parsed.Options.Sets.Add(Next()); break;
                case "--settings": parsed.Options.SettingsFile = Next(); break;
                case "--settings-from": parsed.SettingsFrom = Next(); break;
                case "--port": parsed.Options.Port = Next(); break;
                case "--bind": parsed.Options.Bind = Next(); break;
                case "--skills-dir": parsed.Options.SkillsDir = Next(); break;
                case "--ctrl-break": parsed.CtrlBreak = Next(); break;
                case "--restore-backup":
                    parsed.RestoreBackup = value ?? (index + 1 < argv.Length && !argv[index + 1].StartsWith("--", StringComparison.Ordinal) ? argv[++index] : "");
                    break;
                default:
                    if (!FlagNames.Contains(name)) throw new ConfigError($"모르는 옵션입니다: {argument} (i-api.exe --help)");
                    parsed.Flags.Add(name);
                    break;
            }
        }
        return parsed;
    }

    public static int Main(string[] argv)
    {
        ConnectConsole();
        try { return Run(argv); }
        catch (ConfigError problem)
        {
            Console.Error.WriteLine(problem.Message);
            return 2;
        }
    }

    /// <summary>창 없는 프로그램이라 명령 줄에서 부르면 부른 쪽 콘솔에 씀 (출력을 받는 쪽이 있으면 그대로)</summary>
    private static void ConnectConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Console.IsOutputRedirected)
        {
            // 관리 창 · 스크립트가 출력을 받음: 한글이 깨지지 않게 UTF-8 로 (받는 쪽도 UTF-8 로 읽음)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            if (Console.IsErrorRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
            return;
        }
        try
        {
            if (!Native.AttachConsole(-1)) return;
            var output = new StreamWriter(new FileStream("CONOUT$", FileMode.Open, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(output);
            Console.SetError(output);
            Console.WriteLine();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static int Run(string[] argv)
    {
        var args = Parse(argv);
        if (args.Flags.Contains("--help") || args.Flags.Contains("-h"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Name + " " + Version + ": 옵션은 CDEP\\README.md · i-api.exe 옆 README.md 를 보세요.");
            return 0;
        }
        if (args.Flags.Contains("--version"))
        {
            Console.WriteLine(Version);
            return 0;
        }
        if (args.CtrlBreak is { } pid) return int.TryParse(pid, out int target) ? Native.CtrlBreakHelper(target) : 2;
        // 손으로 실행 (더블클릭 · 바로 가기, 또는 --setup): 관리 창 (포트 · 서버 · API 켜고 끄기 · 앱 목록 · 릴리스 · 러너 · 공지 · 스킬).
        // 로그인 자동 실행(wscript) · 관리 창이 띄운 서버는 그냥 서버로 돎
        if (args.Flags.Contains("--setup") || argv.Length == 0 && StartedByHand())
        {
#if WINDOWS
            return ManagerWindow.Run();
#else
            throw new ConfigError("포트를 정해 실행하세요: i-api --port <포트>");
#endif
        }
        // i-api.exe --port <포트> 만: 그 포트로 정하고 (설정 파일에 씀) 로그인 자동 실행 · 방화벽, 서버를 (다시) 시작
        if (args.Options.Port is { } chosen && args.Flags.Count == 0 && args.SettingsFrom is null && args.RestoreBackup is null)
        {
            if (!int.TryParse(chosen, out int port)) throw new ConfigError($"--port 는 숫자로 씁니다: {chosen}");
            var (ok, message) = Configure(args.Options, port);
            Console.WriteLine(message);
            return ok ? 0 : 1;
        }
        if (args.Flags.Contains("--halt"))
        {
            Console.WriteLine($"멈춘 서버 {Setup.StopRunning()}개");
            return 0;
        }
        if (args.SettingsFrom is { } source) return CopySettings(source, args.Options);
        if (args.Flags.Contains("--uninstall-startup"))
        {
            Setup.UninstallStartup();
            return 0;
        }
        if (args.Flags.Contains("--stop"))
        {
            int stopped = Setup.StopRunning();
            Setup.UninstallStartup(onlyThis: true);
            Console.WriteLine(stopped > 0 ? "API 서버를 멈췄습니다." : "실행 중인 API 서버가 없습니다.");
            return 0;
        }
        var config = new Config(args.Options);
        if (args.Flags.Contains("--start-runners")) return StartRunnersAtLogon(config);  // 주소 확인보다 먼저: 로그온 직후에는 네트워크가 아직 없을 수 있고 러너는 필요 없음
        if (args.Flags.Contains("--allow-firewall"))
        {
            bool opened = Setup.AllowFirewall(config.Port);
            if (config.UsesEquipmentPort) opened = Setup.AllowFirewall(config.EquipmentPort) && opened;
            return opened ? 0 : 1;
        }
        if (args.Flags.Contains("--backup"))
        {
            string? made = DataFolders.BackupStore(config);
            Console.WriteLine(made is not null ? $"백업했습니다: {made}" : "바뀐 것이 없거나 스토어가 비어 있어 새 백업을 만들지 않았습니다.");
            return 0;
        }
        if (args.RestoreBackup is { } backup)
        {
            if (Setup.Answers(config, 1))
            {
                Console.Error.WriteLine("API 서버가 실행 중입니다. 서버 중지.cmd(또는 --stop)로 멈춘 뒤 다시 실행하세요.");
                return 1;
            }
            foreach (string line in DataFolders.RestoreBackup(config, backup.Length > 0 ? backup : null)) Console.WriteLine(line);
            return 0;
        }
        try { config.Validate(); }
        catch (ConfigError problem)
        {
            // 로그인 때 창 없이 시작하면 콘솔이 없음: 서버가 시작하지 않은 이유는 로그에
            if (!args.Flags.Contains("--check"))
            {
                Log.Setup(config.LogFile, false);
                Log.Error(problem.Message);
            }
            throw;
        }
        if (args.Flags.Contains("--start")) return Setup.StartServer(config);
        if (args.Flags.Contains("--restart")) return Setup.RestartServer(config);
        if (args.Flags.Contains("--install-startup"))
        {
            Setup.InstallStartup(config.SetArgs, Util.SamePath(config.SettingsFile, Settings.DefaultFile) ? null : config.SettingsFile);
            // 서버 프로그램이 바뀌면 예전 "허용" 답이 덮지 않음: 포트 규칙을 둠
            if (!Config.IsLoopback(config.Bind) && !args.Flags.Contains("--no-firewall"))
            {
                Setup.AllowFirewall(config.Port);
                if (config.UsesEquipmentPort) Setup.AllowFirewall(config.EquipmentPort);
            }
            return 0;
        }
        if (args.Flags.Contains("--check"))
        {
            Console.WriteLine(string.Join(Environment.NewLine, config.Summary().Append(Setup.FirewallText(config))));
            return 0;
        }
        return Serve(config, quiet: args.Flags.Contains("--quiet"));
    }

    /// <summary>탐색기(더블클릭 · 바로 가기)에서 실행됨</summary>
    private static bool StartedByHand()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var processes = Native.Processes();
        int parent = processes.FirstOrDefault(process => process.Pid == Environment.ProcessId).Parent;
        return processes.Any(process => process.Pid == parent && process.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 이 PC 에서 이 포트로 서버를 켬: 설정 파일에 포트를 쓰고, 로그인 자동 실행을 등록하고 (재부팅해도 다시 켜짐), 방화벽을 열고 (없으면 Windows 확인 창),
    /// 돌고 있던 이 서버를 멈춘 뒤 창 없이 시작해 응답을 확인. (성공, 알릴 글)
    /// </summary>
    internal static (bool Ok, string Message) Configure(Options given, int port)
    {
        if (port is < 1 or > 65535) return (false, "포트는 1~65535 의 숫자로 씁니다.");
        string file = Path.GetFullPath(given.SettingsFile ?? Settings.DefaultFile);
        string text = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Settings.EnsureComplete(file);
        var options = new Options { SettingsFile = given.SettingsFile, Bind = given.Bind, Port = text };
        options.Sets.AddRange(given.Sets);
        Config config;
        try
        {
            config = new Config(options);
            config.Validate();
        }
        catch (ConfigError problem) { return (false, problem.Message); }
        bool Free()
        {
            try
            {
                using var probe = new System.Net.Sockets.TcpListener(config.Bind is "" or "0.0.0.0" ? System.Net.IPAddress.Any : System.Net.IPAddress.Parse(config.Bind), port);
                probe.Start();
                probe.Stop();
                return true;
            }
            catch (Exception error) when (error is System.Net.Sockets.SocketException or FormatException) { return false; }
        }
        string inUse = $"포트 {port} 을(를) 이 PC 의 다른 프로그램이 쓰고 있습니다. 다른 포트를 고르세요.";
        // 다른 포트로 바꿀 때는 돌고 있는 서버를 멈추기 전에 확인 (쓸 수 없는 포트면 서버는 그대로)
        bool samePort = Settings.Read(file).GetValueOrDefault("RELAY_PORT", "").Trim() == text;
        if (!samePort && !Free()) return (false, inUse);
        if (Setup.StopRunning() > 0) Thread.Sleep(1000);
        if (samePort && !Free()) return (false, inUse);
        Settings.SetValue(file, "RELAY_PORT", text);
        var output = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            Setup.InstallStartup(config.SetArgs, Util.SamePath(config.SettingsFile, Settings.DefaultFile) ? null : config.SettingsFile);
            if (!Config.IsLoopback(config.Bind))
            {
                if (Setup.FirewallAllowed(port) == false) Setup.AllowFirewall(port);
                if (config.UsesEquipmentPort && Setup.FirewallAllowed(config.EquipmentPort) == false) Setup.AllowFirewall(config.EquipmentPort);
            }
            if (Setup.RestartServer(config) != 0)
            {
                string last = "";
                try { last = File.ReadLines(config.LogFile, Encoding.UTF8).LastOrDefault() ?? ""; } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                return (false, $"서버가 응답하지 않습니다. 로그: {config.LogFile}" + (last.Length > 0 ? "\n" + last : ""));
            }
        }
        finally { Console.SetOut(output); }
        string scheme = config.TlsCert.Length > 0 ? "https" : "http";
        var addresses = config.Host.Length > 0 ? [config.Host] : Config.LocalAddresses();
        string where = string.Join(", ", addresses.Select(address => $"{scheme}://{address}:{port}"));
        string firewall = Config.IsLoopback(config.Bind) || Setup.FirewallAllowed(port) != false ? "" : " · 방화벽이 닫혀 있어 다른 PC 는 접속하지 못합니다 ([실행] 후 Windows 확인 창에서 \"예\")";
        return (true, $"실행 중: {where}{firewall}");
    }

    /// <summary>예전 relay.py 의 SETTINGS (또는 다른 settings.txt) 를 이 서버의 설정 파일로 옮김</summary>
    private static int CopySettings(string source, Options options)
    {
        string path = Path.GetFullPath(source);
        if (!File.Exists(path)) throw new ConfigError($"없는 파일입니다: {path}");
        string target = Path.GetFullPath(options.SettingsFile ?? Settings.DefaultFile);
        if (Util.SamePath(path, target)) throw new ConfigError("다른 설정 파일을 고르세요(지금 이 설정 파일입니다).");
        var values = path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? Settings.FromRelayScript(Util.ReadText(path)) : Settings.Read(path);
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in Settings.Defaults) merged[setting.Key] = setting.Value;
        foreach (var (key, value) in values)
            if (value != "None") merged[key] = value;
        var added = Settings.Defaults.Select(setting => setting.Key).Where(key => !values.ContainsKey(key)).ToList();
        Util.AtomicWrite(target, Util.Utf8.GetBytes(Settings.Compose(merged)));
        if (added.Count > 0) Console.WriteLine($"새 설정 {added.Count}개를 기본값으로 덧붙였습니다: {string.Join(", ", added)}");
        Console.WriteLine($"{path}의 설정을 {target}에 옮겼습니다.");
        // 예전 서버는 스토어를 자기 옆(2.10 전)에 두었을 수 있음: 그 설정이 가리키는 데이터 폴더로 복사해 새 서버가 같은 스킬로 시작
        var dataKeys = new[] { "RELAY_DATA_ROOT", "RELAY_SKILLS_DIR", "RELAY_PLUGINS_DIR", "RELAY_ANNOUNCEMENTS_DIR", "RELAY_BACKUP_DIR", "RELAY_LOG_FILE" };
        var copied = new Options { SettingsFile = target };
        foreach (string key in dataKeys)
            if (values.TryGetValue(key, out var value) && value.Trim().Length > 0 && value != "None") copied.Sets.Add($"{key}={value.Trim()}");
        var config = new Config(copied, new Dictionary<string, string>());
        foreach (string line in DataFolders.AdoptOldData(config, [Path.GetDirectoryName(path)!], includeDefaults: false)) Console.WriteLine(line);
        return 0;
    }

    /// <summary>--start-runners (로그온 작업 GitHub Runners): 켜 둔 러너를 시작하고 끝냄</summary>
    private static int StartRunnersAtLogon(Config config)
    {
        Log.Setup(config.LogFile, false);
        var runners = new Runners(config);
        string problem = runners.Available();
        if (problem.Length > 0)
        {
            Log.Warning("runners not started: " + problem);
            return 1;
        }
        try
        {
            foreach (string line in runners.StartOn()) Log.Info("logon task: " + line);
        }
        catch (RelayError error)
        {
            Log.Warning("logon task: " + error.Message);
            return 1;
        }
        return 0;
    }

    /// <summary>서버로 돎: 멈추라는 신호(--halt · --stop · 관리 창)가 오거나 Ctrl+C 까지</summary>
    private static int Serve(Config config, bool quiet)
    {
        // 로그를 열기 전에 (로그가 새 데이터 폴더를 만듦), 다른 서버가 그 포트에서 아직 답하면 하지 않음
        string? moved = Setup.Answers(config, 0) ? null : DataFolders.MoveRenamedData(config);
        Log.Setup(config.LogFile, !quiet);
        if (moved is not null) Log.Info(moved);
        if (Util.SamePath(config.SettingsFile, Settings.DefaultFile) && Settings.EnsureComplete(config.SettingsFile) is { Count: > 0 } added)
            Log.Info($"설정 파일에 새 설정 {added.Count}개를 기본값으로 덧붙였습니다: {string.Join(", ", added)}");
        using var instance = new Setup.Instance(ExecutablePath);
        RelayServer server;
        try
        {
            DataFolders.AdoptOldData(config);
            server = new RelayServer(config);
            server.Start();
        }
        catch (Exception error) when (error is System.Net.Sockets.SocketException or IOException or System.Security.Cryptography.CryptographicException)
        {
            Log.Error(error is System.Security.Cryptography.CryptographicException
                ? $"HTTPS 인증서(RELAY_TLS_CERT·RELAY_TLS_KEY)를 읽지 못했습니다: {error.Message}"
                : $"포트 {config.Port}을(를) 열지 못했거나 인증서 파일이 없습니다(이미 실행 중일 수 있습니다): {error.Message}");
            return 1;
        }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            instance.Stop.Set();
        };
        new Thread(() => Housekeeping(server, stop.Token)) { IsBackground = true, Name = "housekeeping" }.Start();
        new Thread(() => GitHubSync(server, stop.Token)) { IsBackground = true, Name = "github-sync" }.Start();
        new Thread(server.Runners.RepointTask) { IsBackground = true, Name = "runners-task" }.Start();
        foreach (string line in config.Summary()) Log.Info(line);
        if (!Config.IsLoopback(config.Bind) && Setup.FirewallAllowed(config.Port) == false) Log.Warning(Setup.FirewallText(config));
        if (server.EquipmentPort is { } equipment && !Config.IsLoopback(config.Bind) && Setup.FirewallAllowed(equipment) == false)
            Log.Warning($"방화벽: 설비 상태 TCP {equipment} 허용 규칙 없음 — 다른 PC의 EQ Status가 접속하지 못할 수 있습니다(i-api.exe --allow-firewall)");
        Log.Info($"API 서버 i-api {Version} 시작 (.NET {Environment.Version.ToString(3)})");
        instance.Stop.WaitOne();
        stop.Cancel();
        server.Dispose();
        Log.Info("API 서버 종료");
        return 0;
    }

    private static void Housekeeping(RelayServer server, CancellationToken stop)
    {
        bool first = true;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                server.Skills.Housekeep();
                server.Plugins.Housekeep();
            }
            catch (Exception error) { Log.Exception("housekeeping failed", error); }
            // 시작할 때 한 번, 그다음 하루에 한 번. 스토어가 그대로면 새로 쓰지 않음
            try
            {
                var backups = DataFolders.StoreBackups(server.Config);
                if (first || backups.Count == 0 || (DateTime.UtcNow - File.GetLastWriteTimeUtc(backups[0])).TotalSeconds >= 86400)
                {
                    if (DataFolders.BackupStore(server.Config) is { } made) Log.Info("store backup " + made);
                }
            }
            catch (Exception error) { Log.Exception("store backup failed", error); }
            first = false;
            stop.WaitHandle.WaitOne(TimeSpan.FromHours(1));
        }
    }

    /// <summary>깃허브 파일에 링크한 스킬 · 플러그인이 그 파일을 따라감: 시작하고 조금 뒤, 그다음 RELAY_GITHUB_SYNC_MINUTES 마다</summary>
    private static void GitHubSync(RelayServer server, CancellationToken stop)
    {
        int wait = Math.Min(5, server.Config.GitHubSyncSeconds);
        while (!stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait)))
        {
            wait = server.Config.GitHubSyncSeconds;
            foreach (string name in server.Skills.Linked())
            {
                try
                {
                    if (server.Skills.SyncSource(name, server.Config.GitHubToken) is { } number) Log.Info($"skill {name} v{number} from GitHub");
                }
                catch (Exception error) { Log.Exception($"GitHub sync failed: {name}", error); }
            }
            foreach (string name in server.Plugins.Linked())
            {
                try
                {
                    if (server.Plugins.SyncSource(name, server.Config.GitHubToken) is { } number) Log.Info($"plugin {name} v{number} from GitHub");
                }
                catch (Exception error) { Log.Exception($"GitHub sync failed: plugin {name}", error); }
            }
        }
    }
}
