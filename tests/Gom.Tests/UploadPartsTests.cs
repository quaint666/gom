using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Gom.Tests;

public class BlobStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gom-blob-" + Guid.NewGuid().ToString("N"));
    private DiskBlobStore Store() => new(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public async Task What_is_written_is_read_back_and_a_second_write_replaces_it_whole()
    {
        var store = Store(); var name = Guid.NewGuid().ToString();
        await store.WriteAsync(name, new byte[] { 1, 2, 3 }, default);
        await using (var s = store.OpenRead(name)!) { var m = new MemoryStream(); await s.CopyToAsync(m); Assert.Equal(new byte[] { 1, 2, 3 }, m.ToArray()); }
        await store.WriteAsync(name, new byte[] { 9 }, default);
        await using (var s = store.OpenRead(name)!) { var m = new MemoryStream(); await s.CopyToAsync(m); Assert.Equal(new byte[] { 9 }, m.ToArray()); }
    }

    [Fact]
    public async Task Nothing_is_left_beside_a_finished_write_and_a_missing_name_reads_as_nothing_and_deletes_quietly()
    {
        var store = Store(); var name = Guid.NewGuid().ToString();
        await store.WriteAsync(name, new byte[10], default);
        Assert.Single(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
        store.Delete(name);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
        store.Delete(name);
        Assert.Null(store.OpenRead(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../secret")]
    [InlineData("a/b")]
    [InlineData("..\\x")]
    [InlineData("0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f/../../x")]
    [InlineData("../0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f")]
    [InlineData("x0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f")]
    [InlineData("0F0F0F0F-0F0F-0F0F-0F0F-0F0F0F0F0F0F")]
    [InlineData("0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f.exe")]
    [InlineData("0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f.t.t")]
    public async Task A_name_this_server_would_not_make_never_reaches_the_disk(string name)
    {
        var store = Store();
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(name, new byte[1], default));
        Assert.Throws<ArgumentException>(() => store.OpenRead(name));
        Assert.Throws<ArgumentException>(() => store.Delete(name));
    }

    [Fact]
    public async Task A_small_version_has_a_name_of_its_own()
    {
        var store = Store(); var id = Guid.NewGuid();
        await store.WriteAsync(id.ToString(), new byte[] { 1 }, default);
        await store.WriteAsync(id + ".t", new byte[] { 2 }, default);
        Assert.Equal(2, Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void An_empty_folder_setting_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new DiskBlobStore(" "));
    }
}

public class FileNameTests
{
    [Theory]
    [InlineData("báo cáo.pdf", "báo cáo.pdf")]
    [InlineData("C:\\Users\\me\\Desktop\\anh.png", "anh.png")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("../../x.txt", "x.txt")]
    [InlineData("  .hidden.  ", "hidden")]
    [InlineData("", "tệp")]
    [InlineData(null, "tệp")]
    [InlineData("...", "tệp")]
    public void Keeps_the_last_part_and_drops_the_rest(string? given, string expected) => Assert.Equal(expected, FileNames.Clean(given));

    [Fact]
    public void Drops_controls_and_the_marks_that_turn_text_around()
    {
        Assert.Equal("photogpj.exe", FileNames.Clean("photo\u202Egpj.exe"));
        Assert.Equal("ab", FileNames.Clean("a\0b\r\n"));
        Assert.Equal("ab.txt", FileNames.Clean("a\u200Bb.txt"));
    }

    [Fact]
    public void A_long_name_is_cut_but_keeps_its_type_and_never_splits_a_letter()
    {
        var name = new string('a', 300) + ".pdf";
        var clean = FileNames.Clean(name);
        Assert.Equal(FileNames.MaxLength, new System.Globalization.StringInfo(clean).LengthInTextElements);
        Assert.EndsWith(".pdf", clean);
        var emojis = string.Concat(Enumerable.Repeat("👨‍👩‍👧", 200)) + ".png";
        var cleanEmoji = FileNames.Clean(emojis);
        Assert.Equal(FileNames.MaxLength, new System.Globalization.StringInfo(cleanEmoji).LengthInTextElements);
        Assert.EndsWith(".png", cleanEmoji);
    }

    [Theory]
    [InlineData("anh.jpg", "anh.webp")]
    [InlineData("anh", "anh.webp")]
    [InlineData("a.b.png", "a.b.webp")]
    [InlineData(".png", ".png.webp")]
    public void A_picture_is_named_for_what_it_is_now(string clean, string expected) => Assert.Equal(expected, FileNames.AsWebp(clean));
}

public class ImageProcessorTests
{
    private static readonly UploadOptions O = new() { MaxPixels = 1_000_000 };

    private static ImageResult Run(byte[] bytes, UploadOptions? o = null) => ImageProcessor.Process(bytes, o ?? O);

    private static (SKEncodedImageFormat? Format, int Width, int Height) Look(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        return (codec?.EncodedFormat, codec?.Info.Width ?? 0, codec?.Info.Height ?? 0);
    }

    [Fact]
    public void A_big_picture_is_made_smaller_as_webp_with_a_small_version_and_keeps_its_shape()
    {
        var source = TestImages.Jpeg(900, 600);
        var result = Run(source, new UploadOptions { ImageMaxEdge = 450, ThumbEdge = 90, MaxPixels = 1_000_000 });
        Assert.Null(result.Problem);
        var image = result.Image!;
        Assert.Equal((SKEncodedImageFormat.Webp, 450, 300), Look(image.Main));
        Assert.Equal((450, 300), (image.Width, image.Height));
        Assert.Equal((SKEncodedImageFormat.Webp, 90, 60), Look(image.Thumb));
        Assert.True(image.Main.Length < source.Length, "the stored picture is smaller than what was sent");
    }

    [Fact]
    public void A_picture_is_never_made_bigger()
    {
        var result = Run(TestImages.Png(120, 80));
        Assert.Equal((SKEncodedImageFormat.Webp, 120, 80), Look(result.Image!.Main));
        Assert.Equal((SKEncodedImageFormat.Webp, 120, 80), Look(result.Image.Thumb));
    }

    [Fact]
    public void No_metadata_survives_not_the_location_and_not_the_exif_block()
    {
        var source = TestImages.WithExif(TestImages.Jpeg(200, 120), 1);
        Assert.True(TestImages.Contains(source, TestImages.GpsMarker));
        var image = Run(source).Image!;
        Assert.False(TestImages.Contains(image.Main, TestImages.GpsMarker));
        Assert.False(TestImages.Contains(image.Main, "Exif"));
        Assert.False(TestImages.Contains(image.Thumb, TestImages.GpsMarker));
        Assert.False(TestImages.Contains(image.Thumb, "Exif"));
    }

    [Theory]
    [InlineData(1, 200, 120)]
    [InlineData(2, 200, 120)]
    [InlineData(3, 200, 120)]
    [InlineData(4, 200, 120)]
    [InlineData(5, 120, 200)]
    [InlineData(6, 120, 200)]
    [InlineData(7, 120, 200)]
    [InlineData(8, 120, 200)]
    public void The_turn_a_phone_noted_is_applied_before_the_note_is_dropped(int orientation, int width, int height)
    {
        var image = Run(TestImages.WithExif(TestImages.Jpeg(200, 120), orientation)).Image!;
        Assert.Equal((SKEncodedImageFormat.Webp, width, height), Look(image.Main));
        Assert.Equal((width, height), (image.Width, image.Height));
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopRight)]
    [InlineData(SKEncodedOrigin.BottomRight)]
    [InlineData(SKEncodedOrigin.BottomLeft)]
    [InlineData(SKEncodedOrigin.LeftTop)]
    [InlineData(SKEncodedOrigin.RightTop)]
    [InlineData(SKEncodedOrigin.RightBottom)]
    [InlineData(SKEncodedOrigin.LeftBottom)]
    public void Every_orientation_moves_every_pixel_to_where_the_standard_puts_it(SKEncodedOrigin origin)
    {
        const int w = 3, h = 2;
        using var source = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) source.SetPixel(x, y, new SKColor((byte)(40 * (x + 1)), (byte)(60 * (y + 1)), (byte)(10 * (x + 3 * y + 1))));
        using var turned = ImageProcessor.Orient(source, origin);

        // The standard, written out by hand (stored pixel (x, y) goes to ...).
        (int X, int Y) Where(int x, int y) => origin switch
        {
            SKEncodedOrigin.TopRight => (w - 1 - x, y),
            SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),
            SKEncodedOrigin.BottomLeft => (x, h - 1 - y),
            SKEncodedOrigin.LeftTop => (y, x),
            SKEncodedOrigin.RightTop => (h - 1 - y, x),
            SKEncodedOrigin.RightBottom => (h - 1 - y, w - 1 - x),
            SKEncodedOrigin.LeftBottom => (y, w - 1 - x),
            _ => throw new InvalidOperationException(),
        };
        var sideways = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        Assert.Equal(sideways ? (h, w) : (w, h), (turned.Width, turned.Height));
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (tx, ty) = Where(x, y);
                Assert.Equal(source.GetPixel(x, y), turned.GetPixel(tx, ty));
            }
    }

    [Fact]
    public void See_through_parts_stay_see_through()
    {
        var image = Run(TestImages.Png(100, 100, transparentCorner: true)).Image!;
        using var decoded = SKBitmap.Decode(image.Main);
        Assert.Equal(0, decoded.GetPixel(2, 2).Alpha);
        Assert.Equal(255, decoded.GetPixel(90, 90).Alpha);
    }

    [Fact]
    public void Something_that_is_not_a_picture_or_is_broken_is_refused_in_words()
    {
        Assert.Equal(ImageProblem.NotAnImage, Run("hello"u8.ToArray()).Problem);
        Assert.Equal(ImageProblem.NotAnImage, Run("GIF89a\x01\0\x01\0\0\0\0"u8.ToArray()).Problem);
        var broken = TestImages.Jpeg(100, 100); Array.Resize(ref broken, 300);
        Assert.NotNull(Run(broken).Problem);
        Assert.NotNull(Run([0xFF, 0xD8, 0xFF, 0x00, 0x01, 0x02]).Problem);   // starts like a JPEG, is nothing
    }

    [Fact]
    public void A_picture_with_too_many_pixels_is_refused_from_its_header_before_it_is_decoded()
    {
        var result = Run(TestImages.Png(1500, 1000));   // 1.5 million pixels, over the 1 million allowed here
        Assert.Equal(ImageProblem.TooManyPixels, result.Problem);
        Assert.Null(result.Image);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, true)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, true)]    // GIF89a
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, true)]    // GIF87a
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x38, 0x61 }, false)]   // not a GIF version
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 }, false)]
    [InlineData(new byte[] { }, false)]
    public void Recognises_the_four_formats_by_their_first_bytes(byte[] head, bool image) => Assert.Equal(image, ImageProcessor.LooksLikeImage(head));

    [Fact]
    public void Recognises_webp_by_its_riff_header()
    {
        using var b = TestImages.Busy(40, 40);
        Assert.True(ImageProcessor.LooksLikeImage(TestImages.Encode(b, SKEncodedImageFormat.Webp)));
        Assert.False(ImageProcessor.LooksLikeImage("RIFF....WAVE"u8.ToArray()));
    }
}

