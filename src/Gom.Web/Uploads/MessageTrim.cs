using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Social;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Uploads;

/// <summary>
/// The owner trims messages sent before a date: everywhere, in one-to-one chats only, or in one group. Everything a trimmed message
/// carries goes with it (its pictures and files, reactions, pins, "delete for me" marks and stream steps), and so do notices from
/// before the date. Gone for good, as with <see cref="FileTrim"/>. An answer to a trimmed message stays and shows
/// "Tin nhắn không còn hiển thị", which is why a message's link to the one it answers is not a database key. Each conversation that
/// lost something gets a notice naming nobody; a page that receives it asks for the conversation again.
/// </summary>
public static class MessageTrim
{
    public sealed record TrimRequest(DateTimeOffset? Before, string? Where, string? Confirm);
    /// <summary><see cref="Messages"/> counts what people wrote (notices go too, uncounted); the pictures and files are those the messages carried.</summary>
    public sealed record TrimResult(int Messages, StorageEndpoints.Totals Pictures, StorageEndpoints.Totals Files, int Conversations);

    private sealed record Rule(DateTimeOffset Before, string Where, Guid? Group);

    private static IResult Error(string code) => Results.BadRequest(new { error = code });

    public static void MapMessageTrim(this RouteGroupBuilder owner)
    {
        owner.MapPost("/trim/messages/preview", Preview);
        owner.MapPost("/trim/messages", Trim);
    }

    private static async Task<Rule?> Read(TrimRequest r, GomDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (r.Before is not { } before || before > now) return null;
        var (ok, group) = await FileTrim.ReadPlace(db, r.Where, ct);
        return ok ? new Rule(before, r.Where!, group) : null;
    }

    /// <summary>The messages and notices the rule covers.</summary>
    private static IQueryable<Message> Matching(GomDbContext db, Rule rule)
    {
        var q = db.Messages.Where(m => m.CreatedAt < rule.Before);
        if (rule.Group is { } g) q = q.Where(m => m.ConversationId == g);
        else if (rule.Where == "direct") q = q.Where(m => db.Conversations.Any(c => c.Id == m.ConversationId && c.Kind == ConversationKind.Direct));
        return q;
    }

    private static async Task<TrimResult> Count(GomDbContext db, IQueryable<Message> q, CancellationToken ct)
    {
        var files = db.Attachments.Where(a => a.RemovedAt == null && q.Any(m => m.Id == a.MessageId));
        var byKind = await files.GroupBy(a => a.Kind).Select(g => new { Kind = g.Key, Count = g.Count(), Bytes = g.Sum(a => a.Size) }).ToListAsync(ct);
        StorageEndpoints.Totals Of(AttachmentKind kind) => byKind.Where(k => k.Kind == kind).Select(k => new StorageEndpoints.Totals(k.Count, k.Bytes)).SingleOrDefault() ?? new(0, 0);
        return new TrimResult(await q.CountAsync(m => m.Notice == null, ct), Of(AttachmentKind.Image), Of(AttachmentKind.File),
            await q.Select(m => m.ConversationId).Distinct().CountAsync(ct));
    }

    private static async Task<IResult> Preview(TrimRequest request, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var rule = await Read(request, db, clock.GetUtcNow(), ct);
        return rule is null ? Error("invalid_trim") : Results.Ok(await Count(db, Matching(db, rule), ct));
    }

    /// <summary>
    /// One conversation at a time, locked as a send locks it. Every key on a message is Restrict, so what hangs off the messages goes
    /// first; the notice is written in the same transaction, and the bytes of their files are deleted after the commit.
    /// </summary>
    private static async Task<IResult> Trim(TrimRequest request, HttpContext http, GomDbContext db, TimeProvider clock, IBlobStore blobs, LiveDelivery live, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rule = await Read(request, db, now, ct);
        if (rule is null) return Error("invalid_trim");
        if (request.Confirm != FileTrim.ConfirmWord) return Error("trim_not_confirmed");

        var owner = GomClaims.User(http.User);
        var conversations = await Matching(db, rule).Select(m => m.ConversationId).Distinct().ToListAsync(ct);
        int messages = 0, pictures = 0, files = 0, touched = 0;
        long pictureBytes = 0, fileBytes = 0;
        foreach (var conversation in conversations)
        {
            List<Guid> gone;
            Message notice;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await GroupEndpoints.LockGroup(db, conversation, ct);
                var ids = await Matching(db, rule).Where(m => m.ConversationId == conversation).Select(m => m.Id).ToListAsync(ct);
                if (ids.Count == 0) continue;
                var said = await db.Messages.CountAsync(m => ids.Contains(m.Id) && m.Notice == null, ct);
                var attached = await db.Attachments.Where(a => a.MessageId != null && ids.Contains(a.MessageId.Value)).Select(a => new { a.Id, a.Kind, a.Size, a.RemovedAt }).ToListAsync(ct);
                gone = [.. attached.Select(a => a.Id)];

                await db.Attachments.Where(a => gone.Contains(a.Id)).ExecuteDeleteAsync(ct);
                await db.Reactions.Where(r => ids.Contains(r.MessageId)).ExecuteDeleteAsync(ct);
                await db.Pins.Where(p => ids.Contains(p.MessageId)).ExecuteDeleteAsync(ct);
                await db.Hides.Where(h => ids.Contains(h.MessageId)).ExecuteDeleteAsync(ct);
                await db.Events.Where(e => ids.Contains(e.MessageId)).ExecuteDeleteAsync(ct);
                await db.Messages.Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync(ct);
                notice = await Notices.Write(db, conversation, owner, NoticeKind.MessagesTrimmed, null, rule.Before.ToString("O"), now, ct);
                await tx.CommitAsync(ct);

                messages += said;
                foreach (var a in attached.Where(a => a.RemovedAt == null))
                {
                    if (a.Kind == AttachmentKind.Image) { pictures++; pictureBytes += a.Size; } else { files++; fileBytes += a.Size; }
                }
            }
            touched++;
            AttachmentFiles.Forget(blobs, gone);
            await Notices.Publish(db, live, notice, ct);
        }
        return Results.Ok(new TrimResult(messages, new(pictures, pictureBytes), new(files, fileBytes), touched));
    }
}
