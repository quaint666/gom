using System.Security.Claims;
using System.Text.Encodings.Web;
using Gom.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Auth;

public static class GomClaims
{
    public const string Scheme = "gom";
    public const string SessionId = "sid";
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string GomId = "gomid";
    public const string DisplayName = "displayname";
    public const string IsOwner = "isowner";

    public static Guid User(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(UserId)!);
    public static Guid Session(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(SessionId)!);
}

/// <summary>Reads the session cookie, finds the live session and user, and slides the expiry at most once a day.</summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions, ILoggerFactory logger, UrlEncoder encoder,
    GomDbContext db, TimeProvider clock, IOptions<AuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionCookie.Name, out var token) || string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        var hash = Tokens.Hash(token);
        var now = clock.GetUtcNow();
        var found = await (
            from s in db.Sessions
            join u in db.Users on s.UserId equals u.Id
            where s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now && u.DisabledAt == null
            select new { Session = s, User = u }).SingleOrDefaultAsync(Context.RequestAborted);
        if (found is null) return AuthenticateResult.Fail("No live session.");

        var session = found.Session;
        if (now - session.LastSeenAt >= TimeSpan.FromDays(1))
        {
            var o = auth.Value;
            var expires = Min(now.AddDays(o.SessionDays), session.CreatedAt.AddDays(o.SessionMaxDays));
            await db.Sessions.Where(s => s.Id == session.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now).SetProperty(x => x.ExpiresAt, expires), Context.RequestAborted);
            SessionCookie.Set(Response, token, expires);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(GomClaims.UserId, found.User.Id.ToString()),
            new Claim(GomClaims.SessionId, session.Id.ToString()),
            new Claim(GomClaims.GomId, found.User.GomId),
            new Claim(GomClaims.DisplayName, found.User.DisplayName),
            new Claim(GomClaims.IsOwner, found.User.IsOwner ? "true" : "false"),
        ], GomClaims.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), GomClaims.Scheme));
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
