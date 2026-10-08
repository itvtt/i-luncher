namespace TerminalNotes.TerminalTransport;

public interface ITerminalProcess : IAsyncDisposable
{
    event Action<byte[]>? OutputReceived;
    event Action<TerminalProcessExit>? Exited;
    event Action<Exception>? InputFailed;
    bool IsRunning { get; }
    Task StartAsync(TerminalProcessOptions options, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    void Resize(int columns, int rows);
    Task CloseAsync(CancellationToken cancellationToken = default);
}
