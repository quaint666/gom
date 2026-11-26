using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Gom.Web.Chat;

/// <summary>
/// The relay a call falls back on when the two phones cannot reach each other directly (configuration section "Turn";
/// docs/voice-calls.md, decisions 21 to 24). Cloudflare Realtime TURN: Gom's server only asks it for short logins over HTTPS.
/// </summary>
public sealed class TurnOptions
{
    /// <summary>The TURN key's ID (Cloudflare's "uid"). With <see cref="ApiToken"/>, both or neither.</summary>
    public string KeyId { get; set; } = "";
    /// <summary>The TURN key's secret, sent as the bearer token. Never in the repository.</summary>
    public string ApiToken { get; set; } = "";
    /// <summary>How long a login lasts; a longer call asks for a fresh one. Cloudflare allows at most 48 hours.</summary>
    public int LoginHours { get; set; } = 3;
    /// <summary>The address phones use to learn their own, when there is no relay login (no key set, or Cloudflare did not answer).</summary>
    public string Stun { get; set; } = "stun:stun.cloudflare.com:3478";

    public bool Enabled => KeyId.Length > 0 && ApiToken.Length > 0;

    /// <summary>Refuses a half-set key (one of the two missing is a mistake, not a choice) and a login length Cloudflare would refuse.</summary>
    public void Check()
    {
        if ((KeyId.Length > 0) != (ApiToken.Length > 0)) throw new InvalidOperationException("Turn__KeyId and Turn__ApiToken are set together or not at all.");
        if (LoginHours is < 1 or > 48) throw new InvalidOperationException("Turn__LoginHours must be 1 to 48.");
    }
}

/// <summary>One entry of WebRTC's list of servers: a relay has a login, an address-only server has none.</summary>
public sealed record IceServer(
    string[] Urls,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Username = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Credential = null);

/// <summary>Asks the relay for a login and takes one back. Tests put a fake in its place.</summary>
public interface ITurnClient
{
    Task<IReadOnlyList<IceServer>> IssueAsync(int seconds, string tag, CancellationToken ct);
    Task RevokeAsync(string username, CancellationToken ct);
}

/// <summary>Cloudflare's credentials API: no redirects followed, a short timeout (set where the client is registered).</summary>
public sealed class CloudflareTurnClient(IHttpClientFactory clients, IOptions<TurnOptions> options) : ITurnClient
{
    public const string Client = "turn";
    private const string Keys = "https://rtc.live.cloudflare.com/v1/turn/keys/";

    private sealed record Generated(IceServer[]? IceServers);

    private HttpRequestMessage Request(string path, object? body)
    {
        var o = options.Value;
        var request = new HttpRequestMessage(HttpMethod.Post, Keys + Uri.EscapeDataString(o.KeyId) + "/credentials/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    public async Task<IReadOnlyList<IceServer>> IssueAsync(int seconds, string tag, CancellationToken ct)
    {
        using var request = Request("generate-ice-servers", new { ttl = seconds, customIdentifier = tag });
        using var response = await clients.CreateClient(Client).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var generated = await response.Content.ReadFromJsonAsync<Generated>(JsonSerializerOptions.Web, ct);
        return generated?.IceServers ?? [];
    }

    public async Task RevokeAsync(string username, CancellationToken ct)
    {
        using var request = Request(Uri.EscapeDataString(username) + "/revoke", null);
        using var response = await clients.CreateClient(Client).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Hands a device the servers for one call: a relay login when the key is set and Cloudflare answers, otherwise only the address
/// server, and says which (a direct connection may still work; the app says so if it does not). Each login is tagged with an opaque
/// ID for Cloudflare's usage figures, a keyed hash of the person's ID, never a name or Gom ID.
/// </summary>
public sealed class TurnLogins(ITurnClient client, IOptions<TurnOptions> options, ILogger<TurnLogins> log)
{
    /// <param name="Username">The relay login's name, to take it back later; null when there is no relay login.</param>
    public sealed record Login(IReadOnlyList<IceServer> IceServers, string? Username, int Seconds);

    public async Task<Login> IssueAsync(Guid person, CancellationToken ct)
    {
        var o = options.Value;
        var seconds = o.LoginHours * 3600;
        var addressOnly = new Login([new IceServer([o.Stun])], null, seconds);
        if (!o.Enabled) return addressOnly;
        try
        {
            // Browsers block port 53, and without trickle ICE a phone would wait for it to time out (Cloudflare's advice: leave it out).
            var servers = (await client.IssueAsync(seconds, Tag(person, o.ApiToken), ct))
                .Select(s => s with { Urls = [.. s.Urls.Where(u => !PortFiftyThree(u))] }).Where(s => s.Urls.Length > 0).ToList();
            if (servers.FirstOrDefault(s => !string.IsNullOrEmpty(s.Username) && !string.IsNullOrEmpty(s.Credential)) is not { } relay)
            {
                log.LogWarning("The relay answered with no login; this call gets a direct connection only");
                return addressOnly;
            }
            return new Login(servers, relay.Username, seconds);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "The relay did not give a login; this call gets a direct connection only");
            return addressOnly;
        }
    }

    /// <summary>The same for a person every time (so their usage adds up), and nothing about them can be read from it.</summary>
    public static string Tag(Guid person, string key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), person.ToByteArray()))[..32];

    private static bool PortFiftyThree(string url) => url.Split('?')[0].EndsWith(":53", StringComparison.Ordinal);
}

/// <summary>Who may ask for relay logins how often: a bucket per person, with the same numbers as starting calls.</summary>
public sealed class TurnLimiter(SendLimiter bucket)
{
    public TimeSpan? TryTake(Guid person) => bucket.TryTake(person);
}

/// <summary>
/// Takes relay logins back off the request path, so a slow Cloudflare never holds up hanging up. A login that fails to go is left to
/// run out on its own (<see cref="TurnOptions.LoginHours"/>); nothing is retried.
/// </summary>
public sealed class TurnRevoker(ITurnClient client, ILogger<TurnRevoker> log) : BackgroundService
{
    private readonly Channel<string> _names = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,   // with Wait, TryWrite says no when full, so a drop can be logged
    });

    public void Revoke(string username)
    {
        if (!_names.Writer.TryWrite(username)) log.LogWarning("revoke queue full; a relay login is left to run out");
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var name in _names.Reader.ReadAllAsync(stop))
            {
                try { await client.RevokeAsync(name, stop); }
                catch (Exception e) when (!stop.IsCancellationRequested) { log.LogWarning(e, "Taking back a relay login failed; it will run out on its own"); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
