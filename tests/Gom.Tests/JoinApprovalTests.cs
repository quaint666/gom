using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>A group that asks for approval before someone is added, and a ban from coming back.</summary>
public class JoinApprovalTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put, Delete = HttpMethod.Delete;

    /// <summary>A leads a group with B (plain member) and C (deputy). D, E and F are friends of B (and of A) outside the group.</summary>
    private sealed record Team(User A, string CA, User B, string CB, User C, string CC, User D, string CD, User E, string CE, User F, string CF, Guid Chat);

    private async Task<Team> NewTeam(bool approval = true)
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        var (d, cd) = await app.Member(); var (e, ce) = await app.Member(); var (f, cf) = await app.Member();
        foreach (var x in new[] { b, c, d, e, f }) await app.Befriend(a, x);
        foreach (var x in new[] { d, e, f }) await app.Befriend(b, x);
        foreach (var x in new[] { d, e, f }) await app.Befriend(c, x);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId, c.GomId } }, ca);
        var chat = (await AppFixture.Json(r)).GetProperty("id").GetGuid();
        (await app.Call(Put, $"/api/conversations/{chat}/members/{c.Id}/role", new { role = "deputy" }, ca)).Dispose();
        var t = new Team(a, ca, b, cb, c, cc, d, cd, e, ce, f, cf, chat);
        if (approval) await Approval(t, true);
        return t;
    }

    private async Task Approval(Team t, bool on)
    {
        using var r = await app.Call(Put, $"/api/conversations/{t.Chat}/settings", new { joinApproval = on }, t.CA);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    private Task<HttpResponseMessage> Add(Team t, string cookie, User who) => app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = who.GomId }, cookie);
    private Task<HttpResponseMessage> Requests(Team t, string cookie) => app.Call(Get, $"/api/conversations/{t.Chat}/requests", cookie: cookie);
    private Task<HttpResponseMessage> Approve(Team t, string cookie, User who) => app.Call(Post, $"/api/conversations/{t.Chat}/requests/{who.Id}/approve", cookie: cookie);
    private Task<HttpResponseMessage> Decline(Team t, string cookie, User who) => app.Call(Delete, $"/api/conversations/{t.Chat}/requests/{who.Id}", cookie: cookie);
    private Task<HttpResponseMessage> Remove(Team t, string cookie, User who, bool ban) => app.Call(Delete, $"/api/conversations/{t.Chat}/members/{who.Id}" + (ban ? "?ban=true" : ""), cookie: cookie);
    private Task<HttpResponseMessage> Bans(Team t, string cookie) => app.Call(Get, $"/api/conversations/{t.Chat}/bans", cookie: cookie);
    private Task<HttpResponseMessage> Unban(Team t, string cookie, User who) => app.Call(Delete, $"/api/conversations/{t.Chat}/bans/{who.Id}", cookie: cookie);

    private async Task<bool> IsMember(Team t, User who)
    {
        await using var db = app.Db.NewContext();
        return await db.Members.AnyAsync(m => m.ConversationId == t.Chat && m.UserId == who.Id);
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString();

    // ---- the setting

    [Fact]
    public async Task The_setting_starts_off_and_only_the_leader_changes_it()
    {
        var t = await NewTeam(approval: false);
        using var list = await app.Call(Get, "/api/conversations", cookie: t.CA);
        Assert.False((await AppFixture.Json(list)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == t.Chat).GetProperty("joinApproval").GetBoolean());
        using var byDeputy = await app.Call(Put, $"/api/conversations/{t.Chat}/settings", new { joinApproval = true }, t.CC);
        using var byMember = await app.Call(Put, $"/api/conversations/{t.Chat}/settings", new { joinApproval = true }, t.CB);
        using var empty = await app.Call(Put, $"/api/conversations/{t.Chat}/settings", new { }, t.CA);
        Assert.Equal(HttpStatusCode.Forbidden, byDeputy.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        await Approval(t, true);
        using var after = await app.Call(Get, "/api/conversations", cookie: t.CB);
        Assert.True((await AppFixture.Json(after)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == t.Chat).GetProperty("joinApproval").GetBoolean());
    }

    [Fact]
    public async Task With_approval_off_a_member_adds_a_friend_at_once()
    {
        var t = await NewTeam(approval: false);
        using var r = await Add(t, t.CB, t.D);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.True(await IsMember(t, t.D));
    }

    // ---- asking

    [Fact]
    public async Task With_approval_on_a_plain_member_only_asks_and_the_leader_and_deputies_add_at_once()
    {
        var t = await NewTeam();
        using var ask = await Add(t, t.CB, t.D);
        Assert.Equal(HttpStatusCode.Accepted, ask.StatusCode);
        Assert.Equal("pending", (await AppFixture.Json(ask)).GetProperty("status").GetString());
        Assert.False(await IsMember(t, t.D));
        using var byLeader = await Add(t, t.CA, t.E);
        using var byDeputy = await Add(t, t.CC, t.F);
        Assert.Equal(HttpStatusCode.NoContent, byLeader.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, byDeputy.StatusCode);
        Assert.True(await IsMember(t, t.E));
        Assert.True(await IsMember(t, t.F));
    }

    [Fact]
    public async Task Asking_twice_for_the_same_person_is_a_409_and_asking_for_a_member_is_too()
    {
        var t = await NewTeam();
        await app.Befriend(t.B, t.C);   // a friend of B who is already in the group
        (await Add(t, t.CB, t.D)).Dispose();
        using var again = await Add(t, t.CB, t.D);
        using var other = await Add(t, t.CB, t.C);
        Assert.Equal("already_requested", await ErrorOf(again));
        Assert.Equal("already_member", await ErrorOf(other));
    }

    [Fact]
    public async Task Two_people_asking_for_the_same_person_at_the_same_moment_make_one_request_and_no_error()
    {
        var t = await NewTeam();
        var (g, cg) = await app.Member();
        await app.Befriend(t.B, g); await app.Befriend(t.C, g);
        using var promote = await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = g.GomId }, t.CA);
        var calls = await Task.WhenAll(Add(t, t.CB, t.D), Add(t, cg, t.D));
        Assert.DoesNotContain(calls, c => (int)c.StatusCode >= 500);
        await using var db = app.Db.NewContext();
        Assert.Equal(1, await db.JoinRequests.CountAsync(x => x.ConversationId == t.Chat && x.UserId == t.D.Id));
        foreach (var c in calls) c.Dispose();
    }

    // ---- answering

    [Fact]
    public async Task The_leader_and_deputies_see_who_is_waiting_and_who_asked_by_name_and_a_plain_member_does_not()
    {
        var t = await NewTeam();
        // Test members are named after their Gom ID; give two of them real names so a leaked ID cannot hide behind them.
        (await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "Bé Ba" }, t.CB)).Dispose();
        (await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "Bạn Dũng" }, t.CD)).Dispose();
        (await Add(t, t.CB, t.D)).Dispose();
        foreach (var cookie in new[] { t.CA, t.CC })
        {
            using var r = await Requests(t, cookie);
            var text = await r.Content.ReadAsStringAsync();
            var one = JsonDocument.Parse(text).RootElement.GetProperty("requests").EnumerateArray().Single();
            Assert.Equal(t.D.Id, one.GetProperty("id").GetGuid());
            Assert.Equal("Bạn Dũng", one.GetProperty("displayName").GetString());
            Assert.Equal("Bé Ba", one.GetProperty("by").GetProperty("displayName").GetString());
            Assert.DoesNotContain(t.D.GomId, text);
            Assert.DoesNotContain(t.B.GomId, text);
        }
        using var member = await Requests(t, t.CB);
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
    }

    [Fact]
    public async Task Approving_adds_the_person_who_then_sees_only_what_comes_after_and_the_request_is_gone()
    {
        var t = await NewTeam();
        using var said = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid(), body = "chuyện cũ" }, t.CA);
        (await Add(t, t.CB, t.D)).Dispose();
        using var r = await Approve(t, t.CC, t.D);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.True(await IsMember(t, t.D));
        using var history = await app.Call(Get, $"/api/conversations/{t.Chat}/messages", cookie: t.CD);
        Assert.Empty((await AppFixture.Json(history)).GetProperty("messages").EnumerateArray());
        using var left = await Requests(t, t.CA);
        Assert.Empty((await AppFixture.Json(left)).GetProperty("requests").EnumerateArray());
    }

    [Fact]
    public async Task A_plain_member_cannot_approve_and_there_must_be_a_request_to_approve()
    {
        var t = await NewTeam();
        (await Add(t, t.CB, t.D)).Dispose();
        using var byMember = await Approve(t, t.CB, t.D);
        using var nothing = await Approve(t, t.CA, t.E);
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode);
        Assert.False(await IsMember(t, t.D));
    }

    [Fact]
    public async Task Approving_into_a_full_group_is_a_409_and_the_request_stays()
    {
        var t = await NewTeam();    // the test server's limit is 5 people
        (await Add(t, t.CA, t.E)).Dispose();
        (await Add(t, t.CA, t.F)).Dispose();
        var (g, _) = await app.Member();
        await app.Befriend(t.A, g); await app.Befriend(t.B, g);
        (await Add(t, t.CB, g)).Dispose();
        using var r = await Approve(t, t.CA, g);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("group_full", await ErrorOf(r));
        await using var db = app.Db.NewContext();
        Assert.True(await db.JoinRequests.AnyAsync(x => x.ConversationId == t.Chat && x.UserId == g.Id));
    }

    [Fact]
    public async Task The_leader_declines_and_the_member_who_asked_may_take_their_request_back_but_not_someone_elses()
    {
        var t = await NewTeam();
        (await Add(t, t.CB, t.D)).Dispose();
        (await Add(t, t.CB, t.E)).Dispose();
        using var byOther = await Decline(t, t.CC == t.CB ? t.CB : t.CC, t.D);   // a deputy may
        Assert.Equal(HttpStatusCode.NoContent, byOther.StatusCode);
        using var own = await Decline(t, t.CB, t.E);
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        (await Add(t, t.CB, t.F)).Dispose();
        var (g, cg) = await app.Member();
        await app.Befriend(t.A, g);
        (await Add(t, t.CA, g)).Dispose();
        using var notYours = await Decline(t, cg, t.F);
        Assert.Equal(HttpStatusCode.Forbidden, notYours.StatusCode);
        Assert.False(await IsMember(t, t.D));
    }

    [Fact]
    public async Task Turning_approval_off_drops_what_was_waiting()
    {
        var t = await NewTeam();
        (await Add(t, t.CB, t.D)).Dispose();
        await Approval(t, false);
        await using var db = app.Db.NewContext();
        Assert.False(await db.JoinRequests.AnyAsync(x => x.ConversationId == t.Chat));
    }

    [Fact]
    public async Task The_leader_and_deputies_are_told_to_reload_when_a_request_arrives_and_a_plain_member_is_not()
    {
        var t = await NewTeam();
        using var wsA = await Connect(t.CA);
        using var wsC = await Connect(t.CC);
        using var wsAsker = await Connect(t.CB);   // a plain member in the group: it is the one who asked, and needs no reload
        using var wsOutside = await Connect(t.CE);
        (await Add(t, t.CB, t.D)).Dispose();
        Assert.Equal("conversations", (await Next(wsA)).GetProperty("t").GetString());
        Assert.Equal("conversations", (await Next(wsC)).GetProperty("t").GetString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Next(wsAsker, TimeSpan.FromMilliseconds(400)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Next(wsOutside, TimeSpan.FromMilliseconds(400)));
    }

    // ---- bans

    [Fact]
    public async Task Removing_with_a_ban_keeps_the_person_out_whoever_tries_to_add_them_and_without_a_ban_they_can_return()
    {
        var t = await NewTeam(approval: false);
        (await Add(t, t.CB, t.D)).Dispose();
        (await Add(t, t.CB, t.E)).Dispose();
        (await Remove(t, t.CA, t.D, ban: true)).Dispose();
        (await Remove(t, t.CA, t.E, ban: false)).Dispose();
        using var byMember = await Add(t, t.CB, t.D);
        using var byLeader = await Add(t, t.CA, t.D);
        using var back = await Add(t, t.CB, t.E);
        Assert.Equal("banned", await ErrorOf(byMember));
        Assert.Equal(HttpStatusCode.Forbidden, byLeader.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
    }

    [Fact]
    public async Task A_banned_person_cannot_be_approved_either()
    {
        var t = await NewTeam();
        (await Add(t, t.CA, t.D)).Dispose();
        (await Add(t, t.CB, t.E)).Dispose();
        (await Remove(t, t.CA, t.D, ban: true)).Dispose();
        // The request for E was made before E was banned: ban E by removing them (they are not a member), so ban through a member route instead.
        await using (var db = app.Db.NewContext())
        {
            db.Bans.Add(new GroupBan { ConversationId = t.Chat, UserId = t.E.Id, BannedBy = t.A.Id, CreatedAt = app.Clock.Now });
            await db.SaveChangesAsync();
        }
        using var r = await Approve(t, t.CA, t.E);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.False(await IsMember(t, t.E));
    }

    [Fact]
    public async Task Leaving_is_not_a_ban()
    {
        var t = await NewTeam(approval: false);
        (await Add(t, t.CB, t.D)).Dispose();
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/members/me", cookie: t.CD)).Dispose();
        using var back = await Add(t, t.CB, t.D);
        Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
    }

    [Fact]
    public async Task The_leader_and_deputies_see_the_bans_and_only_the_leader_lifts_one()
    {
        var t = await NewTeam(approval: false);
        (await Add(t, t.CB, t.D)).Dispose();
        (await Remove(t, t.CC, t.D, ban: true)).Dispose();
        foreach (var cookie in new[] { t.CA, t.CC })
        {
            using var r = await Bans(t, cookie);
            var one = (await AppFixture.Json(r)).GetProperty("bans").EnumerateArray().Single();
            Assert.Equal(t.D.Id, one.GetProperty("id").GetGuid());
        }
        using var member = await Bans(t, t.CB);
        using var byDeputy = await Unban(t, t.CC, t.D);
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byDeputy.StatusCode);
        using var byLeader = await Unban(t, t.CA, t.D);
        Assert.Equal(HttpStatusCode.NoContent, byLeader.StatusCode);
        using var back = await Add(t, t.CB, t.D);
        Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
    }

    [Fact]
    public async Task Someone_outside_the_group_learns_nothing_from_any_of_these()
    {
        var t = await NewTeam();
        var (_, stranger) = await app.Member();
        foreach (var r in new[] { await Requests(t, stranger), await Bans(t, stranger), await Approve(t, stranger, t.D), await Decline(t, stranger, t.D), await Unban(t, stranger, t.D) })
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            r.Dispose();
        }
    }

    [Fact]
    public async Task Ending_a_group_works_when_it_has_requests_and_bans()
    {
        var t = await NewTeam();
        (await Add(t, t.CB, t.D)).Dispose();
        (await Add(t, t.CA, t.E)).Dispose();
        (await Remove(t, t.CA, t.E, ban: true)).Dispose();
        using var r = await app.Call(Delete, $"/api/conversations/{t.Chat}", cookie: t.CA);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.JoinRequests.AnyAsync(x => x.ConversationId == t.Chat));
        Assert.False(await db.Bans.AnyAsync(x => x.ConversationId == t.Chat));
    }

    private async Task<WebSocket> Connect(string cookie)
    {
        var client = app.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => { r.Headers["Cookie"] = $"__Host-gom={cookie}"; r.Headers["Origin"] = AppFixture.Origin; };
        var ws = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        Assert.Equal("ready", (await Next(ws)).GetProperty("t").GetString());
        return ws;
    }

    private static async Task<JsonElement> Next(WebSocket ws, TimeSpan? within = null)
    {
        using var cts = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(5));
        var buffer = new byte[16384];
        var r = await ws.ReceiveAsync(buffer, cts.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)).RootElement.Clone();
    }
}
