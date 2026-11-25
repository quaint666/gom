using Gom.Web.Auth;
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
    }

    /// <summary>
    /// Takes a picture as the body, keeps the largest square from its middle, and makes it this person's picture. The old one's file
    /// goes. The answer is the new picture's id, which is also part of its address.
    /// </summary>
    private static async Task<IResult> Set(HttpContext http, GomDbContext db, IServiceProvider services, IOptions<UploadOptions> options,
        UploadLimiter limiter, ImageLimiter images, UploadMemory memory, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Path)) return Error(StatusCodes.Status503ServiceUnavailable, "uploads_disabled");
        var me = GomClaims.User(http.User);

        var slow = limiter.TryTake(me);
        if (slow is not null)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(slow.Value.TotalSeconds)).ToString();
            return Error(StatusCodes.Status429TooManyRequests, "too_many_uploads");
        }

        if (http.Request.ContentLength > o.MaxImageBytes) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = o.MaxImageBytes;
        var wait = TimeSpan.FromSeconds(o.ImageWaitSeconds);
        using var bodyHold = await memory.Bodies.EnterAsync(Math.Min(http.Request.ContentLength ?? o.MaxImageBytes, memory.Bodies.Total), wait);
        if (bodyHold is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
        var received = await AttachmentEndpoints.ReadLimited(http.Request.Body, o.MaxImageBytes, ct);
        if (received is null) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        if (received.Length == 0) return Error(StatusCodes.Status400BadRequest, "empty_upload");

        var planned = ImageProcessor.Plan(received, o);
        if (planned.Problem is { } early) return Error(StatusCodes.Status422UnprocessableEntity, early == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image");
        var plan = planned.Plan!;
        if (!memory.Images.Fits(plan.Cost)) return Error(StatusCodes.Status422UnprocessableEntity, "image_dimensions");
        using var imageHold = await memory.Images.EnterAsync(plan.Cost, wait);
        if (imageHold is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
        var made = await images.RunAsync(() => ImageProcessor.Avatar(received, plan, Edge, Quality));
        if (made is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
        if (made.Bytes is not { } bytes)
            return Error(StatusCodes.Status422UnprocessableEntity, made.Problem == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image");

        var store = services.GetRequiredService<IBlobStore>();
        var fresh = Guid.NewGuid();
        await store.WriteAsync(fresh.ToString(), bytes, ct);
        Guid? old;
        try
        {
            old = await Swap(db, me, fresh, ct);
        }
        catch
        {
            store.Delete(fresh.ToString());
            throw;
        }
        if (old is { } gone) store.Delete(gone.ToString());
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
    /// Someone's picture by its id, for those who may see who they are (themself, friends, anyone sharing a conversation). Only a
    /// current picture is served: the id changes when the picture does, so its address may be kept for good.
    /// </summary>
    private static async Task<IResult> Fetch(Guid avatarId, HttpContext http, GomDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var owner = await db.Users.Where(u => u.AvatarId == avatarId).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct);
        if (owner is not { } userId || !await PeopleEndpoints.MayKnow(db, me, userId, ct)) return Results.NotFound();
        var stream = services.GetService<IBlobStore>()?.OpenRead(avatarId.ToString());
        if (stream is null) return Results.NotFound();
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.Stream(stream, "image/webp");
    }
}
