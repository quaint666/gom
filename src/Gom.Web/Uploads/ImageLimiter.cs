namespace Gom.Web.Uploads;

/// <summary>
/// Lets only a few pictures be decoded and encoded at once: each takes about half a second of CPU and a couple of hundred
/// megabytes at its biggest. A caller that waits too long gets null (a 503).
/// </summary>
public sealed class ImageLimiter(int slots, TimeSpan wait)
{
    private readonly SemaphoreSlim _slots = new(slots, slots);

    public async Task<T?> RunAsync<T>(Func<T> work) where T : class
    {
        if (!await _slots.WaitAsync(wait)) return null;
        try { return await Task.Run(work); }
        finally { _slots.Release(); }
    }
}
