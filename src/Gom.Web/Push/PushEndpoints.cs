using System.Buffers.Text;
using Gom.Web.Auth;
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
    }

    /// <summary>The server's public key (a browser needs it to subscribe), and whether this device is on, at which level (for a device that is not: the default, words).</summary>
    private static async Task<IResult> Get(HttpContext http, GomDbContext db, IOptions<PushOptions> options, CancellationToken ct)
    {
        if (!options.Value.Enabled) return Results.NotFound();
        var session = GomClaims.Session(http.User);
        var mine = await db.PushSubscriptions.AsNoTracking().Where(p => p.SessionId == session).Select(p => (PushLevel?)p.Level).SingleOrDefaultAsync(ct);
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
