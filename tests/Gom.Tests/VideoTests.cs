using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Gom.Web.Data;
using Gom.Web.Push;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Gom.Tests;

/// <summary>Videos: an MP4 is kept as sent, written to disk as it arrives, and read for its size and length; its still picture comes from the sender.</summary>
public class VideoTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put;

    // ---- an MP4 made by hand (nothing here writes video): the boxes this server reads, with filler where the pictures would be

    private static byte[] Box(string type, params byte[][] body)
    {
        var length = 8 + body.Sum(b => b.Length);
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        var at = 8;
        foreach (var part in body) { part.CopyTo(bytes, at); at += part.Length; }
        return bytes;
    }

    private static byte[] U32(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    private static byte[] Track(string handler, int width, int height, bool upright)
    {
        // version 0: flags, two times, the track's number, a reserved word, its duration; then 8 reserved bytes, layer, group, volume, reserved
        var tkhd = Box("tkhd", U32(0, 0, 0, 1, 0, 1000), new byte[16],
            upright ? U32(0, 0x10000, 0, 0xFFFF0000, 0, 0, 0, 0, 0x40000000) : U32(0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000),
            U32((uint)width << 16, (uint)height << 16));
        var hdlr = Box("hdlr", U32(0, 0), Encoding.ASCII.GetBytes(handler), new byte[12], [0]);
        return Box("trak", tkhd, Box("mdia", hdlr));
    }

    /// <summary>An MP4 of the given size and length. <paramref name="moovLast"/>: the outline after the pictures, as a phone writes it.</summary>
    public static byte[] Mp4(int width = 1280, int height = 720, uint durationMs = 4200, bool upright = false, bool sound = false, bool pictures = true,
        bool moovLast = false, int filler = 2000, int emptyBoxes = 0)
    {
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(0x200), Encoding.ASCII.GetBytes("isomavc1mp41"));
        var mvhd = Box("mvhd", U32(0, 0, 0, 1000, durationMs), new byte[80]);
        var tracks = new List<byte[]> { mvhd };
        if (sound) tracks.Add(Track("soun", 0, 0, false));
        if (pictures) tracks.Add(Track("vide", width, height, upright));
        tracks.AddRange(Enumerable.Range(0, emptyBoxes).Select(_ => Box("free")));
        var moov = Box("moov", [.. tracks]);
        var mdat = Box("mdat", Enumerable.Range(0, filler).Select(i => (byte)i).ToArray());
        return moovLast ? [.. ftyp, .. mdat, .. moov] : [.. ftyp, .. moov, .. mdat];
    }

    private static byte[] Jpeg(int w, int h)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(new SKColor(40, 120, 90));
        return bitmap.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    // ----

    private async Task<(string Cookie, Guid Chat, string Other)> Chat()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return (ca, await app.OpenChat(ca, b.GomId), cb);
    }

    private Task<HttpResponseMessage> Upload(Guid chat, string cookie, byte[] body, string name = "clip.mp4") =>
        app.CallBytes(Post, $"/api/conversations/{chat}/attachments?name={Uri.EscapeDataString(name)}", body, cookie);

    private async Task<System.Text.Json.JsonElement> Uploaded(Guid chat, string cookie, byte[] body, string name = "clip.mp4")
    {
        using var r = await Upload(chat, cookie, body, name);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await AppFixture.Json(r)).Clone();
    }

    private async Task Send(Guid chat, string cookie, params Guid[] files)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "", attachments = files }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString()!;

    [Fact]
    public async Task An_mp4_is_kept_as_sent_with_its_size_and_length_and_plays_a_piece_at_a_time()
    {
        var (cookie, chat, _) = await Chat();
        var mp4 = Mp4(1280, 720, 4200);
        var json = await Uploaded(chat, cookie, mp4);
        Assert.Equal("video", json.GetProperty("kind").GetString());
        Assert.Equal("clip.mp4", json.GetProperty("name").GetString());
        Assert.Equal(1280, json.GetProperty("width").GetInt32());
        Assert.Equal(720, json.GetProperty("height").GetInt32());
        Assert.Equal(4200, json.GetProperty("durationMs").GetInt32());
        Assert.Equal(mp4.Length, json.GetProperty("size").GetInt64());
        var id = json.GetProperty("id").GetGuid();

        using var full = await app.Call(Get, $"/api/attachments/{id}", cookie: cookie);
        Assert.Equal("video/mp4", full.Content.Headers.ContentType!.MediaType);
        Assert.Null(full.Content.Headers.ContentDisposition);   // played in the page, not downloaded
        Assert.True(full.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromDays(1), full.Headers.CacheControl.MaxAge);
        Assert.Equal(mp4, await full.Content.ReadAsByteArrayAsync());

        var piece = new HttpRequestMessage(Get, $"/api/attachments/{id}");
        piece.Headers.Range = new RangeHeaderValue(100, 199);
        piece.Headers.Add("Cookie", $"{Gom.Web.Auth.SessionCookie.Name}={cookie}");
        using var client = app.Client();
        using var part = await client.SendAsync(piece);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(mp4[100..200], await part.Content.ReadAsByteArrayAsync());

        using var noStill = await app.Call(Get, $"/api/attachments/{id}/thumb", cookie: cookie);
        Assert.Equal(HttpStatusCode.NotFound, noStill.StatusCode);   // no still picture was sent
    }

    [Fact]
    public async Task A_phone_held_upright_is_shown_upright_and_an_outline_after_the_pictures_is_found()
    {
        var (cookie, chat, _) = await Chat();
        var json = await Uploaded(chat, cookie, Mp4(1920, 1080, 61_500, upright: true, sound: true, moovLast: true));
        Assert.Equal("video", json.GetProperty("kind").GetString());
        Assert.Equal(1080, json.GetProperty("width").GetInt32());
        Assert.Equal(1920, json.GetProperty("height").GetInt32());
        Assert.Equal(61_500, json.GetProperty("durationMs").GetInt32());
    }

    [Theory]
    [InlineData("sound only")]
    [InlineData("no length")]
    [InlineData("cut short")]
    [InlineData("no tracks")]
    [InlineData("sound, no length")]
    public async Task An_mp4_that_is_not_a_whole_video_is_an_ordinary_file(string what)
    {
        var (cookie, chat, _) = await Chat();
        var bytes = what switch
        {
            "sound only" => Mp4(sound: true, pictures: false),
            "no length" => Mp4(durationMs: 0),
            "no tracks" => Mp4(pictures: false),
            "sound, no length" => Mp4(sound: true, pictures: false, durationMs: 0),
            _ => Mp4(moovLast: true)[..^40],
        };
        var json = await Uploaded(chat, cookie, bytes, "a.m4a");
        Assert.Equal("file", json.GetProperty("kind").GetString());
        Assert.Equal(what == "sound only", json.GetProperty("audio").GetBoolean());   // only sound alone may play in the page
        using var full = await app.Call(Get, $"/api/attachments/{json.GetProperty("id").GetGuid()}", cookie: cookie);
        Assert.Equal("application/octet-stream", full.Content.Headers.ContentType!.MediaType);
        Assert.Equal("a.m4a", full.Content.Headers.ContentDisposition!.FileNameStar ?? full.Content.Headers.ContentDisposition.FileName);
    }

    [Fact]
    public async Task A_video_over_its_limit_is_refused_in_words_and_nothing_of_it_is_kept()
    {
        var (cookie, chat, _) = await Chat();
        using var small = app.Factory.WithWebHostBuilder(b => b.UseSetting("Uploads:MaxVideoBytes", "3000"));
        using var client = small.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(AppFixture.Origin) });
        async Task<HttpResponseMessage> Send(byte[] body)
        {
            var request = new HttpRequestMessage(Post, $"/api/conversations/{chat}/attachments?name=a.mp4") { Content = new ByteArrayContent(body) };
            request.Headers.Add("Cookie", $"{Gom.Web.Auth.SessionCookie.Name}={cookie}");
            request.Headers.Add("Origin", AppFixture.Origin);
            return await client.SendAsync(request);
        }
        var files = Directory.EnumerateFiles(app.UploadsDir, "*", SearchOption.AllDirectories).Count();
        using (var r = await Send(Mp4(filler: 4000)))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
            Assert.Equal("video_too_large", await ErrorOf(r));
        }
        using (var fits = await Send(Mp4(filler: 1000))) Assert.Equal(HttpStatusCode.Created, fits.StatusCode);
        Assert.Equal(files + 1, Directory.EnumerateFiles(app.UploadsDir, "*", SearchOption.AllDirectories).Count());   // only the one that fit
    }

    [Fact]
    public async Task Videos_count_towards_the_day_s_limit()
    {
        var (cookie, chat, _) = await Chat();
        using var small = app.Factory.WithWebHostBuilder(b => b.UseSetting("Uploads:DailyBytes", "5000"));
        using var client = small.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(AppFixture.Origin) });
        async Task<HttpResponseMessage> Send(byte[] body)
        {
            var request = new HttpRequestMessage(Post, $"/api/conversations/{chat}/attachments?name=a.mp4") { Content = new ByteArrayContent(body) };
            request.Headers.Add("Cookie", $"{Gom.Web.Auth.SessionCookie.Name}={cookie}");
            request.Headers.Add("Origin", AppFixture.Origin);
            return await client.SendAsync(request);
        }
        using (var first = await Send(Mp4(filler: 3000))) Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var second = await Send(Mp4(filler: 3000));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("daily_upload_limit", await ErrorOf(second));
    }

    [Fact]
    public async Task Its_still_picture_comes_from_its_sender_before_it_is_sent_and_is_made_a_small_webp()
    {
        var (cookie, chat, other) = await Chat();
        var id = (await Uploaded(chat, cookie, Mp4())).GetProperty("id").GetGuid();

        using (var theirs = await app.CallBytes(Put, $"/api/attachments/{id}/poster", Jpeg(64, 36), other)) Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        using (var moving = await app.CallBytes(Put, $"/api/attachments/{id}/poster", AnimatedPictureTests.Gif(4, 3, 3), cookie))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, moving.StatusCode);
            Assert.Equal("invalid_image", await ErrorOf(moving));
        }
        using (var poster = await app.CallBytes(Put, $"/api/attachments/{id}/poster", Jpeg(1280, 720), cookie)) Assert.Equal(HttpStatusCode.NoContent, poster.StatusCode);
        using var still = await app.Call(Get, $"/api/attachments/{id}/thumb", cookie: cookie);
        Assert.Equal("image/webp", still.Content.Headers.ContentType!.MediaType);
        using (var codec = SKCodec.Create(new MemoryStream(await still.Content.ReadAsByteArrayAsync()))!)
        {
            Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
            Assert.Equal(400, codec.Info.Width);   // the small version's longer side
        }

        await Send(chat, cookie, id);
        using var late = await app.CallBytes(Put, $"/api/attachments/{id}/poster", Jpeg(64, 36), cookie);
        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);   // once others can see it, it does not change
        using var seen = await app.Call(Get, $"/api/attachments/{id}/thumb", cookie: other);
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);

        // Not a video: no still picture to give.
        using var picture = await app.CallBytes(Post, $"/api/conversations/{chat}/attachments?name=a.jpg", Jpeg(40, 30), cookie);
        var pictureId = (await AppFixture.Json(picture)).GetProperty("id").GetGuid();
        using var notVideo = await app.CallBytes(Put, $"/api/attachments/{pictureId}/poster", Jpeg(64, 36), cookie);
        Assert.Equal(HttpStatusCode.NotFound, notVideo.StatusCode);
    }

    [Fact]
    public async Task Passed_on_it_is_still_a_video_with_its_length_and_still_picture_and_it_is_listed_with_the_pictures()
    {
        var (cookie, chat, _) = await Chat();
        var id = (await Uploaded(chat, cookie, Mp4(durationMs: 9000))).GetProperty("id").GetGuid();
        using (await app.CallBytes(Put, $"/api/attachments/{id}/poster", Jpeg(64, 36), cookie)) { }
        await Send(chat, cookie, id);

        using var copied = await app.Call(Post, $"/api/conversations/{chat}/attachments/copy", new { from = id }, cookie);
        var copy = await AppFixture.Json(copied);
        Assert.Equal("video", copy.GetProperty("kind").GetString());
        Assert.Equal(9000, copy.GetProperty("durationMs").GetInt32());
        using var still = await app.Call(Get, $"/api/attachments/{copy.GetProperty("id").GetGuid()}/thumb", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);

        using var media = await app.Call(Get, $"/api/conversations/{chat}/media?kind=image", cookie: cookie);
        var item = Assert.Single((await AppFixture.Json(media)).GetProperty("items").EnumerateArray());
        Assert.Equal("video", item.GetProperty("attachment").GetProperty("kind").GetString());
        using var files = await app.Call(Get, $"/api/conversations/{chat}/media?kind=file", cookie: cookie);
        Assert.Empty((await AppFixture.Json(files)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_list_says_a_video_was_sent_and_the_owner_counts_and_trims_videos_on_their_own()
    {
        var (cookie, chat, _) = await Chat();
        var mp4 = Mp4();
        var id = (await Uploaded(chat, cookie, mp4)).GetProperty("id").GetGuid();
        await Send(chat, cookie, id);
        using var list = await app.Call(Get, "/api/conversations", cookie: cookie);
        var row = (await AppFixture.Json(list)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chat);
        Assert.Equal("video", row.GetProperty("lastMessage").GetProperty("attachment").GetString());

        var (_, owner) = await app.Owner();
        using var storage = await app.Call(Get, "/api/owner/storage", cookie: owner);
        Assert.True((await AppFixture.Json(storage)).GetProperty("videos").GetProperty("count").GetInt32() >= 1);

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        foreach (var (kind, videos) in new[] { ("video", 1), ("image", 0), ("file", 0) })
        {
            using var preview = await app.Call(Post, "/api/owner/trim/files/preview", new { before = app.Clock.Now, kind, where = "direct", minBytes = (long)mp4.Length, confirm = "Delete" }, owner);
            var counted = await AppFixture.Json(preview);
            Assert.True(counted.GetProperty("videos").GetProperty("count").GetInt32() >= videos, kind);
            if (videos == 0) Assert.Equal(0, counted.GetProperty("videos").GetProperty("count").GetInt32());
        }
        await using var db = app.Db.NewContext();
        Assert.Equal(AttachmentKind.Video, (await db.Attachments.SingleAsync(a => a.Id == id)).Kind);
    }

    [Theory]
    [InlineData(0, 0, 1, "Đã gửi 1 video")]
    [InlineData(0, 0, 3, "Đã gửi 3 video")]
    [InlineData(2, 0, 1, "Đã gửi 3 tệp đính kèm")]
    [InlineData(0, 1, 2, "Đã gửi 3 tệp đính kèm")]
    public void A_notification_names_videos(int images, int files, int videos, string what)
    {
        var job = new PushJob(Guid.NewGuid(), 5, Guid.NewGuid(), "Minh", images, files, Videos: videos);
        Assert.Equal(("Minh", what), PushSender.Words(job, ConversationKind.Direct, null, Gom.Web.ServerText.Vi));
    }

    [Fact]
    public void Reading_an_outline_stops_at_a_box_that_lies_about_its_size_or_too_many_boxes()
    {
        var mp4 = Mp4();
        const int moovAt = 28;   // right after the ftyp box
        Assert.Equal("moov", Encoding.ASCII.GetString(mp4, moovAt + 4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(mp4.AsSpan(moovAt), uint.MaxValue - 1);   // moov says it runs far past the file
        Assert.Null(Gom.Web.Uploads.Mp4.Read(new MemoryStream(mp4)));
        Assert.Null(Gom.Web.Uploads.Mp4.Read(new MemoryStream(Mp4(emptyBoxes: 10_000))));   // more boxes than any real file has
        Assert.NotNull(Gom.Web.Uploads.Mp4.Read(new MemoryStream(Mp4(emptyBoxes: 9_000))));
        Assert.NotNull(Gom.Web.Uploads.Mp4.Read(new MemoryStream(Mp4())));
    }
}
