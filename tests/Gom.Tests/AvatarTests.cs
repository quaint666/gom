using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Gom.Web.Uploads;
using SkiaSharp;

namespace Gom.Tests;

/// <summary>Profile pictures: a square from the middle of a photo, seen only by those who may see who you are.</summary>
public class AvatarTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Put = HttpMethod.Put, Post = HttpMethod.Post, Delete = HttpMethod.Delete;

    private static byte[] Photo(int width = 600, int height = 400) => TestImages.Encode(TestImages.Busy(width, height), SKEncodedImageFormat.Jpeg);

    private async Task<Guid> SetAvatar(string cookie, byte[] bytes)
    {
        using var r = await app.CallBytes(Put, "/api/me/avatar", bytes, cookie, "image/jpeg");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("avatar").GetGuid();
    }

    private async Task<HttpResponseMessage> Fetch(string cookie, Guid avatar) => await app.Call(Get, $"/api/avatars/{avatar}", cookie: cookie);

    [Fact]
    public async Task A_photo_becomes_a_256_pixel_square_webp_that_can_be_kept_for_good_and_me_says_which()
    {
        var (_, ca) = await app.Member();
        var avatar = await SetAvatar(ca, Photo(600, 400));

        using var r = await Fetch(ca, avatar);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/webp", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", r.Headers.CacheControl?.ToString());
        Assert.True(r.Headers.CacheControl?.Private);
        using var picture = SKBitmap.Decode(await r.Content.ReadAsByteArrayAsync());
        Assert.Equal((256, 256), (picture.Width, picture.Height));

        using var me = await app.Call(Get, "/api/me", cookie: ca);
        Assert.Equal(avatar, (await AppFixture.Json(me)).GetProperty("avatar").GetGuid());
    }

    [Fact]
    public async Task A_small_photo_is_not_made_bigger()
    {
        var (_, ca) = await app.Member();
        using var r = await Fetch(ca, await SetAvatar(ca, Photo(120, 90)));
        using var picture = SKBitmap.Decode(await r.Content.ReadAsByteArrayAsync());
        Assert.Equal((90, 90), (picture.Width, picture.Height));
    }

    [Fact]
    public async Task A_new_picture_replaces_the_old_one_whose_address_and_file_then_go()
    {
        var (_, ca) = await app.Member();
        var first = await SetAvatar(ca, Photo());
        var second = await SetAvatar(ca, Photo(500, 500));
        Assert.NotEqual(first, second);
        using (var old = await Fetch(ca, first)) Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        using (var now = await Fetch(ca, second)) Assert.Equal(HttpStatusCode.OK, now.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(app.UploadsDir, first.ToString(), SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Removing_it_goes_back_to_the_initials()
    {
        var (_, ca) = await app.Member();
        var avatar = await SetAvatar(ca, Photo());
        using (var off = await app.Call(Delete, "/api/me/avatar", cookie: ca)) Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        using (var me = await app.Call(Get, "/api/me", cookie: ca)) Assert.Equal(JsonValueKind.Null, (await AppFixture.Json(me)).GetProperty("avatar").ValueKind);
        using (var gone = await Fetch(ca, avatar)) Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(app.UploadsDir, avatar.ToString(), SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Friends_and_people_in_a_shared_group_see_it_and_a_stranger_does_not()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member(); var (_, cs) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(b, c);
        using (var g = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { c.GomId } }, cb)) Assert.Equal(HttpStatusCode.OK, g.StatusCode);
        var avatar = await SetAvatar(cb, Photo());

        using (var friend = await Fetch(ca, avatar)) Assert.Equal(HttpStatusCode.OK, friend.StatusCode);
        using (var grouped = await Fetch(cc, avatar)) Assert.Equal(HttpStatusCode.OK, grouped.StatusCode);
        using (var stranger = await Fetch(cs, avatar)) Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        using (var unknown = await Fetch(cs, Guid.NewGuid())) Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var signedOut = await app.Call(Get, $"/api/avatars/{avatar}")) Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Fact]
    public async Task The_lists_that_show_people_say_which_picture()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var avatar = await SetAvatar(cb, Photo());
        var chat = await app.OpenChat(ca, b.GomId);
        using (var g = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId } }, ca))
        {
            var group = (await AppFixture.Json(g)).GetProperty("id").GetGuid();
            using var members = await app.Call(Get, $"/api/conversations/{group}/members", cookie: ca);
            Assert.Contains((await AppFixture.Json(members)).EnumerateArray(), m => m.GetProperty("id").GetGuid() == b.Id && m.GetProperty("avatar").GetGuid() == avatar);
        }
        using (var list = await app.Call(Get, "/api/conversations", cookie: ca))
            Assert.Contains((await AppFixture.Json(list)).EnumerateArray(), x => x.GetProperty("id").GetGuid() == chat && x.GetProperty("peer").GetProperty("avatar").GetGuid() == avatar);
        using (var friends = await app.Call(Get, "/api/friends", cookie: ca))
            Assert.Equal(avatar, (await AppFixture.Json(friends)).GetProperty("friends")[0].GetProperty("avatar").GetGuid());
        using (var card = await app.Call(Get, $"/api/people/{b.Id}", cookie: ca))
            Assert.Equal(avatar, (await AppFixture.Json(card)).GetProperty("avatar").GetGuid());
    }

    [Fact]
    public async Task What_is_not_a_picture_or_is_too_big_is_refused_and_nothing_changes()
    {
        var (_, ca) = await app.Member();
        using (var text = await app.CallBytes(Put, "/api/me/avatar", "xin chào"u8.ToArray(), ca)) Assert.Equal(HttpStatusCode.UnprocessableEntity, text.StatusCode);
        using (var empty = await app.CallBytes(Put, "/api/me/avatar", [], ca)) Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using (var big = await app.CallBytes(Put, "/api/me/avatar", new byte[300_000], ca)) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        using var me = await app.Call(Get, "/api/me", cookie: ca);
        Assert.Equal(JsonValueKind.Null, (await AppFixture.Json(me)).GetProperty("avatar").ValueKind);
    }

    [Fact]
    public void The_square_is_taken_from_the_middle()
    {
        // Red sides, a green middle third: the middle square is all green.
        using var wide = new SKBitmap(300, 100);
        using (var canvas = new SKCanvas(wide)) { canvas.Clear(SKColors.Red); canvas.DrawRect(100, 0, 100, 100, new SKPaint { Color = SKColors.Lime }); }
        var bytes = TestImages.Encode(wide, SKEncodedImageFormat.Png);
        var plan = ImageProcessor.Plan(bytes, new UploadOptions()).Plan!;
        var made = ImageProcessor.Avatar(bytes, plan, 256, 90);
        using var square = SKBitmap.Decode(made.Bytes!);
        Assert.Equal((100, 100), (square.Width, square.Height));
        foreach (var (x, y) in new[] { (2, 2), (97, 2), (50, 50), (2, 97), (97, 97) })
        {
            var p = square.GetPixel(x, y);
            Assert.True(p.Green > 200 && p.Red < 60, $"({x},{y}) is {p}");
        }
    }
}
