using System.Threading.Channels;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Push;

/// <summary>
/// A call's wake-up for the person called's apps. <paramref name="Ring"/>: it rings until <paramref name="Until"/> (the push dies with
/// the ring, so a late one never rings). Otherwise it stopped ringing there, with <paramref name="Outcome"/> ("answered" elsewhere, or
/// how it ended); <paramref name="ExceptSession"/> is the device that answered, which knows.
/// </summary>
public sealed record CallPush(Guid CallId, Guid Callee, bool Ring, DateTimeOffset Until, string? Outcome = null, Guid? ExceptSession = null);

/// <summary>Call pushes, on a queue of their own, so a ring never waits behind a large group's message notifications.</summary>
public sealed class CallPushQueue(FcmAccount account, ILogger<CallPushQueue> log)
{
    private readonly Channel<CallPush> _jobs = Channel.CreateBounded<CallPush>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,   // with Wait, TryWrite says no when full, so a drop can be logged
    });

    public bool Enabled => account.Enabled;
    public ChannelReader<CallPush> Reader => _jobs.Reader;

    public void Enqueue(CallPush job)
    {
        if (!Enabled) return;
        if (!_jobs.Writer.TryWrite(job)) log.LogWarning("call push queue full; a call push was dropped");
    }
}

/// <summary>
/// Sends each call push to every app of the person called on a live session, in order (a stop never overtakes its ring). Data only: the
/// call's id, and for a stop how it ended; the app asks the server for the rest. An app FCM says is gone is forgotten. A ring FCM is too
/// busy for is tried once more after the wait it asks for, while the call still rings; nothing else is retried.
/// </summary>
public sealed class CallPusher(CallPushQueue queue, IServiceScopeFactory scopes, IFcmTransport fcm, TimeProvider clock, ILogger<CallPusher> log) : BackgroundService
{
    /// <summary>How long a "stopped ringing" push is worth delivering: past this, the phone stopped ringing on its own long ago.</summary>
    public const int StopSeconds = 60;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!queue.Enabled) return;
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stop))
            {
                try { await DeliverAsync(job, stop); }
                catch (Exception e) when (!stop.IsCancellationRequested) { log.LogWarning(e, "call pushes failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task DeliverAsync(CallPush job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GomDbContext>();
        var now = clock.GetUtcNow();
        var devices = await (
            from u in db.Users
            where u.Id == job.Callee && u.DisabledAt == null
            join s in db.Sessions on u.Id equals s.UserId
            where s.RevokedAt == null && s.ExpiresAt > now && s.Id != job.ExceptSession
            join d in db.FcmDevices on s.Id equals d.SessionId
            select new { d.Id, d.Token }).AsNoTracking().ToListAsync(ct);
        var data = job.Ring
            ? new Dictionary<string, string> { ["t"] = "call", ["id"] = job.CallId.ToString() }
            : new Dictionary<string, string> { ["t"] = "call-end", ["id"] = job.CallId.ToString(), ["outcome"] = job.Outcome ?? "" };
        foreach (var d in devices)
        {
            var result = await SendOnceAsync(d.Token, data, job, ct);
            if (result is { Busy: true, RetryAfter: var wait } && job.Ring)
            {
                var pause = wait ?? TimeSpan.FromSeconds(1);
                if (clock.GetUtcNow() + pause < job.Until)
                {
                    await Task.Delay(pause, clock, ct);
                    result = await SendOnceAsync(d.Token, data, job, ct);
                }
            }
            if (result?.Gone == true) await db.FcmDevices.Where(x => x.Id == d.Id).ExecuteDeleteAsync(ct);
            else if (result is { Status: not (>= 200 and < 300) }) log.LogWarning("FCM answered {Status} to a call push", result.Status);
        }
    }

    /// <summary>Null when there is nothing left to send (the ring is over).</summary>
    private async Task<FcmResult?> SendOnceAsync(string token, Dictionary<string, string> data, CallPush job, CancellationToken ct)
    {
        var ttl = job.Ring ? (int)Math.Ceiling((job.Until - clock.GetUtcNow()).TotalSeconds) : StopSeconds;
        return ttl <= 0 ? null : await fcm.SendAsync(token, data, ttl, high: true, ct);
    }
}
