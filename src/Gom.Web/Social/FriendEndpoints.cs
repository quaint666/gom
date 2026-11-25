using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gom.Web.Social;

/// <summary>Friend requests, the friend list, and blocking. People are named by their exact Gom ID, never searched.</summary>
public static class FriendEndpoints
{
    public sealed record AskRequest(string? GomId);

    public static void MapFriends(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/friends", List);
        api.MapPost("/friends/requests", Ask);
        api.MapPost("/friends/requests/{gomId}/accept", Accept);
        api.MapDelete("/friends/requests/{gomId}", Withdraw);
        api.MapDelete("/friends/{gomId}", Unfriend);
        api.MapGet("/blocks", ListBlocks);
        api.MapPost("/blocks/{gomId}", Block);
        api.MapDelete("/blocks/{gomId}", Unblock);
    }

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    /// <summary>
    /// The member with this exact Gom ID (any case, spaces trimmed), or null. A locked member counts as absent, and so does anything that
    /// cannot be a Gom ID (it never reaches the database: PostgreSQL refuses a NUL in text, which would be a 500).
    /// </summary>
    internal static Task<Guid?> FindMember(GomDbContext db, string? gomId, CancellationToken ct)
    {
        var id = (gomId ?? "").Trim().ToLowerInvariant();
        if (!GomId.IsValid(id)) return Task.FromResult<Guid?>(null);
        return db.Users.Where(u => u.GomId == id && u.DisabledAt == null).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct);
    }

    private static async Task<IResult> List(HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var rows = await (
            from f in db.Friendships
            where f.UserLow == me || f.UserHigh == me
            join u in db.Users on (f.UserLow == me ? f.UserHigh : f.UserLow) equals u.Id
            where u.DisabledAt == null
            orderby u.DisplayName, u.GomId
            select new { f.Status, f.RequestedBy, f.CreatedAt, u.GomId, u.DisplayName, u.AvatarId, OtherId = u.Id }).ToListAsync(ct);

        return Results.Ok(new
        {
            friends = rows.Where(r => r.Status == FriendStatus.Accepted).Select(r => new { gomId = r.GomId, displayName = r.DisplayName, avatar = r.AvatarId }),
            incoming = rows.Where(r => r.Status == FriendStatus.Pending && r.RequestedBy != me)
                .Select(r => new { gomId = r.GomId, displayName = r.DisplayName, avatar = r.AvatarId, createdAt = r.CreatedAt }),
            outgoing = rows.Where(r => r.Status == FriendStatus.Pending && r.RequestedBy == me)
                .Select(r => new { gomId = r.GomId, displayName = r.DisplayName, avatar = r.AvatarId, createdAt = r.CreatedAt }),
        });
    }

    private static async Task<IResult> Ask(AskRequest request, HttpContext http, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var other = await FindMember(db, request.GomId, ct);
        if (other is null) return Error(StatusCodes.Status404NotFound, "user_not_found");
        return await AskAsync(db, clock, me, other.Value, ct);
    }

    /// <summary>Asks one person to be friends. Shared by "ask by Gom ID" and "ask a member of a group you are both in".</summary>
    internal static async Task<IResult> AskAsync(GomDbContext db, TimeProvider clock, Guid me, Guid other, CancellationToken ct)
    {
        if (other == me) return Error(StatusCodes.Status400BadRequest, "cannot_add_self");

        if (await db.Blocks.AnyAsync(b => b.BlockerId == me && b.BlockedId == other, ct)) return Error(StatusCodes.Status409Conflict, "you_blocked_them");
        // Someone who blocked you must not learn it: it looks like the request went out, and nothing is stored.
        if (await db.Blocks.AnyAsync(b => b.BlockerId == other && b.BlockedId == me, ct)) return Results.Ok(new { status = "pending" });

        var (low, high) = Friendship.Order(me, other);
        // Two people asking each other at once: one insert loses the race, then sees the other's request and accepts it.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await db.Friendships.AsNoTracking().SingleOrDefaultAsync(f => f.UserLow == low && f.UserHigh == high, ct);
            if (existing is not null)
            {
                if (existing.Status == FriendStatus.Accepted) return Error(StatusCodes.Status409Conflict, "already_friends");
                if (existing.RequestedBy == me) return Results.Ok(new { status = "pending" });
                var now = clock.GetUtcNow();
                await db.Friendships.Where(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Pending)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FriendStatus.Accepted).SetProperty(f => f.AcceptedAt, (DateTimeOffset?)now), ct);
                return Results.Ok(new { status = "friends" });
            }

            db.Friendships.Add(new Friendship { UserLow = low, UserHigh = high, RequestedBy = me, Status = FriendStatus.Pending, CreatedAt = clock.GetUtcNow() });
            try
            {
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { status = "pending" });
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> Accept(string gomId, HttpContext http, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var other = await FindMember(db, gomId, ct);
        if (other is null) return Results.NotFound();
        var (low, high) = Friendship.Order(me, other.Value);
        var now = clock.GetUtcNow();
        // Only the one who was asked can accept: the request must have come from the other person.
        var accepted = await db.Friendships
            .Where(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Pending && f.RequestedBy == other)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FriendStatus.Accepted).SetProperty(f => f.AcceptedAt, (DateTimeOffset?)now), ct);
        return accepted == 0 ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>The one asked declines, or the one who asked cancels: the request disappears either way.</summary>
    private static async Task<IResult> Withdraw(string gomId, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var other = await FindMember(db, gomId, ct);
        if (other is null) return Results.NotFound();
        var (low, high) = Friendship.Order(me, other.Value);
        var removed = await db.Friendships.Where(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Pending).ExecuteDeleteAsync(ct);
        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> Unfriend(string gomId, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var other = await FindMember(db, gomId, ct);
        if (other is null) return Results.NotFound();
        var (low, high) = Friendship.Order(me, other.Value);
        var removed = await db.Friendships.Where(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Accepted).ExecuteDeleteAsync(ct);
        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> ListBlocks(HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        return Results.Ok(await db.Blocks.Where(b => b.BlockerId == me)
            .Join(db.Users, b => b.BlockedId, u => u.Id, (b, u) => new { gomId = u.GomId, displayName = u.DisplayName }).OrderBy(x => x.gomId).ToListAsync(ct));
    }

    private static async Task<IResult> Block(string gomId, HttpContext http, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var other = await FindMember(db, gomId, ct);
        if (other is null) return Results.NotFound();
        if (other == me) return Error(StatusCodes.Status400BadRequest, "cannot_add_self");

        var now = clock.GetUtcNow();
        var (low, high) = Friendship.Order(me, other.Value);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"INSERT INTO blocks (blocker_id, blocked_id, created_at) VALUES ({me}, {other.Value}, {now}) ON CONFLICT DO NOTHING", ct);
        await db.Friendships.Where(f => f.UserLow == low && f.UserHigh == high).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Unblock(string gomId, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var other = await FindMember(db, gomId, ct);
        if (other is null) return Results.NotFound();
        await db.Blocks.Where(b => b.BlockerId == me && b.BlockedId == other).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }
}
