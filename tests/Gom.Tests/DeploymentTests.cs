using System.Net;
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
