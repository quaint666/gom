using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>Reactions: one emoji per person per message, told to everyone through the stream.</summary>
public class ReactionTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put, Delete = HttpMethod.Delete;

    private sealed record Team(User A, string CA, User B, string CB, User E, string CE, Guid Chat);

    private async Task<Team> NewTeam()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (e, ce) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, e);
        // Real names, so a leaked Gom ID cannot hide behind a name that equals it.
        (await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "Bé Ba" }, cb)).Dispose();
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId } }, ca);
        return new Team(a, ca, b, cb, e, ce, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private async Task<Guid> Say(Team t, string cookie, string body)
    {
        using var r = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> React(Team t, string cookie, Guid m, string? emoji) => app.Call(Put, $"/api/conversations/{t.Chat}/messages/{m}/reaction", new { emoji }, cookie);
    private Task<HttpResponseMessage> Unreact(Team t, string cookie, Guid m) => app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{m}/reaction", cookie: cookie);

    private async Task<JsonElement> Seen(Team t, string cookie, Guid m)
    {
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/messages", cookie: cookie);
        return (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == m);
    }

    private static string[] Summary(JsonElement message) => !message.TryGetProperty("reactions", out var r) || r.ValueKind == JsonValueKind.Null
        ? [] : r.EnumerateArray().Select(x => $"{x.GetProperty("emoji").GetString()}:{x.GetProperty("users").GetArrayLength()}").ToArray();

    [Fact]
    public async Task A_reaction_shows_to_everyone_with_who_chose_it()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        using var r = await React(t, t.CB, m, "👍");
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        foreach (var cookie in new[] { t.CA, t.CB })
        {
            var seen = await Seen(t, cookie, m);
            Assert.Equal(new[] { "👍:1" }, Summary(seen));
            var who = seen.GetProperty("reactions")[0].GetProperty("users")[0];
            Assert.Equal(t.B.Id, who.GetProperty("id").GetGuid());
            Assert.Equal("Bé Ba", who.GetProperty("displayName").GetString());
            Assert.DoesNotContain(t.B.GomId, seen.GetRawText());
        }
    }

    [Fact]
    public async Task Several_people_may_choose_the_same_emoji_and_each_has_one()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        (await React(t, t.CA, m, "👍")).Dispose();
        app.Clock.Advance(TimeSpan.FromSeconds(5));   // the fake clock stands still unless moved
        (await React(t, t.CB, m, "👍")).Dispose();
        Assert.Equal(new[] { "👍:2" }, Summary(await Seen(t, t.CA, m)));
        app.Clock.Advance(TimeSpan.FromSeconds(5));
        (await React(t, t.CB, m, "❤️")).Dispose();
        Assert.Equal(new[] { "👍:1", "❤️:1" }, Summary(await Seen(t, t.CA, m)));
    }

    [Fact]
    public async Task Choosing_the_same_one_again_changes_nothing_and_takes_no_number()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        (await React(t, t.CB, m, "😆")).Dispose();
        await using var db = app.Db.NewContext();
        var before = await db.Conversations.Where(c => c.Id == t.Chat).Select(c => c.LastSeq).SingleAsync();
        using var again = await React(t, t.CB, m, "😆");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        await using var db2 = app.Db.NewContext();
        Assert.Equal(before, await db2.Conversations.Where(c => c.Id == t.Chat).Select(c => c.LastSeq).SingleAsync());
    }

    [Fact]
    public async Task Taking_a_reaction_back_removes_it_and_doing_it_twice_is_fine()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        (await React(t, t.CB, m, "😮")).Dispose();
        using var off = await Unreact(t, t.CB, m);
        using var again = await Unreact(t, t.CB, m);
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Empty(Summary(await Seen(t, t.CA, m)));
    }

    [Fact]
    public async Task Taking_back_a_reaction_that_was_never_there_takes_no_number()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        await using var db = app.Db.NewContext();
        var before = await db.Conversations.Where(c => c.Id == t.Chat).Select(c => c.LastSeq).SingleAsync();
        using var r = await Unreact(t, t.CB, m);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db2 = app.Db.NewContext();
        Assert.Equal(before, await db2.Conversations.Where(c => c.Id == t.Chat).Select(c => c.LastSeq).SingleAsync());
    }

    [Fact]
    public async Task A_message_of_another_conversation_cannot_be_reacted_to_here()
    {
        var t = await NewTeam();
        var other = await app.OpenChat(t.CA, t.E.GomId);
        using var sent = await app.Call(Post, $"/api/conversations/{other}/messages", new { clientId = Guid.NewGuid(), body = "chuyện riêng" }, t.CA);
        var elsewhere = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();
        using var r = await React(t, t.CA, elsewhere, "👍");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task A_message_taken_back_shows_no_reactions()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CB, "sẽ thu hồi");
        (await React(t, t.CA, m, "👍")).Dispose();
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{m}", cookie: t.CB)).Dispose();
        var seen = await Seen(t, t.CA, m);
        Assert.True(seen.GetProperty("deleted").GetBoolean());
        Assert.Empty(Summary(seen));
    }

    [Fact]
    public async Task A_device_catching_up_is_given_the_reactions_too()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        (await React(t, t.CB, m, "😢")).Dispose();
        using var ws = await Connect(t.CA);
        await Send(ws, "{\"t\":\"sync\",\"c\":\"" + t.Chat + "\",\"after\":0}");
        var events = (await NextOfType(ws, "events")).GetProperty("events").EnumerateArray().ToArray();
        Assert.All(events, e => Assert.Equal(new[] { "😢:1" }, Summary(e.GetProperty("message"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hi")]
    [InlineData("🦄")]
    [InlineData(null)]
    public async Task Only_the_six_emoji_are_allowed(string? emoji)
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        using var r = await React(t, t.CB, m, emoji);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_reaction", (await AppFixture.Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Each_change_takes_a_number_in_the_stream_so_a_device_that_was_away_learns_of_it()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        using var ws = await Connect(t.CA);
        (await React(t, t.CB, m, "👍")).Dispose();
        var live = (await NextOfType(ws, "event")).GetProperty("event");
        Assert.Equal("updated", live.GetProperty("kind").GetString());
        Assert.Equal(new[] { "👍:1" }, Summary(live.GetProperty("message")));
        await Send(ws, $$"""{"t":"sync","c":"{{t.Chat}}","after":0}""");
        var kinds = (await NextOfType(ws, "events")).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToArray();
        Assert.Equal(new[] { "created", "updated" }, kinds);
    }

    [Fact]
    public async Task A_reaction_is_pushed_to_the_conversation_and_not_to_a_person_who_deleted_the_message_for_themself()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CB, "tin");
        (await app.Call(Put, $"/api/conversations/{t.Chat}/messages/{m}/hidden", cookie: t.CA)).Dispose();
        using var wsA = await Connect(t.CA);
        using var wsB = await Connect(t.CB);
        (await React(t, t.CB, m, "❤️")).Dispose();
        Assert.Equal("updated", (await NextOfType(wsB, "event")).GetProperty("event").GetProperty("kind").GetString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NextOfType(wsA, "event", TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task A_message_taken_back_after_you_deleted_it_for_yourself_does_not_come_back_to_you()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CB, "tin");
        (await app.Call(Put, $"/api/conversations/{t.Chat}/messages/{m}/hidden", cookie: t.CA)).Dispose();
        using var wsA = await Connect(t.CA);
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{m}", cookie: t.CB)).Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NextOfType(wsA, "event", TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task What_you_cannot_see_cannot_be_reacted_to()
    {
        var t = await NewTeam();
        var before = await Say(t, t.CA, "trước khi E vào");
        (await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = t.E.GomId }, t.CA)).Dispose();
        var gone = await Say(t, t.CB, "sẽ thu hồi");
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{gone}", cookie: t.CB)).Dispose();
        var (_, stranger) = await app.Member();
        using var early = await React(t, t.CE, before, "👍");
        using var recalled = await React(t, t.CA, gone, "👍");
        using var outsider = await React(t, stranger, before, "👍");
        using var unknown = await React(t, t.CA, Guid.NewGuid(), "👍");
        foreach (var r in new[] { early, recalled, outsider, unknown }) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task A_one_to_one_chat_ended_by_unfriending_takes_no_new_reactions()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        using var sent = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "tin" }, ca);
        var m = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();
        (await app.Call(Delete, $"/api/friends/{b.GomId}", cookie: ca)).Dispose();
        using var r = await app.Call(Put, $"/api/conversations/{chat}/messages/{m}/reaction", new { emoji = "👍" }, cb);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Someone_who_left_the_group_takes_their_reaction_with_them()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        (await React(t, t.CB, m, "👍")).Dispose();
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/members/me", cookie: t.CB)).Dispose();
        Assert.Empty(Summary(await Seen(t, t.CA, m)));
    }

    [Fact]
    public async Task Two_reactions_at_the_same_moment_by_one_person_leave_exactly_one()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        var calls = await Task.WhenAll(new[] { "👍", "❤️", "😆", "😮" }.Select(e => React(t, t.CB, m, e)));
        Assert.All(calls, c => Assert.Equal(HttpStatusCode.NoContent, c.StatusCode));
        var seen = Summary(await Seen(t, t.CA, m));
        Assert.Single(seen);
        foreach (var c in calls) c.Dispose();
    }

    [Fact]
    public async Task Ending_a_group_works_when_messages_have_reactions()
    {
        var t = await NewTeam();
        var m = await Say(t, t.CA, "tin");
        (await React(t, t.CB, m, "👍")).Dispose();
        using var r = await app.Call(Delete, $"/api/conversations/{t.Chat}", cookie: t.CA);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Reactions.AnyAsync(x => x.MessageId == m));
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

    private static async Task<JsonElement> NextOfType(WebSocket ws, string type, TimeSpan? within = null)
    {
        var until = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var f = await Next(ws, until - DateTime.UtcNow);
            if (f.GetProperty("t").GetString() == type) return f;
        }
    }
}
