using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fake-launcher")
            return FakeLauncher(args[1], args[2]);
        return RunAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes hidden CMD startup " + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(fixture, "project");
        Directory.CreateDirectory(project);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "appdata"));
        string plan = Path.Combine(project, "plan.md");
        await File.WriteAllTextAsync(plan, "# preserved\n");
        string fakeCommand = Path.Combine(fixture, "opencode.exe");
        await File.WriteAllTextAsync(fakeCommand, "test fixture only");
        try
        {
            await VerifyPreCancelledLaunch(project, plan, fakeCommand);
            await VerifyDelayedHealthyStartup(project, plan, fakeCommand);
            await VerifyDelayedFailure(project, plan, fakeCommand);
            await VerifyRequestTimeoutRetries(project, plan, fakeCommand);
            await VerifyUnauthorizedFailsImmediately(project, plan, fakeCommand);
            await VerifyIdentityMismatch(project, plan, fakeCommand, "wrong-session");
            await VerifyIdentityMismatch(project, plan, fakeCommand, "wrong-project");
            await VerifyTimeoutCleansOwnedTree(project, plan, fakeCommand);
            await VerifyCancellationCleansOwnedTree(project, plan, fakeCommand);
            Console.WriteLine("PASS: hidden startup handshake tests complete without visible windows or model calls.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task VerifyPreCancelledLaunch(string project, string plan, string command)
    {
        var launch = WithHiddenProcess(Prepare(project, plan, command, "ses_cancelled_before_start"), "never-ready");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { await ExternalTerminalLauncher.LaunchAsync(launch, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && !File.Exists(Path.Combine(launch.Directory, "cmd.pid")),
            "an already cancelled request never creates its external process");
        Console.WriteLine("PASS pre-cancelled launch");
    }
    private static async Task VerifyDelayedHealthyStartup(string project, string plan, string command)
    {
        ExternalTerminalLaunch launch = Prepare(project, plan, command, "ses_delayed_success");
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(launch.Directory, "launch.json"))))
        {
            Check(json.RootElement.GetProperty("Port").GetInt32() == launch.Port && json.RootElement.GetProperty("Session").GetString() == launch.SessionId,
                "prepared launch pins the ephemeral loopback port and exact session ID");
            string script = File.ReadAllText(Path.Combine(launch.Directory, "launch.ps1"));
            Check(script.Contains("'--hostname', '127.0.0.1', '--port'", StringComparison.Ordinal) &&
                  script.Contains("'--session', $cmdSession.Session", StringComparison.Ordinal),
                "OpenCode receives loopback server and exact session arguments");
        }
        await using var endpoint = new FakeEndpoint(launch, "correct");
        launch = WithHiddenProcess(launch, "delayed-success");
        var timer = Stopwatch.StartNew();
        Task pending = ExternalTerminalLauncher.LaunchAsync(launch, timeout: TimeSpan.FromSeconds(6));
        await Task.Delay(350);
        Check(!pending.IsCompleted, "a live but unverified process is not mistaken for a ready session");
        await pending;
        Check(timer.Elapsed >= TimeSpan.FromSeconds(1), "success waits for the mock API to expose the exact session");
        Check(ExternalTerminalLauncher.IsSessionActive(project, launch.SessionId), "success requires the active exclusive session lease");
        await File.WriteAllTextAsync(Path.Combine(launch.Directory, "finish"), "finish");
        await Until(() => File.Exists(Path.Combine(launch.Directory, "completed")) && !ExternalTerminalLauncher.IsSessionActive(project, launch.SessionId),
            "fake successful launcher releases its lease");
        Console.WriteLine("PASS delayed healthy startup");
    }

    private static async Task VerifyDelayedFailure(string project, string plan, string command)
    {
        ExternalTerminalLaunch launch = Prepare(project, plan, command, "ses_delayed_failure");
        await using var endpoint = new FakeEndpoint(launch, "correct");
        launch = WithHiddenProcess(launch, "late-failure");
        var timer = Stopwatch.StartNew();
        bool failed = false;
        try { await ExternalTerminalLauncher.LaunchAsync(launch, timeout: TimeSpan.FromSeconds(6)); }
        catch (IOException ex) when (ex.Message.Contains("fixture delayed failure", StringComparison.Ordinal)) { failed = true; }
        Check(failed && timer.Elapsed >= TimeSpan.FromSeconds(1), "late launcher failure cannot pass a stale startup marker");
        Check(!ExternalTerminalLauncher.IsSessionActive(project, launch.SessionId), "failed startup releases its owned lease");
        Check(!IsProcessRunning(ReadStartedPid(launch)), "failed startup waits for its owned helper process to exit");
        Console.WriteLine("PASS delayed failure cleanup");
    }

    private static async Task VerifyRequestTimeoutRetries(string project, string plan, string command)
    {
        ExternalTerminalLaunch launch = Prepare(project, plan, command, "ses_slow_probe");
        await using var endpoint = new FakeEndpoint(launch, "slow-first-probe");
        launch = WithHiddenProcess(launch, "slow-probe");
        await ExternalTerminalLauncher.LaunchAsync(launch, timeout: TimeSpan.FromSeconds(8));
        Check(endpoint.RequestCount >= 2, "one slow API probe times out and retries inside the total startup budget");
        await File.WriteAllTextAsync(Path.Combine(launch.Directory, "finish"), "finish");
        await Until(() => File.Exists(Path.Combine(launch.Directory, "completed")) && !ExternalTerminalLauncher.IsSessionActive(project, launch.SessionId),
            "slow probe helper releases its lease");
        Console.WriteLine("PASS per-request timeout retry");
    }

    private static async Task VerifyUnauthorizedFailsImmediately(string project, string plan, string command)
    {
        ExternalTerminalLaunch launch = Prepare(project, plan, command, "ses_unauthorized");
        await using var endpoint = new FakeEndpoint(launch, "unauthorized");
        launch = WithHiddenProcess(launch, "never-ready");
        bool rejected = false;
        try { await ExternalTerminalLauncher.LaunchAsync(launch, timeout: TimeSpan.FromSeconds(5)); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { rejected = true; }
        Check(rejected, "HTTP 401 fails immediately rather than being treated as transient startup");
        Check(!ExternalTerminalLauncher.IsSessionActive(project, launch.SessionId) && !IsProcessRunning(ReadStartedPid(launch)),
            "unauthorized startup terminates and waits for only its own helper process");
        Console.WriteLine("PASS unauthorized endpoint rejection");
    }

    private static async Task VerifyIdentityMismatch(string project, string plan, string command, string scenario)
    {
        string session = "ses_" + scenario.Replace('-', '_');
        ExternalTerminalLaunch launch = Prepare(project, plan, command, session);
        await using var endpoint = new FakeEndpoint(launch, scenario);
        launch = WithHiddenProcess(launch, scenario);
        bool rejected = false;
        try { await ExternalTerminalLauncher.LaunchAsync(launch, timeout: TimeSpan.FromSeconds(5)); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, $"{scenario} API mismatch cannot be accepted as the requested session");
        Check(!ExternalTerminalLauncher.IsSessionActive(project, launch.SessionId), $"{scenario} failure releases its lease");
        Check(!IsProcessRunning(ReadStartedPid(launch)), $"{scenario} failure waits for its owned helper exit");
        Console.WriteLine("PASS " + scenario);
    }

    private static async Task VerifyTimeoutCleansOwnedTree(string project, string plan, string command)
    {
        ExternalTerminalLaunch launch = Prepare(project, plan, command, "ses_timeout");
        await using var endpoint = new FakeEndpoint(launch, "never-ready");
        launch = WithHiddenProcess(launch, "never-ready");
        bool timedOut = false;
        try { await ExternalTerminalLauncher.LaunchAsync(launch, timeout: TimeSpan.FromMilliseconds(700)); }
        catch (TimeoutException) { timedOut = true; }
        Check(timedOut, "startup timeout is explicit");
        Check(!ExternalTerminalLauncher.IsSessionActive(project, launch.SessionId), "timeout releases the lease by ending the owned tree");
        Check(!IsProcessRunning(ReadStartedPid(launch)), "timeout waits for its owned launcher process to exit");
        Console.WriteLine("PASS timeout cleanup");
    }

    private static async Task VerifyCancellationCleansOwnedTree(string project, string plan, string command)
    {
        ExternalTerminalLaunch launch = Prepare(project, plan, command, "ses_cancel");
        await using var endpoint = new FakeEndpoint(launch, "never-ready");
        launch = WithHiddenProcess(launch, "never-ready");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        bool cancelled = false;
        try { await ExternalTerminalLauncher.LaunchAsync(launch, cancellation.Token, TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "caller cancellation is propagated");
        Check(!ExternalTerminalLauncher.IsSessionActive(project, launch.SessionId), "cancellation releases the exclusive lease");
        Check(!IsProcessRunning(ReadStartedPid(launch)), "cancellation waits for its owned process to exit");
        Console.WriteLine("PASS cancellation cleanup");
    }

    private static ExternalTerminalLaunch Prepare(string project, string plan, string command, string session) =>
        ExternalTerminalLauncher.Prepare(command, project, session, plan, "hidden startup test", false);

    private static ExternalTerminalLaunch WithHiddenProcess(ExternalTerminalLaunch launch, string scenario)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = launch.Project
        };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--fake-launcher");
        info.ArgumentList.Add(launch.Directory);
        info.ArgumentList.Add(scenario);
        return launch with { StartInfo = info };
    }

    private static int FakeLauncher(string directory, string scenario)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "launch.json")));
        string leasePath = json.RootElement.GetProperty("Lease").GetString()!;
        var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(Path.Combine(directory, "started"), "started");
        try
        {
            if (scenario is "delayed-success" or "wrong-session" or "wrong-project")
            {
                Thread.Sleep(1500);
                File.WriteAllText(Path.Combine(directory, "api-ready"), "ready");
                while (!File.Exists(Path.Combine(directory, "finish"))) Thread.Sleep(40);
            }
            else if (scenario == "slow-probe")
            {
                File.WriteAllText(Path.Combine(directory, "api-ready"), "ready");
                while (!File.Exists(Path.Combine(directory, "finish"))) Thread.Sleep(40);
            }
            else if (scenario == "late-failure")
            {
                Thread.Sleep(1500);
                File.WriteAllText(Path.Combine(directory, "failed.txt"), "fixture delayed failure");
            }
            else
            {
                while (true) Thread.Sleep(1000);
            }
            return 0;
        }
        finally
        {
            lease.Dispose();
            File.WriteAllText(Path.Combine(directory, "completed"), "completed");
        }
    }

    private static int ReadStartedPid(ExternalTerminalLaunch launch) =>
        int.Parse(File.ReadAllText(Path.Combine(launch.Directory, "cmd.pid")), System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsProcessRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task Until(Func<bool> predicate, string description)
    {
        for (int i = 0; i < 200; i++)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(description);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private sealed class FakeEndpoint : IAsyncDisposable
    {
        private readonly ExternalTerminalLaunch _launch;
        private readonly string _scenario;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public FakeEndpoint(ExternalTerminalLaunch launch, string scenario)
        {
            _launch = launch;
            _scenario = scenario;
            _listener = new TcpListener(IPAddress.Loopback, launch.Port);
            _listener.Start();
            _server = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { return; }
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                        await reader.ReadLineAsync(_stop.Token);
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                        int requestNumber = Interlocked.Increment(ref _requestCount);
                        if (_scenario == "slow-first-probe" && requestNumber == 1)
                            await Task.Delay(TimeSpan.FromMilliseconds(2500), _stop.Token);
                        bool ready = File.Exists(Path.Combine(_launch.Directory, "api-ready"));
                        string body;
                        int status;
                        if (_scenario == "unauthorized")
                        {
                            body = "{}";
                            status = 401;
                        }
                        else if (!ready)
                        {
                            body = "{}";
                            status = 404;
                        }
                        else
                        {
                            string session = _scenario == "wrong-session" ? "ses_other" : _launch.SessionId;
                            string directory = _scenario == "wrong-project" ? Path.GetDirectoryName(_launch.Project)! : _launch.Project;
                            body = JsonSerializer.Serialize(new { id = session, directory });
                            status = 200;
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        string statusText = status == 200 ? "OK" : status == 401 ? "Unauthorized" : "Not Found";
                        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {statusText}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header, _stop.Token);
                        await stream.WriteAsync(bytes, _stop.Token);
                    }
                    catch (IOException) { }
                    catch (SocketException) { }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _server; }
            catch (SocketException) { }
        }
    }
}