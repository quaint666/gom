using System.Net;
using System.Text.Json;

namespace Gom.Tests;

/// <summary>A conversation's own colour and background: anyone in it sets them for everyone in it, and a line says who did.</summary>
public class ConversationLookTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Put = HttpMethod.Put, Get = HttpMethod.Get, Post = HttpMethod.Post, Delete = HttpMethod.Delete;

    private async Task<(Gom.Web.Data.User A, string CA, string CB, string CC, Guid Group)> Group()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà mình", members = new[] { b.GomId, c.GomId } }, ca);
        return (a, ca, cb, cc, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private Task<HttpResponseMessage> SetLook(Guid chat, string cookie, string? palette, string? pattern) =>
        app.Call(Put, $"/api/conversations/{chat}/look", new { palette, pattern }, cookie);

    private async Task<JsonElement> Listed(Guid chat, string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chat).Clone();
    }

    private async Task<List<JsonElement>> Restyled(Guid chat, string cookie)
    {
        using var r = await app.Call(Get, $"/api/conversations/{chat}/messages", cookie: cookie);
        return [.. (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray()
            .Where(m => m.TryGetProperty("notice", out var n) && n.ValueKind == JsonValueKind.Object && n.GetProperty("type").GetString() == "restyled")
            .Select(m => m.Clone())];
    }

    [Fact]
    public async Task Any_member_sets_it_everyone_sees_it_and_one_line_says_who()
    {
        var g = await Group();
        var before = await Listed(g.Group, g.CB);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("palette").ValueKind);   // each person's own until someone chooses
        Assert.Equal(JsonValueKind.Null, before.GetProperty("pattern").ValueKind);

        using (var r = await SetLook(g.Group, g.CC, "sea", "waves")) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);   // a plain member
        foreach (var cookie in new[] { g.CA, g.CB, g.CC })
        {
            var seen = await Listed(g.Group, cookie);
            Assert.Equal("sea", seen.GetProperty("palette").GetString());
            Assert.Equal("waves", seen.GetProperty("pattern").GetString());
        }
        var line = Assert.Single(await Restyled(g.Group, g.CA));
        Assert.Equal(JsonValueKind.Null, line.GetProperty("notice").GetProperty("target").ValueKind);
        Assert.Equal(0, (await Listed(g.Group, g.CA)).GetProperty("unread").GetInt32());   // a notice is not unread

        // The same again changes nothing and says nothing; back to each person's own says so once.
        var seq = (await Listed(g.Group, g.CA)).GetProperty("lastSeq").GetInt64();
        using (var same = await SetLook(g.Group, g.CB, "sea", "waves")) Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);
        Assert.Equal(seq, (await Listed(g.Group, g.CA)).GetProperty("lastSeq").GetInt64());
        using (var reset = await SetLook(g.Group, g.CB, null, null)) Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Listed(g.Group, g.CC)).GetProperty("palette").ValueKind);
        Assert.Equal(2, (await Restyled(g.Group, g.CC)).Count);
    }

    [Fact]
    public async Task Only_codes_Gom_has_and_only_someone_in_it_and_a_one_to_one_chat_only_between_friends()
    {
        var g = await Group();
        using (var bad = await SetLook(g.Group, g.CA, "neon", null)) Assert.Equal("invalid_palette", (await AppFixture.Json(bad)).GetProperty("error").GetString());
        using (var bad = await SetLook(g.Group, g.CA, null, "hearts")) Assert.Equal("invalid_pattern", (await AppFixture.Json(bad)).GetProperty("error").GetString());
        var (_, stranger) = await app.Member();
        using (var r = await SetLook(g.Group, stranger, "sea", null)) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(await Restyled(g.Group, g.CA));

        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var direct = await app.OpenChat(ca, b.GomId);
        using (var r = await SetLook(direct, cb, "lotus", "lotus")) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("lotus", (await Listed(direct, ca)).GetProperty("palette").GetString());
        using (await app.Call(Delete, $"/api/friends/{b.GomId}", cookie: ca)) { }
        using var after = await SetLook(direct, ca, "jade", null);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal("not_friends", (await AppFixture.Json(after)).GetProperty("error").GetString());
    }
}
