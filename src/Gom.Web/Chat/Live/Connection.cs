using System.Threading.Channels;

namespace Gom.Web.Chat.Live;

/// <summary>Where frames finally go: a real WebSocket, or a fake in tests.</summary>
public interface IFrameSink
{
    Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);
    Task CloseAsync(int code, string reason);
}

/// <summary>
/// One open socket: a bounded queue and one writer loop. Whoever has something to say only ever queues it (never waits).
/// A client that cannot keep up is closed, and finds what it missed again from its cursor.
/// </summary>
public sealed class Connection(Guid userId, Guid sessionId, IFrameSink sink, int capacity, TimeSpan sendTimeout, ILogger logger)
{
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private int _closing;
    private int _closeCode = 1000;
    private string _closeReason = "";
    private volatile bool _closedByServer;

    /// <summary>True when the server decided to end it (too slow, bad frames, replaced), as opposed to the client closing or the line dropping.</summary>
    public bool ClosedByServer => _closedByServer;
    public int CloseCode => _closeCode;
    /// <summary>Why the server ended it: always one of our own fixed texts, never anything the client wrote.</summary>
    public string CloseReason => _closeReason;

    public Guid UserId { get; } = userId;
    public Guid SessionId { get; } = sessionId;
    /// <summary>The page said, as it connected, that it can take a voice call (<c>/ws?calls=1</c>).</summary>
    public bool TakesCalls { get; init; }

    /// <summary>Queues a frame. False (and the connection is closed) when the client is too far behind or already closing.</summary>
    public bool TryEnqueue(byte[] frame)
    {
        if (Volatile.Read(ref _closing) == 1) return false;
        if (_queue.Writer.TryWrite(frame)) return true;
        Close(1013, "too slow, reconnect and catch up");
        return false;
    }

    /// <summary>Ends it now: what is still queued is dropped. The first reason given is the one the client hears.</summary>
    public void Close(int code, string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1) return;
        _closeCode = code;
        _closeReason = reason;
        _closedByServer = true;
        _queue.Writer.TryComplete();
        _stop.Cancel();
    }

    /// <summary>Ends it politely: everything queued goes out first, then a normal close.</summary>
    public void Finish()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1) return;
        _queue.Writer.TryComplete();
    }

    /// <summary>The one loop that writes to the socket. Returns when the connection is over and the close has been sent.</summary>
    public async Task RunWriter()
    {
        try
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                limit.CancelAfter(sendTimeout);
                try { await sink.SendAsync(frame, limit.Token); }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { Close(1013, "send timed out"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            logger.LogDebug(e, "socket write failed");
            Close(1013, "write failed");
        }
        finally
        {
            try { await sink.CloseAsync(_closeCode, _closeReason).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) { logger.LogDebug(e, "socket close failed"); }
        }
    }
}
