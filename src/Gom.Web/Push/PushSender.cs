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
    IFcmTransport fcm, FcmAccount account, TimeProvider clock, ILogger<PushSender> log) : BackgroundService
{
    private readonly PushOptions _o = options.Value;

    /// <summary>FCM keeps a message for a phone that is off at most four weeks.</summary>
    private const int FcmMaxTtl = 2_419_200;

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
        if (job.JoinRequest || job.Contact) { await TellOwnerAsync(db, job, now, ct); return; }
        if (job.MissedCall) { await TellMissedAsync(db, job, now, ct); return; }
        var chat = await db.Conversations.AsNoTracking().Where(c => c.Id == job.ConversationId).Select(c => new { c.Kind, c.Title }).SingleOrDefaultAsync(ct);
        if (chat is null) return;

        var told =
            from m in db.Members
            where m.ConversationId == job.ConversationId && m.UserId != job.SenderId && m.JoinedSeq < job.Seq
                && !m.Muted && (m.MutedUntil == null || m.MutedUntil <= now)
            join u in db.Users on m.UserId equals u.Id
            where u.DisabledAt == null
            join s in db.Sessions on m.UserId equals s.UserId
            where s.RevokedAt == null && s.ExpiresAt > now
            select new { Session = s.Id, u.Language };
        // Only a device that showed the words has anything to take back.
        await TellAppsAsync(db, told.Select(t => t.Session), onlyWords: job.Recalled,
            new() { ["t"] = job.Recalled ? "recall" : "message", ["c"] = job.ConversationId.ToString(), ["m"] = job.MessageId.ToString() }, high: !job.Recalled, ct);
        if (!_o.Enabled) return;
        var devices = await (
            from t in told
            join p in db.PushSubscriptions on t.Session equals p.SessionId
            where !job.Recalled || p.Level == PushLevel.Words
            select new { p.Id, p.Endpoint, p.P256dh, p.Auth, p.Level, t.Language }).AsNoTracking().ToListAsync(ct);
        if (devices.Count == 0) return;

        var topic = Base64Url.EncodeToString(job.ConversationId.ToByteArray());   // 22 characters: RFC 8030 allows up to 32
        foreach (var d in devices)
        {
            // In the language of the person the device belongs to.
            var say = ServerText.For(d.Language);
            var (title, body) = Words(job, chat.Kind, chat.Title, say, words: d.Level == PushLevel.Words);
            object payload = job.Recalled ? new { c = job.ConversationId, m = job.MessageId, title, body = say.Recalled, recalled = true }
                : d.Level == PushLevel.Nothing ? new { c = job.ConversationId, m = job.MessageId, title = "Gom", body = say.NewMessage, recalled = false }
                : new { c = job.ConversationId, m = job.MessageId, title, body, recalled = false };
            await SendAsync(db, d.Id, d.Endpoint, d.P256dh, d.Auth, payload, job.Recalled ? "normal" : "high", topic, now, ct);
        }
    }

    /// <summary>
    /// A missed call: the person called hears of it on every browser, even in a muted chat (mute is for messages; docs/voice-calls.md,
    /// decision 7), at their level ("nothing" names nobody). A tap opens the chat, as a message's does. Their apps heard already: the
    /// call's own "stopped ringing" push says it was missed (<see cref="CallPusher"/>).
    /// </summary>
    private async Task TellMissedAsync(GomDbContext db, PushJob job, DateTimeOffset now, CancellationToken ct)
    {
        if (!_o.Enabled) return;
        var devices = await (
            from m in db.Members
            where m.ConversationId == job.ConversationId && m.UserId != job.SenderId
            join u in db.Users on m.UserId equals u.Id
            where u.DisabledAt == null
            join s in db.Sessions on m.UserId equals s.UserId
            where s.RevokedAt == null && s.ExpiresAt > now
            join p in db.PushSubscriptions on s.Id equals p.SessionId
            select new { p.Id, p.Endpoint, p.P256dh, p.Auth, p.Level, u.Language }).AsNoTracking().ToListAsync(ct);
        var topic = Base64Url.EncodeToString(job.ConversationId.ToByteArray());
        foreach (var d in devices)
        {
            var say = ServerText.For(d.Language);
            var title = d.Level == PushLevel.Nothing ? "Gom" : job.SenderName;
            await SendAsync(db, d.Id, d.Endpoint, d.P256dh, d.Auth, new { c = job.ConversationId, m = job.MessageId, title, body = say.MissedCall, recalled = false }, "high", topic, now, ct);
        }
    }

    /// <summary>
    /// Someone asked to join, or wrote from the privacy page: the owner's devices are told, at their level ("nothing" names nobody; a
    /// contact message never carries its words); a tap opens the owner's page.
    /// </summary>
    private async Task TellOwnerAsync(GomDbContext db, PushJob job, DateTimeOffset now, CancellationToken ct)
    {
        var owners =
            from u in db.Users
            where u.IsOwner && u.DisabledAt == null
            join s in db.Sessions on u.Id equals s.UserId
            where s.RevokedAt == null && s.ExpiresAt > now
            select new { Session = s.Id, u.Language };
        await TellAppsAsync(db, owners.Select(o => o.Session), onlyWords: false, new() { ["t"] = "owner", ["kind"] = job.Contact ? "contact" : "join" }, high: false, ct);
        if (!_o.Enabled) return;
        var devices = await (
            from o in owners
            join p in db.PushSubscriptions on o.Session equals p.SessionId
            select new { p.Id, p.Endpoint, p.P256dh, p.Auth, p.Level, o.Language }).AsNoTracking().ToListAsync(ct);
        foreach (var d in devices)
        {
            var say = ServerText.For(d.Language);
            var body = job.Contact ? say.ContactMessage : d.Level == PushLevel.Nothing ? say.JoinRequestUnnamed : say.JoinRequest(job.SenderName);
            await SendAsync(db, d.Id, d.Endpoint, d.P256dh, d.Auth, new { c = "", title = "Gom", body, open = "owner" }, "normal", job.Contact ? "contact" : "join-requests", now, ct);
        }
    }

    /// <summary>
    /// The Android apps on these sessions (FCM; docs/voice-calls.md, decision 43): data only, which conversation and message, never a name
    /// or words; the app fetches the message with its session and words the notification at its own level. A device FCM says is gone is
    /// forgotten; nothing is retried.
    /// </summary>
    private async Task TellAppsAsync(GomDbContext db, IQueryable<Guid> sessions, bool onlyWords, Dictionary<string, string> data, bool high, CancellationToken ct)
    {
        if (!account.Enabled) return;
        var apps = await db.FcmDevices.Where(d => sessions.Contains(d.SessionId) && (!onlyWords || d.Level == PushLevel.Words))
            .Select(d => new { d.Id, d.Token }).AsNoTracking().ToListAsync(ct);
        foreach (var app in apps)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));
                var result = await fcm.SendAsync(app.Token, data, Math.Min(_o.TtlSeconds, FcmMaxTtl), high, timeout.Token);
                if (result.Gone) await db.FcmDevices.Where(d => d.Id == app.Id).ExecuteDeleteAsync(ct);
                else if (result.Status is < 200 or >= 300) log.LogWarning("FCM answered {Status}", result.Status);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("FCM push failed: {Error}", e.GetType().Name);
            }
        }
    }

    /// <summary>Encrypts and hands one notification to one device's push service; forgets a device its service says is gone.</summary>
    private async Task SendAsync(GomDbContext db, Guid id, string endpoint, byte[] p256dh, byte[] auth, object payload, string urgency, string topic,
        DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var encrypted = WebPushCrypto.Encrypt(p256dh, auth, JsonSerializer.SerializeToUtf8Bytes(payload));
            var headers = new Dictionary<string, string>
            {
                ["TTL"] = _o.TtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Urgency"] = urgency,
                ["Topic"] = topic,
                ["Content-Encoding"] = "aes128gcm",
                ["Authorization"] = WebPushCrypto.VapidHeader(endpoint, _o.Subject, _o.PublicKey, _o.PrivateKey, now),
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));
            var status = await transport.SendAsync(endpoint, headers, encrypted, timeout.Token);
            if (status is 404 or 410) await db.PushSubscriptions.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
            else if (status is < 200 or >= 300) log.LogWarning("push service {Host} answered {Status}", new Uri(endpoint).Host, status);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("push to {Host} failed: {Error}", new Uri(endpoint).Host, e.GetType().Name);
        }
    }

    /// <summary>
    /// What a notification says: the conversation, who wrote (in a group), and what kind of message it is; with <paramref name="words"/>
    /// (a device that chose to see them), the message's own words when it has some.
    /// </summary>
    public static (string Title, string Body) Words(PushJob job, ConversationKind kind, string? groupTitle, ServerText say, bool words = false)
    {
        var what = words && job.Text.Length > 0 ? job.Text : job.Sticker ? say.SentSticker : (job.Images, job.Files, job.Videos) switch
        {
            ( > 0, > 0, 0) => say.SentPicturesAndFiles(job.Images, job.Files),
            ( > 0, 0, 0) => say.SentPictures(job.Images),
            (0, > 0, 0) => say.SentFiles(job.Files),
            (0, 0, > 0) => say.SentVideos(job.Videos),
            (var i, var f, > 0) => say.SentAttachments(i + f + job.Videos),
            _ => say.NewMessage,
        };
        return kind == ConversationKind.Group ? (groupTitle ?? say.Group, $"{job.SenderName}: {what}") : (job.SenderName, what);
    }
}
