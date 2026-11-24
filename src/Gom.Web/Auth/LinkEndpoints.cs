using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gom.Web.Auth;

/// <summary>Invites, sign-up through an invite, password-reset links, and the owner's lists.</summary>
public static class LinkEndpoints
{
    public sealed record JoinRequest(string? GomId, string? Password, string? DisplayName);
    public sealed record ResetRequest(string? Password);

    public static void MapLinks(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/api").RequireAuthorization("Owner");
        owner.MapPost("/invites", CreateInvite);
        owner.MapGet("/invites", ListInvites);
        owner.MapDelete("/invites/{id:guid}", RevokeInvite);
        owner.MapGet("/users", ListUsers);
        owner.MapPost("/users/{gomId}/reset-link", CreateReset);

        app.MapGet("/api/join/{token}", PeekInvite);
        app.MapPost("/api/join/{token}", Join);
        app.MapGet("/api/reset/{token}", PeekReset);
        app.MapPost("/api/reset/{token}", Reset);
    }

    // The same answer for a link that never existed, ran out, was used or was revoked: no hints.
    private static IResult InvalidLink() => Results.Json(new { error = "invalid_link" }, statusCode: StatusCodes.Status404NotFound);

    private static IResult Rejected(PasswordProblem problem) => Results.BadRequest(new
    {
        error = problem switch
        {
            PasswordProblem.TooShort => "password_too_short",
            PasswordProblem.TooLong => "password_too_long",
            _ => "password_too_common",
        },
    });

    private static IQueryable<OneTimeLink> Live(GomDbContext db, string token, LinkKind kind, DateTimeOffset now)
    {
        var hash = Tokens.Hash(token);
        return db.Links.Where(l => l.TokenHash == hash && l.Kind == kind && l.ConsumedAt == null && l.RevokedAt == null && l.ExpiresAt > now);
    }

    /// <summary>Marks the link used, but only if it is still live: of two people racing, exactly one gets 1 here.</summary>
    private static Task<int> Consume(GomDbContext db, Guid linkId, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        db.Links.Where(l => l.Id == linkId && l.ConsumedAt == null && l.RevokedAt == null && l.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ConsumedAt, now).SetProperty(l => l.ConsumedBy, (Guid?)userId), ct);

    // ---- the owner

