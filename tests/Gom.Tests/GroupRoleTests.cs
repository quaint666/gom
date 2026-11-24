using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>The leader, the deputies and what each may do. A group of A (leader), B, C, D; E is outside.</summary>
public class GroupRoleTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get, Delete = HttpMethod.Delete, Put = HttpMethod.Put;

    private sealed record Team(User A, string CA, User B, string CB, User C, string CC, User D, string CD, User E, string CE, Guid Chat);

    private async Task<Team> NewTeam()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        var (d, cd) = await app.Member(); var (e, ce) = await app.Member();
        foreach (var x in new[] { b, c, d, e }) await app.Befriend(a, x);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà mình", members = new[] { b.GomId, c.GomId, d.GomId } }, ca);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new Team(a, ca, b, cb, c, cc, d, cd, e, ce, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private Task<HttpResponseMessage> Remove(Team t, string cookie, User who) => app.Call(Delete, $"/api/conversations/{t.Chat}/members/{who.Id}", cookie: cookie);
    private Task<HttpResponseMessage> SetRole(Team t, string cookie, User who, string role) => app.Call(Put, $"/api/conversations/{t.Chat}/members/{who.Id}/role", new { role }, cookie);
    private Task<HttpResponseMessage> HandOver(Team t, string cookie, User to) => app.Call(Post, $"/api/conversations/{t.Chat}/leader", new { userId = to.Id }, cookie);
    private Task<HttpResponseMessage> Leave(Team t, string cookie) => app.Call(Delete, $"/api/conversations/{t.Chat}/members/me", cookie: cookie);
    private Task<HttpResponseMessage> Disband(Team t, string cookie) => app.Call(Delete, $"/api/conversations/{t.Chat}", cookie: cookie);
    private Task<HttpResponseMessage> Say(Team t, string cookie, string body) => app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
    private Task<HttpResponseMessage> History(Team t, string cookie) => app.Call(Get, $"/api/conversations/{t.Chat}/messages", cookie: cookie);

    private static async Task<string?> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString();

    private async Task<Dictionary<Guid, GroupRole>> Roles(Team t)
    {
        await using var db = app.Db.NewContext();
        return await db.Members.Where(m => m.ConversationId == t.Chat).ToDictionaryAsync(m => m.UserId, m => m.Role);
    }

    private async Task Promote(Team t, User who)
    {
        using var r = await SetRole(t, t.CA, who, "deputy");
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    // ---- who leads

    [Fact]
    public async Task The_creator_is_the_leader_and_the_others_are_plain_members()
    {
        var t = await NewTeam();
        var roles = await Roles(t);
        Assert.Equal(GroupRole.Leader, roles[t.A.Id]);
        Assert.All(new[] { t.B, t.C, t.D }, m => Assert.Equal(GroupRole.Member, roles[m.Id]));
    }

    [Fact]
    public async Task The_member_list_says_who_leads_and_who_is_a_deputy()
    {
        var t = await NewTeam();
        await Promote(t, t.B);
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/members", cookie: t.CC);
        var byId = (await AppFixture.Json(r)).EnumerateArray().ToDictionary(m => m.GetProperty("id").GetGuid(), m => m.GetProperty("role").GetString());
        Assert.Equal("leader", byId[t.A.Id]);
        Assert.Equal("deputy", byId[t.B.Id]);
        Assert.Equal("member", byId[t.C.Id]);
    }

    // ---- naming deputies

    [Fact]
    public async Task Only_the_leader_names_deputies_and_takes_the_title_back()
    {
        var t = await NewTeam();
        using var byMember = await SetRole(t, t.CB, t.C, "deputy");
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);

        await Promote(t, t.B);
        using var byDeputy = await SetRole(t, t.CB, t.C, "deputy");
        Assert.Equal(HttpStatusCode.Forbidden, byDeputy.StatusCode);

        using var back = await SetRole(t, t.CA, t.B, "member");
        Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        Assert.Equal(GroupRole.Member, (await Roles(t))[t.B.Id]);
    }

    [Fact]
    public async Task A_role_must_be_deputy_or_member_and_not_for_the_leader_or_for_yourself()
    {
        var t = await NewTeam();
        using var odd = await SetRole(t, t.CA, t.B, "leader");
        using var self = await SetRole(t, t.CA, t.A, "deputy");
        using var stranger = await SetRole(t, t.CA, t.E, "deputy");
        Assert.Equal(HttpStatusCode.BadRequest, odd.StatusCode);
        Assert.Equal("invalid_role", await ErrorOf(odd));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal(GroupRole.Leader, (await Roles(t))[t.A.Id]);
    }

    // ---- removing

    [Fact]
    public async Task The_leader_removes_a_member_who_then_sees_nothing_and_cannot_send_and_the_others_carry_on()
    {
        var t = await NewTeam();
        using var said = await Say(t, t.CB, "trước khi bị xóa");
        Assert.Equal(HttpStatusCode.OK, said.StatusCode);

        using var removed = await Remove(t, t.CA, t.B);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var history = await History(t, t.CB);
        using var send = await Say(t, t.CB, "còn gửi được không");
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, send.StatusCode);
        using var others = await Say(t, t.CC, "chúng ta vẫn nói chuyện");
        Assert.Equal(HttpStatusCode.OK, others.StatusCode);
        using var list = await app.Call(Get, "/api/conversations", cookie: t.CB);
        Assert.DoesNotContain(t.Chat.ToString(), await list.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_plain_member_cannot_remove_anyone()
    {
        var t = await NewTeam();
        using var r = await Remove(t, t.CB, t.C);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("not_allowed", await ErrorOf(r));
        Assert.Equal(4, (await Roles(t)).Count);
    }

    [Fact]
    public async Task A_deputy_removes_plain_members_but_not_the_leader_or_another_deputy()
    {
        var t = await NewTeam();
        await Promote(t, t.B); await Promote(t, t.C);

        using var leader = await Remove(t, t.CB, t.A);
        using var deputy = await Remove(t, t.CB, t.C);
        using var plain = await Remove(t, t.CB, t.D);

        Assert.Equal(HttpStatusCode.Forbidden, leader.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deputy.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, plain.StatusCode);
        Assert.Equal(3, (await Roles(t)).Count);
    }

    [Fact]
    public async Task The_leader_removes_a_deputy_too()
    {
        var t = await NewTeam();
        await Promote(t, t.B);
        using var r = await Remove(t, t.CA, t.B);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    [Fact]
    public async Task Removing_yourself_is_leaving_and_removing_a_stranger_is_not_found()
    {
        var t = await NewTeam();
        using var self = await Remove(t, t.CA, t.A);
        using var stranger = await Remove(t, t.CA, t.E);
        using var outsider = await Remove(t, t.CE, t.B);
        Assert.Equal("use_leave", await ErrorOf(self));
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    [Fact]
    public async Task A_removed_member_can_be_added_again_and_sees_only_what_comes_after()
    {
        var t = await NewTeam();
        (await Say(t, t.CC, "chuyện cũ")).Dispose();
        (await Remove(t, t.CA, t.B)).Dispose();
        (await Say(t, t.CC, "trong lúc vắng mặt")).Dispose();
        using var add = await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = t.B.GomId }, t.CA);
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
        (await Say(t, t.CC, "sau khi quay lại")).Dispose();

        using var history = await History(t, t.CB);

        var all = (await AppFixture.Json(history)).GetProperty("messages").EnumerateArray().ToList();
        var bodies = all.Where(m => !(m.TryGetProperty("notice", out var notice) && notice.ValueKind == JsonValueKind.Object)).Select(m => m.GetProperty("body").GetString()).ToArray();
        Assert.Equal(new[] { "sau khi quay lại" }, bodies);
        Assert.Single(all, m => m.TryGetProperty("notice", out var added) && added.ValueKind == JsonValueKind.Object);   // and the line that says they came back
        Assert.Equal(GroupRole.Member, (await Roles(t))[t.B.Id]);
    }

    [Fact]
    public async Task A_one_to_one_chat_has_no_removing()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        using var r = await app.Call(Delete, $"/api/conversations/{chat}/members/{b.Id}", cookie: ca);
        Assert.Equal("not_a_group", await ErrorOf(r));
    }

    // ---- handing over

    [Fact]
    public async Task The_leader_hands_over_and_becomes_a_plain_member_and_there_is_one_leader()
    {
        var t = await NewTeam();
        using var r = await HandOver(t, t.CA, t.B);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var roles = await Roles(t);
        Assert.Equal(GroupRole.Leader, roles[t.B.Id]);
        Assert.Equal(GroupRole.Member, roles[t.A.Id]);
        Assert.Equal(1, roles.Values.Count(x => x == GroupRole.Leader));
    }

    [Fact]
    public async Task Only_the_leader_hands_over_and_only_to_a_member_of_the_group_who_is_someone_else()
    {
        var t = await NewTeam();
        await Promote(t, t.B);
        using var byDeputy = await HandOver(t, t.CB, t.C);
        using var toStranger = await HandOver(t, t.CA, t.E);
        using var toSelf = await HandOver(t, t.CA, t.A);
        Assert.Equal(HttpStatusCode.Forbidden, byDeputy.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, toStranger.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, toSelf.StatusCode);
        Assert.Equal(GroupRole.Leader, (await Roles(t))[t.A.Id]);
    }

    [Fact]
    public async Task Two_hand_overs_at_the_same_moment_leave_exactly_one_leader()
    {
        var t = await NewTeam();
        var both = await Task.WhenAll(HandOver(t, t.CA, t.B), HandOver(t, t.CA, t.C));
        var codes = both.Select(r => r.StatusCode).OrderBy(c => c).ToArray();
        Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Forbidden }, codes);
        Assert.Equal(1, (await Roles(t)).Values.Count(x => x == GroupRole.Leader));
        foreach (var r in both) r.Dispose();
    }

    // ---- leaving

    [Fact]
    public async Task The_leader_cannot_leave_without_handing_over()
    {
        var t = await NewTeam();
        using var r = await Leave(t, t.CA);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("leader_must_hand_over", await ErrorOf(r));
        Assert.Equal(GroupRole.Leader, (await Roles(t))[t.A.Id]);
    }

    [Fact]
    public async Task After_handing_over_the_old_leader_can_leave()
    {
        var t = await NewTeam();
        (await HandOver(t, t.CA, t.B)).Dispose();
        using var r = await Leave(t, t.CA);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    [Fact]
    public async Task A_leader_alone_in_the_group_who_leaves_ends_it()
    {
        var t = await NewTeam();
        foreach (var who in new[] { t.B, t.C, t.D }) (await Remove(t, t.CA, who)).Dispose();
        using var r = await Leave(t, t.CA);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Conversations.AnyAsync(c => c.Id == t.Chat));
    }

    // ---- disbanding

    [Fact]
    public async Task Only_the_leader_disbands()
    {
        var t = await NewTeam();
        await Promote(t, t.B);
        using var deputy = await Disband(t, t.CB);
        using var outsider = await Disband(t, t.CE);
        Assert.Equal(HttpStatusCode.Forbidden, deputy.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == t.Chat));
    }

    [Fact]
    public async Task Disbanding_takes_the_group_and_all_its_messages_from_everyone()
    {
        var t = await NewTeam();
        (await Say(t, t.CB, "một")).Dispose();
        (await Say(t, t.CC, "hai")).Dispose();

        using var r = await Disband(t, t.CA);

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Conversations.AnyAsync(c => c.Id == t.Chat));
        Assert.False(await db.Messages.AnyAsync(m => m.ConversationId == t.Chat));
        Assert.False(await db.Events.AnyAsync(e => e.ConversationId == t.Chat));
        Assert.False(await db.Members.AnyAsync(m => m.ConversationId == t.Chat));
        foreach (var cookie in new[] { t.CA, t.CB, t.CC, t.CD })
        {
            using var h = await History(t, cookie);
            Assert.Equal(HttpStatusCode.NotFound, h.StatusCode);
        }
    }
}
