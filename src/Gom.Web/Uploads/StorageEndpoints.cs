using Gom.Web.Data;
using Gom.Web.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Uploads;

/// <summary>
/// The owner's view of what Gom keeps: the disk, the database, pictures and files by group, one-to-one chats together, and by person.
/// One-to-one chats are only counted together, so the owner sees sizes, not who talks to whom. Also deletes a group nobody is in.
/// </summary>
public static class StorageEndpoints
{
    public sealed record Totals(int Count, long Bytes);
    public sealed record DiskDto(long Total, long Free);
    public sealed record GroupDto(Guid Id, string? Title, int Members, int Messages, int Files, long Bytes);
    public sealed record DirectDto(int Conversations, int Messages, int Files, long Bytes);
    public sealed record PersonDto(string GomId, string DisplayName, bool Deleted, int Files, long Bytes);
    public sealed record StorageDto(DiskDto? Disk, long Database, long? UploadsFolder, Totals Pictures, Totals Files, DirectDto Direct, List<GroupDto> Groups, List<PersonDto> People);

    public static void MapStorage(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/api/owner").RequireAuthorization("Owner");
        owner.MapGet("/storage", Get);
        owner.MapDelete("/groups/{id:guid}", DeleteEmptyGroup);
        owner.MapTrim();
        owner.MapMessageTrim();
    }

    private static async Task<IResult> Get(GomDbContext db, IOptions<UploadOptions> options, CancellationToken ct)
    {
        // Pictures and files count once they are in a message and until they are trimmed; an upload nobody sent is gone within the hour.
        var sent = db.Attachments.Where(a => a.MessageId != null && a.RemovedAt == null);
        var byKind = await sent.GroupBy(a => a.Kind).Select(g => new { Kind = g.Key, Count = g.Count(), Bytes = g.Sum(a => a.Size) }).ToListAsync(ct);
        Totals Of(AttachmentKind kind) => byKind.Where(k => k.Kind == kind).Select(k => new Totals(k.Count, k.Bytes)).SingleOrDefault() ?? new Totals(0, 0);

        var said = db.Messages.Where(m => m.Notice == null && m.DeletedAt == null);
        var groups = await db.Conversations.Where(c => c.Kind == ConversationKind.Group)
            .Select(c => new GroupDto(c.Id, c.Title,
                db.Members.Count(m => m.ConversationId == c.Id),
                said.Count(m => m.ConversationId == c.Id),
                sent.Count(a => a.ConversationId == c.Id),
                sent.Where(a => a.ConversationId == c.Id).Sum(a => (long?)a.Size) ?? 0))
            .ToListAsync(ct);

        var direct = db.Conversations.Where(c => c.Kind == ConversationKind.Direct).Select(c => c.Id);
        var directDto = new DirectDto(
            await direct.CountAsync(ct),
            await said.CountAsync(m => direct.Contains(m.ConversationId), ct),
            await sent.CountAsync(a => direct.Contains(a.ConversationId), ct),
            await sent.Where(a => direct.Contains(a.ConversationId)).SumAsync(a => (long?)a.Size, ct) ?? 0);

        var byUploader = await sent.GroupBy(a => a.UploaderId).Select(g => new { Id = g.Key, Count = g.Count(), Bytes = g.Sum(a => a.Size) }).ToListAsync(ct);
        var uploaders = byUploader.Select(u => u.Id).ToList();
        var names = await db.Users.Where(u => uploaders.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var people = byUploader.Select(u => new PersonDto(names[u.Id].GomId, names[u.Id].DisplayName, names[u.Id].DeletedAt != null, u.Count, u.Bytes)).ToList();

        var database = (await db.Database.SqlQuery<long>($"SELECT pg_database_size(current_database()) AS \"Value\"").ToListAsync(ct)).Single();
        var (disk, folder) = OnDisk(options.Value.Path);

        return Results.Ok(new StorageDto(disk, database, folder, Of(AttachmentKind.Image), Of(AttachmentKind.File), directDto,
            [.. groups.OrderByDescending(g => g.Bytes).ThenByDescending(g => g.Messages)],
            [.. people.OrderByDescending(p => p.Bytes)]));
    }

    /// <summary>
    /// The disk the uploads live on, and the uploads folder as it really is: pictures, their small versions, files and avatars.
    /// Nothing when uploads are switched off.
    /// </summary>
    private static (DiskDto?, long?) OnDisk(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return (null, null);
        var drive = new DriveInfo(path);
        var folder = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return (new DiskDto(drive.TotalSize, drive.AvailableFreeSpace), folder);
    }

    /// <summary>
    /// A group whose last member deleted their account stays with nobody in it; the owner may delete it with everything in it.
    /// A group someone is still in is not the owner's to delete.
    /// </summary>
    private static async Task<IResult> DeleteEmptyGroup(Guid id, GomDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await GroupEndpoints.LockGroup(db, id, ct);
        if (!await db.Conversations.AnyAsync(c => c.Id == id && c.Kind == ConversationKind.Group, ct)) return Results.NotFound();
        if (await db.Members.AnyAsync(m => m.ConversationId == id, ct)) return Results.Json(new { error = "group_not_empty" }, statusCode: StatusCodes.Status409Conflict);
        var files = await GroupEndpoints.DeleteGroup(db, id, ct);
        await tx.CommitAsync(ct);
        AttachmentFiles.Forget(blobs, files);
        return Results.NoContent();
    }
}
