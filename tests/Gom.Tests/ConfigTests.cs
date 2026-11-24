using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Gom.Tests;

public class ConfigTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void The_shipped_password_work_factor_is_at_least_600000()
    {
        var configured = factory.Services.GetRequiredService<IConfiguration>().GetValue<int>("Auth:PasswordIterations");
        Assert.True(configured >= 600_000, $"Auth:PasswordIterations is {configured}");
    }
}
