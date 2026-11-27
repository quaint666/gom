using System.Net;
using System.Text.Json;
using Gom.Web.Chat;
using SkiaSharp;

namespace Gom.Tests;

/// <summary>Sticker packs the owner makes from their own pictures, in everyone's picker after Gom's own.</summary>
public class StickerPackTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Delete = HttpMethod.Delete;

    [Fact]
    public void A_code_is_c_and_the_id_and_nothing_else_reads_as_one()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("c-0f8fad5bd9cb469fa16570867728950e", StickerPacks.CodeOf(id));
        Assert.Equal(id, StickerPacks.IdOf(StickerPacks.CodeOf(id)));
        Assert.Null(StickerPacks.IdOf("1f600"));
        Assert.Null(StickerPacks.IdOf("c-nope"));
        Assert.Null(StickerPacks.IdOf("c-0f8fad5b-d9cb-469f-a165-70867728950e"));   // only the short form
        Assert.Null(StickerPacks.IdOf(null));
        Assert.True(StickerPacks.CodeOf(id).Length <= Stickers.MaxCodeLength);
    }

    private async Task<Guid> NewPack(string name = "Nhà mình")
    {
        var (_, owner) = await app.Owner();
        using var r = await app.Call(Post, "/api/owner/sticker-packs", new { name }, owner);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> Add(Guid pack, byte[] picture)
    {
        var (_, owner) = await app.Owner();
        return await app.CallBytes(Post, $"/api/owner/sticker-packs/{pack}/stickers", picture, owner);
    }

    private async Task<(Guid Id, string Code)> Added(Guid pack, byte[] picture)
    {
        using var r = await Add(pack, picture);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await AppFixture.Json(r);
        return (json.GetProperty("id").GetGuid(), json.GetProperty("code").GetString()!);
    }

    private async Task<List<JsonElement>> Listed(string cookie)
    {
        using var r = await app.Call(Get, "/api/sticker-packs", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return [.. (await AppFixture.Json(r)).EnumerateArray().Select(p => p.Clone())];
    }

    private async Task<HttpResponseMessage> SendSticker(string cookie, Guid chat, string code) =>
        await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "", sticker = code }, cookie);

    [Fact]
    public async Task The_owner_makes_a_pack_a_still_picture_becomes_a_small_WebP_with_its_transparency_and_members_see_and_send_it()
    {
        var pack = await NewPack("Mèo nhà");
        var (id, code) = await Added(pack, TestImages.Png(800, 400, transparentCorner: true));
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);

        var listed = (await Listed(ca)).Single(p => p.GetProperty("id").GetGuid() == pack);
        Assert.Equal("Mèo nhà", listed.GetProperty("name").GetString());
        Assert.Equal(code, Assert.Single(listed.GetProperty("stickers").EnumerateArray()).GetProperty("code").GetString());

        using var picture = await app.Call(Get, $"/api/stickers/{id}", cookie: ca);
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/webp", picture.Content.Headers.ContentType?.MediaType);
        using var bitmap = SKBitmap.Decode(await picture.Content.ReadAsByteArrayAsync());
        Assert.Equal((240, 120), (bitmap.Width, bitmap.Height));   // the longer side at 240, the shape kept
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);                // the see-through corner stays see-through

        var chat = await app.OpenChat(ca, b.GomId);
        using var sent = await SendSticker(ca, chat, code);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(code, (await AppFixture.Json(sent)).GetProperty("sticker").GetString());
    }

    [Fact]
    public async Task A_moving_GIF_is_kept_as_sent_and_a_broken_one_or_something_else_is_turned_away()
    {
        var pack = await NewPack();
        var gif = AnimatedPictureTests.Gif(64, 64, 3);
        var (id, _) = await Added(pack, gif);
        var (_, ca) = await app.Member();
        using var picture = await app.Call(Get, $"/api/stickers/{id}", cookie: ca);
        Assert.Equal("image/gif", picture.Content.Headers.ContentType?.MediaType);
        Assert.Equal(gif, await picture.Content.ReadAsByteArrayAsync());

        using (var broken = await Add(pack, AnimatedPictureTests.Gif(4, 3, 3, cut: 4)))
            Assert.Equal("invalid_image", (await AppFixture.Json(broken)).GetProperty("error").GetString());
        using (var text = await Add(pack, "xin chào"u8.ToArray()))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, text.StatusCode);
        using (var big = await Add(pack, new byte[StickerPacks.MaxBytes + 1]))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
    }

    [Fact]
    public async Task Taken_out_of_the_picker_it_cannot_be_sent_any_more_but_one_already_sent_still_draws()
    {
        var pack = await NewPack();
        var (keptId, keptCode) = await Added(pack, TestImages.Png(100, 100));
        var (goneId, goneCode) = await Added(pack, TestImages.Png(100, 100));
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        using (var before = await SendSticker(ca, chat, goneCode)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var (_, owner) = await app.Owner();
        using (var r = await app.Call(Delete, $"/api/owner/stickers/{goneId}", cookie: owner)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var stickers = (await Listed(ca)).Single(p => p.GetProperty("id").GetGuid() == pack).GetProperty("stickers").EnumerateArray().Select(s => s.GetProperty("id").GetGuid());
        Assert.Equal([keptId], stickers);
        using (var after = await SendSticker(ca, chat, goneCode)) Assert.Equal("invalid_sticker", (await AppFixture.Json(after)).GetProperty("error").GetString());
        using (var still = await app.Call(Get, $"/api/stickers/{goneId}", cookie: ca)) Assert.Equal(HttpStatusCode.OK, still.StatusCode);

        using (var r = await app.Call(Delete, $"/api/owner/sticker-packs/{pack}", cookie: owner)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.DoesNotContain(await Listed(ca), p => p.GetProperty("id").GetGuid() == pack);
        using (var packGone = await SendSticker(ca, chat, keptCode)) Assert.Equal("invalid_sticker", (await AppFixture.Json(packGone)).GetProperty("error").GetString());
        using (var notThere = await SendSticker(ca, chat, StickerPacks.CodeOf(Guid.NewGuid()))) Assert.Equal("invalid_sticker", (await AppFixture.Json(notThere)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_pack_needs_a_name_and_holds_at_most_100()
    {
        var (_, owner) = await app.Owner();
        foreach (var name in new[] { "", "   ", new string('a', StickerPacks.NameChars + 1) })
        {
            using var r = await app.Call(Post, "/api/owner/sticker-packs", new { name }, owner);
            Assert.Equal("invalid_title", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        var pack = await NewPack();
        var small = TestImages.PlainPng(8, 8);
        for (var i = 0; i < StickerPacks.PerPack; i++) { using var r = await Add(pack, small); Assert.Equal(HttpStatusCode.OK, r.StatusCode); }
        using var full = await Add(pack, small);
        Assert.Equal("pack_full", (await AppFixture.Json(full)).GetProperty("error").GetString());
        Assert.Equal(100, StickerPacks.PerPack);
        Assert.Contains("\"stickerPackChars\":40", Gom.Web.Pages.Limits(new(), new(), new(), new()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_owner_makes_or_changes_packs_and_only_members_see_them()
    {
        var (_, member) = await app.Member();
        var pack = await NewPack();
        using (var r = await app.Call(Post, "/api/owner/sticker-packs", new { name = "x" }, member)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await app.CallBytes(Post, $"/api/owner/sticker-packs/{pack}/stickers", TestImages.Png(10, 10), member)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await app.Call(Delete, $"/api/owner/sticker-packs/{pack}", cookie: member)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await app.Call(Get, "/api/sticker-packs")) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}
