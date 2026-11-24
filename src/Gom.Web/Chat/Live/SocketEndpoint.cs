using System.Net.WebSockets;
using System.Text.Json;
using Gom.Web.Auth;
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
        IOptions<ChatOptions> options, ILoggerFactory loggers)
    {
        if (!http.WebSockets.IsWebSocketRequest) return Results.BadRequest(new { error = "websocket_required" });

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var o = options.Value;
        var connection = new Connection(GomClaims.User(http.User), GomClaims.Session(http.User), new WebSocketSink(socket),
            o.SocketQueue, TimeSpan.FromSeconds(o.SocketSendTimeoutSeconds), loggers.CreateLogger<Connection>());
        registry.Add(connection);
        var writer = connection.RunWriter();
        try
        {
            connection.TryEnqueue(Frames.Ready());
            await ReceiveLoop(socket, connection, scopes, http.RequestAborted);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            registry.Remove(connection);
            connection.Finish();
            await writer;
        }
        return Results.Empty;
    }

    private static async Task ReceiveLoop(WebSocket socket, Connection connection, IServiceScopeFactory scopes, CancellationToken ct)
    {
        var buffer = new byte[MaxFrameBytes];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType == WebSocketMessageType.Binary) { connection.Close(1003, "text only"); return; }
            if (!result.EndOfMessage) { connection.Close(1009, "frame too big"); return; }

            var reply = await Answer(buffer.AsMemory(0, result.Count), connection.UserId, scopes, ct);
            connection.TryEnqueue(reply);
        }
    }

    private static async Task<byte[]> Answer(ReadOnlyMemory<byte> frame, Guid me, IServiceScopeFactory scopes, CancellationToken ct)
    {
        Guid conversation;
        long after;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != "sync")
                return Frames.Error("bad_request");
            if (!root.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.String || !Guid.TryParse(c.GetString(), out conversation))
                return Frames.Error("bad_request");
            if (!root.TryGetProperty("after", out var a) || a.ValueKind != JsonValueKind.Number || !a.TryGetInt64(out after) || after < 0)
                return Frames.Error("bad_request", conversation);
        }
        catch (JsonException) { return Frames.Error("bad_request"); }

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
            where m.Seq > access.JoinedSeq
            join u in db.Users on m.SenderId equals u.Id
            select new { e.Seq, e.Kind, Message = m, u.Id, u.DisplayName }).Take(SyncPage + 1).ToListAsync(ct);

        var events = rows.Take(SyncPage).Select(r => new EventDto(r.Seq, r.Kind.ToString().ToLowerInvariant(),
            new MessageEndpoints.MessageDto(r.Message.Id, r.Message.Seq, r.Message.ConversationId, r.Message.ClientId,
                new MessageEndpoints.PersonDto(r.Id, r.DisplayName), r.Message.Body, r.Message.CreatedAt, r.Message.DeletedAt != null))).ToList();
        return Frames.Events(conversation, events, rows.Count > SyncPage);
    }
}
