using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>Changing the words of your own message.</summary>
public class EditTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put, Delete = HttpMethod.Delete, Patch = HttpMethod.Patch;

    private sealed record Team(User A, string CA, User B, string CB, User E, string CE, Guid Chat);

    private async Task<Team> NewTeam()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (e, ce) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, e);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId } }, ca);
        return new Team(a, ca, b, cb, e, ce, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private async Task<Guid> Say(Team t, string cookie, string body, Guid? replyTo = null)
    {
        using var r = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid(), body, replyTo }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Edit(Team t, string cookie, Guid m, string? body) => app.Call(Patch, $"/api/conversations/{t.Chat}/messages/{m}", new { body }, cookie);

    private async Task<JsonElement> Seen(Team t, string cookie, Guid m)
    {
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/messages", cookie: cookie);
        return (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == m);
    }

    private async Task<long> LastSeq(Team t)
    {
        await using var db = app.Db.NewContext();
        return await db.Conversations.Where(c => c.Id == t.Chat).Select(c => c.LastSeq).SingleAsync();
    }

    [Fact]
    public async Task The_sender_changes_the_words_and_everyone_sees_them_with_a_mark_that_it_was_edited()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tối nay ăn phỏ");
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        using var r = await Edit(t, t.CA, m, "  tối nay ăn phở  ");
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        foreach (var cookie in new[] { t.CA, t.CB })
        {
            var seen = await Seen(t, cookie, m);
            Assert.Equal("tối nay ăn phở", seen.GetProperty("body").GetString());
            Assert.Equal(app.Clock.Now, seen.GetProperty("editedAt").GetDateTimeOffset());
        }
    }

    [Fact]
    public async Task A_message_never_edited_has_no_mark()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "nguyên văn");
        var seen = await Seen(t, t.CB, m);
        Assert.True(!seen.TryGetProperty("editedAt", out var e) || e.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task An_edit_keeps_the_place_of_the_message_and_is_not_new_to_anyone()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "một");
        await Say(t, t.CA, "hai");
        using var mark = await app.Call(Post, $"/api/conversations/{t.Chat}/read", new { seq = 2 }, t.CB);
        (await Edit(t, t.CA, m, "một, đã sửa")).Dispose();
        using var list = await app.Call(Get, "/api/conversations", cookie: t.CB);
        var mine = (await AppFixture.Json(list)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == t.Chat);
        Assert.Equal(0, mine.GetProperty("unread").GetInt32());
        Assert.Equal(1, (await Seen(t, t.CB, m)).GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task Writing_the_same_words_changes_nothing_and_takes_no_number()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "y nguyên");
        var before = await LastSeq(t);
        using var r = await Edit(t, t.CA, m, "  y nguyên ");
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(before, await LastSeq(t));
        Assert.True(!(await Seen(t, t.CA, m)).TryGetProperty("editedAt", out var e) || e.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Only_the_sender_may_edit_even_the_leader()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CB, "của B");
        using var byLeader = await Edit(t, t.CA, m, "đổi");
        Assert.Equal(HttpStatusCode.Forbidden, byLeader.StatusCode);
        Assert.Equal("not_your_message", (await AppFixture.Json(byLeader)).GetProperty("error").GetString());
        Assert.Equal("của B", (await Seen(t, t.CB, m)).GetProperty("body").GetString());
    }

    [Fact]
    public async Task What_was_taken_back_cannot_be_edited_and_what_was_edited_can_still_be_taken_back()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "một");
        (await Edit(t, t.CA, m, "hai")).Dispose();
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{m}", cookie: t.CA)).Dispose();
        using var r = await Edit(t, t.CA, m, "ba");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var seen = await Seen(t, t.CB, m);
        Assert.True(seen.GetProperty("deleted").GetBoolean());
        Assert.Equal("", seen.GetProperty("body").GetString());
        Assert.True(!seen.TryGetProperty("editedAt", out var e) || e.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task What_you_cannot_see_cannot_be_edited_and_an_outsider_learns_nothing()
    {
        var t = await NewTeam();
        var before = await Say(t, t.CA, "trước khi E vào");
        (await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = t.E.GomId }, t.CA)).Dispose();
        var (_, stranger) = await app.Member();
        using var early = await Edit(t, t.CE, before, "x");
        using var outsider = await Edit(t, stranger, before, "x");
        using var unknown = await Edit(t, t.CA, Guid.NewGuid(), "x");
        foreach (var r in new[] { early, outsider, unknown }) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task A_message_of_another_conversation_cannot_be_edited_through_this_one_even_by_its_writer()
    {
        var t = await NewTeam();
        var mine = await Say(t, t.CB, "của B trong nhóm");
        // B leaves the group, then reaches for the old message through a conversation B is still in.
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/members/me", cookie: t.CB)).Dispose();
        var other = await app.OpenChat(t.CA, t.E.GomId);
        using var r = await app.Call(Patch, $"/api/conversations/{other}/messages/{mine}", new { body = "sửa lén" }, t.CA);
        using var viaOwn = await app.Call(Patch, $"/api/conversations/{t.Chat}/messages/{mine}", new { body = "sửa lén" }, t.CB);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viaOwn.StatusCode);
        Assert.Equal("của B trong nhóm", (await Seen(t, t.CA, mine)).GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(null, "empty_message")]
    [InlineData("", "empty_message")]
    [InlineData("   ", "empty_message")]
    [InlineData("chuông\u0007", "invalid_message")]
    public async Task The_new_words_must_be_there_and_clean(string? body, string code)
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "gốc");
        using var r = await Edit(t, t.CA, m, body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(code, (await AppFixture.Json(r)).GetProperty("error").GetString());
        Assert.Equal("gốc", (await Seen(t, t.CA, m)).GetProperty("body").GetString());
    }

    [Fact]
    public async Task The_new_words_may_not_be_longer_than_a_message_may_be()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "gốc");
        using var r = await Edit(t, t.CA, m, new string('a', 4001));
        Assert.Equal("message_too_long", (await AppFixture.Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Editing_over_and_over_runs_into_the_same_limit_as_writing()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "gốc");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 30; i++) { using var r = await Edit(t, t.CA, m, $"lần {i}"); codes.Add(r.StatusCode); }
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    [Fact]
    public async Task A_one_to_one_chat_ended_by_unfriending_takes_no_edits()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        using var sent = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "gốc" }, ca);
        var m = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();
        (await app.Call(Delete, $"/api/friends/{b.GomId}", cookie: ca)).Dispose();
        using var r = await app.Call(Patch, $"/api/conversations/{chat}/messages/{m}", new { body = "mới" }, ca);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Two_edits_at_the_same_moment_leave_one_of_them_and_no_error()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "gốc");
        var calls = await Task.WhenAll(new[] { "một", "hai", "ba" }.Select(w => Edit(t, t.CA, m, w)));
        Assert.All(calls, c => Assert.Equal(HttpStatusCode.NoContent, c.StatusCode));
        Assert.Contains((await Seen(t, t.CB, m)).GetProperty("body").GetString(), new[] { "một", "hai", "ba" });
        foreach (var c in calls) c.Dispose();
    }

    [Fact]
    public async Task Everyone_is_told_live_and_a_device_catching_up_gets_the_new_words()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "cũ");
        using var wsB = await Connect(t.CB);
        (await Edit(t, t.CA, m, "mới")).Dispose();
        var live = (await NextOfType(wsB, "event")).GetProperty("event");
        Assert.Equal("updated", live.GetProperty("kind").GetString());
        Assert.Equal("mới", live.GetProperty("message").GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.String, live.GetProperty("message").GetProperty("editedAt").ValueKind);
        using var late = await Connect(t.CB);
        await Send(late, "{\"t\":\"sync\",\"c\":\"" + t.Chat + "\",\"after\":0}");
        var bodies = (await NextOfType(late, "events")).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("message").GetProperty("body").GetString()).ToArray();
        Assert.All(bodies, b => Assert.Equal("mới", b));
    }

    [Fact]
    public async Task A_quote_of_an_edited_message_shows_the_new_words_and_search_and_pins_follow()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tên cũ");
        var answer = await Say(t, t.CB, "đáp", m);
        (await app.Call(Put, $"/api/conversations/{t.Chat}/messages/{m}/pin", cookie: t.CB)).Dispose();
        (await Edit(t, t.CA, m, "tên mới")).Dispose();
        var quote = (await Seen(t, t.CB, answer)).GetProperty("reply").GetProperty("snippet").GetString();
        Assert.Equal("tên mới", quote);
        using var pins = await app.Call(Get, $"/api/conversations/{t.Chat}/pins", cookie: t.CB);
        Assert.Equal("tên mới", (await AppFixture.Json(pins)).GetProperty("pins")[0].GetProperty("body").GetString());
        using var old = await app.Call(Get, $"/api/conversations/{t.Chat}/search?q=c%E1%BB%A5", cookie: t.CB);
        using var fresh = await app.Call(Get, $"/api/conversations/{t.Chat}/search?q=m%E1%BB%9Bi", cookie: t.CB);
        Assert.Empty((await AppFixture.Json(old)).GetProperty("results").EnumerateArray());
        Assert.Single((await AppFixture.Json(fresh)).GetProperty("results").EnumerateArray());
    }

    private async Task<WebSocket> Connect(string cookie)
    {
        var client = app.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => { r.Headers["Cookie"] = $"__Host-gom={cookie}"; r.Headers["Origin"] = AppFixture.Origin; };
        var ws = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        Assert.Equal("ready", (await Next(ws)).GetProperty("t").GetString());
        return ws;
    }

    private static Task Send(WebSocket ws, string text) => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> Next(WebSocket ws, TimeSpan? within = null)
    {
        using var cts = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(5));
        var buffer = new byte[65536];
        var r = await ws.ReceiveAsync(buffer, cts.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)).RootElement.Clone();
    }

    private static async Task<JsonElement> NextOfType(WebSocket ws, string type)
    {
        while (true)
        {
            var f = await Next(ws);
            if (f.GetProperty("t").GetString() == type) return f;
        }
    }
}
