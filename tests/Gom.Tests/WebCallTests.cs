using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Chat;

namespace Gom.Tests;

/// <summary>Calls switched on, the Android app not taking them yet: only open pages that take calls can be called.</summary>
public sealed class WebCallFixture : CallFixture
{
    protected override bool AppsTakeCalls => false;
}

/// <summary>
/// Who can be called while calls are on the web only (docs/voice-calls.md, section 2b): a person with a page open that said, as its
/// socket connected, that it takes calls. The app's registration does not count, and a call never wakes it.
/// </summary>
public class WebCallTests(WebCallFixture app) : IClassFixture<WebCallFixture>
{
    private const string Offer = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n";

    private async Task<(string CA, string CB, string GomIdB, Guid Chat)> Friends()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return (ca, cb, b.GomId, await app.OpenChat(ca, b.GomId));
    }

    private Task<HttpResponseMessage> Start(string cookie, Guid chat) =>
        app.Call(HttpMethod.Post, "/api/calls", new { conversationId = chat, clientId = Guid.NewGuid(), sdp = Offer, v = CallEndpoints.ProtocolVersion }, cookie);

    private async Task<WebSocket> Page(string cookie, string query = "")
    {
        var client = app.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => { r.Headers["Cookie"] = $"__Host-gom={cookie}"; r.Headers["Origin"] = AppFixture.Origin; };
        var ws = await client.ConnectAsync(new Uri("ws://localhost/ws" + query), CancellationToken.None);
        Assert.Equal("ready", (await Next(ws)).GetProperty("t").GetString());
        return ws;
    }

    private static async Task<JsonElement> Next(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[65536];
        var r = await ws.ReceiveAsync(buffer, cts.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)).RootElement.Clone();
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString();

    [Fact]
    public async Task Only_an_open_page_that_takes_calls_makes_its_person_callable()
    {
        var p = await Friends();
        await app.App(p.CB);                       // the app alone does not count yet
        using var plain = await Page(p.CB);        // nor a page that did not say it takes calls
        using (var r = await Start(p.CA, p.Chat)) { Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("cannot_take_calls", await ErrorOf(r)); }
        using (var r = await app.Call(HttpMethod.Post, "/api/calls/relay", new { conversationId = p.Chat }, p.CA)) Assert.Equal("cannot_take_calls", await ErrorOf(r));

        using var page = await Page(p.CB, "?calls=1");
        using (var r = await app.Call(HttpMethod.Post, "/api/calls/relay", new { conversationId = p.Chat }, p.CA)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await Start(p.CA, p.Chat)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var ring = await Next(page);
        Assert.Equal(("call", "incoming"), (ring.GetProperty("t").GetString(), ring.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Once_that_page_closes_its_person_cannot_be_called()
    {
        var p = await Friends();
        var page = await Page(p.CB, "?calls=1");
        await page.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        page.Dispose();
        HttpStatusCode status = default;
        for (var i = 0; i < 50; i++)   // the server lets go of the socket off the request path
        {
            using var r = await app.Call(HttpMethod.Post, "/api/calls/relay", new { conversationId = p.Chat }, p.CA);
            if ((status = r.StatusCode) == HttpStatusCode.Conflict) break;
            await Task.Delay(20);
        }
        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    [Fact]
    public async Task A_call_never_wakes_the_app_while_apps_do_not_take_calls()
    {
        var p = await Friends();
        var token = await app.App(p.CB);
        using var page = await Page(p.CB, "?calls=1");
        Guid id;
        using (var r = await Start(p.CA, p.Chat)) id = (await AppFixture.Json(r)).GetProperty("id").GetGuid();
        using (await app.Call(HttpMethod.Post, $"/api/calls/{id}/hangup", cookie: p.CA)) { }
        await Task.Delay(300);
        Assert.Empty(app.Fcm.To(token));
    }
}
