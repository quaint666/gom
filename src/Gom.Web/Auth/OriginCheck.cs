using Microsoft.Extensions.Options;

namespace Gom.Web.Auth;

/// <summary>Every request that can change something must name one of our own origins. A form on another site cannot fake this header.</summary>
public sealed class OriginCheck(RequestDelegate next, IOptions<AuthOptions> options)
{
    private readonly HashSet<string> _allowed = options.Value.AllowedOrigins.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Task Invoke(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method))
            return next(context);

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && _allowed.Contains(origin)) return next(context);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new { error = "bad_origin" });
    }
}
