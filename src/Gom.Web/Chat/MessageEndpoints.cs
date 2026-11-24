using System.Globalization;
using Gom.Web.Auth;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gom.Web.Chat;

/// <summary>Sending, history and read position. The database is the truth; nothing here depends on a live connection.</summary>
public static class MessageEndpoints
{
    public sealed record SendRequest(string? ClientId, string? Body, Guid? ReplyTo = null);
    public sealed record ReadRequest(long? Seq);
    /// <summary>Who wrote a message: a name and an opaque id, never the Gom ID (members who are not friends do not see each other's).</summary>
    public sealed record PersonDto(Guid Id, string DisplayName);
    public sealed record MessageDto(Guid Id, long Seq, Guid ConversationId, Guid ClientId, PersonDto Sender, string Body, DateTimeOffset CreatedAt, bool Deleted = false, ReplyDto? Reply = null);

    /// <summary>The hard stop in UTF-16 units (the column size). A single "character" can be an endless run of combining marks, so counting characters alone is not enough.</summary>
    private const int MaxUnits = 16000;

    public static void MapMessages(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/conversations/{id:guid}").RequireAuthorization();
        api.MapPost("/messages", Send);
        api.MapGet("/messages", History);
        api.MapPost("/read", MarkRead);
        api.MapDelete("/messages/{messageId:guid}", Recall);
        api.MapPut("/messages/{messageId:guid}/hidden", Hide);
    }

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    internal static MessageDto ToDto(Message m, PersonDto sender, ReplyDto? reply = null) => new(m.Id, m.Seq, m.ConversationId, m.ClientId, sender, m.Body, m.CreatedAt, m.DeletedAt != null, reply);

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
        SendLimiter limiter, LiveDelivery live, IOptions<ChatOptions> options, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        var kind = access.Kind;

        if (!Guid.TryParse(request.ClientId, out var clientId) || clientId == Guid.Empty) return Error(StatusCodes.Status400BadRequest, "invalid_client_id");
        var sender = new PersonDto(me, http.User.FindFirst(GomClaims.DisplayName)!.Value);

        // A retry of something already stored gets the stored message back, whatever else has changed since.
        var existing = await FindExisting(db, me, clientId, ct);
        if (existing is not null) return await Same(db, existing, id, sender, access.JoinedSeq, ct);

        var (body, problem) = Clean(request.Body, options.Value.MaxChars);
        if (body is null) return Error(StatusCodes.Status400BadRequest, problem!);

        // An answer must point at a message of this conversation that this person can see and that is still there.
        long? parentSeq = null;
        if (request.ReplyTo is { } parentId)
        {
            parentSeq = await db.Messages.Where(m => m.Id == parentId && m.ConversationId == id && m.Seq > access.JoinedSeq && m.DeletedAt == null)
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
            var seq = (await db.Database.SqlQuery<long>(
                $"UPDATE conversations SET last_seq = last_seq + 1, last_message_at = {now} WHERE id = {id} RETURNING last_seq AS \"Value\"").ToListAsync(ct)).Single();

            message = new Message { Id = Guid.NewGuid(), ConversationId = id, Seq = seq, SenderId = me, ClientId = clientId, Body = body, CreatedAt = now, ReplyToId = request.ReplyTo };
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
                return winner is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : await Same(db, winner, id, sender, access.JoinedSeq, ct);
            }

