using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gom.Web.Social;

/// <summary>
/// Groups: make one from your friends, add more of your friends, leave, see who is in it, ask another member to be friends, and the
/// leader's and deputies' powers: remove a member, name deputies, hand leadership over, disband.
/// </summary>
public static class GroupEndpoints
{
    public sealed record CreateRequest(string? Title, string[]? Members);
    public sealed record AddRequest(string? GomId);
    public sealed record RoleRequest(string? Role);
    public sealed record LeaderRequest(Guid? UserId);
    public sealed record RenameRequest(string? Title);
    public sealed record SettingsRequest(bool? JoinApproval);

    public static void MapGroups(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/conversations").RequireAuthorization();
        api.MapPost("/group", Create);
        api.MapGet("/{id:guid}/members", ListMembers);
        api.MapPost("/{id:guid}/members", AddMember);
        api.MapDelete("/{id:guid}/members/me", Leave);
        api.MapDelete("/{id:guid}/members/{userId:guid}", Remove);
        api.MapPut("/{id:guid}/members/{userId:guid}/role", SetRole);
        api.MapPost("/{id:guid}/leader", HandOver);
        api.MapDelete("/{id:guid}", Disband);
        api.MapPatch("/{id:guid}", Rename);
        api.MapPut("/{id:guid}/settings", SetSettings);
        api.MapGet("/{id:guid}/requests", ListRequests);
        api.MapPost("/{id:guid}/requests/{userId:guid}/approve", Approve);
        api.MapDelete("/{id:guid}/requests/{userId:guid}", Decline);
        api.MapGet("/{id:guid}/bans", ListBans);
        api.MapDelete("/{id:guid}/bans/{userId:guid}", Unban);
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
        foreach (var person in people.Append(me))
            db.Members.Add(new ConversationMember { ConversationId = chat.Id, UserId = person, JoinedAt = now, Role = person == me ? GroupRole.Leader : GroupRole.Member });
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
        await LockGroup(db, id, ct);
        // Read again under the lock: the role (and the setting) may have changed while this request was waiting.
        var role = await db.Members.Where(m => m.ConversationId == id && m.UserId == me).Select(m => (GroupRole?)m.Role).SingleOrDefaultAsync(ct);
        if (role is null) return Results.NotFound();
        if (await db.Members.AnyAsync(m => m.ConversationId == id && m.UserId == other, ct)) return Error(StatusCodes.Status409Conflict, "already_member");
        if (await db.Bans.AnyAsync(x => x.ConversationId == id && x.UserId == other, ct)) return Error(StatusCodes.Status403Forbidden, "banned");

        // A group that asks for approval takes a plain member's addition as a request for the leader or a deputy to answer.
        if (role == GroupRole.Member && await db.Conversations.Where(c => c.Id == id).Select(c => c.JoinApproval).SingleAsync(ct))
        {
            if (await db.JoinRequests.AnyAsync(x => x.ConversationId == id && x.UserId == other, ct)) return Error(StatusCodes.Status409Conflict, "already_requested");
            db.JoinRequests.Add(new JoinRequest { ConversationId = id, UserId = other.Value, RequestedBy = me, CreatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            live.ConversationsChanged(await db.Members.Where(m => m.ConversationId == id && m.Role != GroupRole.Member).Select(m => m.UserId).ToListAsync(ct));
            return Results.Json(new { status = "pending" }, statusCode: StatusCodes.Status202Accepted);
        }

        var now = clock.GetUtcNow();
        var problem = await AdmitAsync(db, id, other.Value, now, options.Value.MaxGroupMembers, ct);
        if (problem is not null) return problem;
        var notice = await Notices.Write(db, id, me, NoticeKind.Added, other.Value, "", now, ct);
        await tx.CommitAsync(ct);
        live.ConversationsChanged(await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct));
        await Notices.Publish(db, live, notice, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Puts a person into the group: they see only what comes after. Call it inside the transaction, with the group locked.
    /// Returns the refusal, or null when they are in. A request they had waiting is gone.
    /// </summary>
    private static async Task<IResult?> AdmitAsync(GomDbContext db, Guid id, Guid person, DateTimeOffset now, int maxMembers, CancellationToken ct)
    {
        if (await db.Members.CountAsync(m => m.ConversationId == id, ct) >= maxMembers) return Error(StatusCodes.Status409Conflict, "group_full");
        var seq = await db.Conversations.Where(c => c.Id == id).Select(c => c.LastSeq).SingleAsync(ct);
        db.Members.Add(new ConversationMember { ConversationId = id, UserId = person, JoinedAt = now, JoinedSeq = seq, LastReadSeq = seq });
        await db.SaveChangesAsync(ct);
        await db.JoinRequests.Where(x => x.ConversationId == id && x.UserId == person).ExecuteDeleteAsync(ct);
        return null;
    }

    private static async Task<IResult> Leave(Guid id, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live, IBlobStore blobs, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockGroup(db, id, ct);
        var everyone = await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct);
        if (!everyone.Contains(me)) return Results.NotFound();
        if (access.Role == GroupRole.Leader)
        {
            // The leader hands over first. Alone in the group, there is nobody to hand over to: leaving ends it.
            if (everyone.Count > 1) return Error(StatusCodes.Status409Conflict, "leader_must_hand_over");
            var files = await DeleteGroup(db, id, ct);
            await tx.CommitAsync(ct);
            AttachmentFiles.Forget(blobs, files);
            live.ConversationsChanged(everyone);
            return Results.NoContent();
        }

        await db.Members.Where(m => m.ConversationId == id && m.UserId == me).ExecuteDeleteAsync(ct);
        var notice = await Notices.Write(db, id, me, NoticeKind.Left, null, "", clock.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
        live.ConversationsChanged(everyone);
        await Notices.Publish(db, live, notice, ct);
        return Results.NoContent();
    }

    /// <summary>The same lock a send and an add take, so who is in the group and who leads it cannot change under a decision.</summary>
    private static Task<int> LockGroup(GomDbContext db, Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT 1 FROM conversations WHERE id = {id} FOR UPDATE", ct);

    /// <summary>Takes a group and everything in it out of the database. Every key is Restrict, so this goes from the leaves inward.</summary>
    private static async Task<List<Guid>> DeleteGroup(GomDbContext db, Guid id, CancellationToken ct)
    {
        // The uploads of the conversation, used or not; their files are removed after the commit.
        var files = await db.Attachments.Where(a => a.ConversationId == id).Select(a => a.Id).ToListAsync(ct);
        await db.Attachments.Where(a => a.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.JoinRequests.Where(x => x.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.Bans.Where(x => x.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.Reactions.Where(r => db.Messages.Any(m => m.Id == r.MessageId && m.ConversationId == id)).ExecuteDeleteAsync(ct);
        await db.Pins.Where(p => p.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.Hides.Where(h => db.Messages.Any(m => m.Id == h.MessageId && m.ConversationId == id)).ExecuteDeleteAsync(ct);
        await db.Previews.Where(p => db.Messages.Any(m => m.Id == p.MessageId && m.ConversationId == id)).ExecuteDeleteAsync(ct);
        await db.Events.Where(e => e.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.Messages.Where(m => m.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.Members.Where(m => m.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.Conversations.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        return files;
    }

    /// <summary>A leader or a deputy takes a member out. A deputy cannot take out the leader or another deputy. The person is gone as if they had left.</summary>
    private static async Task<IResult> Remove(Guid id, Guid userId, bool? ban, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        if (userId == me) return Error(StatusCodes.Status400BadRequest, "use_leave");
        if (access.Role == GroupRole.Member) return Error(StatusCodes.Status403Forbidden, "not_allowed");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockGroup(db, id, ct);
        // Read again under the lock: the roles may have changed while this request was waiting.
        var mine = await db.Members.Where(m => m.ConversationId == id && m.UserId == me).Select(m => (GroupRole?)m.Role).SingleOrDefaultAsync(ct);
        var theirs = await db.Members.Where(m => m.ConversationId == id && m.UserId == userId).Select(m => (GroupRole?)m.Role).SingleOrDefaultAsync(ct);
        if (mine is null) return Results.NotFound();
        if (theirs is null) return Results.NotFound();
        if (mine == GroupRole.Member || (mine == GroupRole.Deputy && theirs != GroupRole.Member)) return Error(StatusCodes.Status403Forbidden, "not_allowed");

        var everyone = await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct);
        await db.Members.Where(m => m.ConversationId == id && m.UserId == userId).ExecuteDeleteAsync(ct);
        // "And do not let them back": nobody can add them until the leader lifts it.
        if (ban == true) await db.Database.ExecuteSqlAsync($"INSERT INTO group_bans (conversation_id, user_id, banned_by, created_at) VALUES ({id}, {userId}, {me}, {clock.GetUtcNow()}) ON CONFLICT DO NOTHING", ct);
        var notice = await Notices.Write(db, id, me, NoticeKind.Removed, userId, "", clock.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
        live.ConversationsChanged(everyone);
        await Notices.Publish(db, live, notice, ct);
        return Results.NoContent();
    }

    /// <summary>The leader names a deputy, or takes the title back.</summary>
    private static async Task<IResult> SetRole(Guid id, Guid userId, RoleRequest request, HttpContext http, GomDbContext db, LiveDelivery live, CancellationToken ct)
    {
        var role = request.Role switch { "deputy" => GroupRole.Deputy, "member" => GroupRole.Member, _ => (GroupRole?)null };
        if (role is null) return Error(StatusCodes.Status400BadRequest, "invalid_role");
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        if (access.Role != GroupRole.Leader) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        if (userId == me) return Error(StatusCodes.Status400BadRequest, "invalid_role");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockGroup(db, id, ct);
        var changed = await db.Members.Where(m => m.ConversationId == id && m.UserId == userId && m.Role != GroupRole.Leader)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, role.Value), ct);
        if (changed == 0) return Results.NotFound();
        await tx.CommitAsync(ct);
        live.ConversationsChanged(await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct));
        return Results.NoContent();
    }

    /// <summary>The leader makes another member the leader, and becomes a plain member.</summary>
    private static async Task<IResult> HandOver(Guid id, LeaderRequest request, HttpContext http, GomDbContext db, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        if (access.Role != GroupRole.Leader) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        if (request.UserId is not { } next || next == me) return Error(StatusCodes.Status400BadRequest, "invalid_member");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockGroup(db, id, ct);
        if (!await db.Members.AnyAsync(m => m.ConversationId == id && m.UserId == me && m.Role == GroupRole.Leader, ct)) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        if (!await db.Members.AnyAsync(m => m.ConversationId == id && m.UserId == next, ct)) return Results.NotFound();
        // Two steps, in this order: there is never a moment with two leaders (the index would refuse it).
        await db.Members.Where(m => m.ConversationId == id && m.UserId == me).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, GroupRole.Member), ct);
        await db.Members.Where(m => m.ConversationId == id && m.UserId == next).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, GroupRole.Leader), ct);
        await tx.CommitAsync(ct);
        live.ConversationsChanged(await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct));
        return Results.NoContent();
    }

    /// <summary>The leader or a deputy gives the group a new name. Everyone in it is told to reload their list.</summary>
    private static async Task<IResult> Rename(Guid id, RenameRequest request, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        if (access.Role == GroupRole.Member) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        var title = request.Title is null ? null : DisplayName.Normalize(request.Title, "");
        if (string.IsNullOrEmpty(title)) return Error(StatusCodes.Status400BadRequest, "invalid_title");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockGroup(db, id, ct);
        // Read again under the lock: the person may have been demoted or removed while this request waited.
        var role = await db.Members.Where(m => m.ConversationId == id && m.UserId == me).Select(m => (GroupRole?)m.Role).SingleOrDefaultAsync(ct);
        if (role is null) return Results.NotFound();
        if (role == GroupRole.Member) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        // Naming it what it is already called changes nothing, and says nothing.
        var before = await db.Conversations.Where(c => c.Id == id).Select(c => c.Title).SingleAsync(ct);
        await db.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, title), ct);
        var notice = before == title ? null : await Notices.Write(db, id, me, NoticeKind.Renamed, null, title, clock.GetUtcNow(), ct);
        var everyone = await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct);
        await tx.CommitAsync(ct);
        live.ConversationsChanged(everyone);
        if (notice is not null) await Notices.Publish(db, live, notice, ct);
        return Results.NoContent();
    }

