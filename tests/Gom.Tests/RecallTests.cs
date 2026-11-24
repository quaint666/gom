using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class RecallTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get, Delete = HttpMethod.Delete;

    private sealed record Pair(User A, string CA, User B, string CB, Guid Chat);

    private async Task<Pair> NewPair()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return new Pair(a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private async Task<Guid> SendOne(string cookie, Guid chat, string body)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Recall(string cookie, Guid chat, Guid message, string? origin = AppFixture.Origin) =>
        app.Call(Delete, $"/api/conversations/{chat}/messages/{message}", cookie: cookie, origin: origin);

    private async Task<long> LastSeq(Guid chat)
    {
        await using var db = app.Db.NewContext();
        return await db.Conversations.Where(c => c.Id == chat).Select(c => c.LastSeq).SingleAsync();
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString();

    [Fact]
    public async Task The_sender_recalls_a_message_and_its_words_are_gone_from_the_database()
    {
        var p = await NewPair();
        var id = await SendOne(p.CA, p.Chat, "lỡ gửi nhầm");

        using var response = await Recall(p.CA, p.Chat, id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = app.Db.NewContext();
        var message = await db.Messages.SingleAsync(m => m.Id == id);
        Assert.Equal(("", 1L), (message.Body, message.Seq));   // the words are erased, the place in the order stays
        Assert.Equal(app.Clock.Now, message.DeletedAt);
        var ev = await db.Events.SingleAsync(e => e.ConversationId == p.Chat && e.Seq == 2);
        Assert.Equal((EventKind.Deleted, id), (ev.Kind, ev.MessageId));
        Assert.Equal(2L, await LastSeq(p.Chat));
    }

    [Fact]
    public async Task Recalling_twice_is_fine_and_makes_no_second_event()
    {
        var p = await NewPair();
        var id = await SendOne(p.CA, p.Chat, "một lần");
        using var first = await Recall(p.CA, p.Chat, id);

        using var again = await Recall(p.CA, p.Chat, id);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(2L, await LastSeq(p.Chat));
        await using var db = app.Db.NewContext();
        Assert.Equal(2, await db.Events.CountAsync(e => e.ConversationId == p.Chat));
    }

    [Fact]
    public async Task Ten_recalls_of_one_message_at_once_make_one_event()
    {
        var p = await NewPair();
        var id = await SendOne(p.CA, p.Chat, "đông người");

        var all = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Recall(p.CA, p.Chat, id)));

        Assert.All(all, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(2L, await LastSeq(p.Chat));
        await using var db = app.Db.NewContext();
        Assert.Equal(1, await db.Events.CountAsync(e => e.ConversationId == p.Chat && e.Kind == EventKind.Deleted));
    }

    [Fact]
    public async Task Nobody_recalls_someone_elses_message()
    {
        var p = await NewPair();
        var id = await SendOne(p.CA, p.Chat, "của A");

        using var byB = await Recall(p.CB, p.Chat, id);

        Assert.Equal(HttpStatusCode.Forbidden, byB.StatusCode);
        Assert.Equal("not_your_message", await ErrorOf(byB));
        await using var db = app.Db.NewContext();
        Assert.Null((await db.Messages.SingleAsync(m => m.Id == id)).DeletedAt);
        Assert.Equal(1L, await LastSeq(p.Chat));
    }

    [Fact]
    public async Task Strangers_unknown_messages_and_messages_from_other_conversations_are_a_404()
    {
        var p = await NewPair();
        var other = await NewPair();
        var id = await SendOne(p.CA, p.Chat, "ở đây");
        var elsewhere = await SendOne(other.CA, other.Chat, "ở kia");
        var (_, outsider) = await app.Member();

        using var byOutsider = await Recall(outsider, p.Chat, id);
        using var unknown = await Recall(p.CA, p.Chat, Guid.NewGuid());
        using var wrongChat = await Recall(p.CA, p.Chat, elsewhere);
        using var wrongOwner = await Recall(other.CA, p.Chat, id);

        Assert.All(new[] { byOutsider, unknown, wrongChat, wrongOwner }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(1L, await LastSeq(p.Chat));
    }

    [Fact]
    public async Task Recalling_needs_a_session_and_a_good_Origin()
    {
        var p = await NewPair();
        var id = await SendOne(p.CA, p.Chat, "x");
        using var anonymous = await app.Call(Delete, $"/api/conversations/{p.Chat}/messages/{id}");
        using var foreign = await Recall(p.CA, p.Chat, id, origin: "https://evil.test");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(1L, await LastSeq(p.Chat));
    }

    [Fact]
    public async Task You_can_still_take_back_your_own_words_after_an_unfriend_but_not_after_leaving_a_group()
    {
        var p = await NewPair();
        var id = await SendOne(p.CA, p.Chat, "rút lại");
        using var unfriend = await app.Call(Delete, $"/api/friends/{p.B.GomId}", cookie: p.CA);
        using var direct = await Recall(p.CA, p.Chat, id);
        Assert.Equal(HttpStatusCode.NoContent, direct.StatusCode);

        var (c, cc) = await app.Member();
        await app.Befriend(p.A, c);
        using var made = await app.Call(Post, "/api/conversations/group", new { title = "Nhóm", members = new[] { c.GomId } }, p.CA);
        var group = (await AppFixture.Json(made)).GetProperty("id").GetGuid();
        var inGroup = await SendOne(cc, group, "trong nhóm");
        using var leave = await app.Call(Delete, $"/api/conversations/{group}/members/me", cookie: cc);
        using var afterLeaving = await Recall(cc, group, inGroup);
        Assert.Equal(HttpStatusCode.NotFound, afterLeaving.StatusCode);
    }

    [Fact]
    public async Task A_message_from_before_you_rejoined_a_group_is_out_of_sight_and_so_out_of_reach()
    {
        var p = await NewPair();
        var (c, cc) = await app.Member();
        await app.Befriend(p.A, c);
        using var made = await app.Call(Post, "/api/conversations/group", new { title = "Nhóm", members = new[] { c.GomId } }, p.CA);
        var group = (await AppFixture.Json(made)).GetProperty("id").GetGuid();
        var old = await SendOne(cc, group, "từ trước khi tôi rời");
        using var leave = await app.Call(Delete, $"/api/conversations/{group}/members/me", cookie: cc);
        using var back = await app.Call(Post, $"/api/conversations/{group}/members", new { gomId = c.GomId }, p.CA);

        using var response = await Recall(cc, group, old);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task History_shows_a_recalled_message_as_a_placeholder_without_its_words()
    {
        var p = await NewPair();
        var id = await SendOne(p.CA, p.Chat, "bí mật");
        using var keep = await app.Call(Post, $"/api/conversations/{p.Chat}/messages", new { clientId = Guid.NewGuid(), body = "ở lại" }, p.CB);
        using var recall = await Recall(p.CA, p.Chat, id);

        using var history = await app.Call(Get, $"/api/conversations/{p.Chat}/messages", cookie: p.CB);
        var body = await history.Content.ReadAsStringAsync();
        var messages = JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().ToArray();

        Assert.DoesNotContain("bí mật", body);
        var recalled = messages.Single(m => m.GetProperty("id").GetGuid() == id);
        Assert.True(recalled.GetProperty("deleted").GetBoolean());
        Assert.Equal("", recalled.GetProperty("body").GetString());
        Assert.Equal(1, recalled.GetProperty("seq").GetInt64());
        Assert.False(messages.Single(m => m.GetProperty("id").GetGuid() != id).GetProperty("deleted").GetBoolean());
        Assert.Equal(3L, JsonDocument.Parse(body).RootElement.GetProperty("lastSeq").GetInt64());   // the cursor counts the recall too
    }

    [Fact]
    public async Task The_list_marks_a_recalled_last_message_and_does_not_count_it_as_unread()
    {
        var p = await NewPair();
        var id = await SendOne(p.CA, p.Chat, "sẽ rút");
        using var recall = await Recall(p.CA, p.Chat, id);

        using var list = await app.Call(Get, "/api/conversations", cookie: p.CB);
        var entry = (await AppFixture.Json(list)).EnumerateArray().Single();

        Assert.True(entry.GetProperty("lastMessage").GetProperty("deleted").GetBoolean());
        Assert.Equal("", entry.GetProperty("lastMessage").GetProperty("body").GetString());
        Assert.Equal(0, entry.GetProperty("unread").GetInt32());
        Assert.Equal(2L, entry.GetProperty("lastSeq").GetInt64());
    }

    [Fact]
    public async Task Sending_goes_on_after_a_recall_with_the_next_number()
    {
        var p = await NewPair();
        var id = await SendOne(p.CA, p.Chat, "một");
        using var recall = await Recall(p.CA, p.Chat, id);

        using var next = await app.Call(Post, $"/api/conversations/{p.Chat}/messages", new { clientId = Guid.NewGuid(), body = "hai" }, p.CA);

        Assert.Equal(3L, (await AppFixture.Json(next)).GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task Recalls_and_sends_at_once_still_get_consecutive_numbers()
    {
        var p = await NewPair();
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) ids.Add(await SendOne(p.CA, p.Chat, $"m{i}"));

        var tasks = ids.Select(id => Recall(p.CA, p.Chat, id)).Concat(Enumerable.Range(0, 6).Select(i =>
            app.Call(Post, $"/api/conversations/{p.Chat}/messages", new { clientId = Guid.NewGuid(), body = $"n{i}" }, p.CB))).ToList();
        var done = await Task.WhenAll(tasks);

        Assert.All(done, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        await using var db = app.Db.NewContext();
        Assert.Equal(Enumerable.Range(1, 18).Select(i => (long)i), await db.Events.Where(e => e.ConversationId == p.Chat).OrderBy(e => e.Seq).Select(e => e.Seq).ToListAsync());
        Assert.Equal(18L, await LastSeq(p.Chat));
    }
}
