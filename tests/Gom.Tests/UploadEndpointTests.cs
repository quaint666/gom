using System.Net;
using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Gom.Tests;

/// <summary>Uploading pictures and files to a conversation, and fetching them.</summary>
public class UploadEndpointTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post;

    private sealed record Pair(User A, string CA, User B, string CB, Guid Chat);

    private async Task<Pair> NewPair()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return new Pair(a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private Task<HttpResponseMessage> Upload(Guid chat, string cookie, byte[] body, string name = "x", long? declared = null) =>
        app.CallBytes(Post, $"/api/conversations/{chat}/attachments?name={Uri.EscapeDataString(name)}", body, cookie, declaredLength: declared);

    private async Task<Guid> Uploaded(Guid chat, string cookie, byte[] body, string name = "x")
    {
        using var r = await Upload(chat, cookie, body, name);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString()!;

    [Fact]
    public async Task A_member_uploads_a_photo_and_gets_a_smaller_webp_and_a_small_version_back()
    {
        var p = await NewPair();
        var photo = TestImages.WithExif(TestImages.Jpeg(1000, 700), 1);
        using var created = await Upload(p.Chat, p.CA, photo, "Ảnh nghỉ hè.JPG");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await AppFixture.Json(created);
        Assert.Equal("image", json.GetProperty("kind").GetString());
        Assert.Equal("Ảnh nghỉ hè.webp", json.GetProperty("name").GetString());
        Assert.InRange(json.GetProperty("width").GetInt32(), 1, 1600);
        var id = json.GetProperty("id").GetGuid();

        using var full = await app.Call(Get, $"/api/attachments/{id}", cookie: p.CA);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("image/webp", full.Content.Headers.ContentType!.MediaType);
        var bytes = await full.Content.ReadAsByteArrayAsync();
        Assert.Equal(SKEncodedImageFormat.Webp, SKCodec.Create(new MemoryStream(bytes))!.EncodedFormat);
        Assert.True(bytes.Length < photo.Length);
        Assert.Equal(bytes.Length, json.GetProperty("size").GetInt64());

        using var thumb = await app.Call(Get, $"/api/attachments/{id}/thumb", cookie: p.CA);
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task What_is_served_has_no_location_in_it_and_is_marked_private_and_not_to_be_sniffed()
    {
        var p = await NewPair();
        var id = await Uploaded(p.Chat, p.CA, TestImages.WithExif(TestImages.Jpeg(300, 200), 1));
        foreach (var path in new[] { $"/api/attachments/{id}", $"/api/attachments/{id}/thumb" })
        {
            using var r = await app.Call(Get, path, cookie: p.CA);
            var bytes = await r.Content.ReadAsByteArrayAsync();
            Assert.False(TestImages.Contains(bytes, TestImages.GpsMarker));
            Assert.False(TestImages.Contains(bytes, "Exif"));
            Assert.True(r.Headers.CacheControl!.Private);
            Assert.Equal(TimeSpan.FromDays(1), r.Headers.CacheControl.MaxAge);
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        }
    }

    [Fact]
    public async Task A_file_is_kept_exactly_and_is_only_ever_a_download_whatever_it_is_called()
    {
        var p = await NewPair();
        var bytes = "<html><script>alert(1)</script></html>"u8.ToArray();
        using var created = await Upload(p.Chat, p.CA, bytes, "trang.html");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await AppFixture.Json(created);
        Assert.Equal("file", json.GetProperty("kind").GetString());
        Assert.Equal("trang.html", json.GetProperty("name").GetString());
        var id = json.GetProperty("id").GetGuid();

        using var got = await app.Call(Get, $"/api/attachments/{id}", cookie: p.CA);
        Assert.Equal(bytes, await got.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", got.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", got.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("trang.html", got.Content.Headers.ContentDisposition.FileNameStar ?? got.Content.Headers.ContentDisposition.FileName);
        Assert.Equal("nosniff", got.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", got.Headers.CacheControl!.ToString());   // a file is not kept by the browser
    }

    [Fact]
    public async Task A_picture_can_be_asked_for_again_cheaply_with_its_tag()
    {
        var p = await NewPair();
        var id = await Uploaded(p.Chat, p.CA, TestImages.Png(60, 60));
        using var first = await app.Call(Get, $"/api/attachments/{id}", cookie: p.CA);
        var tag = first.Headers.ETag!.ToString();
        using var client = app.Client();
        var again = new HttpRequestMessage(Get, $"/api/attachments/{id}");
        again.Headers.Add("Cookie", $"{Gom.Web.Auth.SessionCookie.Name}={p.CA}");
        again.Headers.TryAddWithoutValidation("If-None-Match", tag);
        using var second = await client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        // The small version is a different thing with a different tag.
        using var thumb = await app.Call(Get, $"/api/attachments/{id}/thumb", cookie: p.CA);
        Assert.NotEqual(tag, thumb.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task A_file_with_an_odd_name_is_saved_under_a_clean_one()
    {
        var p = await NewPair();
        using var created = await Upload(p.Chat, p.CA, new byte[] { 1, 2, 3 }, "C:\\Users\\x\\photo\u202Egpj.exe");
        Assert.Equal("photogpj.exe", (await AppFixture.Json(created)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_file_has_no_small_version()
    {
        var p = await NewPair();
        var id = await Uploaded(p.Chat, p.CA, new byte[] { 1, 2, 3 });
        using var r = await app.Call(Get, $"/api/attachments/{id}/thumb", cookie: p.CA);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task A_stranger_can_neither_upload_to_a_conversation_nor_fetch_from_it_and_is_told_nothing()
    {
        var p = await NewPair();
        var (_, stranger) = await app.Member();
        var id = await Uploaded(p.Chat, p.CA, TestImages.Png(50, 50));
        using var up = await Upload(p.Chat, stranger, new byte[] { 1 });
        Assert.Equal(HttpStatusCode.NotFound, up.StatusCode);
        using var get = await app.Call(Get, $"/api/attachments/{id}", cookie: stranger);
        using var gone = await app.Call(Get, $"/api/attachments/{Guid.NewGuid()}", cookie: p.CA);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);   // the same answer as for one that does not exist
    }

    [Fact]
    public async Task Until_it_is_in_a_message_only_the_uploader_sees_it()
    {
        var p = await NewPair();
        var id = await Uploaded(p.Chat, p.CA, TestImages.Png(50, 50));
        using var other = await app.Call(Get, $"/api/attachments/{id}", cookie: p.CB);
        using var mine = await app.Call(Get, $"/api/attachments/{id}", cookie: p.CA);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
    }

    /// <summary>What putting it in a message will do (that step is built next): marks the upload as part of the message.</summary>
    private async Task Attach(Guid attachment, Guid chat, string cookie)
    {
        using var sent = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "ảnh" }, cookie);
        var messageId = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();
        await using var db = app.Db.NewContext();
        var row = await db.Attachments.FindAsync(attachment);
        row!.MessageId = messageId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Once_it_is_in_a_message_every_member_sees_it_and_a_stranger_still_does_not()
    {
        var p = await NewPair();
        var (_, stranger) = await app.Member();
        var id = await Uploaded(p.Chat, p.CA, TestImages.Png(50, 50));
        await Attach(id, p.Chat, p.CA);
        using var member = await app.Call(Get, $"/api/attachments/{id}", cookie: p.CB);
        using var outsider = await app.Call(Get, $"/api/attachments/{id}", cookie: stranger);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    [Fact]
    public async Task A_file_has_no_small_version_even_if_something_were_stored_under_that_name()
    {
        var p = await NewPair();
        var id = await Uploaded(p.Chat, p.CA, new byte[] { 1, 2, 3 });
        await new Gom.Web.Uploads.DiskBlobStore(app.UploadsDir).WriteAsync(id + ".t", new byte[] { 9 }, default);
        using var r = await app.Call(Get, $"/api/attachments/{id}/thumb", cookie: p.CA);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Someone_signed_out_gets_nothing()
    {
        var p = await NewPair();
        var id = await Uploaded(p.Chat, p.CA, TestImages.Png(50, 50));
        using var up = await app.CallBytes(Post, $"/api/conversations/{p.Chat}/attachments", new byte[] { 1 });
        using var get = await app.Call(Get, $"/api/attachments/{id}");
        Assert.Equal(HttpStatusCode.Unauthorized, up.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
    }

    [Fact]
    public async Task An_upload_without_the_site_origin_is_refused_like_any_change()
    {
        var p = await NewPair();
        using var client = app.Client();
        var request = new HttpRequestMessage(Post, $"/api/conversations/{p.Chat}/attachments") { Content = new ByteArrayContent(new byte[] { 1 }) };
        request.Headers.Add("Cookie", $"{Gom.Web.Auth.SessionCookie.Name}={p.CA}");
        request.Headers.Add("Origin", "https://evil.example");
        using var r = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Too_big_is_refused_by_kind_a_file_over_its_limit_and_a_picture_over_its_own()
    {
        var p = await NewPair();
        using var file = await Upload(p.Chat, p.CA, new byte[300_001]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, file.StatusCode);
        Assert.Equal("too_large", await ErrorOf(file));
        // A picture of 250 KB is a fine file size but over the 200 KB allowed for pictures.
        var big = TestImages.Jpeg(700, 700); Array.Resize(ref big, 250_000);
        using var picture = await Upload(p.Chat, p.CA, big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, picture.StatusCode);
    }

    [Fact]
    public async Task A_size_announced_up_front_is_refused_without_reading_it()
    {
        var p = await NewPair();
        using var r = await Upload(p.Chat, p.CA, new byte[10], declared: 5_000_000);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
    }

    [Fact]
    public async Task Reading_a_body_stops_at_the_limit_whatever_the_client_said_about_its_size()
    {
        Assert.Equal(new byte[10], await AttachmentEndpoints.ReadLimited(new MemoryStream(new byte[10]), 10, default));   // exactly the limit is fine
        Assert.Null(await AttachmentEndpoints.ReadLimited(new MemoryStream(new byte[11]), 10, default));
        Assert.Null(await AttachmentEndpoints.ReadLimited(new MemoryStream(new byte[300_000]), 100_000, default));    // across several reads
        Assert.Empty((await AttachmentEndpoints.ReadLimited(new MemoryStream([]), 10, default))!);
    }

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        var p = await NewPair();
        using var r = await Upload(p.Chat, p.CA, []);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("empty_upload", await ErrorOf(r));
    }

    [Fact]
    public async Task A_broken_picture_and_a_picture_with_too_many_pixels_each_get_their_own_answer()
    {
        var p = await NewPair();
        var broken = TestImages.Jpeg(100, 100); Array.Resize(ref broken, 400);
        using var a = await Upload(p.Chat, p.CA, broken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, a.StatusCode);
        Assert.Equal("invalid_image", await ErrorOf(a));
        using var b = await Upload(p.Chat, p.CA, TestImages.PlainPng(1500, 1000));   // 1.5 million pixels, 1 million allowed in tests; a few hundred bytes as a file
        Assert.Equal(HttpStatusCode.UnprocessableEntity, b.StatusCode);
        Assert.Equal("image_dimensions", await ErrorOf(b));
    }

    [Fact]
    public async Task A_failed_picture_leaves_nothing_on_disk_or_in_the_table()
    {
        var p = await NewPair();
        var before = Directory.Exists(app.UploadsDir) ? Directory.EnumerateFiles(app.UploadsDir, "*", SearchOption.AllDirectories).Count() : 0;
        var broken = TestImages.Jpeg(100, 100); Array.Resize(ref broken, 400);
        (await Upload(p.Chat, p.CA, broken)).Dispose();
        var after = Directory.Exists(app.UploadsDir) ? Directory.EnumerateFiles(app.UploadsDir, "*", SearchOption.AllDirectories).Count() : 0;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Two_who_stopped_being_friends_cannot_upload_in_their_old_one_to_one_chat()
    {
        var p = await NewPair();
        using var unfriend = await app.Call(HttpMethod.Delete, $"/api/friends/{p.B.GomId}", cookie: p.CA);
        Assert.Equal(HttpStatusCode.NoContent, unfriend.StatusCode);
        using var r = await Upload(p.Chat, p.CA, new byte[] { 1 });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("not_friends", await ErrorOf(r));
    }

    [Fact]
    public async Task A_flood_of_uploads_is_slowed_with_a_wait_time()
    {
        var p = await NewPair();
        for (var i = 0; i < 30; i++) (await Upload(p.Chat, p.CA, new byte[] { 1 })).Dispose();   // the burst allowed in tests (the clock stands still)
        using var r = await Upload(p.Chat, p.CA, new byte[] { 1 });
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Equal("too_many_uploads", await ErrorOf(r));
        Assert.NotNull(r.Headers.RetryAfter);
    }

    private HttpRequestMessage UploadRequest(WebApplicationFactory<Program> factory, Guid chat, string cookie, byte[] body)
    {
        var request = new HttpRequestMessage(Post, $"/api/conversations/{chat}/attachments") { Content = new ByteArrayContent(body) };
        request.Headers.Add("Cookie", $"{Gom.Web.Auth.SessionCookie.Name}={cookie}");
        request.Headers.Add("Origin", AppFixture.Origin);
        return request;
    }

    [Fact]
    public async Task A_picture_needing_more_memory_than_there_is_for_pictures_is_turned_away_in_words_not_allowed_to_bring_the_site_down()
    {
        var p = await NewPair();
        using var small = app.Factory.WithWebHostBuilder(b => b.UseSetting("Uploads:MemoryMegabytes", "1"));
        using var client = small.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(AppFixture.Origin) });
        using var picture = await client.SendAsync(UploadRequest(small, p.Chat, p.CA, TestImages.Jpeg(300, 200)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, picture.StatusCode);
        Assert.Equal("image_dimensions", await ErrorOf(picture));
        using var file = await client.SendAsync(UploadRequest(small, p.Chat, p.CA, new byte[] { 1, 2, 3 }));   // a file needs no picture memory
        Assert.Equal(HttpStatusCode.Created, file.StatusCode);
    }

    [Fact]
    public async Task When_the_picture_memory_is_all_in_use_an_upload_waits_its_time_and_then_says_it_is_busy()
    {
        var p = await NewPair();
        using var busy = app.Factory.WithWebHostBuilder(b => { b.UseSetting("Uploads:MemoryMegabytes", "64"); b.UseSetting("Uploads:ImageWaitSeconds", "0"); });
        using var client = busy.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(AppFixture.Origin) });
        var memory = busy.Services.GetRequiredService<UploadMemory>();
        var hold = await memory.Images.EnterAsync(memory.Images.Total, TimeSpan.Zero);
        using var refused = await client.SendAsync(UploadRequest(busy, p.Chat, p.CA, TestImages.Jpeg(300, 200)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("busy", await ErrorOf(refused));
        hold!.Dispose();
        using var served = await client.SendAsync(UploadRequest(busy, p.Chat, p.CA, TestImages.Jpeg(300, 200)));
        Assert.Equal(HttpStatusCode.Created, served.StatusCode);
        Assert.Equal(0, memory.Images.Used);   // and everything is given back afterwards
        Assert.Equal(0, memory.Bodies.Used);
    }

    [Fact]
    public async Task When_the_memory_for_bytes_being_received_is_all_in_use_even_a_small_file_waits_its_time_and_then_says_it_is_busy()
    {
        var p = await NewPair();
        using var busy = app.Factory.WithWebHostBuilder(b => { b.UseSetting("Uploads:MemoryMegabytes", "1"); b.UseSetting("Uploads:ImageWaitSeconds", "0"); });
        using var client = busy.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(AppFixture.Origin) });
        var memory = busy.Services.GetRequiredService<UploadMemory>();
        var hold = await memory.Bodies.EnterAsync(memory.Bodies.Total, TimeSpan.Zero);
        using var refused = await client.SendAsync(UploadRequest(busy, p.Chat, p.CA, new byte[] { 1, 2, 3 }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        hold!.Dispose();
        using var served = await client.SendAsync(UploadRequest(busy, p.Chat, p.CA, new byte[] { 1, 2, 3 }));
        Assert.Equal(HttpStatusCode.Created, served.StatusCode);
    }

    [Fact]
    public async Task Memory_is_given_back_after_every_kind_of_upload_including_failed_ones()
    {
        var p = await NewPair();
        var memory = app.Factory.Services.GetRequiredService<UploadMemory>();
        var broken = TestImages.Jpeg(100, 100); Array.Resize(ref broken, 400);
        (await Upload(p.Chat, p.CA, TestImages.Jpeg(300, 200))).Dispose();
        (await Upload(p.Chat, p.CA, broken)).Dispose();
        (await Upload(p.Chat, p.CA, TestImages.PlainPng(1500, 1000))).Dispose();
        (await Upload(p.Chat, p.CA, new byte[300_001])).Dispose();
        (await Upload(p.Chat, p.CA, [])).Dispose();
        Assert.Equal(0, memory.Images.Used);
        Assert.Equal(0, memory.Bodies.Used);
    }

    [Fact]
    public async Task Without_a_folder_set_uploads_are_off_and_say_so()
    {
        var p = await NewPair();
        using var off = app.Factory.WithWebHostBuilder(b => b.UseSetting("Uploads:Path", ""));
        using var client = off.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(AppFixture.Origin) });
        var request = new HttpRequestMessage(Post, $"/api/conversations/{p.Chat}/attachments") { Content = new ByteArrayContent(new byte[] { 1 }) };
        request.Headers.Add("Cookie", $"{Gom.Web.Auth.SessionCookie.Name}={p.CA}");
        request.Headers.Add("Origin", AppFixture.Origin);
        using var r = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("uploads_disabled", await ErrorOf(r));
    }

    [Fact]
    public async Task The_table_records_who_uploaded_what_where_and_how_big()
    {
        var p = await NewPair();
        var id = await Uploaded(p.Chat, p.CA, new byte[] { 1, 2, 3, 4 }, "ghi chú.txt");
        await using var db = app.Db.NewContext();
        var row = await db.Attachments.FindAsync(id);
        Assert.NotNull(row);
        Assert.Equal((p.Chat, p.A.Id, (Guid?)null, AttachmentKind.File, "ghi chú.txt", 4L), (row.ConversationId, row.UploaderId, row.MessageId, row.Kind, row.Name, row.Size));
        Assert.Null(row.Width);
    }
}
