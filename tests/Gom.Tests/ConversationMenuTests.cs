using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Data;

namespace Gom.Tests;

/// <summary>
/// The "..." menu of a conversation in the list: pin to the top, mark unread, hide until something new is said, and delete the
/// conversation for yourself. Each is one person's own; nobody else's list or thread changes.
/// </summary>
public class ConversationMenuTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Put = HttpMethod.Put, Get = HttpMethod.Get, Post = HttpMethod.Post;

    private sealed record Team(User A, string CA, User B, string CB, User C, string CC, Guid Chat);

    /// <summary>A group of three; A leads it.</summary>
    private async Task<Team> NewTeam()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId, c.GomId } }, ca);
        return new Team(a, ca, b, cb, c, cc, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private async Task<Guid> Say(Guid chat, string cookie, string body, Guid? replyTo = null)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body, replyTo }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<HttpStatusCode> Switch(string cookie, Guid chat, string what, object body)
    {
        using var r = await app.Call(Put, $"/api/conversations/{chat}/{what}", body, cookie);
        return r.StatusCode;
    }

    private async Task<HttpStatusCode> Clear(string cookie, Guid chat)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/clear", cookie: cookie);
        return r.StatusCode;
    }

    private async Task<JsonElement[]> List(string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return [.. (await AppFixture.Json(r)).EnumerateArray()];
    }

    private async Task<JsonElement?> Listed(string cookie, Guid chat) =>
        (await List(cookie)).Cast<JsonElement?>().SingleOrDefault(c => c!.Value.GetProperty("id").GetGuid() == chat);

    private async Task<string[]> Bodies(string cookie, Guid chat)
    {
        using var r = await app.Call(Get, $"/api/conversations/{chat}/messages", cookie: cookie);
        return [.. (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().OrderBy(m => m.GetProperty("seq").GetInt64()).Select(m => m.GetProperty("body").GetString()!)];
    }

    // ---- pin to the top

    [Fact]
    public async Task A_pinned_conversation_comes_first_for_you_alone_and_unpinning_puts_it_back()
    {
        var t = await NewTeam();
        var (d, cd) = await app.Member();
        await app.Befriend(t.A, d);
        var direct = await app.OpenChat(t.CA, d.GomId);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await Say(direct, cd, "mới nhất");   // the one-to-one chat is now the most recent

        Assert.Equal(direct, (await List(t.CA))[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, await Switch(t.CA, t.Chat, "pinned", new { on = true }));
        var mine = await List(t.CA);
        Assert.Equal(t.Chat, mine[0].GetProperty("id").GetGuid());
        Assert.True(mine[0].GetProperty("pinned").GetBoolean());
        Assert.False((await Listed(t.CB, t.Chat))!.Value.GetProperty("pinned").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, await Switch(t.CA, t.Chat, "pinned", new { on = false }));
        Assert.Equal(direct, (await List(t.CA))[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_latest_pin_is_on_top_and_pinning_again_changes_nothing()
    {
        var (a, ca) = await app.Member();
        var chats = new List<Guid>();
        // chats[1] is the more recent; pinned the other way round, so only the time of the pin can put chats[0] on top.
        for (var i = 0; i < 2; i++) { var (f, _) = await app.Member(); await app.Befriend(a, f); chats.Add(await app.OpenChat(ca, f.GomId)); app.Clock.Advance(TimeSpan.FromMinutes(1)); }
        await Switch(ca, chats[1], "pinned", new { on = true });
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await Switch(ca, chats[0], "pinned", new { on = true });
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NoContent, await Switch(ca, chats[1], "pinned", new { on = true }));   // already pinned: keeps its place
        Assert.Equal([chats[0], chats[1]], (await List(ca)).Take(2).Select(c => c.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task At_most_five_are_pinned()
    {
        var (a, ca) = await app.Member();
        var chats = new List<Guid>();
        for (var i = 0; i < 6; i++) { var (f, _) = await app.Member(); await app.Befriend(a, f); chats.Add(await app.OpenChat(ca, f.GomId)); }
        foreach (var chat in chats.Take(5)) Assert.Equal(HttpStatusCode.NoContent, await Switch(ca, chat, "pinned", new { on = true }));
        using var r = await app.Call(Put, $"/api/conversations/{chats[5]}/pinned", new { on = true }, ca);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("pin_limit", (await AppFixture.Json(r)).GetProperty("error").GetString());
    }

    // ---- mark unread

    [Fact]
    public async Task Marking_unread_is_a_mark_of_your_own_that_never_moves_your_read_position_and_reading_clears_it()
    {
        var t = await NewTeam();
        await Say(t.Chat, t.CB, "xin chào");
        using (await app.Call(Post, $"/api/conversations/{t.Chat}/read", new { seq = 99 }, t.CA)) { }
        var before = (await Listed(t.CA, t.Chat))!.Value.GetProperty("lastReadSeq").GetInt64();

        Assert.Equal(HttpStatusCode.NoContent, await Switch(t.CA, t.Chat, "unread", new { on = true }));
        var mine = (await Listed(t.CA, t.Chat))!.Value;
        Assert.True(mine.GetProperty("markedUnread").GetBoolean());
        Assert.Equal(before, mine.GetProperty("lastReadSeq").GetInt64());
        Assert.False((await Listed(t.CB, t.Chat))!.Value.GetProperty("markedUnread").GetBoolean());

        // Opening it again reads it, even with nothing new to read.
        using (await app.Call(Post, $"/api/conversations/{t.Chat}/read", new { seq = before }, t.CA)) { }
        Assert.False((await Listed(t.CA, t.Chat))!.Value.GetProperty("markedUnread").GetBoolean());
    }

    [Fact]
    public async Task Writing_in_it_clears_your_mark_and_the_switch_can_clear_it_too()
    {
        var t = await NewTeam();
        await Switch(t.CA, t.Chat, "unread", new { on = true });
        await Say(t.Chat, t.CA, "đây");
        Assert.False((await Listed(t.CA, t.Chat))!.Value.GetProperty("markedUnread").GetBoolean());

        await Switch(t.CA, t.Chat, "unread", new { on = true });
        Assert.Equal(HttpStatusCode.NoContent, await Switch(t.CA, t.Chat, "unread", new { on = false }));
        Assert.False((await Listed(t.CA, t.Chat))!.Value.GetProperty("markedUnread").GetBoolean());
    }

    // ---- hide until something new

    [Fact]
    public async Task A_hidden_conversation_leaves_your_list_until_someone_says_something_and_a_notice_does_not_bring_it_back()
    {
        var t = await NewTeam();
        await Say(t.Chat, t.CB, "một");
        Assert.Equal(HttpStatusCode.NoContent, await Switch(t.CA, t.Chat, "hidden", new { on = true }));
        Assert.Null(await Listed(t.CA, t.Chat));
        Assert.NotNull(await Listed(t.CB, t.Chat));
        Assert.Equal(["một"], await Bodies(t.CA, t.Chat));   // only the list: the thread is all there

        using (await app.Call(Put, $"/api/conversations/{t.Chat}", new { title = "Nhà mới" }, t.CA)) { }   // a rename notice
        Assert.Null(await Listed(t.CA, t.Chat));

        await Say(t.Chat, t.CC, "hai");
        Assert.NotNull(await Listed(t.CA, t.Chat));
    }

    [Fact]
    public async Task Showing_it_again_brings_it_back_at_once()
    {
        var t = await NewTeam();
        await Switch(t.CA, t.Chat, "hidden", new { on = true });
        Assert.Equal(HttpStatusCode.NoContent, await Switch(t.CA, t.Chat, "hidden", new { on = false }));
        Assert.NotNull(await Listed(t.CA, t.Chat));
    }

    // ---- delete for me

    [Fact]
    public async Task Deleting_a_conversation_for_yourself_empties_it_for_you_alone_and_what_comes_after_is_seen()
    {
        var t = await NewTeam();
        var old = await Say(t.Chat, t.CB, "cũ");
        await Switch(t.CA, t.Chat, "pinned", new { on = true });
        await Switch(t.CA, t.Chat, "unread", new { on = true });

        Assert.Equal(HttpStatusCode.NoContent, await Clear(t.CA, t.Chat));
        Assert.Empty(await Bodies(t.CA, t.Chat));
        Assert.Equal(["cũ"], await Bodies(t.CB, t.Chat));
        Assert.Null(await Listed(t.CA, t.Chat));
        // Shown again by hand, it has no last message: the old one is not theirs any more.
        await Switch(t.CA, t.Chat, "hidden", new { on = false });
        Assert.Equal(JsonValueKind.Null, (await Listed(t.CA, t.Chat))!.Value.GetProperty("lastMessage").ValueKind);

        // Still a member: what is said next is theirs, unread, and a quote of the old message shows nothing.
        await Say(t.Chat, t.CB, "mới", replyTo: old);
        var mine = (await Listed(t.CA, t.Chat))!.Value;
        Assert.Equal(1, mine.GetProperty("unread").GetInt32());
        Assert.False(mine.GetProperty("pinned").GetBoolean());
        Assert.False(mine.GetProperty("markedUnread").GetBoolean());
        using var h = await app.Call(Get, $"/api/conversations/{t.Chat}/messages", cookie: t.CA);
        var only = Assert.Single((await AppFixture.Json(h)).GetProperty("messages").EnumerateArray());
        Assert.Equal("mới", only.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Null, only.GetProperty("reply").GetProperty("snippet").ValueKind);
    }

    [Fact]
    public async Task What_was_deleted_for_you_cannot_be_searched_pinned_reacted_to_answered_or_fetched()
    {
        var t = await NewTeam();
        var old = await Say(t.Chat, t.CB, "bí mật");
        await Clear(t.CA, t.Chat);

        using var search = await app.Call(Get, $"/api/conversations/{t.Chat}/search?q=bí", cookie: t.CA);
        Assert.Empty((await AppFixture.Json(search)).GetProperty("results").EnumerateArray());
        using var pin = await app.Call(Put, $"/api/conversations/{t.Chat}/messages/{old}/pin", cookie: t.CA);
        Assert.Equal(HttpStatusCode.NotFound, pin.StatusCode);
        using var react = await app.Call(Put, $"/api/conversations/{t.Chat}/messages/{old}/reaction", new { emoji = "👍" }, t.CA);
        Assert.Equal(HttpStatusCode.NotFound, react.StatusCode);
        using var answer = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid(), body = "?", replyTo = old }, t.CA);
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
    }

    [Fact]
    public async Task Your_other_devices_are_told_and_later_changes_to_what_you_deleted_reach_you_no_more()
    {
        var t = await NewTeam();
        var old = await Say(t.Chat, t.CB, "cũ");
        using var phone = await Connect(t.CA);
        using var other = await Connect(t.CC);

        Assert.Equal(HttpStatusCode.NoContent, await Clear(t.CA, t.Chat));
        var frame = await NextOfType(phone, "cleared");
        Assert.Equal(t.Chat, frame.GetProperty("c").GetGuid());
        Assert.True(frame.GetProperty("seq").GetInt64() > 0);

        // An edit of the old message goes to C, and not to A.
        using (await app.Call(HttpMethod.Patch, $"/api/conversations/{t.Chat}/messages/{old}", new { body = "đã sửa" }, t.CB)) { }
        Assert.Equal("updated", (await NextOfType(other, "event")).GetProperty("event").GetProperty("kind").GetString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NextOfType(phone, "event", TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task Someone_outside_the_conversation_gets_a_404_from_every_switch()
    {
        var t = await NewTeam();
        var (_, outsider) = await app.Member();
        foreach (var what in new[] { "pinned", "unread", "hidden" })
            Assert.Equal(HttpStatusCode.NotFound, await Switch(outsider, t.Chat, what, new { on = true }));
        Assert.Equal(HttpStatusCode.NotFound, await Clear(outsider, t.Chat));
        Assert.Equal(HttpStatusCode.NotFound, await Clear(t.CA, Guid.NewGuid()));
    }

    [Fact]
    public async Task Every_switch_must_say_on_or_off()
    {
        var t = await NewTeam();
        foreach (var what in new[] { "pinned", "unread", "hidden" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, await Switch(t.CA, t.Chat, what, new { }));
            Assert.Equal(HttpStatusCode.BadRequest, await Switch(t.CA, t.Chat, what, new { on = "perhaps" }));
        }
    }

    // ---- the socket, as in WebSocketTests

    private async Task<WebSocket> Connect(string cookie)
    {
        var client = app.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => { r.Headers["Cookie"] = $"__Host-gom={cookie}"; r.Headers["Origin"] = AppFixture.Origin; };
        var ws = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        Assert.Equal("ready", (await NextOfType(ws, "ready")).GetProperty("t").GetString());
        return ws;
    }

    private static async Task<JsonElement> NextOfType(WebSocket ws, string type, TimeSpan? within = null)
    {
        using var cts = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(5));
        var buffer = new byte[16384];
        while (true)
        {
            var text = new StringBuilder();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, cts.Token);
                text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            } while (!r.EndOfMessage);
            var frame = JsonDocument.Parse(text.ToString()).RootElement.Clone();
            if (frame.GetProperty("t").GetString() == type) return frame;
        }
    }
}
