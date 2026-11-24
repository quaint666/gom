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

    /// <summary>Tells everyone in a conversation that its pinned messages changed.</summary>
    public async Task PinsChangedAsync(GomDbContext db, Guid conversation)
    {
        try
        {
            var frame = Frames.Pins(conversation);
            foreach (var user in await db.Members.AsNoTracking().Where(m => m.ConversationId == conversation).Select(m => m.UserId).ToListAsync())
                foreach (var connection in registry.For(user)) connection.TryEnqueue(frame);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "pin notification failed; devices will see it the next time they look");
        }
    }

    /// <summary>Tells one person's devices that they deleted a message for themself.</summary>
    public void MessageHidden(Guid person, Guid conversation, Guid message)
    {
        var frame = Frames.Hidden(conversation, message);
        foreach (var connection in registry.For(person)) connection.TryEnqueue(frame);
    }

    /// <summary>Sends one step of the stream (a message created or recalled) to every member's devices.</summary>
    /// <param name="eventSeq">The step's own number; for a recall it is later than the message's.</param>
    /// <param name="parentSeq">For an answer, the number of the message it quotes: members who joined after it are not shown its words or its writer.</param>
    public async Task PublishAsync(GomDbContext db, long eventSeq, string kind, MessageEndpoints.MessageDto message, long? parentSeq = null)
    {
        try
        {
            // Membership is read now, not remembered: whoever is a member at this moment gets it, nobody else.
            // And a member who joined after the message was written is not told anything about it, not even that it was taken back.
            var members = await db.Members.AsNoTracking().Where(m => m.ConversationId == message.ConversationId && m.JoinedSeq < message.Seq)
                .Select(m => new { m.UserId, m.JoinedSeq }).ToListAsync();
            var frame = Frames.Live(message.ConversationId, new EventDto(eventSeq, kind, message));
            // Two readers can see one answer differently: someone who joined after the quoted message gets the quote with nothing in it.
            var hidden = message.Reply is null || parentSeq is null ? frame
                : Frames.Live(message.ConversationId, new EventDto(eventSeq, kind, message with { Reply = Replies.Hidden(message.Reply.Id) }));
            foreach (var member in members)
            {
                var send = parentSeq is { } p && member.JoinedSeq >= p ? hidden : frame;
                foreach (var connection in registry.For(member.UserId))
                    connection.TryEnqueue(send);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "live delivery failed; devices will catch up from their cursor");
        }
    }
}
