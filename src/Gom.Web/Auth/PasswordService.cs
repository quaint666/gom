using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Gom.Web.Auth;

public enum PasswordCheck { Wrong, Ok, OkRehash }

public sealed class AuthOptions
{
    /// <summary>PBKDF2 iterations for new hashes. Set in configuration (Auth:PasswordIterations).</summary>
    public int PasswordIterations { get; set; }

    /// <summary>Origins allowed to make state-changing requests, such as https://gom.example. Empty means none, so nothing can change.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>A session lasts this long after its last use (sliding)...</summary>
    public int SessionDays { get; set; } = 30;

    /// <summary>...but never longer than this after sign-in.</summary>
    public int SessionMaxDays { get; set; } = 90;

    /// <summary>How many password hashes may run at once; 0 means half the cores.</summary>
    public int MaxConcurrentHashes { get; set; }

    /// <summary>How long a sign-in waits for a free hashing slot before answering 503.</summary>
    public int HashWaitSeconds { get; set; } = 2;

    /// <summary>How long an invite link works.</summary>
    public int InviteDays { get; set; } = 7;

    /// <summary>How many people one invite lets in unless the owner chose otherwise.</summary>
    public int InviteMaxUses { get; set; } = 10;

    /// <summary>How long a password-reset link works.</summary>
    public int ResetHours { get; set; } = 24;
}

/// <summary>Hashes and checks passwords with ASP.NET's PBKDF2 hasher, using the iteration count from configuration.</summary>
public sealed class PasswordService
{
    // The hasher needs a user object but never reads it.
    private static readonly object NoUser = new();

    private readonly PasswordHasher<object> _hasher;
    private readonly Lazy<string> _dummy;

    public PasswordService(int iterations)
    {
        _hasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = iterations }));
        _dummy = new Lazy<string>(() => _hasher.HashPassword(NoUser, Guid.NewGuid().ToString("N")));
    }

    public PasswordService(IOptions<AuthOptions> options) : this(options.Value.PasswordIterations) { }

    public string Hash(string password) => _hasher.HashPassword(NoUser, password);

    public PasswordCheck Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(NoUser, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Ok,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.OkRehash,
            _ => PasswordCheck.Wrong,
        };

    /// <summary>Does the same work as a real check, for a Gom ID that does not exist, so timing does not reveal it.</summary>
    public void BurnTime(string password) => _hasher.VerifyHashedPassword(NoUser, _dummy.Value, password);
}
