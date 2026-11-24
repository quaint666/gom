using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Gom.Web.Auth;

public enum PasswordCheck { Wrong, Ok, OkRehash }

public sealed class AuthOptions
{
    /// <summary>PBKDF2 iterations for new hashes. Set in configuration (Auth:PasswordIterations).</summary>
    public int PasswordIterations { get; set; }
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
