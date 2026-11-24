using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gom.Tests;

public class DeploymentTests(AppFixture app) : IClassFixture<AppFixture>
{
    [Theory]
    [InlineData("/healthz")]
    [InlineData("/")]
    [InlineData("/sign-in")]
    [InlineData("/friends")]
    public async Task A_monitor_may_ask_with_HEAD(string path)
    {
        using var response = await app.Call(HttpMethod.Head, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The page policy is <c>style-src 'self'</c>: a style attribute is refused, also the one a template writes on its first draw.
    /// Styles that depend on a value go through the style object after drawing (see placeMenu in chat.ts).
    /// </summary>
    [Fact]
    public void No_script_writes_an_inline_style_attribute()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gom.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var scripts = Path.Combine(dir.FullName, "src", "Gom.Web", "Scripts");
        Assert.True(Directory.Exists(scripts));
        var offenders = Directory.EnumerateFiles(scripts, "*.ts", SearchOption.AllDirectories)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"\sstyle="))
            .Select(f => Path.GetRelativePath(dir.FullName, f)).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public async Task A_method_an_address_does_not_take_is_a_405()
    {
        using var response = await app.Call(HttpMethod.Put, "/healthz");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Every_answer_says_which_features_of_the_device_the_page_may_not_use()
    {
        using var response = await app.Call(HttpMethod.Get, "/sign-in");
        Assert.Equal("camera=(), microphone=(), geolocation=()", response.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact]
    public async Task A_script_is_checked_with_the_server_each_time_and_a_page_is_never_stored()
    {
        using var script = await app.Call(HttpMethod.Get, "/js/main.js");
        using var page = await app.Call(HttpMethod.Get, "/sign-in");
        Assert.Equal("no-cache", script.Headers.CacheControl?.ToString());
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_font_may_be_kept_for_a_week()
    {
        using var font = await app.Call(HttpMethod.Get, "/fonts/BeVietnamPro-Regular.woff2");
        Assert.Equal(HttpStatusCode.OK, font.StatusCode);
        Assert.Equal("public, max-age=604800", font.Headers.CacheControl?.ToString());
    }

    [Fact]
    public void The_database_commands_are_not_written_to_the_log_in_production()
    {
        // At Information, EF prints every statement; the level is set in appsettings.json for every environment.
        var level = app.Factory.Services.GetRequiredService<IConfiguration>()["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"];
        Assert.Equal("Warning", level);
    }
}

/// <summary>What the app believes about the visitor behind a proxy chain.</summary>
public class ProxyTests(AppFixture app) : IClassFixture<AppFixture>
{
    /// <summary>Shows the address the app ends up with, after the forwarded-headers step, in a response header.</summary>
    private sealed class ShowAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, run) =>
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["X-Seen"] = $"{context.Connection.RemoteIpAddress}|{context.Request.Scheme}";
                    return Task.CompletedTask;
                });
                await run();
            });
            next(builder);
        };
    }

    private async Task<string> Seen(string? hops)
    {
        var factory = app.Factory.WithWebHostBuilder(b =>
        {
            if (hops is not null) b.UseSetting("ForwardedHeaders:Hops", hops);
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter, ShowAddress>());
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("X-Test-Ip", "10.9.9.9");
        request.Headers.Add("X-Forwarded-For", "1.2.3.4, 5.6.7.8");
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await client.SendAsync(request);
        return response.Headers.GetValues("X-Seen").Single();
    }

    [Fact]
    public async Task With_no_proxy_the_forwarded_headers_are_not_believed() =>
        Assert.Equal("10.9.9.9|http", await Seen(null));

    [Fact]
    public async Task With_two_proxies_the_visitor_is_the_second_address_from_the_right() =>
        Assert.Equal("1.2.3.4|https", await Seen("2"));

    [Fact]
    public async Task With_one_proxy_the_visitor_is_the_last_address() =>
        Assert.Equal("5.6.7.8|https", await Seen("1"));

    [Fact]
    public void A_negative_number_of_proxies_stops_the_app_at_start()
    {
        var factory = app.Factory.WithWebHostBuilder(b => b.UseSetting("ForwardedHeaders:Hops", "-1"));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
}
