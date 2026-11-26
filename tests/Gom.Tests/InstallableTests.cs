using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gom.Tests;

/// <summary>The icon, the manifest and the service worker: what makes Gom something that can be added to a home screen.</summary>
public class InstallableTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/sign-in")]
    [InlineData("/settings")]
    [InlineData("/settings/owner")]
    public async Task Every_page_names_its_icon_manifest_and_colour(string path)
    {
        using var response = await factory.CreateClient().GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("<link rel=\"icon\" href=\"/icon.svg\"", html);
        Assert.Contains("<link rel=\"manifest\" href=\"/manifest.webmanifest\">", html);
        Assert.Contains("<link rel=\"apple-touch-icon\"", html);
        Assert.Contains("<meta name=\"theme-color\"", html);
    }

    [Fact]
    public async Task The_manifest_makes_a_standalone_app_with_icons_that_exist_and_are_the_size_they_say()
    {
        var client = factory.CreateClient();
        using var response = await client.GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var m = doc.RootElement;
        Assert.Equal("standalone", m.GetProperty("display").GetString());
        Assert.Equal("/", m.GetProperty("start_url").GetString());
        Assert.Equal("Gom", m.GetProperty("name").GetString());

        var icons = m.GetProperty("icons").EnumerateArray().ToList();
        Assert.Contains(icons, i => i.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(icons, i => i.GetProperty("sizes").GetString() == "512x512" && i.GetProperty("purpose").GetString() == "any");
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "maskable");
        foreach (var icon in icons)
        {
            var bytes = await client.GetByteArrayAsync(icon.GetProperty("src").GetString());
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);   // a PNG
            var side = icon.GetProperty("sizes").GetString()!.Split('x')[0];
            var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            Assert.Equal(int.Parse(side), width);
        }
    }

    [Fact]
    public async Task The_icon_and_the_offline_page_are_served()
    {
        var client = factory.CreateClient();
        using var icon = await client.GetAsync("/icon.svg");
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("image/svg+xml", icon.Content.Headers.ContentType?.MediaType);
        using var offline = await client.GetAsync("/offline.html");
        Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
        var page = await offline.Content.ReadAsStringAsync();
        Assert.Contains("Mất kết nối", page);
        // "Thử lại" loads the same address again through this script; the page policy allows only scripts of our own.
        Assert.Contains("<script src=\"/offline.js\"></script>", page);
        using var script = await client.GetAsync("/offline.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("location.reload()", await script.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_page_policy_lets_the_page_use_its_own_manifest_and_worker_and_nothing_else()
    {
        using var response = await factory.CreateClient().GetAsync("/");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("manifest-src 'self'", csp);
        Assert.Contains("worker-src 'self'", csp);
    }

    [Fact]
    public async Task The_service_worker_is_always_checked_with_the_server_and_leaves_the_api_and_the_socket_alone()
    {
        using var response = await factory.CreateClient().GetAsync("/sw.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        var js = await response.Content.ReadAsStringAsync();
        Assert.Contains("url.pathname.startsWith('/api/')", js);
        Assert.Contains("url.pathname === '/ws'", js);
        Assert.Contains("request.method !== 'GET'", js);
    }

    [Fact]
    public async Task Everything_the_service_worker_keeps_in_advance_exists()
    {
        var client = factory.CreateClient();
        var js = await (await client.GetAsync("/sw.js")).Content.ReadAsStringAsync();
        var kept = Regex.Match(js, @"const KEPT = \[([^\]]*)\]").Groups[1].Value;
        var paths = Regex.Matches(kept, @"'([^']+)'").Select(x => x.Groups[1].Value).ToList();
        Assert.NotEmpty(paths);
        foreach (var path in paths) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }
}
