using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gom.Tests;

public class PageTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    public static TheoryData<string, string> Pages => new()
    {
        { "/", "home" }, { "/c/6f1d0b5e-0000-4000-8000-000000000000", "home" }, { "/sign-in", "signin" }, { "/join/some-token", "join" }, { "/sign-up", "signup" }, { "/reset/some-token", "reset" },
        { "/settings", "settings" }, { "/friends", "friends" }, { "/groups/new", "newgroup" }, { "/groups/6f1d0b5e-0000-4000-8000-000000000000", "group" }, { "/add/some_id", "add" }, { "/owner", "owner" }, { "/owner/storage", "storage" },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Each_page_is_served_as_html_for_its_script(string path, string page)
    {
        using var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"data-page=\"{page}\"", html);
        Assert.Contains("<html lang=\"vi\">", html);
    }

    /// <summary>The title is the page's to set, in its language (the script's string tables); until then every page is "Gom".</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Every_page_starts_titled_Gom(string path, string page)
    {
        _ = page;
        var html = await (await factory.CreateClient().GetAsync(path)).Content.ReadAsStringAsync();
        Assert.Contains("<title>Gom</title>", html);
    }

    private static async Task<JsonElement> LimitsOf(HttpClient client, string path)
    {
        var html = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
        var attribute = Regex.Match(html, "<body data-page=\"[a-z]+\" data-limits=\"([^\"]*)\" data-video-script=\"/js/video(\\.[0-9a-f]{10})?\\.js\">");
        Assert.True(attribute.Success);   // the video converter's address, fingerprinted when the build made a copy
        return JsonDocument.Parse(WebUtility.HtmlDecode(attribute.Groups[1].Value)).RootElement.Clone();
    }

    /// <summary>The numbers are written out here, not read from the code, so a changed limit fails a test (see the page tests' setup too).</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Each_page_carries_the_server_s_limits_before_anyone_signs_in(string path, string page)
    {
        _ = page;
        var l = await LimitsOf(factory.CreateClient(), path);
        Assert.Equal(4000, l.GetProperty("messageChars").GetInt32());
        Assert.Equal(10, l.GetProperty("attachments").GetInt32());
        Assert.Equal(10 * 1024 * 1024, l.GetProperty("imageBytes").GetInt64());
        Assert.Equal(25 * 1024 * 1024, l.GetProperty("fileBytes").GetInt64());
        Assert.Equal(45 * 1024 * 1024, l.GetProperty("videoBytes").GetInt64());
        Assert.Equal(1024 * 1024, l.GetProperty("posterBytes").GetInt64());
        Assert.Equal(3, l.GetProperty("pins").GetInt32());
        Assert.Equal(60, l.GetProperty("nameChars").GetInt32());
        Assert.Equal(10, l.GetProperty("passwordMin").GetInt32());
        Assert.Equal(128, l.GetProperty("passwordMax").GetInt32());
        Assert.Equal(3, l.GetProperty("gomIdMin").GetInt32());
        Assert.Equal(20, l.GetProperty("gomIdMax").GetInt32());
        Assert.Equal(100, l.GetProperty("searchChars").GetInt32());
        Assert.Equal(100, l.GetProperty("quoteChars").GetInt32());
        Assert.Equal(200, l.GetProperty("listPreviewChars").GetInt32());
        Assert.Equal(30, l.GetProperty("inviteDays").GetInt32());
        Assert.Equal(100, l.GetProperty("inviteUses").GetInt32());
        Assert.Equal(200, l.GetProperty("noteChars").GetInt32());
        Assert.Equal(7, l.GetProperty("inviteDefaultDays").GetInt32());
        Assert.Equal(10, l.GetProperty("inviteDefaultUses").GetInt32());
    }

    [Fact]
    public async Task A_limit_changed_in_the_configuration_reaches_the_pages()
    {
        using var changed = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Chat:MaxChars", "1234");
            b.UseSetting("Uploads:MaxPerMessage", "4");
            b.UseSetting("Auth:InviteDays", "3");
        });
        var l = await LimitsOf(changed.CreateClient(), "/sign-in");
        Assert.Equal(1234, l.GetProperty("messageChars").GetInt32());
        Assert.Equal(4, l.GetProperty("attachments").GetInt32());
        Assert.Equal(3, l.GetProperty("inviteDefaultDays").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Each_page_follows_the_system_colour_scheme_and_sets_it_before_the_styles(string path, string page)
    {
        _ = page;
        var html = await (await factory.CreateClient().GetAsync(path)).Content.ReadAsStringAsync();

        Assert.Contains("<meta name=\"color-scheme\" content=\"light dark\">", html);
        // The files may carry their fingerprint (theme-init.<hash>.js) once the site is built.
        var script = System.Text.RegularExpressions.Regex.Match(html, @"/theme-init(\.[0-9a-f]{10})?\.js").Index;
        var styles = System.Text.RegularExpressions.Regex.Match(html, @"/css/gom(\.[0-9a-f]{10})?\.css").Index;
        Assert.True(script > 0 && styles > 0 && script < styles, "theme-init.js must come before the stylesheet");
        Assert.Contains("name=\"viewport\"", html);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task No_page_has_inline_script_or_style_so_the_policy_can_forbid_them(string path, string page)
    {
        _ = page;
        var html = await (await factory.CreateClient().GetAsync(path)).Content.ReadAsStringAsync();

        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"<style|\sstyle=|\sonclick=|\sonload=", RegexOptions.IgnoreCase), html);
    }

    [Fact]
    public async Task Pages_carry_a_strict_policy_and_are_never_cached_or_leaked_through_the_referrer()
    {
        using var response = await factory.CreateClient().GetAsync("/join/secret-looking-token");

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("base-uri 'none'", csp);
        Assert.Contains("media-src 'self';", csp);   // videos play from this site only, never from a blob or another site
        Assert.DoesNotContain("unsafe", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task API_answers_are_never_cached_either()
    {
        using var response = await factory.CreateClient().GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/css/gom.css", "text/css")]
    [InlineData("/theme-init.js", "text/javascript")]
    [InlineData("/fonts/BeVietnamPro-Regular.woff2", "font/woff2")]
    [InlineData("/fonts/BeVietnamPro-SemiBold.woff2", "font/woff2")]
    [InlineData("/fonts/BeVietnamPro-Bold.woff2", "font/woff2")]
    public async Task Static_files_are_served(string path, string type)
    {
        using var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_unknown_path_is_a_404()
    {
        using var response = await factory.CreateClient().GetAsync("/no/such/page");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_theme_script_only_reads_the_stored_choice_and_cannot_throw()
    {
        var js = await factory.CreateClient().GetStringAsync("/theme-init.js");
        Assert.Contains("try", js);
        Assert.Contains("color-scheme", js);
        Assert.DoesNotContain("fetch", js);
    }
}