public class ImageLimiterTests
{
    [Fact]
    public async Task A_second_picture_waits_for_the_slot_and_gives_up_after_its_time()
    {
        var limiter = new ImageLimiter(1, TimeSpan.FromMilliseconds(100));
        using var held = new ManualResetEventSlim();
        var first = limiter.RunAsync(() => { held.Wait(TimeSpan.FromSeconds(10)); return "one"; });
        await Task.Delay(50);
        Assert.Null(await limiter.RunAsync(() => "two"));
        held.Set();
        Assert.Equal("one", await first);
        Assert.Equal("three", await limiter.RunAsync(() => "three"));
    }
}

public class AttachmentCleanerTests(AppFixture app) : IClassFixture<AppFixture>
{
    [Fact]
    public async Task Removes_old_unused_uploads_with_their_files_and_leaves_new_ones_and_used_ones()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        using var sent = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "x" }, ca);
        var messageId = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();

        var store = new DiskBlobStore(app.UploadsDir);
        async Task<Guid> Add(Guid? message, TimeSpan age)
        {
            var id = Guid.NewGuid();
            await store.WriteAsync(id.ToString(), new byte[5], default); await store.WriteAsync(id + ".t", new byte[2], default);
            await using var db = app.Db.NewContext();
            db.Attachments.Add(new Attachment { Id = id, ConversationId = chat, UploaderId = a.Id, MessageId = message, Kind = AttachmentKind.Image, Name = "x.webp", ContentType = "image/webp", Size = 5, CreatedAt = app.Clock.Now - age });
            await db.SaveChangesAsync();
            return id;
        }
        var oldUnused = await Add(null, TimeSpan.FromHours(3));
        var newUnused = await Add(null, TimeSpan.FromMinutes(5));
        var oldUsed = await Add(messageId, TimeSpan.FromHours(3));

        await using var run = app.Db.NewContext();
        var removed = await AttachmentCleaner.RunOnce(run, store, app.Clock.Now, TimeSpan.FromHours(1), default);

        Assert.Equal(1, removed);
        await using var check = app.Db.NewContext();
        Assert.False(await check.Attachments.AnyAsync(x => x.Id == oldUnused));
        Assert.True(await check.Attachments.AnyAsync(x => x.Id == newUnused));
        Assert.True(await check.Attachments.AnyAsync(x => x.Id == oldUsed));
        Assert.Null(store.OpenRead(oldUnused.ToString())); Assert.Null(store.OpenRead(oldUnused + ".t"));
        Assert.NotNull(store.OpenRead(newUnused.ToString())); Assert.NotNull(store.OpenRead(oldUsed.ToString()));
    }
}

