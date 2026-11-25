using System.Net;
using System.Text.Json;
using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Gom.Tests;

/// <summary>Deleting your own account: the person goes, what they said stays.</summary>
public class AccountDeletionTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put, Delete = HttpMethod.Delete;

    private Task<HttpResponseMessage> DeleteMe(string cookie, string? gomId) => app.Call(Delete, "/api/me", new { gomId }, cookie);

    private async Task<User> Row(Guid id)
    {
        await using var db = app.Db.NewContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    private async Task<Guid> Group(string cookie, string title, params string[] members)
    {
        using var r = await app.Call(Post, "/api/conversations/group", new { title, members }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> Say(Guid chat, string cookie, string body)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        app.Clock.Advance(TimeSpan.FromSeconds(2));
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private string BlobPath(Guid id) { var name = id.ToString(); return Path.Combine(app.UploadsDir, name[..2], name); }

    [Fact]
    public async Task Without_typing_your_own_Gom_ID_nothing_happens()
    {
        var (a, ca) = await app.Member();
        foreach (var typed in new[] { null, "", "someone_else", a.GomId + "x" })
        {
            using var r = await DeleteMe(ca, typed);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("confirm_mismatch", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        Assert.Null((await Row(a.Id)).DeletedAt);
        using var me = await app.Call(Get, "/api/me", cookie: ca);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task The_owner_cannot_delete_their_own_account()
    {
        var (owner, co) = await app.Owner();
        using var r = await DeleteMe(co, owner.GomId);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("owner_cannot_delete", (await AppFixture.Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_account_is_emptied_and_switched_off_every_sign_in_ends_and_signing_in_again_fails_as_a_wrong_password_does()
    {
        var (a, ca) = await app.Member();
        var other = await app.SignIn(a.GomId);
        using var r = await DeleteMe(ca, "  @" + a.GomId.ToUpperInvariant() + " ");
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Contains("expires=Thu, 01 Jan 1970", AppFixture.SetCookieHeader(r) ?? "", StringComparison.OrdinalIgnoreCase);   // the cookie is cleared

        var row = await Row(a.Id);
        Assert.Equal(app.Clock.Now, row.DeletedAt);
        Assert.Equal(a.Id, row.DeletedBy);
        Assert.NotNull(row.DisabledAt);
        Assert.Equal(AccountDeletion.DeletedName, row.DisplayName);
        Assert.Equal("", row.PasswordHash);
        Assert.Equal(a.GomId, row.GomId);   // kept, so nobody else can take it
        Assert.All(await app.SessionsOf(a.Id), s => Assert.NotNull(s.RevokedAt));

        foreach (var cookie in new[] { ca, other })
        {
            using var me = await app.Call(Get, "/api/me", cookie: cookie);
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        }
        using var again = await app.Call(Post, "/api/session", new { gomId = a.GomId, password = AppFixture.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal("invalid_credentials", (await AppFixture.Json(again)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task What_they_wrote_stays_with_the_new_name_and_the_one_to_one_conversation_stays_closed_to_writing()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        var said = await Say(chat, ca, "nhớ giữ tin này");
        using var react = await app.Call(Put, $"/api/conversations/{chat}/messages/{said}/reaction", new { emoji = "❤️" }, ca);
        (await DeleteMe(ca, a.GomId)).Dispose();

        using var history = await app.Call(Get, $"/api/conversations/{chat}/messages", cookie: cb);
        var m = (await AppFixture.Json(history)).GetProperty("messages").EnumerateArray().Single();
        Assert.Equal("nhớ giữ tin này", m.GetProperty("body").GetString());
        Assert.Equal(AccountDeletion.DeletedName, m.GetProperty("sender").GetProperty("displayName").GetString());
        Assert.Single(m.GetProperty("reactions").EnumerateArray());

        using var list = await app.Call(Get, "/api/conversations", cookie: cb);
        var row = (await AppFixture.Json(list)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chat);
        Assert.Equal(AccountDeletion.DeletedName, row.GetProperty("title").GetString());
        Assert.True(row.GetProperty("peer").GetProperty("deleted").GetBoolean());

        using var send = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "còn đó không?" }, cb);
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
        using var friends = await app.Call(Get, "/api/friends", cookie: cb);
        Assert.Empty((await AppFixture.Json(friends)).GetProperty("friends").EnumerateArray());

        using var card = await app.Call(Get, $"/api/people/{a.Id}", cookie: cb);
        Assert.True((await AppFixture.Json(card)).GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task Requests_either_way_blocks_and_group_requests_bans_and_hides_go()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        (await app.Call(Post, "/api/friends/requests", new { gomId = b.GomId }, ca)).Dispose();
        (await app.Call(Post, "/api/friends/requests", new { gomId = a.GomId }, cc)).Dispose();
        (await app.Call(Post, $"/api/blocks/{a.GomId}", null, cb)).Dispose();
        await app.Befriend(c, b);
        var g = await Group(cc, "Nhóm", b.GomId);
        var said = await Say(g, cc, "tin");
        await using (var setup = app.Db.NewContext())
        {
            setup.JoinRequests.Add(new JoinRequest { ConversationId = g, UserId = a.Id, RequestedBy = b.Id, CreatedAt = app.Clock.Now });
            setup.Bans.Add(new GroupBan { ConversationId = g, UserId = a.Id, BannedBy = c.Id, CreatedAt = app.Clock.Now });
            setup.Hides.Add(new MessageHide { UserId = a.Id, MessageId = said, CreatedAt = app.Clock.Now });
            await setup.SaveChangesAsync();
        }
        (await DeleteMe(ca, a.GomId)).Dispose();
        await using var db = app.Db.NewContext();
        Assert.False(await db.JoinRequests.AnyAsync(j => j.UserId == a.Id));
        Assert.False(await db.Bans.AnyAsync(x => x.UserId == a.Id));
        Assert.False(await db.Hides.AnyAsync(h => h.UserId == a.Id));
        Assert.False(await db.Friendships.AnyAsync(f => f.UserLow == a.Id || f.UserHigh == a.Id));
        Assert.False(await db.Blocks.AnyAsync(x => x.BlockerId == a.Id || x.BlockedId == a.Id));
    }

    [Fact]
    public async Task A_leader_leaves_each_group_with_a_notice_and_the_role_passes_to_a_deputy_first_else_the_longest_standing_member()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member(); var (d, cd) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c); await app.Befriend(a, d);
        // b has been in it longest; c, a deputy, came later: the deputy still comes first.
        var withDeputy = await Group(ca, "Có phó", b.GomId);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        (await app.Call(Post, $"/api/conversations/{withDeputy}/members", new { gomId = c.GomId }, ca)).Dispose();
        (await app.Call(Put, $"/api/conversations/{withDeputy}/members/{c.Id}/role", new { role = "deputy" }, ca)).Dispose();
        var plain = await Group(ca, "Không phó", b.GomId);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        (await app.Call(Post, $"/api/conversations/{plain}/members", new { gomId = d.GomId }, ca)).Dispose();

        using var r = await DeleteMe(ca, a.GomId);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        await using var db = app.Db.NewContext();
        Assert.Equal(c.Id, await db.Members.Where(m => m.ConversationId == withDeputy && m.Role == GroupRole.Leader).Select(m => m.UserId).SingleAsync());
        Assert.Equal(b.Id, await db.Members.Where(m => m.ConversationId == plain && m.Role == GroupRole.Leader).Select(m => m.UserId).SingleAsync());
        Assert.False(await db.Members.AnyAsync(m => m.UserId == a.Id && (m.ConversationId == withDeputy || m.ConversationId == plain)));
        foreach (var g in new[] { withDeputy, plain })
            Assert.True(await db.Messages.AnyAsync(m => m.ConversationId == g && m.Notice == NoticeKind.Left && m.SenderId == a.Id));
    }

    [Fact]
    public async Task A_group_with_nobody_else_left_stays_as_it_was()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var g = await Group(ca, "Hai người", b.GomId);
        await Say(g, ca, "tin cũ");
        (await DeleteMe(cb, b.GomId)).Dispose();
        (await DeleteMe(ca, a.GomId)).Dispose();
        await using var db = app.Db.NewContext();
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == g));
        Assert.True(await db.Messages.AnyAsync(m => m.ConversationId == g && m.Body == "tin cũ"));
        Assert.False(await db.Members.AnyAsync(m => m.ConversationId == g));
    }

    [Fact]
    public async Task The_profile_picture_uploads_not_in_a_message_unused_links_and_devices_go()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        using var pic = await app.CallBytes(Put, "/api/me/avatar", TestImages.Encode(TestImages.Busy(300, 300), SKEncodedImageFormat.Jpeg), ca, "image/jpeg");
        var avatar = (await AppFixture.Json(pic)).GetProperty("avatar").GetGuid();
        using var up = await app.CallBytes(Post, $"/api/conversations/{chat}/attachments?name=a.jpg", TestImages.Jpeg(200, 100), ca);
        var loose = (await AppFixture.Json(up)).GetProperty("id").GetGuid();
        Assert.True(File.Exists(BlobPath(avatar)));
        Assert.True(File.Exists(BlobPath(loose)));
        var (_, co) = await app.Owner();
        (await app.Call(Post, $"/api/users/{a.GomId}/reset-link", null, co)).Dispose();
        await using (var db = app.Db.NewContext())
        {
            var session = (await app.SessionsOf(a.Id)).First().Id;
            db.PushSubscriptions.Add(new PushSubscription { Id = Guid.NewGuid(), SessionId = session, Endpoint = "https://fcm.googleapis.com/fcm/send/x", P256dh = new byte[65], Auth = new byte[16], CreatedAt = app.Clock.Now });
            await db.SaveChangesAsync();
        }

        (await DeleteMe(ca, a.GomId)).Dispose();

        Assert.False(File.Exists(BlobPath(avatar)));
        Assert.False(File.Exists(BlobPath(loose)));
        await using var after = app.Db.NewContext();
        Assert.Null((await Row(a.Id)).AvatarId);
        Assert.False(await after.Attachments.AnyAsync(x => x.Id == loose));
        Assert.False(await after.Links.AnyAsync(l => l.TargetUserId == a.Id && l.RevokedAt == null));
        Assert.False(await after.PushSubscriptions.AnyAsync(p => after.Sessions.Any(s => s.Id == p.SessionId && s.UserId == a.Id)));
    }

    [Fact]
    public async Task The_owner_sees_the_account_as_deleted_and_cannot_switch_it_back_on()
    {
        var (a, ca) = await app.Member();
        (await DeleteMe(ca, a.GomId)).Dispose();
        var (_, co) = await app.Owner();
        using var users = await app.Call(Get, "/api/users", cookie: co);
        var row = (await AppFixture.Json(users)).EnumerateArray().Single(u => u.GetProperty("gomId").GetString() == a.GomId);
        Assert.True(row.GetProperty("deleted").GetBoolean());
        using var on = await app.Call(Post, $"/api/users/{a.GomId}/enable", null, co);
        Assert.Equal(HttpStatusCode.Conflict, on.StatusCode);
        Assert.NotNull((await Row(a.Id)).DisabledAt);
    }

    [Fact]
    public async Task The_database_refuses_a_deleted_account_that_is_not_switched_off()
    {
        var (a, ca) = await app.Member();
        (await DeleteMe(ca, a.GomId)).Dispose();
        await using var db = app.Db.NewContext();
        var e = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Users.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisabledAt, (DateTimeOffset?)null)));
        Assert.Equal("ck_users_deleted", e.ConstraintName);
    }
}
