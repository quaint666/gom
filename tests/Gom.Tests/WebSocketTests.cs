using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gom.Tests;

public class WebSocketTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private async Task<WebSocket> Connect(string cookie, string? origin = AppFixture.Origin)
    {
        var client = app.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = $"__Host-gom={cookie}";
            if (origin is not null) r.Headers["Origin"] = origin;
        };
        return await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
    }

    /// <summary>The next whole text frame as JSON, or the close status if the server closed.</summary>
    private static async Task<JsonElement> Next(WebSocket ws, TimeSpan? within = null)
    {
        using var cts = new CancellationTokenSource(within ?? Patience);
        var buffer = new byte[16384];
        var text = new StringBuilder();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) throw new ClosedException(ws.CloseStatus is { } s ? (int)s : -1);
            text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
        } while (!r.EndOfMessage);
        return JsonDocument.Parse(text.ToString()).RootElement.Clone();
    }

    private sealed class ClosedException(int code) : Exception($"closed with {code}") { public int Code { get; } = code; }

    private static async Task<int> WaitForClose(WebSocket ws)
    {
        try { while (true) await Next(ws); }
        catch (ClosedException c) { return c.Code; }
    }

    private static async Task AssertSilent(WebSocket ws)
    {
        try { var j = await Next(ws, TimeSpan.FromMilliseconds(400)); Assert.Fail("unexpected frame: " + j); }
        catch (OperationCanceledException) { }
    }

    private static Task SendText(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private async Task<WebSocket> ConnectReady(string cookie)
    {
        var ws = await Connect(cookie);
        Assert.Equal("ready", (await Next(ws)).GetProperty("t").GetString());
        return ws;
    }

    private async Task<(User A, string CA, User B, string CB, Guid Chat)> NewPair()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return (a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private Task<HttpResponseMessage> Send(string cookie, Guid chat, string body, Guid? client = null) =>
        app.Call(HttpMethod.Post, $"/api/conversations/{chat}/messages", new { clientId = client ?? Guid.NewGuid(), body }, cookie);

    // ---- getting in

    [Fact]
    public async Task A_signed_in_member_connects_and_is_told_it_is_ready()
    {
        var (_, cookie) = await app.Member();
        using var ws = await ConnectReady(cookie);
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task Without_a_session_the_upgrade_is_refused()
    {
        var client = app.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers["Origin"] = AppFixture.Origin;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task A_plain_request_to_the_socket_address_is_a_400_for_members_and_a_401_for_everyone_else()
    {
        var (_, cookie) = await app.Member();
        using var member = await app.Call(HttpMethod.Get, "/ws", cookie: cookie);
        using var anonymous = await app.Call(HttpMethod.Get, "/ws");
        Assert.Equal(HttpStatusCode.BadRequest, member.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.test")]
    [InlineData(null)]
    public async Task The_upgrade_needs_one_of_our_own_origins(string? origin)
    {
        var (_, cookie) = await app.Member();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Connect(cookie, origin));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task A_signed_out_session_cannot_connect()
    {
        var (_, cookie) = await app.Member();
        using var signOut = await app.Call(HttpMethod.Delete, "/api/session", cookie: cookie);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Connect(cookie));
        Assert.Contains("401", ex.Message);
    }

    // ---- live delivery

    [Fact]
    public async Task A_message_reaches_every_device_of_both_people_and_nobody_else()
    {
        var p = await NewPair();
        var (strangerUser, stranger) = await app.Member();
        var (friendOfStranger, _) = await app.Member();
        await app.Befriend(strangerUser, friendOfStranger);
        await app.OpenChat(stranger, friendOfStranger.GomId);   // a member of some other conversation, but not of this one
        using var bPhone = await ConnectReady(p.CB);
        using var bLaptop = await ConnectReady(p.CB);
        using var aPhone = await ConnectReady(p.CA);
        using var other = await ConnectReady(stranger);

        using var sent = await Send(p.CA, p.Chat, "xin chào");

        foreach (var ws in new[] { bPhone, bLaptop, aPhone })
        {
            var frame = await Next(ws);
            Assert.Equal("event", frame.GetProperty("t").GetString());
            Assert.Equal(p.Chat, frame.GetProperty("c").GetGuid());
            var e = frame.GetProperty("event");
            Assert.Equal((1L, "created"), (e.GetProperty("seq").GetInt64(), e.GetProperty("kind").GetString()));
            Assert.Equal("xin chào", e.GetProperty("message").GetProperty("body").GetString());
            Assert.Equal(p.A.Id, e.GetProperty("message").GetProperty("sender").GetProperty("id").GetGuid());
            Assert.False(e.GetProperty("message").GetProperty("sender").TryGetProperty("gomId", out _));
        }
        await AssertSilent(other);
    }

    [Fact]
    public async Task Messages_arrive_in_the_order_they_were_numbered()
    {
        var p = await NewPair();
        using var ws = await ConnectReady(p.CB);
        for (var i = 1; i <= 5; i++) { using var r = await Send(p.CA, p.Chat, $"m{i}"); }

        var seqs = new List<long>();
        for (var i = 0; i < 5; i++) seqs.Add((await Next(ws)).GetProperty("event").GetProperty("seq").GetInt64());

        Assert.Equal([1L, 2, 3, 4, 5], seqs);
    }

    [Fact]
    public async Task By_the_time_a_device_hears_of_a_message_it_is_already_in_the_database()
    {
        var p = await NewPair();
        using var ws = await ConnectReady(p.CB);
        using var sent = await Send(p.CA, p.Chat, "committed first");

        var id = (await Next(ws)).GetProperty("event").GetProperty("message").GetProperty("id").GetGuid();

        await using var db = app.Db.NewContext();
        Assert.True(await db.Messages.AnyAsync(m => m.Id == id));
    }

    [Fact]
    public async Task A_refused_or_repeated_send_is_announced_at_most_once()
    {
        var p = await NewPair();
        using var ws = await ConnectReady(p.CB);
        var client = Guid.NewGuid();
        using var first = await Send(p.CA, p.Chat, "once", client);
        using var again = await Send(p.CA, p.Chat, "once", client);
        using var empty = await Send(p.CA, p.Chat, "   ");

        Assert.Equal(1, (await Next(ws)).GetProperty("event").GetProperty("seq").GetInt64());
        await AssertSilent(ws);
    }

    [Fact]
    public async Task A_device_that_connected_late_asks_for_what_it_missed_and_gets_it_in_order()
    {
        var p = await NewPair();
        for (var i = 1; i <= 4; i++) { using var r = await Send(p.CA, p.Chat, $"m{i}"); }
        using var ws = await ConnectReady(p.CB);

        await SendText(ws, $$"""{"t":"sync","c":"{{p.Chat}}","after":1}""");
        var reply = await Next(ws);

        Assert.Equal("events", reply.GetProperty("t").GetString());
        Assert.Equal(p.Chat, reply.GetProperty("c").GetGuid());
        var events = reply.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal([2L, 3, 4], events.Select(e => e.GetProperty("seq").GetInt64()));
        Assert.Equal(["m2", "m3", "m4"], events.Select(e => e.GetProperty("message").GetProperty("body").GetString()));
        Assert.False(reply.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task A_long_gap_comes_back_in_pages_of_two_hundred_with_a_flag()
    {
        var p = await NewPair();
        await using (var db = app.Db.NewContext())
        {
            for (var i = 1; i <= 250; i++)
            {
                var m = new Message { Id = Guid.NewGuid(), ConversationId = p.Chat, Seq = i, SenderId = p.A.Id, ClientId = Guid.NewGuid(), Body = $"m{i}", CreatedAt = app.Clock.Now };
                db.Messages.Add(m);
                db.Events.Add(new MessageEvent { ConversationId = p.Chat, Seq = i, Kind = EventKind.Created, MessageId = m.Id, CreatedAt = m.CreatedAt });
            }
            await db.SaveChangesAsync();
        }
        using var ws = await ConnectReady(p.CB);

        await SendText(ws, $$"""{"t":"sync","c":"{{p.Chat}}","after":0}""");
        var first = await Next(ws);
        await SendText(ws, $$"""{"t":"sync","c":"{{p.Chat}}","after":200}""");
        var second = await Next(ws);

        Assert.Equal(200, first.GetProperty("events").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(50, second.GetProperty("events").GetArrayLength());
        Assert.Equal(201, second.GetProperty("events")[0].GetProperty("seq").GetInt64());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Asking_to_sync_a_conversation_you_are_not_in_leaks_nothing()
    {
        var p = await NewPair();
        using var sent = await Send(p.CA, p.Chat, "private");
        var (_, outsider) = await app.Member();
        using var ws = await ConnectReady(outsider);

        await SendText(ws, $$"""{"t":"sync","c":"{{p.Chat}}","after":0}""");
        var reply = await Next(ws);
        await SendText(ws, $$"""{"t":"sync","c":"{{Guid.NewGuid()}}","after":0}""");
        var unknown = await Next(ws);

        Assert.Equal("error", reply.GetProperty("t").GetString());
        Assert.Equal("not_found", reply.GetProperty("error").GetString());
        Assert.DoesNotContain("private", reply.ToString());
        Assert.Equal(reply.ToString(), unknown.ToString().Replace(unknown.GetProperty("c").GetString()!, p.Chat.ToString()));
    }

    [Fact]
    public async Task Rubbish_from_the_client_gets_an_error_and_the_connection_stays()
    {
        var (_, cookie) = await app.Member();
        using var ws = await ConnectReady(cookie);

        foreach (var junk in new[] { "not json", "[]", "{}", """{"t":"dance"}""", """{"t":"sync"}""", """{"t":"sync","c":"x","after":0}""", """{"t":"sync","c":"00000000-0000-0000-0000-000000000000","after":-1}""" })
        {
            await SendText(ws, junk);
            var reply = await Next(ws);
            Assert.Equal("error", reply.GetProperty("t").GetString());
            Assert.Equal("bad_request", reply.GetProperty("error").GetString());
        }
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task A_frame_over_four_kilobytes_closes_the_connection()
    {
        var (_, cookie) = await app.Member();
        using var ws = await ConnectReady(cookie);

        await SendText(ws, new string('x', 10_000));

        Assert.Equal(1009, await WaitForClose(ws));
    }

    [Fact]
    public async Task A_binary_frame_closes_the_connection()
    {
        var (_, cookie) = await app.Member();
        using var ws = await ConnectReady(cookie);

        await ws.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);

        Assert.Equal(1003, await WaitForClose(ws));
    }

    // ---- groups

    private async Task<(User A, string CA, User B, string CB, User C, string CC)> Trio()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c);
        return (a, ca, b, cb, c, cc);
    }

    private async Task<Guid> NewGroup(string cookie, params string[] members)
    {
        using var r = await app.Call(HttpMethod.Post, "/api/conversations/group", new { title = "Nhóm", members }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private static async Task ExpectListChanged(WebSocket ws) => Assert.Equal("conversations", (await Next(ws)).GetProperty("t").GetString());

    [Fact]
    public async Task Making_a_group_tells_every_member_s_devices_to_reload_their_list_and_nobody_else()
    {
        var t = await Trio();
        var (_, stranger) = await app.Member();
        using var a = await ConnectReady(t.CA);
        using var b = await ConnectReady(t.CB);
        using var c = await ConnectReady(t.CC);
        using var other = await ConnectReady(stranger);

        await NewGroup(t.CA, t.B.GomId, t.C.GomId);

        foreach (var ws in new[] { a, b, c }) await ExpectListChanged(ws);
        await AssertSilent(other);
    }

    [Fact]
    public async Task Adding_someone_tells_the_new_member_and_the_old_ones()
    {
        var t = await Trio();
        var (d, cd) = await app.Member();
        await app.Befriend(t.A, d);
        var chat = await NewGroup(t.CA, t.B.GomId, t.C.GomId);
        using var b = await ConnectReady(t.CB);
        using var newcomer = await ConnectReady(cd);

        using var add = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/members", new { gomId = d.GomId }, t.CA);

        await ExpectListChanged(b);
        await ExpectListChanged(newcomer);
    }

    [Fact]
    public async Task Someone_who_leaves_is_told_on_every_device_and_hears_nothing_more_from_the_group()
    {
        var t = await Trio();
        var chat = await NewGroup(t.CA, t.B.GomId, t.C.GomId);
        using var phone = await ConnectReady(t.CB);
        using var laptop = await ConnectReady(t.CB);
        using var a = await ConnectReady(t.CA);

        using var leave = await app.Call(HttpMethod.Delete, $"/api/conversations/{chat}/members/me", cookie: t.CB);
        await ExpectListChanged(phone);
        await ExpectListChanged(laptop);
        await ExpectListChanged(a);

        using var sent = await Send(t.CA, chat, "B không còn nghe");
        Assert.Equal("event", (await Next(a)).GetProperty("t").GetString());
        await AssertSilent(phone);
        await AssertSilent(laptop);
    }

    [Fact]
    public async Task A_new_member_cannot_sync_what_was_said_before_they_joined()
    {
        var t = await Trio();
        var (d, cd) = await app.Member();
        await app.Befriend(t.A, d);
        var chat = await NewGroup(t.CA, t.B.GomId, t.C.GomId);
        using var first = await Send(t.CA, chat, "trước");
        using var add = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/members", new { gomId = d.GomId }, t.CA);
        using var second = await Send(t.CB, chat, "sau");
        using var ws = await ConnectReady(cd);

        await SendText(ws, $$"""{"t":"sync","c":"{{chat}}","after":0}""");
        var reply = await Next(ws);

        Assert.Equal(["sau"], reply.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("message").GetProperty("body").GetString()!).ToArray());
    }

    // ---- recall

    [Fact]
    public async Task A_recall_reaches_every_device_of_both_people_as_a_deleted_event_with_its_own_number()
    {
        var p = await NewPair();
        var (_, stranger) = await app.Member();
        using var sent = await Send(p.CA, p.Chat, "sẽ thu hồi");
        var messageId = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();
        using var aPhone = await ConnectReady(p.CA);
        using var bPhone = await ConnectReady(p.CB);
        using var bLaptop = await ConnectReady(p.CB);
        using var other = await ConnectReady(stranger);

        using var recall = await app.Call(HttpMethod.Delete, $"/api/conversations/{p.Chat}/messages/{messageId}", cookie: p.CA);

        foreach (var ws in new[] { aPhone, bPhone, bLaptop })
        {
            var frame = await Next(ws);
            var e = frame.GetProperty("event");
            Assert.Equal(("event", "deleted", 2L), (frame.GetProperty("t").GetString(), e.GetProperty("kind").GetString(), e.GetProperty("seq").GetInt64()));
            var m = e.GetProperty("message");
            Assert.Equal((messageId, 1L, true, ""), (m.GetProperty("id").GetGuid(), m.GetProperty("seq").GetInt64(), m.GetProperty("deleted").GetBoolean(), m.GetProperty("body").GetString()));
        }
        await AssertSilent(other);
    }

    [Fact]
    public async Task A_device_that_was_away_learns_of_a_recall_from_the_stream_and_never_sees_the_words()
    {
        var p = await NewPair();
        using var first = await Send(p.CA, p.Chat, "giữ lại");
        using var second = await Send(p.CA, p.Chat, "sẽ mất");
        var secondId = (await AppFixture.Json(second)).GetProperty("id").GetGuid();
        using var recall = await app.Call(HttpMethod.Delete, $"/api/conversations/{p.Chat}/messages/{secondId}", cookie: p.CA);
        using var ws = await ConnectReady(p.CB);

        await SendText(ws, $$"""{"t":"sync","c":"{{p.Chat}}","after":0}""");
        var reply = await Next(ws);

        var events = reply.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal([(1L, "created"), (2L, "created"), (3L, "deleted")], events.Select(e => (e.GetProperty("seq").GetInt64(), e.GetProperty("kind").GetString()!)).ToArray());
        Assert.DoesNotContain("sẽ mất", reply.ToString());
        Assert.Equal([false, true, true], events.Select(e => e.GetProperty("message").GetProperty("deleted").GetBoolean()).ToArray());   // the created event of a recalled message shows it as recalled too
    }

    [Fact]
    public async Task Someone_who_joined_a_group_later_hears_nothing_of_a_recall_of_a_message_from_before()
    {
        var t = await Trio();
        var (d, cd) = await app.Member();
        await app.Befriend(t.A, d);
        var chat = await NewGroup(t.CA, t.B.GomId, t.C.GomId);
        using var before = await Send(t.CA, chat, "từ trước");
        var beforeId = (await AppFixture.Json(before)).GetProperty("id").GetGuid();
        using var add = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/members", new { gomId = d.GomId }, t.CA);
        using var newcomer = await ConnectReady(cd);
        using var existing = await ConnectReady(t.CB);

        using var recall = await app.Call(HttpMethod.Delete, $"/api/conversations/{chat}/messages/{beforeId}", cookie: t.CA);

        Assert.Equal("deleted", (await Next(existing)).GetProperty("event").GetProperty("kind").GetString());
        await AssertSilent(newcomer);
        await SendText(newcomer, $$"""{"t":"sync","c":"{{chat}}","after":0}""");
        Assert.Empty((await Next(newcomer)).GetProperty("events").EnumerateArray());
    }

    // ---- when the session ends, the socket ends

    [Fact]
    public async Task Signing_out_closes_that_devices_socket_and_only_that_one()
    {
        var user = await app.AddUser();
        var phone = await app.SignIn(user.GomId);
        var laptop = await app.SignIn(user.GomId);
        using var wsPhone = await ConnectReady(phone);
        using var wsLaptop = await ConnectReady(laptop);

        using var signOut = await app.Call(HttpMethod.Delete, "/api/session", cookie: phone);

        Assert.Equal(4401, await WaitForClose(wsPhone));
        await AssertSilent(wsLaptop);
        Assert.Equal(WebSocketState.Open, wsLaptop.State);
    }

    [Fact]
    public async Task Ending_another_session_from_settings_closes_its_socket()
    {
        var user = await app.AddUser();
        var phone = await app.SignIn(user.GomId);
        var laptop = await app.SignIn(user.GomId);
        using var wsPhone = await ConnectReady(phone);
        var phoneSession = (await app.SessionsOf(user.Id)).First().Id;

        using var end = await app.Call(HttpMethod.Delete, $"/api/sessions/{phoneSession}", cookie: laptop);

        Assert.Equal(4401, await WaitForClose(wsPhone));
    }

    [Fact]
    public async Task Changing_your_password_closes_the_other_devices_sockets_and_keeps_this_one()
    {
        var user = await app.AddUser();
        var here = await app.SignIn(user.GomId);
        var elsewhere = await app.SignIn(user.GomId);
        using var wsHere = await ConnectReady(here);
        using var wsElsewhere = await ConnectReady(elsewhere);

        using var change = await app.Call(HttpMethod.Put, "/api/me/password", new { current = AppFixture.Password, password = "a different long password" }, here);

        Assert.Equal(4401, await WaitForClose(wsElsewhere));
        await AssertSilent(wsHere);   // a close from the server would show up here
        Assert.Equal(WebSocketState.Open, wsHere.State);
    }

    [Fact]
    public async Task Locking_a_member_closes_all_their_sockets()
    {
        var member = await app.AddUser();
        var cookie = await app.SignIn(member.GomId);
        var (_, ownerCookie) = await app.Owner();
        using var ws1 = await ConnectReady(cookie);
        using var ws2 = await ConnectReady(cookie);

        using var lockIt = await app.Call(HttpMethod.Post, $"/api/users/{member.GomId}/disable", cookie: ownerCookie);

        Assert.Equal(4401, await WaitForClose(ws1));
        Assert.Equal(4401, await WaitForClose(ws2));
    }

    [Fact]
    public async Task A_password_reset_closes_every_socket_of_that_person()
    {
        var member = await app.AddUser();
        var cookie = await app.SignIn(member.GomId);
        var (_, ownerCookie) = await app.Owner();
        using var ws = await ConnectReady(cookie);
        using var link = await app.Call(HttpMethod.Post, $"/api/users/{member.GomId}/reset-link", cookie: ownerCookie);
        var token = (await AppFixture.Json(link)).GetProperty("token").GetString();

        using var reset = await app.Call(HttpMethod.Post, $"/api/reset/{token}", new { password = "yet another long password" });

        Assert.Equal(4401, await WaitForClose(ws));
    }

    [Fact]
    public async Task A_closed_socket_is_forgotten()
    {
        var registry = app.Factory.Services.GetRequiredService<Gom.Web.Chat.Live.ConnectionRegistry>();
        var (_, cookie) = await app.Member();
        var before = registry.Count;
        using var ws = await ConnectReady(cookie);
        Assert.Equal(before + 1, registry.Count);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        for (var i = 0; i < 50 && registry.Count != before; i++) await Task.Delay(100);
        Assert.Equal(before, registry.Count);
    }

    // ---- limits

    [Fact]
    public async Task A_sixth_connection_pushes_out_the_oldest()
    {
        var (_, cookie) = await app.Member();
        var sockets = new List<WebSocket>();
        for (var i = 0; i < 5; i++) sockets.Add(await ConnectReady(cookie));

        sockets.Add(await ConnectReady(cookie));

        Assert.Equal(1008, await WaitForClose(sockets[0]));
        await AssertSilent(sockets[5]);
        Assert.Equal(WebSocketState.Open, sockets[5].State);
        foreach (var s in sockets) s.Dispose();
    }
}
