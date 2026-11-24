namespace Gom.Web.Chat;

/// <summary>A token bucket per sender: a burst is fine, a flood is slowed. In memory, and bounded.</summary>
public sealed class SendLimiter(TimeProvider clock, int burst, double perSecond, int maxEntries = 10_000)
{
    private readonly Dictionary<Guid, (double Tokens, DateTimeOffset At)> _buckets = [];
    private readonly Lock _gate = new();

    public int Count { get { lock (_gate) return _buckets.Count; } }

    /// <summary>Null when the send may go ahead (one token is spent); otherwise how long to wait for the next one.</summary>
    public TimeSpan? TryTake(Guid sender)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var (tokens, at) = _buckets.TryGetValue(sender, out var b) ? b : (burst, now);
            tokens = Math.Min(burst, tokens + (now - at).TotalSeconds * perSecond);
            if (tokens < 1)
            {
                _buckets[sender] = (tokens, now);
                return TimeSpan.FromSeconds((1 - tokens) / perSecond);
            }
            _buckets[sender] = (tokens - 1, now);
            if (_buckets.Count > maxEntries) Trim(now);
            return null;
        }
    }

    /// <summary>Forget senders whose bucket has refilled (they look new anyway); if that is not enough, the older half.</summary>
    private void Trim(DateTimeOffset now)
    {
        foreach (var key in _buckets.Where(e => e.Value.Tokens + (now - e.Value.At).TotalSeconds * perSecond >= burst).Select(e => e.Key).ToList())
            _buckets.Remove(key);
        if (_buckets.Count <= maxEntries) return;
        foreach (var key in _buckets.OrderBy(e => e.Value.At).Take(_buckets.Count / 2).Select(e => e.Key).ToList())
            _buckets.Remove(key);
    }
}
