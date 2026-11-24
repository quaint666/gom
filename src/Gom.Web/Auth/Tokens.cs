using System.Security.Cryptography;
using System.Text;

namespace Gom.Web.Auth;

/// <summary>Random tokens for sessions and links. Only the SHA-256 hash is stored; the token itself exists once, in the cookie or the link.</summary>
public static class Tokens
{
    public readonly record struct Issued(string Token, byte[] Hash);

    public static Issued New()
    {
        // 32 random bytes is 256 bits, so a fast hash is enough: there is nothing to guess.
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new Issued(token, Hash(token));
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
