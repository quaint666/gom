using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gom.Tests;

/// <summary>A throwaway database, migrated from scratch, so a test class never sees another's rows.</summary>
public sealed class TestDatabase : IAsyncLifetime
{
    private readonly string _name = "gom_test_" + Guid.NewGuid().ToString("N");
    private string _admin = "";

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _admin = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? throw new InvalidOperationException("ConnectionStrings__Default is not set.");
        await using (var c = new NpgsqlConnection(_admin))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_name}", c);
            await cmd.ExecuteNonQueryAsync();
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(_admin) { Database = _name, Pooling = false }.ConnectionString;
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var c = new NpgsqlConnection(_admin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)", c);
        await cmd.ExecuteNonQueryAsync();
    }

    public GomDbContext NewContext() =>
        new(new DbContextOptionsBuilder<GomDbContext>().UseNpgsql(ConnectionString).Options);
}
