namespace Gom.Web.Auth;

/// <summary>
/// Slows repeated failed sign-ins, per address and per Gom ID, without ever locking anyone out.
/// After 3 failures the wait doubles (1, 2, 4 ... seconds) up to 60 seconds, and failures are forgotten after 15 quiet minutes.
/// It lives in memory, so a restart clears it; that is accepted on a single node.
/// </summary>
public sealed class SignInThrottle(TimeProvider clock, int maxEntries = 10_000)
{
    private const int FreeFailures = 3;
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Forget = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Lock _lock = new();

    private struct Entry { public int Count; public DateTimeOffset LastFailure; }

    public int EntryCount { get { lock (_lock) return _entries.Count; } }

    /// <summary>How long this address and this ID must still wait before another try.</summary>
    public TimeSpan Wait(string ip, string gomId)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            return Max(WaitFor("ip:" + ip, now), WaitFor("id:" + gomId, now));
        }
    }

    /// <summary>
    /// Starts an attempt: how long to wait if one must, else zero, and the attempt is counted as a failure already. Counted before the
    /// password is checked, so a burst of attempts sent together meets the wait too (counted after, all of them passed the check at
    /// once: 18 of 30 were checked where 3 should have been). A right password takes its count back (<see cref="Succeeded(string, string)"/>),
    /// and so does an attempt that was never checked (<see cref="Undo"/>).
    /// </summary>
    public TimeSpan Begin(string ip, string gomId)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            var wait = Max(WaitFor("ip:" + ip, now), WaitFor("id:" + gomId, now));
            if (wait > TimeSpan.Zero) return wait;
            Count("ip:" + ip, now);
            Count("id:" + gomId, now);
            if (_entries.Count > maxEntries) Trim(now);
            return TimeSpan.Zero;
        }
    }

    /// <summary>An attempt <see cref="Begin"/> counted that was never checked (no hashing slot): it does not count.</summary>
    public void Undo(string ip, string gomId)
    {
        lock (_lock) { TakeBack("ip:" + ip); TakeBack("id:" + gomId); }
    }

    /// <summary>
    /// A right password after <see cref="Begin"/>: that ID is cleared, and the address gets this attempt back but keeps the rest (clearing the
    /// address would let an attacker reset it with an account of their own; keeping this one would slow a family behind one address).
    /// </summary>
    public void Succeeded(string ip, string gomId)
    {
        lock (_lock) { _entries.Remove("id:" + gomId); TakeBack("ip:" + ip); }
    }

    private void TakeBack(string key)
    {
        if (!_entries.TryGetValue(key, out var e)) return;
        if (--e.Count <= 0) _entries.Remove(key); else _entries[key] = e;
    }

    /// <summary>A correct password clears that ID only. Clearing the address too would let an attacker reset it with an account of their own.</summary>
    public void Succeeded(string gomId)
    {
        lock (_lock) _entries.Remove("id:" + gomId);
    }

    private TimeSpan WaitFor(string key, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(key, out var e) || now - e.LastFailure > Forget || e.Count < FreeFailures) return TimeSpan.Zero;
        var backoff = TimeSpan.FromSeconds(Math.Min(MaxWait.TotalSeconds, 1 << Math.Min(e.Count - FreeFailures, 6)));
        var remaining = e.LastFailure + backoff - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void Count(string key, DateTimeOffset now)
    {
        _entries.TryGetValue(key, out var e);
        if (now - e.LastFailure > Forget) e.Count = 0;
        e.Count++;
        e.LastFailure = now;
        _entries[key] = e;
    }

    // Bounds memory when someone invents IDs: drop what is already forgotten, then the older half.
    private void Trim(DateTimeOffset now)
    {
        foreach (var key in _entries.Where(kv => now - kv.Value.LastFailure > Forget).Select(kv => kv.Key).ToList())
            _entries.Remove(key);
        if (_entries.Count <= maxEntries) return;
        foreach (var key in _entries.OrderBy(kv => kv.Value.LastFailure).Take(_entries.Count / 2).Select(kv => kv.Key).ToList())
            _entries.Remove(key);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
