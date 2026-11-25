using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>What a notice says, for the page to put in words: what happened, who it was about, for a rename the new name, for a trim the date.</summary>
public sealed record NoticeDto(string Type, MessageEndpoints.PersonDto? Target, string? Title, DateTimeOffset? Before = null);

/// <summary>
/// Notices: the lines in a group's thread that say who was added, who was taken out, who left, and what the group was renamed to;
/// and, in any conversation, that the owner trimmed old pictures and files, or old messages (naming nobody).
/// A notice takes its place in the stream like a message (so a device that was away gets it from sync, and everyone sees the same
/// order) but it is not something anyone wrote: it cannot be answered, taken back, edited, pinned, reacted to or searched, does not
/// count as unread, and is not the "last message" in the list.
/// </summary>
public static class Notices
{
    private static string Name(NoticeKind kind) => kind switch
    {
        NoticeKind.Added => "added", NoticeKind.Removed => "removed", NoticeKind.Left => "left", NoticeKind.FilesTrimmed => "filesTrimmed", NoticeKind.MessagesTrimmed => "messagesTrimmed", _ => "renamed",
    };

    private static NoticeDto Dto(NoticeKind kind, MessageEndpoints.PersonDto? target, string body) => new(Name(kind), target,
        kind == NoticeKind.Renamed ? body : null,
        kind is NoticeKind.FilesTrimmed or NoticeKind.MessagesTrimmed ? DateTimeOffset.Parse(body, System.Globalization.CultureInfo.InvariantCulture) : null);

    /// <summary>
    /// Writes a notice into the stream. <paramref name="text"/> is the new name of a rename, the date of a trim, else empty. Call it inside the transaction that made the change, with the conversation locked. Returns what
    /// to hand to <see cref="Publish"/> once the transaction has committed.
    /// </summary>
    public static async Task<Message> Write(GomDbContext db, Guid conversation, Guid actor, NoticeKind kind, Guid? target, string text, DateTimeOffset now, CancellationToken ct)
    {
        // The number moves on, but the conversation does not rise in the list: nothing was said.
        var seq = (await db.Database.SqlQuery<long>(
            $"UPDATE conversations SET last_seq = last_seq + 1 WHERE id = {conversation} RETURNING last_seq AS \"Value\"").ToListAsync(ct)).Single();
        var message = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conversation, Seq = seq, SenderId = actor, ClientId = Guid.NewGuid(), Body = text,
            CreatedAt = now, Notice = kind, NoticeTargetId = target,
        };
        db.Messages.Add(message);
        db.Events.Add(new MessageEvent { ConversationId = conversation, Seq = seq, Kind = EventKind.Created, MessageId = message.Id, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>Tells the members who are in the conversation now. After the commit.</summary>
    public static async Task Publish(GomDbContext db, LiveDelivery live, Message notice, CancellationToken ct)
    {
        var people = await db.Users.AsNoTracking().Where(u => u.Id == notice.SenderId || u.Id == notice.NoticeTargetId).Select(u => new MessageEndpoints.PersonDto(u.Id, u.DisplayName)).ToListAsync(ct);
        var actor = people.Single(p => p.Id == notice.SenderId);
        var target = notice.NoticeTargetId is { } t ? people.Single(p => p.Id == t) : null;
        var dto = MessageEndpoints.ToDto(notice, actor, null, null, new MessageExtras(null, Dto(notice.Notice!.Value, target, notice.Body)));
        await live.PublishAsync(db, notice.Seq, "created", dto);
    }

    /// <summary>The notices among these messages, with the person each is about named.</summary>
    public static async Task<Dictionary<Guid, NoticeDto>> For(GomDbContext db, IEnumerable<Guid> messageIds, CancellationToken ct)
    {
        var ids = messageIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await (from m in db.Messages.AsNoTracking() where ids.Contains(m.Id) && m.Notice != null
                          join u in db.Users on m.NoticeTargetId equals u.Id into targets
                          from t in targets.DefaultIfEmpty()
                          select new { m.Id, Kind = m.Notice!.Value, m.Body, TargetId = (Guid?)t.Id, TargetName = t.DisplayName }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => Dto(r.Kind, r.TargetId is { } id ? new MessageEndpoints.PersonDto(id, r.TargetName) : null, r.Body));
    }
}
