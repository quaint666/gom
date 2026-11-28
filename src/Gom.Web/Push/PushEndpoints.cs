using System.Buffers.Text;
using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gom.Web.Push;

/// <summary>Turning notifications on and off for the device you are on (its sign-in session). All 404 while push is off.</summary>
public static class PushEndpoints
{
    public sealed record KeysDto(string? P256dh, string? Auth);
    public sealed record SubscribeRequest(string? Endpoint, KeysDto? Keys, string? Level);

    public static void MapPush(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/push").RequireAuthorization();
        api.MapGet("", Get);
        api.MapPut("", Subscribe);
        api.MapDelete("", Unsubscribe);
        api.MapPut("/fcm", RegisterApp);
        api.MapDelete("/fcm", UnregisterApp);
        api.MapGet("/notice", Notice);
    }

    /// <summary>
    /// The server's public key (a browser needs it to subscribe), and whether this device is on, at which level (for a device that is
    /// not: the default, words). In the Android app the device is its FCM registration, so the same switch in Settings works there.
    /// </summary>
    private static async Task<IResult> Get(HttpContext http, GomDbContext db, IOptions<PushOptions> options, FcmAccount fcm, CancellationToken ct)
    {
        if (!options.Value.Enabled && !fcm.Enabled) return Results.NotFound();
        var session = GomClaims.Session(http.User);
        var mine = await db.PushSubscriptions.AsNoTracking().Where(p => p.SessionId == session).Select(p => (PushLevel?)p.Level).SingleOrDefaultAsync(ct)
            ?? await db.FcmDevices.AsNoTracking().Where(d => d.SessionId == session).Select(d => (PushLevel?)d.Level).SingleOrDefaultAsync(ct);
        return Results.Ok(new { key = options.Value.PublicKey, subscribed = mine is not null, level = Level(mine ?? PushLevel.Words) });
    }

    /// <summary>
    /// Registers this device, replacing what it had. The same browser address under another session (signed in again) moves here, so one
    /// browser never gets a notification twice.
    /// </summary>
    private static async Task<IResult> Subscribe(SubscribeRequest request, HttpContext http, GomDbContext db, IOptions<PushOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled) return Results.NotFound();
        if (request.Endpoint is not { Length: > 0 and <= 1024 } endpoint || !o.Allows(endpoint)) return Error("invalid_endpoint");
        if (Decode(request.Keys?.P256dh) is not { } p256dh || !WebPushCrypto.IsPublicKey(p256dh)
            || Decode(request.Keys?.Auth) is not { Length: 16 } auth) return Error("invalid_keys");
        if (ParseLevel(request.Level) is not { } level) return Error("invalid_level");

