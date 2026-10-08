using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerminalNotes.Services;

/// <summary>One dashboard per settings file. The OS releases ownership on exit/crash.</summary>
public sealed class AppInstanceCoordinator : IDisposable
{
    private const int MaximumRequestBytes = 32 * 1024;
    private readonly FileStream _ownership;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    public string PipeName { get; }

    private AppInstanceCoordinator(string settingsPath, FileStream ownership)
    {
        _ownership = ownership;
        PipeName = GetPipeName(settingsPath);
    }

    public static string GetPipeName(string settingsPath) => "TerminalNotes-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(settingsPath).ToUpperInvariant())))[..32];

    public static bool IsSharingViolation(IOException exception) => (exception.HResult & 0xffff) is 32 or 33;

    public static AppInstanceCoordinator? TryAcquire(string settingsPath)
    {
        settingsPath = Path.GetFullPath(settingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        FileStream ownership;
        try { ownership = new(settingsPath + ".instance.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
        catch (IOException exception) when (IsSharingViolation(exception)) { return null; }
        try
        {
            ownership.SetLength(0);
            JsonSerializer.Serialize(ownership, new { processId = Environment.ProcessId });
            ownership.Flush();
            return new(settingsPath, ownership);
        }
        catch { ownership.Dispose(); throw; }
    }

    public static int? ReadOwnerProcessId(string settingsPath)
    {
        try
        {
            using var file = new FileStream(settingsPath + ".instance.lock", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(file);
            return document.RootElement.GetProperty("processId").GetInt32();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException) { return null; }
    }

    public void StartListening(Func<string?, Task<bool>> activate)
    {
        if (_listener is not null) throw new InvalidOperationException("Activation listener already started.");
        _listener = ListenAsync(activate);
    }

    private async Task ListenAsync(Func<string?, Task<bool>> activate)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                byte[] header = new byte[4];
                await pipe.ReadExactlyAsync(header, requestTimeout.Token).ConfigureAwait(false);
                int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 1 or > MaximumRequestBytes) continue;
                byte[] payload = new byte[length];
                await pipe.ReadExactlyAsync(payload, requestTimeout.Token).ConfigureAwait(false);
                string? workspace = JsonSerializer.Deserialize<string>(payload);
                if (workspace is not null && !Path.IsPathFullyQualified(workspace)) continue;
                bool accepted = await activate(workspace).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, requestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Partial/failed launch messages must not disable later activation.
                System.Diagnostics.Trace.WriteLine(exception);
                if (!_shutdown.IsCancellationRequested) await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    public static async Task<bool> TryActivateAsync(string settingsPath, string? workspace, CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", GetPipeName(settingsPath), PipeDirection.InOut,
                PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(workspace);
            if (payload.Length > MaximumRequestBytes) return false;
            byte[] header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            byte[] acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, cancellationToken).ConfigureAwait(false);
            return acknowledgement[0] == 1;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _ownership.Dispose();
        // Never delete a lock path: deletion can split ownership between processes.
    }
}
