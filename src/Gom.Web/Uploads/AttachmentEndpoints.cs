using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Uploads;

/// <summary>Uploading a picture or a file to a conversation, and fetching it again. Putting it in a message is a separate step.</summary>
public static class AttachmentEndpoints
{
    /// <summary>What an upload turned into, as the browser sees it.</summary>
    public sealed record AttachmentDto(Guid Id, string Kind, string Name, long Size, int? Width, int? Height);

    /// <summary>One picture or file in the side panel's list, with the message it belongs to (a click goes there).</summary>
    public sealed record MediaItem(AttachmentDto Attachment, Guid MessageId, long Seq, DateTimeOffset CreatedAt);

    /// <summary>How many messages' worth of pictures (or files) one page of the media list holds.</summary>
    private const int MediaPage = 24;

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    public static AttachmentDto ToDto(Attachment a) => new(a.Id, a.Kind == AttachmentKind.Image ? "image" : "file", a.Name, a.Size, a.Width, a.Height);

    public static void MapAttachments(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/conversations/{id:guid}/attachments", Upload);
        api.MapGet("/conversations/{id:guid}/media", Media);
        api.MapGet("/attachments/{id:guid}", (Guid id, HttpContext http, GomDbContext db, IServiceProvider sp, CancellationToken ct) => Fetch(id, thumb: false, http, db, sp, ct));
        api.MapGet("/attachments/{id:guid}/thumb", (Guid id, HttpContext http, GomDbContext db, IServiceProvider sp, CancellationToken ct) => Fetch(id, thumb: true, http, db, sp, ct));
    }

    /// <summary>
    /// The pictures ("image") or the files ("file") of a conversation, newest message first, a page of messages at a time; ask for the
    /// next page with before = the seq of the last item. Only what this person may fetch: said after they joined, not taken back, not
    /// hidden by them, and in a message (an upload not yet sent is not listed).
    /// </summary>
    private static async Task<IResult> Media(Guid id, string? kind, long? before, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        AttachmentKind wanted;
        if (kind == "image") wanted = AttachmentKind.Image;
        else if (kind == "file") wanted = AttachmentKind.File;
        else return Error(StatusCodes.Status400BadRequest, "invalid_kind");

        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();

        var visible =
            from a in db.Attachments.AsNoTracking()
            where a.ConversationId == id && a.Kind == wanted
            join m in db.Messages on a.MessageId equals m.Id
            where m.Seq > access.JoinedSeq && m.DeletedAt == null && m.Notice == null && (before == null || m.Seq < before)
                && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id)
            select new { Attachment = a, m.Seq, m.CreatedAt };

