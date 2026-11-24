using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Uploads;

/// <summary>Deletes uploads nobody put in a message, once they are older than the allowed time.</summary>
public static class AttachmentCleaner
{
    /// <summary>Returns how many were removed.</summary>
    public static async Task<int> RunOnce(GomDbContext db, IBlobStore store, DateTimeOffset now, TimeSpan keep, CancellationToken ct)
    {
        var cutoff = now - keep;
        var old = await db.Attachments.Where(a => a.MessageId == null && a.CreatedAt < cutoff).OrderBy(a => a.CreatedAt).Take(500).ToListAsync(ct);
        foreach (var a in old)
        {
            store.Delete(a.Id.ToString());
            store.Delete(a.Id + ".t");
        }
        db.Attachments.RemoveRange(old);
        await db.SaveChangesAsync(ct);
        return old.Count;
    }
}

public sealed class AttachmentJanitor(IServiceScopeFactory scopes, IOptions<UploadOptions> options, TimeProvider clock, ILogger<AttachmentJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Path)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var removed = await AttachmentCleaner.RunOnce(scope.ServiceProvider.GetRequiredService<GomDbContext>(), scope.ServiceProvider.GetRequiredService<IBlobStore>(),
                        clock.GetUtcNow(), TimeSpan.FromHours(Math.Max(1, options.Value.PendingHours)), stop);
                    if (removed > 0) log.LogInformation("Removed {Count} uploads that were never used in a message", removed);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Cleaning up unused uploads failed; it will be tried again"); }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }
}
