using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Uploads;

/// <summary>Uploading a picture, a video or a file to a conversation, and fetching it again. Putting it in a message is a separate step.</summary>
public static class AttachmentEndpoints
{
    /// <summary>
    /// What an upload turned into, as the browser sees it. <see cref="Removed"/>: the owner trimmed it; only a placeholder is shown.
    /// <see cref="Animated"/>: a moving picture, whose own address is the moving one and whose small version is still.
    /// <see cref="DurationMs"/>: how long a video plays.
    /// </summary>
    public sealed record AttachmentDto(Guid Id, string Kind, string Name, long Size, int? Width, int? Height, bool Removed = false, bool Animated = false, int? DurationMs = null);

    /// <summary>Which picture or file to copy into another conversation.</summary>
    public sealed record CopyRequest(Guid? From);

    /// <summary>
    /// One picture or file in the side panel's list, with the message it belongs to: who sent it and its words, which the picture
    /// viewer shows under the picture.
    /// </summary>
    public sealed record MediaItem(AttachmentDto Attachment, Guid MessageId, long Seq, DateTimeOffset CreatedAt, MessageEndpoints.PersonDto Sender, string Body);

    /// <summary>How many messages' worth of pictures (or files) one page of the media list holds.</summary>
    private const int MediaPage = 24;

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    public static AttachmentDto ToDto(Attachment a) => new(a.Id, KindName(a.Kind), a.Name, a.Size, a.Width, a.Height, a.RemovedAt != null, a.Animated, a.DurationMs);

    /// <summary>The kind as the browser names it.</summary>
    public static string KindName(AttachmentKind kind) => kind switch { AttachmentKind.Image => "image", AttachmentKind.Video => "video", _ => "file" };

    /// <summary>What the browser shows in a conversation's picture grid: pictures and videos together, as a phone's gallery does.</summary>
    private static readonly AttachmentKind[] Gallery = [AttachmentKind.Image, AttachmentKind.Video];

    public static void MapAttachments(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/conversations/{id:guid}/attachments", Upload);
        api.MapPost("/conversations/{id:guid}/attachments/copy", Copy);
        api.MapPut("/attachments/{id:guid}/poster", Poster);
        api.MapGet("/conversations/{id:guid}/media", Media);
        api.MapGet("/attachments/{id:guid}", (Guid id, HttpContext http, GomDbContext db, IServiceProvider sp, CancellationToken ct) => Fetch(id, thumb: false, http, db, sp, ct));
        api.MapGet("/attachments/{id:guid}/thumb", (Guid id, HttpContext http, GomDbContext db, IServiceProvider sp, CancellationToken ct) => Fetch(id, thumb: true, http, db, sp, ct));
    }

    /// <summary>
    /// The pictures and videos ("image") or the files ("file") of a conversation, newest message first, a page of messages at a time; ask for the
    /// next page with before = the seq of the last item. Only what this person may fetch: said after they joined, not taken back, not
    /// hidden by them, and in a message (an upload not yet sent is not listed).
    /// </summary>
    private static async Task<IResult> Media(Guid id, string? kind, long? before, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        AttachmentKind[] wanted;
        if (kind == "image") wanted = Gallery;
        else if (kind == "file") wanted = [AttachmentKind.File];
        else return Error(StatusCodes.Status400BadRequest, "invalid_kind");

        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();

        var visible =
            from a in db.Attachments.AsNoTracking()
            where a.ConversationId == id && wanted.Contains(a.Kind) && a.RemovedAt == null
            join m in db.Messages on a.MessageId equals m.Id
            where m.Seq > access.SeesAfter && m.DeletedAt == null && m.Notice == null && (before == null || m.Seq < before)
                && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id)
            join u in db.Users on m.SenderId equals u.Id
            select new { Attachment = a, m.Seq, m.CreatedAt, m.Body, SenderId = u.Id, u.DisplayName };

