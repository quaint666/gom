using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Push;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Gom.Tests;

/// <summary>The app with calls switched on (they are off by default).</summary>
public sealed class CallFixture : AppFixture
{
    /// <summary>Where relay logins come from in tests: a fake Cloudflare.</summary>
    public FakeTurn Turn { get; } = new();

    /// <summary>Where the apps' wake-ups go in tests: a fake FCM.</summary>
    public FakeFcm Fcm { get; } = new();

    /// <summary>A Firebase service account's key file, made up for the tests (base64, as the setting takes it).</summary>
    public static readonly RSA FcmKey = RSA.Create(2048);
    public static readonly string FcmAccount = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
    {
        type = "service_account", project_id = "gom-test", client_email = "sender@gom-test.iam.gserviceaccount.com", private_key = FcmKey.ExportPkcs8PrivateKeyPem(),
    }));

    /// <summary>Registers the Android app on this session (so its person can take calls); returns the app's token.</summary>
    public async Task<string> App(string cookie, string? level = null)
    {
        var token = "fcm-" + Guid.NewGuid().ToString("N");
        using var r = await Call(HttpMethod.Put, "/api/push/fcm", new { token, level }, cookie);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, r.StatusCode);
        return token;
    }

    protected override void Settings(IWebHostBuilder b)
    {
        b.UseSetting("Calls:Enabled", "true");
        b.UseSetting("Push:PublicKey", PushFixture.Keys.Public);
        b.UseSetting("Push:PrivateKey", PushFixture.Keys.Private);
        b.UseSetting("Push:Subject", PushFixture.Subject);
        b.UseSetting("Turn:KeyId", "test-key");
        b.UseSetting("Turn:ApiToken", "test-token");
        b.UseSetting("Fcm:ServiceAccount", FcmAccount);
        b.ConfigureServices(s =>
        {
            s.RemoveAll<ITurnClient>();
            s.AddSingleton<ITurnClient>(Turn);
            s.RemoveAll<IFcmTransport>();
            s.AddSingleton<IFcmTransport>(Fcm);
        });
    }
}

/// <summary>FCM, faked: keeps every message, and answers what a test tells it to for a token (200 when nothing is said).</summary>
public sealed class FakeFcm : IFcmTransport
{
    public sealed record Sent(string Token, IReadOnlyDictionary<string, string> Data, int Ttl, bool High);

    public ConcurrentQueue<Sent> All { get; } = new();
    public ConcurrentDictionary<string, Func<FcmResult>> Answers { get; } = new();

    public Task<FcmResult> SendAsync(string token, IReadOnlyDictionary<string, string> data, int ttlSeconds, bool high, CancellationToken ct)
    {
        All.Enqueue(new Sent(token, data, ttlSeconds, high));
        return Task.FromResult(Answers.TryGetValue(token, out var answer) ? answer() : new FcmResult(200));
    }

    public List<Sent> To(string token) => All.Where(s => s.Token == token).ToList();

    /// <summary>Waits for the nth message to this app (they are sent off the request path).</summary>
    public async Task<Sent> Next(string token, int already = 0)
    {
        for (var i = 0; i < 150; i++)
        {
            var sent = To(token);
            if (sent.Count > already) return sent[already];
            await Task.Delay(20);
        }
        throw new TimeoutException($"no message {already + 1} to {token}");
    }
}

/// <summary>Cloudflare's credentials API, faked: each login has its own name, and every login taken back is kept to look at.</summary>
public sealed class FakeTurn : ITurnClient
{
    private int _count;
    public ConcurrentQueue<(int Seconds, string Tag, string Name)> Issued { get; } = new();
    public ConcurrentQueue<string> Revoked { get; } = new();
    /// <summary>Cloudflare not answering.</summary>
    public bool Down { get; set; }

    public Task<IReadOnlyList<IceServer>> IssueAsync(int seconds, string tag, CancellationToken ct)
    {
        if (Down) throw new HttpRequestException("the relay is down");
        var name = "login-" + Interlocked.Increment(ref _count);
        Issued.Enqueue((seconds, tag, name));
        // As Cloudflare answers, with a port 53 address added (its docs say it may give one).
        return Task.FromResult<IReadOnlyList<IceServer>>([
            new(["stun:stun.cloudflare.com:3478", "stun:stun.cloudflare.com:53"]),
            new(["turn:turn.cloudflare.com:3478?transport=udp", "turn:turn.cloudflare.com:53?transport=udp", "turns:turn.cloudflare.com:443?transport=tcp"], name, "secret-" + name),
        ]);
    }

