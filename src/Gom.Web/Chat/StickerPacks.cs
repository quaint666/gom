using Gom.Web.Auth;
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
/// draws where it was sent, so its picture stays. A pack is in every conversation's picker, or only in the conversations the owner
/// chose (H): the server keeps to that for the picker and for every send.
/// </summary>
public static class StickerPacks
{
    public sealed record PackRequest(string? Name);
    public sealed record ChatsRequest(bool Everywhere, List<Guid>? Chats);

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

    /// <summary>The owner's packs in this conversation's picker: those everywhere, and those chosen for it.</summary>
    private static IQueryable<StickerPack> In(GomDbContext db, Guid conversation) =>
        db.StickerPacks.Where(p => p.DeletedAt == null && (p.Everywhere || db.StickerPackChats.Any(x => x.PackId == p.Id && x.ConversationId == conversation)));

    /// <summary>Gom's own, or one of the owner's still in the picker of this conversation.</summary>
    public static async Task<bool> MaySend(GomDbContext db, string code, Guid conversation, CancellationToken ct) =>
        Stickers.Allowed.Contains(code)
        || (IdOf(code) is { } id && await db.CustomStickers.AnyAsync(s => s.Id == id && s.DeletedAt == null && In(db, conversation).Any(p => p.Id == s.PackId), ct));

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
        owner.MapPut("/sticker-packs/{id:guid}/chats", SetChats);
    }

    /// <summary>
    /// The packs, oldest first, each with its stickers in the order they were added. With a conversation the person is in: that
    /// conversation's picker. Without one: the packs that are everywhere, or for the owner every pack, with where it shows.
    /// </summary>
    private static async Task<IResult> List(Guid? conversation, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var owner = http.User.FindFirst(GomClaims.IsOwner)?.Value == "true";
        var shown = conversation is { } c
            ? (await db.Members.AnyAsync(m => m.ConversationId == c && m.UserId == me, ct) ? In(db, c) : db.StickerPacks.Where(p => p.DeletedAt == null && p.Everywhere))
            : owner ? db.StickerPacks.Where(p => p.DeletedAt == null) : db.StickerPacks.Where(p => p.DeletedAt == null && p.Everywhere);
        var packs = await shown.OrderBy(p => p.CreatedAt).Select(p => new { p.Id, p.Name, p.Everywhere }).ToListAsync(ct);
        var ids = packs.Select(p => p.Id).ToList();
        var stickers = await db.CustomStickers.Where(s => s.DeletedAt == null && ids.Contains(s.PackId)).OrderBy(s => s.Position)
            .Select(s => new { s.Id, s.PackId }).ToListAsync(ct);
        // Which conversations a pack was chosen for is the owner's business only.
        var chats = owner && conversation is null ? await db.StickerPackChats.Where(x => ids.Contains(x.PackId)).ToListAsync(ct) : [];
        return Results.Ok(packs.Select(p => new
        {
            id = p.Id, name = p.Name, everywhere = p.Everywhere,
            chats = owner && conversation is null ? chats.Where(x => x.PackId == p.Id).Select(x => x.ConversationId) : null,
            stickers = stickers.Where(s => s.PackId == p.Id).Select(s => new { id = s.Id, code = CodeOf(s.Id) }),
        }));
    }

    /// <summary>Where a pack shows: everywhere, or only in the conversations chosen, each one the owner is in (they cannot see others).</summary>
    private static async Task<IResult> SetChats(Guid id, ChatsRequest request, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var chosen = (request.Chats ?? []).Distinct().ToList();
        if (await db.Members.CountAsync(m => m.UserId == me && chosen.Contains(m.ConversationId), ct) != chosen.Count) return Error(StatusCodes.Status400BadRequest, "invalid_member");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.StickerPacks.Where(p => p.Id == id && p.DeletedAt == null).ExecuteUpdateAsync(s => s.SetProperty(p => p.Everywhere, request.Everywhere), ct) == 0) return Results.NotFound();
        await db.StickerPackChats.Where(x => x.PackId == id).ExecuteDeleteAsync(ct);
        db.StickerPackChats.AddRange(chosen.Select(c => new StickerPackChat { PackId = id, ConversationId = c }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
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
