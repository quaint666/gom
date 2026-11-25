using System.Buffers.Text;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Push;

/// <summary>Hands one encrypted push to a push service; answers its HTTP status. Tests put a fake in its place.</summary>
public interface IPushTransport
{
    Task<int> SendAsync(string endpoint, IReadOnlyDictionary<string, string> headers, byte[] body, CancellationToken ct);
}

/// <summary>The real one: a POST, no redirects followed (a push service has no reason to send one), a short timeout.</summary>
public sealed class HttpPushTransport(IHttpClientFactory clients) : IPushTransport
{
    public const string Client = "push";

    public async Task<int> SendAsync(string endpoint, IReadOnlyDictionary<string, string> headers, byte[] body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        foreach (var (name, value) in headers)
            if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content.Headers.TryAddWithoutValidation(name, value);
        using var response = await clients.CreateClient(Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return (int)response.StatusCode;
    }
}

/// <summary>
/// Sends the notifications for each message on the queue, one after the other, off the request path: to every registered device of
/// every member who is told (not the writer, not someone who muted the conversation or joined after the message, not an ended
/// session or a disabled account). A device whose push service says it is gone (404, 410) is forgotten; any other failure is logged
/// and that notification is lost. Nothing is retried.
/// </summary>
public sealed class PushSender(PushQueue queue, IServiceScopeFactory scopes, IPushTransport transport, IOptions<PushOptions> options,
    TimeProvider clock, ILogger<PushSender> log) : BackgroundService
{
    private readonly PushOptions _o = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!queue.Enabled) return;
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await DeliverAsync(job, stoppingToken); }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogWarning(e, "notifications for a message failed"); }
        }
    }

    /// <summary>Sends one message's notifications. Public so a test can wait for it instead of the background loop.</summary>
    public async Task DeliverAsync(PushJob job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GomDbContext>();
        var now = clock.GetUtcNow();
        var chat = await db.Conversations.AsNoTracking().Where(c => c.Id == job.ConversationId).Select(c => new { c.Kind, c.Title }).SingleOrDefaultAsync(ct);
        if (chat is null) return;

        var devices = await (
            from m in db.Members
            where m.ConversationId == job.ConversationId && m.UserId != job.SenderId && m.JoinedSeq < job.Seq
                && !m.Muted && (m.MutedUntil == null || m.MutedUntil <= now)
            join u in db.Users on m.UserId equals u.Id
            where u.DisabledAt == null
            join s in db.Sessions on m.UserId equals s.UserId
            where s.RevokedAt == null && s.ExpiresAt > now
            join p in db.PushSubscriptions on s.Id equals p.SessionId
            select new { p.Id, p.Endpoint, p.P256dh, p.Auth, p.Level }).AsNoTracking().ToListAsync(ct);
        if (devices.Count == 0) return;

        var (title, body) = Words(job, chat.Kind, chat.Title);
        var topic = Base64Url.EncodeToString(job.ConversationId.ToByteArray());   // 22 characters: RFC 8030 allows up to 32
        foreach (var d in devices)
        {
            var payload = d.Level == PushLevel.Nothing
                ? new { c = job.ConversationId, title = "Gom", body = ServerText.Vi.NewMessage }
                : new { c = job.ConversationId, title, body };
            try
            {
                var encrypted = WebPushCrypto.Encrypt(d.P256dh, d.Auth, JsonSerializer.SerializeToUtf8Bytes(payload));
                var headers = new Dictionary<string, string>
                {
                    ["TTL"] = _o.TtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Urgency"] = "high",
                    ["Topic"] = topic,
                    ["Content-Encoding"] = "aes128gcm",
                    ["Authorization"] = WebPushCrypto.VapidHeader(d.Endpoint, _o.Subject, _o.PublicKey, _o.PrivateKey, now),
                };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));
                var status = await transport.SendAsync(d.Endpoint, headers, encrypted, timeout.Token);
                if (status is 404 or 410) await db.PushSubscriptions.Where(p => p.Id == d.Id).ExecuteDeleteAsync(ct);
                else if (status is < 200 or >= 300) log.LogWarning("push service {Host} answered {Status}", new Uri(d.Endpoint).Host, status);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("push to {Host} failed: {Error}", new Uri(d.Endpoint).Host, e.GetType().Name);
            }
        }
    }

    /// <summary>
    /// What a notification says: the conversation, who wrote (in a group), and what kind of message it is, never its words.
    /// </summary>
    public static (string Title, string Body) Words(PushJob job, ConversationKind kind, string? groupTitle)
    {
        var what = job.Sticker ? ServerText.Vi.SentSticker : (job.Images, job.Files) switch
        {
            ( > 0, > 0) => ServerText.Vi.SentPicturesAndFiles(job.Images, job.Files),
            ( > 0, _) => ServerText.Vi.SentPictures(job.Images),
            (_, > 0) => ServerText.Vi.SentFiles(job.Files),
            _ => ServerText.Vi.NewMessage,
        };
        return kind == ConversationKind.Group ? (groupTitle ?? ServerText.Vi.Group, $"{job.SenderName}: {what}") : (job.SenderName, what);
    }
}
