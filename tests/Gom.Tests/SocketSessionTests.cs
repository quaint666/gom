using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>Sockets that check their session every second, so a test can watch one notice its session is over.</summary>
public sealed class QuickSessionCheckFixture : AppFixture
{
    protected override void Settings(IWebHostBuilder b) => b.UseSetting("Chat:SessionCheckSeconds", "1");
}

/// <summary>
/// An open socket notices a session that ended without anything announcing it (it ran out, or was ended while the socket was opening),
/// and closes with 4401, which sends the page to sign-in.
/// </summary>
public class SocketSessionTests(QuickSessionCheckFixture app) : IClassFixture<QuickSessionCheckFixture>
{
    private async Task<WebSocket> Connect(string cookie)
    {
        var client = app.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => { r.Headers["Cookie"] = $"__Host-gom={cookie}"; r.Headers["Origin"] = AppFixture.Origin; };
        var ws = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        Assert.Equal("ready", (await Next(ws, TimeSpan.FromSeconds(5)))!.Value.GetProperty("t").GetString());
        return ws;
    }

    /// <summary>The next frame, or null when the server closed (its code is then in CloseStatus).</summary>
    private static async Task<JsonElement?> Next(WebSocket ws, TimeSpan within)
    {
        using var cts = new CancellationTokenSource(within);
        var buffer = new byte[16384];
        var text = new StringBuilder();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
        } while (!r.EndOfMessage);
        return JsonDocument.Parse(text.ToString()).RootElement.Clone();
    }

    private static async Task<int?> ClosedWithin(WebSocket ws, TimeSpan within)
    {
        try
        {
            while (await Next(ws, within) is not null) { }
            return ws.CloseStatus is { } s ? (int)s : -1;
        }
        catch (OperationCanceledException) { return null; }
    }

    [Fact]
    public async Task A_socket_whose_session_ran_out_is_closed_with_4401()
    {
        var (_, cookie) = await app.Member();
        using var ws = await Connect(cookie);
        app.Clock.Advance(TimeSpan.FromDays(91));   // past the longest a session lives
        try { Assert.Equal(4401, await ClosedWithin(ws, TimeSpan.FromSeconds(5))); }
        finally { app.Clock.Advance(-TimeSpan.FromDays(91)); }
    }

    [Fact]
    public async Task A_socket_whose_session_was_ended_without_telling_it_is_closed_with_4401()
    {
        var (user, cookie) = await app.Member();
        using var ws = await Connect(cookie);
        // As if it ended in the moment the socket was opening: the row changes, nobody tells the socket.
        await using (var db = app.Db.NewContext())
            await db.Database.ExecuteSqlAsync($"UPDATE sessions SET revoked_at = now() WHERE user_id = {user.Id}");
        Assert.Equal(4401, await ClosedWithin(ws, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_socket_with_a_live_session_stays_open()
    {
        var (_, cookie) = await app.Member();
        using var ws = await Connect(cookie);
        Assert.Null(await ClosedWithin(ws, TimeSpan.FromSeconds(3)));
    }
}
