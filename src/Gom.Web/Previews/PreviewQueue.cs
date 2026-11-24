using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Gom.Web.Previews;

public sealed record PreviewJob(Guid MessageId, Guid ConversationId, Guid SenderId, Uri Url);

/// <summary>A bounded line of links waiting for a preview. When it is full, a new link simply gets none: sending a message never waits for this.</summary>
public sealed class PreviewQueue(IOptions<PreviewOptions> options)
{
    private readonly Channel<PreviewJob> _channel = Channel.CreateBounded<PreviewJob>(new BoundedChannelOptions(Math.Max(1, options.Value.QueueLength)) { FullMode = BoundedChannelFullMode.DropWrite });

    public bool Enabled => options.Value.Enabled;
    public bool TryEnqueue(PreviewJob job) => Enabled && _channel.Writer.TryWrite(job);
    public ChannelReader<PreviewJob> Reader => _channel.Reader;
}
