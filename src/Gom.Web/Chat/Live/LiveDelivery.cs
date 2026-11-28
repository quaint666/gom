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

    /// <summary>
    /// Tells the other members' open pages that this person is typing. Only when both share their reading (the same switch as read
    /// receipts: someone who hides when they read does not show when they type, and does not see it either), to members of the
    /// conversation only, and in a one-to-one chat only while the two are still friends. Lost when a page is behind; never kept.
    /// </summary>
    public async Task TypingAsync(GomDbContext db, Guid conversation, Guid person, CancellationToken ct)
    {
        try
        {
            var access = await Access.Of(db, conversation, person, ct);
            if (access is null || access.Kind == ConversationKind.Announce) return;   // nobody types in the announcements
            var name = await db.Users.AsNoTracking().Where(u => u.Id == person && u.ShareReadReceipts).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
            if (name is null) return;
            if (access.Kind == ConversationKind.Direct && !await MessageEndpoints.StillFriends(db, conversation, person, ct)) return;
            var watchers = await (from m in db.Members.AsNoTracking() where m.ConversationId == conversation && m.UserId != person
                                  join u in db.Users on m.UserId equals u.Id where u.ShareReadReceipts && u.DisabledAt == null select m.UserId).ToListAsync(ct);
            var frame = Frames.Typing(conversation, person, name);
            foreach (var user in watchers)
                foreach (var connection in registry.For(user))
                    connection.TryEnqueueLossy(frame);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "typing notification failed");   // a hint: nothing to catch up
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

    /// <summary>Tells one person's devices that they deleted a conversation for themself, up to this number.</summary>
    public void ConversationCleared(Guid person, Guid conversation, long seq)
    {
        var frame = Frames.Cleared(conversation, seq);
        foreach (var connection in registry.For(person)) connection.TryEnqueue(frame);
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
            // Someone who deleted the conversation for themself after it is not told either: for them it is gone.
            var members = await db.Members.AsNoTracking().Where(m => m.ConversationId == message.ConversationId && m.JoinedSeq < message.Seq && m.ClearedSeq < message.Seq)
                .Select(m => new { m.UserId, SeesAfter = m.JoinedSeq > m.ClearedSeq ? m.JoinedSeq : m.ClearedSeq }).ToListAsync();
            // Someone who deleted this message for themself is not told of later changes to it: it would come back.
            var gone = kind == "created" ? [] : await db.Hides.AsNoTracking().Where(h => h.MessageId == message.Id).Select(h => h.UserId).ToListAsync();
            members = members.Where(m => !gone.Contains(m.UserId)).ToList();
            var frame = Frames.Live(message.ConversationId, new EventDto(eventSeq, kind, message));
            // Two readers can see one answer differently: someone who joined after the quoted message gets the quote with nothing in it.
            var hidden = message.Reply is null || parentSeq is null ? frame
                : Frames.Live(message.ConversationId, new EventDto(eventSeq, kind, message with { Reply = Replies.Hidden(message.Reply.Id) }));
            foreach (var member in members)
            {
                var send = parentSeq is { } p && member.SeesAfter >= p ? hidden : frame;
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
