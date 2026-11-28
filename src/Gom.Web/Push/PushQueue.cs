using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Gom.Web.Push;

/// <summary>A message that may need notifications: what is needed to word them, and who wrote it (who is not told).</summary>
/// <summary>
/// One message to tell people about: who wrote, what kind it is, its words (already cut to a snippet) for the devices that show
/// them, and its id. <paramref name="Recalled"/>: it was taken back, so a notification showing its words is replaced.
/// <paramref name="JoinRequest"/>: not a message but someone asking to join (the sender), told to the owner.
/// <paramref name="Contact"/>: not a message but a note from the privacy page's form, told to the owner (never its words).
/// <paramref name="MissedCall"/>: a call the person called missed (the sender is the caller), told to them even in a muted chat.
/// <paramref name="Mentioned"/>: the members it names; each is told even if they muted the conversation (not if they blocked the writer).
/// <paramref name="MentionsAll"/>: it says "@all": worded for everyone, but it never rings through a mute. <paramref name="OnlyMentioned"/>:
/// tell only those named (a message edited to add a mention: the others heard of it when it was sent).
/// </summary>
public sealed record PushJob(Guid ConversationId, long Seq, Guid SenderId, string SenderName, int Images, int Files, bool Sticker = false,
    string Text = "", Guid MessageId = default, bool Recalled = false, bool JoinRequest = false, int Videos = 0, bool Contact = false, bool MissedCall = false,
    IReadOnlyList<Guid>? Mentioned = null, bool MentionsAll = false, bool OnlyMentioned = false)
{
    /// <summary>How this message names the person told: by name, by "@all", or not at all.</summary>
    public Mention MentionOf(Guid person) =>
        Mentioned?.Contains(person) == true ? Mention.You : MentionsAll && !OnlyMentioned ? Mention.All : Mention.None;

    /// <summary>
    /// A message as its notification words it: sending one and the app asking for its words (<c>GET /api/push/notice</c>) both start
    /// here, so the two never word it differently.
    /// </summary>
    public static PushJob From(Data.Message message, string senderName, IReadOnlyList<Uploads.AttachmentEndpoints.AttachmentDto>? attachments)
    {
        var files = attachments ?? [];
        return new PushJob(message.ConversationId, message.Seq, message.SenderId, senderName, files.Count(a => a.Kind == "image"),
            files.Count(a => a.Kind == "file"), message.Sticker is not null, Chat.Replies.Snippet(message.Body), message.Id,
            Videos: files.Count(a => a.Kind == "video"), Mentioned: message.Mentions, MentionsAll: message.MentionsAll);
    }
}

/// <summary>Whether a message names the person it is worded for.</summary>
public enum Mention { None, You, All }

/// <summary>
/// Messages waiting for their notifications, handed over after the commit. Bounded: when it is full a notification is dropped, never
/// the send (the message is in the database, and open devices have it already).
/// </summary>
public sealed class PushQueue(IOptions<PushOptions> options, FcmAccount fcm, ILogger<PushQueue> log)
{
    private readonly Channel<PushJob> _jobs = Channel.CreateBounded<PushJob>(new BoundedChannelOptions(Math.Max(1, options.Value.QueueSize))
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,   // with Wait, TryWrite says no when full, so a drop can be logged
    });

    /// <summary>Browsers (Web Push keys set), the Android app (an FCM account set), or both.</summary>
    public bool Enabled { get; } = options.Value.Enabled || fcm.Enabled;

    public ChannelReader<PushJob> Reader => _jobs.Reader;

    public void Enqueue(PushJob job)
    {
        if (!Enabled) return;
        if (!_jobs.Writer.TryWrite(job)) log.LogWarning("push queue full; a notification was dropped");
    }
}
