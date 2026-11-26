namespace Gom.Web.Auth;

/// <summary>
/// Headers on every response. The policy lets the page run only its own script and styles (no inline), talk only to
/// itself, and never be framed. Pages and API answers are never cached (except a picture, which says so itself, privately), and no page leaks its address (it can contain
/// an invite token) through the Referer header.
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    private const string Policy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self'; font-src 'self'; connect-src 'self'; " +
        "form-action 'self'; base-uri 'none'; frame-ancestors 'none'; manifest-src 'self'; worker-src 'self'";

    public Task Invoke(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = Policy;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(self), geolocation=()";
            // Never cached, unless an answer says on its own how long it may be (the pictures of a conversation do, privately).
            if ((context.Request.Path.StartsWithSegments("/api") || (context.Response.ContentType?.StartsWith("text/html") ?? false)) && !headers.ContainsKey("Cache-Control"))
                headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        return next(context);
    }
}
