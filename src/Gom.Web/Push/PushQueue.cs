using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Gom.Web.Push;

/// <summary>A message that may need notifications: what is needed to word them, and who wrote it (who is not told).</summary>
public sealed record PushJob(Guid ConversationId, long Seq, Guid SenderId, string SenderName, int Images, int Files);

/// <summary>
/// Messages waiting for their notifications, handed over after the commit. Bounded: when it is full a notification is dropped, never
/// the send (the message is in the database, and open devices have it already).
/// </summary>
public sealed class PushQueue(IOptions<PushOptions> options, ILogger<PushQueue> log)
{
    private readonly Channel<PushJob> _jobs = Channel.CreateBounded<PushJob>(new BoundedChannelOptions(Math.Max(1, options.Value.QueueSize))
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,   // with Wait, TryWrite says no when full, so a drop can be logged
    });

    public bool Enabled { get; } = options.Value.Enabled;

    public ChannelReader<PushJob> Reader => _jobs.Reader;

    public void Enqueue(PushJob job)
    {
        if (!Enabled) return;
        if (!_jobs.Writer.TryWrite(job)) log.LogWarning("push queue full; a notification was dropped");
    }
}
