using System.Net;
using System.Text;
using System.Text.Json;
using Gom.Web.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gom.Tests;

/// <summary>What Gom sends Cloudflare's credentials API and reads back, against the shape its docs give (no network).</summary>
public class TurnTests
{
    /// <summary>Cloudflare, as far as one request goes: keeps it and answers what the test says.</summary>
    private sealed class FakeCloudflare(HttpStatusCode status, string body = "") : HttpMessageHandler, IHttpClientFactory
    {
        public HttpRequestMessage? Seen { get; private set; }
        public string? SeenBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            SeenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    }

    private static readonly IOptions<TurnOptions> Key = Options.Create(new TurnOptions { KeyId = "abc123", ApiToken = "token-xyz" });

    // The example answer on developers.cloudflare.com/realtime/turn/generate-credentials/ (values shortened).
    private const string Answer = """
        {"iceServers":[{"urls":["stun:stun.cloudflare.com:3478"]},
         {"urls":["turn:turn.cloudflare.com:3478?transport=udp","turn:turn.cloudflare.com:443?transport=udp","turn:turn.cloudflare.com:3478?transport=tcp",
                  "turn:turn.cloudflare.com:80?transport=tcp","turns:turn.cloudflare.com:5349?transport=tcp","turns:turn.cloudflare.com:443?transport=tcp"],
          "username":"bc91b63e2b5d759f8eb9f3b58062439e0a0e15893d76317d833265ad08d6631099ce7c7087caabb31ad3e1c386424e3e",
          "credential":"ebd71f1d3edbc2b0edae3cd5a6d82284aeb5c3b8fdaa9b8e3bf9cec683e0d45fe9f5b44e5145db3300f06c250a15b4a0"}]}
        """;

    [Fact]
    public async Task A_login_is_asked_for_with_the_key_its_length_and_its_tag_and_read_from_cloudflares_answer()
    {
        var cloudflare = new FakeCloudflare(HttpStatusCode.Created, Answer);
        var servers = await new CloudflareTurnClient(cloudflare, Key).IssueAsync(10800, "tag1", default);

        Assert.Equal(HttpMethod.Post, cloudflare.Seen!.Method);
        Assert.Equal("https://rtc.live.cloudflare.com/v1/turn/keys/abc123/credentials/generate-ice-servers", cloudflare.Seen.RequestUri!.ToString());
        Assert.Equal("Bearer token-xyz", cloudflare.Seen.Headers.Authorization!.ToString());
        using var body = JsonDocument.Parse(cloudflare.SeenBody!);
        Assert.Equal(10800, body.RootElement.GetProperty("ttl").GetInt32());
        Assert.Equal("tag1", body.RootElement.GetProperty("customIdentifier").GetString());

        Assert.Equal(2, servers.Count);
        Assert.Null(servers[0].Username);
        Assert.Equal(6, servers[1].Urls.Length);
        Assert.StartsWith("bc91b63e", servers[1].Username);
        Assert.StartsWith("ebd71f1d", servers[1].Credential);
    }

    [Fact]
    public async Task A_login_is_taken_back_by_its_name_in_the_address()
    {
        var cloudflare = new FakeCloudflare(HttpStatusCode.NoContent);
        await new CloudflareTurnClient(cloudflare, Key).RevokeAsync("bc91b63e", default);
        Assert.Equal(HttpMethod.Post, cloudflare.Seen!.Method);
        Assert.Equal("https://rtc.live.cloudflare.com/v1/turn/keys/abc123/credentials/bc91b63e/revoke", cloudflare.Seen.RequestUri!.ToString());
        Assert.Equal("Bearer token-xyz", cloudflare.Seen.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task A_refusal_from_cloudflare_is_a_direct_connection_only_not_an_error()
    {
        var cloudflare = new FakeCloudflare(HttpStatusCode.Unauthorized, """{"error":"bad token"}""");
        var logins = new TurnLogins(new CloudflareTurnClient(cloudflare, Key), Key, NullLogger<TurnLogins>.Instance);
        var login = await logins.IssueAsync(Guid.NewGuid(), default);
        Assert.Null(login.Username);
        Assert.Equal("stun:stun.cloudflare.com:3478", Assert.Single(Assert.Single(login.IceServers).Urls));
    }

    [Fact]
    public void A_persons_tag_is_the_same_each_time_differs_between_people_and_changes_with_the_key()
    {
        var a = Guid.NewGuid();
        Assert.Equal(TurnLogins.Tag(a, "k1"), TurnLogins.Tag(a, "k1"));
        Assert.NotEqual(TurnLogins.Tag(a, "k1"), TurnLogins.Tag(Guid.NewGuid(), "k1"));
        Assert.NotEqual(TurnLogins.Tag(a, "k1"), TurnLogins.Tag(a, "k2"));
    }

    [Theory]
    [InlineData("k", "", 3)]
    [InlineData("", "t", 3)]
    [InlineData("k", "t", 0)]
    [InlineData("k", "t", 49)]
    public void A_half_set_key_or_a_login_length_cloudflare_refuses_is_a_mistake(string keyId, string token, int hours) =>
        Assert.Throws<InvalidOperationException>(() => new TurnOptions { KeyId = keyId, ApiToken = token, LoginHours = hours }.Check());
}
