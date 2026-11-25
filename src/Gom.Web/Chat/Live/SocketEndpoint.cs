using System.Net.WebSockets;
using System.Text.Json;
using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Chat.Live;

/// <summary>GET /ws: the live connection. The server pushes events; a device asks for what it missed with "sync". Sending stays on REST.</summary>
public static class SocketEndpoint
{
    private const int MaxFrameBytes = 4096;
    private const int SyncPage = 200;

    private sealed class WebSocketSink(WebSocket socket) : IFrameSink
    {
        public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) =>
            socket.SendAsync(frame, WebSocketMessageType.Text, true, ct).AsTask();

        public async Task CloseAsync(int code, string reason)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
            }
            catch { socket.Abort(); }
        }
    }

    public static void MapSocket(this IEndpointRouteBuilder app) =>
        app.MapGet("/ws", Handle).RequireAuthorization();

    private static async Task<IResult> Handle(HttpContext http, ConnectionRegistry registry, IServiceScopeFactory scopes,
        IOptions<ChatOptions> options, TimeProvider clock, ILoggerFactory loggers)
    {
        if (!http.WebSockets.IsWebSocketRequest) return Results.BadRequest(new { error = "websocket_required" });

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var o = options.Value;
        var connection = new Connection(GomClaims.User(http.User), GomClaims.Session(http.User), new WebSocketSink(socket),
            o.SocketQueue, TimeSpan.FromSeconds(o.SocketSendTimeoutSeconds), loggers.CreateLogger<Connection>());
        registry.Add(connection);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var writer = connection.RunWriter();
        using var beating = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        var heartbeat = Heartbeat(connection, TimeSpan.FromSeconds(Math.Max(1, o.HeartbeatSeconds)), beating.Token);
        var lost = "";
        try
        {
            connection.TryEnqueue(Frames.Ready());
            await ReceiveLoop(socket, connection, scopes, o, new SyncBucket(clock, o.SyncBurst, o.SyncPerSecond), http.RequestAborted);
        }
        catch (WebSocketException) { lost = "line lost, no close frame"; }
        catch (IOException) { lost = "line lost, no close frame"; }
        catch (OperationCanceledException) { lost = "request aborted"; }
        finally
        {
            registry.Remove(connection);
            await beating.CancelAsync();
            connection.Finish();
            await writer;
            await heartbeat;
            // Who ended it, with what code and how old it was: facts only. What a client writes as its reason is never logged.
            var age = (int)Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
            var log = loggers.CreateLogger("Gom.Web.Chat.Live.Socket");
            if (lost != "") log.LogInformation("Socket closed: {Who}, after {AgeSeconds} s", lost, age);
            else if (connection.ClosedByServer) log.LogInformation("Socket closed: by the server, code {Code}, reason {Reason}, after {AgeSeconds} s", connection.CloseCode, connection.CloseReason, age);
            else log.LogInformation("Socket closed: by the client, code {Code}, after {AgeSeconds} s", socket.CloseStatus is { } c ? (int)c : 1005, age);
        }
        return Results.Empty;
    }

    /// <summary>Sends "still here" at a steady pace until the connection ends, so a quiet line is never silent.</summary>
    private static async Task Heartbeat(Connection connection, TimeSpan every, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                if (!connection.TryEnqueue(Frames.Heartbeat())) return;
        }
        catch (OperationCanceledException) { }
    }

    private static async Task ReceiveLoop(WebSocket socket, Connection connection, IServiceScopeFactory scopes, ChatOptions o, SyncBucket bucket, CancellationToken ct)
    {
        var buffer = new byte[MaxFrameBytes];
        var bad = 0;
        var slow = 0;
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType == WebSocketMessageType.Binary) { connection.Close(1003, "text only"); return; }
            if (!result.EndOfMessage) { connection.Close(1009, "frame too big"); return; }

            var (asked, problem) = Parse(buffer.AsMemory(0, result.Count));
            if (asked is null)
            {
                connection.TryEnqueue(problem!);
                // A client that keeps sending what we cannot read is broken or hostile: it is let go.
                if (++bad >= o.MaxBadFrames) { connection.Close(1008, "too many bad frames"); return; }
                continue;
            }
            // Each ask costs a few queries, so a socket may only make so many. The page waits a moment and asks again; one that
            // does not wait, and keeps asking, is let go (1013: try again later, so the page reconnects by itself, slowly).
            if (!bucket.TryTake())
            {
                connection.TryEnqueue(Frames.Error("slow_down", asked.Conversation));
                if (++slow >= o.MaxSlowSyncs) { connection.Close(1013, "too many requests"); return; }
                continue;
            }
            slow = 0;
            connection.TryEnqueue(await Answer(asked, connection.UserId, scopes, ct));
        }
    }

    private sealed record Asked(Guid Conversation, long After);

    /// <summary>What a "sync" frame asks for, or the error frame to send back when it cannot be read.</summary>
    private static (Asked? Asked, byte[]? Problem) Parse(ReadOnlyMemory<byte> frame)
    {
        Guid conversation;
        long after;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != "sync")
                return (null, Frames.Error("bad_request"));
            if (!root.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.String || !Guid.TryParse(c.GetString(), out conversation))
                return (null, Frames.Error("bad_request"));
            if (!root.TryGetProperty("after", out var a) || a.ValueKind != JsonValueKind.Number || !a.TryGetInt64(out after) || after < 0)
                return (null, Frames.Error("bad_request", conversation));
        }
        catch (JsonException) { return (null, Frames.Error("bad_request")); }
        return (new Asked(conversation, after), null);
    }

    private static async Task<byte[]> Answer(Asked asked, Guid me, IServiceScopeFactory scopes, CancellationToken ct)
    {
        var (conversation, after) = asked;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GomDbContext>();
        // Membership on every ask: "not a member" and "no such conversation" look the same.
        var access = await Access.Of(db, conversation, me, ct);
        if (access is null) return Frames.Error("not_found", conversation);
        var floor = Math.Max(after, access.JoinedSeq);   // never what was said before they joined

        var rows = await (
            from e in db.Events.AsNoTracking()
            where e.ConversationId == conversation && e.Seq > floor
            orderby e.Seq
            join m in db.Messages on e.MessageId equals m.Id
            where m.Seq > access.JoinedSeq && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id)
            join u in db.Users on m.SenderId equals u.Id
            select new { e.Seq, e.Kind, Message = m, u.Id, u.DisplayName }).Take(SyncPage + 1).ToListAsync(ct);

        var page = rows.Take(SyncPage).ToList();
        var replies = await Replies.For(db, page.Select(r => r.Message), me, access.JoinedSeq, ct);
        var reactions = await Reactions.For(db, conversation, page.Where(r => r.Message.DeletedAt == null).Select(r => r.Message.Id), ct);
        var withFiles = await MessageAttachments.For(db, page.Where(r => r.Message.DeletedAt == null).Select(r => r.Message.Id), ct);
        var events = page.Select(r => new EventDto(r.Seq, r.Kind.ToString().ToLowerInvariant(),
            MessageEndpoints.ToDto(r.Message, new MessageEndpoints.PersonDto(r.Id, r.DisplayName), replies.GetValueOrDefault(r.Message.Id), reactions.GetValueOrDefault(r.Message.Id), withFiles.GetValueOrDefault(r.Message.Id)))).ToList();
        return Frames.Events(conversation, events, rows.Count > SyncPage);
    }
}
