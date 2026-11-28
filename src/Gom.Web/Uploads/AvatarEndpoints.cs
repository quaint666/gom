using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Social;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Uploads;

/// <summary>Profile pictures: setting and removing your own, and fetching someone's.</summary>
public static class AvatarEndpoints
{
    /// <summary>The side of the square kept: sharp at 72 CSS pixels on a screen of three pixels to one, the largest a picture is drawn.</summary>
    public const int Edge = 256;
    private const int Quality = 82;

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    public static void MapAvatars(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPut("/me/avatar", Set);
        api.MapDelete("/me/avatar", Remove);
        api.MapGet("/avatars/{avatarId:guid}", Fetch);
        api.MapPut("/conversations/{id:guid}/avatar", SetGroup);
        api.MapDelete("/conversations/{id:guid}/avatar", RemoveGroup);
    }

    /// <summary>
    /// Reads a picture from the body and makes it a profile picture: the largest square from its middle, at most <see cref="Edge"/>
    /// pixels, a WebP. The answer is the picture's bytes, or the refusal to send back.
    /// </summary>
    private static async Task<(byte[]? Bytes, IResult? Problem)> Make(HttpContext http, Guid me, UploadOptions o, UploadLimiter limiter, ImageLimiter images,
        UploadMemory memory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Path)) return (null, Error(StatusCodes.Status503ServiceUnavailable, "uploads_disabled"));
        var slow = limiter.TryTake(me);
        if (slow is not null)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(slow.Value.TotalSeconds)).ToString();
            return (null, Error(StatusCodes.Status429TooManyRequests, "too_many_uploads"));
        }

        if (http.Request.ContentLength > o.MaxImageBytes) return (null, Error(StatusCodes.Status413PayloadTooLarge, "too_large"));
        http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = o.MaxImageBytes;
        var wait = TimeSpan.FromSeconds(o.ImageWaitSeconds);
        using var bodyHold = await memory.Bodies.EnterAsync(Math.Min(http.Request.ContentLength ?? o.MaxImageBytes, memory.Bodies.Total), wait);
        if (bodyHold is null) return (null, Error(StatusCodes.Status503ServiceUnavailable, "busy"));
        var received = await AttachmentEndpoints.ReadLimited(http.Request.Body, o.MaxImageBytes, ct);
        if (received is null) return (null, Error(StatusCodes.Status413PayloadTooLarge, "too_large"));
        if (received.Length == 0) return (null, Error(StatusCodes.Status400BadRequest, "empty_upload"));

        // A profile picture is a still JPEG, PNG or WebP; a GIF is turned away rather than frozen on its first frame.
        if (ImageProcessor.LooksLikeGif(received)) return (null, Error(StatusCodes.Status422UnprocessableEntity, "invalid_image"));
        var planned = ImageProcessor.Plan(received, o);
        if (planned.Problem is { } early) return (null, Error(StatusCodes.Status422UnprocessableEntity, early == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image"));
        var plan = planned.Plan!;
        if (!memory.Images.Fits(plan.Cost)) return (null, Error(StatusCodes.Status422UnprocessableEntity, "image_dimensions"));
        using var imageHold = await memory.Images.EnterAsync(plan.Cost, wait);
        if (imageHold is null) return (null, Error(StatusCodes.Status503ServiceUnavailable, "busy"));
        var made = await images.RunAsync(() => ImageProcessor.Avatar(received, plan, Edge, Quality));
        if (made is null) return (null, Error(StatusCodes.Status503ServiceUnavailable, "busy"));
        if (made.Bytes is not { } bytes)
            return (null, Error(StatusCodes.Status422UnprocessableEntity, made.Problem == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image"));
        return (bytes, null);
    }

    /// <summary>
    /// Writes a new picture's file, then puts it in place with <paramref name="swap"/>, which gives back the one it replaced; that one's
    /// file goes. A failure on the way leaves no file nobody points to.
    /// </summary>
    private static async Task<Guid> Store(IBlobStore store, byte[] bytes, Func<Guid, Task<Guid?>> swap, CancellationToken ct)
    {
        var fresh = Guid.NewGuid();
        await store.WriteAsync(fresh.ToString(), bytes, ct);
        Guid? old;
        try { old = await swap(fresh); }
        catch
        {
            store.Delete(fresh.ToString());
            throw;
        }
        if (old is { } gone) store.Delete(gone.ToString());
        return fresh;
    }

    /// <summary>
    /// Takes a picture as the body, keeps the largest square from its middle, and makes it this person's picture. The old one's file
    /// goes. The answer is the new picture's id, which is also part of its address.
    /// </summary>
    private static async Task<IResult> Set(HttpContext http, GomDbContext db, IServiceProvider services, IOptions<UploadOptions> options,
        UploadLimiter limiter, ImageLimiter images, UploadMemory memory, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var (bytes, problem) = await Make(http, me, options.Value, limiter, images, memory, ct);
        if (problem is not null) return problem;
        var fresh = await Store(services.GetRequiredService<IBlobStore>(), bytes!, (id) => Swap(db, me, id, ct), ct);
        return Results.Ok(new { avatar = fresh });
    }

    /// <summary>Back to the initials; the picture's file goes.</summary>
    private static async Task<IResult> Remove(HttpContext http, GomDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await Swap(db, me, null, ct) is { } gone) services.GetRequiredService<IBlobStore>().Delete(gone.ToString());
        return Results.NoContent();
    }

    /// <summary>
    /// Puts the new picture in place and gives back the one it replaced, in one statement, so two changes at once cannot both think
    /// they replaced the same picture (and leave a file nobody points to).
    /// </summary>
    private static async Task<Guid?> Swap(GomDbContext db, Guid me, Guid? fresh, CancellationToken ct)
    {
        var old = await db.Database.SqlQuery<Guid?>($"UPDATE users SET avatar_id = {fresh} WHERE id = {me} RETURNING OLD.avatar_id AS \"Value\"").ToListAsync(ct);
        return old.SingleOrDefault();
    }

    /// <summary>
    /// A group's picture, set by its leader or a deputy (as renaming it is): everyone in it sees it, with a line saying who changed it.
    /// </summary>
    private static async Task<IResult> SetGroup(Guid id, HttpContext http, GomDbContext db, IServiceProvider services, IOptions<UploadOptions> options,
        UploadLimiter limiter, ImageLimiter images, UploadMemory memory, TimeProvider clock, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await MayPicture(db, id, me, ct) is { } refused) return refused;
        var (bytes, problem) = await Make(http, me, options.Value, limiter, images, memory, ct);
        if (problem is not null) return problem;
        var fresh = await Store(services.GetRequiredService<IBlobStore>(), bytes!, (picture) => SwapGroup(db, id, picture, ct), ct);
        await Changed(db, id, me, clock, live, ct);
        return Results.Ok(new { avatar = fresh });
    }

    /// <summary>A group back to its initials; the picture's file goes, and a line says who did it.</summary>
    private static async Task<IResult> RemoveGroup(Guid id, HttpContext http, GomDbContext db, IServiceProvider services, TimeProvider clock, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await MayPicture(db, id, me, ct) is { } refused) return refused;
        if (await SwapGroup(db, id, null, ct) is not { } gone) return Results.NoContent();   // it had none: nothing changed, nothing said
        services.GetRequiredService<IBlobStore>().Delete(gone.ToString());
        await Changed(db, id, me, clock, live, ct);
        return Results.NoContent();
    }

    /// <summary>Only a group's leader or a deputy changes its picture; to anyone not in it, the group does not exist.</summary>
    private static async Task<IResult?> MayPicture(GomDbContext db, Guid id, Guid me, CancellationToken ct)
    {
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind != ConversationKind.Group) return Error(StatusCodes.Status400BadRequest, "not_a_group");
        return access.Role == GroupRole.Member ? Error(StatusCodes.Status403Forbidden, "not_allowed") : null;
    }

    private static async Task<Guid?> SwapGroup(GomDbContext db, Guid id, Guid? fresh, CancellationToken ct)
    {
        var old = await db.Database.SqlQuery<Guid?>($"UPDATE conversations SET avatar_id = {fresh} WHERE id = {id} RETURNING OLD.avatar_id AS \"Value\"").ToListAsync(ct);
        return old.SingleOrDefault();
    }

    /// <summary>The line in the group, and every member's list told to fetch again (the picture is drawn there).</summary>
    private static async Task Changed(GomDbContext db, Guid id, Guid me, TimeProvider clock, LiveDelivery live, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var notice = await Notices.Write(db, id, me, NoticeKind.Pictured, null, "", clock.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
        live.ConversationsChanged(await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct));
        await Notices.Publish(db, live, notice, ct);
    }

    /// <summary>
    /// A picture by its id: someone's, for those who may see who they are (themself, friends, anyone sharing a conversation), or a
    /// group's, for its members. Only a current picture is served: the id changes when the picture does, so its address may be kept
    /// for good.
    /// </summary>
    private static async Task<IResult> Fetch(Guid avatarId, HttpContext http, GomDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var owner = await db.Users.Where(u => u.AvatarId == avatarId).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct);
        var mayGroup = owner is null && await db.Conversations.AnyAsync(c => c.AvatarId == avatarId && db.Members.Any(m => m.ConversationId == c.Id && m.UserId == me), ct);
        if (!mayGroup && (owner is not { } userId || !await PeopleEndpoints.MayKnow(db, me, userId, ct))) return Results.NotFound();
        var stream = services.GetService<IBlobStore>()?.OpenRead(avatarId.ToString());
        if (stream is null) return Results.NotFound();
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.Stream(stream, "image/webp");
    }
}
