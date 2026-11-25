using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Social;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Uploads;

/// <summary>
/// The owner trims pictures and files sent before a date: everywhere, in one-to-one chats only, or in one group; pictures, files or
/// both; optionally only the larger ones. A preview says how much first; the trim itself needs "Delete" typed. The bytes go for good
/// (the backup copies of the uploads are mirrors, so backups lose them too); each row stays, marked removed, so its message shows a
/// placeholder; and every conversation that lost something gets a notice that names nobody.
/// </summary>
public static class FileTrim
{
    /// <summary><see cref="Where"/>: "all", "direct" (every one-to-one chat), or a group's id. <see cref="Kind"/>: "all", "image", "video" or "file".</summary>
    public sealed record TrimRequest(DateTimeOffset? Before, string? Kind, string? Where, long? MinBytes, string? Confirm);
    public sealed record TrimResult(StorageEndpoints.Totals Pictures, StorageEndpoints.Totals Files, int Conversations, StorageEndpoints.Totals Videos);

    /// <summary>What the owner types to say yes. English on purpose, as agreed: it is not a word anyone types by accident.</summary>
    public const string ConfirmWord = "Delete";

    private sealed record Rule(DateTimeOffset Before, AttachmentKind? Kind, string Where, Guid? Group, long MinBytes);

    private static IResult Error(string code) => Results.BadRequest(new { error = code });

    public static void MapTrim(this RouteGroupBuilder owner)
    {
        owner.MapPost("/trim/files/preview", Preview);
        owner.MapPost("/trim/files", Trim);
    }

    private static async Task<Rule?> Read(TrimRequest r, GomDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (r.Before is not { } before || before > now || r.MinBytes is < 0) return null;
        AttachmentKind? kind;
        switch (r.Kind)
        {
            case "all": kind = null; break;
            case "image": kind = AttachmentKind.Image; break;
            case "video": kind = AttachmentKind.Video; break;
            case "file": kind = AttachmentKind.File; break;
            default: return null;
        }
        var (ok, group) = await ReadPlace(db, r.Where, ct);
        return ok ? new Rule(before, kind, r.Where!, group, r.MinBytes ?? 0) : null;
    }

    /// <summary>Where a trim may reach: "all", "direct" (every one-to-one chat, never one alone), or a group's id. Shared with <see cref="MessageTrim"/>.</summary>
    internal static async Task<(bool Ok, Guid? Group)> ReadPlace(GomDbContext db, string? where, CancellationToken ct)
    {
        if (where is "all" or "direct") return (true, null);
        if (!Guid.TryParse(where, out var g) || !await db.Conversations.AnyAsync(c => c.Id == g && c.Kind == ConversationKind.Group, ct)) return (false, null);
        return (true, g);
    }

    /// <summary>The sent, not yet trimmed pictures and files the rule covers. "Sent before" is the time of the message they are in.</summary>
    private static IQueryable<Attachment> Matching(GomDbContext db, Rule rule)
    {
        var q = db.Attachments.Where(a => a.MessageId != null && a.RemovedAt == null && a.Size >= rule.MinBytes
            && db.Messages.Any(m => m.Id == a.MessageId && m.CreatedAt < rule.Before));
        if (rule.Kind is { } k) q = q.Where(a => a.Kind == k);
        if (rule.Group is { } g) q = q.Where(a => a.ConversationId == g);
        else if (rule.Where == "direct") q = q.Where(a => db.Conversations.Any(c => c.Id == a.ConversationId && c.Kind == ConversationKind.Direct));
        return q;
    }

    private static async Task<TrimResult> Count(IQueryable<Attachment> q, CancellationToken ct)
    {
        var byKind = await q.GroupBy(a => a.Kind).Select(g => new { Kind = g.Key, Count = g.Count(), Bytes = g.Sum(a => a.Size) }).ToListAsync(ct);
        StorageEndpoints.Totals Of(AttachmentKind kind) => byKind.Where(k => k.Kind == kind).Select(k => new StorageEndpoints.Totals(k.Count, k.Bytes)).SingleOrDefault() ?? new(0, 0);
        return new TrimResult(Of(AttachmentKind.Image), Of(AttachmentKind.File), await q.Select(a => a.ConversationId).Distinct().CountAsync(ct), Of(AttachmentKind.Video));
    }

    private static async Task<IResult> Preview(TrimRequest request, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var rule = await Read(request, db, clock.GetUtcNow(), ct);
        return rule is null ? Error("invalid_trim") : Results.Ok(await Count(Matching(db, rule), ct));
    }

    /// <summary>
    /// One conversation at a time, each in its own transaction with the conversation locked (as a send locks it): its rows are marked
    /// and its notice written together, and only after the commit are the bytes deleted, so a failed commit never loses a file.
    /// </summary>
    private static async Task<IResult> Trim(TrimRequest request, HttpContext http, GomDbContext db, TimeProvider clock, IBlobStore blobs, LiveDelivery live, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rule = await Read(request, db, now, ct);
        if (rule is null) return Error("invalid_trim");
        if (request.Confirm != ConfirmWord) return Error("trim_not_confirmed");

        var owner = GomClaims.User(http.User);
        var conversations = await Matching(db, rule).Select(a => a.ConversationId).Distinct().ToListAsync(ct);
        var removed = new Tally();
        var touched = 0;
        foreach (var conversation in conversations)
        {
            List<Guid> gone;
            Message notice;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await GroupEndpoints.LockGroup(db, conversation, ct);
                var rows = await Matching(db, rule).Where(a => a.ConversationId == conversation).Select(a => new { a.Id, a.Kind, a.Size }).ToListAsync(ct);
                if (rows.Count == 0) continue;   // taken back meanwhile
                gone = [.. rows.Select(r => r.Id)];
                await db.Attachments.Where(a => gone.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.RemovedAt, (DateTimeOffset?)now), ct);
                notice = await Notices.Write(db, conversation, owner, NoticeKind.FilesTrimmed, null, rule.Before.ToString("O"), now, ct);
                await tx.CommitAsync(ct);
                foreach (var r in rows) removed.Add(r.Kind, r.Size);
            }
            touched++;
            AttachmentFiles.Forget(blobs, gone);
            await Notices.Publish(db, live, notice, ct);
        }
        return Results.Ok(new TrimResult(removed.Of(AttachmentKind.Image), removed.Of(AttachmentKind.File), touched, removed.Of(AttachmentKind.Video)));
    }
}

/// <summary>How many pictures, videos and files a trim took, and their bytes, kind by kind.</summary>
internal sealed class Tally
{
    private readonly Dictionary<AttachmentKind, (int Count, long Bytes)> _byKind = [];

    public void Add(AttachmentKind kind, long bytes)
    {
        var (count, sum) = _byKind.GetValueOrDefault(kind);
        _byKind[kind] = (count + 1, sum + bytes);
    }

    public StorageEndpoints.Totals Of(AttachmentKind kind) => _byKind.TryGetValue(kind, out var t) ? new(t.Count, t.Bytes) : new(0, 0);
}
