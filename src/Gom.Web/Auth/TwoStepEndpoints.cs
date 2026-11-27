using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Gom.Web.Auth;

/// <summary>
/// Two-step sign-in, each person's own choice: after the password, a 6-digit code from an authenticator app (<see cref="Totp"/>).
/// Turning it on or off takes the password and a code, so a session left open somewhere cannot lock its owner out or take it away.
/// There are no backup codes: the owner's reset link turns it off for a member, and the server command <c>--two-step-off</c> does it
/// for the owner.
/// </summary>
public static class TwoStepEndpoints
{
    public sealed record TurnOnRequest(string? Key, string? Code, string? Password);
    public sealed record TurnOffRequest(string? Code, string? Password);

    public static void MapTwoStep(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me/two-step/new", NewKey).RequireAuthorization();
        app.MapPut("/api/me/two-step", TurnOn).RequireAuthorization();
        app.MapPost("/api/me/two-step/off", TurnOff).RequireAuthorization();
    }

    /// <summary>
    /// A fresh key to add to an authenticator app: typed in, opened (on a phone), or scanned (the QR code, a PNG). Nothing is kept: it
    /// comes back with the first code, in <see cref="TurnOn"/>.
    /// </summary>
    private static IResult NewKey(HttpContext http)
    {
        var key = Totp.NewKey();
        var uri = Totp.Uri(http.User.FindFirst(GomClaims.GomId)!.Value, key);
        using var data = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { key = Totp.Base32(key), uri, qr = "data:image/png;base64," + Convert.ToBase64String(png) });
    }

    private static async Task<IResult> TurnOn(TurnOnRequest request, HttpContext http, GomDbContext db, PasswordService passwords,
        HashLimiter limiter, SignInThrottle throttle, TimeProvider clock, CancellationToken ct)
    {
        if (request.Key is null || request.Code is null || string.IsNullOrEmpty(request.Password)) return Results.BadRequest(new { error = "missing_fields" });
        var key = Totp.FromBase32(request.Key);
        if (key is null) return Results.BadRequest(new { error = "invalid_code" });
        var me = GomClaims.User(http.User);
        if (await AccountEndpoints.CheckMyPassword(http, db, passwords, limiter, throttle, request.Password, ct) is { } refused) return refused;
        if (await db.Users.AnyAsync(u => u.Id == me && u.TwoStepKey != null, ct)) return Results.Conflict(new { error = "two_step_on" });
        if (Totp.Match(key, request.Code, clock.GetUtcNow(), 0) is not { } step) return Results.BadRequest(new { error = "invalid_code" });
        await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoStepKey, key).SetProperty(u => u.TwoStepLastStep, step), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TurnOff(TurnOffRequest request, HttpContext http, GomDbContext db, PasswordService passwords,
        HashLimiter limiter, SignInThrottle throttle, TimeProvider clock, CancellationToken ct)
    {
        if (request.Code is null || string.IsNullOrEmpty(request.Password)) return Results.BadRequest(new { error = "missing_fields" });
        var me = GomClaims.User(http.User);
        if (await AccountEndpoints.CheckMyPassword(http, db, passwords, limiter, throttle, request.Password, ct) is { } refused) return refused;
        var row = await db.Users.Where(u => u.Id == me).Select(u => new { u.TwoStepKey, u.TwoStepLastStep }).SingleAsync(ct);
        if (row.TwoStepKey is null) return Results.NoContent();
        if (!await Use(db, me, row.TwoStepKey, row.TwoStepLastStep, request.Code, clock.GetUtcNow(), ct)) return Results.BadRequest(new { error = "invalid_code" });
        await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoStepKey, (byte[]?)null).SetProperty(u => u.TwoStepLastStep, 0L), ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Takes a code for this person once: its step is written down only if it is later than the last one used, so of two sign-ins with
    /// the same code exactly one gets through.
    /// </summary>
    public static async Task<bool> Use(GomDbContext db, Guid userId, byte[] key, long lastUsed, string? code, DateTimeOffset now, CancellationToken ct)
    {
        if (Totp.Match(key, code, now, lastUsed) is not { } step) return false;
        return await db.Users.Where(u => u.Id == userId && u.TwoStepLastStep < step)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoStepLastStep, step), ct) == 1;
    }

    /// <summary>The server command for an owner who lost their phone: <c>--two-step-off gomId</c>. Returns what to print.</summary>
    public static async Task<string> TurnOffFromCommand(GomDbContext db, string? gomId, CancellationToken ct)
    {
        var id = gomId?.Trim().ToLowerInvariant();
        if (!GomId.IsValid(id)) return "Usage: --two-step-off <gomId>";
        var changed = await db.Users.Where(u => u.GomId == id && u.TwoStepKey != null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoStepKey, (byte[]?)null).SetProperty(u => u.TwoStepLastStep, 0L), ct);
        return changed == 1 ? $"Two-step sign-in is now off for {id}." : $"{id} has no two-step sign-in on (or no such account).";
    }
}
