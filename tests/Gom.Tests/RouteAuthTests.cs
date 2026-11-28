using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Gom.Tests;

/// <summary>
/// Every address under /api, and the live socket, asks for a signed-in person, except the few a visitor needs before having an
/// account. A new endpoint that forgets RequireAuthorization fails here instead of shipping open.
/// </summary>
public class RouteAuthTests(AppFixture app) : IClassFixture<AppFixture>
{
    /// <summary>What a visitor may call: signing in, an invite, asking to join, a password reset, and writing to the owner. Each checks its own token or limit.</summary>
    private static readonly HashSet<string> Open =
    [
        "POST /api/session",
        "GET /api/join/{token}", "POST /api/join/{token}",
        "GET /api/sign-up", "POST /api/sign-up",
        "GET /api/reset/{token}", "POST /api/reset/{token}",
        "POST /api/contact",
    ];

    [Fact]
    public void Every_api_address_needs_a_signed_in_person_except_the_few_a_visitor_needs()
    {
        var endpoints = app.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && (raw.StartsWith("/api", StringComparison.Ordinal) || raw == "/ws"));
        var open = new List<string>();
        foreach (var e in endpoints)
        {
            var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"];
            var signedIn = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0 && e.Metadata.GetMetadata<IAllowAnonymous>() is null;
            if (signedIn) continue;
            open.AddRange(methods.Select(m => $"{m} {e.RoutePattern.RawText}"));
        }
        Assert.Empty(open.Where(o => !Open.Contains(o)).Order());
    }

    /// <summary>The owner's tools, each written out: one that loses its Owner policy, or a new one, changes this list on purpose.</summary>
    private static readonly string[] OwnerOnly =
    [
        "DELETE /api/contact/{id:guid}", "DELETE /api/invites/{id:guid}", "DELETE /api/join-requests/{gomId}", "DELETE /api/owner/groups/{id:guid}",
        "DELETE /api/owner/sticker-packs/{id:guid}", "DELETE /api/owner/stickers/{id:guid}",
        "GET /api/contact", "GET /api/invites", "GET /api/join-requests", "GET /api/owner/storage", "GET /api/users",
        "POST /api/invites", "POST /api/join-requests/{gomId}/approve", "POST /api/join-requests/{gomId}/refuse",
        "POST /api/owner/sticker-packs", "POST /api/owner/sticker-packs/{id:guid}/stickers",
        "POST /api/owner/trim/files", "POST /api/owner/trim/files/preview", "POST /api/owner/trim/messages", "POST /api/owner/trim/messages/preview",
        "POST /api/users/{gomId}/disable", "POST /api/users/{gomId}/enable", "POST /api/users/{gomId}/reset-link",
        "PUT /api/owner/sticker-packs/{id:guid}/where", "PUT /api/sign-up-open",
    ];

    /// <summary>
    /// The owner's tools (Settings › Trang quản trị and Dung lượng) are hidden from members on the page, but the page is not the
    /// guard: each is behind the Owner policy, and answers a signed-in member 403 before it reads anything.
    /// </summary>
    [Fact]
    public async Task Every_owner_address_refuses_a_member()
    {
        var owner = app.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == "Owner"))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (Method: m, Route: e.RoutePattern.RawText!)))
            .ToList();
        Assert.Equal(OwnerOnly, owner.Select(o => $"{o.Method} {o.Route}").Order());
        var (_, member) = await app.Member();
        var wrong = new List<string>();
        foreach (var (method, route) in owner)
        {
            var path = Filled(route);
            using var r = await app.Call(new HttpMethod(method), path, method is "GET" or "DELETE" ? null : new { }, member);
            if (r.StatusCode != System.Net.HttpStatusCode.Forbidden) wrong.Add($"{method} {path}: {(int)r.StatusCode}");
        }
        Assert.Empty(wrong);
    }

    /// <summary>A route's parameters filled with something of the right shape ({id:guid} with a GUID, anything else with a word).</summary>
    private static string Filled(string pattern) =>
        System.Text.RegularExpressions.Regex.Replace(pattern, @"\{([a-zA-Z]+)(:[^}]*)?\}", m => m.Groups[2].Value.Contains("guid") ? Guid.NewGuid().ToString() : "someone");
}
