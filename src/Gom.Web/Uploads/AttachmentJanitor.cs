using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Uploads;

/// <summary>Deletes uploads nobody put in a message, once they are older than the allowed time.</summary>
public static class AttachmentCleaner
{
    /// <summary>
    /// Returns how many were removed. The rows go first, in one statement that checks again that each is still in no message (a send
    /// may take one at the last moment); only the files of the rows it deleted go, after. Read first and deleted later, a send in
    /// between kept a message whose file was gone, and its row was deleted from under it.
    /// </summary>
    public static async Task<int> RunOnce(GomDbContext db, IBlobStore store, DateTimeOffset now, TimeSpan keep, CancellationToken ct)
    {
        var cutoff = now - keep;
        var gone = await db.Database.SqlQuery<Guid>($"""
            DELETE FROM attachments WHERE id IN (
                SELECT id FROM attachments WHERE message_id IS NULL AND created_at < {cutoff} ORDER BY created_at LIMIT 500 FOR UPDATE SKIP LOCKED)
            AND message_id IS NULL
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        AttachmentFiles.Forget(store, gone);
        return gone.Count;
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
