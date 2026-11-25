namespace Gom.Web.Push;

/// <summary>
/// Settings for notifications ("Push" in the configuration). Without both keys, push is off: the endpoints answer 404 and nothing is
/// sent. A key pair comes from <c>Gom.Web --vapid-keys</c>; the private key is a secret.
/// </summary>
public sealed class PushOptions
{
    /// <summary>The server's public key (VAPID), base64url, P-256 uncompressed (65 bytes). Browsers get it to subscribe.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>The matching private key, base64url (32 bytes). It signs every push, so push services know who sends.</summary>
    public string PrivateKey { get; set; } = "";

    /// <summary>Who to contact about this server, for the push services: a <c>mailto:</c> or <c>https:</c> address.</summary>
    public string Subject { get; set; } = "";

    /// <summary>
    /// The push services a browser may name. The server sends to whatever address a device registers, so any other host is refused: a
    /// made-up address would otherwise aim the server's requests inside its own network. "*." allows any name under it.
    /// </summary>
    /// "jmt17.google.com" is Google's staging service: every browser that does not say it is a stable release gets it, which is any
    /// Chromium build (Fedora's, for one) and Chrome Beta, Dev and Canary (components/push_messaging/push_messaging_utils.cc).
    public List<string> Hosts { get; set; } = ["fcm.googleapis.com", "jmt17.google.com", "*.push.services.mozilla.com", "*.push.apple.com", "*.notify.windows.com"];

    /// <summary>Pushes waiting to be sent; when full, a new one is dropped (the message itself is safe in the database).</summary>
    public int QueueSize { get; set; } = 512;

    /// <summary>How long one push service may take to answer.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>How long a push service keeps a notification for a device that is off.</summary>
    public int TtlSeconds { get; set; } = 86_400;

    public bool Enabled => PublicKey.Length > 0 && PrivateKey.Length > 0;

    /// <summary>Whether a device may register this address: https, the standard port, and a host on the list.</summary>
    public bool Allows(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        return Hosts.Any(h => h.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(h[1..], StringComparison.Ordinal)   // "*.x" is ".x" at the end: a name under x, never x itself or "evilx"
            : host == h);
    }
}