    private static async Task<IResult> CreateInvite(HttpContext http, GomDbContext db, TimeProvider clock, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var issued = Tokens.New();
        var link = new OneTimeLink
        {
            Id = Guid.NewGuid(), Kind = LinkKind.Invite, TokenHash = issued.Hash, CreatedBy = GomClaims.User(http.User),
            CreatedAt = now, ExpiresAt = now.AddDays(options.Value.InviteDays),
        };
        db.Links.Add(link);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = link.Id, token = issued.Token, path = "/join/" + issued.Token, expiresAt = link.ExpiresAt });
    }

    private static async Task<IResult> ListInvites(GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = await db.Links.Where(l => l.Kind == LinkKind.Invite).OrderByDescending(l => l.CreatedAt)
            .Select(l => new
            {
                l.Id, l.CreatedAt, l.ExpiresAt, l.ConsumedAt, l.RevokedAt,
                UsedBy = db.Users.Where(u => u.Id == l.ConsumedBy).Select(u => u.GomId).FirstOrDefault(),
            }).ToListAsync(ct);
        return Results.Ok(rows.Select(r => new
        {
            id = r.Id, createdAt = r.CreatedAt, expiresAt = r.ExpiresAt, usedBy = r.UsedBy,
            status = r.ConsumedAt is not null ? "used" : r.RevokedAt is not null ? "revoked" : r.ExpiresAt <= now ? "expired" : "pending",
        }));
    }

    private static async Task<IResult> RevokeInvite(Guid id, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var revoked = await db.Links.Where(l => l.Id == id && l.Kind == LinkKind.Invite && l.ConsumedAt == null && l.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.RevokedAt, now), ct);
        return revoked == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> ListUsers(GomDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Users.OrderBy(u => u.CreatedAt)
            .Select(u => new { gomId = u.GomId, displayName = u.DisplayName, isOwner = u.IsOwner, disabled = u.DisabledAt != null, createdAt = u.CreatedAt })
            .ToListAsync(ct));

    private static async Task<IResult> CreateReset(string gomId, HttpContext http, GomDbContext db, TimeProvider clock, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var id = gomId.Trim().ToLowerInvariant();
        var target = await db.Users.Where(u => u.GomId == id && u.DisabledAt == null).Select(u => new { u.Id }).SingleOrDefaultAsync(ct);
        if (target is null) return Results.NotFound();

        var now = clock.GetUtcNow();
        // One live reset link per person: a new one cancels the old.
        await db.Links.Where(l => l.Kind == LinkKind.Reset && l.TargetUserId == target.Id && l.ConsumedAt == null && l.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.RevokedAt, now), ct);

        var issued = Tokens.New();
        var link = new OneTimeLink
        {
            Id = Guid.NewGuid(), Kind = LinkKind.Reset, TokenHash = issued.Hash, CreatedBy = GomClaims.User(http.User),
            TargetUserId = target.Id, CreatedAt = now, ExpiresAt = now.AddHours(options.Value.ResetHours),
        };
        db.Links.Add(link);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { token = issued.Token, path = "/reset/" + issued.Token, expiresAt = link.ExpiresAt });
    }

    // ---- sign-up through an invite

    private static async Task<IResult> PeekInvite(string token, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var invitedBy = await Live(db, token, LinkKind.Invite, clock.GetUtcNow())
            .Select(l => db.Users.Where(u => u.Id == l.CreatedBy).Select(u => u.DisplayName).First()).SingleOrDefaultAsync(ct);
        return invitedBy is null ? InvalidLink() : Results.Ok(new { invitedBy });
    }

    private static async Task<IResult> Join(string token, JoinRequest request, HttpContext http, GomDbContext db, PasswordService passwords,
        HashLimiter limiter, TimeProvider clock, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var link = await Live(db, token, LinkKind.Invite, now).SingleOrDefaultAsync(ct);
        if (link is null) return InvalidLink();

        if (request.GomId is null || request.Password is null) return Results.BadRequest(new { error = "missing_fields" });
        var gomId = request.GomId.Trim().ToLowerInvariant();
        if (!GomId.IsValid(gomId)) return Results.BadRequest(new { error = "invalid_gom_id" });
        var problem = PasswordPolicy.Check(request.Password);
        if (problem != PasswordProblem.None) return Rejected(problem);
        var displayName = DisplayName.Normalize(request.DisplayName, gomId);
        if (displayName is null) return Results.BadRequest(new { error = "invalid_display_name" });

        var password = request.Password;
        var hash = await limiter.RunAsync(() => (Hash: passwords.Hash(password), Ok: true));
        if (hash is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = new User { Id = Guid.NewGuid(), GomId = gomId, DisplayName = displayName, PasswordHash = hash.Value.Hash, CreatedAt = now };
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Json(new { error = "id_taken" }, statusCode: StatusCodes.Status409Conflict);
        }

        // Someone else may have used this invite since we looked: then nothing of ours is kept.
        if (await Consume(db, link.Id, user.Id, now, ct) == 0) return InvalidLink();

        var started = AuthEndpoints.StartSession(http, db, user.Id, now, options.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        SessionCookie.Set(http.Response, started.Token, started.Expires);
        return Results.Ok(new { gomId = user.GomId, displayName = user.DisplayName, isOwner = false });
    }

    // ---- password reset

    private static IQueryable<ResetTarget> FindReset(GomDbContext db, string token, DateTimeOffset now) =>
        Live(db, token, LinkKind.Reset, now)
            .Join(db.Users.Where(u => u.DisabledAt == null), l => l.TargetUserId, u => u.Id, (l, u) => new ResetTarget(l.Id, u.Id, u.GomId, u.DisplayName));

    private sealed record ResetTarget(Guid LinkId, Guid UserId, string GomId, string DisplayName);

    private static async Task<IResult> PeekReset(string token, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var found = await FindReset(db, token, clock.GetUtcNow()).SingleOrDefaultAsync(ct);
        return found is null ? InvalidLink() : Results.Ok(new { gomId = found.GomId, displayName = found.DisplayName });
    }

    private static async Task<IResult> Reset(string token, ResetRequest request, HttpContext http, GomDbContext db, PasswordService passwords,
        HashLimiter limiter, SignInThrottle throttle, TimeProvider clock, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var found = await FindReset(db, token, now).SingleOrDefaultAsync(ct);
        if (found is null) return InvalidLink();

        if (request.Password is null) return Results.BadRequest(new { error = "missing_fields" });
        var problem = PasswordPolicy.Check(request.Password);
        if (problem != PasswordProblem.None) return Rejected(problem);

        var password = request.Password;
        var hash = await limiter.RunAsync(() => (Hash: passwords.Hash(password), Ok: true));
        if (hash is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Consume(db, found.LinkId, found.UserId, now, ct) == 0) return InvalidLink();

        await db.Users.Where(u => u.Id == found.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, hash.Value.Hash), ct);
        // Whoever held the old password, on whatever device, is signed out.
        await db.Sessions.Where(s => s.UserId == found.UserId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        var started = AuthEndpoints.StartSession(http, db, found.UserId, now, options.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        throttle.Succeeded(found.GomId);
        SessionCookie.Set(http.Response, started.Token, started.Expires);
        var isOwner = await db.Users.Where(u => u.Id == found.UserId).Select(u => u.IsOwner).SingleAsync(ct);
        return Results.Ok(new { gomId = found.GomId, displayName = found.DisplayName, isOwner });
    }
}
