using System.Net;
using System.Text.Json;
using Gom.Web.Data;

namespace Gom.Tests;

/// <summary>Looking for words in one conversation, or in all of yours.</summary>
public class SearchTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put, Delete = HttpMethod.Delete;

    private sealed record Team(User A, string CA, User B, string CB, User E, string CE, Guid Chat);

    private async Task<Team> NewTeam()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (e, ce) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, e);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId } }, ca);
        return new Team(a, ca, b, cb, e, ce, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private async Task<Guid> Say(Team t, string cookie, string body)
    {
        using var r = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<string[]> Find(Team t, string cookie, string q)
    {
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/search?q={Uri.EscapeDataString(q)}", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("results").EnumerateArray().Select(m => m.GetProperty("body").GetString()!).ToArray();
    }

    [Fact]
    public async Task Finds_a_word_whatever_its_capitals()
    {
        var t = await NewTeam();
        await Say(t, t.CB, "Tối nay ĂN Phở nhé");
        await Say(t, t.CB, "không liên quan");
        Assert.Equal(new[] { "Tối nay ĂN Phở nhé" }, await Find(t, t.CA, "phở"));
        Assert.Equal(new[] { "Tối nay ĂN Phở nhé" }, await Find(t, t.CA, "PHỞ"));
    }

    [Fact]
    public async Task Finds_every_word_in_any_order_and_not_a_message_with_only_some_of_them()
    {
        var t = await NewTeam();
        await Say(t, t.CB, "Phở ở Hà Nội ngon lắm");
        await Say(t, t.CB, "phở Sài Gòn");
        Assert.Equal(new[] { "Phở ở Hà Nội ngon lắm" }, await Find(t, t.CA, "ha noi pho"));
        Assert.Equal(new[] { "Phở ở Hà Nội ngon lắm" }, await Find(t, t.CA, "  noi   PHỞ "));
        Assert.Empty(await Find(t, t.CA, "phở huế"));
    }

    [Fact]
    public async Task Ignores_accents_both_ways()
    {
        var t = await NewTeam();
        await Say(t, t.CB, "Đình ăn phở");
        await Say(t, t.CB, "pho bò");
        Assert.Equal(2, (await Find(t, t.CA, "pho")).Length);
        Assert.Contains("Đình ăn phở", await Find(t, t.CA, "dinh"));
        Assert.Contains("Đình ăn phở", await Find(t, t.CA, "Đình"));
        Assert.Contains("pho bò", await Find(t, t.CA, "phở"));
    }

    [Fact]
    public async Task Newest_first_and_at_most_thirty()
    {
        var t = await NewTeam();
        // Two writers, so neither runs into the limit on how fast one person may send.
        for (var i = 1; i <= 35; i++) await Say(t, i % 2 == 0 ? t.CA : t.CB, $"mì số {i}");
        var found = await Find(t, t.CA, "mì số");
        Assert.Equal(30, found.Length);
        Assert.Equal("mì số 35", found[0]);
        Assert.Equal("mì số 6", found[^1]);
    }

    [Fact]
    public async Task Percent_underscore_and_backslash_are_ordinary_characters()
    {
        var t = await NewTeam();
        await Say(t, t.CB, "giảm 50% hôm nay");
        await Say(t, t.CB, "tên_file");
        await Say(t, t.CB, @"đường dẫn C:\tmp");
        await Say(t, t.CB, "một câu bình thường");
        Assert.Equal(new[] { "giảm 50% hôm nay" }, await Find(t, t.CA, "%"));
        Assert.Equal(new[] { "tên_file" }, await Find(t, t.CA, "_"));
        Assert.Equal(new[] { @"đường dẫn C:\tmp" }, await Find(t, t.CA, @"\"));
        Assert.Empty(await Find(t, t.CA, "50%x"));
    }

    [Fact]
    public async Task Does_not_find_what_was_taken_back()
    {
        var t = await NewTeam();
        var gone = await Say(t, t.CB, "bí mật rồi thu hồi");
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{gone}", cookie: t.CB)).Dispose();
        Assert.Empty(await Find(t, t.CA, "bí mật"));
    }

    [Fact]
    public async Task Does_not_find_what_you_deleted_for_yourself_but_others_still_do()
    {
        var t = await NewTeam();
        var one = await Say(t, t.CB, "tin cần tìm");
        (await app.Call(Put, $"/api/conversations/{t.Chat}/messages/{one}/hidden", cookie: t.CA)).Dispose();
        Assert.Empty(await Find(t, t.CA, "cần tìm"));
        Assert.Single(await Find(t, t.CB, "cần tìm"));
    }

    [Fact]
    public async Task Does_not_find_what_was_said_before_you_joined()
    {
        var t = await NewTeam();
        await Say(t, t.CA, "chuyện cũ về phở");
        (await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = t.E.GomId }, t.CA)).Dispose();
        await Say(t, t.CA, "chuyện mới về phở");
        Assert.Equal(new[] { "chuyện mới về phở" }, await Find(t, t.CE, "phở"));
        Assert.Equal(2, (await Find(t, t.CA, "phở")).Length);
    }

    [Fact]
    public async Task Only_looks_in_the_conversation_asked_about()
    {
        var t = await NewTeam();
        var other = await app.OpenChat(t.CA, t.E.GomId);
        (await app.Call(Post, $"/api/conversations/{other}/messages", new { clientId = Guid.NewGuid(), body = "phở bên kia" }, t.CA)).Dispose();
        await Say(t, t.CB, "phở bên này");
        Assert.Equal(new[] { "phở bên này" }, await Find(t, t.CA, "phở"));
    }

    [Fact]
    public async Task Someone_outside_gets_a_404_and_learns_nothing()
    {
        var t = await NewTeam();
        await Say(t, t.CB, "phở");
        var (_, stranger) = await app.Member();
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/search?q=ph", cookie: stranger);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_search_is_a_400(string q)
    {
        var t = await NewTeam();
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/search?q={Uri.EscapeDataString(q)}", cookie: t.CA);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var none = await app.Call(Get, $"/api/conversations/{t.Chat}/search", cookie: t.CA);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Fact]
    public async Task A_search_longer_than_a_hundred_characters_is_a_400_and_exactly_a_hundred_is_fine()
    {
        var t = await NewTeam();
        using var tooLong = await app.Call(Get, $"/api/conversations/{t.Chat}/search?q={new string('a', 101)}", cookie: t.CA);
        using var fine = await app.Call(Get, $"/api/conversations/{t.Chat}/search?q={new string('a', 100)}", cookie: t.CA);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
    }

    [Fact]
    public async Task A_result_names_who_wrote_it_by_name_and_opaque_id_and_never_a_Gom_ID()
    {
        var t = await NewTeam();
        // Test members are named after their Gom ID; give B a real name so a leaked ID cannot hide behind it.
        (await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "Bé Ba" }, t.CB)).Dispose();
        await Say(t, t.CB, "phở nhé");
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/search?q=ph", cookie: t.CA);
        var text = await r.Content.ReadAsStringAsync();
        var hit = JsonDocument.Parse(text).RootElement.GetProperty("results")[0];
        Assert.Equal(t.B.Id, hit.GetProperty("sender").GetProperty("id").GetGuid());
        Assert.Equal("Bé Ba", hit.GetProperty("sender").GetProperty("displayName").GetString());
        Assert.DoesNotContain(t.B.GomId, text);
    }

    // ---- all of your conversations

    private async Task<(string[] Bodies, Guid[] Chats)> FindAll(string cookie, string q)
    {
        using var r = await app.Call(Get, $"/api/search?q={Uri.EscapeDataString(q)}", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var results = (await AppFixture.Json(r)).GetProperty("results").EnumerateArray().ToArray();
        return ([.. results.Select(m => m.GetProperty("body").GetString()!)], [.. results.Select(m => m.GetProperty("conversationId").GetGuid())]);
    }

    [Fact]
    public async Task The_list_search_looks_in_every_conversation_you_are_in_newest_first_and_only_those()
    {
        var t = await NewTeam();
        var direct = await app.OpenChat(t.CA, t.E.GomId);
        await Say(t, t.CB, "phở nhóm");
        app.Clock.Advance(TimeSpan.FromMinutes(1));   // the newer one is in the conversation made first
        (await app.Call(Post, $"/api/conversations/{direct}/messages", new { clientId = Guid.NewGuid(), body = "phở riêng" }, t.CE)).Dispose();
        var (bodies, chats) = await FindAll(t.CA, "pho");
        Assert.Equal(new[] { "phở riêng", "phở nhóm" }, bodies);
        Assert.Equal(new[] { direct, t.Chat }, chats);
        Assert.Equal(new[] { "phở nhóm" }, (await FindAll(t.CB, "pho")).Bodies);   // B is not in A and E's conversation
        var (_, stranger) = await app.Member();
        Assert.Empty((await FindAll(stranger, "pho")).Bodies);
    }

    [Fact]
    public async Task The_list_search_leaves_out_the_same_things_as_one_conversation_does()
    {
        var t = await NewTeam();
        await Say(t, t.CA, "chuyện cũ về phở");
        (await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = t.E.GomId }, t.CA)).Dispose();
        var gone = await Say(t, t.CB, "phở thu hồi");
        (await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{gone}", cookie: t.CB)).Dispose();
        var hidden = await Say(t, t.CB, "phở đã xóa ở phía mình");
        (await app.Call(Put, $"/api/conversations/{t.Chat}/messages/{hidden}/hidden", cookie: t.CE)).Dispose();
        await Say(t, t.CB, "phở mới");
        Assert.Equal(new[] { "phở mới" }, (await FindAll(t.CE, "phở")).Bodies);   // not before E joined, not taken back, not hidden by E
        Assert.Equal(new[] { "phở mới", "phở đã xóa ở phía mình", "chuyện cũ về phở" }, (await FindAll(t.CA, "phở")).Bodies);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_list_search_refuses_an_empty_search(string q)
    {
        var (_, cookie) = await app.Member();
        using var r = await app.Call(Get, $"/api/search?q={Uri.EscapeDataString(q)}", cookie: cookie);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_query", (await AppFixture.Json(r)).GetProperty("error").GetString());
        using var signedOut = await app.Call(Get, "/api/search?q=pho");
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }
}
