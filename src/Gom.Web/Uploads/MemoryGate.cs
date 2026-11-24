namespace Gom.Web.Uploads;

/// <summary>
/// A budget of bytes that uploads may hold in memory at once. A caller asks for what it needs and waits its turn (first come,
/// first served) or gives up after its time; a request bigger than the whole budget is refused at once. This is what keeps
/// a few big uploads from using all the memory of the container and taking the whole site down with them.
/// </summary>
public sealed class MemoryGate(long total)
{
    private readonly object _gate = new();
    private readonly Queue<(long Cost, TaskCompletionSource<bool> Turn)> _waiting = new();
    private long _used;

    public long Total => total;
    public long Used { get { lock (_gate) return _used; } }

    /// <summary>Whether a request of this size could ever be served.</summary>
    public bool Fits(long cost) => cost <= total;

    /// <summary>The held bytes (give them back by disposing), or null when the wait ran out. The size must <see cref="Fits"/>.</summary>
    public async Task<IDisposable?> EnterAsync(long cost, TimeSpan wait)
    {
        if (cost > total) throw new ArgumentOutOfRangeException(nameof(cost), "Larger than the whole budget.");
        TaskCompletionSource<bool> turn;
        lock (_gate)
        {
            // Only straight in when nobody is waiting ahead: otherwise a small request could keep a big one waiting forever.
            if (_waiting.Count == 0 && _used + cost <= total) { _used += cost; return new Hold(this, cost); }
            turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue((cost, turn));
        }
        try
        {
            await turn.Task.WaitAsync(wait);
            return new Hold(this, cost);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                // Served at the very moment the time ran out: the bytes are held, so take them rather than lose them.
                if (turn.Task.IsCompletedSuccessfully) return new Hold(this, cost);
                var rest = _waiting.Where(w => w.Turn != turn).ToArray();
                _waiting.Clear();
                foreach (var w in rest) _waiting.Enqueue(w);
                Serve();
            }
            return null;
        }
    }

    private void Release(long cost)
    {
        lock (_gate)
        {
            _used -= cost;
            Serve();
        }
    }

    /// <summary>Starts the ones at the front of the line for as long as they fit. Called with the lock held.</summary>
    private void Serve()
    {
        while (_waiting.Count > 0 && _used + _waiting.Peek().Cost <= total)
        {
            var (cost, turn) = _waiting.Dequeue();
            _used += cost;
            turn.SetResult(true);
        }
    }

    private sealed class Hold(MemoryGate owner, long cost) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(cost); }
    }
}

/// <summary>The two budgets: bytes of uploads being received, and bytes pictures use while they are made smaller.</summary>
public sealed record UploadMemory(MemoryGate Bodies, MemoryGate Images);
