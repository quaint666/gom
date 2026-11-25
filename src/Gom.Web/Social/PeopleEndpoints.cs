using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Social;

/// <summary>What one person's card shows about someone else: who they are to you, and the groups you are both in.</summary>
public static class PeopleEndpoints
{
    public static void MapPeople(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/people/{userId:guid}", Card);
    }

    /// <summary>Whether this person may see who that one is (their name, their picture): themself, a friend, or someone they share a conversation with now.</summary>
    internal static async Task<bool> MayKnow(GomDbContext db, Guid me, Guid userId, CancellationToken ct)
    {
        if (userId == me) return true;
        var (low, high) = Friendship.Order(me, userId);
        return await db.Friendships.AnyAsync(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Accepted, ct)
            || await (from mine in db.Members where mine.UserId == me
                      join theirs in db.Members on mine.ConversationId equals theirs.ConversationId
                      where theirs.UserId == userId select mine).AnyAsync(ct);
    }

    /// <summary>
    /// Someone you are friends with or share a conversation with now: their name, their Gom ID if they are your friend, where a friend
    /// request between you stands, and the groups you are both in, most recently active first. Anybody else is a 404, so an id alone
    /// finds nobody. Your own id is a 404 too: your card is the settings page.
    /// </summary>
    private static async Task<IResult> Card(Guid userId, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (userId == me) return Results.NotFound();
        var them = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Id, u.GomId, u.DisplayName, u.AvatarId, Deleted = u.DeletedAt != null }).SingleOrDefaultAsync(ct);
        if (them is null) return Results.NotFound();

        var (low, high) = Friendship.Order(me, userId);
        var row = await db.Friendships.AsNoTracking().SingleOrDefaultAsync(f => f.UserLow == low && f.UserHigh == high, ct);
        var friends = row?.Status == FriendStatus.Accepted;

        var shared =
            from mine in db.Members
            where mine.UserId == me
            join theirs in db.Members on mine.ConversationId equals theirs.ConversationId
            where theirs.UserId == userId
            join c in db.Conversations on mine.ConversationId equals c.Id
            select c;
        if (!friends && !await shared.AnyAsync(ct)) return Results.NotFound();

        var groups = await shared.Where(c => c.Kind == ConversationKind.Group)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Select(c => new { id = c.Id, title = c.Title, memberCount = db.Members.Count(m => m.ConversationId == c.Id) })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            id = them.Id,
            displayName = them.DisplayName,
            avatar = them.AvatarId,
            // A Gom ID is shown to friends only, as in a group's member list.
            gomId = friends ? them.GomId : null,
            // A request from someone I blocked is not shown to me.
            friend = friends ? "friends" : row is { Status: FriendStatus.Pending } ? (row.RequestedBy == me ? "sent"
                : await db.Blocks.AnyAsync(b => b.BlockerId == me && b.BlockedId == userId, ct) ? "none" : "received") : "none",
            commonGroups = groups,
            deleted = them.Deleted,
        });
    }
}
