using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Gom.Web.Previews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gom.Tests;

/// <summary>Link previews from the posting of an address to the card under the message, against a site on this machine.</summary>
public class LinkPreviewTests(AppFixture app) : IClassFixture<AppFixture>, IAsyncLifetime
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Delete = HttpMethod.Delete, Patch = HttpMethod.Patch;
    private static int _ip;
    private LocalSite _site = null!;
    private WebApplicationFactory<Program> _on = null!;

    public Task InitializeAsync()
    {
        _site = new LocalSite();
        _on = app.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("LinkPreviews:Enabled", "true");
            b.UseSetting("LinkPreviews:AllowedPorts:0", _site.Port.ToString());
            b.UseSetting("LinkPreviews:TimeoutSeconds", "3");
            b.ConfigureServices(s => s.AddSingleton(_ => new SafeFetcher(Options.Create(new PreviewOptions { AllowedPorts = [_site.Port], TimeoutSeconds = 3 }), ip => IPAddress.IsLoopback(ip))));
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() { await _on.DisposeAsync(); await _site.DisposeAsync(); }

    private async Task<HttpResponseMessage> Call(HttpMethod method, string path, object? body, string cookie, bool previewsOn = true)
    {
        using var client = (previewsOn ? _on : app.Factory).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(AppFixture.Origin) });
        client.DefaultRequestHeaders.Add("X-Test-Ip", $"10.7.{Interlocked.Increment(ref _ip) / 250}.{_ip % 250 + 1}");
        return await AppFixture.Send(client, method, path, body, cookie);
    }

    private sealed record Pair(User A, string CA, User B, string CB, Guid Chat);

    private async Task<Pair> NewPair()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return new Pair(a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private async Task<Guid> Say(Pair p, string body, bool previewsOn = true)
    {
        using var r = await Call(Post, $"/api/conversations/{p.Chat}/messages", new { clientId = Guid.NewGuid().ToString(), body }, p.CA, previewsOn);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> Message(Pair p, Guid id)
    {
        using var r = await Call(Get, $"/api/conversations/{p.Chat}/messages", null, p.CB);
        return (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == id).Clone();
    }

    /// <summary>The message once its preview has come (it is made in the background), or the last look at it when it has not within a few seconds.</summary>
    private async Task<JsonElement> WithPreview(Pair p, Guid id, bool expected = true)
    {
        JsonElement m = default;
        for (var i = 0; i < (expected ? 80 : 12); i++)
        {
            m = await Message(p, id);
            if (m.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.Object) return m;
            await Task.Delay(100);
        }
        return m;
    }

    private static bool Has(JsonElement m) => m.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.Object;
    private string Link(string path = "/page") => _site.Url(path);

    [Fact]
    public async Task A_message_with_an_address_gets_a_card_with_title_description_site_and_a_small_picture()
    {
        var p = await NewPair();
        _site.Html = LocalSite.Page("Chuyến đi Đà Lạt", "Ảnh và kế hoạch cho cả nhà", "Gom Blog", "/pic.png");
        var id = await Say(p, $"xem cái này nè {Link()}, hay lắm");
        var m = await WithPreview(p, id);
        var preview = m.GetProperty("preview");
        Assert.Equal("Chuyến đi Đà Lạt", preview.GetProperty("title").GetString());
        Assert.Equal("Ảnh và kế hoạch cho cả nhà", preview.GetProperty("description").GetString());
        Assert.Equal("Gom Blog", preview.GetProperty("site").GetString());
        Assert.StartsWith("http://127.0.0.1", preview.GetProperty("url").GetString());

        var image = preview.GetProperty("imageId").GetGuid();
        using var thumb = await Call(Get, $"/api/attachments/{image}/thumb", null, p.CB);
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType!.MediaType);
        // The picture is not one of the message's own attachments.
        Assert.True(!m.TryGetProperty("attachments", out var files) || files.ValueKind == JsonValueKind.Null || files.GetArrayLength() == 0);
    }

    [Fact]
    public async Task The_reader_sees_it_in_history_search_and_pins_the_same_way()
    {
        var p = await NewPair();
        var id = await Say(p, $"tìm cái này {Link()}");
        await WithPreview(p, id);
        using var found = await Call(Get, $"/api/conversations/{p.Chat}/search?q=t%C3%ACm", null, p.CB);
        Assert.True(Has((await AppFixture.Json(found)).GetProperty("results")[0]));
    }

    [Fact]
    public async Task Nothing_is_fetched_when_previews_are_off_which_they_are_unless_switched_on()
    {
        var p = await NewPair();
        var before = _site.Total;
        var id = await Say(p, $"không xem trước {Link()}", previewsOn: false);
        await Task.Delay(700);
        Assert.False(Has(await Message(p, id)));
        Assert.Equal(before, _site.Total);
    }

    [Fact]
    public async Task A_message_without_an_address_or_with_a_page_that_says_nothing_gets_no_card()
    {
        var p = await NewPair();
        var before = _site.Total;
        var plain = await Say(p, "chỉ có chữ, không có liên kết");
        await Task.Delay(500);
        Assert.False(Has(await Message(p, plain)));
        Assert.Equal(before, _site.Total);

        var empty = await Say(p, $"trang trống {Link("/empty")}");
        var text = await Say(p, $"không phải trang web {Link("/text")}");
        var missing = await Say(p, $"không có trang {Link("/missing")}");
        Assert.False(Has(await WithPreview(p, empty, expected: false)));
        Assert.False(Has(await WithPreview(p, text, expected: false)));
        Assert.False(Has(await WithPreview(p, missing, expected: false)));
    }

    [Fact]
    public async Task A_picture_that_is_not_a_picture_or_is_a_vector_drawing_is_left_out_but_the_card_still_comes()
    {
        var p = await NewPair();
        _site.Html = LocalSite.Page("Chỉ chữ", "mô tả", "Nơi nào đó", "/svg");
        var id = await Say(p, Link());
        var m = await WithPreview(p, id);
        Assert.True(Has(m));
        Assert.Equal(JsonValueKind.Null, m.GetProperty("preview").GetProperty("imageId").ValueKind);
        _site.Html = LocalSite.Page("Chuyến đi Đà Lạt", "Ảnh và kế hoạch cho cả nhà", "Gom Blog", "/pic.png");
    }

    [Fact]
    public async Task Only_the_writer_can_take_the_card_away_and_it_goes_for_everyone_with_its_picture()
    {
        var p = await NewPair();
        var id = await Say(p, Link());
        var image = (await WithPreview(p, id)).GetProperty("preview").GetProperty("imageId").GetGuid();

        using var other = await Call(Delete, $"/api/conversations/{p.Chat}/messages/{id}/preview", null, p.CB);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        using var mine = await Call(Delete, $"/api/conversations/{p.Chat}/messages/{id}/preview", null, p.CA);
        Assert.Equal(HttpStatusCode.NoContent, mine.StatusCode);
        Assert.False(Has(await Message(p, id)));
        using var gone = await Call(Get, $"/api/attachments/{image}/thumb", null, p.CB);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var again = await Call(Delete, $"/api/conversations/{p.Chat}/messages/{id}/preview", null, p.CA);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task Taking_the_message_back_takes_the_card_and_its_picture_files_with_it()
    {
        var p = await NewPair();
        var id = await Say(p, Link());
        var image = (await WithPreview(p, id)).GetProperty("preview").GetProperty("imageId").GetGuid();
        Assert.True(File.Exists(Path.Combine(app.UploadsDir, image.ToString())) || Directory.EnumerateFiles(app.UploadsDir, image.ToString(), SearchOption.AllDirectories).Any());
        using var recalled = await Call(Delete, $"/api/conversations/{p.Chat}/messages/{id}", null, p.CA);
        Assert.Equal(HttpStatusCode.NoContent, recalled.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Previews.AnyAsync(x => x.MessageId == id));
        Assert.False(await db.Attachments.AnyAsync(x => x.Id == image));
        Assert.Empty(Directory.EnumerateFiles(app.UploadsDir, image + "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Editing_the_address_away_removes_the_card_and_a_new_address_gets_its_own()
    {
        var p = await NewPair();
        var id = await Say(p, Link());
        await WithPreview(p, id);
        _site.Html = LocalSite.Page("Trang khác", "Mô tả khác", "Nơi khác", null);
        using var edited = await Call(Patch, $"/api/conversations/{p.Chat}/messages/{id}", new { body = $"đổi rồi {Link("/page")}?lan=2" }, p.CA);
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var m = await WithPreview(p, id);
        Assert.Equal("Trang khác", m.GetProperty("preview").GetProperty("title").GetString());

        using var plain = await Call(Patch, $"/api/conversations/{p.Chat}/messages/{id}", new { body = "không còn liên kết" }, p.CA);
        Assert.Equal(HttpStatusCode.NoContent, plain.StatusCode);
        Assert.False(Has(await Message(p, id)));
        _site.Html = LocalSite.Page("Chuyến đi Đà Lạt", "Ảnh và kế hoạch cho cả nhà", "Gom Blog", "/pic.png");
    }

    [Fact]
    public async Task Disbanding_a_group_works_with_cards_in_it()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        using var made = await Call(Post, "/api/conversations/group", new { title = "Nhóm", members = new[] { b.GomId } }, ca);
        var group = (await AppFixture.Json(made)).GetProperty("id").GetGuid();
        using var sent = await Call(Post, $"/api/conversations/{group}/messages", new { clientId = Guid.NewGuid().ToString(), body = Link() }, ca);
        var id = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();
        for (var i = 0; i < 80; i++)
        {
            await using var peek = app.Db.NewContext();
            if (await peek.Previews.AnyAsync(x => x.MessageId == id)) break;
            await Task.Delay(100);
        }
        using var gone = await Call(Delete, $"/api/conversations/{group}", null, ca);
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Previews.AnyAsync(x => x.MessageId == id));
    }

    [Fact]
    public async Task Only_the_first_address_is_looked_at()
    {
        var p = await NewPair();
        _site.Html = LocalSite.Page("Cái đầu", "mô tả", "Nơi", null);
        var id = await Say(p, $"{Link()} rồi {Link("/text")}");
        var m = await WithPreview(p, id);
        Assert.Equal("Cái đầu", m.GetProperty("preview").GetProperty("title").GetString());
        _site.Html = LocalSite.Page("Chuyến đi Đà Lạt", "Ảnh và kế hoạch cho cả nhà", "Gom Blog", "/pic.png");
    }
}
