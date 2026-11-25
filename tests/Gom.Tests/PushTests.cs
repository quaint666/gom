using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gom.Web.Data;
using Gom.Web.Push;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gom.Tests;

/// <summary>A push service that keeps what it is sent, and answers what a test tells it to.</summary>
public sealed class FakePush : IPushTransport
{
    public sealed record Sent(string Endpoint, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    public ConcurrentQueue<Sent> All { get; } = new();

    /// <summary>The answer for an address: a status, or an exception thrown. 201 when nothing is said.</summary>
    public ConcurrentDictionary<string, Func<Task<int>>> Answers { get; } = new();

    public async Task<int> SendAsync(string endpoint, IReadOnlyDictionary<string, string> headers, byte[] body, CancellationToken ct)
    {
        All.Enqueue(new Sent(endpoint, headers, body));
        return Answers.TryGetValue(endpoint, out var answer) ? await answer().WaitAsync(ct) : 201;
    }

    public List<Sent> To(string endpoint) => All.Where(s => s.Endpoint == endpoint).ToList();

    /// <summary>Waits for a push to this address (the background sender is not awaited by the request).</summary>
    public async Task<Sent> Next(string endpoint, int already = 0)
    {
        for (var i = 0; i < 100; i++)
        {
            var sent = To(endpoint);
            if (sent.Count > already) return sent[already];
            await Task.Delay(50);
        }
        throw new TimeoutException("no push arrived");
    }
}

/// <summary>The app with notifications switched on (a key pair made for the run).</summary>
public sealed class PushFixture : AppFixture
{
    public static readonly (string Public, string Private) Keys = WebPushCrypto.NewVapidKeys();
    public const string Subject = "mailto:push@gom.test";

    protected override void Settings(IWebHostBuilder b)
    {
        b.UseSetting("Push:PublicKey", Keys.Public);
        b.UseSetting("Push:PrivateKey", Keys.Private);
        b.UseSetting("Push:Subject", Subject);
    }
}

/// <summary>A browser's side of a subscription: its key pair and secret, and reading what was encrypted for it.</summary>
public sealed class FakeBrowser : IDisposable
{
    public ECDiffieHellman Key { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public byte[] Auth { get; } = RandomNumberGenerator.GetBytes(16);
    public string Endpoint { get; } = "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N");

    public byte[] Public
    {
        get { var q = Key.ExportParameters(false).Q; return [4, .. q.X!, .. q.Y!]; }
    }

    public object Subscription(string? level = null) => new
    {
        endpoint = Endpoint,
        keys = new { p256dh = Base64Url.EncodeToString(Public), auth = Base64Url.EncodeToString(Auth) },
        level,
    };

    /// <summary>RFC 8291 from the receiving end, written separately from the app's code so a shared mistake cannot pass.</summary>
    public JsonElement Read(byte[] body)
    {
        var salt = body[..16];
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16)));
        int idLength = body[20];
        var asPublic = body[21..(21 + idLength)];
        using var sender = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] } });
        var secret = Key.DeriveRawSecretAgreement(sender.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Auth, [.. "WebPush: info\0"u8, .. Public, .. asPublic]);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());
        var rest = body[(21 + idLength)..];
        var plain = new byte[rest.Length - 16];
        using (var gcm = new AesGcm(cek, 16)) gcm.Decrypt(nonce, rest[..^16], rest[^16..], plain);
        Assert.Equal(2, plain[^1]);   // the last record, no padding
        return JsonDocument.Parse(plain.AsMemory(0, plain.Length - 1)).RootElement.Clone();
    }

    public void Dispose() => Key.Dispose();
}

/// <summary>The encryption and the signature, against the standards' own numbers.</summary>
public class PushCryptoTests
{
    private static byte[] B(string s) => Base64Url.DecodeFromChars(s.Replace(" ", ""));

