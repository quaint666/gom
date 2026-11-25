using System.Globalization;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gom.Web.Auth;

/// <summary>
/// Asking to join: when the owner has switched it on, anyone may sign up and is signed in to an account that can do nothing but
/// wait (see the "standing" claim) until the owner approves it. The owner can also refuse (the person is told, and the owner can
/// still change their mind) or delete the request (the Gom ID is free again); the person can withdraw it. Invites skip all this.
/// </summary>
public static class SignUpEndpoints
{
    /// <summary>The longest "who are you" note, in characters as a person counts them.</summary>
    public const int NoteMaxLength = 200;

    public sealed record SignUpRequest(string? GomId, string? Password, string? DisplayName, string? Note);
    public sealed record OpenRequest(bool Open);

    public static void MapSignUp(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sign-up", IsOpen);
        app.MapPost("/api/sign-up", SignUp);
        app.MapDelete("/api/sign-up", Withdraw).RequireAuthorization("SignedIn");

        var owner = app.MapGroup("/api").RequireAuthorization("Owner");
        owner.MapPut("/sign-up-open", SetOpen);
        owner.MapGet("/join-requests", List);
        owner.MapPost("/join-requests/{gomId}/approve", Approve);
        owner.MapPost("/join-requests/{gomId}/refuse", Refuse);
        owner.MapDelete("/join-requests/{gomId}", Delete);
    }

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);

    private static async Task<bool> Open(GomDbContext db, CancellationToken ct) => await db.Site.Select(s => s.OpenSignUp).SingleAsync(ct);

    /// <summary>Whether the sign-in page offers to ask to join.</summary>
    private static async Task<IResult> IsOpen(GomDbContext db, CancellationToken ct) => Results.Ok(new { open = await Open(db, ct) });

    /// <summary>The note: trimmed, 1 to <see cref="NoteMaxLength"/> characters, no control or invisible formatting characters (new lines are fine).</summary>
    public static string? CleanNote(string? input)
    {
        var note = input?.Trim();
        if (string.IsNullOrEmpty(note) || new StringInfo(note).LengthInTextElements > NoteMaxLength) return null;
        foreach (var c in note)
        {
            if (c is '\n') continue;
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return null;
        }
        return note;
    }

    private static async Task<IResult> SignUp(SignUpRequest request, HttpContext http, GomDbContext db, PasswordService passwords, HashLimiter limiter,
        SignInThrottle throttle, Push.PushQueue push, TimeProvider clock, IOptions<AuthOptions> options, CancellationToken ct)
    {
        if (!await Open(db, ct)) return Error(StatusCodes.Status403Forbidden, "sign_up_closed");

        // Throttled per address as sign-in is: each sign-up counts, so one address cannot fill the waiting list in a burst.
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var wait = throttle.Begin(ip, "+sign-up " + ip);
        if (wait > TimeSpan.Zero)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Error(StatusCodes.Status429TooManyRequests, "too_many_attempts");
        }

        if (request.GomId is null || request.Password is null) return Error(StatusCodes.Status400BadRequest, "missing_fields");
        var gomId = request.GomId.Trim().ToLowerInvariant();
        if (!GomId.IsValid(gomId)) return Error(StatusCodes.Status400BadRequest, "invalid_gom_id");
        var problem = PasswordPolicy.Check(request.Password);
        if (problem != PasswordProblem.None) return LinkEndpoints.Rejected(problem);
        var displayName = DisplayName.Normalize(request.DisplayName, gomId);
        if (displayName is null) return Error(StatusCodes.Status400BadRequest, "invalid_display_name");
        if (CleanNote(request.Note) is not { } note) return Error(StatusCodes.Status400BadRequest, "invalid_note");

        var now = clock.GetUtcNow();
        var waiting = await db.Users.CountAsync(u => u.RequestedAt != null && u.RefusedAt == null, ct);
        if (waiting >= options.Value.MaxWaitingRequests) return Error(StatusCodes.Status403Forbidden, "sign_up_full");

        var password = request.Password;
        var hash = await limiter.RunAsync(() => (Hash: passwords.Hash(password), Ok: true));
        if (hash is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = new User
        {
            Id = Guid.NewGuid(), GomId = gomId, DisplayName = displayName, PasswordHash = hash.Value.Hash, CreatedAt = now,
            RequestedAt = now, RequestNote = note,
        };
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error(StatusCodes.Status409Conflict, "id_taken");
        }
        var started = AuthEndpoints.StartSession(http, db, user.Id, now, options.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        SessionCookie.Set(http.Response, started.Token, started.Expires);
        push.Enqueue(new Push.PushJob(Guid.Empty, 0, user.Id, displayName, 0, 0, JoinRequest: true));
        return Results.Ok(new { id = user.Id, gomId = user.GomId, displayName = user.DisplayName, standing = GomClaims.Pending });
    }

    /// <summary>The person takes back their own request (waiting or refused): the account goes, and its Gom ID is free again.</summary>
    private static async Task<IResult> Withdraw(HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (!await db.Users.AnyAsync(u => u.Id == me && u.RequestedAt != null, ct)) return Error(StatusCodes.Status409Conflict, "not_a_request");
        await Remove(db, me, ct);
        SessionCookie.Clear(http.Response);
        return Results.NoContent();
    }

    /// <summary>A request is only an account and its sessions (it could do nothing else), so it is deleted outright.</summary>
    private static async Task Remove(GomDbContext db, Guid user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Sessions.Where(s => s.UserId == user).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Id == user && u.RequestedAt != null).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<IResult> SetOpen(OpenRequest request, GomDbContext db, CancellationToken ct)
    {
        await db.Site.ExecuteUpdateAsync(s => s.SetProperty(x => x.OpenSignUp, request.Open), ct);
        return Results.NoContent();
    }

    /// <summary>Waiting requests, oldest first, then refused ones; and whether sign-up is open.</summary>
    private static async Task<IResult> List(GomDbContext db, CancellationToken ct)
    {
        var requests = await db.Users.Where(u => u.RequestedAt != null).OrderBy(u => u.RefusedAt != null).ThenBy(u => u.RequestedAt)
            .Select(u => new { gomId = u.GomId, displayName = u.DisplayName, note = u.RequestNote, requestedAt = u.RequestedAt, refused = u.RefusedAt != null })
            .ToListAsync(ct);
        return Results.Ok(new { open = await Open(db, ct), requests });
    }

    private static Task<Guid> RequestId(GomDbContext db, string gomId, CancellationToken ct)
    {
        var id = gomId.Trim().ToLowerInvariant();
        return db.Users.Where(u => u.GomId == id && u.RequestedAt != null).Select(u => u.Id).SingleOrDefaultAsync(ct);
    }

    /// <summary>The request becomes a member, and the owner who approved it its first friend (as an invite makes the inviter).</summary>
    private static async Task<IResult> Approve(string gomId, HttpContext http, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var user = await RequestId(db, gomId, ct);
        if (user == Guid.Empty) return Results.NotFound();
        var owner = GomClaims.User(http.User);
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.Users.Where(u => u.Id == user && u.RequestedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.RequestedAt, (DateTimeOffset?)null).SetProperty(u => u.RefusedAt, (DateTimeOffset?)null).SetProperty(u => u.RequestNote, (string?)null), ct) == 0)
            return Results.NotFound();
        var (low, high) = Friendship.Order(owner, user);
        db.Friendships.Add(new Friendship { UserLow = low, UserHigh = high, RequestedBy = owner, Status = FriendStatus.Accepted, CreatedAt = now, AcceptedAt = now });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Refused: the person can still sign in, and is told; the owner can approve or delete it later.</summary>
    private static async Task<IResult> Refuse(string gomId, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var id = gomId.Trim().ToLowerInvariant();
        var done = await db.Users.Where(u => u.GomId == id && u.RequestedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.RefusedAt, u => u.RefusedAt ?? now), ct);
        return done == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> Delete(string gomId, GomDbContext db, CancellationToken ct)
    {
        var user = await RequestId(db, gomId, ct);
        if (user == Guid.Empty) return Results.NotFound();
        await Remove(db, user, ct);
        return Results.NoContent();
    }
}
