using Microsoft.Extensions.Options;

namespace Gom.Web.Auth;

/// <summary>Every request that can change something must name one of our own origins. A form on another site cannot fake this header.</summary>
public sealed class OriginCheck(RequestDelegate next, IOptions<AuthOptions> options)
{
    private readonly HashSet<string> _allowed = options.Value.AllowedOrigins.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Task Invoke(HttpContext context)
    {
        // A WebSocket upgrade is a GET, but it opens a long-lived channel with the person's cookie: it must name our origin too.
        var upgrade = context.Request.Headers.Upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase);
        if (!upgrade && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method)))
            return next(context);

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && _allowed.Contains(origin)) return next(context);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new { error = "bad_origin" });
    }
}
