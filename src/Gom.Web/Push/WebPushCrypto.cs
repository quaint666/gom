using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gom.Web.Push;

/// <summary>
/// What a push carries, encrypted for one browser (RFC 8291, "aes128gcm" from RFC 8188), and the signature that says which server sent
/// it (VAPID, RFC 8292). Written here rather than taken from a library: every step is in .NET, and a test checks the result byte for
/// byte against RFC 8291's worked example.
/// </summary>
public static class WebPushCrypto
{
    private const int RecordSize = 4096;
    private static readonly byte[] CekInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");
    private static readonly byte[] KeyInfoLabel = Encoding.ASCII.GetBytes("WebPush: info\0");

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> for the browser whose public key and secret are given. A fresh key pair and salt are made
    /// each time; a test passes the example's own.
    /// </summary>
    public static byte[] Encrypt(byte[] uaPublic, byte[] authSecret, byte[] plaintext, ECDiffieHellman? sender = null, byte[]? salt = null)
    {
        if (plaintext.Length > RecordSize - 16 - 1 - 86) throw new ArgumentException("Too long for one record.", nameof(plaintext));
        using var own = sender is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var asKey = sender ?? own!;
        salt ??= RandomNumberGenerator.GetBytes(16);

        using var ua = ECDiffieHellman.Create(PublicParameters(uaPublic));   // throws when the point is not on P-256
        var asPublic = PublicBytes(asKey);
        var ecdhSecret = asKey.DeriveRawSecretAgreement(ua.PublicKey);

        // IKM = HKDF(auth_secret, ecdh_secret, "WebPush: info" 0x00 ua_public as_public, 32)
        var keyInfo = Concat(KeyInfoLabel, uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, CekInfo);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, NonceInfo);

        // One record: the words, then 0x02 (the last record, no padding).
        var record = new byte[plaintext.Length + 1];
        plaintext.CopyTo(record, 0);
        record[^1] = 2;
        var cipher = new byte[record.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(cek, 16)) gcm.Encrypt(nonce, record, cipher, tag);

        // Header: salt (16), record size (4, big-endian), key id length (1), key id = the sender's public key (65).
        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return Concat(header, cipher, tag);
    }

    /// <summary>
    /// The Authorization header value: a token signed with the server's key, for the push service at <paramref name="endpoint"/>'s
    /// origin, good for 12 hours.
    /// </summary>
    public static string VapidHeader(string endpoint, string subject, string publicKey, string privateKey, DateTimeOffset now)
    {
        var audience = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var head = Base64Url.EncodeToString("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = (now + TimeSpan.FromHours(12)).ToUnixTimeSeconds(), sub = subject }));
        var signed = $"{head}.{claims}";
        var pub = Base64Url.DecodeFromChars(publicKey);
        var p = PublicParameters(pub);
        p.D = Base64Url.DecodeFromChars(privateKey);
        using var ecdsa = ECDsa.Create(p);
        var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={signed}.{Base64Url.EncodeToString(signature)}, k={publicKey}";
    }

    /// <summary>A new key pair for the server, base64url: (public 65 bytes, private 32 bytes).</summary>
    public static (string Public, string Private) NewVapidKeys()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(includePrivateParameters: true);
        return (Base64Url.EncodeToString(Concat([4], p.Q.X!, p.Q.Y!)), Base64Url.EncodeToString(p.D!));
    }

    /// <summary>Whether these bytes are a P-256 public key a browser could have sent (uncompressed, on the curve).</summary>
    public static bool IsPublicKey(byte[] key)
    {
        if (key.Length != 65 || key[0] != 4) return false;
        try { using var _ = ECDiffieHellman.Create(PublicParameters(key)); return true; }
        catch (CryptographicException) { return false; }
    }

    private static ECParameters PublicParameters(byte[] uncompressed) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..65] },
    };

    private static byte[] PublicBytes(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        return Concat([4], q.X!, q.Y!);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }
}
