using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Data;

namespace Gom.Tests;

/// <summary>Naming a group again: the leader and the deputies may, and everyone sees the new name.</summary>
public class GroupRenameTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Patch = HttpMethod.Patch, Put = HttpMethod.Put, Get = HttpMethod.Get, Post = HttpMethod.Post;

    private sealed record Team(User A, string CA, User B, string CB, User C, string CC, User E, string CE, Guid Chat);

    private async Task<Team> NewTeam()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member(); var (e, ce) = await app.Member();
        foreach (var x in new[] { b, c, e }) await app.Befriend(a, x);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Tên cũ", members = new[] { b.GomId, c.GomId } }, ca);
        return new Team(a, ca, b, cb, c, cc, e, ce, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private Task<HttpResponseMessage> Rename(Team t, string cookie, object? title) => app.Call(Patch, $"/api/conversations/{t.Chat}", new { title }, cookie);

    private async Task<string?> TitleSeenBy(Team t, string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == t.Chat).GetProperty("title").GetString();
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString();

    [Fact]
    public async Task The_leader_renames_and_every_member_sees_the_new_name()
    {
        var t = await NewTeam();
        using var r = await Rename(t, t.CA, "  Nhà mình  ");
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        foreach (var cookie in new[] { t.CA, t.CB, t.CC }) Assert.Equal("Nhà mình", await TitleSeenBy(t, cookie));
    }

    [Fact]
    public async Task A_deputy_renames_but_a_plain_member_does_not()
    {
        var t = await NewTeam();
        using var promote = await app.Call(Put, $"/api/conversations/{t.Chat}/members/{t.B.Id}/role", new { role = "deputy" }, t.CA);
        Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);

        using var byDeputy = await Rename(t, t.CB, "Do phó nhóm");
        using var byMember = await Rename(t, t.CC, "Do thành viên");

        Assert.Equal(HttpStatusCode.NoContent, byDeputy.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal("not_allowed", await ErrorOf(byMember));
        Assert.Equal("Do phó nhóm", await TitleSeenBy(t, t.CA));
    }

    [Fact]
    public async Task Someone_outside_cannot_rename_and_cannot_tell_the_group_exists()
    {
        var t = await NewTeam();
        using var r = await Rename(t, t.CE, "Lạ");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("Tên cũ", await TitleSeenBy(t, t.CA));
    }

    [Fact]
    public async Task A_one_to_one_chat_has_no_name_to_change()
    {
        var t = await NewTeam();
        var direct = await app.OpenChat(t.CA, t.E.GomId);
        using var r = await app.Call(Patch, $"/api/conversations/{direct}", new { title = "x" }, t.CA);
        Assert.Equal("not_a_group", await ErrorOf(r));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tên\u0007chuông")]
    public async Task A_name_must_be_there_and_must_be_clean(string? title)
    {
        var t = await NewTeam();
        using var r = await Rename(t, t.CA, title);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_title", await ErrorOf(r));
        Assert.Equal("Tên cũ", await TitleSeenBy(t, t.CA));
    }

    [Fact]
    public async Task A_name_over_sixty_characters_is_refused_and_exactly_sixty_is_fine()
    {
        var t = await NewTeam();
        using var tooLong = await Rename(t, t.CA, new string('a', 61));
        using var fine = await Rename(t, t.CA, new string('b', 60));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, fine.StatusCode);
    }

    [Fact]
    public async Task Everyone_in_the_group_is_told_to_reload_their_list_and_nobody_else()
    {
        var t = await NewTeam();
        using var wsB = await Connect(t.CB);
        using var wsE = await Connect(t.CE);
        (await Rename(t, t.CA, "Mới")).Dispose();
        Assert.Equal("conversations", (await Next(wsB)).GetProperty("t").GetString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Next(wsE, TimeSpan.FromMilliseconds(400)));
    }

    private async Task<WebSocket> Connect(string cookie)
    {
        var client = app.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => { r.Headers["Cookie"] = $"__Host-gom={cookie}"; r.Headers["Origin"] = AppFixture.Origin; };
        var ws = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        Assert.Equal("ready", (await Next(ws)).GetProperty("t").GetString());
        return ws;
    }

    private static async Task<JsonElement> Next(WebSocket ws, TimeSpan? within = null)
    {
        using var cts = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(5));
        var buffer = new byte[16384];
        var r = await ws.ReceiveAsync(buffer, cts.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)).RootElement.Clone();
    }
}
