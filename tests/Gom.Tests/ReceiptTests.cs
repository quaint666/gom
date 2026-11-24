using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class ReceiptTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get, Patch = HttpMethod.Patch;

    private sealed record Pair(User A, string CA, User B, string CB, Guid Chat);

    private async Task<Pair> NewPair()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return new Pair(a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private Task<HttpResponseMessage> Send(string cookie, Guid chat, string body) =>
        app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);

    private async Task Read(string cookie, Guid chat, long seq)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/read", new { seq }, cookie);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    private async Task SetReceipts(string cookie, bool enabled)
    {
        using var r = await app.Call(Patch, "/api/me/receipts", new { enabled }, cookie);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    private async Task<(bool Enabled, Dictionary<Guid, long> Reads)> Reads(string cookie, Guid chat)
    {
        using var r = await app.Call(Get, $"/api/conversations/{chat}/reads", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await AppFixture.Json(r);
        return (json.GetProperty("enabled").GetBoolean(),
            json.GetProperty("reads").EnumerateArray().ToDictionary(x => x.GetProperty("userId").GetGuid(), x => x.GetProperty("seq").GetInt64()));
    }

    // ---- the switch

    [Fact]
    public async Task Receipts_are_on_by_default_and_the_switch_is_remembered()
    {
        var (u, cookie) = await app.Member();
        using var before = await app.Call(Get, "/api/me", cookie: cookie);
        Assert.True((await AppFixture.Json(before)).GetProperty("readReceipts").GetBoolean());

        await SetReceipts(cookie, false);
        using var off = await app.Call(Get, "/api/me", cookie: cookie);
        await SetReceipts(cookie, true);
        using var on = await app.Call(Get, "/api/me", cookie: cookie);

        Assert.False((await AppFixture.Json(off)).GetProperty("readReceipts").GetBoolean());
        Assert.True((await AppFixture.Json(on)).GetProperty("readReceipts").GetBoolean());
    }

    [Fact]
    public async Task The_switch_needs_a_session_a_good_Origin_and_a_value()
    {
        var (_, cookie) = await app.Member();
        using var anonymous = await app.Call(Patch, "/api/me/receipts", new { enabled = false });
        using var foreign = await app.Call(Patch, "/api/me/receipts", new { enabled = false }, cookie, origin: "https://evil.test");
        using var empty = await app.Call(Patch, "/api/me/receipts", new { }, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    // ---- who sees what

    [Fact]
    public async Task Two_people_who_share_see_how_far_each_has_read()
    {
        var p = await NewPair();
        using var m1 = await Send(p.CA, p.Chat, "một");
        using var m2 = await Send(p.CA, p.Chat, "hai");
        await Read(p.CB, p.Chat, 1);

        var seenByA = await Reads(p.CA, p.Chat);
        var seenByB = await Reads(p.CB, p.Chat);

        Assert.True(seenByA.Enabled);
        Assert.Equal(new Dictionary<Guid, long> { [p.B.Id] = 1 }, seenByA.Reads);
        Assert.Equal(new Dictionary<Guid, long> { [p.A.Id] = 2 }, seenByB.Reads);   // A's own sends moved A's position
    }

    [Fact]
    public async Task Hiding_yours_hides_theirs_from_you_and_yours_from_them()
    {
        var p = await NewPair();
        using var m1 = await Send(p.CA, p.Chat, "một");
        await Read(p.CB, p.Chat, 1);
        await SetReceipts(p.CA, false);

        var seenByA = await Reads(p.CA, p.Chat);
        var seenByB = await Reads(p.CB, p.Chat);

        Assert.False(seenByA.Enabled);
        Assert.Empty(seenByA.Reads);   // A may not see B's, though B shares
        Assert.True(seenByB.Enabled);
        Assert.Empty(seenByB.Reads);   // B may not see A's
    }

    [Fact]
    public async Task Switching_back_on_shows_the_position_as_it_is_now()
    {
        var p = await NewPair();
        using var m1 = await Send(p.CA, p.Chat, "một");
        await SetReceipts(p.CB, false);
        await Read(p.CB, p.Chat, 1);          // read while hidden: still recorded, just not shown
        await SetReceipts(p.CB, true);

        Assert.Equal(1L, (await Reads(p.CA, p.Chat)).Reads[p.B.Id]);
    }

    [Fact]
    public async Task Only_members_can_ask()
    {
        var p = await NewPair();
        var (_, outsider) = await app.Member();
        using var byOutsider = await app.Call(Get, $"/api/conversations/{p.Chat}/reads", cookie: outsider);
        using var anonymous = await app.Call(Get, $"/api/conversations/{p.Chat}/reads");
        Assert.Equal(HttpStatusCode.NotFound, byOutsider.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task In_a_group_you_see_every_other_member_who_shares_and_nobody_who_does_not()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member(); var (d, cd) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c); await app.Befriend(a, d);
        using var made = await app.Call(Post, "/api/conversations/group", new { title = "Nhóm", members = new[] { b.GomId, c.GomId, d.GomId } }, ca);
        var chat = (await AppFixture.Json(made)).GetProperty("id").GetGuid();
        using var m1 = await Send(ca, chat, "một");
        await Read(cb, chat, 1); await Read(cc, chat, 1); await Read(cd, chat, 1);
        await SetReceipts(cc, false);

        var seen = await Reads(ca, chat);

        Assert.Equivalent(new Dictionary<Guid, long> { [b.Id] = 1, [d.Id] = 1 }, seen.Reads);   // not C (hidden), not A (me)
    }

    [Fact]
    public async Task Someone_who_left_a_group_is_not_listed()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        using var made = await app.Call(Post, "/api/conversations/group", new { title = "Nhóm", members = new[] { b.GomId } }, ca);
        var chat = (await AppFixture.Json(made)).GetProperty("id").GetGuid();
        using var leave = await app.Call(HttpMethod.Delete, $"/api/conversations/{chat}/members/me", cookie: cb);
        Assert.Empty((await Reads(ca, chat)).Reads);
    }
}
