using System.Threading.Channels;

namespace TerminalNotes.Services;

/// <summary>
/// Lossless FIFO storage for input chunks accepted by a ConPTY session.
/// </summary>
internal sealed class TerminalInputBuffer
{
    private readonly object _gate = new();
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private bool _completed;
    private Exception? _failure;

    public ChannelReader<byte[]> Reader => _channel.Reader;

    public Exception? Failure
    {
        get { lock (_gate) return _failure; }
    }

    public bool TryWrite(byte[] bytes)
    {
        lock (_gate)
            return !_completed && _channel.Writer.TryWrite(bytes);
    }

    public bool TryFail(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            if (_completed) return false;
            _failure = failure;
            _completed = true;
            _channel.Writer.TryComplete(failure);
            return true;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _channel.Writer.TryComplete();
        }
    }
}
