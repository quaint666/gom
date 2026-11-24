namespace Gom.Web.Previews;

/// <summary>Link previews ("LinkPreviews" in the configuration). Off unless switched on: they make this server fetch pages that people post.</summary>
public sealed class PreviewOptions
{
    /// <summary>LinkPreviews__Enabled. Needs the server to reach the internet. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>The longest a whole fetch (every redirect, the page and its picture counted separately) may take.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>The most bytes of a page that are read; a longer page is cut there (its head is what is wanted).</summary>
    public int MaxPageBytes { get; set; } = 512 * 1024;

    /// <summary>The most bytes of a preview picture that are read.</summary>
    public int MaxImageBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>How many redirects are followed, each one checked again as if it were the first address.</summary>
    public int MaxRedirects { get; set; } = 3;

    /// <summary>The only ports a preview may be fetched from.</summary>
    public int[] AllowedPorts { get; set; } = [80, 443];

    /// <summary>How many fetches run at once, and how many wait; past that a link simply gets no preview.</summary>
    public int Concurrency { get; set; } = 2;
    public int QueueLength { get; set; } = 64;
}
