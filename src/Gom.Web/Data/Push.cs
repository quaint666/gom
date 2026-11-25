namespace Gom.Web.Data;

/// <summary>How much a notification on this device says.</summary>
public enum PushLevel { Name = 0, Nothing = 1, Words = 2 }

/// <summary>
/// One device's notifications: where its push service takes them, and the keys to encrypt them for that browser. It belongs to one
/// sign-in session, so signing out (or the session running out) ends the device's notifications too.
/// </summary>
public class PushSubscription
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    /// <summary>The push service's address for this browser; only known push services are accepted (see PushOptions.Hosts).</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>The browser's public key (P-256, 65 bytes uncompressed) and its 16-byte secret, as RFC 8291 asks for.</summary>
    public byte[] P256dh { get; set; } = [];
    public byte[] Auth { get; set; } = [];
    public PushLevel Level { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
