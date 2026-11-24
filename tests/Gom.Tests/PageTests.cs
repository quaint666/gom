using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gom.Tests;

public class PageTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    public static TheoryData<string, string> Pages => new()
    {
        { "/", "home" }, { "/c/6f1d0b5e-0000-4000-8000-000000000000", "home" }, { "/sign-in", "signin" }, { "/join/some-token", "join" }, { "/reset/some-token", "reset" },
        { "/settings", "settings" }, { "/friends", "friends" }, { "/groups/new", "newgroup" }, { "/groups/6f1d0b5e-0000-4000-8000-000000000000", "group" }, { "/add/some_id", "add" }, { "/owner", "owner" },
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

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Each_page_follows_the_system_colour_scheme_and_sets_it_before_the_styles(string path, string page)
    {
        _ = page;
        var html = await (await factory.CreateClient().GetAsync(path)).Content.ReadAsStringAsync();

        Assert.Contains("<meta name=\"color-scheme\" content=\"light dark\">", html);
        Assert.True(html.IndexOf("/theme-init.js", StringComparison.Ordinal) is var script && script > 0
            && script < html.IndexOf("/css/gom.css", StringComparison.Ordinal), "theme-init.js must come before the stylesheet");
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
