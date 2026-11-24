using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Auth;

public sealed class OwnerOptions
{
    /// <summary>Owner__GomId in the environment.</summary>
    public string? GomId { get; set; }
    /// <summary>Owner__Password in the environment. Used once, on an empty database; remove it afterwards.</summary>
    public string? Password { get; set; }
}

public enum SeedResult { NotConfigured, Created, AlreadySeeded }

/// <summary>Creates the owner on first start from configuration. There is no web page for it, so nobody can race you to it.</summary>
public sealed class OwnerSeeder(OwnerOptions options, PasswordService passwords, TimeProvider clock, ILogger<OwnerSeeder> log)
{
    public async Task<SeedResult> SeedAsync(GomDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.GomId) && string.IsNullOrEmpty(options.Password)) return SeedResult.NotConfigured;

        if (await db.Users.AnyAsync(ct))
        {
            if (!string.IsNullOrEmpty(options.Password))
                log.LogWarning("Owner__Password is still set but an account already exists, so it was ignored. Remove it from the configuration.");
            return SeedResult.AlreadySeeded;
        }

        var gomId = options.GomId?.Trim().ToLowerInvariant();
        if (!GomId.IsValid(gomId))
            throw new InvalidOperationException("Owner__GomId is missing or not a valid Gom ID (lowercase letters, digits and underscore, 3 to 20 characters, not a reserved word).");
        if (options.Password is null || PasswordPolicy.Check(options.Password) != PasswordProblem.None)
            throw new InvalidOperationException("Owner__Password is missing or not allowed (10 to 128 characters and not a very common password).");

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), GomId = gomId!, DisplayName = gomId!, PasswordHash = passwords.Hash(options.Password),
            IsOwner = true, CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Created the owner account {GomId}. Remove Owner__Password from the configuration now.", gomId);
        return SeedResult.Created;
    }
}

/// <summary>Runs the seeding once at start, only if the owner is configured.</summary>
public sealed class OwnerSeedingService(IServiceScopeFactory scopes, OwnerSeeder seeder) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await seeder.SeedAsync(scope.ServiceProvider.GetRequiredService<GomDbContext>(), ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
