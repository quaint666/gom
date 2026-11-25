using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Auth;

public static class AuthEndpoints
{
    public sealed record SignInRequest(string? GomId, string? Password);

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/session", SignIn);
        app.MapDelete("/api/session", SignOut).RequireAuthorization("SignedIn");
        app.MapGet("/api/me", async (HttpContext c, GomDbContext db, Microsoft.Extensions.Options.IOptions<Gom.Web.Uploads.UploadOptions> uploads, CancellationToken ct) =>
        {
            var row = await db.Users.Where(u => u.Id == GomClaims.User(c.User)).Select(u => new { u.AvatarId, u.Language, u.Palette, u.Pattern }).SingleAsync(ct);
            return Results.Ok(new
            {
            id = GomClaims.User(c.User),
            gomId = c.User.FindFirst(GomClaims.GomId)!.Value,
            displayName = c.User.FindFirst(GomClaims.DisplayName)!.Value,
            isOwner = c.User.FindFirst(GomClaims.IsOwner)!.Value == "true",
            readReceipts = c.User.FindFirst(GomClaims.ReadReceipts)!.Value == "true",
            // Whether this server takes pictures and files, so the page does not offer what can only fail.
            uploads = !string.IsNullOrWhiteSpace(uploads.Value.Path),
            avatar = row.AvatarId,
            // The person's language: a page in another one remembers it and reloads.
            language = row.Language,
            // The colour of their pages: a page remembers it for the next one, so it is drawn in it from the first paint.
            palette = row.Palette,
            pattern = row.Pattern,
            // "member", or for a request to join "pending" or "refused": the page then shows only its waiting screen.
            standing = c.User.FindFirst(GomClaims.Standing)?.Value ?? "member",
            });
        }).RequireAuthorization("SignedIn");
        app.MapGet("/api/sessions", ListSessions).RequireAuthorization();
        app.MapDelete("/api/sessions/{id:guid}", EndSession).RequireAuthorization();
    }

    private static async Task<IResult> SignIn(
        SignInRequest request, HttpContext http, GomDbContext db, PasswordService passwords, SignInThrottle throttle,
        HashLimiter limiter, TimeProvider clock, Microsoft.Extensions.Options.IOptions<AuthOptions> options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.GomId) || string.IsNullOrEmpty(request.Password))
            return Results.BadRequest(new { error = "missing_fields" });

        var gomId = request.GomId.Trim().ToLowerInvariant();
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var wait = throttle.Begin(ip, gomId);
        if (wait > TimeSpan.Zero)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
            return Results.Json(new { error = "too_many_attempts" }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        // Something that cannot be a Gom ID is nobody, without asking the database (a NUL there would be a 500).
        var user = GomId.IsValid(gomId) ? await db.Users.SingleOrDefaultAsync(u => u.GomId == gomId, ct) : null;

        // A missing user is checked against a dummy hash, so the answer takes as long either way.
        var password = request.Password;
        var check = await limiter.RunAsync(() =>
        {
            if (user is null) { passwords.BurnTime(password); return PasswordCheck.Wrong; }
            return passwords.Verify(user.PasswordHash, password);
        });
        if (check is null) { throttle.Undo(ip, gomId); return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }

        // A wrong password was counted by Begin already.
        if (user is null || user.DisabledAt is not null || check == PasswordCheck.Wrong)
            return Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);

        throttle.Succeeded(ip, gomId);

        if (check == PasswordCheck.OkRehash)
        {
            var fresh = await limiter.RunAsync(() => (Hash: passwords.Hash(password), Ok: true));
            if (fresh is not null)
                await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, fresh.Value.Hash), ct);
        }

        var now = clock.GetUtcNow();
        var started = StartSession(http, db, user.Id, now, options.Value);
        await db.SaveChangesAsync(ct);

        SessionCookie.Set(http.Response, started.Token, started.Expires);
        return Results.Ok(new { id = user.Id, gomId = user.GomId, displayName = user.DisplayName, isOwner = user.IsOwner });
    }

    /// <summary>Adds a new session for the user to the context; the caller saves it, then sets the cookie.</summary>
    internal static (string Token, DateTimeOffset Expires) StartSession(HttpContext http, GomDbContext db, Guid userId, DateTimeOffset now, AuthOptions options)
    {
        var issued = Tokens.New();
        var expires = now.AddDays(options.SessionDays);
        db.Sessions.Add(new Session
        {
            Id = Guid.NewGuid(), UserId = userId, TokenHash = issued.Hash,
            DeviceLabel = DeviceLabel.From(http.Request.Headers.UserAgent.ToString()),
            CreatedAt = now, LastSeenAt = now, ExpiresAt = expires,
        });
        return (issued.Token, expires);
    }

    private static async Task<IResult> SignOut(HttpContext http, GomDbContext db, TimeProvider clock, ConnectionRegistry sockets, CancellationToken ct)
    {
        var sid = GomClaims.Session(http.User);
        var now = clock.GetUtcNow();
        await db.Sessions.Where(s => s.Id == sid).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        sockets.CloseSession(sid);
        SessionCookie.Clear(http.Response);
        return Results.NoContent();
    }

    private static async Task<IResult> ListSessions(HttpContext http, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var current = GomClaims.Session(http.User);
        var now = clock.GetUtcNow();
        var rows = await db.Sessions
            .Where(s => s.UserId == me && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new { id = s.Id, deviceLabel = s.DeviceLabel, createdAt = s.CreatedAt, lastSeenAt = s.LastSeenAt, current = s.Id == current })
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> EndSession(Guid id, HttpContext http, GomDbContext db, TimeProvider clock, ConnectionRegistry sockets, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var now = clock.GetUtcNow();
        var ended = await db.Sessions.Where(s => s.Id == id && s.UserId == me && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        if (ended == 0) return Results.NotFound();
        sockets.CloseSession(id);
        if (id == GomClaims.Session(http.User)) SessionCookie.Clear(http.Response);
        return Results.NoContent();
    }
}
