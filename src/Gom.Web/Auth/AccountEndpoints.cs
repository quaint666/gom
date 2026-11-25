using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Auth;

/// <summary>Your own nickname and password, deleting your account (<see cref="AccountDeletion"/>), and the owner switching a member off and on.</summary>
public static class AccountEndpoints
{
    public sealed record NicknameRequest(string? DisplayName);
    public sealed record PasswordRequest(string? Current, string? Password);
    public sealed record LanguageRequest(string? Language);
    public sealed record PaletteRequest(string? Palette);
    public sealed record PatternRequest(string? Pattern);

    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        app.MapPatch("/api/me", ChangeNickname).RequireAuthorization();
        app.MapPut("/api/me/password", ChangePassword).RequireAuthorization();
        app.MapPut("/api/me/language", ChangeLanguage).RequireAuthorization();
        app.MapPut("/api/me/palette", ChangePalette).RequireAuthorization();
        app.MapPut("/api/me/pattern", ChangePattern).RequireAuthorization();
        app.MapDelete("/api/me", AccountDeletion.Delete).RequireAuthorization();

        var owner = app.MapGroup("/api/users").RequireAuthorization("Owner");
        owner.MapPost("/{gomId}/disable", Disable);
        owner.MapPost("/{gomId}/enable", Enable);
    }

    /// <summary>The person's language, for their pages on every device and for what their notifications say.</summary>
    private static async Task<IResult> ChangeLanguage(LanguageRequest request, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        if (request.Language is not { } language || !ServerText.Languages.Contains(language)) return Results.BadRequest(new { error = "invalid_language" });
        var me = GomClaims.User(http.User);
        await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, language), ct);
        return Results.NoContent();
    }

    /// <summary>The colour of the person's pages, on every device.</summary>
    private static async Task<IResult> ChangePalette(PaletteRequest request, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        if (request.Palette is not { } palette || !Chat.Appearance.Palettes.Contains(palette)) return Results.BadRequest(new { error = "invalid_palette" });
        var me = GomClaims.User(http.User);
        await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(s => s.SetProperty(u => u.Palette, palette), ct);
        return Results.NoContent();
    }

    /// <summary>The background behind the person's messages, on every device.</summary>
    private static async Task<IResult> ChangePattern(PatternRequest request, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        if (request.Pattern is not { } pattern || !Chat.Appearance.Patterns.Contains(pattern)) return Results.BadRequest(new { error = "invalid_pattern" });
        var me = GomClaims.User(http.User);
        await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(s => s.SetProperty(u => u.Pattern, pattern), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangeNickname(NicknameRequest request, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var gomId = http.User.FindFirst(GomClaims.GomId)!.Value;
        var name = DisplayName.Normalize(request.DisplayName, gomId);
        if (name is null) return Results.BadRequest(new { error = "invalid_display_name" });

        var me = GomClaims.User(http.User);
        await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisplayName, name), ct);
        return Results.Ok(new { id = me, gomId, displayName = name, isOwner = http.User.FindFirst(GomClaims.IsOwner)!.Value == "true" });
    }

    private static async Task<IResult> ChangePassword(PasswordRequest request, HttpContext http, GomDbContext db, PasswordService passwords,
        HashLimiter limiter, SignInThrottle throttle, TimeProvider clock, ConnectionRegistry sockets, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Current) || request.Password is null) return Results.BadRequest(new { error = "missing_fields" });

        // A stolen session must not be a free way to guess the password.
        var gomId = http.User.FindFirst(GomClaims.GomId)!.Value;
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var wait = throttle.Begin(ip, gomId);
        if (wait > TimeSpan.Zero)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
            return Results.Json(new { error = "too_many_attempts" }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        var me = GomClaims.User(http.User);
        var stored = await db.Users.Where(u => u.Id == me).Select(u => u.PasswordHash).SingleAsync(ct);
        var current = request.Current;
        var check = await limiter.RunAsync(() => passwords.Verify(stored, current));
        if (check is null) { throttle.Undo(ip, gomId); return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        // A wrong password was counted by Begin already; a right one takes its count back.
        if (check == PasswordCheck.Wrong) return Results.BadRequest(new { error = "wrong_password" });
        throttle.Succeeded(ip, gomId);

        var problem = PasswordPolicy.Check(request.Password);
        if (problem != PasswordProblem.None) return Results.BadRequest(new
        {
            error = problem switch { PasswordProblem.TooShort => "password_too_short", PasswordProblem.TooLong => "password_too_long", _ => "password_too_common" },
        });

        var password = request.Password;
        var hash = await limiter.RunAsync(() => (Hash: passwords.Hash(password), Ok: true));
        if (hash is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var now = clock.GetUtcNow();
        var here = GomClaims.Session(http.User);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, hash.Value.Hash), ct);
        // Whoever else holds the old password is signed out; this device stays.
        await db.Sessions.Where(s => s.UserId == me && s.Id != here && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        await tx.CommitAsync(ct);

        sockets.CloseUser(me, here);
        return Results.NoContent();
    }

    private static async Task<IResult> Disable(string gomId, GomDbContext db, TimeProvider clock, ConnectionRegistry sockets, CancellationToken ct)
    {
        var id = gomId.Trim().ToLowerInvariant();
        var target = await db.Users.Where(u => u.GomId == id).Select(u => new { u.Id, u.IsOwner }).SingleOrDefaultAsync(ct);
        if (target is null) return Results.NotFound();
        if (target.IsOwner) return Results.BadRequest(new { error = "cannot_disable_owner" });

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Users.Where(u => u.Id == target.Id && u.DisabledAt == null).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisabledAt, (DateTimeOffset?)now), ct);
        await db.Sessions.Where(s => s.UserId == target.Id && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        await tx.CommitAsync(ct);
        sockets.CloseUser(target.Id);
        return Results.NoContent();
    }

    private static async Task<IResult> Enable(string gomId, GomDbContext db, CancellationToken ct)
    {
        var id = gomId.Trim().ToLowerInvariant();
        var user = await db.Users.Where(u => u.GomId == id).Select(u => new { u.Id, Deleted = u.DeletedAt != null }).SingleOrDefaultAsync(ct);
        if (user is null) return Results.NotFound();
        // A deleted account has no password and no name to come back to.
        if (user.Deleted) return Results.Json(new { error = "account_deleted" }, statusCode: StatusCodes.Status409Conflict);
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisabledAt, (DateTimeOffset?)null), ct);
        return Results.NoContent();
    }
}
