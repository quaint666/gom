using System.Net;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Gom.Tests;

/// <summary>A group's picture: set or taken away by its leader or a deputy, seen by its members only, with a line saying who changed it.</summary>
public class GroupPictureTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Put = HttpMethod.Put, Post = HttpMethod.Post, Delete = HttpMethod.Delete;
    private static byte[] Photo() => TestImages.Encode(TestImages.Busy(600, 400), SKEncodedImageFormat.Jpeg);

    /// <summary>A leader, a plain member, and someone outside, with the group between the first two.</summary>
    private async Task<(Guid Group, User Leader, string LeaderCookie, string MemberCookie, string OutsiderCookie)> Group()
    {
        var (leader, lc) = await app.Member();
        var (member, mc) = await app.Member();
        var (_, oc) = await app.Member();
        await app.Befriend(leader, member);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { member.GomId } }, lc);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return ((await AppFixture.Json(r)).GetProperty("id").GetGuid(), leader, lc, mc, oc);
    }

    private async Task<HttpResponseMessage> Set(Guid group, string cookie) => await app.CallBytes(Put, $"/api/conversations/{group}/avatar", Photo(), cookie, "image/jpeg");

    private async Task<string?> Listed(Guid group, string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        var row = (await AppFixture.Json(r)).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == group);
        return row.GetProperty("avatar").ValueKind == System.Text.Json.JsonValueKind.Null ? null : row.GetProperty("avatar").GetString();
    }

    [Fact]
    public async Task The_leader_sets_it_its_members_see_it_and_a_line_says_who()
    {
        var (group, leader, lc, mc, oc) = await Group();
        using var set = await Set(group, lc);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var avatar = (await AppFixture.Json(set)).GetProperty("avatar").GetGuid();

        Assert.Equal(avatar.ToString(), await Listed(group, mc));
        using (var seen = await app.Call(Get, $"/api/avatars/{avatar}", cookie: mc))
        {
            Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
            using var picture = SKBitmap.Decode(await seen.Content.ReadAsByteArrayAsync());
            Assert.Equal((256, 256), (picture.Width, picture.Height));
        }
        using (var outside = await app.Call(Get, $"/api/avatars/{avatar}", cookie: oc))
            Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);

        await using var db = app.Db.NewContext();
        Assert.True(await db.Messages.AnyAsync(m => m.ConversationId == group && m.Notice == NoticeKind.Pictured && m.SenderId == leader.Id));
    }

    [Fact]
    public async Task A_plain_member_may_not_and_someone_outside_finds_no_group()
    {
        var (group, _, _, mc, oc) = await Group();
        using var member = await Set(group, mc);
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
        using var outside = await Set(group, oc);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        using var remove = await app.Call(Delete, $"/api/conversations/{group}/avatar", cookie: mc);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
    }

    [Fact]
    public async Task Taken_away_the_group_shows_its_initials_and_the_old_picture_is_gone()
    {
        var (group, _, lc, mc, _) = await Group();
        using var set = await Set(group, lc);
        var avatar = (await AppFixture.Json(set)).GetProperty("avatar").GetGuid();
        using (var r = await app.Call(Delete, $"/api/conversations/{group}/avatar", cookie: lc)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Null(await Listed(group, mc));
        using var old = await app.Call(Get, $"/api/avatars/{avatar}", cookie: mc);
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }

    [Fact]
    public async Task A_new_picture_replaces_the_old_one()
    {
        var (group, _, lc, mc, _) = await Group();
        using var first = await Set(group, lc);
        var old = (await AppFixture.Json(first)).GetProperty("avatar").GetGuid();
        using var second = await Set(group, lc);
        var fresh = (await AppFixture.Json(second)).GetProperty("avatar").GetGuid();
        Assert.NotEqual(old, fresh);
        using var gone = await app.Call(Get, $"/api/avatars/{old}", cookie: mc);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }
}
