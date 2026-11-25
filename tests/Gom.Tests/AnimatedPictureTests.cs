using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SkiaSharp;

namespace Gom.Tests;

/// <summary>Moving pictures (GIFs, WebPs with frames): kept as sent, checked frame by frame, with a still small version.</summary>
public class AnimatedPictureTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put;

    /// <summary>
    /// A GIF made by hand (nothing here writes GIFs): a screen of the given size, two colours, looping, and the given number of frames,
    /// each one pixel at the top left, black and white in turn. <paramref name="cut"/> drops that many bytes from the end.
    /// </summary>
    public static byte[] Gif(int width, int height, int frames, int cut = 0)
    {
        var b = new List<byte>();
        b.AddRange("GIF89a"u8.ToArray());
        b.AddRange([(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x80, 0, 0]);
        b.AddRange([0, 0, 0, 255, 255, 255]);                                            // the two colours
        b.AddRange([0x21, 0xFF, 0x0B]); b.AddRange("NETSCAPE2.0"u8.ToArray()); b.AddRange([3, 1, 0, 0, 0]);   // loop for ever
        for (var i = 0; i < frames; i++)
        {
            b.AddRange([0x21, 0xF9, 4, 0x04, 10, 0, 0, 0]);                                // 0.1 s, keep what was drawn
            b.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0]);                                 // one pixel at 0,0
            b.AddRange([2, 2, i % 2 == 0 ? (byte)0x44 : (byte)0x4C, 1, 0]);                // colour 0 or 1
        }
        b.Add(0x3B);
        return [.. b.Take(b.Count - cut)];
    }

    private async Task<(string Cookie, Guid Chat)> Chat()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        return (ca, await app.OpenChat(ca, b.GomId));
    }

    private Task<HttpResponseMessage> Upload(Guid chat, string cookie, byte[] body, string name = "meo.gif") =>
        app.CallBytes(Post, $"/api/conversations/{chat}/attachments?name={Uri.EscapeDataString(name)}", body, cookie);

    private static async Task<string> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString()!;

    [Fact]
    public async Task A_moving_gif_is_kept_exactly_served_as_a_gif_and_its_small_version_is_a_still_webp()
    {
        var (cookie, chat) = await Chat();
        var gif = Gif(4, 3, 3);
        using var created = await Upload(chat, cookie, gif);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await AppFixture.Json(created);
        Assert.Equal("image", json.GetProperty("kind").GetString());
        Assert.True(json.GetProperty("animated").GetBoolean());
        Assert.Equal("meo.gif", json.GetProperty("name").GetString());
        Assert.Equal(4, json.GetProperty("width").GetInt32());
        Assert.Equal(3, json.GetProperty("height").GetInt32());
        Assert.Equal(gif.Length, json.GetProperty("size").GetInt64());
        var id = json.GetProperty("id").GetGuid();

        using var full = await app.Call(Get, $"/api/attachments/{id}", cookie: cookie);
        Assert.Equal("image/gif", full.Content.Headers.ContentType!.MediaType);
        Assert.Equal(gif, await full.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", full.Headers.GetValues("X-Content-Type-Options").Single());

        using var thumb = await app.Call(Get, $"/api/attachments/{id}/thumb", cookie: cookie);
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType!.MediaType);
        using var still = SKCodec.Create(new MemoryStream(await thumb.Content.ReadAsByteArrayAsync()))!;
        Assert.Equal(SKEncodedImageFormat.Webp, still.EncodedFormat);
        Assert.True(still.FrameCount <= 1);

        // Passed on, it is still moving.
        using var copied = await app.Call(Post, $"/api/conversations/{chat}/attachments/copy", new { from = id }, cookie);
        Assert.Equal(HttpStatusCode.NotFound, copied.StatusCode);   // not yet in a message: nothing to pass on
        using (var sent = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "", attachments = new[] { id } }, cookie))
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        using var copy = await app.Call(Post, $"/api/conversations/{chat}/attachments/copy", new { from = id }, cookie);
        Assert.True((await AppFixture.Json(copy)).GetProperty("animated").GetBoolean());
    }

    [Fact]
    public async Task A_gif_of_one_frame_is_an_ordinary_picture_made_into_webp()
    {
        var (cookie, chat) = await Chat();
        using var created = await Upload(chat, cookie, Gif(4, 3, 1), "tinh.gif");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await AppFixture.Json(created);
        Assert.False(json.GetProperty("animated").GetBoolean());
        Assert.Equal("tinh.webp", json.GetProperty("name").GetString());
        using var full = await app.Call(Get, $"/api/attachments/{json.GetProperty("id").GetGuid()}", cookie: cookie);
        Assert.Equal("image/webp", full.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Too_many_frames_or_pixels_or_a_gif_cut_short_are_each_refused_in_words()
    {
        var (cookie, chat) = await Chat();
        using (var frames = await Upload(chat, cookie, Gif(4, 3, 1001))) Assert.Equal("image_dimensions", await ErrorOf(frames));
        using (var huge = await Upload(chat, cookie, Gif(10_000, 10_000, 2))) Assert.Equal("image_dimensions", await ErrorOf(huge));
        using (var manyBig = await Upload(chat, cookie, Gif(5_000, 5_000, 7))) Assert.Equal("image_dimensions", await ErrorOf(manyBig));   // 25 MP each, 175 MP in all
        using var cut = await Upload(chat, cookie, Gif(4, 3, 3, cut: 4));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cut.StatusCode);
        Assert.Equal("invalid_image", await ErrorOf(cut));
    }

    [Fact]
    public async Task A_moving_picture_has_its_own_size_limit()
    {
        var (cookie, chat) = await Chat();
        using var small = app.Factory.WithWebHostBuilder(b => b.UseSetting("Uploads:MaxAnimatedBytes", "100"));
        using var client = small.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(AppFixture.Origin) });
        var request = new HttpRequestMessage(Post, $"/api/conversations/{chat}/attachments?name=a.gif") { Content = new ByteArrayContent(Gif(4, 3, 10)) };
        request.Headers.Add("Cookie", $"{Gom.Web.Auth.SessionCookie.Name}={cookie}");
        request.Headers.Add("Origin", AppFixture.Origin);
        using var r = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal("animated_too_large", await ErrorOf(r));
    }

    [Fact]
    public async Task A_gif_is_not_taken_as_a_profile_picture()
    {
        var (_, cookie) = await app.Member();
        using var moving = await app.CallBytes(Put, "/api/me/avatar", Gif(4, 3, 3), cookie, "image/gif");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, moving.StatusCode);
        using var still = await app.CallBytes(Put, "/api/me/avatar", Gif(4, 3, 1), cookie, "image/gif");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, still.StatusCode);
    }
}