    /// <summary>The leader turns "adding people needs approval" on or off.</summary>
    private static async Task<IResult> SetSettings(Guid id, SettingsRequest request, HttpContext http, GomDbContext db, LiveDelivery live, CancellationToken ct)
    {
        if (request.JoinApproval is not { } on) return Error(StatusCodes.Status400BadRequest, "invalid_setting");
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        if (access.Role != GroupRole.Leader) return Error(StatusCodes.Status403Forbidden, "not_allowed");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockGroup(db, id, ct);
        if (!await db.Members.AnyAsync(m => m.ConversationId == id && m.UserId == me && m.Role == GroupRole.Leader, ct)) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        await db.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.JoinApproval, on), ct);
        // Turning it off: what is waiting is no longer waiting for anyone to say yes, so it is dropped (the people can be added directly).
        if (!on) await db.JoinRequests.Where(x => x.ConversationId == id).ExecuteDeleteAsync(ct);
        var everyone = await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct);
        await tx.CommitAsync(ct);
        live.ConversationsChanged(everyone);
        return Results.NoContent();
    }

    /// <summary>What waits for an answer: who is to be added and who asked. For the leader and the deputies; the people to add are shown by name, not by Gom ID.</summary>
    private static async Task<IResult> ListRequests(Guid id, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        if (access.Role == GroupRole.Member) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        var rows = await (from r in db.JoinRequests.AsNoTracking() where r.ConversationId == id
                          join u in db.Users on r.UserId equals u.Id
                          join asker in db.Users on r.RequestedBy equals asker.Id
                          orderby r.CreatedAt
                          select new { Id = u.Id, u.DisplayName, By = new { asker.Id, asker.DisplayName }, r.CreatedAt }).ToListAsync(ct);
        return Results.Ok(new { requests = rows });
    }

    /// <summary>The leader or a deputy says yes: the person is in, seeing only what comes after.</summary>
    private static async Task<IResult> Approve(Guid id, Guid userId, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live, IOptions<ChatOptions> options, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        if (access.Role == GroupRole.Member) return Error(StatusCodes.Status403Forbidden, "not_allowed");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockGroup(db, id, ct);
        var role = await db.Members.Where(m => m.ConversationId == id && m.UserId == me).Select(m => (GroupRole?)m.Role).SingleOrDefaultAsync(ct);
        if (role is null) return Results.NotFound();
        if (role == GroupRole.Member) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        if (!await db.JoinRequests.AnyAsync(x => x.ConversationId == id && x.UserId == userId, ct)) return Results.NotFound();
        if (await db.Bans.AnyAsync(x => x.ConversationId == id && x.UserId == userId, ct)) return Error(StatusCodes.Status403Forbidden, "banned");
        var now = clock.GetUtcNow();
        var problem = await AdmitAsync(db, id, userId, now, options.Value.MaxGroupMembers, ct);
        if (problem is not null) return problem;
        var notice = await Notices.Write(db, id, me, NoticeKind.Added, userId, "", now, ct);
        await tx.CommitAsync(ct);
        live.ConversationsChanged(await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct));
        await Notices.Publish(db, live, notice, ct);
        return Results.NoContent();
    }

    /// <summary>The leader or a deputy says no, or the member who asked takes the request back.</summary>
    private static async Task<IResult> Decline(Guid id, Guid userId, HttpContext http, GomDbContext db, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        var request = await db.JoinRequests.AsNoTracking().Where(x => x.ConversationId == id && x.UserId == userId).SingleOrDefaultAsync(ct);
        if (request is null) return Results.NotFound();
        if (access.Role == GroupRole.Member && request.RequestedBy != me) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        await db.JoinRequests.Where(x => x.ConversationId == id && x.UserId == userId).ExecuteDeleteAsync(ct);
        live.ConversationsChanged(await db.Members.Where(m => m.ConversationId == id && (m.Role != GroupRole.Member || m.UserId == request.RequestedBy)).Select(m => m.UserId).ToListAsync(ct));
        return Results.NoContent();
    }

    /// <summary>Who may not be added back. For the leader and the deputies.</summary>
    private static async Task<IResult> ListBans(Guid id, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        if (access.Role == GroupRole.Member) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        var rows = await (from x in db.Bans.AsNoTracking() where x.ConversationId == id
                          join u in db.Users on x.UserId equals u.Id
                          orderby x.CreatedAt
                          select new { u.Id, u.DisplayName, x.CreatedAt }).ToListAsync(ct);
        return Results.Ok(new { bans = rows });
    }

    /// <summary>The leader lets someone be added again.</summary>
    private static async Task<IResult> Unban(Guid id, Guid userId, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        if (access.Role != GroupRole.Leader) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        await db.Bans.Where(x => x.ConversationId == id && x.UserId == userId).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    /// <summary>The leader ends the group: its messages go for everyone.</summary>
    private static async Task<IResult> Disband(Guid id, HttpContext http, GomDbContext db, LiveDelivery live, IBlobStore blobs, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        if (access.Role != GroupRole.Leader) return Error(StatusCodes.Status403Forbidden, "not_allowed");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockGroup(db, id, ct);
        if (!await db.Members.AnyAsync(m => m.ConversationId == id && m.UserId == me && m.Role == GroupRole.Leader, ct)) return Error(StatusCodes.Status403Forbidden, "not_allowed");
        var everyone = await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct);
        var files = await DeleteGroup(db, id, ct);
        await tx.CommitAsync(ct);
        AttachmentFiles.Forget(blobs, files);
        live.ConversationsChanged(everyone);
        return Results.NoContent();
    }

    private static async Task<IResult> ListMembers(Guid id, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await Access.Of(db, id, me, ct) is null) return Results.NotFound();

        var members = await (from m in db.Members where m.ConversationId == id join u in db.Users on m.UserId equals u.Id orderby u.DisplayName, u.Id
                             select new { u.Id, u.GomId, u.DisplayName, m.Role }).ToListAsync(ct);
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
                role = m.Role.ToString().ToLowerInvariant(),
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
