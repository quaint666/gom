using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gom.Tests;

public class ConfigTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void The_shipped_password_work_factor_is_at_least_600000()
    {
        var configured = factory.Services.GetRequiredService<IConfiguration>().GetValue<int>("Auth:PasswordIterations");
        Assert.True(configured >= 600_000, $"Auth:PasswordIterations is {configured}");
    }

    /// <summary>Npgsql's default tries Kerberos encryption first, which crashed the app inside libgssapi_krb5 (decisions, 2026-11-26).</summary>
    [Fact]
    public void The_database_connection_never_tries_kerberos_encryption()
    {
        using var scope = factory.Services.CreateScope();
        var used = scope.ServiceProvider.GetRequiredService<GomDbContext>().Database.GetConnectionString();
        Assert.Equal(GssEncryptionMode.Disable, new NpgsqlConnectionStringBuilder(used).GssEncryptionMode);
    }

    /// <summary>Npgsql's default pool (100) is also PostgreSQL's default most connections: a pile-up behind a lock took them all (decisions, 2026-11-28).</summary>
    [Fact]
    public void The_database_pool_is_capped_at_30_unless_the_connection_string_names_a_size()
    {
        using var scope = factory.Services.CreateScope();
        var used = scope.ServiceProvider.GetRequiredService<GomDbContext>().Database.GetConnectionString();
        Assert.Equal(30, new NpgsqlConnectionStringBuilder(used).MaxPoolSize);

        Assert.Equal(30, new NpgsqlConnectionStringBuilder(ConnectionSettings.For("Host=db;Database=gom")).MaxPoolSize);
        Assert.Equal(50, new NpgsqlConnectionStringBuilder(ConnectionSettings.For("Host=db;Database=gom;Maximum Pool Size=50")).MaxPoolSize);
        Assert.Equal(100, new NpgsqlConnectionStringBuilder(ConnectionSettings.For("Host=db;Database=gom;MaxPoolSize=100")).MaxPoolSize);
    }
}
