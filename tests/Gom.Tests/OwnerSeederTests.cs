using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gom.Tests;

public class ListLogger : ILogger<OwnerSeeder>
{
    public List<(LogLevel Level, string Message)> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add((logLevel, formatter(state, exception)));
}

public class OwnerSeederTests : IAsyncLifetime
{
    private readonly TestDatabase _database = new();
    private readonly ListLogger _log = new();
    private readonly PasswordService _passwords = new(1_000);

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    private OwnerSeeder Seeder(string? gomId, string? password) =>
        new(new OwnerOptions { GomId = gomId, Password = password }, _passwords, TimeProvider.System, _log);

    [Fact]
    public async Task Nothing_configured_means_nothing_happens()
    {
        await using var db = _database.NewContext();
        Assert.Equal(SeedResult.NotConfigured, await Seeder(null, null).SeedAsync(db, default));
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [Fact]
    public async Task An_empty_database_gets_the_owner()
    {
        await using var db = _database.NewContext();

        var result = await Seeder("the_boss", "a long enough password").SeedAsync(db, default);

        Assert.Equal(SeedResult.Created, result);
        var owner = await db.Users.SingleAsync();
        Assert.True(owner.IsOwner);
        Assert.Equal("the_boss", owner.GomId);
        Assert.Equal("the_boss", owner.DisplayName);
        Assert.Equal(PasswordCheck.Ok, _passwords.Verify(owner.PasswordHash, "a long enough password"));
    }

    [Fact]
    public async Task Running_again_changes_nothing_and_reminds_you_to_remove_the_password()
    {
        await using var db = _database.NewContext();
        await Seeder("the_boss", "a long enough password").SeedAsync(db, default);
        var before = (await db.Users.SingleAsync()).PasswordHash;

        var result = await Seeder("someone_else", "a different password").SeedAsync(db, default);

        Assert.Equal(SeedResult.AlreadySeeded, result);
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(before, (await db.Users.SingleAsync()).PasswordHash);
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("Owner__Password"));
    }

    [Fact]
    public async Task With_no_password_left_in_the_configuration_there_is_no_warning()
    {
        await using var db = _database.NewContext();
        await Seeder("the_boss", "a long enough password").SeedAsync(db, default);
        _log.Lines.Clear();

        await Seeder(null, null).SeedAsync(db, default);

        Assert.Empty(_log.Lines);
    }

    [Theory]
    [InlineData("ab", "a long enough password")]
    [InlineData("admin", "a long enough password")]
    [InlineData("the_boss", "short")]
    [InlineData("the_boss", null)]
    [InlineData(null, "a long enough password")]
    public async Task A_half_set_or_invalid_configuration_stops_the_start_with_a_clear_message(string? gomId, string? password)
    {
        await using var db = _database.NewContext();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Seeder(gomId, password).SeedAsync(db, default));

        Assert.Contains("Owner__", ex.Message);
        Assert.Equal(0, await db.Users.CountAsync());
    }
}

public class OwnerSeedingAtStartTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    [Fact]
    public async Task Starting_the_app_with_the_owner_configured_creates_the_owner()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", database.ConnectionString);
            b.UseSetting("Auth:PasswordIterations", "1000");
            b.UseSetting("Owner:GomId", "boot_owner");
            b.UseSetting("Owner:Password", "a long enough password");
        });
        using var client = factory.CreateClient(); // starting the host runs the seeding

        await using var db = database.NewContext();
        Assert.Equal("boot_owner", (await db.Users.SingleAsync()).GomId);
    }
}
