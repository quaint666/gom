namespace Gom.Web.Chat.Live;

/// <summary>
/// How fast one socket may ask to catch up: a burst, then a steady pace. Each socket owns one, used only by its receive loop, so
/// nothing is shared and nothing needs a lock; it goes away with the socket.
/// </summary>
public sealed class SyncBucket(TimeProvider clock, int burst, double perSecond)
{
    private double _tokens = burst;
    private DateTimeOffset _at = clock.GetUtcNow();

    /// <summary>True when the request may go ahead (one token is spent).</summary>
    public bool TryTake()
    {
        var now = clock.GetUtcNow();
        _tokens = Math.Min(burst, _tokens + Math.Max(0, (now - _at).TotalSeconds) * perSecond);
        _at = now;
        if (_tokens < 1) return false;
        _tokens--;
        return true;
    }
}
