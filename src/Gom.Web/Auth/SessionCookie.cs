namespace Gom.Web.Auth;

/// <summary>The one cookie. The __Host- prefix makes browsers insist on Secure, Path=/ and no Domain.</summary>
public static class SessionCookie
{
    public const string Name = "__Host-gom";

    private static CookieOptions Options(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires,
    };

    public static void Set(HttpResponse response, string token, DateTimeOffset expires) =>
        response.Cookies.Append(Name, token, Options(expires));

    public static void Clear(HttpResponse response) => response.Cookies.Delete(Name, Options(null));
}