public class MemoryGateTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(80);

    [Fact]
    public async Task Bytes_that_fit_are_held_at_once_and_given_back_by_disposing_once()
    {
        var gate = new MemoryGate(100);
        var a = await gate.EnterAsync(60, Short);
        Assert.Equal(60, gate.Used);
        var b = await gate.EnterAsync(40, Short);
        Assert.Equal(100, gate.Used);
        a!.Dispose(); a.Dispose();   // twice changes nothing
        Assert.Equal(40, gate.Used);
        b!.Dispose();
        Assert.Equal(0, gate.Used);
    }

    [Fact]
    public async Task More_than_the_whole_budget_is_known_in_advance_and_never_waits()
    {
        var gate = new MemoryGate(100);
        Assert.True(gate.Fits(100)); Assert.False(gate.Fits(101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gate.EnterAsync(101, Short));
    }

    [Fact]
    public async Task A_request_that_does_not_fit_now_waits_for_room_and_gets_it_when_bytes_are_given_back()
    {
        var gate = new MemoryGate(100);
        var first = await gate.EnterAsync(80, Short);
        var waiting = gate.EnterAsync(50, TimeSpan.FromSeconds(10));
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted);
        first!.Dispose();
        var second = await waiting;
        Assert.NotNull(second);
        Assert.Equal(50, gate.Used);
    }

    [Fact]
    public async Task Waiting_ends_in_nothing_after_its_time_and_the_place_in_line_is_given_up()
    {
        var gate = new MemoryGate(100);
        var first = await gate.EnterAsync(90, Short);
        Assert.Null(await gate.EnterAsync(50, Short));
        first!.Dispose();
        Assert.Equal(0, gate.Used);                           // nothing was held for the one that gave up
        Assert.NotNull(await gate.EnterAsync(100, Short));    // and nothing is left in line to be served ahead
    }

    [Fact]
    public async Task A_big_request_is_not_kept_waiting_forever_by_small_ones_that_come_after_it()
    {
        var gate = new MemoryGate(100);
        var holding = await gate.EnterAsync(60, Short);
        var big = gate.EnterAsync(90, TimeSpan.FromSeconds(10));    // does not fit until the 60 is back
        await Task.Delay(30);
        var small = gate.EnterAsync(10, TimeSpan.FromSeconds(10));  // would fit now, but the big one is first in line
        await Task.Delay(30);
        Assert.False(small.IsCompleted);
        holding!.Dispose();
        var bigHold = await big;
        var smallHold = await small;                                // 10 more fits (100 in all), so it is served right behind the big one
        Assert.NotNull(smallHold);
        Assert.Equal(100, gate.Used);
        bigHold!.Dispose();
        smallHold.Dispose();
        Assert.Equal(0, gate.Used);
    }

    [Fact]
    public async Task Many_at_once_never_hold_more_than_the_budget()
    {
        var gate = new MemoryGate(100);
        var peak = 0L;
        var tasks = Enumerable.Range(0, 40).Select(async i =>
        {
            using var hold = await gate.EnterAsync(30 + i % 4 * 10, TimeSpan.FromSeconds(20));
            Assert.NotNull(hold);
            lock (gate) peak = Math.Max(peak, gate.Used);
            await Task.Delay(5);
        });
        await Task.WhenAll(tasks);
        Assert.InRange(peak, 1, 100);
        Assert.Equal(0, gate.Used);
    }
}