    [Fact]
    public void Encryption_gives_RFC_8291s_example_byte_for_byte()
    {
        var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new ECPoint
            {
                X = B("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8")[1..33],
                Y = B("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8")[33..65],
            },
        });
        var body = WebPushCrypto.Encrypt(
            B("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
            B("BTBZMqHH6r4Tts7J_aSIgg"),
            Encoding.ASCII.GetBytes("When I grow up, I want to be a watermelon"),
            sender, B("DGv6ra1nlYgDCS1FRnbzlw"));
        var expected = B("DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27ml" +
                         "mlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPT" +
                         "pK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN");
        Assert.Equal(expected, body);
    }

    [Fact]
    public void A_browser_key_must_be_a_point_on_P256()
    {
        using var real = new FakeBrowser();
        Assert.True(WebPushCrypto.IsPublicKey(real.Public));
        var off = real.Public.ToArray(); off[64] ^= 1;
        Assert.False(WebPushCrypto.IsPublicKey(off));
        Assert.False(WebPushCrypto.IsPublicKey(real.Public[..64]));
        var compressed = real.Public.ToArray(); compressed[0] = 2;
        Assert.False(WebPushCrypto.IsPublicKey(compressed));
    }

    [Fact]
    public void The_signature_names_the_push_service_and_is_made_with_the_servers_key()
    {
        var (pub, priv) = WebPushCrypto.NewVapidKeys();
        var now = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
        var header = WebPushCrypto.VapidHeader("https://fcm.googleapis.com/fcm/send/abc", "mailto:x@gom.test", pub, priv, now);

        Assert.StartsWith("vapid t=", header);
        Assert.EndsWith($", k={pub}", header);
        var token = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = token.Split('.');
        var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement;
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        Assert.Equal("mailto:x@gom.test", claims.GetProperty("sub").GetString());
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());
        Assert.Equal("ES256", JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement.GetProperty("alg").GetString());

        var key = Base64Url.DecodeFromChars(pub);
        using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = key[1..33], Y = key[33..65] } });
        Assert.True(ecdsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Theory]
    [InlineData(0, 0, "Tin nhắn mới")]
    [InlineData(2, 0, "Đã gửi 2 ảnh")]
    [InlineData(0, 1, "Đã gửi 1 file")]
    [InlineData(3, 2, "Đã gửi 3 ảnh và 2 file")]
    public void A_notification_says_what_kind_of_message_never_its_words(int images, int files, string what)
    {
        var job = new PushJob(Guid.NewGuid(), 5, Guid.NewGuid(), "Minh", images, files);
        Assert.Equal(("Minh", what), PushSender.Words(job, ConversationKind.Direct, null, Gom.Web.ServerText.Vi));
        Assert.Equal(("Nhà mình", $"Minh: {what}"), PushSender.Words(job, ConversationKind.Group, "Nhà mình", Gom.Web.ServerText.Vi));
    }

    [Fact]
    public void A_device_that_chose_the_words_sees_them_and_the_kind_of_message_when_there_are_none()
    {
        var text = new PushJob(Guid.NewGuid(), 5, Guid.NewGuid(), "Minh", 2, 0, Text: "ảnh hôm qua");
        Assert.Equal(("Minh", "ảnh hôm qua"), PushSender.Words(text, ConversationKind.Direct, null, Gom.Web.ServerText.Vi, words: true));
        Assert.Equal(("Nhà mình", "Minh: ảnh hôm qua"), PushSender.Words(text, ConversationKind.Group, "Nhà mình", Gom.Web.ServerText.Vi, words: true));
        Assert.Equal(("Minh", "Đã gửi 2 ảnh"), PushSender.Words(text, ConversationKind.Direct, null, Gom.Web.ServerText.Vi));
        var pictures = new PushJob(Guid.NewGuid(), 5, Guid.NewGuid(), "Minh", 2, 0);
        Assert.Equal(("Minh", "Đã gửi 2 ảnh"), PushSender.Words(pictures, ConversationKind.Direct, null, Gom.Web.ServerText.Vi, words: true));
    }

    [Fact]
    public void Someone_who_reads_English_gets_the_kind_of_message_in_English()
    {
        var en = Gom.Web.ServerText.En;
        Assert.Equal(("Minh", "Sent a picture"), PushSender.Words(new PushJob(Guid.NewGuid(), 5, Guid.NewGuid(), "Minh", 1, 0), ConversationKind.Direct, null, en));
        Assert.Equal(("Minh", "Sent 3 pictures and 1 file"), PushSender.Words(new PushJob(Guid.NewGuid(), 5, Guid.NewGuid(), "Minh", 3, 1), ConversationKind.Direct, null, en));
        Assert.Equal(("Group", "Minh: New message"), PushSender.Words(new PushJob(Guid.NewGuid(), 5, Guid.NewGuid(), "Minh", 0, 0), ConversationKind.Group, null, en));
    }

    [Fact]
    public void A_sticker_is_named_as_one()
    {
        var job = new PushJob(Guid.NewGuid(), 5, Guid.NewGuid(), "Minh", 0, 0, Sticker: true);
        Assert.Equal(("Minh", "Đã gửi 1 nhãn dán"), PushSender.Words(job, ConversationKind.Direct, null, Gom.Web.ServerText.Vi));
        Assert.Equal(("Nhà mình", "Minh: Đã gửi 1 nhãn dán"), PushSender.Words(job, ConversationKind.Group, "Nhà mình", Gom.Web.ServerText.Vi));
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/x", true)]
    [InlineData("https://jmt17.google.com/fcm/send/x", true)]
    [InlineData("https://evil-jmt17.google.com/fcm/send/x", false)]
    [InlineData("https://jmt17.google.com.evil.test/x", false)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/x", true)]
    [InlineData("https://web.push.apple.com/x", true)]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=x", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/x", false)]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/x", false)]
    [InlineData("https://fcm.googleapis.com.evil.test/x", false)]
    [InlineData("https://evilfcm.googleapis.com/x", false)]
    [InlineData("https://push.services.mozilla.com/x", false)]
    [InlineData("https://evilpush.services.mozilla.com/x", false)]
    [InlineData("https://user@fcm.googleapis.com/x", false)]
    [InlineData("https://192.168.1.10/x", false)]
    [InlineData("not a url", false)]
    public void Only_known_push_services_are_accepted(string endpoint, bool allowed) =>
        Assert.Equal(allowed, new PushOptions().Allows(endpoint));
}

