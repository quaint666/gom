using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gom.Web.Social;

/// <summary>Starting a one-to-one conversation and listing the ones you are in.</summary>
public static class ConversationEndpoints
{
    public sealed record OpenRequest(string? GomId);
    public sealed record MuteRequest(bool? Muted, DateTimeOffset? Until);
    public sealed record SwitchRequest(bool? On);

    /// <summary>The list carries only the start of the last message; history has the whole of it.</summary>
    private const int PreviewChars = 200;

    /// <summary>The longest a mute for a while may last; longer is "until turned back on". The choices offered end within a day.</summary>
    private static readonly TimeSpan LongestMute = TimeSpan.FromDays(7);

    public static void MapConversations(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/conversations").RequireAuthorization();
        api.MapGet("", List);
        api.MapPost("/direct", OpenDirect);
        api.MapPut("/{id:guid}/mute", SetMute);
        api.MapPut("/{id:guid}/pinned", SetPinned);
        api.MapPut("/{id:guid}/unread", SetUnread);
        api.MapPut("/{id:guid}/hidden", SetHidden);
        api.MapPost("/{id:guid}/clear", Clear);
    }

    // ---- this person's own handling of a conversation in their list (the "..." menu); nobody else sees any of it

    private static IResult InvalidSwitch() => Results.Json(new { error = "invalid_setting" }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Pins it to the top of this person's list (a few at most), or takes the pin away. Pinning again changes nothing.</summary>
    private static async Task<IResult> SetPinned(Guid id, SwitchRequest request, HttpContext http, GomDbContext db, TimeProvider clock,
        Chat.Live.LiveDelivery live, Microsoft.Extensions.Options.IOptions<Chat.ChatOptions> options, CancellationToken ct)
    {
        if (request.On is not { } on) return InvalidSwitch();
        var me = GomClaims.User(http.User);
        var row = await db.Members.Where(m => m.ConversationId == id && m.UserId == me).Select(m => new { m.PinnedAt }).SingleOrDefaultAsync(ct);
        if (row is null) return Results.NotFound();
        if (on == (row.PinnedAt is not null)) return Results.NoContent();
        if (on && await db.Members.CountAsync(m => m.UserId == me && m.PinnedAt != null, ct) >= options.Value.MaxPinnedConversations)
            return Results.Json(new { error = "pin_limit" }, statusCode: StatusCodes.Status409Conflict);
        DateTimeOffset? at = on ? clock.GetUtcNow() : null;
        await db.Members.Where(m => m.ConversationId == id && m.UserId == me).ExecuteUpdateAsync(s => s.SetProperty(m => m.PinnedAt, at), ct);
        live.ConversationsChanged([me]);
        return Results.NoContent();
    }

    /// <summary>
    /// Marks it unread, to come back to, or clears that mark. The read position does not move: others who see how far you have read would
    /// see it go back. Reading the conversation clears the mark too.
    /// </summary>
    private static async Task<IResult> SetUnread(Guid id, SwitchRequest request, HttpContext http, GomDbContext db, Chat.Live.LiveDelivery live, CancellationToken ct)
    {
        if (request.On is not { } on) return InvalidSwitch();
        var me = GomClaims.User(http.User);
        var changed = await db.Members.Where(m => m.ConversationId == id && m.UserId == me).ExecuteUpdateAsync(s => s.SetProperty(m => m.MarkedUnread, on), ct);
        if (changed == 0) return Results.NotFound();
        live.ConversationsChanged([me]);
        return Results.NoContent();
    }

    /// <summary>Hides it from this person's list until something new is said in it, or shows it again. The conversation itself is untouched.</summary>
    private static async Task<IResult> SetHidden(Guid id, SwitchRequest request, HttpContext http, GomDbContext db, Chat.Live.LiveDelivery live, CancellationToken ct)
    {
        if (request.On is not { } on) return InvalidSwitch();
        var me = GomClaims.User(http.User);
        var newest = await db.Conversations.Where(c => c.Id == id).Select(c => (long?)c.LastSeq).SingleOrDefaultAsync(ct);
        long? hidden = on ? newest : null;
        var changed = newest is null ? 0 : await db.Members.Where(m => m.ConversationId == id && m.UserId == me).ExecuteUpdateAsync(s => s.SetProperty(m => m.HiddenSeq, hidden), ct);
        if (changed == 0) return Results.NotFound();
        live.ConversationsChanged([me]);
        return Results.NoContent();
    }

    /// <summary>
    /// Deletes the conversation for this person only: everything said so far is gone for them (as if they had joined now), and it leaves
    /// their list until something new is said. Everyone else keeps it all. They stay in the conversation, and nothing reads as unread.
    /// </summary>
    private static async Task<IResult> Clear(Guid id, HttpContext http, GomDbContext db, Chat.Live.LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var newest = await db.Conversations.Where(c => c.Id == id).Select(c => (long?)c.LastSeq).SingleOrDefaultAsync(ct);
        if (newest is not { } upTo) return Results.NotFound();
        var changed = await db.Members.Where(m => m.ConversationId == id && m.UserId == me).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.ClearedSeq, m => m.ClearedSeq > upTo ? m.ClearedSeq : upTo)
            .SetProperty(m => m.LastReadSeq, m => m.LastReadSeq > upTo ? m.LastReadSeq : upTo)
            .SetProperty(m => m.HiddenSeq, (long?)upTo)
            .SetProperty(m => m.MarkedUnread, false)
            .SetProperty(m => m.PinnedAt, (DateTimeOffset?)null), ct);
        if (changed == 0) return Results.NotFound();
        live.ConversationCleared(me, id, upTo);
        live.ConversationsChanged([me]);
        return Results.NoContent();
    }

    private static async Task<IResult> OpenDirect(OpenRequest request, HttpContext http, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var other = await FriendEndpoints.FindMember(db, request.GomId, ct);
        if (other is null) return Results.Json(new { error = "user_not_found" }, statusCode: StatusCodes.Status404NotFound);
        if (other == me) return Results.Json(new { error = "cannot_chat_self" }, statusCode: StatusCodes.Status400BadRequest);

        var (low, high) = Friendship.Order(me, other.Value);
        // A new conversation needs a friend. (A blocked pair is never friends: blocking removes the friendship.)
        var friends = await db.Friendships.AnyAsync(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Accepted, ct);
        if (!friends) return Results.Json(new { error = "not_friends" }, statusCode: StatusCodes.Status403Forbidden);

        // A pair has one conversation (unique key). If two open it at once, the loser of the insert finds the winner's.
        var key = $"{low}:{high}";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await db.Conversations.Where(c => c.DirectKey == key).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct);
            if (existing is not null) return Results.Ok(new { id = existing.Value });

            var now = clock.GetUtcNow();
            var chat = new Conversation { Id = Guid.NewGuid(), Kind = ConversationKind.Direct, DirectKey = key, CreatedBy = me, CreatedAt = now };
            db.Conversations.Add(chat);
            db.Members.Add(new ConversationMember { ConversationId = chat.Id, UserId = low, JoinedAt = now });
            db.Members.Add(new ConversationMember { ConversationId = chat.Id, UserId = high, JoinedAt = now });
            try
            {
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { id = chat.Id });
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Mutes a conversation for this person only: until they turn it back on, or with <c>until</c> for a while (the phone works out the
    /// time, so "until 8 in the morning" is its own morning). Unmuting clears both. Their other devices are told to reload the list.
    /// </summary>
    private static async Task<IResult> SetMute(Guid id, MuteRequest request, HttpContext http, GomDbContext db, TimeProvider clock, Chat.Live.LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (request.Muted is not { } muted) return InvalidMute();
        var now = clock.GetUtcNow();
        if (request.Until is { } until && (!muted || until <= now || until > now + LongestMute)) return InvalidMute();
        var always = muted && request.Until is null;
        var ends = request.Until?.ToUniversalTime();
        var changed = await db.Members.Where(m => m.ConversationId == id && m.UserId == me)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Muted, always).SetProperty(m => m.MutedUntil, ends), ct);
        if (changed == 0) return Results.NotFound();
        live.ConversationsChanged([me]);
        return Results.NoContent();
    }

    private static IResult InvalidMute() => Results.Json(new { error = "invalid_mute" }, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<IResult> List(HttpContext http, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var now = clock.GetUtcNow();
        var rows = await (
            from m in db.Members
            where m.UserId == me
            join c in db.Conversations on m.ConversationId equals c.Id
            orderby (c.LastMessageAt ?? c.CreatedAt) descending
            select new
            {
                c.Id, c.Kind, c.Title, c.LastSeq, c.JoinApproval, m.LastReadSeq, m.Muted, m.MutedUntil, m.PinnedAt, m.MarkedUnread, m.HiddenSeq,
                MemberCount = db.Members.Count(o => o.ConversationId == c.Id),
                Peer = db.Members.Where(o => o.ConversationId == c.Id && o.UserId != me)
                    .Join(db.Users, o => o.UserId, u => u.Id, (o, u) => new { u.Id, u.GomId, u.DisplayName, u.AvatarId, Deleted = u.DeletedAt != null }).FirstOrDefault(),
                Last = db.Messages.Where(x => x.ConversationId == c.Id && x.Seq > m.JoinedSeq && x.Seq > m.ClearedSeq && x.Notice == null && !db.Hides.Any(h => h.UserId == me && h.MessageId == x.Id)).OrderByDescending(x => x.Seq)
                    .Join(db.Users, x => x.SenderId, u => u.Id, (x, u) => new
                    {
                        x.Seq, Body = x.Body.Length > PreviewChars ? x.Body.Substring(0, PreviewChars) : x.Body, x.Sticker, x.CreatedAt, Deleted = x.DeletedAt != null, SenderId = u.Id, u.DisplayName,
                        Attached = db.Attachments.Where(a => a.MessageId == x.Id).OrderBy(a => a.Position).Select(a => (int?)a.Kind).FirstOrDefault(),
                    }).FirstOrDefault(),
                Unread = db.Messages.Count(x => x.ConversationId == c.Id && x.Seq > m.LastReadSeq && x.SenderId != me && x.DeletedAt == null && x.Notice == null && !db.Hides.Any(h => h.UserId == me && h.MessageId == x.Id)),
            }).ToListAsync(ct);

        // Hidden until something is said after it was hidden (a notice does not bring it back); pinned ones first, the latest pin on top.
        var shown = rows.Where(r => r.HiddenSeq is not { } h || (r.Last is not null && r.Last.Seq > h))
            .OrderByDescending(r => r.PinnedAt.HasValue).ThenByDescending(r => r.PinnedAt).ToList();
        return Results.Ok(shown.Select(r => new
        {
            id = r.Id,
            kind = r.Kind.ToString().ToLowerInvariant(),
            title = r.Kind == ConversationKind.Direct ? r.Peer?.DisplayName : r.Title,
            peer = r.Kind == ConversationKind.Direct && r.Peer is not null ? new { id = r.Peer.Id, gomId = r.Peer.GomId, displayName = r.Peer.DisplayName, avatar = r.Peer.AvatarId, deleted = r.Peer.Deleted } : null,
            memberCount = r.MemberCount,
            lastSeq = r.LastSeq,
            lastReadSeq = r.LastReadSeq,
            unread = r.Unread,
            muted = r.Muted || r.MutedUntil > now,
            mutedUntil = r.MutedUntil > now ? r.MutedUntil : null,
            joinApproval = r.Kind == ConversationKind.Group && r.JoinApproval,
            pinned = r.PinnedAt is not null,
            markedUnread = r.MarkedUnread,
            lastMessage = r.Last is null ? null : new
            {
                seq = r.Last.Seq, body = r.Last.Body, createdAt = r.Last.CreatedAt, deleted = r.Last.Deleted,
                attachment = r.Last.Attached is null ? null : r.Last.Attached == (int)AttachmentKind.Image ? "image" : "file",
                sticker = r.Last.Deleted ? null : r.Last.Sticker,
                sender = new { id = r.Last.SenderId, displayName = r.Last.DisplayName },
            },
        }));
    }
}
