using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class ConversationTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get;

    private Task<HttpResponseMessage> Open(string cookie, string gomId) =>
        app.Call(Post, "/api/conversations/direct", new { gomId }, cookie);

    private async Task<JsonElement[]> List(string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).EnumerateArray().ToArray();
    }

    private static Guid IdOf(JsonElement j) => j.GetProperty("id").GetGuid();

    [Fact]
    public async Task Friends_open_a_one_to_one_conversation_and_both_are_members()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);

        using var response = await Open(ca, b.GomId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = IdOf(await AppFixture.Json(response));
        await using var db = app.Db.NewContext();
        var chat = await db.Conversations.SingleAsync(c => c.Id == id);
        Assert.Equal(ConversationKind.Direct, chat.Kind);
        Assert.Equal(0, chat.LastSeq);
        Assert.Equal(a.Id, chat.CreatedBy);
        Assert.Equivalent(new[] { a.Id, b.Id }, await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync());
    }

    [Fact]
    public async Task Opening_it_again_from_either_side_gives_the_same_conversation()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);

        using var first = await Open(ca, b.GomId);
        using var second = await Open(ca, b.GomId);
        using var fromOther = await Open(cb, a.GomId);

        var id = IdOf(await AppFixture.Json(first));
        Assert.Equal(id, IdOf(await AppFixture.Json(second)));
        Assert.Equal(id, IdOf(await AppFixture.Json(fromOther)));
        Assert.Single(await List(ca));
    }

    [Fact]
    public async Task Two_friends_opening_it_at_the_same_moment_get_one_conversation()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);

        var both = await Task.WhenAll(Open(ca, b.GomId), Open(cb, a.GomId));

        Assert.All(both, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = (await Task.WhenAll(both.Select(AppFixture.Json))).Select(IdOf).Distinct().ToList();
        Assert.Single(ids);
        await using var db = app.Db.NewContext();
        Assert.Equal(2, await db.Members.CountAsync(m => m.ConversationId == ids[0]));
        Assert.Single(await List(ca));
    }

    [Fact]
    public async Task Strangers_and_pending_requests_cannot_start_a_conversation()
    {
        var (_, ca) = await app.Member();
        var (stranger, _) = await app.Member();
        var (asked, _) = await app.Member();
        using var ask = await app.Call(Post, "/api/friends/requests", new { gomId = asked.GomId }, ca);

        using var noRelation = await Open(ca, stranger.GomId);
        using var pending = await Open(ca, asked.GomId);

        Assert.Equal(HttpStatusCode.Forbidden, noRelation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, pending.StatusCode);
        Assert.Equal("not_friends", (await AppFixture.Json(noRelation)).GetProperty("error").GetString());
        Assert.Empty(await List(ca));
    }

    [Fact]
    public async Task Nobody_starts_a_conversation_with_themself_or_with_someone_unknown_or_locked()
    {
        var (a, ca) = await app.Member();
        var locked = await app.AddUser(disabled: true);

        using var self = await Open(ca, a.GomId);
        using var unknown = await Open(ca, "nobody_at_all");
        using var disabled = await Open(ca, locked.GomId);

        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
    }

    [Fact]
    public async Task After_a_block_no_new_conversation_can_be_started_in_either_direction()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        using var block = await app.Call(Post, $"/api/blocks/{b.GomId}", cookie: ca);

        using var byBlocker = await Open(ca, b.GomId);
        using var byBlocked = await Open(cb, a.GomId);

        Assert.Equal(HttpStatusCode.Forbidden, byBlocker.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byBlocked.StatusCode);
    }

    [Fact]
    public async Task An_old_conversation_stays_in_the_list_after_unfriending_but_cannot_be_reopened_as_new()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        using var open = await Open(ca, b.GomId);
        using var unfriend = await app.Call(HttpMethod.Delete, $"/api/friends/{b.GomId}", cookie: ca);

        Assert.Single(await List(ca));
        Assert.Single(await List(cb));
        using var again = await Open(ca, b.GomId);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    public async Task The_list_is_only_my_conversations_and_names_the_other_person_as_they_are_now()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        var (c, cc) = await app.Member();
        await app.Befriend(a, b);
        await app.Befriend(b, c);
        using var ab = await Open(ca, b.GomId);
        using var bc = await Open(cb, c.GomId);
        using var rename = await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "Bé Ba" }, cb);

        var mine = await List(ca);

        var chat = Assert.Single(mine);
        Assert.Equal(IdOf(await AppFixture.Json(ab)), IdOf(chat));
        Assert.Equal("direct", chat.GetProperty("kind").GetString());
        Assert.Equal("Bé Ba", chat.GetProperty("title").GetString());
        Assert.Equal(b.GomId, chat.GetProperty("peer").GetProperty("gomId").GetString());
        Assert.Equal(0, chat.GetProperty("lastSeq").GetInt64());
        Assert.Equal(0, chat.GetProperty("lastReadSeq").GetInt64());
        Assert.Equal(2, (await List(cb)).Length);
    }

    [Fact]
    public async Task The_list_gives_each_person_their_own_read_position()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        using var open = await Open(ca, b.GomId);
        var id = IdOf(await AppFixture.Json(open));
        await using (var db = app.Db.NewContext())
        {
            await db.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeq, 5L));
            await db.Members.Where(m => m.ConversationId == id && m.UserId == a.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, 2L));
        }

        var mine = Assert.Single(await List(ca));
        var theirs = Assert.Single(await List(cb));

        Assert.Equal((5L, 2L), (mine.GetProperty("lastSeq").GetInt64(), mine.GetProperty("lastReadSeq").GetInt64()));
        Assert.Equal((5L, 0L), (theirs.GetProperty("lastSeq").GetInt64(), theirs.GetProperty("lastReadSeq").GetInt64()));
    }

    [Fact]
    public async Task Both_calls_need_a_session_and_the_post_needs_a_good_Origin()
    {
        var (_, ca) = await app.Member();
        var (b, _) = await app.Member();

        using var list = await app.Call(Get, "/api/conversations");
        using var anonymous = await app.Call(Post, "/api/conversations/direct", new { gomId = b.GomId });
        using var foreign = await app.Call(Post, "/api/conversations/direct", new { gomId = b.GomId }, ca, origin: "https://evil.test");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }
}
