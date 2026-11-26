using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gom.Tests;

/// <summary>The app with calls switched on (they are off by default).</summary>
public sealed class CallFixture : AppFixture
{
    protected override void Settings(IWebHostBuilder b) => b.UseSetting("Calls:Enabled", "true");
}

/// <summary>
/// One-to-one voice calls, the server's half (docs/voice-calls.md): who may call, ringing every device, the first answer winning,
/// every way a call ends and the line it leaves, and the phones' offers passed on. The phones are fakes: no audio is tested here.
/// </summary>
public class CallTests(CallFixture app) : IClassFixture<CallFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Delete = HttpMethod.Delete;
    private const string Offer = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n";
    private const string Answer = "v=0\r\no=- 3 4 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n";

    private sealed record Pair(User A, string CA, User B, string CB, Guid Chat);

    private async Task<Pair> Friends()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return new Pair(a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private Task<HttpResponseMessage> Start(string cookie, Guid chat, Guid? clientId = null, string sdp = Offer, int v = CallEndpoints.ProtocolVersion) =>
        app.Call(Post, "/api/calls", new { conversationId = chat, clientId = clientId ?? Guid.NewGuid(), sdp, v }, cookie);

    private async Task<Guid> Ringing(Pair p)
    {
        using var r = await Start(p.CA, p.Chat);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await AppFixture.Json(r);
        Assert.Equal("ringing", body.GetProperty("state").GetString());
        return body.GetProperty("id").GetGuid();
    }

    private async Task<Guid> Answered(Pair p)
    {
        var id = await Ringing(p);
        using var r = await app.Call(Post, $"/api/calls/{id}/accept", new { sdp = Answer, v = CallEndpoints.ProtocolVersion }, p.CB);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return id;
    }

    private async Task<JsonElement> Read(Guid id, string cookie)
    {
        using var r = await app.Call(Get, $"/api/calls/{id}", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString();

    /// <summary>The call's line in the chat, as the history gives it.</summary>
    private async Task<JsonElement> Line(Guid chat, string cookie)
    {
        using var r = await app.Call(Get, $"/api/conversations/{chat}/messages", cookie: cookie);
        var lines = (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray()
            .Where(m => m.TryGetProperty("notice", out var n) && n.ValueKind == JsonValueKind.Object && n.GetProperty("type").GetString() == "call").ToList();
        return Assert.Single(lines).GetProperty("notice").GetProperty("call").Clone();
    }

    private async Task<int> Unread(Guid chat, string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chat).GetProperty("unread").GetInt32();
    }

    private async Task Sweep()
    {
        using var scope = app.Factory.Services.CreateScope();
        await CallSweeper.RunOnce(scope.ServiceProvider.GetRequiredService<GomDbContext>(), app.Factory.Services.GetRequiredService<CallRelay>(),
            scope.ServiceProvider.GetRequiredService<LiveDelivery>(), app.Clock.GetUtcNow(), app.Factory.Services.GetRequiredService<IOptions<CallOptions>>().Value, default);
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
        var buffer = new byte[65536];
        var r = await ws.ReceiveAsync(buffer, cts.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)).RootElement.Clone();
    }

    /// <summary>The next call frame (skipping the conversation's own news, such as the call's line going out).</summary>
    private static async Task<JsonElement> NextCall(WebSocket ws)
    {
        for (var i = 0; i < 10; i++)
        {
            var frame = await Next(ws);
            if (frame.GetProperty("t").GetString() == "call") return frame;
        }
        throw new InvalidOperationException("no call frame");
    }

    // ---- who may call

    [Fact]
    public async Task Calls_are_off_unless_switched_on()
    {
        var off = new AppFixture();
        await off.InitializeAsync();
        try
        {
        var (a, ca) = await off.Member();
        var (b, _) = await off.Member();
        await off.Befriend(a, b);
        var chat = await off.OpenChat(ca, b.GomId);
        using (var r = await off.Call(Post, "/api/calls", new { conversationId = chat, clientId = Guid.NewGuid(), sdp = Offer, v = 1 }, ca)) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var me = await off.Call(Get, "/api/me", cookie: ca)) Assert.False((await AppFixture.Json(me)).GetProperty("calls").GetBoolean());
        }
        finally { await off.DisposeAsync(); }
        using (var me = await app.Call(Get, "/api/me", cookie: (await app.Member()).Cookie)) Assert.True((await AppFixture.Json(me)).GetProperty("calls").GetBoolean());
    }

    [Fact]
    public async Task Only_friends_one_to_one()
    {
        var (a, ca) = await app.Member();
        var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        await using (var db = app.Db.NewContext())
        {
            var (low, high) = Friendship.Order(a.Id, b.Id);
            await db.Friendships.Where(f => f.UserLow == low && f.UserHigh == high).ExecuteDeleteAsync();
        }
        using (var r = await Start(ca, chat)) { Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode); Assert.Equal("not_friends", await ErrorOf(r)); }

        var (_, stranger) = await app.Member();
        using (var r = await Start(stranger, chat)) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        var p = await Friends();
        using var g = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { p.B.GomId } }, p.CA);
        var group = (await AppFixture.Json(g)).GetProperty("id").GetGuid();
        using (var r = await Start(p.CA, group)) Assert.Equal("invalid_call", await ErrorOf(r));
    }

    [Theory]
    [InlineData(0, Offer, "unsupported_version")]
    [InlineData(2, Offer, "unsupported_version")]
    [InlineData(1, "", "invalid_sdp")]
    [InlineData(1, "hello", "invalid_sdp")]
    [InlineData(1, "v=0\r\na=x\u0001\r\n", "invalid_sdp")]
    public async Task A_start_speaks_this_protocol_and_carries_an_offer(int v, string sdp, string error)
    {
        var p = await Friends();
        using var r = await Start(p.CA, p.Chat, sdp: sdp, v: v);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(error, await ErrorOf(r));
    }

    [Fact]
    public async Task An_offer_too_long_is_refused()
    {
        var p = await Friends();
        using var r = await Start(p.CA, p.Chat, sdp: Offer + new string('a', CallEndpoints.MaxSdpChars));
        Assert.Equal("invalid_sdp", await ErrorOf(r));
    }

    [Fact]
    public async Task Starting_again_with_the_same_id_is_the_same_call()
    {
        var p = await Friends();
        var clientId = Guid.NewGuid();
        using var first = await Start(p.CA, p.Chat, clientId);
        using var again = await Start(p.CA, p.Chat, clientId);
        Assert.Equal((await AppFixture.Json(first)).GetProperty("id").GetGuid(), (await AppFixture.Json(again)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Starting_calls_fast_is_slowed()
    {
        var p = await Friends();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var r = await Start(p.CA, p.Chat);
            codes.Add(r.StatusCode);
            if (r.StatusCode == HttpStatusCode.OK)
                using (await app.Call(Post, $"/api/calls/{(await AppFixture.Json(r)).GetProperty("id").GetGuid()}/hangup", cookie: p.CA)) { }
            else
            {
                Assert.Equal("too_many_calls", await ErrorOf(r));
                Assert.NotNull(r.Headers.RetryAfter);
            }
        }
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], codes);
    }

    // ---- ringing and answering

    [Fact]
    public async Task Every_device_of_the_person_called_rings_and_any_may_read_the_offer()
    {
        var p = await Friends();
        var phone = await app.SignIn(p.B.GomId, userAgent: "Phone");
        using var one = await Connect(p.CB);
        using var two = await Connect(phone);
        var id = await Ringing(p);
        foreach (var ws in new[] { one, two })
        {
            var frame = await NextCall(ws);
            Assert.Equal("incoming", frame.GetProperty("kind").GetString());
            Assert.Equal(id, frame.GetProperty("id").GetGuid());
            Assert.Equal(p.A.Id, frame.GetProperty("from").GetProperty("id").GetGuid());
        }
        var seen = await Read(id, phone);
        Assert.Equal(Offer, seen.GetProperty("offer").GetString());
        Assert.Equal(45, seen.GetProperty("ringSeconds").GetInt32());
        Assert.Equal(30, seen.GetProperty("aliveSeconds").GetInt32());
        var (_, stranger) = await app.Member();
        using (var r = await app.Call(Get, $"/api/calls/{id}", cookie: stranger)) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task The_first_device_to_answer_wins_and_the_others_stop_ringing()
    {
        var p = await Friends();
        var phone = await app.SignIn(p.B.GomId, userAgent: "Phone");
        using var caller = await Connect(p.CA);
        using var other = await Connect(phone);
        var id = await Ringing(p);
        Assert.Equal("incoming", (await NextCall(other)).GetProperty("kind").GetString());

        using (var r = await app.Call(Post, $"/api/calls/{id}/accept", new { sdp = Answer, v = 1 }, p.CB)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var accepted = await NextCall(caller);
        Assert.Equal("accepted", accepted.GetProperty("kind").GetString());
        Assert.Equal(Answer, accepted.GetProperty("sdp").GetString());
        Assert.Equal("answered_elsewhere", (await NextCall(other)).GetProperty("kind").GetString());

        using (var r = await app.Call(Post, $"/api/calls/{id}/accept", new { sdp = Answer, v = 1 }, phone)) Assert.Equal("call_answered", await ErrorOf(r));
        Assert.Equal(JsonValueKind.Null, (await Read(id, phone)).GetProperty("offer").ValueKind);   // the device not in it no longer gets the offer
        Assert.Equal(Answer, (await Read(id, p.CA)).GetProperty("answer").GetString());
        using (var r = await app.Call(Post, $"/api/calls/{id}/hangup", cookie: phone)) Assert.Equal("not_in_call", await ErrorOf(r));
    }

    [Fact]
    public async Task Only_the_person_called_answers_or_declines()
    {
        var p = await Friends();
        var id = await Ringing(p);
        using (var r = await app.Call(Post, $"/api/calls/{id}/accept", new { sdp = Answer, v = 1 }, p.CA)) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var r = await app.Call(Post, $"/api/calls/{id}/decline", cookie: p.CA)) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // ---- how calls end, and the line each leaves

    [Fact]
    public async Task Declined_both_hear_it_and_the_line_is_not_unread()
    {
        var p = await Friends();
        using var caller = await Connect(p.CA);
        var id = await Ringing(p);
        using (var r = await app.Call(Post, $"/api/calls/{id}/decline", cookie: p.CB)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var ended = await NextCall(caller);
        Assert.Equal("ended", ended.GetProperty("kind").GetString());
        Assert.Equal("declined", ended.GetProperty("outcome").GetString());
        var line = await Line(p.Chat, p.CB);
        Assert.Equal("declined", line.GetProperty("outcome").GetString());
        Assert.Equal(p.A.Id, line.GetProperty("callerId").GetGuid());
        Assert.Equal(0, await Unread(p.Chat, p.CB));
    }

    [Fact]
    public async Task The_caller_giving_up_is_a_missed_call_unread_for_the_person_called_only()
    {
        var p = await Friends();
        var id = await Ringing(p);
        using (var r = await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CA)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("missed", (await Line(p.Chat, p.CB)).GetProperty("outcome").GetString());
        Assert.Equal(1, await Unread(p.Chat, p.CB));
        Assert.Equal(0, await Unread(p.Chat, p.CA));
        using (var r = await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CA)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);   // again: nothing
        await Line(p.Chat, p.CB);   // still one line
    }

    [Fact]
    public async Task A_call_nobody_answers_in_time_is_missed()
    {
        var p = await Friends();
        var id = await Ringing(p);
        app.Clock.Advance(TimeSpan.FromSeconds(44));
        await Sweep();
        Assert.Equal("ringing", (await Read(id, p.CA)).GetProperty("state").GetString());
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        using (var r = await app.Call(Post, $"/api/calls/{id}/accept", new { sdp = Answer, v = 1 }, p.CB)) Assert.Equal("call_ended", await ErrorOf(r));   // too late, even before the sweep
        var read = await Read(id, p.CA);
        Assert.Equal("missed", read.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task The_sweep_ends_a_call_that_rang_out()
    {
        var p = await Friends();
        var id = await Ringing(p);
        app.Clock.Advance(TimeSpan.FromSeconds(46));
        await Sweep();
        Assert.Equal("missed", (await Read(id, p.CB)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_finished_call_says_how_long_it_lasted()
    {
        var p = await Friends();
        var id = await Answered(p);
        for (var i = 0; i < 6; i++)
        {
            app.Clock.Advance(TimeSpan.FromSeconds(30));
            using (await app.Call(Post, $"/api/calls/{id}/alive", cookie: p.CA)) { }
            using (await app.Call(Post, $"/api/calls/{id}/alive", cookie: p.CB)) { }
        }
        app.Clock.Advance(TimeSpan.FromSeconds(5));
        await Sweep();   // both were heard from: it goes on
        Assert.Equal("active", (await Read(id, p.CA)).GetProperty("state").GetString());
        using (var r = await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CB)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var line = await Line(p.Chat, p.CA);
        Assert.Equal("completed", line.GetProperty("outcome").GetString());
        Assert.Equal(185, line.GetProperty("seconds").GetInt32());
        Assert.Equal(0, await Unread(p.Chat, p.CB));
        using (var r = await app.Call(Post, $"/api/calls/{id}/alive", cookie: p.CA)) Assert.Equal("call_ended", await ErrorOf(r));
    }

    [Fact]
    public async Task A_side_gone_silent_ends_the_call_at_when_it_was_last_heard()
    {
        var p = await Friends();
        var id = await Answered(p);
        app.Clock.Advance(TimeSpan.FromSeconds(30));
        using (await app.Call(Post, $"/api/calls/{id}/alive", cookie: p.CB)) { }
        for (var i = 0; i < 4; i++)
        {
            app.Clock.Advance(TimeSpan.FromSeconds(30));
            using (await app.Call(Post, $"/api/calls/{id}/alive", cookie: p.CA)) { }
        }
        await Sweep();   // B last heard 120 s ago
        var line = await Line(p.Chat, p.CA);
        Assert.Equal("completed", line.GetProperty("outcome").GetString());
        Assert.Equal(30, line.GetProperty("seconds").GetInt32());
    }

    [Fact]
    public async Task Someone_in_a_call_is_busy_and_it_is_a_missed_call_for_them()
    {
        var p = await Friends();
        await Answered(p);
        var (c, cc) = await app.Member();
        await app.Befriend(c, p.B);
        var chat = await app.OpenChat(cc, p.B.GomId);
        using var r = await Start(cc, chat);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await AppFixture.Json(r);
        Assert.Equal("ended", body.GetProperty("state").GetString());
        Assert.Equal("busy", body.GetProperty("outcome").GetString());
        Assert.Equal(1, await Unread(chat, p.CB));

        // and the caller is free again at once
        var (d, cd) = await app.Member();
        await app.Befriend(c, d);
        using var next = await Start(cc, await app.OpenChat(cc, d.GomId));
        Assert.Equal("ringing", (await AppFixture.Json(next)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Someone_in_a_call_cannot_start_another()
    {
        var p = await Friends();
        await Ringing(p);
        var (c, _) = await app.Member();
        await app.Befriend(p.A, c);
        using var r = await Start(p.CA, await app.OpenChat(p.CA, c.GomId));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("in_call", await ErrorOf(r));
    }

    [Fact]
    public async Task Calling_someone_who_is_calling_you_shows_their_call()
    {
        var p = await Friends();
        var theirs = await Ringing(p);
        using var r = await Start(p.CB, p.Chat);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = await AppFixture.Json(r);
        Assert.Equal("call_waiting", body.GetProperty("error").GetString());
        Assert.Equal(theirs, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Unfriending_or_signing_out_ends_the_call()
    {
        var p = await Friends();
        var id = await Answered(p);
        await using (var db = app.Db.NewContext())
        {
            var (low, high) = Friendship.Order(p.A.Id, p.B.Id);
            await db.Friendships.Where(f => f.UserLow == low && f.UserHigh == high).ExecuteDeleteAsync();
        }
        await Sweep();
        Assert.Equal("completed", (await Read(id, p.CA)).GetProperty("outcome").GetString());

        var q = await Friends();
        var ringing = await Ringing(q);
        using (await app.Call(Delete, "/api/session", cookie: q.CA)) { }
        await Sweep();
        await using var check = app.Db.NewContext();
        Assert.Equal(CallOutcome.Failed, (await check.Calls.SingleAsync(k => k.Id == ringing)).Outcome);
        Assert.False(await check.CallParties.AnyAsync(x => x.CallId == ringing));
    }

    // ---- the phones' offers mid-call

    [Fact]
    public async Task A_new_offer_mid_call_goes_to_the_other_device_only_and_waits_for_one_that_missed_it()
    {
        var p = await Friends();
        var id = await Answered(p);
        var other = await app.SignIn(p.B.GomId, userAgent: "Tablet");
        using var callee = await Connect(p.CB);
        using (var r = await app.Call(Post, $"/api/calls/{id}/sdp", new { type = "offer", sdp = Offer }, p.CA))
            Assert.Equal(1, (await AppFixture.Json(r)).GetProperty("rev").GetInt32());
        var frame = await NextCall(callee);
        Assert.Equal("sdp", frame.GetProperty("kind").GetString());
        Assert.Equal("offer", frame.GetProperty("type").GetString());
        Assert.Equal(1, frame.GetProperty("rev").GetInt32());
        Assert.Equal("offer", (await Read(id, p.CB)).GetProperty("sdp").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, (await Read(id, p.CA)).GetProperty("sdp").ValueKind);   // your own is not given back
        using (var r = await app.Call(Post, $"/api/calls/{id}/sdp", new { type = "offer", sdp = Offer }, other)) Assert.Equal("not_in_call", await ErrorOf(r));
        using (var r = await app.Call(Post, $"/api/calls/{id}/sdp", new { type = "pranswer", sdp = Offer }, p.CA)) Assert.Equal("invalid_sdp", await ErrorOf(r));
    }

    // ---- records

    [Fact]
    public async Task Deleting_an_account_takes_its_calls_and_their_lines()
    {
        var p = await Friends();
        var id = await Ringing(p);
        using (await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CA)) { }
        await Line(p.Chat, p.CB);
        var q = await Friends();
        var going = await Answered(q);
        using var survivor = await Connect(q.CB);
        using (var r = await app.Call(Delete, "/api/me", new { gomId = q.A.GomId }, q.CA)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("ended", (await NextCall(survivor)).GetProperty("kind").GetString());
        using (var r = await app.Call(Delete, "/api/me", new { gomId = p.A.GomId }, p.CA)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Calls.AnyAsync(k => k.Id == id || k.Id == going));
        Assert.False(await db.Messages.AnyAsync(m => m.ConversationId == p.Chat && m.Notice == NoticeKind.Call));
    }

    [Fact]
    public async Task A_trim_of_old_messages_takes_the_call_records_with_their_lines()
    {
        var p = await Friends();
        var id = await Ringing(p);
        using (await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CA)) { }
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var (_, owner) = await app.Owner();
        using (var r = await app.Call(Post, "/api/owner/trim/messages", new { before = app.Clock.Now, where = "direct", confirm = "Delete" }, owner))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Calls.AnyAsync(k => k.Id == id));
    }
}
