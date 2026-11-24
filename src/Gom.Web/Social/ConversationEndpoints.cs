using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gom.Web.Social;

/// <summary>Starting a one-to-one conversation and listing the ones you are in.</summary>
public static class ConversationEndpoints
{
    public sealed record OpenRequest(string? GomId);
    public sealed record MuteRequest(bool? Muted);

    /// <summary>The list carries only the start of the last message; history has the whole of it.</summary>
    private const int PreviewChars = 200;

    public static void MapConversations(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/conversations").RequireAuthorization();
        api.MapGet("", List);
        api.MapPost("/direct", OpenDirect);
        api.MapPut("/{id:guid}/mute", SetMute);
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

    /// <summary>Mutes or unmutes a conversation for this person only. Their other devices are told to reload the list.</summary>
    private static async Task<IResult> SetMute(Guid id, MuteRequest request, HttpContext http, GomDbContext db, Chat.Live.LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (request.Muted is not { } muted) return Results.Json(new { error = "invalid_mute" }, statusCode: StatusCodes.Status400BadRequest);
        var changed = await db.Members.Where(m => m.ConversationId == id && m.UserId == me).ExecuteUpdateAsync(s => s.SetProperty(m => m.Muted, muted), ct);
        if (changed == 0) return Results.NotFound();
        live.ConversationsChanged([me]);
        return Results.NoContent();
    }

    private static async Task<IResult> List(HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var rows = await (
            from m in db.Members
            where m.UserId == me
            join c in db.Conversations on m.ConversationId equals c.Id
            orderby (c.LastMessageAt ?? c.CreatedAt) descending
            select new
            {
                c.Id, c.Kind, c.Title, c.LastSeq, c.JoinApproval, m.LastReadSeq, m.Muted,
                MemberCount = db.Members.Count(o => o.ConversationId == c.Id),
                Peer = db.Members.Where(o => o.ConversationId == c.Id && o.UserId != me)
                    .Join(db.Users, o => o.UserId, u => u.Id, (o, u) => new { u.GomId, u.DisplayName }).FirstOrDefault(),
                Last = db.Messages.Where(x => x.ConversationId == c.Id && x.Seq > m.JoinedSeq && !db.Hides.Any(h => h.UserId == me && h.MessageId == x.Id)).OrderByDescending(x => x.Seq)
                    .Join(db.Users, x => x.SenderId, u => u.Id, (x, u) => new
                    {
                        x.Seq, Body = x.Body.Length > PreviewChars ? x.Body.Substring(0, PreviewChars) : x.Body, x.CreatedAt, Deleted = x.DeletedAt != null, SenderId = u.Id, u.DisplayName,
                    }).FirstOrDefault(),
                Unread = db.Messages.Count(x => x.ConversationId == c.Id && x.Seq > m.LastReadSeq && x.SenderId != me && x.DeletedAt == null && !db.Hides.Any(h => h.UserId == me && h.MessageId == x.Id)),
            }).ToListAsync(ct);

        return Results.Ok(rows.Select(r => new
        {
            id = r.Id,
            kind = r.Kind.ToString().ToLowerInvariant(),
            title = r.Kind == ConversationKind.Direct ? r.Peer?.DisplayName : r.Title,
            peer = r.Kind == ConversationKind.Direct && r.Peer is not null ? new { gomId = r.Peer.GomId, displayName = r.Peer.DisplayName } : null,
            memberCount = r.MemberCount,
            lastSeq = r.LastSeq,
            lastReadSeq = r.LastReadSeq,
            unread = r.Unread,
            muted = r.Muted,
            joinApproval = r.Kind == ConversationKind.Group && r.JoinApproval,
            lastMessage = r.Last is null ? null : new
            {
                seq = r.Last.Seq, body = r.Last.Body, createdAt = r.Last.CreatedAt, deleted = r.Last.Deleted,
                sender = new { id = r.Last.SenderId, displayName = r.Last.DisplayName },
            },
        }));
    }
}
