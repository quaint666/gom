using System.Net;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>
/// An invite, or an approval, that also puts the new person into one of the owner's groups (the testers' link, "join and land in the
/// family group"). The account is always made; the group only when the owner may still add to it.
/// </summary>
public class InviteGroupTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put;
    private static string NewId() => "g_" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>A group the owner leads, with one friend in it.</summary>
    private async Task<Guid> OwnersGroup(string title = "Testers")
    {
        var (owner, cookie) = await app.Owner();
        var friend = await app.AddUser();
        await app.Befriend(owner, friend);
        using var r = await app.Call(Post, "/api/conversations/group", new { title, members = new[] { friend.GomId } }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<string> Invite(Guid group)
    {
        var (_, cookie) = await app.Owner();
        using var r = await app.Call(Post, "/api/invites", new { uses = 0, group }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("token").GetString()!;
    }

    private Task<HttpResponseMessage> Join(string token, string gomId) =>
        app.Call(Post, $"/api/join/{token}", new { gomId, password = AppFixture.Password });

    private async Task<bool> IsMember(Guid group, string gomId)
    {
        await using var db = app.Db.NewContext();
        return await db.Members.AnyAsync(m => m.ConversationId == group && db.Users.Any(u => u.Id == m.UserId && u.GomId == gomId));
    }

    [Fact]
    public async Task Whoever_joins_through_the_link_lands_in_its_group_added_by_the_owner()
    {
        var group = await OwnersGroup();
        var token = await Invite(group);
        var id = NewId();
        using var joined = await Join(token, id);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.Equal(group, (await AppFixture.Json(joined)).GetProperty("group").GetGuid());
        Assert.True(await IsMember(group, id));

        var (owner, cookie) = await app.Owner();
        await using (var db = app.Db.NewContext())
        {
            var person = await db.Users.SingleAsync(u => u.GomId == id);
            Assert.True(await db.Messages.AnyAsync(m => m.ConversationId == group && m.Notice == NoticeKind.Added && m.SenderId == owner.Id && m.NoticeTargetId == person.Id));
        }
        using var list = await app.Call(Get, "/api/invites", cookie: cookie);
        var row = (await AppFixture.Json(list)).EnumerateArray().First(x => x.GetProperty("group").ValueKind != System.Text.Json.JsonValueKind.Null && x.GetProperty("group").GetProperty("id").GetGuid() == group);
        Assert.Equal("Testers", row.GetProperty("group").GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_list_says_your_place_in_each_group_and_none_for_a_one_to_one_chat()
    {
        var group = await OwnersGroup("Places");
        var (owner, cookie) = await app.Owner();
        var friend = await app.AddUser();
        await app.Befriend(owner, friend);
        var direct = await app.OpenChat(cookie, friend.GomId);
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        var rows = (await AppFixture.Json(r)).EnumerateArray().ToList();
        Assert.Equal("leader", rows.Single(x => x.GetProperty("id").GetGuid() == group).GetProperty("role").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows.Single(x => x.GetProperty("id").GetGuid() == direct).GetProperty("role").ValueKind);
    }

    [Fact]
    public async Task A_link_without_a_group_adds_to_none_and_says_so()
    {
        using var joined = await Join(await app.NewInviteToken(), NewId());
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await AppFixture.Json(joined)).GetProperty("group").ValueKind);
    }

    [Fact]
    public async Task A_link_is_refused_for_a_group_the_owner_does_not_lead()
    {
        var (owner, cookie) = await app.Owner();
        var (leader, leaderCookie) = await app.Member();
        var other = await app.AddUser();
        await app.Befriend(leader, owner);
        await app.Befriend(leader, other);
        using var made = await app.Call(Post, "/api/conversations/group", new { title = "Not mine", members = new[] { owner.GomId, other.GomId } }, leaderCookie);
        var theirs = (await AppFixture.Json(made)).GetProperty("id").GetGuid();
        using var r = await app.Call(Post, "/api/invites", new { group = theirs }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var unknown = await app.Call(Post, "/api/invites", new { group = Guid.NewGuid() }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task When_the_owner_no_longer_leads_the_group_the_account_is_still_made_without_it()
    {
        var group = await OwnersGroup();
        var token = await Invite(group);
        var (owner, _) = await app.Owner();
        await using (var db = app.Db.NewContext())
            await db.Members.Where(m => m.ConversationId == group && m.UserId == owner.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, GroupRole.Member));
        var id = NewId();
        using var joined = await Join(token, id);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.False(await IsMember(group, id));
    }

    [Fact]
    public async Task A_disbanded_group_leaves_its_link_working_without_it()
    {
        var group = await OwnersGroup();
        var token = await Invite(group);
        var (_, cookie) = await app.Owner();
        using (var gone = await app.Call(HttpMethod.Delete, $"/api/conversations/{group}", cookie: cookie))
            Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        using var joined = await Join(token, NewId());
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Links.AnyAsync(l => l.GroupId == group));
    }

    private async Task<string> Ask()
    {
        var (_, cookie) = await app.Owner();
        using (var open = await app.Call(Put, "/api/sign-up-open", new { open = true }, cookie)) Assert.Equal(HttpStatusCode.NoContent, open.StatusCode);
        app.Clock.Advance(TimeSpan.FromDays(1));
        var id = NewId();
        using var r = await app.Call(Post, "/api/sign-up", new { gomId = id, password = AppFixture.Password, note = "I'm here to test" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return id;
    }

    [Fact]
    public async Task Approving_with_a_group_makes_a_member_of_it_too()
    {
        var group = await OwnersGroup();
        var id = await Ask();
        var (_, cookie) = await app.Owner();
        using var r = await app.Call(Post, $"/api/join-requests/{id}/approve", new { group }, cookie);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.True(await IsMember(group, id));
    }

    [Fact]
    public async Task Approving_with_a_group_the_owner_may_not_add_to_approves_nothing()
    {
        var id = await Ask();
        var (_, cookie) = await app.Owner();
        using var r = await app.Call(Post, $"/api/join-requests/{id}/approve", new { group = Guid.NewGuid() }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.NotNull(await db.Users.Where(u => u.GomId == id).Select(u => u.RequestedAt).SingleAsync());   // still waiting
    }
}
