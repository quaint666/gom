using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gom.Tests;

/// <summary>How fast one socket may ask to catch up. A small burst and a slow pace are set here so the limit is easy to reach; time moves only when a test says so.</summary>
public sealed class SyncLimitTests : IClassFixture<AppFixture>, IDisposable
{
    private readonly AppFixture _app;
    private readonly WebApplicationFactory<Program> _factory;

    public SyncLimitTests(AppFixture app)
    {
        _app = app;
        _factory = app.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Chat:SyncBurst", "3");
            b.UseSetting("Chat:SyncPerSecond", "1");
            b.UseSetting("Chat:MaxSlowSyncs", "3");
        });
    }

    public void Dispose() => _factory.Dispose();

    private async Task<WebSocket> ConnectReady(string cookie)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => { r.Headers["Cookie"] = $"__Host-gom={cookie}"; r.Headers["Origin"] = AppFixture.Origin; };
        var ws = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        Assert.Equal("ready", (await Next(ws)).GetProperty("t").GetString());
        return ws;
    }

    private sealed class ClosedException(int code) : Exception($"closed with {code}") { public int Code { get; } = code; }

    private static async Task<JsonElement> Next(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[16384];
        var text = new StringBuilder();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) throw new ClosedException(ws.CloseStatus is { } s ? (int)s : -1);
            text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
        } while (!r.EndOfMessage);
        return JsonDocument.Parse(text.ToString()).RootElement.Clone();
    }

    /// <summary>Asks to catch up and returns the answer's kind: "events", "not_found" or "slow_down" (the "t" of the frame, or the error code).</summary>
    private static async Task<string> Ask(WebSocket ws, Guid chat)
    {
        await ws.SendAsync(Encoding.UTF8.GetBytes($$"""{"t":"sync","c":"{{chat}}","after":0}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        return Kind(await Next(ws));
    }

    private static string Kind(JsonElement frame) => frame.GetProperty("t").GetString() == "error" ? frame.GetProperty("error").GetString()! : frame.GetProperty("t").GetString()!;

    private static async Task<int> WaitForClose(WebSocket ws)
    {
        try { while (true) await Next(ws); }
        catch (ClosedException c) { return c.Code; }
    }

    private async Task<(string Cookie, Guid Chat)> Pair()
    {
        var (a, ca) = await _app.Member(); var (b, _) = await _app.Member();
        await _app.Befriend(a, b);
        return (ca, await _app.OpenChat(ca, b.GomId));
    }

    [Fact]
    public async Task A_burst_is_answered_and_then_the_socket_is_told_to_slow_down_and_stays_open()
    {
        var (cookie, chat) = await Pair();
        using var ws = await ConnectReady(cookie);

        for (var i = 0; i < 3; i++) Assert.Equal("events", await Ask(ws, chat));
        await ws.SendAsync(Encoding.UTF8.GetBytes($$"""{"t":"sync","c":"{{chat}}","after":0}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        var told = await Next(ws);
        Assert.Equal("error", told.GetProperty("t").GetString());
        Assert.Equal("slow_down", told.GetProperty("error").GetString());
        Assert.Equal(chat, told.GetProperty("c").GetGuid());   // the page needs to know which catch-up to ask for again
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task Time_gives_tokens_back_and_a_request_let_through_starts_the_count_of_turned_away_ones_again()
    {
        var (cookie, chat) = await Pair();
        using var ws = await ConnectReady(cookie);
        for (var i = 0; i < 3; i++) await Ask(ws, chat);

        Assert.Equal("slow_down", await Ask(ws, chat));
        Assert.Equal("slow_down", await Ask(ws, chat));        // two in a row; the limit for closing is three
        _app.Clock.Advance(TimeSpan.FromSeconds(1));            // one token back
        Assert.Equal("events", await Ask(ws, chat));            // let through: the count starts again
        Assert.Equal("slow_down", await Ask(ws, chat));
        Assert.Equal("slow_down", await Ask(ws, chat));
        Assert.Equal(WebSocketState.Open, ws.State);            // four turned away in all, but never three in a row
    }

    [Fact]
    public async Task A_socket_that_keeps_asking_after_being_told_to_slow_down_is_closed_for_trying_again_later()
    {
        var (cookie, chat) = await Pair();
        using var ws = await ConnectReady(cookie);
        for (var i = 0; i < 3; i++) await Ask(ws, chat);

        for (var i = 0; i < 3; i++)
            await ws.SendAsync(Encoding.UTF8.GetBytes($$"""{"t":"sync","c":"{{chat}}","after":0}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal(1013, await WaitForClose(ws));
    }

    [Fact]
    public async Task Frames_that_cannot_be_read_use_no_tokens_and_each_socket_has_its_own_burst()
    {
        var (cookie, chat) = await Pair();
        using var first = await ConnectReady(cookie);
        foreach (var junk in new[] { "not json", "[]", """{"t":"dance"}""" })
        {
            await first.SendAsync(Encoding.UTF8.GetBytes(junk), WebSocketMessageType.Text, true, CancellationToken.None);
            Assert.Equal("bad_request", Kind(await Next(first)));
        }
        for (var i = 0; i < 3; i++) Assert.Equal("events", await Ask(first, chat));   // the whole burst was still there
        Assert.Equal("slow_down", await Ask(first, chat));

        using var second = await ConnectReady(cookie);   // the same person on another device
        Assert.Equal("events", await Ask(second, chat));
    }
}
