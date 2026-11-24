using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>Messages pinned to the top of a conversation, for everyone in it.</summary>
public class PinTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put, Delete = HttpMethod.Delete;

    private sealed record Team(User A, string CA, User B, string CB, User E, string CE, Guid Chat);

    private async Task<Team> NewTeam()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (e, ce) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, e);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId } }, ca);
        return new Team(a, ca, b, cb, e, ce, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private async Task<Guid> Say(Team t, string cookie, string body)
    {
        using var r = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Pin(Team t, string cookie, Guid m) => app.Call(Put, $"/api/conversations/{t.Chat}/messages/{m}/pin", cookie: cookie);
    private Task<HttpResponseMessage> Unpin(Team t, string cookie, Guid m) => app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{m}/pin", cookie: cookie);

    private async Task<string[]> Pins(Team t, string cookie)
    {
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/pins", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("pins").EnumerateArray().Select(m => m.GetProperty("body").GetString()!).ToArray();
    }

    [Fact]
    public async Task Any_member_pins_and_everyone_sees_it_newest_pin_first()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CA, "một"); var two = await Say(t, t.CB, "hai");
        (await Pin(t, t.CB, one)).Dispose();
        app.Clock.Advance(TimeSpan.FromSeconds(5));   // the fake clock stands still unless moved
        (await Pin(t, t.CA, two)).Dispose();
        Assert.Equal(new[] { "hai", "một" }, await Pins(t, t.CA));
        Assert.Equal(new[] { "hai", "một" }, await Pins(t, t.CB));
    }

    [Fact]
    public async Task Pinning_twice_changes_nothing_and_unpinning_takes_it_away()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CA, "một");
        using var first = await Pin(t, t.CA, one);
        using var again = await Pin(t, t.CB, one);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Single(await Pins(t, t.CA));
        using var gone = await Unpin(t, t.CB, one);
        using var goneAgain = await Unpin(t, t.CB, one);
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, goneAgain.StatusCode);
        Assert.Empty(await Pins(t, t.CA));
    }

    [Fact]
    public async Task At_most_three_can_be_pinned_and_a_fourth_is_a_409()
    {
        var t = await NewTeam();
        var ids = new List<Guid>();
        for (var i = 1; i <= 4; i++) ids.Add(await Say(t, i % 2 == 0 ? t.CA : t.CB, $"tin {i}"));
        for (var i = 0; i < 3; i++) (await Pin(t, t.CA, ids[i])).Dispose();
        using var fourth = await Pin(t, t.CA, ids[3]);
        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
        Assert.Equal("pin_limit", (await AppFixture.Json(fourth)).GetProperty("error").GetString());
        (await Unpin(t, t.CA, ids[0])).Dispose();
        using var now = await Pin(t, t.CA, ids[3]);
        Assert.Equal(HttpStatusCode.NoContent, now.StatusCode);
    }

    [Fact]
    public async Task Pinning_at_the_same_moment_cannot_go_over_the_limit()
    {
        var t = await NewTeam();
        var ids = new List<Guid>();
        for (var i = 1; i <= 6; i++) ids.Add(await Say(t, i % 2 == 0 ? t.CA : t.CB, $"tin {i}"));
        var calls = await Task.WhenAll(ids.Select(m => Pin(t, t.CA, m)));
        Assert.Equal(3, calls.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(3, calls.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(3, (await Pins(t, t.CA)).Length);
        foreach (var r in calls) r.Dispose();
    }

    [Fact]
    public async Task A_message_taken_back_is_unpinned()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CB, "sẽ thu hồi");
        (await Pin(t, t.CA, one)).Dispose();
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{one}", cookie: t.CB)).Dispose();
        Assert.Empty(await Pins(t, t.CA));
        await using var db = app.Db.NewContext();
        Assert.False(await db.Pins.AnyAsync(p => p.MessageId == one));
    }

    [Fact]
    public async Task A_message_taken_back_cannot_be_pinned()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CB, "đã thu hồi");
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{one}", cookie: t.CB)).Dispose();
        using var r = await Pin(t, t.CA, one);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task What_you_deleted_for_yourself_is_not_shown_to_you_among_the_pins_but_still_is_to_others()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CB, "tin ghim");
        (await Pin(t, t.CA, one)).Dispose();
        (await app.Call(Put, $"/api/conversations/{t.Chat}/messages/{one}/hidden", cookie: t.CA)).Dispose();
        Assert.Empty(await Pins(t, t.CA));
        Assert.Single(await Pins(t, t.CB));
        using var again = await Pin(t, t.CA, one);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_member_who_joined_later_is_not_shown_a_pin_of_what_was_said_before()
    {
        var t = await NewTeam();
        var old = await Say(t, t.CA, "chuyện cũ");
        (await Pin(t, t.CA, old)).Dispose();
        (await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = t.E.GomId }, t.CA)).Dispose();
        Assert.Empty(await Pins(t, t.CE));
        using var r = await Pin(t, t.CE, old);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Someone_outside_the_conversation_cannot_pin_unpin_or_read_the_pins()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CA, "một");
        var (_, stranger) = await app.Member();
        using var a = await Pin(t, stranger, one);
        using var b = await Unpin(t, stranger, one);
        using var c = await app.Call(Get, $"/api/conversations/{t.Chat}/pins", cookie: stranger);
        Assert.Equal(HttpStatusCode.NotFound, a.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, b.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, c.StatusCode);
    }

    [Fact]
    public async Task A_message_of_another_conversation_cannot_be_pinned_here()
    {
        var t = await NewTeam();
        var other = await app.OpenChat(t.CA, t.E.GomId);
        using var m = await app.Call(Post, $"/api/conversations/{other}/messages", new { clientId = Guid.NewGuid(), body = "chuyện riêng" }, t.CA);
        var elsewhere = (await AppFixture.Json(m)).GetProperty("id").GetGuid();
        using var r = await Pin(t, t.CA, elsewhere);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Ending_a_group_works_when_it_has_pins()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CA, "một");
        (await Pin(t, t.CA, one)).Dispose();
        using var r = await app.Call(Delete, $"/api/conversations/{t.Chat}", cookie: t.CA);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Pins.AnyAsync(p => p.ConversationId == t.Chat));
    }

    [Fact]
    public async Task Everyone_in_the_conversation_is_told_when_the_pins_change_and_nobody_else()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CA, "một");
        await app.OpenChat(t.CA, t.E.GomId);   // E is in another conversation, so a frame sent to "everyone" would reach E
        using var wsB = await Connect(t.CB);
        using var wsE = await Connect(t.CE);
        (await Pin(t, t.CA, one)).Dispose();
        var frame = await NextOfType(wsB, "pins");
        Assert.Equal(t.Chat, frame.GetProperty("c").GetGuid());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Next(wsE, TimeSpan.FromMilliseconds(400)));
        (await Unpin(t, t.CA, one)).Dispose();
        Assert.Equal("pins", (await NextOfType(wsB, "pins")).GetProperty("t").GetString());
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

    private static async Task<JsonElement> NextOfType(WebSocket ws, string type)
    {
        while (true)
        {
            var f = await Next(ws);
            if (f.GetProperty("t").GetString() == type) return f;
        }
    }
}
