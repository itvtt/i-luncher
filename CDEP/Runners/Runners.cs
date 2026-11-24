using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IApi;

/// <summary>이 PC 의 프로세스를 한 번 봄. 프로그램 파일 경로는 필요할 때 한 번씩</summary>
internal sealed class ProcessTable
{
    /// <summary>확인 코드가 프로세스 목록을 바꿈</summary>
    internal static Func<List<(int Pid, int Parent, string Name)>> Source = Native.Processes;
    /// <summary>확인 코드가 폴더 안의 프로세스를 바꿈 (null 이면 실제로 봄)</summary>
    internal static Func<string, List<(int Pid, string Name)>?>? UnderOverride;

    private readonly Dictionary<int, (int Parent, string Name)> _entries = [];
    private readonly Dictionary<int, string> _paths = [];

    public ProcessTable()
    {
        foreach (var (pid, parent, name) in Source()) _entries[pid] = (parent, name);
    }

    public string Name(int pid) => _entries.TryGetValue(pid, out var entry) ? entry.Name : "";
    public int Parent(int pid) => _entries.TryGetValue(pid, out var entry) ? entry.Parent : 0;

    public string PathOf(int pid)
    {
        if (!_paths.TryGetValue(pid, out var path)) _paths[pid] = path = Native.ProcessImage(pid);
        return path;
    }

    /// <summary>[(pid, 프로그램 이름)] 프로그램 파일이 폴더 안에 있는 것: 러너의 리스너, 작업자, 단계를 돌리는 node</summary>
    public List<(int Pid, string Name)> Under(string folder)
    {
        if (UnderOverride?.Invoke(folder) is { } faked) return faked;
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar };
        try
        {
            var info = new DirectoryInfo(folder);
            string resolved = info.ResolveLinkTarget(true)?.FullName ?? info.FullName;
            prefixes.Add(Path.TrimEndingDirectorySeparator(resolved) + Path.DirectorySeparatorChar);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return _entries.Where(pair => pair.Key != 0 && prefixes.Any(prefix => PathOf(pair.Key).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => (pair.Key, pair.Value.Name)).ToList();
    }

    public HashSet<int> Descendants(HashSet<int> roots)
    {
        var found = new HashSet<int>();
        var queue = new Stack<int>(roots);
        while (queue.Count > 0)
        {
            int pid = queue.Pop();
            foreach (var (child, (parent, _)) in _entries)
                if (parent == pid && !found.Contains(child) && !roots.Contains(child))
                {
                    found.Add(child);
                    queue.Push(child);
                }
        }
        return found;
    }
}

/// <summary>
/// 이 PC 의 GitHub Actions self-hosted 러너: RUNNERS_ROOT 아래 폴더마다 하나 (러너의 원래 모양: .runner, bin, externals, run.cmd 와 작업에 넘기는 .env).
/// i-luncher 관리자 메뉴의 러너 화면이 보고, 더하고(저장소에 등록), .env 를 고치고, 시작 · 멈추기 · 지우기. 로그온 작업 하나(TaskName)가 켜 둔 러너를 모두 시작;
/// 여기서 멈춘 러너는 여기서 다시 시작할 때까지 꺼져 있음.
/// 러너는 자기 콘솔에서 숨어서 돎. 멈추기는 그 콘솔에 Ctrl+Break: 러너가 GitHub 세션을 닫고 끝남. 대신 강제로 끝내면 GitHub 에 세션이 2분쯤 남아
/// 그동안의 시작은 바로 끝남 ("A session for this runner already exists")
/// </summary>
internal sealed partial class Runners
{
    public const string TaskName = "GitHub Runners";
    private const string StateFile = "runners.json";
    private const int EnvMax = 16_384, GitHubTtl = 30;
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "run-helper.cmd", "update.finished", StateFile };
    private static readonly string[] SessionLeft = ["session for this runner already exists", "session conflict"];

