using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
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
    /// <summary>A conversation's own colour and background; null for either: each person's own.</summary>
    public sealed record LookRequest(string? Palette, string? Pattern);

    /// <summary>The list carries only the start of the last message; history has the whole of it.</summary>
    public const int PreviewChars = 200;

    /// <summary>The longest a mute for a while may last; longer is "until turned back on". The choices offered end within a day.</summary>
    private static readonly TimeSpan LongestMute = TimeSpan.FromDays(7);

    public static void MapConversations(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/conversations").RequireAuthorization();
        api.MapGet("", List);
        api.MapPost("/direct", OpenDirect);
        api.MapPut("/{id:guid}/mute", SetMute);
        api.MapPut("/{id:guid}/look", SetLook);
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

    /// <summary>
    /// Anyone in a conversation sets its colour and background for everyone in it, or gives each person their own back (null). A line
    /// in the conversation says who changed it, as a rename does; setting what it already is changes nothing and says nothing. A
    /// one-to-one chat needs the two to still be friends, as a message does.
    /// </summary>
    private static async Task<IResult> SetLook(Guid id, LookRequest request, HttpContext http, GomDbContext db, LiveDelivery live, TimeProvider clock, CancellationToken ct)
    {
        if (request.Palette is { } p && !Appearance.Palettes.Contains(p)) return Results.BadRequest(new { error = "invalid_palette" });
        if (request.Pattern is { } q && !Appearance.Patterns.Contains(q)) return Results.BadRequest(new { error = "invalid_pattern" });
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind == ConversationKind.Direct && !await MessageEndpoints.StillFriends(db, id, me, ct))
            return Results.Json(new { error = "not_friends" }, statusCode: StatusCodes.Status403Forbidden);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await GroupEndpoints.LockGroup(db, id, ct);   // the same lock a send takes, so the notice gets its number in order
        if (!await db.Members.AnyAsync(m => m.ConversationId == id && m.UserId == me, ct)) return Results.NotFound();
        var before = await db.Conversations.Where(c => c.Id == id).Select(c => new { c.Palette, c.Pattern }).SingleAsync(ct);
        if (before.Palette == request.Palette && before.Pattern == request.Pattern) return Results.NoContent();
        await db.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Palette, request.Palette).SetProperty(c => c.Pattern, request.Pattern), ct);
        var notice = await Notices.Write(db, id, me, NoticeKind.Restyled, null, "", clock.GetUtcNow(), ct);
        var everyone = await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct);
        await tx.CommitAsync(ct);
        live.ConversationsChanged(everyone);
        await Notices.Publish(db, live, notice, ct);
        return Results.NoContent();
    }

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
                c.Id, c.Kind, c.Title, c.LastSeq, c.JoinApproval, c.Palette, c.Pattern, c.DisappearSeconds, c.AvatarId, m.Role, m.LastReadSeq, m.Muted, m.MutedUntil, m.PinnedAt, m.MarkedUnread, m.HiddenSeq,
                MemberCount = db.Members.Count(o => o.ConversationId == c.Id),
                Peer = db.Members.Where(o => o.ConversationId == c.Id && o.UserId != me)
                    .Join(db.Users, o => o.UserId, u => u.Id, (o, u) => new
                    {
                        u.Id, u.GomId, u.DisplayName, u.AvatarId, Deleted = u.DeletedAt != null,
                        Friend = db.Friendships.Any(f => ((f.UserLow == me && f.UserHigh == u.Id) || (f.UserLow == u.Id && f.UserHigh == me)) && f.Status == FriendStatus.Accepted),
                    }).FirstOrDefault(),
                Last = db.Messages.Where(x => x.ConversationId == c.Id && x.Seq > m.JoinedSeq && x.Seq > m.ClearedSeq && (x.Notice == null || x.Notice == NoticeKind.Call) && !db.Hides.Any(h => h.UserId == me && h.MessageId == x.Id)).OrderByDescending(x => x.Seq)
                    .Join(db.Users, x => x.SenderId, u => u.Id, (x, u) => new
                    {
                        x.Seq, Body = x.Body.Length > PreviewChars ? x.Body.Substring(0, PreviewChars) : x.Body, x.Sticker, x.CreatedAt, Deleted = x.DeletedAt != null, SenderId = u.Id, u.DisplayName,
                        Attached = db.Attachments.Where(a => a.MessageId == x.Id).OrderBy(a => a.Position).Select(a => (int?)a.Kind).FirstOrDefault(),
                        // A call's line is the last thing too (Notices: a call is the one notice that is).
                        Call = db.Calls.Where(k => k.MessageId == x.Id).Select(k => new { k.CallerId, k.Outcome, k.CreatedAt, k.AnsweredAt, k.EndedAt }).FirstOrDefault(),
                    }).FirstOrDefault(),
                // A notice is not unread, except a call this person missed (rang out, or found them busy).
                Unread = db.Messages.Count(x => x.ConversationId == c.Id && x.Seq > m.LastReadSeq && x.SenderId != me && x.DeletedAt == null
                    && (x.Notice == null || (x.Notice == NoticeKind.Call && db.Calls.Any(k => k.MessageId == x.Id && k.CalleeId == me && (k.Outcome == CallOutcome.Missed || k.Outcome == CallOutcome.Busy))))
                    && !db.Hides.Any(h => h.UserId == me && h.MessageId == x.Id)),
            }).ToListAsync(ct);

        // Pinned ones first, the latest pin on top. Hidden ones are listed too, marked, so a search can find them; the page leaves them out of view.
        var ordered = rows.OrderByDescending(r => r.PinnedAt.HasValue).ThenByDescending(r => r.PinnedAt).ToList();
        return Results.Ok(ordered.Select(r => new
        {
            id = r.Id,
            kind = r.Kind.ToString().ToLowerInvariant(),
            title = r.Kind == ConversationKind.Direct ? r.Peer?.DisplayName : r.Title,
            // A Gom ID is shown to friends only: after an unfriend or a block the chat stays, its ID does not.
            peer = r.Kind == ConversationKind.Direct && r.Peer is not null ? new { id = r.Peer.Id, gomId = r.Peer.Friend ? r.Peer.GomId : null, displayName = r.Peer.DisplayName, avatar = r.Peer.AvatarId, deleted = r.Peer.Deleted } : null,
            memberCount = r.MemberCount,
            lastSeq = r.LastSeq,
            lastReadSeq = r.LastReadSeq,
            unread = r.Unread,
            muted = r.Muted || r.MutedUntil > now,
            mutedUntil = r.MutedUntil > now ? r.MutedUntil : null,
            joinApproval = r.Kind == ConversationKind.Group && r.JoinApproval,
            // Your place in a group (the owner's invite can add people only to a group they lead or are a deputy of); null for a 1:1 chat.
            role = r.Kind == ConversationKind.Group ? r.Role.ToString().ToLowerInvariant() : null,
            // A group's picture; a 1:1 chat shows the other person's (peer.avatar).
            avatar = r.Kind == ConversationKind.Group ? r.AvatarId : null,
            // Its own colour and background, the same for everyone in it; null: each person's own.
            palette = r.Palette,
            pattern = r.Pattern,
            // Disappearing messages: how long a message sent now lasts, in seconds; null: for good.
            disappearSeconds = r.DisappearSeconds,
            pinned = r.PinnedAt is not null,
            markedUnread = r.MarkedUnread,
            // Hidden until something is said after it was hidden (a notice does not bring it back).
            hidden = r.HiddenSeq is { } h && (r.Last is null || r.Last.Seq <= h),
            lastMessage = r.Last is null ? null : new
            {
                seq = r.Last.Seq, body = r.Last.Body, createdAt = r.Last.CreatedAt, deleted = r.Last.Deleted,
                attachment = r.Last.Attached is { } kind ? Uploads.AttachmentEndpoints.KindName((AttachmentKind)kind) : null,
                sticker = r.Last.Deleted ? null : r.Last.Sticker,
                sender = new { id = r.Last.SenderId, displayName = r.Last.DisplayName },
                call = r.Last.Call is { } k ? Chat.CallNoticeDto.Of(k.CallerId, k.Outcome, k.CreatedAt, k.AnsweredAt, k.EndedAt) : null,
            },
        }));
    }
}
