using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>Muting is one person's own switch on one conversation.</summary>
public class MuteTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Put = HttpMethod.Put, Get = HttpMethod.Get, Post = HttpMethod.Post;

    private async Task<(User A, string CA, User B, string CB, Guid Chat)> Pair()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return (a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private Task<HttpResponseMessage> Mute(string cookie, Guid chat, object? body) => app.Call(Put, $"/api/conversations/{chat}/mute", body, cookie);

    private async Task<JsonElement> Mine(string cookie, Guid chat)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chat);
    }

    [Fact]
    public async Task A_conversation_starts_unmuted()
    {
        var p = await Pair();
        Assert.False((await Mine(p.CA, p.Chat)).GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task Muting_is_yours_alone_and_unmuting_undoes_it()
    {
        var p = await Pair();
        using var on = await Mute(p.CA, p.Chat, new { muted = true });
        Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);
        Assert.True((await Mine(p.CA, p.Chat)).GetProperty("muted").GetBoolean());
        Assert.False((await Mine(p.CB, p.Chat)).GetProperty("muted").GetBoolean());

        using var off = await Mute(p.CA, p.Chat, new { muted = false });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.False((await Mine(p.CA, p.Chat)).GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task A_muted_conversation_still_counts_what_is_unread()
    {
        var p = await Pair();
        (await Mute(p.CA, p.Chat, new { muted = true })).Dispose();
        (await app.Call(Post, $"/api/conversations/{p.Chat}/messages", new { clientId = Guid.NewGuid(), body = "xin chào" }, p.CB)).Dispose();
        var mine = await Mine(p.CA, p.Chat);
        Assert.True(mine.GetProperty("muted").GetBoolean());
        Assert.Equal(1, mine.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task Someone_outside_the_conversation_cannot_mute_it_and_cannot_tell_it_exists()
    {
        var p = await Pair();
        var (_, outsider) = await app.Member();
        using var r = await Mute(outsider, p.Chat, new { muted = true });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.False((await Mine(p.CA, p.Chat)).GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task The_switch_must_say_yes_or_no()
    {
        var p = await Pair();
        using var none = await Mute(p.CA, p.Chat, new { });
        using var odd = await Mute(p.CA, p.Chat, new { muted = "perhaps" });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, odd.StatusCode);
    }

    [Fact]
    public async Task A_mute_for_a_while_ends_by_itself()
    {
        var p = await Pair();
        var until = app.Clock.Now.AddHours(4);
        using var on = await Mute(p.CA, p.Chat, new { muted = true, until });
        Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);
        var mine = await Mine(p.CA, p.Chat);
        Assert.True(mine.GetProperty("muted").GetBoolean());
        Assert.Equal(until, mine.GetProperty("mutedUntil").GetDateTimeOffset());

        app.Clock.Advance(TimeSpan.FromHours(4));
        mine = await Mine(p.CA, p.Chat);
        Assert.False(mine.GetProperty("muted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("mutedUntil").ValueKind);
    }

    [Fact]
    public async Task Muting_until_turned_back_on_replaces_a_mute_for_a_while_and_has_no_end()
    {
        var p = await Pair();
        (await Mute(p.CA, p.Chat, new { muted = true, until = app.Clock.Now.AddHours(1) })).Dispose();
        (await Mute(p.CA, p.Chat, new { muted = true })).Dispose();
        app.Clock.Advance(TimeSpan.FromDays(8));   // past the longest mute for a while, within a sign-in
        var mine = await Mine(p.CA, p.Chat);
        Assert.True(mine.GetProperty("muted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("mutedUntil").ValueKind);
    }

    [Fact]
    public async Task Unmuting_ends_a_mute_for_a_while_early()
    {
        var p = await Pair();
        (await Mute(p.CA, p.Chat, new { muted = true, until = app.Clock.Now.AddHours(8) })).Dispose();
        (await Mute(p.CA, p.Chat, new { muted = false })).Dispose();
        var mine = await Mine(p.CA, p.Chat);
        Assert.False(mine.GetProperty("muted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("mutedUntil").ValueKind);
    }

    [Fact]
    public async Task The_end_of_a_mute_must_be_ahead_within_a_week_and_only_with_muting()
    {
        var p = await Pair();
        var now = app.Clock.Now;
        foreach (var body in new object[]
        {
            new { muted = true, until = now },
            new { muted = true, until = now.AddDays(-1) },
            new { muted = true, until = now.AddDays(7).AddSeconds(1) },
            new { muted = false, until = now.AddHours(1) },
        })
        {
            using var r = await Mute(p.CA, p.Chat, body);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        using var longest = await Mute(p.CA, p.Chat, new { muted = true, until = now.AddDays(7) });
        Assert.Equal(HttpStatusCode.NoContent, longest.StatusCode);
    }

    [Fact]
    public async Task Muting_tells_your_own_devices_to_reload_the_list_and_nobody_else()
    {
        var p = await Pair();
        using var mine = await Connect(p.CA);
        using var theirs = await Connect(p.CB);
        (await Mute(p.CA, p.Chat, new { muted = true })).Dispose();
        Assert.Equal("conversations", (await Next(mine)).GetProperty("t").GetString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Next(theirs, TimeSpan.FromMilliseconds(400)));
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
}
