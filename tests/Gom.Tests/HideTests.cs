using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>"Delete for me": one person stops seeing a message; nobody else's view changes.</summary>
public class HideTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Put = HttpMethod.Put, Get = HttpMethod.Get, Post = HttpMethod.Post, Delete = HttpMethod.Delete;

    private sealed record Team(User A, string CA, User B, string CB, User C, string CC, User E, string CE, Guid Chat);

    private async Task<Team> NewTeam()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member(); var (e, ce) = await app.Member();
        foreach (var x in new[] { b, c, e }) await app.Befriend(a, x);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId, c.GomId } }, ca);
        return new Team(a, ca, b, cb, c, cc, e, ce, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private async Task<Guid> Say(Team t, string cookie, string body, Guid? replyTo = null)
    {
        using var r = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid(), body, replyTo }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Hide(Team t, string cookie, Guid message) => app.Call(Put, $"/api/conversations/{t.Chat}/messages/{message}/hidden", cookie: cookie);

    private async Task<JsonElement[]> History(Team t, string cookie)
    {
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/messages", cookie: cookie);
        return (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().OrderBy(m => m.GetProperty("seq").GetInt64()).ToArray();
    }

    private async Task<string[]> Bodies(Team t, string cookie) => (await History(t, cookie)).Select(m => m.GetProperty("body").GetString()!).ToArray();

    private async Task<JsonElement> Listed(Team t, string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == t.Chat);
    }

    [Fact]
    public async Task A_hidden_message_is_gone_from_my_history_and_still_there_for_everyone_else()
    {
        var t = await NewTeam();
        await Say(t, t.CB, "một");
        var two = await Say(t, t.CB, "hai");
        await Say(t, t.CC, "ba");

        using var r = await Hide(t, t.CA, two);

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(new[] { "một", "ba" }, await Bodies(t, t.CA));
        Assert.Equal(new[] { "một", "hai", "ba" }, await Bodies(t, t.CB));
        Assert.Equal(new[] { "một", "hai", "ba" }, await Bodies(t, t.CC));
    }

    [Fact]
    public async Task You_can_hide_your_own_message_and_a_message_taken_back()
    {
        var t = await NewTeam();
        var mine = await Say(t, t.CA, "của tôi");
        var gone = await Say(t, t.CB, "sẽ thu hồi");
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{gone}", cookie: t.CB)).Dispose();

        using var one = await Hide(t, t.CA, mine);
        using var two = await Hide(t, t.CA, gone);

        Assert.Equal(HttpStatusCode.NoContent, one.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, two.StatusCode);
        Assert.Empty(await History(t, t.CA));
    }

    [Fact]
    public async Task Hiding_twice_is_fine()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CB, "một");
        (await Hide(t, t.CA, one)).Dispose();
        using var again = await Hide(t, t.CA, one);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.Equal(1, await db.Hides.CountAsync(h => h.MessageId == one));
    }

    [Fact]
    public async Task Only_what_you_can_see_can_be_hidden()
    {
        var t = await NewTeam();
        var before = await Say(t, t.CA, "trước khi E vào");
        (await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = t.E.GomId }, t.CA)).Dispose();
        var other = await app.OpenChat(t.CA, t.E.GomId);
        using var oM = await app.Call(Post, $"/api/conversations/{other}/messages", new { clientId = Guid.NewGuid(), body = "chuyện riêng" }, t.CA);
        var elsewhere = (await AppFixture.Json(oM)).GetProperty("id").GetGuid();

        using var early = await Hide(t, t.CE, before);
        using var unknown = await Hide(t, t.CA, Guid.NewGuid());
        using var wrongChat = await Hide(t, t.CA, elsewhere);
        var (_, stranger) = await app.Member();
        using var outsider = await Hide(t, stranger, before);

        Assert.Equal(HttpStatusCode.NotFound, early.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongChat.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    [Fact]
    public async Task The_list_shows_the_last_message_you_have_not_hidden_and_does_not_count_a_hidden_one_as_unread()
    {
        var t = await NewTeam();
        await Say(t, t.CB, "một");
        var two = await Say(t, t.CB, "hai");
        Assert.Equal(2, (await Listed(t, t.CA)).GetProperty("unread").GetInt32());

        (await Hide(t, t.CA, two)).Dispose();

        var listed = await Listed(t, t.CA);
        Assert.Equal("một", listed.GetProperty("lastMessage").GetProperty("body").GetString());
        Assert.Equal(1, listed.GetProperty("unread").GetInt32());
        Assert.Equal("hai", (await Listed(t, t.CB)).GetProperty("lastMessage").GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_quote_of_a_message_you_hid_shows_nothing_of_it_but_others_still_see_it()
    {
        var t = await NewTeam();
        var first = await Say(t, t.CB, "gốc");
        var answer = await Say(t, t.CC, "đáp", first);
        (await Hide(t, t.CA, first)).Dispose();

        var mine = (await History(t, t.CA)).Single(m => m.GetProperty("id").GetGuid() == answer).GetProperty("reply");
        var theirs = (await History(t, t.CB)).Single(m => m.GetProperty("id").GetGuid() == answer).GetProperty("reply");

        Assert.Equal(JsonValueKind.Null, mine.GetProperty("snippet").ValueKind);
        Assert.Equal("gốc", theirs.GetProperty("snippet").GetString());
    }

    [Fact]
    public async Task A_device_catching_up_is_not_given_what_you_hid()
    {
        var t = await NewTeam();
        await Say(t, t.CB, "một");
        var two = await Say(t, t.CB, "hai");
        (await Hide(t, t.CA, two)).Dispose();

        using var ws = await Connect(t.CA);
        await Send(ws, $$"""{"t":"sync","c":"{{t.Chat}}","after":0}""");
        var events = (await NextOfType(ws, "events")).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("message").GetProperty("body").GetString()).ToArray();

        Assert.Equal(new[] { "một" }, events);
    }

    [Fact]
    public async Task Your_own_devices_are_told_to_drop_it_and_nobody_else_is()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CB, "một");
        using var phone = await Connect(t.CA);
        using var laptop = await Connect(t.CA);
        using var other = await Connect(t.CB);

        (await Hide(t, t.CA, one)).Dispose();

        foreach (var ws in new[] { phone, laptop })
        {
            var frame = await NextOfType(ws, "hidden");
            Assert.Equal(t.Chat, frame.GetProperty("c").GetGuid());
            Assert.Equal(one, frame.GetProperty("m").GetGuid());
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Next(other, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task Ending_a_group_works_when_people_have_hidden_messages()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CB, "một");
        (await Hide(t, t.CC, one)).Dispose();

        using var r = await app.Call(Delete, $"/api/conversations/{t.Chat}", cookie: t.CA);

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Hides.AnyAsync(h => h.MessageId == one));
    }

    // ---- sockets

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
            var frame = await Next(ws);
            if (frame.GetProperty("t").GetString() == type) return frame;
        }
    }
}
