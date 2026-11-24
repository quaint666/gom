using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class GroupTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get, Delete = HttpMethod.Delete, Patch = HttpMethod.Patch;

    /// <summary>A is friends with B, C and D. B and C are not friends with each other. E is nobody's friend.</summary>
    private sealed record Crowd(User A, string CA, User B, string CB, User C, string CC, User D, string CD, User E, string CE);

    private async Task<Crowd> NewCrowd()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        var (d, cd) = await app.Member(); var (e, ce) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c); await app.Befriend(a, d);
        return new Crowd(a, ca, b, cb, c, cc, d, cd, e, ce);
    }

    private async Task Rename(string cookie, string name)
    {
        using var r = await app.Call(Patch, "/api/me", new { displayName = name }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private Task<HttpResponseMessage> Create(string cookie, string? title, params string[] members) =>
        app.Call(Post, "/api/conversations/group", new { title, members }, cookie);

    private async Task<Guid> MakeGroup(Crowd k, params string[] others)
    {
        using var r = await Create(k.CA, "Gia đình", others.Length == 0 ? [k.B.GomId, k.C.GomId] : others);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString();

    private async Task<JsonElement[]> List(string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().ToArray();
    }

    private async Task<Guid[]> MemberIds(Guid chat)
    {
        await using var db = app.Db.NewContext();
        return await db.Members.Where(m => m.ConversationId == chat).Select(m => m.UserId).ToArrayAsync();
    }

    private Task<HttpResponseMessage> Send(string cookie, Guid chat, string body) =>
        app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);

    // ---- creating

    [Fact]
    public async Task A_member_makes_a_group_of_their_own_friends_and_is_in_it_themself()
    {
        var k = await NewCrowd();

        using var response = await Create(k.CA, "  Gia đình  ", k.B.GomId, k.C.GomId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await AppFixture.Json(response)).GetProperty("id").GetGuid();
        await using var db = app.Db.NewContext();
        var chat = await db.Conversations.SingleAsync(c => c.Id == id);
        Assert.Equal((ConversationKind.Group, "Gia đình", null, k.A.Id, 0L), (chat.Kind, chat.Title, chat.DirectKey, chat.CreatedBy, chat.LastSeq));
        Assert.Equivalent(new[] { k.A.Id, k.B.Id, k.C.Id }, await MemberIds(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bell\u0007")]
    public async Task A_group_needs_a_proper_name(string? title)
    {
        var k = await NewCrowd();
        using var response = await Create(k.CA, title, k.B.GomId);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_title", await ErrorOf(response));
        Assert.Empty(await List(k.CA));
    }

    [Fact]
    public async Task A_name_of_sixty_characters_is_fine_and_sixty_one_is_not()
    {
        var k = await NewCrowd();
        using var fine = await Create(k.CA, new string('a', 60), k.B.GomId);
        using var tooLong = await Create(k.CA, new string('a', 61), k.B.GomId);
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task A_group_needs_somebody_besides_the_creator()
    {
        var k = await NewCrowd();
        using var none = await Create(k.CA, "Một mình");
        using var onlyMe = await Create(k.CA, "Một mình", k.A.GomId, k.A.GomId);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("no_members", await ErrorOf(none));
        Assert.Equal(HttpStatusCode.BadRequest, onlyMe.StatusCode);
    }

    [Fact]
    public async Task Only_your_own_friends_can_be_put_in_a_group_and_a_refusal_creates_nothing()
    {
        var k = await NewCrowd();
        var locked = await app.AddUser(disabled: true);
        await app.Befriend(k.A, locked);

        using var stranger = await Create(k.CA, "Nhóm", k.B.GomId, k.E.GomId);
        using var unknown = await Create(k.CA, "Nhóm", k.B.GomId, "nobody_at_all");
        using var lockedOne = await Create(k.CA, "Nhóm", k.B.GomId, locked.GomId);

        foreach (var r in new[] { stranger, unknown, lockedOne })
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("not_friends", await ErrorOf(r));   // the same answer for a stranger and for an ID that does not exist
        }
        Assert.Empty(await List(k.CA));
    }

    [Fact]
    public async Task The_same_person_listed_twice_and_a_different_case_count_once()
    {
        var k = await NewCrowd();
        using var response = await Create(k.CA, "Nhóm", k.B.GomId, k.B.GomId.ToUpperInvariant(), " " + k.B.GomId + " ");
        var id = (await AppFixture.Json(response)).GetProperty("id").GetGuid();
        Assert.Equal(2, (await MemberIds(id)).Length);
    }

    [Fact]
    public async Task A_group_cannot_be_bigger_than_the_limit()
    {
        var k = await NewCrowd();
        var more = new List<User> { k.B, k.C, k.D };
        for (var i = 0; i < 2; i++) { var u = await app.AddUser(); await app.Befriend(k.A, u); more.Add(u); }

        using var tooBig = await Create(k.CA, "Đông quá", more.Select(u => u.GomId).ToArray());          // 5 others + creator = 6, limit 5
        using var exactly = await Create(k.CA, "Vừa đủ", more.Take(4).Select(u => u.GomId).ToArray());   // 4 others + creator = 5

        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Equal("group_too_big", await ErrorOf(tooBig));
        Assert.Equal(HttpStatusCode.OK, exactly.StatusCode);
    }

    [Fact]
    public async Task Creating_needs_a_session_and_a_good_Origin()
    {
        var k = await NewCrowd();
        using var anonymous = await app.Call(Post, "/api/conversations/group", new { title = "x", members = new[] { k.B.GomId } });
        using var foreign = await app.Call(Post, "/api/conversations/group", new { title = "x", members = new[] { k.B.GomId } }, k.CA, origin: "https://evil.test");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task Everyone_in_the_group_sees_it_in_their_list_with_its_name_and_size()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);

        foreach (var cookie in new[] { k.CA, k.CB, k.CC })
        {
            var entry = Assert.Single(await List(cookie));
            Assert.Equal(id, entry.GetProperty("id").GetGuid());
            Assert.Equal(("group", "Gia đình", 3), (entry.GetProperty("kind").GetString(), entry.GetProperty("title").GetString(), entry.GetProperty("memberCount").GetInt32()));
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("peer").ValueKind);
        }
        Assert.Empty(await List(k.CD));
    }

    // ---- talking in a group

    [Fact]
    public async Task Members_who_are_not_friends_can_talk_to_each_other_in_the_group_and_see_names_only()
    {
        var k = await NewCrowd();
        await Rename(k.CB, "Bé Hai");
        var id = await MakeGroup(k);

        using var fromB = await Send(k.CB, id, "chào cả nhà");
        using var history = await app.Call(Get, $"/api/conversations/{id}/messages", cookie: k.CC);

        Assert.Equal(HttpStatusCode.OK, fromB.StatusCode);
        var m = (await AppFixture.Json(history)).GetProperty("messages")[0];
        Assert.Equal("chào cả nhà", m.GetProperty("body").GetString());
        Assert.Equal(k.B.Id, m.GetProperty("sender").GetProperty("id").GetGuid());
        Assert.DoesNotContain(k.B.GomId, m.ToString());   // C is not B's friend: no Gom ID anywhere
    }

    [Fact]
    public async Task Sending_in_a_group_needs_membership_not_friendship()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);
        using var unfriend = await app.Call(Delete, $"/api/friends/{k.B.GomId}", cookie: k.CA);   // A and B are no longer friends

        using var fromA = await Send(k.CA, id, "vẫn nhắn được");
        using var outsider = await Send(k.CD, id, "tôi ngoài nhóm");

        Assert.Equal(HttpStatusCode.OK, fromA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    // ---- adding people

    private Task<HttpResponseMessage> Add(string cookie, Guid chat, string gomId) =>
        app.Call(Post, $"/api/conversations/{chat}/members", new { gomId }, cookie);

    [Fact]
    public async Task Any_member_adds_one_of_their_own_friends()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);
        await app.Befriend(k.B, k.E);   // E is B's friend, not A's

        using var response = await Add(k.CB, id, k.E.GomId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(k.E.Id, await MemberIds(id));
        Assert.Equal(4, (await List(k.CA)).Single().GetProperty("memberCount").GetInt32());
    }

    [Fact]
    public async Task You_cannot_add_someone_who_is_not_your_friend_even_if_a_friend_of_yours_knows_them()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);
        await app.Befriend(k.B, k.E);

        using var byA = await Add(k.CA, id, k.E.GomId);          // E is B's friend, not A's
        using var unknown = await Add(k.CA, id, "nobody_at_all");

        Assert.Equal(HttpStatusCode.Forbidden, byA.StatusCode);
        Assert.Equal("not_friends", await ErrorOf(byA));
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.DoesNotContain(k.E.Id, await MemberIds(id));
    }

    [Fact]
    public async Task Adding_someone_who_is_already_in_is_a_409_and_a_full_group_is_a_409()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);
        var extras = new List<User>();
        for (var i = 0; i < 3; i++) { var u = await app.AddUser(); await app.Befriend(k.A, u); extras.Add(u); }

        using var again = await Add(k.CA, id, k.B.GomId);
        using var d = await Add(k.CA, id, k.D.GomId);       // now 4
        using var e1 = await Add(k.CA, id, extras[0].GomId); // now 5, the limit
        using var full = await Add(k.CA, id, extras[1].GomId);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_member", await ErrorOf(again));
        Assert.Equal(HttpStatusCode.NoContent, e1.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Equal("group_full", await ErrorOf(full));
    }

    [Fact]
    public async Task A_new_member_of_a_quiet_group_sees_no_last_message_and_nothing_unread()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);
        using var before = await Send(k.CA, id, "chuyện cũ");
        using var add = await Add(k.CA, id, k.D.GomId);

        var entry = (await List(k.CD)).Single();

        Assert.Equal(JsonValueKind.Null, entry.GetProperty("lastMessage").ValueKind);
        Assert.Equal(0, entry.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task Two_people_adding_at_once_cannot_push_a_group_over_the_limit()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k, k.B.GomId, k.C.GomId, k.D.GomId);   // 4 of 5
        var x = await app.AddUser(); var y = await app.AddUser();
        await app.Befriend(k.A, x); await app.Befriend(k.B, y);

        var both = await Task.WhenAll(Add(k.CA, id, x.GomId), Add(k.CB, id, y.GomId));

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], both.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(5, (await MemberIds(id)).Length);
    }

    [Fact]
    public async Task Adding_the_same_person_twice_at_once_adds_them_once()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);
        await app.Befriend(k.B, k.D);

        var both = await Task.WhenAll(Add(k.CA, id, k.D.GomId), Add(k.CB, id, k.D.GomId));

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], both.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(4, (await MemberIds(id)).Length);
    }

    [Fact]
    public async Task Only_a_member_can_add_and_only_to_a_group()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);
        var direct = await app.OpenChat(k.CA, k.D.GomId);
        await app.Befriend(k.E, k.D);

        using var byOutsider = await Add(k.CD, id, k.D.GomId);
        using var toDirect = await Add(k.CA, direct, k.B.GomId);
        using var anonymous = await app.Call(Post, $"/api/conversations/{id}/members", new { gomId = k.D.GomId });

        Assert.Equal(HttpStatusCode.NotFound, byOutsider.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, toDirect.StatusCode);
        Assert.Equal("not_a_group", await ErrorOf(toDirect));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_new_member_sees_only_what_is_said_after_they_join()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);
        using var before = await Send(k.CA, id, "trước khi D vào");
        using var add = await Add(k.CA, id, k.D.GomId);
        using var after = await Send(k.CB, id, "sau khi D vào");

        using var history = await app.Call(Get, $"/api/conversations/{id}/messages", cookie: k.CD);
        var bodies = (await AppFixture.Json(history)).GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("body").GetString()).ToArray();
        var entry = (await List(k.CD)).Single();

        Assert.Equal("sau khi D vào", Assert.Single(bodies));
        Assert.Equal("sau khi D vào", entry.GetProperty("lastMessage").GetProperty("body").GetString());
        Assert.Equal(1, entry.GetProperty("unread").GetInt32());   // not 2: the earlier one is not theirs to read
        using var olderPage = await app.Call(Get, $"/api/conversations/{id}/messages?before=2", cookie: k.CD);
        var older = await AppFixture.Json(olderPage);
        Assert.Empty(older.GetProperty("messages").EnumerateArray());
        Assert.False(older.GetProperty("hasMore").GetBoolean());
    }

    // ---- leaving

    [Fact]
    public async Task A_member_can_leave_and_then_neither_reads_nor_writes_there()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);

        using var leave = await app.Call(Delete, $"/api/conversations/{id}/members/me", cookie: k.CB);
        using var read = await app.Call(Get, $"/api/conversations/{id}/messages", cookie: k.CB);
        using var write = await Send(k.CB, id, "vẫn viết");
        using var leaveAgain = await app.Call(Delete, $"/api/conversations/{id}/members/me", cookie: k.CB);

        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, leaveAgain.StatusCode);
        Assert.Empty(await List(k.CB));
        Assert.Equal(2, (await List(k.CA)).Single().GetProperty("memberCount").GetInt32());
    }

    [Fact]
    public async Task One_to_one_conversations_cannot_be_left()
    {
        var k = await NewCrowd();
        var direct = await app.OpenChat(k.CA, k.D.GomId);
        using var leave = await app.Call(Delete, $"/api/conversations/{direct}/members/me", cookie: k.CA);
        Assert.Equal(HttpStatusCode.BadRequest, leave.StatusCode);
        Assert.Equal("not_a_group", await ErrorOf(leave));
        Assert.Contains(k.A.Id, await MemberIds(direct));
    }

    // ---- who is in it

    private async Task<JsonElement[]> Members(string cookie, Guid chat)
    {
        using var r = await app.Call(Get, $"/api/conversations/{chat}/members", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).EnumerateArray().ToArray();
    }

    private static JsonElement Find(JsonElement[] list, User u) => list.Single(m => m.GetProperty("id").GetGuid() == u.Id);

    [Fact]
    public async Task The_member_list_shows_a_Gom_ID_only_for_yourself_and_your_friends()
    {
        var k = await NewCrowd();
        await Rename(k.CC, "Bé Ba");
        var id = await MakeGroup(k);

        var forB = await Members(k.CB, id);

        Assert.Equal(3, forB.Length);
        Assert.Equal((true, k.B.GomId), (Find(forB, k.B).GetProperty("isMe").GetBoolean(), Find(forB, k.B).GetProperty("gomId").GetString()));
        Assert.Equal((true, k.A.GomId), (Find(forB, k.A).GetProperty("isFriend").GetBoolean(), Find(forB, k.A).GetProperty("gomId").GetString()));
        var c = Find(forB, k.C);
        Assert.False(c.GetProperty("isFriend").GetBoolean());
        Assert.False(c.TryGetProperty("gomId", out var shown) && shown.ValueKind != JsonValueKind.Null);
        Assert.Equal("Bé Ba", c.GetProperty("displayName").GetString());
        Assert.DoesNotContain(k.C.GomId, string.Join(' ', forB.Select(x => x.ToString())));
    }

    [Fact]
    public async Task The_member_list_is_for_members_only()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);
        using var outsider = await app.Call(Get, $"/api/conversations/{id}/members", cookie: k.CD);
        using var anonymous = await app.Call(Get, $"/api/conversations/{id}/members");
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ---- friend requests from inside a group

    private Task<HttpResponseMessage> AskFromGroup(string cookie, Guid chat, User other) =>
        app.Call(Post, $"/api/conversations/{chat}/members/{other.Id}/friend-request", cookie: cookie);

    [Fact]
    public async Task A_member_asks_another_member_to_be_friends_without_ever_seeing_their_ID()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);

        using var response = await AskFromGroup(k.CB, id, k.C);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pending", (await AppFixture.Json(response)).GetProperty("status").GetString());
        var cSeenByB = Find(await Members(k.CB, id), k.C);
        Assert.Equal("sent", cSeenByB.GetProperty("request").GetString());
        Assert.False(cSeenByB.GetProperty("isFriend").GetBoolean());                  // a request is not a friendship
        Assert.True(!cSeenByB.TryGetProperty("gomId", out var shown) || shown.ValueKind == JsonValueKind.Null);   // and does not reveal the ID
        Assert.Equal("received", Find(await Members(k.CC, id), k.B).GetProperty("request").GetString());
        Assert.Equal("none", Find(await Members(k.CA, id), k.B).GetProperty("request").GetString());
        using var accept = await app.Call(Post, $"/api/friends/requests/{k.B.GomId}/accept", cookie: k.CC);   // C finds B in the requests list
        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);
        Assert.Equal(k.C.GomId, Find(await Members(k.CB, id), k.C).GetProperty("gomId").GetString());   // now friends: the ID shows
    }

    [Fact]
    public async Task Asking_from_a_group_follows_the_usual_friend_rules()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);

        using var self = await AskFromGroup(k.CB, id, k.B);
        using var already = await AskFromGroup(k.CB, id, k.A);   // B and A are friends
        using var notInGroup = await AskFromGroup(k.CB, id, k.E);
        using var byOutsider = await AskFromGroup(k.CD, id, k.B);
        using var block = await app.Call(Post, $"/api/blocks/{k.C.GomId}", cookie: k.CB);
        using var blocked = await AskFromGroup(k.CB, id, k.C);

        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, already.StatusCode);
        Assert.Equal("already_friends", await ErrorOf(already));
        Assert.Equal(HttpStatusCode.NotFound, notInGroup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byOutsider.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("you_blocked_them", await ErrorOf(blocked));
    }

    [Fact]
    public async Task Someone_who_blocked_you_cannot_be_told_so_by_asking_from_a_group()
    {
        var k = await NewCrowd();
        var id = await MakeGroup(k);
        using var block = await app.Call(Post, $"/api/blocks/{k.B.GomId}", cookie: k.CC);   // C blocks B

        using var response = await AskFromGroup(k.CB, id, k.C);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pending", (await AppFixture.Json(response)).GetProperty("status").GetString());
        Assert.Equal("none", Find(await Members(k.CC, id), k.B).GetProperty("request").GetString());   // nothing was stored
    }
}