/// <summary>Push is off without keys: nothing to register with.</summary>
public class PushOffTests(AppFixture app) : IClassFixture<AppFixture>
{
    [Fact]
    public async Task Without_keys_the_endpoints_say_there_is_nothing_here()
    {
        var (_, cookie) = await app.Member();
        using var browser = new FakeBrowser();
        using var get = await app.Call(HttpMethod.Get, "/api/push", cookie: cookie);
        using var put = await app.Call(HttpMethod.Put, "/api/push", browser.Subscription(), cookie);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }
}

/// <summary>Registering a device, and who is told about a message.</summary>
public class PushTests(PushFixture app) : IClassFixture<PushFixture>
{
    private async Task<JsonElement> State(string cookie)
    {
        using var r = await app.Call(HttpMethod.Get, "/api/push", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private async Task Subscribe(string cookie, FakeBrowser browser, string? level = null)
    {
        using var r = await app.Call(HttpMethod.Put, "/api/push", browser.Subscription(level), cookie);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    private async Task<(Gom.Web.Data.User A, string CA, Gom.Web.Data.User B, string CB, Guid Chat)> Pair()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return (a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private async Task<long> Say(string cookie, Guid chat, string body = "xin chào")
    {
        using var r = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("seq").GetInt64();
    }

    /// <summary>Runs the sender for one message and waits for it, so a test can say "nothing was sent" without waiting on a clock.</summary>
    private Task Deliver(Guid chat, long seq, Gom.Web.Data.User from) =>
        app.Factory.Services.GetRequiredService<PushSender>().DeliverAsync(new PushJob(chat, seq, from.Id, from.DisplayName, 0, 0), CancellationToken.None);

    private async Task Db(Func<GomDbContext, Task> change)
    {
        await using var db = app.Db.NewContext();
        await change(db);
    }

    [Fact]
    public void The_push_service_addresses_never_reach_the_log()
    {
        // The address of a push is the device's token: the HTTP client's own request lines must stay out of an ordinary log.
        var logs = app.Factory.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
        foreach (var category in new[] { "System.Net.Http.HttpClient.push.ClientHandler", "System.Net.Http.HttpClient.push.LogicalHandler" })
        {
            var log = logs.CreateLogger(category);
            Assert.False(log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information));
            Assert.True(log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning));
        }
    }

    [Fact]
    public async Task The_key_is_given_and_a_device_is_off_until_it_registers()
    {
        var (_, cookie) = await app.Member();
        var state = await State(cookie);
        Assert.Equal(PushFixture.Keys.Public, state.GetProperty("key").GetString());
        Assert.False(state.GetProperty("subscribed").GetBoolean());

        using var browser = new FakeBrowser();
        await Subscribe(cookie, browser, "nothing");
        state = await State(cookie);
        Assert.True(state.GetProperty("subscribed").GetBoolean());
        Assert.Equal("nothing", state.GetProperty("level").GetString());

        using var off = await app.Call(HttpMethod.Delete, "/api/push", cookie: cookie);
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.False((await State(cookie)).GetProperty("subscribed").GetBoolean());
    }

    [Fact]
    public async Task A_registration_with_a_foreign_address_bad_keys_or_an_unknown_level_is_refused()
    {
        var (_, cookie) = await app.Member();
        using var browser = new FakeBrowser();
        var key = Base64Url.EncodeToString(browser.Public);
        var auth = Base64Url.EncodeToString(browser.Auth);
        var bent = browser.Public.ToArray(); bent[64] ^= 1;
        foreach (var (body, error) in new (object, string)[]
        {
            (new { endpoint = "https://gom-internal.test/hook", keys = new { p256dh = key, auth } }, "invalid_endpoint"),
            (new { endpoint = "http://fcm.googleapis.com/fcm/send/x", keys = new { p256dh = key, auth } }, "invalid_endpoint"),
            (new { keys = new { p256dh = key, auth } }, "invalid_endpoint"),
            (new { endpoint = browser.Endpoint, keys = new { p256dh = Base64Url.EncodeToString(bent), auth } }, "invalid_keys"),
            (new { endpoint = browser.Endpoint, keys = new { p256dh = key, auth = Base64Url.EncodeToString(new byte[15]) } }, "invalid_keys"),
            (new { endpoint = browser.Endpoint, keys = new { p256dh = "!!", auth } }, "invalid_keys"),
            (new { endpoint = browser.Endpoint }, "invalid_keys"),
            (new { endpoint = browser.Endpoint, keys = new { p256dh = key, auth }, level = "loud" }, "invalid_level"),
        })
        {
            using var r = await app.Call(HttpMethod.Put, "/api/push", body, cookie);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal(error, (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        Assert.False((await State(cookie)).GetProperty("subscribed").GetBoolean());
    }

    [Fact]
    public async Task The_same_browser_signed_in_again_moves_its_registration_to_the_new_session()
    {
        var (user, first) = await app.Member();
        using var browser = new FakeBrowser();
        await Subscribe(first, browser);
        var second = await app.SignIn(user.GomId);
        await Subscribe(second, browser);
        Assert.False((await State(first)).GetProperty("subscribed").GetBoolean());
        Assert.True((await State(second)).GetProperty("subscribed").GetBoolean());
    }

    [Fact]
    public async Task A_sticker_sent_is_notified_as_a_sticker()
    {
        var p = await Pair();
        using var phone = new FakeBrowser();
        await Subscribe(p.CA, phone);
        using var r = await app.Call(HttpMethod.Post, $"/api/conversations/{p.Chat}/messages", new { clientId = Guid.NewGuid(), sticker = "1f602" }, p.CB);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var note = phone.Read((await app.Push.Next(phone.Endpoint)).Body);
        Assert.Equal("Đã gửi 1 nhãn dán", note.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_message_notifies_the_others_devices_with_who_and_where_and_not_its_words()
    {
        var p = await Pair();
        using var phone = new FakeBrowser();
        using var mine = new FakeBrowser();
        await Subscribe(p.CA, phone, "name");
        await Subscribe(p.CB, mine);

        var seq = await Say(p.CB, p.Chat, "bí mật");
        var sent = await app.Push.Next(phone.Endpoint);
        var note = phone.Read(sent.Body);
        Assert.Equal(p.Chat, note.GetProperty("c").GetGuid());
        Assert.Equal(p.B.DisplayName, note.GetProperty("title").GetString());
        Assert.Equal("Tin nhắn mới", note.GetProperty("body").GetString());
        Assert.DoesNotContain("bí mật", Encoding.UTF8.GetString(sent.Body));

        Assert.Equal("aes128gcm", sent.Headers["Content-Encoding"]);
        Assert.Equal("high", sent.Headers["Urgency"]);
        Assert.Equal("2419200", sent.Headers["TTL"]);
        Assert.Equal(Base64Url.EncodeToString(p.Chat.ToByteArray()), sent.Headers["Topic"]);
        Assert.EndsWith($"k={PushFixture.Keys.Public}", sent.Headers["Authorization"]);

        // The writer's own device hears nothing: the same message again, awaited this time, still reaches only the other side.
        await Deliver(p.Chat, seq, p.B);
        Assert.Empty(app.Push.To(mine.Endpoint));
        Assert.Equal(2, app.Push.To(phone.Endpoint).Count);
    }

    [Fact]
    public async Task At_the_nothing_level_a_notification_names_neither_person_nor_conversation()
    {
        var p = await Pair();
        using var phone = new FakeBrowser();
        await Subscribe(p.CA, phone, "nothing");
        await Deliver(p.Chat, await Say(p.CB, p.Chat), p.B);
        var note = phone.Read(app.Push.To(phone.Endpoint)[0].Body);
        Assert.Equal("Gom", note.GetProperty("title").GetString());
        Assert.Equal("Tin nhắn mới", note.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Someone_who_muted_hears_nothing_until_a_mute_for_a_while_ends()
    {
        var p = await Pair();
        using var phone = new FakeBrowser();
        await Subscribe(p.CA, phone);
        var seq = await Say(p.CB, p.Chat);
        await app.Push.Next(phone.Endpoint);   // the background send of that message is done
        var before = app.Push.To(phone.Endpoint).Count;

        await Db(db => db.Members.Where(m => m.ConversationId == p.Chat && m.UserId == p.A.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Muted, true)));
        await Deliver(p.Chat, seq, p.B);
        Assert.Equal(before, app.Push.To(phone.Endpoint).Count);

        await Db(db => db.Members.Where(m => m.ConversationId == p.Chat && m.UserId == p.A.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Muted, false).SetProperty(m => m.MutedUntil, app.Clock.Now.AddHours(1))));
        await Deliver(p.Chat, seq, p.B);
        Assert.Equal(before, app.Push.To(phone.Endpoint).Count);

        await Db(db => db.Members.Where(m => m.ConversationId == p.Chat && m.UserId == p.A.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.MutedUntil, app.Clock.Now)));
        await Deliver(p.Chat, seq, p.B);
        Assert.Equal(before + 1, app.Push.To(phone.Endpoint).Count);
    }

    [Fact]
    public async Task Nobody_hears_of_a_message_from_before_they_joined()
    {
        var p = await Pair();
        using var phone = new FakeBrowser();
        await Subscribe(p.CA, phone);
        await Db(db => db.Members.Where(m => m.ConversationId == p.Chat && m.UserId == p.A.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.JoinedSeq, 50)));
        await Deliver(p.Chat, 50, p.B);
        Assert.Empty(app.Push.To(phone.Endpoint));
        await Deliver(p.Chat, 51, p.B);
        Assert.Single(app.Push.To(phone.Endpoint));
    }

    [Fact]
    public async Task A_signed_out_or_run_out_session_and_a_disabled_account_hear_nothing()
    {
        var p = await Pair();
        using var phone = new FakeBrowser();
        await Subscribe(p.CA, phone);
        var session = (await app.SessionsOf(p.A.Id)).Single().Id;
        var seq = await Say(p.CB, p.Chat);
        await app.Push.Next(phone.Endpoint);
        var before = app.Push.To(phone.Endpoint).Count;

        await Db(db => db.Sessions.Where(s => s.Id == session).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, app.Clock.Now)));
        await Deliver(p.Chat, seq, p.B);
        await Db(db => db.Sessions.Where(s => s.Id == session).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)null).SetProperty(x => x.ExpiresAt, app.Clock.Now)));
        await Deliver(p.Chat, seq, p.B);
        await Db(db => db.Sessions.Where(s => s.Id == session).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, app.Clock.Now.AddDays(1))));
        await Db(db => db.Users.Where(u => u.Id == p.A.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisabledAt, app.Clock.Now)));
        await Deliver(p.Chat, seq, p.B);
        Assert.Equal(before, app.Push.To(phone.Endpoint).Count);

        await Db(db => db.Users.Where(u => u.Id == p.A.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisabledAt, (DateTimeOffset?)null)));
        await Deliver(p.Chat, seq, p.B);
        Assert.Equal(before + 1, app.Push.To(phone.Endpoint).Count);
    }

    [Fact]
    public async Task A_device_its_push_service_says_is_gone_is_forgotten_and_other_failures_keep_it()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        using var gone = new FakeBrowser();
        await Subscribe(cb, gone);
        var seq = await Say(ca, chat);
        await app.Push.Next(gone.Endpoint);

        app.Push.Answers[gone.Endpoint] = () => Task.FromResult(500);
        await Deliver(chat, seq, a);
        Assert.True((await State(cb)).GetProperty("subscribed").GetBoolean());

        app.Push.Answers[gone.Endpoint] = () => Task.FromResult(410);
        await Deliver(chat, seq, a);
        Assert.False((await State(cb)).GetProperty("subscribed").GetBoolean());

        using var lost = new FakeBrowser();
        await Subscribe(cb, lost);
        app.Push.Answers[lost.Endpoint] = () => Task.FromResult(404);
        await Deliver(chat, seq, a);
        Assert.False((await State(cb)).GetProperty("subscribed").GetBoolean());
    }

    [Fact]
    public async Task One_failing_device_does_not_stop_the_others()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        var other = await app.SignIn(b.GomId);
        using var broken = new FakeBrowser();
        using var fine = new FakeBrowser();
        await Subscribe(cb, broken);
        await Subscribe(other, fine);
        app.Push.Answers[broken.Endpoint] = () => throw new HttpRequestException("down");
        await Deliver(chat, 1, a);
        Assert.Single(app.Push.To(fine.Endpoint));
        Assert.True((await State(cb)).GetProperty("subscribed").GetBoolean());
    }

    [Fact]
    public async Task A_push_service_that_hangs_does_not_slow_the_send()
    {
        var p = await Pair();
        using var phone = new FakeBrowser();
        await Subscribe(p.CA, phone);
        var stuck = new TaskCompletionSource<int>();
        app.Push.Answers[phone.Endpoint] = () => stuck.Task;
        try
        {
            var send = Say(p.CB, p.Chat);
            Assert.Same(send, await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(5))));
            await send;
            await app.Push.Next(phone.Endpoint);   // the push did start, and is waiting
        }
        finally { stuck.SetResult(201); }
    }

    private async Task<(Guid Id, long Seq)> Said(string cookie, Guid chat, string body)
    {
        using var r = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var m = await AppFixture.Json(r);
        return (m.GetProperty("id").GetGuid(), m.GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task A_new_device_shows_the_words_unless_it_chooses_less_and_a_long_message_is_cut()
    {
        var p = await Pair();
        Assert.Equal("words", (await State(p.CA)).GetProperty("level").GetString());   // what a device not yet registered is offered
        using var phone = new FakeBrowser();
        await Subscribe(p.CA, phone);
        Assert.Equal("words", (await State(p.CA)).GetProperty("level").GetString());

        var (id, _) = await Said(p.CB, p.Chat, "mật khẩu wifi là 1234");
        var note = phone.Read((await app.Push.Next(phone.Endpoint)).Body);
        Assert.Equal("mật khẩu wifi là 1234", note.GetProperty("body").GetString());
        Assert.Equal(id, note.GetProperty("m").GetGuid());

        var flags = string.Concat(Enumerable.Repeat("🇻🇳", 150));   // one character each, as a person counts them
        await Said(p.CB, p.Chat, flags);
        note = phone.Read((await app.Push.Next(phone.Endpoint, 1)).Body);
        Assert.Equal(string.Concat(Enumerable.Repeat("🇻🇳", 100)), note.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_message_taken_back_replaces_its_notification_on_the_devices_that_showed_its_words_only()
    {
        var p = await Pair();
        using var words = new FakeBrowser();
        using var name = new FakeBrowser();
        await Subscribe(p.CA, words, "words");
        var other = await app.SignIn(p.A.GomId);
        await Subscribe(other, name, "name");

        var (id, _) = await Said(p.CB, p.Chat, "gửi nhầm");
        await app.Push.Next(words.Endpoint);
        await app.Push.Next(name.Endpoint);
        using var r = await app.Call(HttpMethod.Delete, $"/api/conversations/{p.Chat}/messages/{id}", cookie: p.CB);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        var sent = await app.Push.Next(words.Endpoint, 1);
        var note = words.Read(sent.Body);
        Assert.True(note.GetProperty("recalled").GetBoolean());
        Assert.Equal(id, note.GetProperty("m").GetGuid());
        Assert.Equal("Tin nhắn đã được thu hồi", note.GetProperty("body").GetString());
        Assert.Equal("normal", sent.Headers["Urgency"]);
        Assert.Equal(Base64Url.EncodeToString(p.Chat.ToByteArray()), sent.Headers["Topic"]);   // replaces the message's push if it is still waiting
        Assert.Single(app.Push.To(name.Endpoint));   // the recall is that device's only job after the message, and it skipped it
    }

    [Fact]
    public async Task The_owner_hears_of_a_request_to_join_and_the_tap_opens_the_owner_s_page()
    {
        var (_, owner) = await app.Owner();
        using var phone = new FakeBrowser();
        await Subscribe(owner, phone, "name");
        using (var open = await app.Call(HttpMethod.Put, "/api/sign-up-open", new { open = true }, owner)) Assert.Equal(HttpStatusCode.NoContent, open.StatusCode);
        using var r = await app.Call(HttpMethod.Post, "/api/sign-up", new { gomId = "bi_" + Guid.NewGuid().ToString("N")[..8], password = AppFixture.Password, displayName = "Bi", note = "Con của cô Lan" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var note = phone.Read((await app.Push.Next(phone.Endpoint)).Body);
        Assert.Equal("Bi xin tham gia Gom", note.GetProperty("body").GetString());
        Assert.Equal("owner", note.GetProperty("open").GetString());
        Assert.DoesNotContain("Con của cô Lan", note.ToString());   // the note stays on the owner's page
    }

    [Fact]
    public async Task Each_device_hears_in_its_person_s_language()
    {
        var p = await Pair();
        using var phone = new FakeBrowser();
        await Subscribe(p.CA, phone, "name");
        using (var r = await app.Call(HttpMethod.Put, "/api/me/language", new { language = "en" }, p.CA)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await Deliver(p.Chat, await Say(p.CB, p.Chat), p.B);
        Assert.Equal("New message", phone.Read(app.Push.To(phone.Endpoint)[0].Body).GetProperty("body").GetString());
    }
}