        // A page is a number of messages, so one message's pictures are never split between two pages.
        var seqs = await visible.Select(x => x.Seq).Distinct().OrderByDescending(s => s).Take(MediaPage + 1).ToListAsync(ct);
        var hasMore = seqs.Count > MediaPage;
        var page = seqs.Take(MediaPage).ToList();
        var rows = await visible.Where(x => page.Contains(x.Seq)).OrderByDescending(x => x.Seq).ThenBy(x => x.Attachment.Position).ThenBy(x => x.Attachment.CreatedAt).ToListAsync(ct);
        return Results.Ok(new { items = rows.Select(r => new MediaItem(ToDto(r.Attachment), r.Attachment.MessageId!.Value, r.Seq, r.CreatedAt)), hasMore });
    }

    private static async Task<IResult> Upload(Guid id, string? name, HttpContext http, GomDbContext db, TimeProvider clock, IServiceProvider services,
        IOptions<UploadOptions> options, UploadLimiter limiter, ImageLimiter images, UploadMemory memory, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Path)) return Error(StatusCodes.Status503ServiceUnavailable, "uploads_disabled");

        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind == ConversationKind.Direct && !await MessageEndpoints.StillFriends(db, id, me, ct)) return Error(StatusCodes.Status403Forbidden, "not_friends");

        var slow = limiter.TryTake(me);
        if (slow is not null)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(slow.Value.TotalSeconds)).ToString();
            return Error(StatusCodes.Status429TooManyRequests, "too_many_uploads");
        }

        // Turned away before a byte is read when the size is announced, and cut off while reading when it is not.
        var most = Math.Max(o.MaxFileBytes, o.MaxImageBytes);
        if (http.Request.ContentLength > most) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = most;

        // The bytes being received are held in memory: wait for room, so a few big uploads cannot use up the whole container.
        var wait = TimeSpan.FromSeconds(o.ImageWaitSeconds);
        using var bodyHold = await memory.Bodies.EnterAsync(Math.Min(http.Request.ContentLength ?? most, memory.Bodies.Total), wait);
        if (bodyHold is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
        var received = await ReadLimited(http.Request.Body, most, ct);
        if (received is null) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        if (received.Length == 0) return Error(StatusCodes.Status400BadRequest, "empty_upload");

        var attachmentId = Guid.NewGuid();
        var clean = FileNames.Clean(name);
        var now = clock.GetUtcNow();
        var attachment = new Attachment { Id = attachmentId, ConversationId = id, UploaderId = me, CreatedAt = now };
        byte[] main; byte[]? thumb = null;

        if (ImageProcessor.LooksLikeImage(received))
        {
            if (received.Length > o.MaxImageBytes) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
            var planned = ImageProcessor.Plan(received, o);
            if (planned.Problem is { } early) return Error(StatusCodes.Status422UnprocessableEntity, early == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image");
            var plan = planned.Plan!;
            // A picture that would need more memory than there is for pictures is turned away in words, not allowed to bring the site down.
            if (!memory.Images.Fits(plan.Cost)) return Error(StatusCodes.Status422UnprocessableEntity, "image_dimensions");
            using var imageHold = await memory.Images.EnterAsync(plan.Cost, wait);
            if (imageHold is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
            var result = await images.RunAsync(() => ImageProcessor.Process(received, plan, o));
            if (result is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
            if (result.Problem is { } problem)
                return Error(StatusCodes.Status422UnprocessableEntity, problem == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image");
            var image = result.Image!;
            main = image.Main; thumb = image.Thumb;
            attachment.Kind = AttachmentKind.Image;
            attachment.Name = FileNames.AsWebp(clean);
            attachment.ContentType = "image/webp";
            attachment.Width = image.Width; attachment.Height = image.Height;
        }
        else
        {
            main = received;
            attachment.Kind = AttachmentKind.File;
            attachment.Name = clean;
            attachment.ContentType = "application/octet-stream";   // whatever the sender called it, it is only ever a download
        }
        attachment.Size = main.Length;

        var store = services.GetRequiredService<IBlobStore>();
        await store.WriteAsync(attachmentId.ToString(), main, ct);
        try
        {
            if (thumb is not null) await store.WriteAsync(attachmentId + ".t", thumb, ct);
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Nothing is kept of an upload that did not make it into the database.
            store.Delete(attachmentId.ToString()); store.Delete(attachmentId + ".t");
            throw;
        }
        return Results.Json(ToDto(attachment), statusCode: StatusCodes.Status201Created);
    }

    /// <summary>All of the body, or null when it is longer than the limit.</summary>
    public static async Task<byte[]?> ReadLimited(Stream body, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int n;
        while ((n = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > limit) return null;
            await buffer.WriteAsync(chunk.AsMemory(0, n), ct);
        }
        return buffer.ToArray();
    }

    private static async Task<IResult> Fetch(Guid id, bool thumb, HttpContext http, GomDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var a = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        // "Not yours to see" and "not there" look the same. Before it is in a message, only the uploader sees it.
        if (a is null) return Results.NotFound();
        var access = await Access.Of(db, a.ConversationId, me, ct);
        if (access is null || a.MessageId is null && a.UploaderId != me) return Results.NotFound();
        // In a message: only what was said after you joined, and not what you hid.
        if (a.MessageId is { } messageId && !await db.Messages.AnyAsync(m => m.Id == messageId && m.Seq > access.JoinedSeq && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id), ct))
            return Results.NotFound();
        var picture = a.Kind is AttachmentKind.Image or AttachmentKind.Preview;
        if (thumb && !picture) return Results.NotFound();

        var stream = services.GetRequiredService<IBlobStore>().OpenRead(thumb ? id + ".t" : id.ToString());
        if (stream is null) return Results.NotFound();

        // A picture may stay in the browser for a day (opening a conversation again must not fetch every picture again); it never
        // changes, so its id is its tag. A file is not kept: the site-wide "no-store" applies.
        if (picture)
        {
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.Stream(stream, "image/webp", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{id}{(thumb ? "t" : "")}\""), enableRangeProcessing: true);
        }
        return Results.Stream(stream, a.ContentType, fileDownloadName: a.Name, enableRangeProcessing: true);
    }
}
