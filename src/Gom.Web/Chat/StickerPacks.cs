using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Chat;

/// <summary>
/// Sticker packs the owner makes from their own pictures (H), shown in everyone's picker after Gom's own (<see cref="Stickers"/>). A still
/// picture is made a WebP at most <see cref="Edge"/> pixels on its longer side, keeping its transparency; a moving GIF or WebP is checked
/// frame by frame and kept as sent. A sticker's code in a message is "c-" and its id; one taken out of the picker, or its pack, still
/// draws where it was sent, so its picture stays.
/// </summary>
public static class StickerPacks
{
    public sealed record PackRequest(string? Name);

    public const int NameChars = 40;
    public const int PerPack = 100;
    public const long MaxBytes = 1024 * 1024;
    /// <summary>Sharp at 120 CSS pixels (a sticker in a message) on a screen of two pixels to one.</summary>
    public const int Edge = 240;
    private const int Quality = 85;
    private const string Prefix = "c-";

    public static string CodeOf(Guid id) => Prefix + id.ToString("N");

    /// <summary>The sticker's id in a code made by <see cref="CodeOf"/>; null for anything else.</summary>
    public static Guid? IdOf(string? code) =>
        code is not null && code.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(code.AsSpan(Prefix.Length), "N", out var id) ? id : null;

    /// <summary>Gom's own, or one of the owner's still in the picker.</summary>
    public static async Task<bool> MaySend(GomDbContext db, string code, CancellationToken ct) =>
        Stickers.Allowed.Contains(code)
        || (IdOf(code) is { } id && await db.CustomStickers.AnyAsync(s => s.Id == id && s.DeletedAt == null && db.StickerPacks.Any(p => p.Id == s.PackId && p.DeletedAt == null), ct));

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    public static void MapStickerPacks(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/sticker-packs", List);
        api.MapGet("/stickers/{id:guid}", Fetch);
        var owner = app.MapGroup("/api/owner").RequireAuthorization("Owner");
        owner.MapPost("/sticker-packs", Create);
        owner.MapDelete("/sticker-packs/{id:guid}", DeletePack);
        owner.MapPost("/sticker-packs/{id:guid}/stickers", Add);
        owner.MapDelete("/stickers/{id:guid}", DeleteSticker);
    }

    /// <summary>The packs in the picker, oldest first, each with its stickers in the order they were added.</summary>
    private static async Task<IResult> List(GomDbContext db, CancellationToken ct)
    {
        var packs = await db.StickerPacks.Where(p => p.DeletedAt == null).OrderBy(p => p.CreatedAt).Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var ids = packs.Select(p => p.Id).ToList();
        var stickers = await db.CustomStickers.Where(s => s.DeletedAt == null && ids.Contains(s.PackId)).OrderBy(s => s.Position)
            .Select(s => new { s.Id, s.PackId }).ToListAsync(ct);
        return Results.Ok(packs.Select(p => new
        {
            id = p.Id, name = p.Name,
            stickers = stickers.Where(s => s.PackId == p.Id).Select(s => new { id = s.Id, code = CodeOf(s.Id) }),
        }));
    }

    /// <summary>A sticker's picture, also one taken out of the picker (it may be in messages). It never changes under its id.</summary>
    private static async Task<IResult> Fetch(Guid id, HttpContext http, GomDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var type = await db.CustomStickers.Where(s => s.Id == id).Select(s => s.ContentType).SingleOrDefaultAsync(ct);
        var stream = type is null ? null : services.GetService<IBlobStore>()?.OpenRead(id.ToString());
        if (stream is null) return Results.NotFound();
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.Stream(stream, type);
    }