            await db.Members.Where(m => m.ConversationId == id && m.UserId == me && m.LastReadSeq < seq)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, seq), ct);
            await tx.CommitAsync(ct);
        }

        // Only now is it sent: the live delivery and the answer both follow the commit.
        var reply = (await Replies.For(db, [message], me, access.JoinedSeq, ct)).GetValueOrDefault(message.Id);
        var dto = ToDto(message, sender, reply);
        await live.PublishAsync(db, dto.Seq, "created", dto, parentSeq);
        await live.ReadChangedAsync(db, id, me, dto.Seq);   // writing something means you have read what came before it
        return Results.Ok(dto);
    }

    private static Task<Message?> FindExisting(GomDbContext db, Guid sender, Guid clientId, CancellationToken ct) =>
        db.Messages.AsNoTracking().Where(m => m.SenderId == sender && m.ClientId == clientId).SingleOrDefaultAsync(ct);

    private static async Task<IResult> Same(GomDbContext db, Message existing, Guid conversation, PersonDto sender, long joinedSeq, CancellationToken ct) =>
        existing.ConversationId == conversation
            ? Results.Ok(ToDto(existing, sender, (await Replies.For(db, [existing], sender.Id, joinedSeq, ct)).GetValueOrDefault(existing.Id)))
            : Error(StatusCodes.Status409Conflict, "client_id_reused");

    /// <summary>In a one-to-one conversation, both must still be friends (an unfriend or a block ends sending, not reading).</summary>
    private static async Task<bool> StillFriends(GomDbContext db, Guid conversation, Guid me, CancellationToken ct)
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
    private static async Task<IResult> Recall(Guid id, Guid messageId, HttpContext http, GomDbContext db, TimeProvider clock, LiveDelivery live, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();

        var found = await db.Messages.AsNoTracking().Where(m => m.Id == messageId && m.ConversationId == id && m.Seq > access.JoinedSeq).SingleOrDefaultAsync(ct);
        if (found is null) return Results.NotFound();
        if (found.SenderId != me) return Error(StatusCodes.Status403Forbidden, "not_your_message");

        var now = clock.GetUtcNow();
        long seq;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Erase first, and only if it is still there: of several recalls at once exactly one gets 1 here.
            var erased = await db.Messages.Where(m => m.Id == messageId && m.DeletedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, (DateTimeOffset?)now).SetProperty(m => m.Body, ""), ct);
            if (erased == 0) return Results.NoContent();

            seq = (await db.Database.SqlQuery<long>(
                $"UPDATE conversations SET last_seq = last_seq + 1 WHERE id = {id} RETURNING last_seq AS \"Value\"").ToListAsync(ct)).Single();
            db.Events.Add(new MessageEvent { ConversationId = id, Seq = seq, Kind = EventKind.Deleted, MessageId = messageId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var sender = new PersonDto(me, http.User.FindFirst(GomClaims.DisplayName)!.Value);
        await live.PublishAsync(db, seq, "deleted", ToDto(new Message { Id = messageId, ConversationId = id, Seq = found.Seq, SenderId = me, ClientId = found.ClientId, Body = "", CreatedAt = found.CreatedAt, DeletedAt = now }, sender));
        return Results.NoContent();
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
        if (!await db.Messages.AnyAsync(m => m.Id == messageId && m.ConversationId == id && m.Seq > access.JoinedSeq, ct)) return Results.NotFound();

        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"INSERT INTO message_hides (user_id, message_id, created_at) VALUES ({me}, {messageId}, {now}) ON CONFLICT DO NOTHING", ct);
        live.MessageHidden(me, id, messageId);
        return Results.NoContent();
    }

    // ---- history

    private static async Task<IResult> History(Guid id, long? before, string? limit, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();

        var take = int.TryParse(limit, out var n) ? Math.Clamp(n, 1, 100) : 50;
        var rows = await (
            from m in db.Messages.AsNoTracking()
            where m.ConversationId == id && m.Seq > access.JoinedSeq && (before == null || m.Seq < before)
                && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id)
            orderby m.Seq descending
            join u in db.Users on m.SenderId equals u.Id
            select new { Message = m, u.Id, u.DisplayName }).Take(take + 1).ToListAsync(ct);

        var page = rows.Take(take).ToList();
        var replies = await Replies.For(db, page.Select(r => r.Message), me, access.JoinedSeq, ct);
        return Results.Ok(new
        {
            messages = page.Select(r => ToDto(r.Message, new PersonDto(r.Id, r.DisplayName), replies.GetValueOrDefault(r.Message.Id))),
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
        return Results.NoContent();
    }
}
