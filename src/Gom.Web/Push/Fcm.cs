using System.Buffers.Text;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gom.Web.Push;

/// <summary>
/// The Firebase service account that sends to the Android app (setting <c>Fcm:ServiceAccount</c>: its JSON key file, base64-encoded).
/// Unset: no app is woken, and nobody can take a call. Set but unreadable: the app refuses to start, rather than fail at the first call.
/// </summary>
public sealed class FcmAccount
{
    public static readonly FcmAccount None = new("", "", null);

    public string ProjectId { get; }
    public string ClientEmail { get; }
    public RSA? Key { get; }
    public bool Enabled => Key is not null;

    private FcmAccount(string projectId, string clientEmail, RSA? key) => (ProjectId, ClientEmail, Key) = (projectId, clientEmail, key);

    private sealed record KeyFile(string? Project_id, string? Client_email, string? Private_key);

    public static FcmAccount Parse(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return None;
        const string Bad = "Fcm__ServiceAccount must be a Firebase service account's JSON key file, base64-encoded, with project_id, client_email and private_key.";
        KeyFile? file;
        try { file = JsonSerializer.Deserialize<KeyFile>(Convert.FromBase64String(base64.Trim()), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception e) when (e is FormatException or JsonException) { throw new InvalidOperationException(Bad, e); }
        if (file is not { Project_id: { Length: > 0 } project, Client_email: { Length: > 0 } email, Private_key: { Length: > 0 } pem }) throw new InvalidOperationException(Bad);
        var key = RSA.Create();
        try { key.ImportFromPem(pem); }
        catch (Exception e) when (e is ArgumentException or CryptographicException) { key.Dispose(); throw new InvalidOperationException(Bad, e); }
        return new FcmAccount(project, email, key);
    }
}

/// <summary>A signed request for an access token, as Google's service-account OAuth asks (RS256, at most an hour).</summary>
public static class GoogleJwt
{
    public const string TokenUrl = "https://oauth2.googleapis.com/token";
    public const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    public static string Assertion(string clientEmail, RSA key, DateTimeOffset now)
    {
        static string Part(object o) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(o));
        var signed = Part(new { alg = "RS256", typ = "JWT" }) + "." +
            Part(new { iss = clientEmail, scope = Scope, aud = TokenUrl, iat = now.ToUnixTimeSeconds(), exp = now.AddHours(1).ToUnixTimeSeconds() });
        var signature = key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signed + "." + Base64Url.EncodeToString(signature);
    }
}

/// <summary>What FCM said: its HTTP status, and how long it asked to wait when it was busy.</summary>
public sealed record FcmResult(int Status, TimeSpan? RetryAfter = null)
{
    /// <summary>The device is gone for good (uninstalled, or its token replaced): forget it.</summary>
    public bool Gone => Status == 404;
    public bool Busy => Status is 429 or 503;
}

/// <summary>
/// Sends one data message to one app. <paramref name="high"/>: it may wake a sleeping phone, and must lead to a notification the person
/// sees, or FCM lowers the app's priority. Tests put a fake in its place.
/// </summary>
public interface IFcmTransport
{
    Task<FcmResult> SendAsync(string token, IReadOnlyDictionary<string, string> data, int ttlSeconds, bool high, CancellationToken ct);
}

/// <summary>
/// FCM's HTTP v1 API: data only (the app words its own notification, so Google never sees a name or a message), and an
/// access token from the service account, kept until five minutes before it runs out.
/// </summary>
public sealed class GoogleFcmTransport(IHttpClientFactory clients, FcmAccount account, TimeProvider clock) : IFcmTransport
{
    public const string Client = "fcm";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _until;

    private sealed record Granted(string? Access_token, int Expires_in);

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (_token is not null && now < _until) return _token;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = GoogleJwt.Assertion(account.ClientEmail, account.Key!, now),
            });
            using var response = await clients.CreateClient(Client).PostAsync(GoogleJwt.TokenUrl, form, ct);
            response.EnsureSuccessStatusCode();
            var granted = await response.Content.ReadFromJsonAsync<Granted>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            if (granted?.Access_token is not { Length: > 0 } token) throw new InvalidOperationException("Google gave no access token.");
            (_token, _until) = (token, now.AddSeconds(Math.Max(0, granted.Expires_in - 300)));
            return token;
        }
        finally { _gate.Release(); }
    }

    public async Task<FcmResult> SendAsync(string token, IReadOnlyDictionary<string, string> data, int ttlSeconds, bool high, CancellationToken ct)
    {
        var message = new
        {
            message = new
            {
                token,
                data,
                android = new { priority = high ? "HIGH" : "NORMAL", ttl = ttlSeconds.ToString(CultureInfo.InvariantCulture) + "s" },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(account.ProjectId)}/messages:send")
        {
            Content = JsonContent.Create(message),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
        using var response = await clients.CreateClient(Client).SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) _token = null;   // refused: the next send asks for a new one
        return new FcmResult((int)response.StatusCode, response.Headers.RetryAfter?.Delta);
    }
}