    private static async Task<IResult> Create(PackRequest request, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > NameChars || name.Any(char.IsControl)) return Error(StatusCodes.Status400BadRequest, "invalid_title");
        var pack = new StickerPack { Id = Guid.NewGuid(), Name = name, CreatedAt = clock.GetUtcNow() };
        db.StickerPacks.Add(pack);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = pack.Id, name = pack.Name, stickers = Array.Empty<object>() });
    }

    private static async Task<IResult> DeletePack(Guid id, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var changed = await db.StickerPacks.Where(p => p.Id == id && p.DeletedAt == null).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, now), ct);
        return changed == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> DeleteSticker(Guid id, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var changed = await db.CustomStickers.Where(s => s.Id == id && s.DeletedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, now), ct);
        return changed == 0 ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>A picture as the body, added at the end of the pack. The answer is the new sticker and its code.</summary>
    private static async Task<IResult> Add(Guid id, HttpContext http, GomDbContext db, IServiceProvider services, IOptions<UploadOptions> options,
        ImageLimiter images, UploadMemory memory, TimeProvider clock, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Path)) return Error(StatusCodes.Status503ServiceUnavailable, "uploads_disabled");
        if (!await db.StickerPacks.AnyAsync(p => p.Id == id && p.DeletedAt == null, ct)) return Results.NotFound();
        if (await db.CustomStickers.CountAsync(s => s.PackId == id && s.DeletedAt == null, ct) >= PerPack) return Error(StatusCodes.Status400BadRequest, "pack_full");

        if (http.Request.ContentLength > MaxBytes) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = MaxBytes;
        var wait = TimeSpan.FromSeconds(o.ImageWaitSeconds);
        using var bodyHold = await memory.Bodies.EnterAsync(Math.Min(http.Request.ContentLength ?? MaxBytes, memory.Bodies.Total), wait);
        if (bodyHold is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
        var received = await AttachmentEndpoints.ReadLimited(http.Request.Body, MaxBytes, ct);
        if (received is null) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        if (received.Length == 0) return Error(StatusCodes.Status400BadRequest, "empty_upload");

        byte[] bytes;
        string type;
        var moving = ImageProcessor.PlanAnimated(received, o);
        if (moving.Animated)
        {
            if (moving.Problem is { } p || moving.Plan is null) return Error(StatusCodes.Status422UnprocessableEntity, moving.Problem == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image");
            if (!memory.Images.Fits(moving.Plan.Cost)) return Error(StatusCodes.Status422UnprocessableEntity, "image_dimensions");
            using var hold = await memory.Images.EnterAsync(moving.Plan.Cost, wait);
            if (hold is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
            var checkedFrames = await images.RunAsync(() => ImageProcessor.CheckAnimated(received, moving.Plan, o));
            if (checkedFrames is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
            if (checkedFrames.Still is null) return Error(StatusCodes.Status422UnprocessableEntity, "invalid_image");
            (bytes, type) = (received, moving.Plan.Gif ? "image/gif" : "image/webp");
        }
        else
        {
            var planned = ImageProcessor.Plan(received, o);
            if (planned.Problem is { } early) return Error(StatusCodes.Status422UnprocessableEntity, early == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image");
            var plan = planned.Plan!;
            if (!memory.Images.Fits(plan.Cost)) return Error(StatusCodes.Status422UnprocessableEntity, "image_dimensions");
            using var hold = await memory.Images.EnterAsync(plan.Cost, wait);
            if (hold is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
            var made = await images.RunAsync(() => ImageProcessor.Sticker(received, plan, Edge, Quality));
            if (made is null) return Error(StatusCodes.Status503ServiceUnavailable, "busy");
            if (made.Bytes is not { } still) return Error(StatusCodes.Status422UnprocessableEntity, made.Problem == ImageProblem.TooManyPixels ? "image_dimensions" : "invalid_image");
            (bytes, type) = (still, "image/webp");
        }

        var store = services.GetRequiredService<IBlobStore>();
        var sticker = new CustomSticker { Id = Guid.NewGuid(), PackId = id, ContentType = type, CreatedAt = clock.GetUtcNow() };
        await store.WriteAsync(sticker.Id.ToString(), bytes, ct);
        try
        {
            sticker.Position = (await db.CustomStickers.Where(s => s.PackId == id).MaxAsync(s => (int?)s.Position, ct) ?? -1) + 1;
            db.CustomStickers.Add(sticker);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            store.Delete(sticker.Id.ToString());
            throw;
        }
        return Results.Ok(new { id = sticker.Id, code = CodeOf(sticker.Id) });
    }
}
