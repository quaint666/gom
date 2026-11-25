using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Auth;

/// <summary>
/// Deleting your own account, as Zalo does it: the person is gone, what they said stays. The row is kept, emptied (a fixed name, no
/// password, no picture) and switched off, so the messages, pictures and reactions in other people's conversations still have a
/// sender and the Gom ID is never given to anyone else. Friendships, group places (with a notice; a leader's role passes on),
/// waiting requests, unused links, uploads not in a message, devices and sign-ins go. Confirmed by typing your own Gom ID.
/// </summary>
public static class AccountDeletion
{
    /// <summary>The name a deleted account shows everywhere it still appears.</summary>
    public const string DeletedName = "Tài khoản đã xóa";

    public sealed record DeleteRequest(string? GomId);

    private static IResult Error(string code) => Results.BadRequest(new { error = code });

    internal static async Task<IResult> Delete([FromBody] DeleteRequest request, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live,
        ConnectionRegistry sockets, IBlobStore blobs, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var gomId = http.User.FindFirst(GomClaims.GomId)!.Value;
        // The owner holds the invites and the switches; Gom without one is not run by anybody.
        if (http.User.FindFirst(GomClaims.IsOwner)!.Value == "true") return Error("owner_cannot_delete");
        var typed = (request.GomId ?? "").Trim().TrimStart('@').ToLowerInvariant();
        if (typed != gomId) return Error("confirm_mismatch");

        var now = clock.GetUtcNow();
        List<Guid> files;
        Guid? picture;
        var notices = new List<Message>();
        HashSet<Guid> told;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            picture = await db.Users.Where(u => u.Id == me).Select(u => u.AvatarId).SingleAsync(ct);
            // First, and only once: of two deletions at once, the second finds nothing to do.
            var marked = await db.Users.Where(u => u.Id == me && u.DeletedAt == null).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.DeletedAt, (DateTimeOffset?)now)
                .SetProperty(u => u.DeletedBy, (Guid?)me)
                .SetProperty(u => u.DisabledAt, u => u.DisabledAt ?? (DateTimeOffset?)now)
                .SetProperty(u => u.DisplayName, DeletedName)
                .SetProperty(u => u.PasswordHash, "")
                .SetProperty(u => u.AvatarId, (Guid?)null), ct);
            if (marked == 0) return Results.NoContent();

            // Everyone whose list or friends change: whoever shares a conversation with them, and whoever is or was asked to be a friend.
            told = (await (from mine in db.Members where mine.UserId == me
                           join other in db.Members on mine.ConversationId equals other.ConversationId
                           where other.UserId != me select other.UserId).Distinct().ToListAsync(ct)).ToHashSet();
            told.UnionWith(await db.Friendships.Where(f => f.UserLow == me || f.UserHigh == me)
                .Select(f => f.UserLow == me ? f.UserHigh : f.UserLow).ToListAsync(ct));

            // The groups, locked in one order (as two deletions may share some): leave each, with a notice; a leader's role passes to
            // the longest-standing deputy, else the longest-standing member. A group nobody is left in stays as it was. One-to-one
            // conversations are kept: the other person still has them, with no way to write (the friendship is gone).
            var groups = await (from m in db.Members where m.UserId == me
                                join c in db.Conversations on m.ConversationId equals c.Id
                                where c.Kind == ConversationKind.Group orderby c.Id select c.Id).ToListAsync(ct);
            if (groups.Count > 0) await db.Database.ExecuteSqlAsync($"SELECT 1 FROM conversations WHERE id = ANY({groups.ToArray()}) ORDER BY id FOR UPDATE", ct);
            foreach (var group in groups)
            {
                var members = await db.Members.Where(m => m.ConversationId == group).Select(m => new { m.UserId, m.Role, m.JoinedAt }).ToListAsync(ct);
                var others = members.Where(m => m.UserId != me).OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId).ToList();
                await db.Members.Where(m => m.ConversationId == group && m.UserId == me).ExecuteDeleteAsync(ct);
                if (others.Count == 0) continue;
                if (members.Single(m => m.UserId == me).Role == GroupRole.Leader)
                {
                    var next = others.FirstOrDefault(m => m.Role == GroupRole.Deputy) ?? others[0];
                    await db.Members.Where(m => m.ConversationId == group && m.UserId == next.UserId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, GroupRole.Leader), ct);
                }
                notices.Add(await Notices.Write(db, group, me, NoticeKind.Left, null, "", now, ct));
            }

            await db.Friendships.Where(f => f.UserLow == me || f.UserHigh == me).ExecuteDeleteAsync(ct);
            await db.Blocks.Where(b => b.BlockerId == me || b.BlockedId == me).ExecuteDeleteAsync(ct);
            await db.JoinRequests.Where(j => j.UserId == me || j.RequestedBy == me).ExecuteDeleteAsync(ct);
            await db.Bans.Where(b => b.UserId == me).ExecuteDeleteAsync(ct);
            await db.Hides.Where(h => h.UserId == me).ExecuteDeleteAsync(ct);
            // Uploads that never went into a message; the files go after the commit.
            files = await db.Attachments.Where(a => a.UploaderId == me && a.MessageId == null).Select(a => a.Id).ToListAsync(ct);
            await db.Attachments.Where(a => a.UploaderId == me && a.MessageId == null).ExecuteDeleteAsync(ct);
            await db.Links.Where(l => (l.CreatedBy == me || l.TargetUserId == me) && l.ConsumedAt == null && l.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.RevokedAt, (DateTimeOffset?)now), ct);
            await db.PushSubscriptions.Where(p => db.Sessions.Any(s => s.Id == p.SessionId && s.UserId == me)).ExecuteDeleteAsync(ct);
            await db.Sessions.Where(s => s.UserId == me && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
            await tx.CommitAsync(ct);
        }

        AttachmentFiles.Forget(blobs, files);
        if (picture is { } old) blobs.Delete(old.ToString());
        sockets.CloseUser(me);
        live.ConversationsChanged(told);
        foreach (var notice in notices) await Notices.Publish(db, live, notice, ct);
        SessionCookie.Clear(http.Response);
        return Results.NoContent();
    }
}
