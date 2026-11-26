using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gom.Web.Push;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>
/// The Android app's wake-ups (FCM): registering a device, the service account it is sent with, Google's login and the message's shape
/// (against Google's docs, no network). What a call sends is in <see cref="CallTests"/>.
/// </summary>
public class FcmTests(CallFixture app) : IClassFixture<CallFixture>
{
    private async Task<List<(string Token, string Level)>> Apps(Guid user)
    {
        await using var db = app.Db.NewContext();
        return [.. (await (from s in db.Sessions where s.UserId == user join d in db.FcmDevices on s.Id equals d.SessionId select new { d.Token, d.Level }).ToListAsync())
            .Select(d => (d.Token, d.Level.ToString().ToLowerInvariant()))];
    }

    [Fact]
    public async Task An_app_registers_on_its_session_at_its_level_and_registering_again_replaces_it()
    {
        var (user, cookie) = await app.Member();
        await app.App(cookie, "nothing");
        var token = await app.App(cookie);
        Assert.Equal([(token, "words")], await Apps(user.Id));
        using (var r = await app.Call(HttpMethod.Delete, "/api/push/fcm", cookie: cookie)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Empty(await Apps(user.Id));
    }

    [Fact]
    public async Task The_same_app_signed_in_again_moves_to_its_new_session()
    {
        var (user, first) = await app.Member();
        var token = "fcm-" + Guid.NewGuid().ToString("N");
        foreach (var cookie in new[] { first, await app.SignIn(user.GomId, userAgent: "Phone") })
            using (var r = await app.Call(HttpMethod.Put, "/api/push/fcm", new { token }, cookie)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.Equal(1, await db.FcmDevices.CountAsync(d => d.Token == token));
    }

    [Fact]
    public async Task The_app_takes_the_place_of_the_browsers_notifications_on_its_session()
    {
        var (user, cookie) = await app.Member();
        using var browser = new FakeBrowser();
        using (var r = await app.Call(HttpMethod.Put, "/api/push", browser.Subscription(), cookie)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var other = await app.SignIn(user.GomId, userAgent: "Laptop");
        using var laptop = new FakeBrowser();
        using (var r = await app.Call(HttpMethod.Put, "/api/push", laptop.Subscription(), other)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await app.App(cookie);
        await using var db = app.Db.NewContext();
        Assert.Equal([laptop.Endpoint], await (from s in db.Sessions where s.UserId == user.Id join p in db.PushSubscriptions on s.Id equals p.SessionId select p.Endpoint).ToListAsync());
    }

    [Theory]
    [InlineData("", null, "invalid_token")]
    [InlineData("has space", null, "invalid_token")]
    [InlineData("tab\there", null, "invalid_token")]
    [InlineData("ok-token", "loud", "invalid_level")]
    public async Task Only_a_token_and_a_level_gom_knows_are_taken(string token, string? level, string error)
    {
        var (_, cookie) = await app.Member();
        using var r = await app.Call(HttpMethod.Put, "/api/push/fcm", new { token, level }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(error, (await AppFixture.Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_token_over_1024_characters_is_refused()
    {
        var (_, cookie) = await app.Member();
        using var r = await app.Call(HttpMethod.Put, "/api/push/fcm", new { token = new string('a', 1025) }, cookie);
        Assert.Equal("invalid_token", (await AppFixture.Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Deleting_an_account_forgets_its_apps()
    {
        var (user, cookie) = await app.Member();
        await app.App(cookie);
        using (var r = await app.Call(HttpMethod.Delete, "/api/me", new { gomId = user.GomId }, cookie)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Empty(await Apps(user.Id));
    }

    [Fact]
    public async Task Without_a_service_account_there_is_nothing_to_register_with()
    {
        var factory = app.Factory.WithWebHostBuilder(b => b.UseSetting("Fcm:ServiceAccount", ""));
        var (_, cookie) = await app.Member();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/push/fcm") { Content = JsonContent(new { token = "abc" }) };
        request.Headers.Add("Cookie", $"__Host-gom={cookie}");
        request.Headers.Add("Origin", AppFixture.Origin);
        using var r = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    private static StringContent JsonContent(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    // ---- The service account ----

    private static string KeyFile(object o) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(o));

    [Fact]
    public void A_key_file_gives_the_project_the_sender_and_the_key()
    {
        var account = FcmAccount.Parse(CallFixture.FcmAccount);
        Assert.True(account.Enabled);
        Assert.Equal("gom-test", account.ProjectId);
        Assert.Equal("sender@gom-test.iam.gserviceaccount.com", account.ClientEmail);
        Assert.False(FcmAccount.Parse("").Enabled);
    }

    public static TheoryData<string> BadKeyFiles => new()
    {
        "not base64!",
        Convert.ToBase64String("not json"u8.ToArray()),
        KeyFile(new { project_id = "p", client_email = "e" }),
        KeyFile(new { project_id = "p", private_key = CallFixture.FcmKey.ExportPkcs8PrivateKeyPem() }),
        KeyFile(new { project_id = "p", client_email = "e", private_key = "-----BEGIN PRIVATE KEY-----\nnope\n-----END PRIVATE KEY-----" }),
    };

    [Theory]
    [MemberData(nameof(BadKeyFiles))]
    public void A_key_file_that_cannot_be_used_is_refused(string value) =>
        Assert.Throws<InvalidOperationException>(() => FcmAccount.Parse(value));

    [Fact]
    public void A_key_file_that_cannot_be_used_stops_the_app_at_start()
    {
        var factory = app.Factory.WithWebHostBuilder(b => b.UseSetting("Fcm:ServiceAccount", KeyFile(new { project_id = "p" })));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    // ---- Google's login and the message (no network) ----

    [Fact]
    public void The_login_request_is_signed_by_the_service_account_for_sending_only_and_lasts_an_hour()
    {
        var now = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
        var jwt = GoogleJwt.Assertion("sender@gom-test.iam.gserviceaccount.com", CallFixture.FcmKey, now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(CallFixture.FcmKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var header = JsonDocument.Parse(Decode(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        using var claims = JsonDocument.Parse(Decode(parts[1]));
        var c = claims.RootElement;
        Assert.Equal("sender@gom-test.iam.gserviceaccount.com", c.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", c.GetProperty("scope").GetString());
        Assert.Equal("https://oauth2.googleapis.com/token", c.GetProperty("aud").GetString());
        Assert.Equal(now.ToUnixTimeSeconds(), c.GetProperty("iat").GetInt64());
        Assert.Equal(3600, c.GetProperty("exp").GetInt64() - c.GetProperty("iat").GetInt64());
    }

    private static byte[] Decode(string part) => System.Buffers.Text.Base64Url.DecodeFromChars(part);

    /// <summary>Google, as far as the token address and FCM go: keeps each request and answers the next status it is given.</summary>
    private sealed class FakeGoogle : HttpMessageHandler, IHttpClientFactory
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];
        public Queue<HttpStatusCode> SendAnswers { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent(new { access_token = $"token-{Seen.Count}", expires_in = 3599, token_type = "Bearer" }) };
            return new HttpResponseMessage(SendAnswers.Count > 0 ? SendAnswers.Dequeue() : HttpStatusCode.OK) { Content = JsonContent(new { name = "projects/gom-test/messages/1" }) };
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    }

    [Fact]
    public async Task A_message_goes_to_the_projects_address_high_priority_data_only_with_its_ttl_and_a_login_kept_for_the_hour()
    {
        var google = new FakeGoogle();
        var clock = new FakeClock();
        var fcm = new GoogleFcmTransport(google, FcmAccount.Parse(CallFixture.FcmAccount), clock);
        var data = new Dictionary<string, string> { ["t"] = "call", ["id"] = "abc" };

        Assert.Equal(200, (await fcm.SendAsync("device-1", data, 45, high: true, default)).Status);
        var (login, form) = google.Seen[0];
        Assert.Equal("https://oauth2.googleapis.com/token", login.RequestUri!.ToString());
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", form);
        Assert.Contains("assertion=", form);
        var (send, body) = google.Seen[1];
        Assert.Equal("https://fcm.googleapis.com/v1/projects/gom-test/messages:send", send.RequestUri!.ToString());
        Assert.Equal("Bearer token-1", send.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        var message = json.RootElement.GetProperty("message");
        Assert.Equal("device-1", message.GetProperty("token").GetString());
        Assert.Equal("abc", message.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("HIGH", message.GetProperty("android").GetProperty("priority").GetString());
        Assert.Equal("45s", message.GetProperty("android").GetProperty("ttl").GetString());
        Assert.False(message.TryGetProperty("notification", out _));   // the app words it: Google never sees a name

        // The login is kept: a second message within the hour asks for none.
        clock.Advance(TimeSpan.FromMinutes(50));
        await fcm.SendAsync("device-1", data, 45, high: true, default);
        Assert.Equal(3, google.Seen.Count);
        // Five minutes before it runs out, a new one.
        clock.Advance(TimeSpan.FromMinutes(5));
        await fcm.SendAsync("device-1", data, 45, high: true, default);
        Assert.Equal("https://oauth2.googleapis.com/token", google.Seen[3].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_message_that_shows_nothing_new_goes_at_normal_priority()
    {
        var google = new FakeGoogle();
        var fcm = new GoogleFcmTransport(google, FcmAccount.Parse(CallFixture.FcmAccount), new FakeClock());
        await fcm.SendAsync("d", new Dictionary<string, string> { ["t"] = "recall" }, 60, high: false, default);
        using var json = JsonDocument.Parse(google.Seen[1].Body);
        Assert.Equal("NORMAL", json.RootElement.GetProperty("message").GetProperty("android").GetProperty("priority").GetString());
    }

    [Fact]
    public async Task A_login_fcm_refuses_is_replaced_on_the_next_message()
    {
        var google = new FakeGoogle();
        var fcm = new GoogleFcmTransport(google, FcmAccount.Parse(CallFixture.FcmAccount), new FakeClock());
        var data = new Dictionary<string, string> { ["t"] = "call" };
        google.SendAnswers.Enqueue(HttpStatusCode.Unauthorized);
        Assert.Equal(401, (await fcm.SendAsync("d", data, 45, high: true, default)).Status);
        await fcm.SendAsync("d", data, 45, high: true, default);
        Assert.Equal(["oauth2.googleapis.com", "fcm.googleapis.com", "oauth2.googleapis.com", "fcm.googleapis.com"], google.Seen.Select(s => s.Request.RequestUri!.Host));
    }
}