        // A page is a number of messages, so one message's pictures are never split between two pages.
        var seqs = await visible.Select(x => x.Seq).Distinct().OrderByDescending(s => s).Take(MediaPage + 1).ToListAsync(ct);
        var hasMore = seqs.Count > MediaPage;
        var page = seqs.Take(MediaPage).ToList();
        var rows = await visible.Where(x => page.Contains(x.Seq)).OrderByDescending(x => x.Seq).ThenBy(x => x.Attachment.Position).ThenBy(x => x.Attachment.CreatedAt).ToListAsync(ct);
        return Results.Ok(new { items = rows.Select(r => new MediaItem(ToDto(r.Attachment), r.Attachment.MessageId!.Value, r.Seq, r.CreatedAt, new MessageEndpoints.PersonDto(r.SenderId, r.DisplayName), r.Body)), hasMore });
    }

    /// <summary>
    /// Forwarding a picture or a file: makes a copy of one this person may fetch, as a new upload of theirs to this conversation (not yet
    /// in a message). It is a real copy, not a shared file, so taking the original back, or ending its group, cannot take it away.
    /// </summary>
    private static async Task<IResult> Copy(Guid id, CopyRequest request, HttpContext http, GomDbContext db, TimeProvider clock, IServiceProvider services,
        IOptions<UploadOptions> options, UploadLimiter limiter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Path)) return Error(StatusCodes.Status503ServiceUnavailable, "uploads_disabled");
        if (request.From is not { } from) return Error(StatusCodes.Status400BadRequest, "invalid_attachment");

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

        // Only what is in a message this person may fetch: "not yours to see" and "not there" look the same.
        var source = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == from, ct);
        if (source is null || source.MessageId is null || source.RemovedAt is not null || source.Kind is not (AttachmentKind.Image or AttachmentKind.File or AttachmentKind.Video) || !await MayFetch(db, source, me, ct)) return Results.NotFound();
        if (await OverDaily(db, me, source.Size, options.Value, clock, ct)) return Error(StatusCodes.Status429TooManyRequests, "daily_upload_limit");

        var copy = new Attachment
        {
            Id = Guid.NewGuid(), ConversationId = id, UploaderId = me, Kind = source.Kind, Name = source.Name, ContentType = source.ContentType,
            Size = source.Size, Width = source.Width, Height = source.Height, Animated = source.Animated, DurationMs = source.DurationMs, CreatedAt = clock.GetUtcNow(),
        };
        var store = services.GetRequiredService<IBlobStore>();
        try
        {
            // A video's still picture is copied when it has one (a sender's browser may not have made one).
            var copied = await store.CopyAsync(source.Id.ToString(), copy.Id.ToString(), ct)
                && (source.Kind != AttachmentKind.Image || await store.CopyAsync(source.Id + ".t", copy.Id + ".t", ct));
            if (copied && source.Kind == AttachmentKind.Video) await store.CopyAsync(source.Id + ".t", copy.Id + ".t", ct);
            if (!copied) { store.Delete(copy.Id.ToString()); store.Delete(copy.Id + ".t"); return Results.NotFound(); }   // the original went while this ran
            db.Attachments.Add(copy);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            store.Delete(copy.Id.ToString()); store.Delete(copy.Id + ".t");
            throw;
        }
        return Results.Json(ToDto(copy), statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// Whether this person may fetch it: they are in its conversation; before it is in a message only its uploader; once it is, only if
    /// the message was said after they joined and they have not hidden it.
    /// </summary>
    private static async Task<bool> MayFetch(GomDbContext db, Attachment a, Guid me, CancellationToken ct)
    {
        var access = await Access.Of(db, a.ConversationId, me, ct);
        if (access is null || a.MessageId is null && a.UploaderId != me) return false;
        return a.MessageId is not { } messageId
            || await db.Messages.AnyAsync(m => m.Id == messageId && m.Seq > access.SeesAfter && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id), ct);
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

        // Already at the day's limit: turned away before a byte is read. Whether this upload fits is known once it is made smaller.
        if (await OverDaily(db, me, 1, o, clock, ct)) return Error(StatusCodes.Status429TooManyRequests, "daily_upload_limit");

        // Turned away before a byte is read when the size is announced, and cut off while reading when it is not.
        var most = Math.Max(o.MaxVideoBytes, Math.Max(o.MaxFileBytes, o.MaxImageBytes));
        if (http.Request.ContentLength > most) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = most;

        // An MP4 goes to disk as it arrives; everything else is read into memory to be looked at.
        var head = await ReadHead(http.Request.Body, Mp4.HeadBytes, ct);
        if (Mp4.LooksLikeMp4(head)) return await UploadVideo(id, me, name, head, http, db, clock, services, o, ct);
        var buffered = Math.Max(o.MaxFileBytes, o.MaxImageBytes);
        if (http.Request.ContentLength > buffered) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");

        // The bytes being received are held in memory: wait for room, so a few big uploads cannot use up the whole container.
        var wait = TimeSpan.FromSeconds(o.ImageWaitSeconds);
        using var bodyHold = await memory.Bodies.EnterAsync(Math.Min(http.Request.ContentLength ?? buffered, memory.Bodies.Total), wait);
        if (bodyHold is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
        var received = await ReadLimited(http.Request.Body, buffered, ct, head);
        if (received is null) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        if (received.Length == 0) return Error(StatusCodes.Status400BadRequest, "empty_upload");

        var attachmentId = Guid.NewGuid();
        var clean = FileNames.Clean(name);
        var now = clock.GetUtcNow();
        var attachment = new Attachment { Id = attachmentId, ConversationId = id, UploaderId = me, CreatedAt = now };
        byte[] main; byte[]? thumb = null;

        var moving = ImageProcessor.PlanAnimated(received, o);
        if (moving.Animated)
        {
            // Kept as it was sent (nothing here can write a moving picture again): every frame is decoded once to prove it is whole and
            // within its limits, and the first frame, still, is its small version.
            if (received.Length > o.MaxAnimatedBytes) return Error(StatusCodes.Status413PayloadTooLarge, "animated_too_large");
            if (moving.Problem is { } early) return Error(StatusCodes.Status422UnprocessableEntity, early == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image");
            var plan = moving.Plan!;
            if (!memory.Images.Fits(plan.Cost)) return Error(StatusCodes.Status422UnprocessableEntity, "image_dimensions");
            using var imageHold = await memory.Images.EnterAsync(plan.Cost, wait);
            if (imageHold is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
            var result = await images.RunAsync(() => ImageProcessor.CheckAnimated(received, plan, o));
            if (result is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
            if (result.Still is not { } still) return Error(StatusCodes.Status422UnprocessableEntity, "invalid_image");
            main = received; thumb = still;
            attachment.Kind = AttachmentKind.Image;
            attachment.Animated = true;
            attachment.Name = clean;
            attachment.ContentType = plan.Gif ? "image/gif" : "image/webp";
            attachment.Width = plan.Width; attachment.Height = plan.Height;
        }
        else if (ImageProcessor.LooksLikeImage(received))
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
        if (await OverDaily(db, me, main.Length, o, clock, ct)) return Error(StatusCodes.Status429TooManyRequests, "daily_upload_limit");

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

    /// <summary>
    /// Whether <paramref name="adding"/> more bytes would take this person past the day's limit: everything they uploaded in the last
    /// 24 hours that is still kept, sent or not. Two uploads at the same moment can both fit and pass it by one upload; that is enough
    /// to stop a disk filling up.
    /// </summary>
    private static async Task<bool> OverDaily(GomDbContext db, Guid me, long adding, UploadOptions o, TimeProvider clock, CancellationToken ct)
    {
        if (o.DailyBytes <= 0) return false;
        var since = clock.GetUtcNow().AddHours(-24);
        var used = await db.Attachments.Where(a => a.UploaderId == me && a.CreatedAt > since).SumAsync(a => (long?)a.Size, ct) ?? 0;
        return used + adding > o.DailyBytes;
    }

    /// <summary>
    /// A video: written to disk as it arrives, then read for its size and length. An MP4 with no pictures in it (sound alone) is kept
    /// as a file when it is small enough for one. The day's limit is checked once its size is known.
    /// </summary>
    private static async Task<IResult> UploadVideo(Guid id, Guid me, string? name, byte[] head, HttpContext http, GomDbContext db, TimeProvider clock,
        IServiceProvider services, UploadOptions o, CancellationToken ct)
    {
        var attachment = new Attachment { Id = Guid.NewGuid(), ConversationId = id, UploaderId = me, Name = FileNames.Clean(name), CreatedAt = clock.GetUtcNow() };
        var key = attachment.Id.ToString();
        var store = services.GetRequiredService<IBlobStore>();
        try
        {
            var size = await store.WriteStreamAsync(key, head, http.Request.Body, Math.Max(o.MaxVideoBytes, o.MaxFileBytes), ct);
            if (size is not { } bytes) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");

            VideoInfo? video;
            await using (var file = store.OpenRead(key)) video = file is null ? null : Mp4.Read(file);
            if (video is not null)
            {
                if (bytes > o.MaxVideoBytes) { store.Delete(key); return Error(StatusCodes.Status413PayloadTooLarge, "video_too_large"); }
                attachment.Kind = AttachmentKind.Video;
                attachment.ContentType = "video/mp4";
                attachment.Width = video.Width; attachment.Height = video.Height; attachment.DurationMs = video.DurationMs;
            }
            else
            {
                if (bytes > o.MaxFileBytes) { store.Delete(key); return Error(StatusCodes.Status413PayloadTooLarge, "too_large"); }
                attachment.Kind = AttachmentKind.File;
                attachment.ContentType = "application/octet-stream";
            }
            attachment.Size = bytes;
            if (await OverDaily(db, me, bytes, o, clock, ct)) { store.Delete(key); return Error(StatusCodes.Status429TooManyRequests, "daily_upload_limit"); }
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            store.Delete(key);   // nothing is kept of an upload that did not make it into the database
            throw;
        }
        return Results.Json(ToDto(attachment), statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// A video's still picture, made by the sender's browser, shown before it plays: only its uploader, only before it is in a message.
    /// It is made into a small WebP like any picture's small version; sent again, it replaces the first.
    /// </summary>
    private static async Task<IResult> Poster(Guid id, HttpContext http, GomDbContext db, IServiceProvider services, IOptions<UploadOptions> options,
        ImageLimiter images, UploadMemory memory, CancellationToken ct)
    {
        var o = options.Value;
        var me = GomClaims.User(http.User);
        var video = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        if (video is null || video.UploaderId != me || video.Kind != AttachmentKind.Video || video.MessageId is not null || video.RemovedAt is not null)
            return Results.NotFound();

        if (http.Request.ContentLength > o.MaxPosterBytes) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = o.MaxPosterBytes;
        var received = await ReadLimited(http.Request.Body, o.MaxPosterBytes, ct);
        if (received is null) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        if (!ImageProcessor.LooksLikeImage(received) || ImageProcessor.PlanAnimated(received, o).Animated) return Error(StatusCodes.Status422UnprocessableEntity, "invalid_image");

        var planned = ImageProcessor.Plan(received, o);
        if (planned.Problem is { } early) return Error(StatusCodes.Status422UnprocessableEntity, early == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image");
        var plan = planned.Plan!;
        if (!memory.Images.Fits(plan.Cost)) return Error(StatusCodes.Status422UnprocessableEntity, "image_dimensions");
        using var imageHold = await memory.Images.EnterAsync(plan.Cost, TimeSpan.FromSeconds(o.ImageWaitSeconds));
        if (imageHold is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
        var result = await images.RunAsync(() => ImageProcessor.Process(received, plan, o));
        if (result is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
        if (result.Problem is { } problem) return Error(StatusCodes.Status422UnprocessableEntity, problem == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image");
        await services.GetRequiredService<IBlobStore>().WriteAsync(id + ".t", result.Image!.Thumb, ct);
        return Results.NoContent();
    }

    /// <summary>Up to <paramref name="count"/> bytes from the start of the body (fewer only when the body is shorter).</summary>
    private static async Task<byte[]> ReadHead(Stream body, int count, CancellationToken ct)
    {
        var head = new byte[count];
        var n = await body.ReadAtLeastAsync(head, count, throwOnEndOfStream: false, ct);
        return head[..n];
    }

    /// <summary>All of the body (after <paramref name="head"/>, bytes already read from it), or null when it is longer than the limit.</summary>
    public static async Task<byte[]?> ReadLimited(Stream body, long limit, CancellationToken ct, byte[]? head = null)
    {
        using var buffer = new MemoryStream();
        if (head is { Length: > 0 })
        {
            if (head.Length > limit) return null;
            buffer.Write(head);
        }
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
        // "Not yours to see" and "not there" look the same. Before it is in a message, only the uploader sees it. A trimmed one is gone.
        if (a is null || a.RemovedAt is not null) return Results.NotFound();
        if (!await MayFetch(db, a, me, ct)) return Results.NotFound();
        var picture = a.Kind == AttachmentKind.Image;
        var video = a.Kind == AttachmentKind.Video;
        if (thumb && !picture && !video) return Results.NotFound();

        var stream = services.GetRequiredService<IBlobStore>().OpenRead(thumb ? id + ".t" : id.ToString());
        if (stream is null) return Results.NotFound();

        // A picture or a video may stay in the browser for a day (opening a conversation again must not fetch every picture again); it
        // never changes, so its id is its tag (a video's still picture can be sent again only before anyone else can see it). A video
        // plays in the page, a piece at a time. A file is not kept: the site-wide "no-store" applies.
        if (picture || video)
        {
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.Stream(stream, thumb ? "image/webp" : a.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{id}{(thumb ? "t" : "")}\""), enableRangeProcessing: true);
        }
        return Results.Stream(stream, a.ContentType, fileDownloadName: a.Name, enableRangeProcessing: true);
    }
}
