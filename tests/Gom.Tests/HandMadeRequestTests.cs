using System.Net;

namespace Gom.Tests;

/// <summary>
/// What a hand-made request can do. PostgreSQL refuses a NUL in text, so a NUL that reached a query was a 500: now it is refused or found
/// to be nobody, as anything else that cannot be valid. And a value that does not bind is a 400, never a quiet default.
/// </summary>
public class HandMadeRequestTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post;
    private const string Nul = "a\u0000b";

    private async Task<HttpStatusCode> Status(HttpMethod method, string path, object? body, string? cookie)
    {
        using var r = await app.Call(method, path, body, cookie);
        return r.StatusCode;
    }

    [Fact]
    public async Task A_nul_in_a_gom_id_is_nobody_wherever_a_person_is_named()
    {
        var (a, ca) = await app.Member();
        var (b, _) = await app.Member();
        await app.Befriend(a, b);
        using (var g = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId } }, ca))
        {
            var group = (await AppFixture.Json(g)).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, await Status(Post, $"/api/conversations/{group}/members", new { gomId = Nul }, ca));
        }
        Assert.Equal(HttpStatusCode.NotFound, await Status(Post, "/api/friends/requests", new { gomId = Nul }, ca));
        Assert.Equal(HttpStatusCode.NotFound, await Status(Post, "/api/conversations/direct", new { gomId = Nul }, ca));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(Post, "/api/conversations/group", new { title = "Nhóm", members = new[] { b.GomId, Nul } }, ca));
    }

    [Fact]
    public async Task A_nul_in_the_sign_in_id_is_a_wrong_sign_in()
    {
        using var r = await AppFixture.Send(app.Client(), Post, "/api/session", new { gomId = Nul, password = "anything at all" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("invalid_credentials", (await AppFixture.Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_nul_in_a_search_is_a_space()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        using (await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "phở bò" }, cb)) { }
        using var r = await app.Call(Get, $"/api/conversations/{chat}/search?q=ph%E1%BB%9F%00b%C3%B2", cookie: ca);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Single((await AppFixture.Json(r)).GetProperty("results").EnumerateArray());
        using var only = await app.Call(Get, $"/api/conversations/{chat}/search?q=%00", cookie: ca);
        Assert.Equal(HttpStatusCode.BadRequest, only.StatusCode);   // nothing left once it is a space
    }

    [Fact]
    public async Task A_history_size_that_is_not_a_number_is_a_400()
    {
        var (a, ca) = await app.Member();
        var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        Assert.Equal(HttpStatusCode.BadRequest, await Status(Get, $"/api/conversations/{chat}/messages?limit=abc", null, ca));
        Assert.Equal(HttpStatusCode.OK, await Status(Get, $"/api/conversations/{chat}/messages?limit=5", null, ca));
        Assert.Equal(HttpStatusCode.OK, await Status(Get, $"/api/conversations/{chat}/messages", null, ca));
    }
}
