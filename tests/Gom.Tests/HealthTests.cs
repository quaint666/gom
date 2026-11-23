using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gom.Tests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Healthz_is_ok_when_the_database_answers()
    {
        var response = await factory.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_is_503_when_the_database_is_down()
    {
        // Port 1 refuses the connection at once, so this stays fast.
        var down = factory.WithWebHostBuilder(b => b.UseSetting(
            "ConnectionStrings:Default", "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2"));

        var response = await down.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
