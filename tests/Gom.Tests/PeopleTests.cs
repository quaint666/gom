using System.Net;
using System.Text.Json;
using Gom.Web.Data;

namespace Gom.Tests;

/// <summary>A person's card: who they are to you and the groups you share. Only for friends and people you share a conversation with.</summary>
public class PeopleTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Delete = HttpMethod.Delete;

    private async Task<Guid> Group(string cookie, string title, params User[] members)
    {
        using var r = await app.Call(Post, "/api/conversations/group", new { title, members = members.Select(m => m.GomId).ToArray() }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Card(string cookie, Guid userId)
    {
        using var r = await app.Call(Get, $"/api/people/{userId}", cookie: cookie);
        return (r.StatusCode, r.StatusCode == HttpStatusCode.OK ? (await AppFixture.Json(r)).Clone() : default);
    }

    [Fact]
    public async Task A_friend_has_their_Gom_ID_and_the_groups_you_share_most_recent_first()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member(); var (c, _) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c);
        // "Mới" is made first, but something is said in it last: the most recently active comes first.
        var newer = await Group(ca, "Mới", b, c);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var older = await Group(ca, "Cũ", b);
        await Group(ca, "Không có B", c);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var said = await app.Call(Post, $"/api/conversations/{newer}/messages", new { clientId = Guid.NewGuid(), body = "chào" }, ca))
            Assert.Equal(HttpStatusCode.OK, said.StatusCode);

        var (status, card) = await Card(ca, b.Id);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(b.DisplayName, card.GetProperty("displayName").GetString());
        Assert.Equal(b.GomId, card.GetProperty("gomId").GetString());
        Assert.Equal("friends", card.GetProperty("friend").GetString());
        var groups = card.GetProperty("commonGroups").EnumerateArray().ToList();
        Assert.Equal([newer, older], groups.Select(g => g.GetProperty("id").GetGuid()));
        Assert.Equal("Mới", groups[0].GetProperty("title").GetString());
        Assert.Equal(3, groups[0].GetProperty("memberCount").GetInt32());
    }

    [Fact]
    public async Task Someone_you_share_a_group_with_but_are_not_friends_with_has_no_Gom_ID_and_the_request_state_shows_from_each_side()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c);
        var home = await Group(ca, "Nhà", b, c);

        var (status, card) = await Card(cb, c.Id);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("gomId").ValueKind);
        Assert.Equal("none", card.GetProperty("friend").GetString());
        Assert.Equal([home], card.GetProperty("commonGroups").EnumerateArray().Select(g => g.GetProperty("id").GetGuid()));

        using (var ask = await app.Call(Post, $"/api/conversations/{home}/members/{c.Id}/friend-request", cookie: cb))
            Assert.True(ask.IsSuccessStatusCode);
        Assert.Equal("sent", (await Card(cb, c.Id)).Body.GetProperty("friend").GetString());
        Assert.Equal("received", (await Card(cc, b.Id)).Body.GetProperty("friend").GetString());
    }

    [Fact]
    public async Task Nobody_else_is_found_not_a_stranger_not_an_unknown_id_and_not_yourself()
    {
        var (a, ca) = await app.Member(); var (stranger, _) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        await Group(ca, "Nhà", b);   // you share conversations with yourself, too: still no card of your own
        Assert.Equal(HttpStatusCode.NotFound, (await Card(ca, stranger.Id)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Card(ca, Guid.NewGuid())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Card(ca, a.Id)).Status);
    }

    [Fact]
    public async Task Leaving_the_only_group_you_shared_makes_them_a_stranger_again()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, _) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c);
        var home = await Group(ca, "Nhà", b, c);
        Assert.Equal(HttpStatusCode.OK, (await Card(cb, c.Id)).Status);
        using (var leave = await app.Call(Delete, $"/api/conversations/{home}/members/me", cookie: cb))
            Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Card(cb, c.Id)).Status);
    }

    [Fact]
    public async Task Someone_you_still_share_a_conversation_with_after_unfriending_is_still_found_without_a_Gom_ID()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        await app.OpenChat(ca, b.GomId);
        using (var off = await app.Call(Delete, $"/api/friends/{b.GomId}", cookie: ca))
            Assert.True(off.IsSuccessStatusCode);
        var (status, card) = await Card(ca, b.Id);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("gomId").ValueKind);
        Assert.Equal("none", card.GetProperty("friend").GetString());
        Assert.Empty(card.GetProperty("commonGroups").EnumerateArray());
    }

    [Fact]
    public async Task Signed_out_it_is_401()
    {
        using var r = await app.Call(Get, $"/api/people/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}
