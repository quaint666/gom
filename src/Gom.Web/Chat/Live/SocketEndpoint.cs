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

    /// <summary>Maps /ws. <paramref name="version"/> is the pages' version (<see cref="Assets.Version"/>), told to each connection when it is ready.</summary>
    public static void MapSocket(this IEndpointRouteBuilder app, string? version) =>
        app.MapGet("/ws", (HttpContext http, ConnectionRegistry registry, IServiceScopeFactory scopes, IOptions<ChatOptions> options,
            TimeProvider clock, ILoggerFactory loggers, LiveDelivery live) => Handle(http, registry, scopes, options, clock, loggers, live, version)).RequireAuthorization();

    private static async Task<IResult> Handle(HttpContext http, ConnectionRegistry registry, IServiceScopeFactory scopes,
        IOptions<ChatOptions> options, TimeProvider clock, ILoggerFactory loggers, LiveDelivery live, string? version)
    {
        if (!http.WebSockets.IsWebSocketRequest) return Results.BadRequest(new { error = "websocket_required" });

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        // A page too old for this server: 4426 tells it to stop trying and reload (a refused upgrade would only be retried).
        if (Pages.TooOld(http.Request.Query["page"]))
        {
            await socket.CloseOutputAsync((WebSocketCloseStatus)4426, "update_required", CancellationToken.None);
            return Results.Empty;
        }
        var o = options.Value;
        var connection = new Connection(GomClaims.User(http.User), GomClaims.Session(http.User), new WebSocketSink(socket),
            o.SocketQueue, TimeSpan.FromSeconds(o.SocketSendTimeoutSeconds), loggers.CreateLogger<Connection>())
            { TakesCalls = http.Request.Query["calls"] == "1" };
        registry.Add(connection);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var writer = connection.RunWriter();
        using var beating = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        var heartbeat = Heartbeat(connection, TimeSpan.FromSeconds(Math.Max(1, o.HeartbeatSeconds)), beating.Token);
        var watching = WatchSession(connection, scopes, clock, TimeSpan.FromSeconds(Math.Max(1, o.SessionCheckSeconds)), beating.Token);
        var lost = "";
        try
        {
            connection.TryEnqueue(Frames.Ready(version));
            await ReceiveLoop(socket, connection, scopes, o, new SyncBucket(clock, o.SyncBurst, o.SyncPerSecond), new SyncBucket(clock, o.TypingBurst, o.TypingPerSecond), live, http.RequestAborted);
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
            await watching;
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
    /// <summary>Closes the socket (4401, "go to sign-in") once its session is no longer live: expired, ended, or its person locked.</summary>
    private static async Task WatchSession(Connection connection, IServiceScopeFactory scopes, TimeProvider clock, TimeSpan every, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                bool live;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<GomDbContext>();
                    var now = clock.GetUtcNow();
                    live = await (from s in db.Sessions where s.Id == connection.SessionId && s.RevokedAt == null && s.ExpiresAt > now
                                  join u in db.Users on s.UserId equals u.Id where u.DisabledAt == null select s.Id).AnyAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException) { continue; }   // the database is away: keep the socket, ask again later
                if (!live) { connection.Close(ConnectionRegistry.SessionEnded, "session ended"); return; }
            }
        }
        catch (OperationCanceledException) { }
    }

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

    private static async Task ReceiveLoop(WebSocket socket, Connection connection, IServiceScopeFactory scopes, ChatOptions o, SyncBucket bucket, SyncBucket typingBucket, LiveDelivery live, CancellationToken ct)
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
            // "Typing" is only a hint: over the pace it is dropped without a word (it never counts against the socket), and one that cannot be
            // delivered is dropped too.
            if (asked.Typing)
            {
                if (typingBucket.TryTake())
                {
                    await using var typed = scopes.CreateAsyncScope();
                    await live.TypingAsync(typed.ServiceProvider.GetRequiredService<GomDbContext>(), asked.Conversation, connection.UserId, ct);
                }
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

    private sealed record Asked(Guid Conversation, long After, bool Typing = false);

    /// <summary>What a "sync" or "typing" frame says, or the error frame to send back when it cannot be read.</summary>
    private static (Asked? Asked, byte[]? Problem) Parse(ReadOnlyMemory<byte> frame)
    {
        Guid conversation;
        long after;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() is not ("sync" or "typing"))
                return (null, Frames.Error("bad_request"));
            if (!root.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.String || !Guid.TryParse(c.GetString(), out conversation))
                return (null, Frames.Error("bad_request"));
            if (t.GetString() == "typing") return (new Asked(conversation, 0, Typing: true), null);
            if (!root.TryGetProperty("after", out var a) || a.ValueKind != JsonValueKind.Number || !a.TryGetInt64(out after) || after < 0)
                return (null, Frames.Error("bad_request", conversation));
        }
        catch (JsonException) { return (null, Frames.Error("bad_request")); }
        return (new Asked(conversation, after), null);
    }

    private static async Task<byte[]> Answer(Asked asked, Guid me, IServiceScopeFactory scopes, CancellationToken ct)
    {
        var (conversation, after, _) = asked;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GomDbContext>();
        // Membership on every ask: "not a member" and "no such conversation" look the same.
        var access = await Access.Of(db, conversation, me, ct);
        if (access is null) return Frames.Error("not_found", conversation);
        var floor = Math.Max(after, access.SeesAfter);   // never what was said before they joined

        var rows = await (
            from e in db.Events.AsNoTracking()
            where e.ConversationId == conversation && e.Seq > floor
            orderby e.Seq
            join m in db.Messages on e.MessageId equals m.Id
            where m.Seq > access.SeesAfter && !db.Hides.Any(h => h.UserId == me && h.MessageId == m.Id)
            join u in db.Users on m.SenderId equals u.Id
            select new { e.Seq, e.Kind, Message = m, u.Id, u.DisplayName }).Take(SyncPage + 1).ToListAsync(ct);

        var page = rows.Take(SyncPage).ToList();
        var replies = await Replies.For(db, page.Select(r => r.Message), me, access.SeesAfter, ct);
        var reactions = await Reactions.For(db, conversation, page.Where(r => r.Message.DeletedAt == null).Select(r => r.Message.Id), ct);
        var withFiles = await MessageAttachments.For(db, page.Where(r => r.Message.DeletedAt == null).Select(r => r.Message.Id), ct);
        var events = page.Select(r => new EventDto(r.Seq, r.Kind.ToString().ToLowerInvariant(),
            MessageEndpoints.ToDto(r.Message, new MessageEndpoints.PersonDto(r.Id, r.DisplayName), replies.GetValueOrDefault(r.Message.Id), reactions.GetValueOrDefault(r.Message.Id), withFiles.GetValueOrDefault(r.Message.Id)))).ToList();
        return Frames.Events(conversation, events, rows.Count > SyncPage);
    }
}
