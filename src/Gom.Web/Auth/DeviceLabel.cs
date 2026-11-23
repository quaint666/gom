namespace Gom.Web.Auth;

/// <summary>A short label such as "Chrome on Android" from the User-Agent. Only the label is kept, never the raw header.</summary>
public static class DeviceLabel
{
    public const string Unknown = "Unknown device";

    public static string From(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return Unknown;
        var ua = userAgent.Length > 512 ? userAgent[..512] : userAgent;

        // Order matters: Edge and Chrome on phones also say "Safari", Chrome on Android also says "Linux".
        var browser =
            ua.Contains("Edg/") || ua.Contains("EdgA/") || ua.Contains("EdgiOS/") ? "Edge" :
            ua.Contains("OPR/") ? "Opera" :
            ua.Contains("Firefox/") || ua.Contains("FxiOS/") ? "Firefox" :
            ua.Contains("Chrome/") || ua.Contains("CriOS/") ? "Chrome" :
            ua.Contains("Safari/") ? "Safari" : null;

        var os =
            ua.Contains("Android") ? "Android" :
            ua.Contains("iPhone") ? "iPhone" :
            ua.Contains("iPad") ? "iPad" :
            ua.Contains("Windows") ? "Windows" :
            ua.Contains("Macintosh") ? "Mac" :
            ua.Contains("CrOS") ? "ChromeOS" :
            ua.Contains("Linux") ? "Linux" : null;

        return browser is null || os is null ? Unknown : $"{browser} on {os}";
    }
}
