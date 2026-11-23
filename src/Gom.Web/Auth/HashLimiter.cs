namespace Gom.Web.Auth;

/// <summary>
/// Lets only a few password hashes run at once. A hash takes about half a second of CPU, so without a cap
/// a flood of sign-ins would starve the rest of the app. A caller that waits too long gets null (a 503).
/// </summary>
public sealed class HashLimiter(int slots, TimeSpan wait)
{
    private readonly SemaphoreSlim _slots = new(slots, slots);

    public async Task<T?> RunAsync<T>(Func<T> work) where T : struct
    {
        if (!await _slots.WaitAsync(wait)) return null;
        try { return await Task.Run(work); }
        finally { _slots.Release(); }
    }
}
