using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerminalNotes.Services;

/// <summary>Owns one hidden, loopback-only OpenCode HTTP server process.</summary>
public sealed class OpenCodeHeadlessHost : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private Process? _process;
    private bool _starting;
    private bool _disposed;

    public OpenCodeHeadlessHost(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("The OpenCode endpoint must be an absolute loopback HTTP URI without credentials, query, or fragment.", nameof(endpoint));
        Endpoint = new Uri(endpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/", UriKind.Absolute);
    }

    public Uri Endpoint { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public uint? ProcessId => _process is { HasExited: false } process ? unchecked((uint)process.Id) : null;
    public int? ExitCode => _process is { HasExited: true } process ? process.ExitCode : null;
    public event Action<int>? Exited;

    public Task StartAsync(string directory, string? configContent, CancellationToken cancellationToken = default) =>
        StartAsync(directory, configContent, autoMode: false, cancellationToken: cancellationToken);

    public async Task StartAsync(string directory, string? configContent, bool autoMode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The headless OpenCode host currently supports Windows only.");
        string workingDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("The selected project directory does not exist.");
        Process? previous;
        lock (_gate)
        {
            if (_starting || _process is { HasExited: false }) throw new InvalidOperationException("The OpenCode headless server is already starting or running.");
            previous = _process;
            _process = null;
            _starting = true;
        }
        previous?.Dispose();

        int port = Endpoint.Port;
        Process? process = null;
        try
        {
            string? effectiveConfig = configContent;
            if (autoMode)
                effectiveConfig = MergeAutoPermission(configContent ?? Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT"));

            AutoInstallDetectionResult command = await new OpenCodeInstallationService().CheckAsync(cancellationToken).ConfigureAwait(false);
            if (!command.IsInstalled || string.IsNullOrWhiteSpace(command.Path))
                throw new InvalidOperationException("A working OpenCode command was not found. " + command.Message);
            ProcessStartInfo startInfo = CreateStartInfo(command.Path, workingDirectory, port, effectiveConfig);
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += OnProcessExited;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            if (!process.Start()) throw new InvalidOperationException("OpenCode did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _process = process;
                _starting = false;
            }
            process = null;

            Uri endpoint = new($"http://127.0.0.1:{port}/");
            using var client = CreateHealthClient();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(StartupTimeout);
            while (!startup.IsCancellationRequested)
            {
                Process? active;
                lock (_gate) active = _process;
                if (active is null || active.HasExited)
                    throw new InvalidOperationException("The OpenCode server exited before becoming ready.");
                try
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                    requestTimeout.CancelAfter(HealthRequestTimeout);
                    using HttpResponseMessage response = await client.GetAsync(new Uri(endpoint, "global/health"), requestTimeout.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        if (response.RequestMessage?.RequestUri is not Uri finalUri || finalUri != new Uri(endpoint, "global/health"))
                            throw new InvalidOperationException("OpenCode redirected the loopback health request.");
                        await using Stream body = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
                        byte[] healthBytes = await ReadBoundedAsync(body, 64 * 1024, requestTimeout.Token).ConfigureAwait(false);
                        using JsonDocument health = JsonDocument.Parse(healthBytes, new JsonDocumentOptions { MaxDepth = 16 });
                        if (health.RootElement.TryGetProperty("healthy", out JsonElement healthy) && healthy.ValueKind == JsonValueKind.True)
                        {
                            Endpoint = endpoint;
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !startup.IsCancellationRequested) { }
                catch (OperationCanceledException) when (startup.IsCancellationRequested) { break; }
                catch (HttpRequestException) { }
                catch (JsonException) { }
                try { await Task.Delay(150, startup.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (startup.IsCancellationRequested) { break; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("OpenCode did not become ready within the startup timeout.");
        }
        catch
        {
            if (process is not null) await StopOwnedProcessAsync(process).ConfigureAwait(false);
            await StopAsync().ConfigureAwait(false);
            lock (_gate) _starting = false;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        if (process is not null)
        {
            try { StopOwnedProcessAsync(process).GetAwaiter().GetResult(); }
            catch { }
            process.Dispose();
        }
    }

    private async Task StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        if (process is not null)
        {
            await StopOwnedProcessAsync(process).ConfigureAwait(false);
            process.Dispose();
        }
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (sender is not Process process) return;
        int code;
        try { code = process.ExitCode; }
        catch (InvalidOperationException) { return; }
        Exited?.Invoke(code);
    }

    private static ProcessStartInfo CreateStartInfo(string commandPath, string directory, int port, string? configContent)
    {
        string extension = Path.GetExtension(commandPath);
        var info = new ProcessStartInfo
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (configContent is not null) info.Environment["OPENCODE_CONFIG_CONTENT"] = configContent;
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) info.FileName = commandPath;
        else if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (commandPath.IndexOfAny(['%', '!', '\r', '\n', '"', '&', '|', '<', '>', '^', '(', ')']) >= 0)
                throw new InvalidOperationException("The OpenCode command path contains characters unsafe for cmd.exe.");
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = $"/d /s /c \"\"{commandPath}\" serve --hostname 127.0.0.1 --port {port}\"";
        }
        else if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            info.ArgumentList.Add("-NoLogo"); info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-ExecutionPolicy"); info.ArgumentList.Add("Bypass"); info.ArgumentList.Add("-File"); info.ArgumentList.Add(commandPath);
        }
        else throw new InvalidOperationException("The resolved OpenCode command has an unsupported file type.");
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add("serve"); info.ArgumentList.Add("--hostname"); info.ArgumentList.Add("127.0.0.1");
            info.ArgumentList.Add("--port"); info.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return info;
    }

    private static HttpClient CreateHealthClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        if (Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD") is { Length: > 0 } password)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes((Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME") ?? "opencode") + ":" + password)));
        return client;
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 4096));
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes) throw new InvalidDataException("OpenCode health response exceeded the bounded size.");
            output.Write(buffer, 0, read);
        }
    }

    private static string MergeAutoPermission(string? configContent)
    {
        JsonObject config;
        if (string.IsNullOrWhiteSpace(configContent)) config = new JsonObject();
        else
        {
            JsonNode? parsed = JsonNode.Parse(configContent);
            config = parsed as JsonObject ?? throw new InvalidDataException("OpenCode inline config must be a JSON object.");
        }
        config["permission"] = JsonValue.Create("allow");
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