public class ImagePlanTests
{
    private static readonly UploadOptions O = new() { MaxPixels = 100_000_000, ImageMaxEdge = 800 };
    private const long Overhead = 16L * 1024 * 1024;

    [Fact]
    public void A_big_jpeg_is_decoded_already_scaled_down_by_the_smallest_step_that_keeps_the_stored_size()
    {
        var plan = ImageProcessor.Plan(TestImages.Jpeg(3200, 2400), O).Plan!;
        Assert.Equal((800, 600), (plan.DecodeWidth, plan.DecodeHeight));                  // a quarter, not an eighth (400 is under 800)
        Assert.Equal(800L * 600 * 4 + Overhead, plan.Cost);
        var wider = ImageProcessor.Plan(TestImages.Jpeg(3200, 2400), new UploadOptions { MaxPixels = 100_000_000, ImageMaxEdge = 1000 }).Plan!;
        Assert.Equal((1600, 1200), (wider.DecodeWidth, wider.DecodeHeight));              // a quarter would be 800, under 1000
    }

    [Fact]
    public void A_picture_not_bigger_than_the_stored_size_is_decoded_whole()
    {
        var plan = ImageProcessor.Plan(TestImages.Jpeg(600, 400), O).Plan!;
        Assert.Equal((600, 400), (plan.DecodeWidth, plan.DecodeHeight));
    }

