using Gom.Web.Chat.Live;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gom.Tests;

/// <summary>The per-socket queue and writer, without a network: a slow client is closed, never waited for.</summary>
public class ConnectionTests
{
    private sealed class FakeSink : IFrameSink
    {
        public List<string> Sent { get; } = [];
        public TaskCompletionSource Gate { get; } = new();
        public bool Blocked { get; set; }
        public int? ClosedWith { get; private set; }
        public TaskCompletionSource Closed { get; } = new();

        public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            if (Blocked) await Gate.Task.WaitAsync(ct);
            else await Task.Delay(30, ct);   // a real socket is never instant, so frames are still queued when a close comes
            lock (Sent) Sent.Add(System.Text.Encoding.UTF8.GetString(frame.Span));
        }

        public Task CloseAsync(int code, string reason) { ClosedWith = code; Closed.TrySetResult(); return Task.CompletedTask; }
    }

    private static byte[] Frame(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static Connection Make(FakeSink sink, int capacity = 3, int sendTimeoutMs = 5000) =>
        new(Guid.NewGuid(), Guid.NewGuid(), sink, capacity, TimeSpan.FromMilliseconds(sendTimeoutMs), NullLogger.Instance);

    [Fact]
    public async Task Frames_go_out_in_the_order_they_were_queued()
    {
        var sink = new FakeSink();
        var conn = Make(sink);
        var writer = conn.RunWriter();

        Assert.True(conn.TryEnqueue(Frame("1")));
        Assert.True(conn.TryEnqueue(Frame("2")));
        Assert.True(conn.TryEnqueue(Frame("3")));
        conn.Finish();   // a polite end: everything queued goes out first
        await writer.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["1", "2", "3"], sink.Sent);
        Assert.Equal(1000, sink.ClosedWith);
    }

    [Fact]
    public async Task A_client_that_stops_reading_is_closed_when_the_queue_is_full_and_nobody_waits_for_it()
    {
        var sink = new FakeSink { Blocked = true };
        var conn = Make(sink, capacity: 3);
        var writer = conn.RunWriter();
        var results = new List<bool>();
        for (var i = 0; i < 10; i++) results.Add(conn.TryEnqueue(Frame(i.ToString())));

        // One frame is stuck in the sink, three wait in the queue; the rest are refused at once.
        Assert.Contains(false, results);
        Assert.False(results[^1]);
        await sink.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1013, sink.ClosedWith);
        Assert.False(conn.TryEnqueue(Frame("late")));
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_send_that_takes_too_long_closes_the_connection()
    {
        var sink = new FakeSink { Blocked = true };
        var conn = Make(sink, sendTimeoutMs: 100);
        var writer = conn.RunWriter();

        Assert.True(conn.TryEnqueue(Frame("stuck")));

        await sink.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1013, sink.ClosedWith);
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(conn.TryEnqueue(Frame("after")));
    }

    [Fact]
    public async Task Closing_twice_closes_once_and_with_the_first_reason()
    {
        var sink = new FakeSink();
        var conn = Make(sink);
        var writer = conn.RunWriter();

        conn.Close(4401, "signed out");
        conn.Close(1013, "slow");
        await writer.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4401, sink.ClosedWith);
    }

    [Fact]
    public async Task A_frame_that_may_be_lost_is_dropped_when_the_queue_is_full_and_never_closes_the_socket()
    {
        var sink = new FakeSink { Blocked = true };
        var conn = Make(sink, capacity: 3);
        var writer = conn.RunWriter();
        var results = new List<bool>();
        for (var i = 0; i < 10; i++) results.Add(conn.TryEnqueueLossy(Frame(i.ToString())));

        Assert.Contains(false, results);                         // some were dropped
        await Task.Delay(200);
        Assert.False(sink.Closed.Task.IsCompleted);              // but the socket was not closed for it
        sink.Gate.SetResult();
        conn.Finish();
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_frame_that_may_be_lost_is_refused_once_the_connection_is_closing()
    {
        var conn = Make(new FakeSink());
        conn.Close(1008, "test");
        Assert.False(conn.TryEnqueueLossy(Frame("x")));
    }
}