    public Task RevokeAsync(string username, CancellationToken ct)
    {
        Revoked.Enqueue(username);
        return Task.CompletedTask;
    }

    /// <summary>Whether this login is taken back within a second (that happens off the request path).</summary>
    public async Task<bool> RevokedSoon(string name)
    {
        for (var i = 0; i < 50; i++)
        {
            if (Revoked.Contains(name)) return true;
            await Task.Delay(20);
        }
        return false;
    }
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

    /// <param name="AppA">The Android app's token on A's first session (each has the app, so each can be called).</param>
    private sealed record Pair(User A, string CA, User B, string CB, Guid Chat, string AppA, string AppB);

    private async Task<Pair> Friends()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return new Pair(a, ca, b, cb, await app.OpenChat(ca, b.GomId), await app.App(ca), await app.App(cb));
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
            scope.ServiceProvider.GetRequiredService<LiveDelivery>(), app.Factory.Services.GetRequiredService<Gom.Web.Push.PushQueue>(), app.Clock.GetUtcNow(), app.Factory.Services.GetRequiredService<IOptions<CallOptions>>().Value, default);
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
        await app.App(cd);
        using var next = await Start(cc, await app.OpenChat(cc, d.GomId));
        Assert.Equal("ringing", (await AppFixture.Json(next)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Someone_in_a_call_cannot_start_another()
    {
        var p = await Friends();
        await Ringing(p);
        var (c, cc) = await app.Member();
        await app.Befriend(p.A, c);
        await app.App(cc);
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

    // ---- the list and notifications (2b)

    private async Task<JsonElement> Listed(Guid chat, string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chat).Clone();
    }

    [Fact]
    public async Task A_call_is_what_last_happened_in_the_list_and_the_chat_rises()
    {
        var p = await Friends();
        var (c, _) = await app.Member();
        await app.Befriend(p.B, c);
        var other = await app.OpenChat(p.CB, c.GomId);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        using (await app.Call(Post, $"/api/conversations/{other}/messages", new { body = "Chào", clientId = Guid.NewGuid() }, p.CB)) { }
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var id = await Answered(p);
        app.Clock.Advance(TimeSpan.FromSeconds(65));
        using (await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CA)) { }

        using var r = await app.Call(Get, "/api/conversations", cookie: p.CB);
        var list = (await AppFixture.Json(r)).EnumerateArray().ToList();
        Assert.Equal(p.Chat, list[0].GetProperty("id").GetGuid());   // above the chat that had a message since
        var call = list[0].GetProperty("lastMessage").GetProperty("call");
        Assert.Equal("completed", call.GetProperty("outcome").GetString());
        Assert.Equal(65, call.GetProperty("seconds").GetInt32());
        Assert.Equal(p.A.Id, call.GetProperty("callerId").GetGuid());
    }

    [Fact]
    public async Task A_missed_call_is_told_to_the_person_called_even_in_a_muted_chat_and_names_nobody_at_the_quietest_level()
    {
        var p = await Friends();
        using var loud = new FakeBrowser();
        using var quiet = new FakeBrowser();
        var second = await app.SignIn(p.B.GomId, userAgent: "Phone");
        using (var r = await app.Call(HttpMethod.Put, "/api/push", loud.Subscription("name"), p.CB)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        using (var r = await app.Call(HttpMethod.Put, "/api/push", quiet.Subscription("nothing"), second)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        using (var r = await app.Call(HttpMethod.Put, $"/api/conversations/{p.Chat}/mute", new { muted = true }, p.CB)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        using var mine = new FakeBrowser();
        using (await app.Call(HttpMethod.Put, "/api/push", mine.Subscription("name"), p.CA)) { }

        var id = await Ringing(p);
        using (await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CA)) { }
        var note = loud.Read((await app.Push.Next(loud.Endpoint)).Body);
        Assert.Equal(p.A.DisplayName, note.GetProperty("title").GetString());
        Assert.Equal("Cuộc gọi nhỡ", note.GetProperty("body").GetString());
        Assert.Equal(p.Chat, note.GetProperty("c").GetGuid());
        var hushed = quiet.Read((await app.Push.Next(quiet.Endpoint)).Body);
        Assert.Equal("Gom", hushed.GetProperty("title").GetString());
        Assert.DoesNotContain(p.A.DisplayName, hushed.ToString());
        Assert.Empty(app.Push.To(mine.Endpoint));   // the caller is not told of their own call
    }

    [Fact]
    public async Task Declined_and_answered_calls_notify_nobody()
    {
        var p = await Friends();
        using var phone = new FakeBrowser();
        using (await app.Call(HttpMethod.Put, "/api/push", phone.Subscription("name"), p.CB)) { }
        var id = await Ringing(p);
        using (await app.Call(Post, $"/api/calls/{id}/decline", cookie: p.CB)) { }
        var done = await Answered(p);
        using (await app.Call(Post, $"/api/calls/{done}/hangup", cookie: p.CA)) { }
        await Task.Delay(300);
        Assert.Empty(app.Push.To(phone.Endpoint));
    }

    // ---- The relay (docs/voice-calls.md, decisions 21 to 24) ----

    private async Task<JsonElement> Login(string cookie, object request)
    {
        using var r = await app.Call(Post, "/api/calls/relay", request, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private static string NameOf(JsonElement login) =>
        login.GetProperty("iceServers").EnumerateArray().Single(s => s.TryGetProperty("username", out _)).GetProperty("username").GetString()!;

    [Fact]
    public async Task Before_calling_a_device_gets_a_relay_login_for_three_hours_without_port_53_and_tagged_with_nothing_about_who()
    {
        var p = await Friends();
        var login = await Login(p.CA, new { conversationId = p.Chat });
        Assert.True(login.GetProperty("relay").GetBoolean());
        Assert.Equal(3 * 3600, login.GetProperty("seconds").GetInt32());
        var urls = login.GetProperty("iceServers").EnumerateArray().SelectMany(s => s.GetProperty("urls").EnumerateArray()).Select(u => u.GetString()!).ToList();
        Assert.Contains("turns:turn.cloudflare.com:443?transport=tcp", urls);
        Assert.DoesNotContain(urls, u => u.Contains(":53", StringComparison.Ordinal));
        var issued = app.Turn.Issued.Single(i => i.Name == NameOf(login));
        Assert.Equal(3 * 3600, issued.Seconds);
        Assert.Matches("^[0-9a-f]{32}$", issued.Tag);
        Assert.DoesNotContain(p.A.Id.ToString("N"), issued.Tag);
        var again = NameOf(await Login(p.CA, new { conversationId = p.Chat }));
        Assert.Equal(issued.Tag, app.Turn.Issued.Single(i => i.Name == again).Tag);   // the same person, the same tag
    }

    [Fact]
    public async Task Both_sides_logins_are_taken_back_when_the_call_ends()
    {
        var p = await Friends();
        var caller = NameOf(await Login(p.CA, new { conversationId = p.Chat }));
        var id = await Ringing(p);
        var callee = NameOf(await Login(p.CB, new { callId = id }));
        using (var r = await app.Call(Post, $"/api/calls/{id}/accept", new { sdp = Answer, v = CallEndpoints.ProtocolVersion }, p.CB)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var fresh = NameOf(await Login(p.CA, new { callId = id }));   // a long call: a fresh login
        Assert.False(await app.Turn.RevokedSoon(caller));
        using (await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CB)) { }
        Assert.True(await app.Turn.RevokedSoon(caller));
        Assert.True(await app.Turn.RevokedSoon(callee));
        Assert.True(await app.Turn.RevokedSoon(fresh));
    }

    [Fact]
    public async Task A_login_no_call_claims_is_taken_back_a_minute_later_and_not_before()
    {
        var p = await Friends();
        var unused = NameOf(await Login(p.CA, new { conversationId = p.Chat }));
        app.Clock.Advance(TimeSpan.FromSeconds(59));
        await Sweep();
        Assert.False(await app.Turn.RevokedSoon(unused));
        app.Clock.Advance(TimeSpan.FromSeconds(2));
        await Sweep();
        Assert.True(await app.Turn.RevokedSoon(unused));
    }

    [Fact]
    public async Task A_login_for_a_device_that_lost_the_race_to_answer_is_taken_back_and_the_winners_is_kept()
    {
        var p = await Friends();
        var id = await Ringing(p);
        var phone = await app.SignIn(p.B.GomId, userAgent: "Phone");
        var winner = NameOf(await Login(p.CB, new { callId = id }));
        var loser = NameOf(await Login(phone, new { callId = id }));
        using (await app.Call(Post, $"/api/calls/{id}/accept", new { sdp = Answer, v = CallEndpoints.ProtocolVersion }, p.CB)) { }
        using (var r = await app.Call(Post, $"/api/calls/{id}/accept", new { sdp = Answer, v = CallEndpoints.ProtocolVersion }, phone)) Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        app.Clock.Advance(TimeSpan.FromSeconds(61));
        using (await app.Call(Post, $"/api/calls/{id}/alive", cookie: p.CA)) { }
        using (await app.Call(Post, $"/api/calls/{id}/alive", cookie: p.CB)) { }
        await Sweep();
        Assert.True(await app.Turn.RevokedSoon(loser));
        Assert.DoesNotContain(winner, app.Turn.Revoked);
    }

    [Fact]
    public async Task A_login_is_only_for_a_call_I_may_make_one_ringing_for_me_or_one_I_am_in()
    {
        var p = await Friends();
        var (stranger, cs) = await app.Member();
        await app.Befriend(stranger, p.A);
        var notFriends = await app.OpenChat(cs, p.A.GomId);
        await using (var db = app.Db.NewContext())
        {
            var (low, high) = Friendship.Order(stranger.Id, p.A.Id);
            await db.Friendships.Where(f => f.UserLow == low && f.UserHigh == high).ExecuteDeleteAsync();
        }
        using (var r = await app.Call(Post, "/api/calls/relay", new { conversationId = notFriends }, cs)) Assert.Equal("not_friends", await ErrorOf(r));
        using (var g = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { p.B.GomId } }, p.CA))
        {
            var group = (await AppFixture.Json(g)).GetProperty("id").GetGuid();
            using var r = await app.Call(Post, "/api/calls/relay", new { conversationId = group }, p.CA);
            Assert.Equal("invalid_call", await ErrorOf(r));
        }
        using (var r = await app.Call(Post, "/api/calls/relay", new { }, p.CA)) Assert.Equal("missing_fields", await ErrorOf(r));

        var id = await Ringing(p);
        using (var r = await app.Call(Post, "/api/calls/relay", new { callId = id }, cs)) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var otherCaller = await app.SignIn(p.A.GomId, userAgent: "Tablet");
        using (var r = await app.Call(Post, "/api/calls/relay", new { callId = id }, otherCaller)) Assert.Equal("not_in_call", await ErrorOf(r));
        using (await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CA)) { }
        using (var r = await app.Call(Post, "/api/calls/relay", new { callId = id }, p.CB)) Assert.Equal("call_ended", await ErrorOf(r));
        Assert.DoesNotContain(app.Turn.Issued, i => i.Tag == TurnLogins.Tag(stranger.Id, "test-token"));
    }

    [Fact]
    public async Task Logins_are_counted_as_calls_are()
    {
        var p = await Friends();
        for (var i = 0; i < 3; i++) await Login(p.CA, new { conversationId = p.Chat });
        using var r = await app.Call(Post, "/api/calls/relay", new { conversationId = p.Chat }, p.CA);
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Equal("too_many_calls", await ErrorOf(r));
        Assert.NotNull(r.Headers.RetryAfter);
    }

    [Fact]
    public async Task When_the_relay_does_not_answer_the_device_gets_a_direct_connection_only_and_is_told()
    {
        var p = await Friends();
        app.Turn.Down = true;
        try
        {
            var login = await Login(p.CA, new { conversationId = p.Chat });
            Assert.False(login.GetProperty("relay").GetBoolean());
            var only = Assert.Single(login.GetProperty("iceServers").EnumerateArray());
            Assert.Equal("stun:stun.cloudflare.com:3478", Assert.Single(only.GetProperty("urls").EnumerateArray()).GetString());
            Assert.False(only.TryGetProperty("username", out _));
        }
        finally { app.Turn.Down = false; }
    }

    [Fact]
    public async Task Logins_of_a_call_that_ended_without_this_server_hearing_are_taken_back_by_the_sweep()
    {
        var p = await Friends();
        var id = await Answered(p);
        var fresh = NameOf(await Login(p.CA, new { callId = id }));
        await using (var db = app.Db.NewContext())   // ended behind the server's back, as a fresh login racing the hang-up would leave it
            await db.Calls.Where(k => k.Id == id).ExecuteUpdateAsync(s => s.SetProperty(k => k.State, CallState.Ended).SetProperty(k => k.Outcome, CallOutcome.Completed));
        await Sweep();
        Assert.True(await app.Turn.RevokedSoon(fresh));
    }

    [Fact]
    public void Half_a_relay_key_stops_the_app_at_start()
    {
        var factory = app.Factory.WithWebHostBuilder(b => b.UseSetting("Turn:ApiToken", ""));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    // ---- Waking the app (FCM; docs/voice-calls.md, decisions 25 to 27) ----

    [Fact]
    public async Task A_call_wakes_every_app_of_the_person_called_with_only_its_id_for_as_long_as_it_rings()
    {
        var p = await Friends();
        var tablet = await app.App(await app.SignIn(p.B.GomId, userAgent: "Tablet"));
        var id = await Ringing(p);
        foreach (var token in new[] { p.AppB, tablet })
        {
            var ring = await app.Fcm.Next(token);
            Assert.Equal(new Dictionary<string, string> { ["t"] = "call", ["id"] = id.ToString() }, ring.Data);
            Assert.Equal(45, ring.Ttl);
            Assert.True(ring.High);
        }
        await Task.Delay(200);
        Assert.Empty(app.Fcm.To(p.AppA));   // never the caller's
    }

    [Fact]
    public async Task Answering_stops_the_other_apps_ringing_and_not_the_one_that_answered()
    {
        var p = await Friends();
        var tablet = await app.App(await app.SignIn(p.B.GomId, userAgent: "Tablet"));
        var id = await Answered(p);
        var stop = await app.Fcm.Next(tablet, 1);
        Assert.Equal(new Dictionary<string, string> { ["t"] = "call-end", ["id"] = id.ToString(), ["outcome"] = "answered" }, stop.Data);
        Assert.Equal(CallPusher.StopSeconds, stop.Ttl);

        using (await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CA)) { }
        await Task.Delay(300);
        Assert.Single(app.Fcm.To(p.AppB));   // only its ring: an answered call's end is not pushed
        Assert.Equal(2, app.Fcm.To(tablet).Count);
    }

    [Theory]
    [InlineData("caller", "missed")]
    [InlineData("callee", "declined")]
    public async Task A_call_that_ends_while_ringing_stops_the_apps_ringing_and_says_how(string who, string outcome)
    {
        var p = await Friends();
        var id = await Ringing(p);
        if (who == "caller") using (await app.Call(Post, $"/api/calls/{id}/hangup", cookie: p.CA)) { }
        else using (await app.Call(Post, $"/api/calls/{id}/decline", cookie: p.CB)) { }
        var stop = await app.Fcm.Next(p.AppB, 1);
        Assert.Equal("call-end", stop.Data["t"]);
        Assert.Equal(outcome, stop.Data["outcome"]);
    }

    [Fact]
    public async Task Someone_busy_is_not_rung_but_their_app_hears_of_the_missed_call()
    {
        var p = await Friends();
        await Answered(p);
        var (c, cc) = await app.Member();
        await app.Befriend(c, p.B);
        using (await Start(cc, await app.OpenChat(cc, p.B.GomId))) { }
        var next = await app.Fcm.Next(p.AppB, 1);
        Assert.Equal("call-end", next.Data["t"]);
        Assert.Equal("busy", next.Data["outcome"]);
        await Task.Delay(200);
        Assert.Equal(2, app.Fcm.To(p.AppB).Count);
    }

    [Fact]
    public async Task An_app_fcm_says_is_gone_is_forgotten()
    {
        var p = await Friends();
        app.Fcm.Answers[p.AppB] = () => new FcmResult(404);
        await Ringing(p);
        await app.Fcm.Next(p.AppB);
        for (var i = 0; i < 50 && await AppCount(p.CB) > 0; i++) await Task.Delay(20);
        Assert.Equal(0, await AppCount(p.CB));
    }

    private async Task<int> AppCount(string cookie)
    {
        await using var db = app.Db.NewContext();
        var gomId = (await AppFixture.Json(await app.Call(Get, "/api/me", cookie: cookie))).GetProperty("gomId").GetString();
        return await (from u in db.Users where u.GomId == gomId join s in db.Sessions on u.Id equals s.UserId join d in db.FcmDevices on s.Id equals d.SessionId select d).CountAsync();
    }

    [Fact]
    public async Task A_ring_fcm_is_too_busy_for_is_tried_once_more_after_the_wait_it_asks()
    {
        var p = await Friends();
        var answers = 0;
        app.Fcm.Answers[p.AppB] = () => Interlocked.Increment(ref answers) == 1 ? new FcmResult(503, TimeSpan.FromMilliseconds(200)) : new FcmResult(200);
        await Ringing(p);
        var again = await app.Fcm.Next(p.AppB, 1);
        Assert.Equal("call", again.Data["t"]);
    }

    [Fact]
    public async Task Someone_with_no_app_signed_in_cannot_be_called()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        using (var r = await Start(ca, chat)) { Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("cannot_take_calls", await ErrorOf(r)); }
        using (var r = await app.Call(Post, "/api/calls/relay", new { conversationId = chat }, ca)) Assert.Equal("cannot_take_calls", await ErrorOf(r));

        // an app on a session that was signed out does not count
        await app.App(cb);
        using (await app.Call(Delete, "/api/session", cookie: cb)) { }
        using (var r = await Start(ca, chat)) Assert.Equal("cannot_take_calls", await ErrorOf(r));
    }
}
