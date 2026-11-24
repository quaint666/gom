using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class FriendTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get, Delete = HttpMethod.Delete;

    private Task<HttpResponseMessage> Ask(string cookie, string gomId) =>
        app.Call(Post, "/api/friends/requests", new { gomId }, cookie);

    private async Task<JsonElement> Lists(string cookie)
    {
        using var r = await app.Call(Get, "/api/friends", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private static string[] Ids(JsonElement list) => list.EnumerateArray().Select(x => x.GetProperty("gomId").GetString()!).ToArray();

    private async Task<int> RowsBetween(User a, User b)
    {
        var (low, high) = Friendship.Order(a.Id, b.Id);
        await using var db = app.Db.NewContext();
        return await db.Friendships.CountAsync(f => f.UserLow == low && f.UserHigh == high);
    }

    // ---- asking

    [Fact]
    public async Task A_member_asks_by_exact_ID_and_both_sides_see_the_request()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();

        using var response = await Ask(ca, b.GomId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pending", (await AppFixture.Json(response)).GetProperty("status").GetString());
        Assert.Equal([b.GomId], Ids((await Lists(ca)).GetProperty("outgoing")));
        Assert.Equal([a.GomId], Ids((await Lists(cb)).GetProperty("incoming")));
        Assert.Empty(Ids((await Lists(ca)).GetProperty("friends")));
        Assert.Empty(Ids((await Lists(ca)).GetProperty("incoming")));   // my own request is not "incoming" for me
        Assert.Empty(Ids((await Lists(cb)).GetProperty("outgoing")));
    }

    [Fact]
    public async Task A_locked_member_drops_out_of_every_list()
    {
        var (a, ca) = await app.Member();
        var friend = await app.AddUser();
        var asker = await app.AddUser();
        var asked = await app.AddUser();
        await app.Befriend(a, friend);
        var cAsker = await app.SignIn(asker.GomId);
        using var ask = await Ask(cAsker, a.GomId);
        var cMe = ca;
        using var askOut = await Ask(cMe, asked.GomId);
        var (_, ownerCookie) = await app.Owner();
        foreach (var u in new[] { friend, asker, asked })
        {
            using var lockIt = await app.Call(Post, $"/api/users/{u.GomId}/disable", cookie: ownerCookie);
            Assert.Equal(HttpStatusCode.NoContent, lockIt.StatusCode);
        }

        var lists = await Lists(ca);

        Assert.Empty(Ids(lists.GetProperty("friends")));
        Assert.Empty(Ids(lists.GetProperty("incoming")));
        Assert.Empty(Ids(lists.GetProperty("outgoing")));
    }

    [Fact]
    public async Task The_ID_is_matched_ignoring_case_and_spaces()
    {
        var (_, ca) = await app.Member();
        var (b, _) = await app.Member();

        using var response = await Ask(ca, "  " + b.GomId.ToUpperInvariant() + " ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_or_locked_ID_is_a_404()
    {
        var (_, ca) = await app.Member();
        var locked = await app.AddUser(disabled: true);

        using var unknown = await Ask(ca, "nobody_at_all");
        using var disabled = await Ask(ca, locked.GomId);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.Equal("user_not_found", (await AppFixture.Json(unknown)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Nobody_asks_themself()
    {
        var (a, ca) = await app.Member();
        using var response = await Ask(ca, a.GomId);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("cannot_add_self", (await AppFixture.Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Asking_twice_is_still_one_request()
    {
        var (a, ca) = await app.Member();
        var (b, _) = await app.Member();
        using var first = await Ask(ca, b.GomId);

        using var second = await Ask(ca, b.GomId);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("pending", (await AppFixture.Json(second)).GetProperty("status").GetString());
        Assert.Equal(1, await RowsBetween(a, b));
    }

    [Fact]
    public async Task Asking_someone_who_already_asked_you_makes_you_friends_at_once()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        using var theirs = await Ask(cb, a.GomId);

        using var mine = await Ask(ca, b.GomId);

        Assert.Equal("friends", (await AppFixture.Json(mine)).GetProperty("status").GetString());
        Assert.Equal([b.GomId], Ids((await Lists(ca)).GetProperty("friends")));
        Assert.Equal([a.GomId], Ids((await Lists(cb)).GetProperty("friends")));
    }

    [Fact]
    public async Task Asking_a_friend_is_a_409()
    {
        var (a, ca) = await app.Member();
        var (b, _) = await app.Member();
        await app.Befriend(a, b);

        using var response = await Ask(ca, b.GomId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("already_friends", (await AppFixture.Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Two_people_asking_each_other_at_the_same_moment_end_up_friends_without_an_error()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();

        var both = await Task.WhenAll(Ask(ca, b.GomId), Ask(cb, a.GomId));

        Assert.All(both, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await RowsBetween(a, b));
        Assert.Equal([b.GomId], Ids((await Lists(ca)).GetProperty("friends")));
    }

    // ---- answering

    [Fact]
    public async Task Only_the_one_asked_can_accept()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        using var ask = await Ask(ca, b.GomId);

        using var byAsker = await app.Call(Post, $"/api/friends/requests/{b.GomId}/accept", cookie: ca);
        using var byAsked = await app.Call(Post, $"/api/friends/requests/{a.GomId}/accept", cookie: cb);

        Assert.Equal(HttpStatusCode.NotFound, byAsker.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, byAsked.StatusCode);
        Assert.Equal([a.GomId], Ids((await Lists(cb)).GetProperty("friends")));
        Assert.Equal([b.GomId], Ids((await Lists(ca)).GetProperty("friends")));
        Assert.Empty(Ids((await Lists(cb)).GetProperty("incoming")));
    }

    [Fact]
    public async Task Accepting_a_request_that_is_not_there_is_a_404()
    {
        var (_, ca) = await app.Member();
        var (b, _) = await app.Member();
        using var response = await app.Call(Post, $"/api/friends/requests/{b.GomId}/accept", cookie: ca);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_asked_can_decline_and_the_asker_can_cancel_and_both_leave_no_trace()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        var (c, cc) = await app.Member();
        using var ab = await Ask(ca, b.GomId);
        using var ac = await Ask(ca, c.GomId);

        using var declined = await app.Call(Delete, $"/api/friends/requests/{a.GomId}", cookie: cb);
        using var cancelled = await app.Call(Delete, $"/api/friends/requests/{c.GomId}", cookie: ca);

        Assert.Equal(HttpStatusCode.NoContent, declined.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal(0, await RowsBetween(a, b));
        Assert.Equal(0, await RowsBetween(a, c));
        using var again = await Ask(ca, b.GomId);   // a declined request can be made again
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Declining_cannot_be_used_to_unfriend()
    {
        var (a, ca) = await app.Member();
        var (b, _) = await app.Member();
        await app.Befriend(a, b);

        using var response = await app.Call(Delete, $"/api/friends/requests/{b.GomId}", cookie: ca);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await RowsBetween(a, b));
    }

    [Fact]
    public async Task Unfriending_removes_the_friendship_for_both()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);

        using var response = await app.Call(Delete, $"/api/friends/{b.GomId}", cookie: ca);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(Ids((await Lists(cb)).GetProperty("friends")));
        using var again = await app.Call(Delete, $"/api/friends/{b.GomId}", cookie: ca);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Unfriending_a_pending_request_is_a_404()
    {
        var (_, ca) = await app.Member();
        var (b, _) = await app.Member();
        using var ask = await Ask(ca, b.GomId);
        using var response = await app.Call(Delete, $"/api/friends/{b.GomId}", cookie: ca);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- invite

    [Fact]
    public async Task Signing_up_through_an_invite_makes_you_friends_with_whoever_invited()
    {
        var (owner, ownerCookie) = await app.Owner();
        var token = await app.NewInviteToken();

        using var join = await app.Call(Post, $"/api/join/{token}", new { gomId = "newcomer_1", password = AppFixture.Password, displayName = "" });

        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        Assert.Equal(["newcomer_1"], Ids((await Lists(ownerCookie)).GetProperty("friends")));
        var cookie = AppFixture.CookieValue(join)!;
        Assert.Equal([owner.GomId], Ids((await Lists(cookie)).GetProperty("friends")));
    }

    // ---- blocking

    [Fact]
    public async Task Blocking_ends_the_friendship_and_any_request_between_you()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        var (c, cc) = await app.Member();
        await app.Befriend(a, b);
        using var ask = await Ask(cc, a.GomId);

        using var blockB = await app.Call(Post, $"/api/blocks/{b.GomId}", cookie: ca);
        using var blockC = await app.Call(Post, $"/api/blocks/{c.GomId}", cookie: ca);

        Assert.Equal(HttpStatusCode.NoContent, blockB.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, blockC.StatusCode);
        Assert.Equal(0, await RowsBetween(a, b));
        Assert.Equal(0, await RowsBetween(a, c));
        using var list = await app.Call(Get, "/api/blocks", cookie: ca);
        Assert.Equal(new[] { b.GomId, c.GomId }.Order(), Ids(await AppFixture.Json(list)).Order());
    }

    [Fact]
    public async Task A_blocked_person_asking_looks_like_it_worked_but_nothing_is_stored()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        using var block = await app.Call(Post, $"/api/blocks/{b.GomId}", cookie: ca);

        using var response = await Ask(cb, a.GomId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pending", (await AppFixture.Json(response)).GetProperty("status").GetString());
        Assert.Equal(0, await RowsBetween(a, b));
        Assert.Empty(Ids((await Lists(ca)).GetProperty("incoming")));
    }

    [Fact]
    public async Task You_cannot_ask_someone_you_blocked_until_you_unblock()
    {
        var (_, ca) = await app.Member();
        var (b, _) = await app.Member();
        using var block = await app.Call(Post, $"/api/blocks/{b.GomId}", cookie: ca);

        using var refused = await Ask(ca, b.GomId);
        using var unblock = await app.Call(Delete, $"/api/blocks/{b.GomId}", cookie: ca);
        using var allowed = await Ask(ca, b.GomId);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("you_blocked_them", (await AppFixture.Json(refused)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NoContent, unblock.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Blocking_twice_is_fine_and_blocking_yourself_or_a_stranger_is_not()
    {
        var (a, ca) = await app.Member();
        var (b, _) = await app.Member();

        using var first = await app.Call(Post, $"/api/blocks/{b.GomId}", cookie: ca);
        using var second = await app.Call(Post, $"/api/blocks/{b.GomId}", cookie: ca);
        using var self = await app.Call(Post, $"/api/blocks/{a.GomId}", cookie: ca);
        using var nobody = await app.Call(Post, "/api/blocks/nobody_at_all", cookie: ca);
        using var unblockNobody = await app.Call(Delete, "/api/blocks/nobody_at_all", cookie: ca);

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unblockNobody.StatusCode);
    }

    // ---- everyone

    [Fact]
    public async Task Everything_needs_a_session_and_a_good_Origin()
    {
        var (_, ca) = await app.Member();
        var (b, _) = await app.Member();

        foreach (var (method, path) in new[]
        {
            (Get, "/api/friends"), (Post, "/api/friends/requests"), (Post, $"/api/friends/requests/{b.GomId}/accept"),
            (Delete, $"/api/friends/requests/{b.GomId}"), (Delete, $"/api/friends/{b.GomId}"),
            (Get, "/api/blocks"), (Post, $"/api/blocks/{b.GomId}"), (Delete, $"/api/blocks/{b.GomId}"),
        })
        {
            using var anonymous = await app.Call(method, path, new { gomId = b.GomId });
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            if (method != Get)
            {
                using var foreign = await app.Call(method, path, new { gomId = b.GomId }, ca, origin: "https://evil.test");
                Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            }
        }
    }

    [Fact]
    public async Task The_lists_show_names_and_IDs_and_nothing_else_about_people()
    {
        var (a, ca) = await app.Member();
        var (b, _) = await app.Member();
        await app.Befriend(a, b);

        using var r = await app.Call(Get, "/api/friends", cookie: ca);
        var body = await r.Content.ReadAsStringAsync();

        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(b.Id.ToString(), body);
    }
}