        var session = GomClaims.Session(http.User);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.PushSubscriptions.Where(p => p.SessionId == session || p.Endpoint == endpoint).ExecuteDeleteAsync(ct);
        db.PushSubscriptions.Add(new PushSubscription { Id = Guid.NewGuid(), SessionId = session, Endpoint = endpoint, P256dh = p256dh, Auth = auth, Level = level, CreatedAt = clock.GetUtcNow() });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two registrations of the same device at the same moment: the other one is kept.
            return Results.Json(new { error = "busy" }, statusCode: StatusCodes.Status409Conflict);
        }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    public sealed record AppRequest(string? Token, string? Level);

    /// <summary>
    /// Registers this device's Android app (its FCM token), replacing what it had; the same token under another session moves here. The
    /// app takes the place of the browser's notifications for this session, so nothing arrives twice (docs/voice-calls.md, decision 44).
    /// </summary>
    private static async Task<IResult> RegisterApp(AppRequest request, HttpContext http, GomDbContext db, FcmAccount fcm, TimeProvider clock, CancellationToken ct)
    {
        if (!fcm.Enabled) return Results.NotFound();
        // FCM's tokens are letters, digits and a few marks; anything else (spaces, controls) is not one.
        if (request.Token is not { Length: > 0 and <= 1024 } token || token.Any(c => c is <= ' ' or > '~')) return Error("invalid_token");
        if (ParseLevel(request.Level) is not { } level) return Error("invalid_level");

        var session = GomClaims.Session(http.User);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.FcmDevices.Where(d => d.SessionId == session || d.Token == token).ExecuteDeleteAsync(ct);
        await db.PushSubscriptions.Where(p => p.SessionId == session).ExecuteDeleteAsync(ct);
        db.FcmDevices.Add(new FcmDevice { Id = Guid.NewGuid(), SessionId = session, Token = token, Level = level, CreatedAt = clock.GetUtcNow() });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Json(new { error = "busy" }, statusCode: StatusCodes.Status409Conflict);
        }
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnregisterApp(HttpContext http, GomDbContext db, FcmAccount fcm, CancellationToken ct)
    {
        if (!fcm.Enabled) return Results.NotFound();
        var session = GomClaims.Session(http.User);
        await db.FcmDevices.Where(d => d.SessionId == session).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    /// <summary>What the app's notification says, worded as a browser's would be, at this device's level and in its person's language.</summary>
    public sealed record NoticeDto(string Title, string Body, string Url, string Tag);

    /// <summary>
    /// The words of a notification for the Android app (docs/voice-calls.md, decision 43): FCM brings it only ids, and it asks here, with
    /// its session, so Google never sees a name or words. <c>c</c> and <c>m</c>: a message (one taken back says so); <c>owner</c>
    /// "join" or "contact": the owner's notices; <c>call</c>: a call ringing for this person, or one they missed (decision 8: "nothing"
    /// names nobody, so the lock screen shows no name). Anything this person cannot see is a 404, and the app then shows its own plain line.
    /// </summary>
    private static async Task<IResult> Notice(Guid? c, Guid? m, string? owner, Guid? call, HttpContext http, GomDbContext db, FcmAccount fcm, CancellationToken ct)
    {
        if (!fcm.Enabled) return Results.NotFound();
        var me = GomClaims.User(http.User);
        var session = GomClaims.Session(http.User);
        var level = await db.FcmDevices.AsNoTracking().Where(d => d.SessionId == session).Select(d => (PushLevel?)d.Level).SingleOrDefaultAsync(ct)
            ?? PushLevel.Nothing;   // not registered: nothing beyond "a new message"
        var person = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => new { u.Language, u.IsOwner }).SingleAsync(ct);
        var say = ServerText.For(person.Language);

        if (owner is not null)
        {
            if (!person.IsOwner || owner is not ("join" or "contact")) return Results.NotFound();
            if (owner == "contact") return Results.Ok(new NoticeDto("Gom", say.ContactMessage, "/settings/contact", "contact"));
            var newest = level == PushLevel.Nothing ? null : await db.Users.AsNoTracking().Where(u => u.RequestedAt != null && u.RefusedAt == null)
                .OrderByDescending(u => u.RequestedAt).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
            return Results.Ok(new NoticeDto("Gom", newest is null ? say.JoinRequestUnnamed : say.JoinRequest(newest), "/settings/requests", "join-requests"));
        }

        if (call is { } callId)
        {
            var k = await db.Calls.AsNoTracking().Where(k => k.Id == callId && k.CalleeId == me)
                .Join(db.Users, k => k.CallerId, u => u.Id, (k, u) => new { k.ConversationId, k.State, k.Outcome, u.DisplayName }).SingleOrDefaultAsync(ct);
            var words = k?.State == CallState.Ringing ? say.IncomingCall
                : k?.State == CallState.Ended && k.Outcome is CallOutcome.Missed or CallOutcome.Busy ? say.MissedCall : null;
            if (k is null || words is null) return Results.NotFound();
            return Results.Ok(new NoticeDto(level == PushLevel.Nothing ? "Gom" : k.DisplayName, words, $"/c/{k.ConversationId}", k.ConversationId.ToString()));
        }

        if (c is not { } chat || m is not { } id) return Error("invalid_notice");
        var access = await Access.Of(db, chat, me, ct);
        if (access is null) return Results.NotFound();
        var found = await (
            from msg in db.Messages.AsNoTracking()
            where msg.Id == id && msg.ConversationId == chat && msg.Seq > access.SeesAfter && msg.Notice == null
                && !db.Hides.Any(h => h.UserId == me && h.MessageId == msg.Id)
            join u in db.Users on msg.SenderId equals u.Id
            join conversation in db.Conversations on msg.ConversationId equals conversation.Id
            select new { Message = msg, u.DisplayName, conversation.Title }).SingleOrDefaultAsync(ct);
        if (found is null) return Results.NotFound();

        var (url, tag) = ($"/c/{chat}", chat.ToString());
        if (level == PushLevel.Nothing) return Results.Ok(new NoticeDto("Gom", say.NewMessage, url, tag));
        var files = found.Message.DeletedAt is null ? (await MessageAttachments.For(db, [id], ct)).GetValueOrDefault(id)?.Attachments : null;
        var job = PushJob.From(found.Message, found.DisplayName, files);
        var (title, body) = PushSender.Words(job, access.Kind, found.Title, say, words: level == PushLevel.Words);
        return Results.Ok(new NoticeDto(title, found.Message.DeletedAt is null ? body : say.Recalled, url, tag));
    }

    private static PushLevel? ParseLevel(string? level) => level switch
    {
        null or "words" => PushLevel.Words,
        "name" => PushLevel.Name,
        "nothing" => PushLevel.Nothing,
        _ => null,
    };

    private static async Task<IResult> Unsubscribe(HttpContext http, GomDbContext db, IOptions<PushOptions> options, CancellationToken ct)
    {
        if (!options.Value.Enabled) return Results.NotFound();
        var session = GomClaims.Session(http.User);
        await db.PushSubscriptions.Where(p => p.SessionId == session).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    private static string Level(PushLevel l) => l switch { PushLevel.Nothing => "nothing", PushLevel.Words => "words", _ => "name" };

    private static byte[]? Decode(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 200) return null;
        try { return Base64Url.DecodeFromChars(s.TrimEnd('=')); } catch (FormatException) { return null; }
    }

    private static IResult Error(string code) => Results.Json(new { error = code }, statusCode: StatusCodes.Status400BadRequest);
}
