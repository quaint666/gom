using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gom.Web.Social;

/// <summary>Groups: make one from your friends, add more of your friends, leave, see who is in it, and ask another member to be friends.</summary>
public static class GroupEndpoints
{
    public sealed record CreateRequest(string? Title, string[]? Members);
    public sealed record AddRequest(string? GomId);

    public static void MapGroups(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/conversations").RequireAuthorization();
        api.MapPost("/group", Create);
        api.MapGet("/{id:guid}/members", ListMembers);
        api.MapPost("/{id:guid}/members", AddMember);
        api.MapDelete("/{id:guid}/members/me", Leave);
        api.MapPost("/{id:guid}/members/{userId:guid}/friend-request", AskMember);
    }

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    /// <summary>Is this person one of my accepted friends?</summary>
    private static Task<bool> IsFriend(GomDbContext db, Guid me, Guid other, CancellationToken ct)
    {
        var (low, high) = Friendship.Order(me, other);
        return db.Friendships.AnyAsync(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Accepted, ct);
    }

    private static async Task<IResult> Create(CreateRequest request, HttpContext http, GomDbContext db, TimeProvider clock,
        LiveDelivery live, IOptions<ChatOptions> options, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var title = request.Title is null ? null : DisplayName.Normalize(request.Title, "");
        if (string.IsNullOrEmpty(title)) return Error(StatusCodes.Status400BadRequest, "invalid_title");

        // Who is meant: each ID once, in any case, never yourself.
        var wanted = (request.Members ?? []).Select(g => (g ?? "").Trim().ToLowerInvariant()).Where(g => g != "" && g != http.User.FindFirst(GomClaims.GomId)!.Value).Distinct().ToList();
        if (wanted.Count == 0) return Error(StatusCodes.Status400BadRequest, "no_members");
        if (wanted.Count + 1 > options.Value.MaxGroupMembers) return Error(StatusCodes.Status400BadRequest, "group_too_big");

        var people = await db.Users.Where(u => wanted.Contains(u.GomId) && u.DisabledAt == null).Select(u => u.Id).ToListAsync(ct);
        // A stranger, an ID that does not exist and a locked member all get the same answer.
        if (people.Count != wanted.Count) return Error(StatusCodes.Status403Forbidden, "not_friends");
        foreach (var person in people)
            if (!await IsFriend(db, me, person, ct)) return Error(StatusCodes.Status403Forbidden, "not_friends");

        var now = clock.GetUtcNow();
        var chat = new Conversation { Id = Guid.NewGuid(), Kind = ConversationKind.Group, Title = title, CreatedBy = me, CreatedAt = now };
        db.Conversations.Add(chat);
        foreach (var person in people.Append(me)) db.Members.Add(new ConversationMember { ConversationId = chat.Id, UserId = person, JoinedAt = now });
        await db.SaveChangesAsync(ct);

        live.ConversationsChanged(people.Append(me));
        return Results.Ok(new { id = chat.Id });
    }

    private static async Task<IResult> AddMember(Guid id, AddRequest request, HttpContext http, GomDbContext db, TimeProvider clock,
        LiveDelivery live, IOptions<ChatOptions> options, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");

        var other = await FriendEndpoints.FindMember(db, request.GomId, ct);
        if (other is null || !await IsFriend(db, me, other.Value, ct)) return Error(StatusCodes.Status403Forbidden, "not_friends");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Lock the conversation row: the same lock a send takes, so "how many" and "what number we are at" cannot move under us.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM conversations WHERE id = {id} FOR UPDATE", ct);
        if (await db.Members.AnyAsync(m => m.ConversationId == id && m.UserId == other, ct)) return Error(StatusCodes.Status409Conflict, "already_member");
        if (await db.Members.CountAsync(m => m.ConversationId == id, ct) >= options.Value.MaxGroupMembers) return Error(StatusCodes.Status409Conflict, "group_full");

        var seq = await db.Conversations.Where(c => c.Id == id).Select(c => c.LastSeq).SingleAsync(ct);
        db.Members.Add(new ConversationMember { ConversationId = id, UserId = other.Value, JoinedAt = clock.GetUtcNow(), JoinedSeq = seq, LastReadSeq = seq });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        live.ConversationsChanged(await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct));
        return Results.NoContent();
    }

    private static async Task<IResult> Leave(Guid id, HttpContext http, GomDbContext db, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");

        await db.Members.Where(m => m.ConversationId == id && m.UserId == me).ExecuteDeleteAsync(ct);
        live.ConversationsChanged((await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct)).Append(me));
        return Results.NoContent();
    }

    private static async Task<IResult> ListMembers(Guid id, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await Access.Of(db, id, me, ct) is null) return Results.NotFound();

        var members = await (from m in db.Members where m.ConversationId == id join u in db.Users on m.UserId equals u.Id orderby u.DisplayName, u.Id
                             select new { u.Id, u.GomId, u.DisplayName }).ToListAsync(ct);
        var mine = await db.Friendships.Where(f => f.UserLow == me || f.UserHigh == me).ToListAsync(ct);

        return Results.Ok(members.Select(m =>
        {
            var row = m.Id == me ? null : mine.SingleOrDefault(f => f.UserLow == m.Id || f.UserHigh == m.Id);
            var isFriend = row?.Status == FriendStatus.Accepted;
            return new
            {
                id = m.Id,
                displayName = m.DisplayName,
                isMe = m.Id == me,
                isFriend,
                // A Gom ID is shown to the person themself and to their friends, and to nobody else.
                gomId = m.Id == me || isFriend ? m.GomId : null,
                request = row is { Status: FriendStatus.Pending } ? (row.RequestedBy == me ? "sent" : "received") : "none",
            };
        }));
    }

    private static async Task<IResult> AskMember(Guid id, Guid userId, HttpContext http, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await Access.Of(db, id, me, ct) is null) return Results.NotFound();
        // Only someone in this very group can be asked this way: an ID alone is not a way to find people.
        var inGroup = await (from m in db.Members where m.ConversationId == id && m.UserId == userId
                             join u in db.Users on m.UserId equals u.Id where u.DisabledAt == null select m.UserId).AnyAsync(ct);
        if (!inGroup) return Results.NotFound();
        return await FriendEndpoints.AskAsync(db, clock, me, userId, ct);
    }
}
