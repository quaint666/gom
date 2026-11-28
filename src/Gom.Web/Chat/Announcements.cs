using System.Security.Claims;
using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>
/// The announcement channel: one conversation of its own kind that everyone who is a member of Gom is in, that only the owner writes in,
/// and that everyone reads. It is made at start (once there is an owner), joined by each person as they become a member, and cannot be
/// left, hidden, deleted for oneself, restyled or given disappearing messages: it is the owner's notice board, the same for everyone.
/// </summary>
public static class Announcements
{
    /// <summary>The error the server answers a person who is not the owner and tries to write in it.</summary>
    public const string OwnerOnly = "owner_only";

    /// <summary>Whether this person may put something in a conversation of this kind: anyone, except in the announcements, where only the owner.</summary>
    public static bool MayPost(ConversationKind kind, ClaimsPrincipal user) =>
        kind != ConversationKind.Announce || user.FindFirst(GomClaims.IsOwner)?.Value == "true";

    /// <summary>
    /// Makes the channel if there is an owner and none yet, and puts in it every member who is not (a person still waiting to be approved
    /// is not a member, nor is an account that was deleted). Safe to run again; a person already in it is left as they are.
    /// </summary>
    public static async Task EnsureAsync(GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var owner = await db.Users.Where(u => u.IsOwner).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct);
        if (owner is null) return;
        var now = clock.GetUtcNow();
        var channel = await db.Conversations.Where(c => c.Kind == ConversationKind.Announce).Select(c => c.Id).SingleOrDefaultAsync(ct);
        if (channel == Guid.Empty)
        {
            channel = Guid.NewGuid();
            db.Conversations.Add(new Conversation { Id = channel, Kind = ConversationKind.Announce, CreatedBy = owner.Value, CreatedAt = now });
            await db.SaveChangesAsync(ct);
        }
        var missing = await db.Users.Where(u => u.RequestedAt == null && u.DeletedAt == null && !db.Members.Any(m => m.ConversationId == channel && m.UserId == u.Id))
            .Select(u => u.Id).ToListAsync(ct);
        if (missing.Count == 0) return;
        var seq = await db.Conversations.Where(c => c.Id == channel).Select(c => c.LastSeq).SingleAsync(ct);
        db.Members.AddRange(missing.Select(id => Member(channel, id, seq, now)));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Puts a person who has just become a member of Gom in the channel (it does nothing where there is none). What was said before is
    /// theirs to read, and none of it counts as unread.
    /// </summary>
    public static async Task JoinAsync(GomDbContext db, Guid person, TimeProvider clock, CancellationToken ct)
    {
        var channel = await db.Conversations.Where(c => c.Kind == ConversationKind.Announce).Select(c => new { c.Id, c.LastSeq }).SingleOrDefaultAsync(ct);
        if (channel is null || await db.Members.AnyAsync(m => m.ConversationId == channel.Id && m.UserId == person, ct)) return;
        db.Members.Add(Member(channel.Id, person, channel.LastSeq, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
    }

    private static ConversationMember Member(Guid channel, Guid person, long seq, DateTimeOffset now) =>
        new() { ConversationId = channel, UserId = person, JoinedAt = now, LastReadSeq = seq, JoinedSeq = 0, Role = GroupRole.Member };
}

/// <summary>Makes the announcement channel at start, after <see cref="Auth.OwnerSeedingService"/> has made the owner.</summary>
public sealed class AnnouncementsSeedingService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AnnouncementsSeedingService> log) : IHostedService
{
    /// <summary>A database that is not ready (not migrated yet) is logged, not fatal: the site still starts, and the next start tries again.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await Announcements.EnsureAsync(scope.ServiceProvider.GetRequiredService<GomDbContext>(), clock, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Could not make the announcement channel; apply the database migrations and start again.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
