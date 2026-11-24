using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Previews;

/// <summary>
/// Makes the previews: takes a link from the line, fetches its page (and picture) safely, stores what it found, and tells the
/// conversation that the message changed. Nothing it does can fail a send, and nothing is logged about the address (it is private).
/// </summary>
public sealed class PreviewWorker(PreviewQueue queue, SafeFetcher fetcher, IServiceScopeFactory scopes, IOptions<PreviewOptions> options,
    IOptions<UploadOptions> uploads, TimeProvider clock, ILogger<PreviewWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Enabled) return;
        var workers = Enumerable.Range(0, Math.Max(1, options.Value.Concurrency)).Select(_ => Loop(stop));
        await Task.WhenAll(workers);
    }

    private async Task Loop(CancellationToken stop)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stop))
            {
                try { await Make(job, stop); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "A link preview failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One preview. Public so a test can run it without waiting for the line.</summary>
    public async Task Make(PreviewJob job, CancellationToken ct)
    {
        var o = options.Value;
        var page = await fetcher.GetAsync(job.Url, t => t is "text/html" or "application/xhtml+xml", o.MaxPageBytes, "text/html,application/xhtml+xml;q=0.9", ct);
        if (page is null) return;
        var meta = PageMetaReader.Read(page.Body, page.Final);
        if (meta.Title is null && meta.Description is null) return;   // nothing worth showing

        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<GomDbContext>();
        var store = sp.GetRequiredService<IBlobStore>();

        var picture = meta.Image is not null && !string.IsNullOrWhiteSpace(uploads.Value.Path) && Uri.TryCreate(meta.Image, UriKind.Absolute, out var imageUri)
            ? await FetchPicture(imageUri, job, sp, store, ct) : null;

        var now = clock.GetUtcNow();
        long seq;
        var stored = false;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM conversations WHERE id = {job.ConversationId} FOR UPDATE", ct);
            // Only while the message is still there, not taken back, and has no preview yet.
            if (await db.Messages.AnyAsync(m => m.Id == job.MessageId && m.DeletedAt == null, ct) && !await db.Previews.AnyAsync(p => p.MessageId == job.MessageId, ct))
            {
                if (picture is not null) { picture.MessageId = job.MessageId; db.Attachments.Add(picture); }
                db.Previews.Add(new MessagePreview
                {
                    MessageId = job.MessageId, Url = job.Url.AbsoluteUri, Title = meta.Title, Description = meta.Description, Site = meta.Site, ImageId = picture?.Id, CreatedAt = now,
                });
                await db.SaveChangesAsync(ct);
                seq = await MessageEndpoints.NextUpdate(db, job.ConversationId, job.MessageId, now, ct);
                await tx.CommitAsync(ct);
                stored = true;
            }
            else seq = 0;
        }
        if (!stored)
        {
            if (picture is not null) AttachmentFiles.Forget(store, [picture.Id]);   // nothing took it
            return;
        }
        var message = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == job.MessageId, ct);
        await MessageEndpoints.PublishUpdate(db, message, seq, job.SenderId, ct, sp.GetRequiredService<LiveDelivery>());
    }

    /// <summary>The page's picture, made smaller and stored like an upload. Null when there is none, it is not a picture, or there is no room for it now.</summary>
    private async Task<Attachment?> FetchPicture(Uri address, PreviewJob job, IServiceProvider sp, IBlobStore store, CancellationToken ct)
    {
        var o = options.Value;
        var fetched = await fetcher.GetAsync(address, t => t.StartsWith("image/", StringComparison.Ordinal) && t != "image/svg+xml", o.MaxImageBytes, "image/*", ct);
        if (fetched is null || fetched.Cut || fetched.Body.Length == 0 || !ImageProcessor.LooksLikeImage(fetched.Body)) return null;
        var up = uploads.Value;
        var planned = ImageProcessor.Plan(fetched.Body, up);
        if (planned.Plan is not { } plan) return null;
        var memory = sp.GetRequiredService<UploadMemory>();
        if (!memory.Images.Fits(plan.Cost)) return null;
        using var hold = await memory.Images.EnterAsync(plan.Cost, TimeSpan.FromSeconds(up.ImageWaitSeconds));
        if (hold is null) return null;
        var result = await sp.GetRequiredService<ImageLimiter>().RunAsync(() => ImageProcessor.Process(fetched.Body, plan, up));
        if (result?.Image is not { } image) return null;

        var id = Guid.NewGuid();
        await store.WriteAsync(id.ToString(), image.Main, ct);
        await store.WriteAsync(id + ".t", image.Thumb, ct);
        return new Attachment
        {
            Id = id, ConversationId = job.ConversationId, UploaderId = job.SenderId, Kind = AttachmentKind.Preview, Name = "preview.webp", ContentType = "image/webp",
            Size = image.Main.Length, Width = image.Width, Height = image.Height, CreatedAt = clock.GetUtcNow(),
        };
    }
}