    [GeneratedRegex("^(GitHub runner .+|i-luncher build runner)$", RegexOptions.IgnoreCase)] private static partial Regex LegacyTask();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")] private static partial Regex RunnerId();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*=")] private static partial Regex EnvLine();
    [GeneratedRegex(@"bin\.(\d+\.\d+\.\d+)$")] private static partial Regex BinVersion();
    [GeneratedRegex(@"\$env:([A-Z][A-Z0-9_]*_ENV_PATH)\b")] private static partial Regex EnvPathVariable();
    [GeneratedRegex("[^A-Za-z0-9._-]")] private static partial Regex NotNameCharacter();
    [GeneratedRegex("[^A-Za-z0-9 ._-]")] private static partial Regex NotFileCharacter();

    private readonly Config _config;
    private readonly Releases? _releases;
    private readonly Lock _lock = new();
    private (List<string>? Names, long Until) _tasks = (null, 0);

    // 확인 코드가 바꾸는 것 (실제 프로그램 · 작업 스케줄러를 건드리지 않게)
    internal Func<List<string>> ScheduledTasks = ListScheduledTasks;
    internal Func<string, string> TaskDefinition = ReadTaskDefinition;
    internal Func<string, string, string> RegisterTask = CreateTask;
    internal Func<string, bool> DeleteTask = name => Native.Run("schtasks", ["/Delete", "/TN", name, "/F"]).Code == 0;
    internal Action<string> Launch;
    internal Func<List<string>, double, Dictionary<string, string>> Await;
    internal Func<string, bool, string> StopRunner;
    internal Action<string> Prepare;
    internal Action<string, string, string, string> Configure;

    public string? Root => _config.RunnersRoot;

    public Runners(Config config, Releases? releases = null)
    {
        _config = config;
        _releases = releases;
        Launch = LaunchRunner;
        Await = AwaitRunners;
        StopRunner = StopRunnerCore;
        Prepare = PrepareFolder;
        Configure = ConfigureRunner;
    }

    // 어디 · 무엇 ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>이 PC 에서 러너 화면이 동작하지 못하는 이유, 되면 ""</summary>
    public string Available()
    {
        if (!OperatingSystem.IsWindows()) return "러너 관리는 Windows에서만 합니다.";
        if (Root is null) return "러너 폴더가 정해지지 않았습니다 (설정 파일의 RUNNERS_ROOT).";
        if (!Directory.Exists(Root)) return $"러너 폴더가 없습니다: {Root}";
        return "";
    }

    private void Require()
    {
        string problem = Available();
        if (problem.Length > 0) throw new RelayError(503, problem);
    }

    private static bool IsRunner(string path) =>
        Directory.Exists(path) && RunnerId().IsMatch(Path.GetFileName(path)) && (File.Exists(Path.Combine(path, ".runner")) || File.Exists(Path.Combine(path, "config.cmd")));

    public List<string> Folders()
    {
        try { return Directory.EnumerateDirectories(Root!).Where(IsRunner).OrderBy(path => Path.GetFileName(path).ToLowerInvariant(), StringComparer.Ordinal).ToList(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentNullException) { return []; }
    }

    public string Folder(string? runnerId)
    {
        Require();
        string? path = runnerId is not null && RunnerId().IsMatch(runnerId) ? Path.Combine(Root!, runnerId) : null;
        if (path is null || !IsRunner(path)) throw new RelayError(404, $"러너를 찾지 못했습니다: {runnerId}");
        return path;
    }

    private HashSet<string> Off() =>
        (Util.ReadJsonObject(Path.Combine(Root!, StateFile))?["off"] as JsonArray ?? []).Select(Util.AsString).OfType<string>()
            .Select(item => item.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    private void SetOn(string runnerId, bool on)
    {
        var off = Off();
        off.Remove(runnerId.ToLowerInvariant());
        if (!on) off.Add(runnerId.ToLowerInvariant());
        Util.WriteJson(Path.Combine(Root!, StateFile), new JsonObject { ["off"] = new JsonArray(off.Order(StringComparer.Ordinal).Select(item => (JsonNode)item).ToArray()) });
    }

    private static JsonObject Registration(string folder) => Util.ReadJsonObject(Path.Combine(folder, ".runner")) ?? [];

    private static string RepositoryOf(string url)
    {
        var parts = GitHubLinks.SplitUrl(url).Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] + "/" + parts[1] : "";
    }

    /// <summary>러너의 버전: 스스로 업데이트한 러너의 bin 은 bin.&lt;버전&gt; 으로 가는 정션</summary>
    public static string RunnerVersion(string folder)
    {
        try
        {
            string? target = new DirectoryInfo(Path.Combine(folder, "bin")).LinkTarget;
            var match = target is null ? Match.Empty : BinVersion().Match(target.TrimEnd('\\', '/'));
            return match.Success ? match.Groups[1].Value : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }

    private JsonObject Local(string folder, ProcessTable table, HashSet<string> off)
    {
        var info = Registration(folder);
        string url = Util.Or(info["gitHubUrl"], "");
        var processes = table.Under(folder);
        var names = processes.Select(process => process.Name.ToLowerInvariant()).ToList();
        string env;
        try { env = File.Exists(Path.Combine(folder, ".env")) ? Util.ReadText(Path.Combine(folder, ".env")).Replace("\r\n", "\n") : ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { env = ""; }
        string id = Path.GetFileName(folder);
        return new JsonObject
        {
            ["id"] = id, ["folder"] = folder, ["name"] = Util.Or(info["agentName"], ""), ["registered"] = info.Count > 0,
            ["repository"] = RepositoryOf(url), ["url"] = url, ["on"] = !off.Contains(id.ToLowerInvariant()), ["running"] = processes.Count > 0,
            ["busy"] = names.Contains("runner.worker.exe"), ["pid"] = processes.FirstOrDefault(process => process.Name.Equals("runner.listener.exe", StringComparison.OrdinalIgnoreCase)).Pid,
            ["version"] = RunnerVersion(folder), ["env"] = env, ["github"] = null, ["githubError"] = "",
        };
    }

    // GitHub ─────────────────────────────────────────────────────────────────────────────────────

    private (int Status, JsonNode? Json) Get(string path, int ttl) =>
        _releases is null ? throw new RelayError(503, "GitHub에 연결하지 않았습니다.") : _releases.Get(path, ttl);

    /// <summary>GitHub 이 지금 쓰는 소유자/이름 (이름을 바꾼 저장소는 새 이름으로 답함)</summary>
    private string Canonical(JsonNode? repository)
    {
        if (Util.AsString(repository) is not { } text || text.Trim().Length == 0) throw new RelayError(400, "저장소(소유자/이름)가 필요합니다.");
        string full = text.Trim();
        if (full.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || full.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) full = RepositoryOf(full);
        if (full.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) full = full[..^4];
        var (status, answer) = Get(Releases.Base(full), 300);
        if (status == 200 && answer is JsonObject found && Util.Truthy(found["full_name"])) return Util.Text(found["full_name"]);
        if (status == 404) throw new RelayError(404, $"GitHub에 {full} 저장소가 없거나 이 서버의 토큰으로 열 수 없습니다.");
        throw new RelayError(502, Releases.Problem(status, answer));
    }

    private List<JsonObject> GitHubRunners(string full, int ttl = GitHubTtl)
    {
        var (status, answer) = Get($"{Releases.Base(full)}/actions/runners?per_page=100", ttl);
        if (status != 200 || answer is not JsonObject list)
            throw new RelayError(502, Releases.Problem(status, answer) + (status is 401 or 403 or 404 ? " (토큰에 저장소 관리 권한이 있어야 합니다)" : ""));
        return (list["runners"] as JsonArray ?? []).OfType<JsonObject>().ToList();
    }

    /// <summary>무언가를 바꾸는 GitHub 요청 하나 (등록 토큰, 지우기), 기억하지 않음</summary>
    private (int Status, JsonNode? Json) GitHubCall(string method, string path)
    {
        string token = _releases?.GitHub.Token() ?? "";
        if (token.Length == 0) throw new RelayError(503, "GitHub 토큰이 없습니다 (RELAY_GITHUB_TOKEN 또는 이 PC의 git 자격 증명).");
        return _releases!.Call(method, path, token);
    }

    private string WebUrl(string full) => $"https://{_config.GitHubWebHost}/{full}";

    private void AddGitHub(JsonObject entry)
    {
        if (!Util.IsTrue(entry["registered"]) || Util.Text(entry["repository"]).Length == 0) return;
        try
        {
            string full = Canonical(entry["repository"]);
            entry["repository"] = full;
            entry["url"] = WebUrl(full);
            string name = Util.Text(entry["name"]).ToLowerInvariant();
            var match = GitHubRunners(full).FirstOrDefault(item => Util.Or(item["name"], "").ToLowerInvariant() == name);
            entry["github"] = match is not null
                ? new JsonObject
                {
                    ["id"] = match["id"]?.DeepClone(), ["status"] = Util.Or(match["status"], ""), ["busy"] = Util.Truthy(match["busy"]),
                    ["labels"] = new JsonArray((match["labels"] as JsonArray ?? []).OfType<JsonObject>().Select(label => (JsonNode)Util.Text(label["name"])).ToArray()),
                }
                : new JsonObject { ["id"] = null, ["status"] = "missing", ["busy"] = false, ["labels"] = new JsonArray() };
        }
        catch (RelayError error) { entry["githubError"] = error.Message; }
    }

    // 화면 ───────────────────────────────────────────────────────────────────────────────────────

    public JsonObject List()
    {
        string problem = Available();
        var answer = new JsonObject
        {
            ["available"] = problem.Length == 0, ["message"] = problem, ["root"] = Root ?? "", ["task"] = null, ["runners"] = new JsonArray(), ["repositories"] = new JsonArray(),
        };
        if (problem.Length > 0) return answer;
        var table = new ProcessTable();
        var off = Off();
        var runners = Folders().Select(folder => Local(folder, table, off)).ToList();
        if (runners.Count > 0 && _releases is not null) Parallel.ForEach(runners, new ParallelOptions { MaxDegreeOfParallelism = 8 }, AddGitHub);
        answer["runners"] = new JsonArray(runners.Select(runner => (JsonNode)runner).ToArray());
        answer["task"] = TaskInfo();
        answer["repositories"] = _releases is null ? new JsonArray()
            : new JsonArray(_releases.Projects().Select(project => (JsonNode)new JsonObject { ["repository"] = project.Full, ["name"] = project.Name }).ToArray());
        return answer;
    }

    /// <summary>새 러너에 채울 것: 폴더 · 러너 이름 · .env (저장소의 릴리스 워크플로가 빌드 설정을 읽는 변수, &lt;설정 폴더&gt;\&lt;저장소&gt;.env)</summary>
    public JsonObject Template(string repository)
    {
        Require();
        string full = Canonical(repository);
        string name = full.Split('/', 2)[1];
        string folder = name;
        for (int number = 2; Directory.Exists(Path.Combine(Root!, folder)) || File.Exists(Path.Combine(Root!, folder)); number++) folder = $"{name}-{number}";
        string computer = NotNameCharacter().Replace(Environment.GetEnvironmentVariable("COMPUTERNAME") is { Length: > 0 } pc ? pc : Dns.GetHostName() is { Length: > 0 } host ? host : "PC", "-");
        var taken = new HashSet<string>(StringComparer.Ordinal);
        try { taken = GitHubRunners(full, 0).Select(item => Util.Or(item["name"], "").ToLowerInvariant()).ToHashSet(StringComparer.Ordinal); }
        catch (RelayError) { }
        string runner = Clip($"{computer}-{name}");
        for (int number = 2; taken.Contains(runner.ToLowerInvariant()); number++) runner = Clip($"{computer}-{name}-{number}");
        var (status, workflow) = _releases!.GetRaw($"{Releases.Base(full)}/contents/.github/workflows/{Releases.Workflow}", 60);
        var variable = status == 200 ? EnvPathVariable().Match(workflow) : Match.Empty;
        var lines = new List<string>();
        if (_config.RunnersEnvDir is { } envDir) lines.Add($"{(variable.Success ? variable.Groups[1].Value : "RELEASE_ENV_PATH")}={Path.Combine(envDir, name + ".env")}");
        lines.Add("DOTNET_CLI_TELEMETRY_OPTOUT=1");
        return new JsonObject { ["repository"] = full, ["folder"] = folder, ["name"] = runner, ["env"] = string.Join("\n", lines) + "\n" };
    }

    private static string Clip(string text) => text.Length > 64 ? text[..64] : text;

    // 바꾸기 ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>한 번에 하나만, 로그온 작업의 시작(다른 프로세스)과도</summary>
    private T Changing<T>(Func<T> action)
    {
        lock (_lock)
        {
            using var handle = new FileStream(Path.Combine(Root!, ".runners.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (true)
            {
                try
                {
                    handle.Lock(0, 1);
                    break;
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow > deadline) throw new RelayError(409, "다른 곳에서 러너를 바꾸는 중입니다. 잠시 뒤에 다시 하세요.");
                    Thread.Sleep(500);
                }
            }
            try { return action(); }
            finally
            {
                try { handle.Unlock(0, 1); } catch (IOException) { }
            }
        }
    }

    private static string EnvText(JsonNode? env)
    {
        if (Util.AsString(env) is not { } text || Encoding.UTF8.GetByteCount(text) > EnvMax) throw new RelayError(400, ".env 내용이 없거나 너무 깁니다.");
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim()).ToList();
        for (int index = 0; index < lines.Count; index++)
        {
            string line = lines[index];
            if (line.Length > 0 && !line.StartsWith('#') && !EnvLine().IsMatch(line))
                throw new RelayError(400, $".env {index + 1}번째 줄은 이름=값 이어야 합니다: {(line.Length > 60 ? line[..60] : line)}");
        }
        string joined = string.Join("\r\n", lines).Trim();
        return joined.Length > 0 ? joined + "\r\n" : "";
    }

    /// <summary>run.cmd 를 자기 콘솔에서 숨겨, 이 계정의 지금 환경으로, 가능하면 서버의 작업 개체 밖에서. 출력은 _diag\run.log</summary>
    private void LaunchRunner(string folder)
    {
        string run = Path.Combine(folder, "run.cmd");
        if (!File.Exists(run)) throw new RelayError(409, $"{Path.GetFileName(folder)}: run.cmd가 없습니다.");
        string diag = Path.Combine(folder, "_diag");
        Directory.CreateDirectory(diag);
        string output = Path.Combine(diag, "run.log");
        bool large = File.Exists(output) && new FileInfo(output).Length > 1_048_576;
        string header = $"\r\n== {DateTime.Now:yyyy-MM-dd HH:mm:ss} start (API server {Program.Version}) ==\r\n";
        if (large) File.WriteAllText(output, header, Util.Utf8);
        else File.AppendAllText(output, header, Util.Utf8);
        string command = $"cmd.exe /d /c \"\"{run}\" < NUL >> \"{output}\" 2>&1\"";
        try { Native.Start(command, folder, Native.CreateNewConsole, Native.UserEnvironment()); }
        catch (System.ComponentModel.Win32Exception error) { throw new RelayError(500, $"{Path.GetFileName(folder)}을(를) 시작하지 못했습니다: {error.Message}"); }
    }

    private static string ExitReason(string folder)
    {
        List<string> lines;
        try
        {
            byte[] data = Util.ReadBytes(Path.Combine(folder, "_diag", "run.log"));
            lines = Native.ConsoleText(data[Math.Max(0, data.Length - 8192)..]).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
        // 이번 시작이 쓴 것만 (시작마다 "== ... start" 줄로 시작)
        int last = lines.FindLastIndex(line => line.StartsWith("== ", StringComparison.Ordinal));
        lines = lines.Skip(last + 1).ToList();
        if (lines.Any(line => SessionLeft.Any(text => line.Contains(text, StringComparison.OrdinalIgnoreCase)))) return "GitHub에 이전 세션이 남아 있습니다. 2분쯤 뒤에 다시 시작하세요.";
        if (lines.Count == 0) return "";
        string end = lines[^1].Trim('"');
        return end.Length > 200 ? end[..200] : end;
    }

    /// <summary>러너마다 리스너가 몇 초 돌 때까지 기다림. 그러지 못한 러너는 {폴더: 이유}</summary>
    private Dictionary<string, string> AwaitRunners(List<string> folders, double seconds)
    {
        var seen = folders.ToDictionary(folder => folder, _ => 0L);
        var problems = new Dictionary<string, string>();
        long deadline = Environment.TickCount64 + (long)(seconds * 1000);
        while (seen.Count > 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(500);
            var table = new ProcessTable();
            foreach (string folder in seen.Keys.ToList())
            {
                bool listening = table.Under(folder).Any(process => process.Name.Equals("runner.listener.exe", StringComparison.OrdinalIgnoreCase));
                if (listening && seen[folder] == 0) seen[folder] = Environment.TickCount64;
                else if (listening && Environment.TickCount64 - seen[folder] >= 4000) seen.Remove(folder);
                else if (!listening && seen[folder] != 0)
                {
                    problems[folder] = ExitReason(folder) is { Length: > 0 } reason ? reason : "시작한 뒤 바로 끝났습니다.";
                    seen.Remove(folder);
                }
            }
        }
        foreach (var (folder, since) in seen)
            if (since == 0) problems[folder] = ExitReason(folder) is { Length: > 0 } reason ? reason : "시작하지 않았습니다.";
        return problems;
    }

    private string StartOne(string folder)
    {
        string id = Path.GetFileName(folder);
        if (new ProcessTable().Under(folder).Count > 0) return $"{id}은(는) 이미 실행 중입니다.";
        if (!File.Exists(Path.Combine(folder, ".runner"))) throw new RelayError(409, $"{id}은(는) 등록되지 않은 러너입니다.");
        Launch(folder);
        if (Await([folder], 25).TryGetValue(folder, out string? problem)) throw new RelayError(502, $"{id}: {problem}");
        Log.Info($"runner {id} started");
        return $"{id}을(를) 시작했습니다.";
    }

    /// <summary>러너 콘솔에 Ctrl+Break, 그다음 남은 것(run.cmd 의 cmd.exe, 작업의 프로그램)을 끝냄. 작업 중이면 force 가 아닐 때 거절. 돌지 않았으면 ""</summary>
    private string StopRunnerCore(string folder, bool force)
    {
        string id = Path.GetFileName(folder);
        var table = new ProcessTable();
        var mine = table.Under(folder);
        if (mine.Count == 0) return "";
        if (!force && mine.Any(process => process.Name.Equals("runner.worker.exe", StringComparison.OrdinalIgnoreCase)))
            throw new RelayError(409, $"{id}이(가) 작업 중입니다. 멈추면 그 빌드가 취소됩니다.");
        var shells = new HashSet<int>();
        foreach (var (pid, _) in mine)
        {
            int parent = table.Parent(pid);
            while (parent != 0 && table.Name(parent).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) && shells.Add(parent)) parent = table.Parent(parent);
        }
        int listener = mine.FirstOrDefault(process => process.Name.Equals("runner.listener.exe", StringComparison.OrdinalIgnoreCase)).Pid;
        Native.SendCtrlBreak(listener != 0 ? listener : mine[0].Pid);
        long deadline = Environment.TickCount64 + 20_000;
        List<(int Pid, string Name)> left;
        while (true)
        {
            table = new ProcessTable();
            left = table.Under(folder);
            if (!left.Any(process => process.Name.Equals("runner.listener.exe", StringComparison.OrdinalIgnoreCase)) || Environment.TickCount64 > deadline) break;
            Thread.Sleep(500);
        }
        bool killed = left.Any(process => process.Name.Equals("runner.listener.exe", StringComparison.OrdinalIgnoreCase));
        var targets = left.Select(process => process.Pid).Concat(shells.Where(pid => table.Name(pid).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))).ToHashSet();
        foreach (int pid in targets.Concat(table.Descendants(targets))) Native.Kill(pid);
        Log.Info($"runner {id} stopped{(killed ? " (killed)" : "")}");
        return killed ? $"{id}을(를) 강제로 멈췄습니다. GitHub에 세션이 2분쯤 남아 그동안은 다시 시작되지 않습니다." : $"{id}을(를) 멈췄습니다.";
    }

    public JsonObject Start(string runnerId, bool force = false)
    {
        string folder = Folder(runnerId);
        return Changing(() =>
        {
            SetOn(Path.GetFileName(folder), true);
            return new JsonObject { ["message"] = StartOne(folder) };
        });
    }

    public JsonObject Stop(string runnerId, bool force = false)
    {
        string folder = Folder(runnerId);
        return Changing(() =>
        {
            string message = StopRunner(folder, force);
            SetOn(Path.GetFileName(folder), false);
            return new JsonObject { ["message"] = message.Length > 0 ? message : $"{Path.GetFileName(folder)}을(를) 껐습니다." };
        });
    }

    public JsonObject Restart(string runnerId, bool force = false)
    {
        string folder = Folder(runnerId);
        return Changing(() =>
        {
            StopRunner(folder, force);
            SetOn(Path.GetFileName(folder), true);
            return new JsonObject { ["message"] = StartOne(folder).Replace("시작했습니다", "다시 시작했습니다") };
        });
    }

    /// <summary>러너의 .env (작업에 넘기는 변수)를 쓰고, 쉬며 도는 중이면 다시 시작해 작업이 보게 함</summary>
    public JsonObject Save(string runnerId, JsonNode? env)
    {
        string folder = Folder(runnerId);
        string text = EnvText(env);
        return Changing(() =>
        {
            Util.AtomicWrite(Path.Combine(folder, ".env"), Util.Utf8.GetBytes(text));
            var mine = new ProcessTable().Under(folder);
            if (mine.Count == 0) return new JsonObject { ["message"] = "저장했습니다." };
            if (mine.Any(process => process.Name.Equals("runner.worker.exe", StringComparison.OrdinalIgnoreCase)))
                return new JsonObject { ["message"] = "저장했습니다. 작업이 끝난 뒤 다시 시작하면 적용됩니다." };
            StopRunner(folder, false);
            StartOne(folder);
            return new JsonObject { ["message"] = "저장하고 다시 시작했습니다." };
        });
    }

    /// <summary>새 러너를 만들 파일이 있는 러너 (하드 링크: 공간이 더 들지 않음), 가장 새 버전</summary>
    private string? TemplateRunner() => Folders()
        .Where(folder => File.Exists(Path.Combine(folder, "bin", "Runner.Listener.exe")) && Directory.Exists(Path.Combine(folder, "externals")) && File.Exists(Path.Combine(folder, "config.cmd")))
        .MaxBy(folder => Version.TryParse(RunnerVersion(folder) is { Length: > 0 } version ? version : "0.0.0", out var parsed) ? parsed : new Version(0, 0, 0));

    private void PrepareFolder(string target)
    {
        if (_config.RunnerPackage is { } package)
        {
            if (!File.Exists(package)) throw new RelayError(409, $"러너 파일이 없습니다: {package} (설정의 RUNNER_PACKAGE)");
            using var archive = ZipFile.OpenRead(package);
            foreach (var member in archive.Entries)
            {
                string relative = member.FullName.Replace('\\', '/');
                if (relative.Length == 0 || relative.StartsWith('/') || Path.IsPathRooted(relative) || relative.Split('/').Contains("..")) continue;
                string destination = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                if (relative.EndsWith('/'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                member.ExtractToFile(destination, overwrite: true);
            }
            return;
        }
        string template = TemplateRunner() ?? throw new RelayError(409, "새 러너를 만들 러너 파일이 없습니다. 설정의 RUNNER_PACKAGE에 actions-runner-win-x64-*.zip 경로를 쓰세요.");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(template))
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith('.') && !Skip.Contains(name) && !name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(target, name));
        }
        foreach (string name in new[] { "bin", "externals" })
        {
            var source = new DirectoryInfo(Path.Combine(template, name));
            string resolved = source.ResolveLinkTarget(true)?.FullName ?? source.FullName;
            LinkTree(resolved, Path.Combine(target, name));
        }
    }

    /// <summary>폴더를 하드 링크로 복사 (공간이 더 들지 않음, 링크가 안 되면 복사). 파일 수</summary>
    private static int LinkTree(string source, string target)
    {
        int count = 0;
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string destination = Path.Combine(target, Path.GetFileName(file));
            if (!Native.HardLink(destination, file)) File.Copy(file, destination, overwrite: true);
            count++;
        }
        foreach (string folder in Directory.EnumerateDirectories(source)) count += LinkTree(folder, Path.Combine(target, Path.GetFileName(folder)));
        return count;
    }

    private void ConfigureRunner(string target, string url, string token, string name)
    {
        var (code, output) = Native.Run("cmd.exe", ["/d", "/c", Path.Combine(target, "config.cmd"), "--unattended", "--url", url, "--token", token, "--name", name, "--work", "_work"],
            300, Native.UserEnvironment(), target);
        if (code is null && output.Length > 0 && !File.Exists(Path.Combine(target, ".runner"))) throw new RelayError(502, $"러너를 등록하지 못했습니다: {output}");
        if (code != 0 || !File.Exists(Path.Combine(target, ".runner")))
        {
            var lines = output.Split('\n').Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.Contains(token, StringComparison.Ordinal) && !line.All(c => "-|_ ".Contains(c))).ToList();
            throw new RelayError(502, "러너를 등록하지 못했습니다: " + (lines.Count > 0 ? string.Join(" / ", lines.TakeLast(3)) : $"종료 코드 {code}"));
        }
    }

    /// <summary>저장소의 새 러너: 파일, GitHub 등록(서버가 받은 토큰), .env, 그리고 켬</summary>
    public JsonObject Add(JsonNode? repository, JsonNode? folderName, JsonNode? name, JsonNode? env)
    {
        Require();
        string full = Canonical(repository);
        string repo = full.Split('/', 2)[1];
        string folderId = folderName is null ? repo : Util.AsString(folderName) is { } given ? (given.Trim().Length > 0 ? given.Trim() : repo) : "";
        string runnerName = name is null ? "" : (Util.AsString(name) ?? "").Trim();
        if (!RunnerId().IsMatch(folderId) || folderId.Equals("env", StringComparison.OrdinalIgnoreCase)) throw new RelayError(400, "폴더 이름은 영문·숫자·.-_ 로 64자까지 씁니다.");
        if (!RunnerId().IsMatch(runnerName)) throw new RelayError(400, "러너 이름은 영문·숫자·.-_ 로 64자까지 씁니다.");
        string text = EnvText(env ?? JsonValue.Create(""));
        string target = Path.Combine(Root!, folderId);
        if (Directory.Exists(target) || File.Exists(target)) throw new RelayError(409, $"{target} 폴더가 이미 있습니다.");
        if (GitHubRunners(full, 0).Any(item => Util.Or(item["name"], "").Equals(runnerName, StringComparison.OrdinalIgnoreCase)))
            throw new RelayError(409, $"{full}에 이미 {runnerName} 러너가 있습니다. 다른 이름을 쓰세요.");
        return Changing(() =>
        {
            try
            {
                Prepare(target);
                var (status, answer) = GitHubCall("POST", $"{Releases.Base(full)}/actions/runners/registration-token");
                string? token = answer is JsonObject found ? Util.AsString(found["token"]) : null;
                if (status != 201 || string.IsNullOrEmpty(token)) throw new RelayError(502, "GitHub이 러너 등록 토큰을 주지 않았습니다. " + Releases.Problem(status, answer));
                Configure(target, WebUrl(full), token, runnerName);
                Util.AtomicWrite(Path.Combine(target, ".env"), Util.Utf8.GetBytes(text));
            }
            catch
            {
                if (Directory.Exists(target))
                {
                    try { Util.DeleteTree(target); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
            Log.Info($"runner {folderId} registered for {full} as {runnerName}");
            SetOn(folderId, true);
            string message;
            try
            {
                StartOne(target);
                message = $"{runnerName}을(를) 등록하고 시작했습니다.";
            }
            catch (RelayError error) { message = $"{runnerName}을(를) 등록했지만 시작하지 못했습니다: {error.Message}"; }
            return new JsonObject { ["id"] = folderId, ["message"] = message };
        });
    }

    /// <summary>러너를 멈추고, GitHub 의 저장소에서 지우고, 폴더를 지움</summary>
    public JsonObject Remove(string runnerId, bool force = false)
    {
        string folder = Folder(runnerId);
        var info = Registration(folder);
        string id = Path.GetFileName(folder);
        Changing(() =>
        {
            StopRunner(folder, force);
            string repository = RepositoryOf(Util.Or(info["gitHubUrl"], "")), name = Util.Or(info["agentName"], "");
            if (repository.Length > 0 && name.Length > 0)
            {
                string full = Canonical(repository);
                var match = GitHubRunners(full, 0).FirstOrDefault(item => Util.Or(item["name"], "").Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match is not null && match["id"] is not null)
                {
                    var (status, answer) = GitHubCall("DELETE", $"{Releases.Base(full)}/actions/runners/{Util.AsInt(match["id"])}");
                    if (status is not (204 or 404)) throw new RelayError(502, "GitHub에서 러너를 지우지 못했습니다. " + Releases.Problem(status, answer));
                }
            }
            Util.DeleteTree(folder);
            SetOn(id, true);
            return 0;
        });
        Log.Info($"runner {id} removed");
        return new JsonObject { ["message"] = $"{id}을(를) 지웠습니다." };
    }

    /// <summary>켜 두었고 등록됐고 돌지 않는 러너를 모두 시작 (로그온 작업): 한꺼번에 띄우고 기다림</summary>
    public List<string> StartOn() => Changing(() =>
    {
        var table = new ProcessTable();
        var off = Off();
        var started = Folders().Where(folder => File.Exists(Path.Combine(folder, ".runner")) && !off.Contains(Path.GetFileName(folder).ToLowerInvariant())
                                                 && table.Under(folder).Count == 0).ToList();
        var lines = new List<string>();
        foreach (string folder in started.ToList())
        {
            try { Launch(folder); }
            catch (RelayError error)
            {
                lines.Add(error.Message);
                started.Remove(folder);
            }
        }
        var problems = started.Count > 0 ? Await(started, 25) : [];
        lines.AddRange(started.Select(folder => problems.TryGetValue(folder, out var problem) ? $"{Path.GetFileName(folder)}: {problem}" : $"{Path.GetFileName(folder)} 시작"));
        return lines;
    });

    // 로그온 작업 ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>로그온 작업이 돌릴 것: 이 서버 프로그램 --start-runners (이번 실행의 --set 과 함께)</summary>
    public (string Command, string Arguments) TaskCommand() =>
        (Program.ExecutablePath, "--start-runners" + string.Concat(_config.SetArgs.Select(item => $" --set \"{item}\"")));

    public JsonObject TaskInfo(bool fresh = false)
    {
        var (names, until) = _tasks;
        if (fresh || names is null || Environment.TickCount64 > until)
        {
            names = ScheduledTasks();
            _tasks = (names, Environment.TickCount64 + 60_000);
        }
        bool exists = names.Any(name => name.Equals("\\" + TaskName, StringComparison.OrdinalIgnoreCase));
        bool current = false;
        if (exists)
        {
            string xml = TaskDefinition(TaskName);
            var (command, arguments) = TaskCommand();
            current = XmlValue(xml, "Command").Equals(command, StringComparison.OrdinalIgnoreCase) && XmlValue(xml, "Arguments").Equals(arguments, StringComparison.OrdinalIgnoreCase);
        }
        var legacy = names.Where(name => name.Count(c => c == '\\') == 1 && LegacyTask().IsMatch(name[1..])).Select(name => name[1..]).ToList();
        return new JsonObject
        {
            ["name"] = TaskName, ["exists"] = exists, ["current"] = current, ["legacy"] = new JsonArray(legacy.Select(name => (JsonNode)name).ToArray()),
        };
    }

    /// <summary>
    /// 로그온 작업 하나를 둠 (이 서버를 가리킴), 예전 러너별 작업은 지움 (XML 은 RUNNERS_ROOT\.backup\tasks). 작업을 지워도 그것이 시작한 것은 돎.
    /// 켜 두었지만 돌지 않는 러너는 시작
    /// </summary>
    public JsonObject InstallTask()
    {
        Require();
        var info = TaskInfo(fresh: true);
        var legacy = (info["legacy"] as JsonArray ?? []).Select(Util.Text).ToList();
        Changing(() =>
        {
            string backups = Path.Combine(Root!, ".backup", "tasks");
            foreach (string name in legacy)
            {
                string xml = TaskDefinition(name);
                if (xml.Length > 0)
                {
                    Directory.CreateDirectory(backups);
                    File.WriteAllText(Path.Combine(backups, NotFileCharacter().Replace(name, "_") + ".xml"), xml, Encoding.Unicode);
                }
                if (!DeleteTask(name)) throw new RelayError(500, $"예전 작업 {name}을(를) 지우지 못했습니다.");
                Log.Info($"legacy runner task {name} removed (backup in {backups})");
            }
            var (command, arguments) = TaskCommand();
            string user = Environment.GetEnvironmentVariable("USERNAME") ?? Environment.GetEnvironmentVariable("USER") ?? "";
            if (Environment.GetEnvironmentVariable("USERDOMAIN") is { Length: > 0 } domain) user = domain + "\\" + user;
            string problem = RegisterTask(TaskName, TaskXml(command, arguments, Path.GetDirectoryName(command) ?? "", user));
            if (problem.Length > 0) throw new RelayError(500, "로그온 작업을 만들지 못했습니다: " + problem);
            Log.Info($"logon task {TaskName} set: {command} {arguments}");
            return 0;
        });
        var lines = StartOn();
        _tasks = (null, 0);
        string removed = legacy.Count > 0 ? $" · 예전 작업 {legacy.Count}개 삭제" : "";
        string startedText = lines.Count > 0 ? $" · {lines.Count}개 시작" : "";
        return new JsonObject
        {
            ["message"] = $"로그온 작업 {TaskName} 하나로 러너를 시작합니다{removed}{startedText}.", ["details"] = new JsonArray(lines.Select(line => (JsonNode)line).ToArray()),
        };
    }

    /// <summary>로그온 작업이 아직 예전 서버(relay.py)로 러너를 시작하면 이 서버로 바꿈 (러너 화면의 [정리]와 같음)</summary>
    public void RepointTask()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || Root is null) return;
            var info = TaskInfo(fresh: true);
            if (!Util.IsTrue(info["exists"]) || Util.IsTrue(info["current"])) return;
            string xml = TaskDefinition(TaskName);
            string target = (XmlValue(xml, "Command") + " " + XmlValue(xml, "Arguments")).ToLowerInvariant();
            if (!target.Contains("relay.py", StringComparison.Ordinal) && !target.Contains("i-api.exe", StringComparison.Ordinal)) return;
            InstallTask();
            Log.Info($"logon task {TaskName} now starts the runners through {Program.ExecutablePath}");
        }
        catch (Exception error) { Log.Exception($"could not point the logon task {TaskName} at this server", error); }
    }

    public static string XmlValue(string xml, string tag)
    {
        var match = Regex.Match(xml, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : "";
    }

    /// <summary>로그온 작업 하나: 이 계정이 로그온하고 조금 뒤 켜 둔 러너를 시작하고 끝남</summary>
    public static string TaskXml(string command, string arguments, string folder, string user)
    {
        static string Escape(string value) => Util.HtmlEscape(value).Replace("&#x27;", "&#x27;");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{Escape("켜 둔 GitHub Actions 러너를 시작합니다 (i-luncher 관리자 메뉴의 러너 화면에서 관리)")}</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(user)}</UserId>
                  <Delay>PT30S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(command)}</Command>
                  <Arguments>{Escape(arguments)}</Arguments>
                  <WorkingDirectory>{Escape(folder)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>

            """;
    }

    /// <summary>이 PC 의 작업 스케줄러 작업 (\GitHub Runners 처럼 전체 이름). Windows 가 아니거나 schtasks 가 실패하면 빈 목록</summary>
    private static List<string> ListScheduledTasks()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var (code, output) = Native.Run("schtasks", ["/Query", "/FO", "CSV", "/NH"]);
        if (code is null) return [];
        var names = new List<string>();
        foreach (string line in output.Split('\n'))
        {
            string row = line.Trim();
            if (!row.StartsWith("\"\\", StringComparison.Ordinal)) continue;
            int end = row.IndexOf('"', 1);
            string name = end > 1 ? row[1..end] : "";
            if (name.StartsWith('\\') && !names.Contains(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>작업의 XML (없으면 "")</summary>
    private static string ReadTaskDefinition(string name)
    {
        if (!OperatingSystem.IsWindows()) return "";
        var (code, output) = Native.Run("schtasks", ["/Query", "/TN", name, "/XML"]);
        return code == 0 ? output : "";
    }

    /// <summary>이 계정의 작업을 만들거나 바꿈 (관리자 권한 필요 없음). "" 또는 실패한 이유</summary>
    private static string CreateTask(string name, string xml)
    {
        string folder = Path.Combine(Path.GetTempPath(), "i-api-task-" + Util.HexToken(4));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "task.xml");
            File.WriteAllText(path, xml, Encoding.Unicode);
            var (code, output) = Native.Run("schtasks", ["/Create", "/TN", name, "/XML", path, "/F"]);
            if (code == 0) return "";
            string text = output.Trim();
            return text.Length > 0 ? (text.Length > 300 ? text[..300] : text) : $"종료 코드 {code}";
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
