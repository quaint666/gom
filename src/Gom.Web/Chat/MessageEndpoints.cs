using System.Globalization;
using Gom.Web.Auth;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gom.Web.Chat;

/// <summary>Sending, history and read position. The database is the truth; nothing here depends on a live connection.</summary>
public static class MessageEndpoints
{
    public sealed record SendRequest(string? ClientId, string? Body, Guid? ReplyTo = null, List<Guid>? Attachments = null, string? Sticker = null);
    public sealed record ReactRequest(string? Emoji);
    public sealed record EditRequest(string? Body);
    public sealed record ReadRequest(long? Seq);
    /// <summary>Who wrote a message: a name and an opaque id, never the Gom ID (members who are not friends do not see each other's).</summary>
    public sealed record PersonDto(Guid Id, string DisplayName);
    public sealed record MessageDto(Guid Id, long Seq, Guid ConversationId, Guid ClientId, PersonDto Sender, string Body, DateTimeOffset CreatedAt, bool Deleted = false, ReplyDto? Reply = null, IReadOnlyList<ReactionDto>? Reactions = null, DateTimeOffset? EditedAt = null, IReadOnlyList<AttachmentEndpoints.AttachmentDto>? Attachments = null, NoticeDto? Notice = null, string? Sticker = null, DateTimeOffset? ExpiresAt = null);

    /// <summary>The hard stop in UTF-16 units (the column size). A single "character" can be an endless run of combining marks, so counting characters alone is not enough.</summary>
    private const int MaxUnits = 16000;

    public static void MapMessages(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/conversations/{id:guid}").RequireAuthorization();
        api.MapPost("/messages", Send);
        api.MapGet("/messages", History);
        api.MapGet("/search", Search);
        api.MapPost("/read", MarkRead);
        api.MapDelete("/messages/{messageId:guid}", Recall);
        api.MapPut("/messages/{messageId:guid}/hidden", Hide);
        api.MapPatch("/messages/{messageId:guid}", Edit);
        api.MapPut("/messages/{messageId:guid}/reaction", React);
        api.MapDelete("/messages/{messageId:guid}/reaction", Unreact);
        api.MapGet("/pins", ListPins);
        api.MapPut("/messages/{messageId:guid}/pin", Pin);
        api.MapDelete("/messages/{messageId:guid}/pin", Unpin);
    }

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    internal static MessageDto ToDto(Message m, PersonDto sender, ReplyDto? reply = null, IReadOnlyList<ReactionDto>? reactions = null, MessageExtras? extras = null) =>
        new(m.Id, m.Seq, m.ConversationId, m.ClientId, sender, m.Notice is null ? m.Body : "", m.CreatedAt, m.DeletedAt != null, reply, m.DeletedAt != null ? null : reactions, m.DeletedAt != null ? null : m.EditedAt,
            m.DeletedAt != null ? null : extras?.Attachments, extras?.Notice, m.DeletedAt != null ? null : m.Sticker, m.ExpiresAt);

    // ---- what a message may contain

    /// <summary>The cleaned body, or the error code. Line breaks become \n; the ends are trimmed; control characters are refused (a half emoji never gets here: the JSON reader replaces it).</summary>
    internal static (string? Body, string? Error) Clean(string? input, int maxChars)
    {
        if (input is null) return (null, "empty_message");
        if (input.Length > MaxUnits) return (null, "message_too_long");   // cheap guard before the real count
        var body = input.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (body.Length == 0) return (null, "empty_message");
        foreach (var c in body)
        {
            if (c is not ('\n' or '\t') && char.GetUnicodeCategory(c) == UnicodeCategory.Control) return (null, "invalid_message");
        }
        if (new StringInfo(body).LengthInTextElements > maxChars) return (null, "message_too_long");
        return (body, null);
    }

    // ---- sending

