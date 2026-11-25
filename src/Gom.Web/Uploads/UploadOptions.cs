namespace Gom.Web.Uploads;

/// <summary>Settings for pictures and files ("Uploads" in the configuration). Every limit can be changed without a new build.</summary>
public sealed class UploadOptions
{
    /// <summary>The folder where uploads live. Empty switches uploads off (the endpoints answer 503). On a server this is a volume that is also in the backup.</summary>
    public string Path { get; set; } = "";

    /// <summary>The largest picture accepted, in bytes, before it is made smaller.</summary>
    public long MaxImageBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>How many pictures and files one message may carry.</summary>
    public int MaxPerMessage { get; set; } = 10;

    /// <summary>
    /// A moving picture (a GIF, or a WebP with frames) is kept as it was sent, so it has its own limits: its size in bytes, its frames,
    /// and its pixels counted over every frame (each frame is decoded once to check it is whole).
    /// </summary>
    public long MaxAnimatedBytes { get; set; } = 8 * 1024 * 1024;
    public int MaxFrames { get; set; } = 1000;
    public long MaxAnimatedPixels { get; set; } = 150_000_000;

    /// <summary>The largest file accepted, in bytes. Files are kept as they are.</summary>
    public long MaxFileBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>A picture is scaled down so its longer side is at most this many pixels (never scaled up).</summary>
    public int ImageMaxEdge { get; set; } = 1600;

    /// <summary>The small version shown in a conversation: its longer side, in pixels.</summary>
    public int ThumbEdge { get; set; } = 400;

    /// <summary>WebP quality of the stored picture and of its small version (0 to 100).</summary>
    public int ImageQuality { get; set; } = 80;
    public int ThumbQuality { get; set; } = 70;

    /// <summary>A picture with more pixels than this is refused before it is decoded (a small file can hold a huge picture).</summary>
    public long MaxPixels { get; set; } = 60_000_000;

    /// <summary>
    /// Megabytes of memory that pictures may use at once while they are made smaller, and the same again for uploads being received.
    /// 0 means a share of the memory the container has (35% and 25%).
    /// </summary>
    public int MemoryMegabytes { get; set; }

    /// <summary>How many pictures are decoded and encoded at once (each takes about half a second and some memory). 0 means half the CPU cores.</summary>
    public int ImageSlots { get; set; }

    /// <summary>How long a picture waits for a free slot before the upload is turned away with a 503.</summary>
    public int ImageWaitSeconds { get; set; } = 20;

    /// <summary>A person may upload this many in a burst, and this many per second after that.</summary>
    public int UploadBurst { get; set; } = 12;
    public double UploadPerSecond { get; set; } = 1;

    /// <summary>
    /// How many bytes one person may upload in any 24 hours, as stored (a picture counts after it was made smaller; a copy counts).
    /// It stops a person, or a stolen session, filling the disk. 0 switches it off.
    /// </summary>
    public long DailyBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>An upload that is not put in a message within this many hours is deleted.</summary>
    public int PendingHours { get; set; } = 1;
}
