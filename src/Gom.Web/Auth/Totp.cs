using System.Security.Cryptography;
using System.Text;

namespace Gom.Web.Auth;

/// <summary>
/// Two-step sign-in codes (RFC 6238, as Google Authenticator, Aegis and 2FAS make them): 6 digits from HMAC-SHA1 of a 20-byte key and
/// the 30-second step since 1970. A code one step early or late is taken too (a phone's clock is rarely exact), and a step once used
/// is never taken again.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int KeyBytes = 20;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    public static long StepAt(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for one step (RFC 4226's dynamic truncation).</summary>
    public static string Code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);
        var at = hash[^1] & 0x0F;
        var number = ((hash[at] & 0x7F) << 24) | (hash[at + 1] << 16) | (hash[at + 2] << 8) | hash[at + 3];
        return (number % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step this code belongs to, if it is one of now's, the one before or the one after, and later than <paramref name="lastUsed"/>;
    /// null otherwise. Spaces are allowed ("123 456", as some apps show it).
    /// </summary>
    public static long? Match(byte[] key, string? code, DateTimeOffset now, long lastUsed)
    {
        var digits = (code ?? "").Replace(" ", "", StringComparison.Ordinal);
        if (digits.Length != Digits || !digits.All(char.IsAsciiDigit)) return null;
        var step = StepAt(now);
        for (var s = step - 1; s <= step + 1; s++)
            if (s > lastUsed && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, s)), Encoding.ASCII.GetBytes(digits)))
                return s;
        return null;
    }

    /// <summary>The key as an authenticator app takes it typed in: Base32 (RFC 4648), without padding.</summary>
    public static string Base32(byte[] key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in key)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { text.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) text.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    /// <summary>The key read back from <see cref="Base32"/>; null for anything that is not a whole key.</summary>
    public static byte[]? FromBase32(string? text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        if (text is null) return null;
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var v = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return bytes.Count == KeyBytes ? [.. bytes] : null;
    }

    /// <summary>The address an authenticator app opens or reads from the QR code: "Gom: gomId", this key.</summary>
    public static string Uri(string gomId, byte[] key) =>
        $"otpauth://totp/Gom:{System.Uri.EscapeDataString(gomId)}?secret={Base32(key)}&issuer=Gom&digits={Digits}&period={StepSeconds}";
}
