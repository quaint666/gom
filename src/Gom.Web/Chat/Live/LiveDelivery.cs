using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat.Live;

/// <summary>Tells connected members about a new message. Runs only after the commit, and never fails the send.</summary>
public sealed class LiveDelivery(ConnectionRegistry registry, ILogger<LiveDelivery> logger)
{
    /// <summary>Tells these people's devices to reload their list of conversations.</summary>
    public void ConversationsChanged(IEnumerable<Guid> people)
    {
        var frame = Frames.ConversationsChanged();
        foreach (var person in people.Distinct())
            foreach (var connection in registry.For(person))
                connection.TryEnqueue(frame);
    }

    /// <summary>Sends one step of the stream (a message created or recalled) to every member's devices.</summary>
    /// <param name="eventSeq">The step's own number; for a recall it is later than the message's.</param>
    /// <summary>
    /// Tells the other members that this person has read up to here, but only if they share their reading and the other member does too.
    /// Never to the reader themself.
    /// </summary>
    public async Task ReadChangedAsync(GomDbContext db, Guid conversation, Guid reader, long seq)
    {
        try
        {
            if (!await db.Users.AsNoTracking().Where(u => u.Id == reader).Select(u => u.ShareReadReceipts).SingleAsync()) return;
            var watchers = await (from m in db.Members.AsNoTracking() where m.ConversationId == conversation && m.UserId != reader
                                  join u in db.Users on m.UserId equals u.Id where u.ShareReadReceipts select m.UserId).ToListAsync();
            var frame = Frames.Read(conversation, reader, seq);
            foreach (var user in watchers)
                foreach (var connection in registry.For(user))
                    connection.TryEnqueue(frame);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "read notification failed; devices will see it the next time they look");
        }
    }

    public async Task PublishAsync(GomDbContext db, long eventSeq, string kind, MessageEndpoints.MessageDto message)
    {
        try
        {
            // Membership is read now, not remembered: whoever is a member at this moment gets it, nobody else.
            // And a member who joined after the message was written is not told anything about it, not even that it was taken back.
            var members = await db.Members.AsNoTracking().Where(m => m.ConversationId == message.ConversationId && m.JoinedSeq < message.Seq).Select(m => m.UserId).ToListAsync();
            var frame = Frames.Live(message.ConversationId, new EventDto(eventSeq, kind, message));
            foreach (var user in members)
                foreach (var connection in registry.For(user))
                    connection.TryEnqueue(frame);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "live delivery failed; devices will catch up from their cursor");
        }
    }
}
