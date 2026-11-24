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
    public sealed record SendRequest(string? ClientId, string? Body);
    public sealed record ReadRequest(long? Seq);
    public sealed record PersonDto(string GomId, string DisplayName);
    public sealed record MessageDto(Guid Id, long Seq, Guid ConversationId, Guid ClientId, PersonDto Sender, string Body, DateTimeOffset CreatedAt);

    /// <summary>The hard stop in UTF-16 units (the column size). A single "character" can be an endless run of combining marks, so counting characters alone is not enough.</summary>
    private const int MaxUnits = 16000;

    public static void MapMessages(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/conversations/{id:guid}").RequireAuthorization();
        api.MapPost("/messages", Send);
        api.MapGet("/messages", History);
        api.MapPost("/read", MarkRead);
    }

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    private static MessageDto ToDto(Message m, PersonDto sender) => new(m.Id, m.Seq, m.ConversationId, m.ClientId, sender, m.Body, m.CreatedAt);

    /// <summary>The kind of conversation this person is in, or null when they are not in it (the same answer as "does not exist").</summary>
    private static Task<ConversationKind?> KindIfMember(GomDbContext db, Guid conversation, Guid me, CancellationToken ct) =>
        (from m in db.Members
         where m.ConversationId == conversation && m.UserId == me
         join c in db.Conversations on m.ConversationId equals c.Id
         select (ConversationKind?)c.Kind).SingleOrDefaultAsync(ct);

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
        var kind = await KindIfMember(db, id, me, ct);
        if (kind is null) return Results.NotFound();

        if (!Guid.TryParse(request.ClientId, out var clientId) || clientId == Guid.Empty) return Error(StatusCodes.Status400BadRequest, "invalid_client_id");
        var sender = new PersonDto(http.User.FindFirst(GomClaims.GomId)!.Value, http.User.FindFirst(GomClaims.DisplayName)!.Value);

        // A retry of something already stored gets the stored message back, whatever else has changed since.
        var existing = await FindExisting(db, me, clientId, ct);
        if (existing is not null) return Same(existing, id, sender);

        var (body, problem) = Clean(request.Body, options.Value.MaxChars);
        if (body is null) return Error(StatusCodes.Status400BadRequest, problem!);

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

            message = new Message { Id = Guid.NewGuid(), ConversationId = id, Seq = seq, SenderId = me, ClientId = clientId, Body = body, CreatedAt = now };
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
                return winner is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Same(winner, id, sender);
            }

            await db.Members.Where(m => m.ConversationId == id && m.UserId == me && m.LastReadSeq < seq)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, seq), ct);
            await tx.CommitAsync(ct);
        }

        // Only now is it sent: the live delivery and the answer both follow the commit.
        var dto = ToDto(message, sender);
        await live.PublishCreatedAsync(db, dto);
        return Results.Ok(dto);
    }

    private static Task<Message?> FindExisting(GomDbContext db, Guid sender, Guid clientId, CancellationToken ct) =>
        db.Messages.AsNoTracking().Where(m => m.SenderId == sender && m.ClientId == clientId).SingleOrDefaultAsync(ct);

    private static IResult Same(Message existing, Guid conversation, PersonDto sender) =>
        existing.ConversationId == conversation ? Results.Ok(ToDto(existing, sender)) : Error(StatusCodes.Status409Conflict, "client_id_reused");

    /// <summary>In a one-to-one conversation, both must still be friends (an unfriend or a block ends sending, not reading).</summary>
    private static async Task<bool> StillFriends(GomDbContext db, Guid conversation, Guid me, CancellationToken ct)
    {
        var peer = await db.Members.Where(m => m.ConversationId == conversation && m.UserId != me).Select(m => m.UserId).SingleAsync(ct);
        var (low, high) = Friendship.Order(me, peer);
        return await db.Friendships.AnyAsync(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Accepted, ct);
    }

    // ---- history

    private static async Task<IResult> History(Guid id, long? before, string? limit, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await KindIfMember(db, id, me, ct) is null) return Results.NotFound();

        var take = int.TryParse(limit, out var n) ? Math.Clamp(n, 1, 100) : 50;
        var rows = await (
            from m in db.Messages.AsNoTracking()
            where m.ConversationId == id && (before == null || m.Seq < before)
            orderby m.Seq descending
            join u in db.Users on m.SenderId equals u.Id
            select new { Message = m, u.GomId, u.DisplayName }).Take(take + 1).ToListAsync(ct);

        return Results.Ok(new
        {
            messages = rows.Take(take).Select(r => ToDto(r.Message, new PersonDto(r.GomId, r.DisplayName))),
            hasMore = rows.Count > take,
        });
    }

    // ---- read position

    private static async Task<IResult> MarkRead(Guid id, ReadRequest request, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await KindIfMember(db, id, me, ct) is null) return Results.NotFound();
        if (request.Seq is null or < 0) return Error(StatusCodes.Status400BadRequest, "invalid_seq");

        // Forward only, and never past the newest message.
        var newest = await db.Conversations.Where(c => c.Id == id).Select(c => c.LastSeq).SingleAsync(ct);
        var target = Math.Min(request.Seq.Value, newest);
        await db.Members.Where(m => m.ConversationId == id && m.UserId == me && m.LastReadSeq < target)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, target), ct);
        return Results.NoContent();
    }
}