    [Fact]
    public void A_png_cannot_be_decoded_scaled_so_its_cost_is_its_whole_size()
    {
        var plan = ImageProcessor.Plan(TestImages.PlainPng(3200, 2400), O).Plan!;
        Assert.Equal((3200, 2400), (plan.DecodeWidth, plan.DecodeHeight));
        Assert.Equal(3200L * 2400 * 4 + Overhead, plan.Cost);
    }

    [Fact]
    public void A_picture_that_must_be_turned_costs_for_a_second_copy()
    {
        var upright = ImageProcessor.Plan(TestImages.WithExif(TestImages.Jpeg(600, 400), 1), O).Plan!;
        var turned = ImageProcessor.Plan(TestImages.WithExif(TestImages.Jpeg(600, 400), 6), O).Plan!;
        Assert.Equal(600L * 400 * 4 + Overhead, upright.Cost);
        Assert.Equal(2 * 600L * 400 * 4 + Overhead, turned.Cost);
    }

    [Fact]
    public void Planning_refuses_what_is_not_a_picture_and_what_has_too_many_pixels_without_decoding_anything()
    {
        Assert.Equal(ImageProblem.NotAnImage, ImageProcessor.Plan("hello"u8.ToArray(), O).Problem);
        Assert.Equal(ImageProblem.TooManyPixels, ImageProcessor.Plan(TestImages.PlainPng(1500, 1000), new UploadOptions { MaxPixels = 1_000_000 }).Problem);
    }

    [Fact]
    public void Decoding_scaled_gives_the_same_stored_size_as_decoding_whole()
    {
        var jpeg = TestImages.Jpeg(3200, 2400);
        var scaled = ImageProcessor.Process(jpeg, O).Image!;
        Assert.Equal((800, 600), (scaled.Width, scaled.Height));
        // The same picture with the scaling switched off ends at the same size: the final step does the rest.
        using var codec = SKCodec.Create(new MemoryStream(jpeg))!;
        var whole = new ImageProcessor.ImagePlan(codec.Info.Width, codec.Info.Height, codec.EncodedOrigin, 0);
        var full = ImageProcessor.Process(jpeg, whole, O).Image!;
        Assert.Equal((full.Width, full.Height), (scaled.Width, scaled.Height));
    }

    [Fact]
    public void A_cut_off_picture_is_still_refused_when_decoded_scaled()
    {
        var broken = TestImages.Jpeg(3200, 2400); Array.Resize(ref broken, broken.Length / 3);
        Assert.NotNull(ImageProcessor.Process(broken, O).Problem);
    }
}