    private static async Task<IResult> Send(Guid id, SendRequest request, HttpContext http, GomDbContext db, TimeProvider clock,
        SendLimiter limiter, LiveDelivery live, Push.PushQueue push, IOptions<ChatOptions> options, IOptions<UploadOptions> uploads, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        var kind = access.Kind;

        if (!Guid.TryParse(request.ClientId, out var clientId) || clientId == Guid.Empty) return Error(StatusCodes.Status400BadRequest, "invalid_client_id");
        var sender = new PersonDto(me, http.User.FindFirst(GomClaims.DisplayName)!.Value);

        // A retry of something already stored gets the stored message back, whatever else has changed since.
        var existing = await FindExisting(db, me, clientId, ct);
        if (existing is not null) return await Same(db, existing, id, sender, access.SeesAfter, ct);

        // A sticker is the whole message: no words and no files with it.
        if (request.Sticker is not null && (!await StickerPacks.MaySend(db, request.Sticker, id, me, http.User.FindFirst(GomClaims.IsOwner)?.Value == "true", ct) || !string.IsNullOrWhiteSpace(request.Body) || request.Attachments is { Count: > 0 }))
            return Error(StatusCodes.Status400BadRequest, "invalid_sticker");
        // A message may be only pictures and files; then its words may be empty. Otherwise it needs words.
        var attached = request.Attachments?.Distinct().ToList() ?? [];
        if (request.Attachments is not null && attached.Count != request.Attachments.Count) return Error(StatusCodes.Status400BadRequest, "invalid_attachment");
        if (attached.Count > Math.Max(1, uploads.Value.MaxPerMessage)) return Error(StatusCodes.Status400BadRequest, "too_many_attachments");
        var (body, problem) = (attached.Count > 0 || request.Sticker is not null) && string.IsNullOrWhiteSpace(request.Body) ? ("", null) : Clean(request.Body, options.Value.MaxChars);
        if (body is null) return Error(StatusCodes.Status400BadRequest, problem!);
        // Only your own uploads to this conversation that no message has taken yet.
        if (attached.Count > 0 && await db.Attachments.CountAsync(a => attached.Contains(a.Id) && a.ConversationId == id && a.UploaderId == me && a.MessageId == null, ct) != attached.Count)
            return Error(StatusCodes.Status400BadRequest, "invalid_attachment");

        // An answer must point at a message of this conversation that this person can see and that is still there.
        long? parentSeq = null;
        if (request.ReplyTo is { } parentId)
        {
            parentSeq = await db.Messages.Where(m => m.Id == parentId && m.ConversationId == id && m.Seq > access.SeesAfter && m.DeletedAt == null && m.Notice == null)
                .Select(m => (long?)m.Seq).SingleOrDefaultAsync(ct);
            if (parentSeq is null) return Error(StatusCodes.Status400BadRequest, "invalid_reply");
        }

        if (kind == ConversationKind.Direct && !await StillFriends(db, id, me, ct)) return Error(StatusCodes.Status403Forbidden, "not_friends");

        var wait = limiter.TryTake(me);
        if (wait is not null)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.Value.TotalSeconds)).ToString();
            return Error(StatusCodes.Status429TooManyRequests, "too_many_messages");
        }

        var now = clock.GetUtcNow();
        Message message;
        // One transaction: take the next number, store the message and its event, move the sender's read position. Then commit.
        // The UPDATE locks the conversation row, so two sends queue up and the numbers follow the commit order.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var numbered = await db.Database.SqlQuery<long>(
                $"UPDATE conversations SET last_seq = last_seq + 1, last_message_at = {now} WHERE id = {id} RETURNING last_seq AS \"Value\"").ToListAsync(ct);
            // Gone while this send waited for it (a group disbanded at that moment): not found, as for any send to it from now on.
            if (numbered.Count == 0) return Results.NotFound();
            var seq = numbered[0];

            // Disappearing messages: the deadline is fixed now, from the setting as it is at this moment (the row is locked by the UPDATE).
            var lasts = await db.Conversations.Where(c => c.Id == id).Select(c => c.DisappearSeconds).SingleAsync(ct);
            message = new Message { Id = Guid.NewGuid(), ConversationId = id, Seq = seq, SenderId = me, ClientId = clientId, Body = body, Sticker = request.Sticker, CreatedAt = now, ReplyToId = request.ReplyTo,
                ExpiresAt = lasts is { } s ? now.AddSeconds(s) : null };
            db.Messages.Add(message);
            db.Events.Add(new MessageEvent { ConversationId = id, Seq = seq, Kind = EventKind.Created, MessageId = message.Id, CreatedAt = now });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another copy of this very send won the race. Leaving the block without committing rolls our number back.
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                var winner = await FindExisting(db, me, clientId, ct);
                return winner is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : await Same(db, winner, id, sender, access.SeesAfter, ct);
            }

            // The uploads join the message in the order chosen. Of two messages that name the same upload, one takes it and the other is refused.
            for (var i = 0; i < attached.Count; i++)
            {
                var (attachmentId, position) = (attached[i], i);
                var taken = await db.Attachments.Where(a => a.Id == attachmentId && a.ConversationId == id && a.UploaderId == me && a.MessageId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.MessageId, message.Id).SetProperty(a => a.Position, position), ct);
                if (taken == 0) { await tx.RollbackAsync(ct); return Error(StatusCodes.Status400BadRequest, "invalid_attachment"); }
            }

            await db.Members.Where(m => m.ConversationId == id && m.UserId == me && (m.LastReadSeq < seq || m.MarkedUnread))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, m => m.LastReadSeq < seq ? seq : m.LastReadSeq).SetProperty(m => m.MarkedUnread, false), ct);
            await tx.CommitAsync(ct);
        }

        // Only now is it sent: the live delivery and the answer both follow the commit.
        var reply = (await Replies.For(db, [message], me, access.SeesAfter, ct)).GetValueOrDefault(message.Id);
        var dto = ToDto(message, sender, reply, null, (await MessageAttachments.For(db, [message.Id], ct)).GetValueOrDefault(message.Id));
        await live.PublishAsync(db, dto.Seq, "created", dto, parentSeq);
        await live.ReadChangedAsync(db, id, me, dto.Seq);   // writing something means you have read what came before it
        push.Enqueue(Push.PushJob.From(message, dto.Sender.DisplayName, dto.Attachments));
        return Results.Ok(dto);
    }

    private static Task<Message?> FindExisting(GomDbContext db, Guid sender, Guid clientId, CancellationToken ct) =>
        db.Messages.AsNoTracking().Where(m => m.SenderId == sender && m.ClientId == clientId).SingleOrDefaultAsync(ct);

    private static async Task<IResult> Same(GomDbContext db, Message existing, Guid conversation, PersonDto sender, long seesAfter, CancellationToken ct) =>
        existing.ConversationId == conversation
            ? Results.Ok(ToDto(existing, sender, (await Replies.For(db, [existing], sender.Id, seesAfter, ct)).GetValueOrDefault(existing.Id), null,
                (await MessageAttachments.For(db, [existing.Id], ct)).GetValueOrDefault(existing.Id)))
            : Error(StatusCodes.Status409Conflict, "client_id_reused");

    /// <summary>In a one-to-one conversation, both must still be friends (an unfriend or a block ends sending, not reading).</summary>
    internal static async Task<bool> StillFriends(GomDbContext db, Guid conversation, Guid me, CancellationToken ct)
    {
        var peer = await db.Members.Where(m => m.ConversationId == conversation && m.UserId != me).Select(m => m.UserId).SingleAsync(ct);
        var (low, high) = Friendship.Order(me, peer);
        return await db.Friendships.AnyAsync(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Accepted, ct);
    }

    // ---- recall

    /// <summary>
    /// Takes back one of your own messages: its words are erased, and a "deleted" event takes the next number so every device,
    /// even one that was away, learns of it from the stream. Taking back twice changes nothing.
    /// </summary>
    private static async Task<IResult> Recall(Guid id, Guid messageId, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live, IBlobStore blobs, Push.PushQueue push, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();

        var found = await db.Messages.AsNoTracking().Where(m => m.Id == messageId && m.ConversationId == id && m.Seq > access.SeesAfter && m.Notice == null).SingleOrDefaultAsync(ct);
        if (found is null) return Results.NotFound();
        if (found.SenderId != me) return Error(StatusCodes.Status403Forbidden, "not_your_message");

        var now = clock.GetUtcNow();
        long seq;
        var pinsGone = false;
        List<Guid> files;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // The conversation row first, as every other change to a message takes it (edit, react, pin, trims): locked in the other
            // order, a recall and an edit of one message at the same moment deadlocked, and one of them was a 500.
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM conversations WHERE id = {id} FOR UPDATE", ct);
            // Erase first, and only if it is still there: of several recalls at once exactly one gets 1 here.
            var erased = await db.Messages.Where(m => m.Id == messageId && m.DeletedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, (DateTimeOffset?)now).SetProperty(m => m.Body, "").SetProperty(m => m.Sticker, (string?)null), ct);
            if (erased == 0) return Results.NoContent();
            // A message taken back is no longer there to stay at the top.
            pinsGone = await db.Pins.Where(p => p.MessageId == messageId).ExecuteDeleteAsync(ct) > 0;
            // Its pictures and files go with it.
            files = await db.Attachments.Where(a => a.MessageId == messageId).Select(a => a.Id).ToListAsync(ct);
            await db.Attachments.Where(a => a.MessageId == messageId).ExecuteDeleteAsync(ct);

            seq = (await db.Database.SqlQuery<long>(
                $"UPDATE conversations SET last_seq = last_seq + 1 WHERE id = {id} RETURNING last_seq AS \"Value\"").ToListAsync(ct)).Single();
            db.Events.Add(new MessageEvent { ConversationId = id, Seq = seq, Kind = EventKind.Deleted, MessageId = messageId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        AttachmentFiles.Forget(blobs, files);
        if (pinsGone) await live.PinsChangedAsync(db, id);
        var sender = new PersonDto(me, http.User.FindFirst(GomClaims.DisplayName)!.Value);
        await live.PublishAsync(db, seq, "deleted", ToDto(new Message { Id = messageId, ConversationId = id, Seq = found.Seq, SenderId = me, ClientId = found.ClientId, Body = "", CreatedAt = found.CreatedAt, DeletedAt = now }, sender));
        // Devices that showed its words get their notification replaced, so the words do not stay on a lock screen.
        push.Enqueue(new Push.PushJob(id, found.Seq, me, sender.DisplayName, 0, 0, MessageId: messageId, Recalled: true));
        return Results.NoContent();
    }

    // ---- search

    /// <summary>Most results one search returns; the newest come first.</summary>
    private const int SearchLimit = 30;

    /// <summary>The most messages one jump back loads at once (what 40 pages of 50 reached before).</summary>
    private const int JumpMost = 2000;
    public const int SearchMaxChars = 100;

    /// <summary>
    /// Finds messages of one conversation that contain every word, in any order, ignoring capitals and accents ("pho ha noi" finds
    /// "Phở ở Hà Nội"). Only what this person can see: after they joined, not taken back, not deleted for themself. Reads the
    /// conversation's own messages (no index yet): fine for chats of this size, and the first thing to index if one grows large.
    /// </summary>
    private static async Task<IResult> Search(Guid id, string? q, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        var words = SearchWords(q);
        if (words is null) return Error(StatusCodes.Status400BadRequest, "invalid_query");
        var found = Containing(db.Messages.AsNoTracking().Where(m => m.ConversationId == id && m.Seq > access.SeesAfter), words, db, me);
        return await SearchResults(found, newestFirst: q => q.OrderByDescending(r => r.Message.Seq), db, ct);
    }

    /// <summary>The words of a search, or null when there are none or too many characters. A control character (a NUL would make PostgreSQL refuse the query) counts as a space.</summary>
    private static string[]? SearchWords(string? q)
    {
        var text = new string((q ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (text.Length == 0 || new StringInfo(text).LengthInTextElements > SearchMaxChars) return null;
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Messages with words in them (not notices, not taken back, not hidden by this person) that contain every one of the words.</summary>
    private static IQueryable<Message> Containing(IQueryable<Message> messages, string[] words, GomDbContext db, Guid me)
    {
        var found = messages.Where(m => m.DeletedAt == null && m.Notice == null && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id));
        foreach (var word in words)
        {
            // % _ and \ typed by a person are ordinary characters.
            var pattern = "%" + word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            // gom_unaccent, not unaccent: the same, but declared immutable, so the trigram index on it can find the words.
            found = found.Where(m => EF.Functions.ILike(GomDbContext.Unaccent(m.Body), GomDbContext.Unaccent(pattern), "\\"));
        }
        return found;
    }

    /// <summary>One message found and who said it. Set by name, not built by a constructor, so the database can order by its parts.</summary>
    private sealed class Found
    {
        public required Message Message { get; init; }
        public required Guid SenderId { get; init; }
        public required string SenderName { get; init; }
    }

    /// <summary>The first <see cref="SearchLimit"/> found, in the given order, with who said each and its files.</summary>
    private static async Task<IResult> SearchResults(IQueryable<Message> found, Func<IQueryable<Found>, IQueryable<Found>> newestFirst, GomDbContext db, CancellationToken ct)
    {
        var rows = await newestFirst(
            from m in found
            join u in db.Users on m.SenderId equals u.Id
            select new Found { Message = m, SenderId = u.Id, SenderName = u.DisplayName }).Take(SearchLimit).ToListAsync(ct);
        var withFiles = await MessageAttachments.For(db, rows.Select(r => r.Message.Id), ct);
        return Results.Ok(new { results = rows.Select(r => ToDto(r.Message, new PersonDto(r.SenderId, r.SenderName), null, null, withFiles.GetValueOrDefault(r.Message.Id))) });
    }

    // ---- pins

    /// <summary>The pinned messages this person can see, newest pin first. A pin of something they hid, or that was said before they joined, is not shown to them.</summary>
    private static async Task<IResult> ListPins(Guid id, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        var rows = await (
            from p in db.Pins.AsNoTracking()
            where p.ConversationId == id
            join m in db.Messages on p.MessageId equals m.Id
            where m.Seq > access.SeesAfter && m.DeletedAt == null && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id)
            join u in db.Users on m.SenderId equals u.Id
            orderby p.CreatedAt descending
            select new { Message = m, u.Id, u.DisplayName }).ToListAsync(ct);
        var withFiles = await MessageAttachments.For(db, rows.Select(r => r.Message.Id), ct);
        return Results.Ok(new { pins = rows.Select(r => ToDto(r.Message, new PersonDto(r.Id, r.DisplayName), null, null, withFiles.GetValueOrDefault(r.Message.Id))) });
    }

    /// <summary>Pins a message for everyone in the conversation (any member may). A few at most; pinning again changes nothing.</summary>
    private static async Task<IResult> Pin(Guid id, Guid messageId, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live, IOptions<ChatOptions> options, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (!await db.Messages.AnyAsync(m => m.Id == messageId && m.ConversationId == id && m.Seq > access.SeesAfter && m.DeletedAt == null && m.Notice == null
                && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id), ct)) return Results.NotFound();
        if (access.Kind == ConversationKind.Direct && !await StillFriends(db, id, me, ct)) return Error(StatusCodes.Status403Forbidden, "not_friends");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The conversation row is locked, so "how many are pinned" cannot change between the count and the insert.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM conversations WHERE id = {id} FOR UPDATE", ct);
        // The message may have been taken back while this request waited for the lock.
        if (!await db.Messages.AnyAsync(m => m.Id == messageId && m.DeletedAt == null, ct)) return Results.NotFound();
        if (await db.Pins.AnyAsync(p => p.ConversationId == id && p.MessageId == messageId, ct)) return Results.NoContent();
        if (await db.Pins.CountAsync(p => p.ConversationId == id, ct) >= options.Value.MaxPins) return Error(StatusCodes.Status409Conflict, "pin_limit");
        db.Pins.Add(new MessagePin { ConversationId = id, MessageId = messageId, PinnedBy = me, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await live.PinsChangedAsync(db, id);
        return Results.NoContent();
    }

    /// <summary>Takes a pin away (any member may). Doing it when there is none changes nothing.</summary>
    private static async Task<IResult> Unpin(Guid id, Guid messageId, HttpContext http, GomDbContext db, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await Access.Of(db, id, me, ct) is null) return Results.NotFound();
        var removed = await db.Pins.Where(p => p.ConversationId == id && p.MessageId == messageId).ExecuteDeleteAsync(ct);
        if (removed > 0) await live.PinsChangedAsync(db, id);
        return Results.NoContent();
    }

    // ---- edit

    /// <summary>
    /// Changes the words of one of your own messages. Everyone in the conversation is told through the stream ("updated"), and the
    /// message shows that it was edited. The old words are not kept. Writing the same words again changes nothing.
    /// </summary>
    private static async Task<IResult> Edit(Guid id, Guid messageId, EditRequest request, HttpContext http, GomDbContext db, TimeProvider clock,
        SendLimiter limiter, LiveDelivery live, IOptions<ChatOptions> options, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        var found = await db.Messages.AsNoTracking().Where(m => m.Id == messageId && m.ConversationId == id && m.Seq > access.SeesAfter && m.DeletedAt == null && m.Notice == null).SingleOrDefaultAsync(ct);
        if (found is null) return Results.NotFound();
        if (found.SenderId != me) return Error(StatusCodes.Status403Forbidden, "not_your_message");
        if (found.Sticker is not null) return Error(StatusCodes.Status400BadRequest, "not_editable");   // a sticker has no words to change
        var (body, problem) = Clean(request.Body, options.Value.MaxChars);
        if (body is null) return Error(StatusCodes.Status400BadRequest, problem!);
        if (access.Kind == ConversationKind.Direct && !await StillFriends(db, id, me, ct)) return Error(StatusCodes.Status403Forbidden, "not_friends");
        if (body == found.Body) return Results.NoContent();

        var wait = limiter.TryTake(me);
        if (wait is not null)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.Value.TotalSeconds)).ToString();
            return Error(StatusCodes.Status429TooManyRequests, "too_many_messages");
        }

        var now = clock.GetUtcNow();
        long seq;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM conversations WHERE id = {id} FOR UPDATE", ct);
            // Only while it is still there and still yours: a recall may have come while this waited for the lock.
            var changed = await db.Messages.Where(m => m.Id == messageId && m.DeletedAt == null && m.Body != body)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Body, body).SetProperty(m => m.EditedAt, (DateTimeOffset?)now), ct);
            if (changed == 0) return Results.NoContent();
            seq = await NextUpdate(db, id, messageId, now, ct);
            await tx.CommitAsync(ct);
        }
        await PublishUpdate(db, found, seq, me, ct, live);
        return Results.NoContent();
    }

    // ---- reactions

    /// <summary>Gives a message this person's reaction (one each; choosing another replaces it). Everyone in the conversation is told, through the stream.</summary>
    private static async Task<IResult> React(Guid id, Guid messageId, ReactRequest request, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live, CancellationToken ct)
    {
        if (request.Emoji is null || !Reactions.Allowed.Contains(request.Emoji)) return Error(StatusCodes.Status400BadRequest, "invalid_reaction");
        var me = GomClaims.User(http.User);
        var (error, found) = await ReactionTarget(db, id, messageId, me, ct);
        if (error is not null) return error;

        var now = clock.GetUtcNow();
        long seq;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // The conversation row is locked: reactions of one person on one message cannot race, and the number comes in order.
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM conversations WHERE id = {id} FOR UPDATE", ct);
            if (!await db.Messages.AnyAsync(m => m.Id == messageId && m.DeletedAt == null, ct)) return Results.NotFound();
            var current = await db.Reactions.Where(r => r.MessageId == messageId && r.UserId == me).Select(r => r.Emoji).SingleOrDefaultAsync(ct);
            if (current == request.Emoji) return Results.NoContent();
            if (current is null) db.Reactions.Add(new MessageReaction { MessageId = messageId, UserId = me, Emoji = request.Emoji, CreatedAt = now });
            else await db.Reactions.Where(r => r.MessageId == messageId && r.UserId == me).ExecuteUpdateAsync(s => s.SetProperty(r => r.Emoji, request.Emoji).SetProperty(r => r.CreatedAt, now), ct);
            seq = await NextUpdate(db, id, messageId, now, ct);
            await tx.CommitAsync(ct);
        }
        await PublishUpdate(db, found!, seq, me, ct, live);
        return Results.NoContent();
    }

    /// <summary>Takes this person's reaction away. When there was none, nothing happens.</summary>
    private static async Task<IResult> Unreact(Guid id, Guid messageId, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var (error, found) = await ReactionTarget(db, id, messageId, me, ct);
        if (error is not null) return error;

        long seq;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM conversations WHERE id = {id} FOR UPDATE", ct);
            if (await db.Reactions.Where(r => r.MessageId == messageId && r.UserId == me).ExecuteDeleteAsync(ct) == 0) return Results.NoContent();
            seq = await NextUpdate(db, id, messageId, clock.GetUtcNow(), ct);
            await tx.CommitAsync(ct);
        }
        await PublishUpdate(db, found!, seq, me, ct, live);
        return Results.NoContent();
    }

    /// <summary>The message a reaction is about, if this person may react to it: in their conversation, after they joined, there, not hidden by them, and (one-to-one) still between friends.</summary>
    private static async Task<(IResult? Error, Message? Message)> ReactionTarget(GomDbContext db, Guid id, Guid messageId, Guid me, CancellationToken ct)
    {
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return (Results.NotFound(), null);
        var found = await db.Messages.AsNoTracking().Where(m => m.Id == messageId && m.ConversationId == id && m.Seq > access.SeesAfter && m.DeletedAt == null && m.Notice == null
            && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id)).SingleOrDefaultAsync(ct);
        if (found is null) return (Results.NotFound(), null);
        if (access.Kind == ConversationKind.Direct && !await StillFriends(db, id, me, ct)) return (Error(StatusCodes.Status403Forbidden, "not_friends"), null);
        return (null, found);
    }

    /// <summary>Takes the next number of the conversation for a change to an existing message ("updated"). Call inside the transaction that made the change.</summary>
    internal static async Task<long> NextUpdate(GomDbContext db, Guid id, Guid messageId, DateTimeOffset now, CancellationToken ct)
    {
        var seq = (await db.Database.SqlQuery<long>(
            $"UPDATE conversations SET last_seq = last_seq + 1 WHERE id = {id} RETURNING last_seq AS \"Value\"").ToListAsync(ct)).Single();
        db.Events.Add(new MessageEvent { ConversationId = id, Seq = seq, Kind = EventKind.Updated, MessageId = messageId, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return seq;
    }

    /// <summary>Tells the conversation the message changed: its words, its quote and its reactions as they are now.</summary>
    internal static async Task PublishUpdate(GomDbContext db, Message message, long seq, Guid actor, CancellationToken ct, LiveDelivery live)
    {
        var author = await db.Users.AsNoTracking().Where(u => u.Id == message.SenderId).Select(u => new PersonDto(u.Id, u.DisplayName)).SingleAsync(ct);
        var current = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        var reactions = await Reactions.For(db, message.ConversationId, [message.Id], ct);
        long? parentSeq = null;
        ReplyDto? reply = null;
        if (current.ReplyToId is { } parent)
        {
            // The quote as the actor sees it; readers who joined after the quoted message get it emptied by the delivery.
            parentSeq = await db.Messages.Where(m => m.Id == parent).Select(m => (long?)m.Seq).SingleOrDefaultAsync(ct);
            var joined = await db.Members.Where(m => m.ConversationId == current.ConversationId && m.UserId == actor).Select(m => m.JoinedSeq > m.ClearedSeq ? m.JoinedSeq : m.ClearedSeq).SingleOrDefaultAsync(ct);
            reply = (await Replies.For(db, [current], actor, joined, ct)).GetValueOrDefault(current.Id);
        }
        var files = await MessageAttachments.For(db, [current.Id], ct);
        await live.PublishAsync(db, seq, "updated", ToDto(current, author, reply, reactions.GetValueOrDefault(current.Id), files.GetValueOrDefault(current.Id)), parentSeq);
    }

    // ---- delete for me

    /// <summary>
    /// Hides one message from this person's own view (theirs or someone else's, taken back or not). Everyone else still has it.
    /// Doing it twice changes nothing. Their other devices are told to drop it.
    /// </summary>
    private static async Task<IResult> Hide(Guid id, Guid messageId, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        // Only what this person can see: "not yours to see" and "no such message" look the same.
        if (!await db.Messages.AnyAsync(m => m.Id == messageId && m.ConversationId == id && m.Seq > access.SeesAfter && m.Notice == null, ct)) return Results.NotFound();

        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"INSERT INTO message_hides (user_id, message_id, created_at) VALUES ({me}, {messageId}, {now}) ON CONFLICT DO NOTHING", ct);
        live.MessageHidden(me, id, messageId);
        return Results.NoContent();
    }

    // ---- history

    private static async Task<IResult> History(Guid id, long? before, int? limit, Guid? to, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();

        // "limit=abc" is a 400 (the framework refuses what does not bind), not a quiet 50.
        var take = Math.Clamp(limit ?? 50, 1, 100);
        // A jump back to a message (a quote, a pin, a search result): everything from it up to `before` in one page, so the thread
        // stays one unbroken run and the page is drawn once. There are at most before - seq messages from it on, so taking that many
        // reaches it. Further back than JumpMost, or a message not here to see: the usual page.
        if (to is { } target && before is { } upTo)
        {
            var at = await db.Messages.Where(m => m.Id == target && m.ConversationId == id && m.Seq > access.SeesAfter && m.Seq < upTo)
                .Select(m => (long?)m.Seq).SingleOrDefaultAsync(ct);
            if (at is { } seq && upTo - seq <= JumpMost) take = Math.Max(take, (int)(upTo - seq));
        }
        var rows = await (
            from m in db.Messages.AsNoTracking()
            where m.ConversationId == id && m.Seq > access.SeesAfter && (before == null || m.Seq < before)
                && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id)
            orderby m.Seq descending
            join u in db.Users on m.SenderId equals u.Id
            select new { Message = m, u.Id, u.DisplayName }).Take(take + 1).ToListAsync(ct);

        var page = rows.Take(take).ToList();
        var replies = await Replies.For(db, page.Select(r => r.Message), me, access.SeesAfter, ct);
        var reactions = await Reactions.For(db, id, page.Where(r => r.Message.DeletedAt == null).Select(r => r.Message.Id), ct);
        var withFiles = await MessageAttachments.For(db, page.Where(r => r.Message.DeletedAt == null).Select(r => r.Message.Id), ct);
        return Results.Ok(new
        {
            messages = page.Select(r => ToDto(r.Message, new PersonDto(r.Id, r.DisplayName), replies.GetValueOrDefault(r.Message.Id), reactions.GetValueOrDefault(r.Message.Id), withFiles.GetValueOrDefault(r.Message.Id))),
            hasMore = rows.Count > take,
            // Where the stream stands, so a device can ask for "everything after this" from here on.
            lastSeq = await db.Conversations.Where(c => c.Id == id).Select(c => c.LastSeq).SingleAsync(ct),
        });
    }

    // ---- read position

    private static async Task<IResult> MarkRead(Guid id, ReadRequest request, HttpContext http, GomDbContext db, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await Access.Of(db, id, me, ct) is null) return Results.NotFound();
        if (request.Seq is null or < 0) return Error(StatusCodes.Status400BadRequest, "invalid_seq");

        // Forward only, and never past the newest message.
        var newest = await db.Conversations.Where(c => c.Id == id).Select(c => c.LastSeq).SingleAsync(ct);
        var target = Math.Min(request.Seq.Value, newest);
        var moved = await db.Members.Where(m => m.ConversationId == id && m.UserId == me && m.LastReadSeq < target)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, target), ct);
        if (moved > 0) await live.ReadChangedAsync(db, id, me, target);   // only when it actually moved
        // Opening it is reading it: a "mark as unread" is done with, on every device.
        if (await db.Members.Where(m => m.ConversationId == id && m.UserId == me && m.MarkedUnread).ExecuteUpdateAsync(s => s.SetProperty(m => m.MarkedUnread, false), ct) > 0)
            live.ConversationsChanged([me]);
        return Results.NoContent();
    }
}
