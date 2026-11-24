using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gom.Web.Chat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gom.Tests;

/// <summary>A socket that says it is still there, and a log that says who closed it and when.</summary>
public class SocketHealthTests(AppFixture app) : IClassFixture<AppFixture>
{
    private sealed class Capture : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => categoryName == "Gom.Web.Chat.Live.Socket" ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue($"{logLevel}|{formatter(state, exception)}");
        public void Dispose() { }
    }

    private WebApplicationFactory<Program> With(Capture? capture = null, int? heartbeat = null) => app.Factory.WithWebHostBuilder(b =>
    {
        if (heartbeat is { } h) b.UseSetting("Chat:HeartbeatSeconds", h.ToString());
        if (capture is not null) b.ConfigureLogging(l => l.AddProvider(capture));
    });

    private async Task<WebSocket> Connect(WebApplicationFactory<Program> factory, string cookie)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => { r.Headers["Cookie"] = $"__Host-gom={cookie}"; r.Headers["Origin"] = AppFixture.Origin; };
        var ws = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        Assert.Equal("ready", (await Next(ws)).GetProperty("t").GetString());
        return ws;
    }

    private static async Task<JsonElement> Next(WebSocket ws, TimeSpan? within = null)
    {
        using var cts = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(5));
        var buffer = new byte[16384];
        var r = await ws.ReceiveAsync(buffer, cts.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)).RootElement.Clone();
    }

    private static async Task<string?> FirstLine(Capture c, string containing)
    {
        for (var i = 0; i < 60; i++)
        {
            var line = c.Lines.FirstOrDefault(l => l.Contains(containing));
            if (line is not null) return line;
            await Task.Delay(100);
        }
        return null;
    }

    [Fact]
    public void The_heartbeat_is_every_25_seconds_unless_set_otherwise()
    {
        Assert.Equal(25, app.Factory.Services.GetRequiredService<IOptions<ChatOptions>>().Value.HeartbeatSeconds);
    }

    [Fact]
    public async Task A_connected_socket_is_sent_a_heartbeat_again_and_again()
    {
        var (_, cookie) = await app.Member();
        using var factory = With(heartbeat: 1);
        using var ws = await Connect(factory, cookie);
        for (var i = 0; i < 3; i++)
            Assert.Equal("hb", (await Next(ws, TimeSpan.FromSeconds(4))).GetProperty("t").GetString());
    }

    [Fact]
    public async Task A_heartbeat_is_not_counted_against_what_the_client_sends()
    {
        var (_, cookie) = await app.Member();
        using var factory = With(heartbeat: 1);
        using var ws = await Connect(factory, cookie);
        await Task.Delay(2500);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{bad"), WebSocketMessageType.Text, true, CancellationToken.None);
        var seen = new List<string>();
        for (var i = 0; i < 4 && seen.Count(s => s == "error") == 0; i++) seen.Add((await Next(ws)).GetProperty("t").GetString()!);
        Assert.Contains("error", seen);
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task A_close_by_the_client_is_logged_with_its_code_and_age_and_never_with_what_the_client_wrote()
    {
        var (_, cookie) = await app.Member();
        var capture = new Capture();
        using var factory = With(capture);
        using var ws = await Connect(factory, cookie);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "evil\nINJECTED line", CancellationToken.None);
        var line = await FirstLine(capture, "Socket closed");
        Assert.NotNull(line);
        Assert.StartsWith("Information|", line);
        Assert.Contains("by the client", line);
        Assert.Contains("code 1000", line);
        Assert.Contains(" s", line);
        Assert.DoesNotContain("INJECTED", line);
    }

    [Fact]
    public async Task A_close_by_the_server_is_logged_with_its_code_and_its_own_reason()
    {
        var (_, cookie) = await app.Member();
        var capture = new Capture();
        using var factory = With(capture);
        using var ws = await Connect(factory, cookie);
        await ws.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        var line = await FirstLine(capture, "Socket closed");
        Assert.NotNull(line);
        Assert.Contains("by the server", line);
        Assert.Contains("code 1003", line);
        Assert.Contains("text only", line);
    }

    [Fact]
    public async Task A_line_that_drops_without_a_close_frame_is_logged_as_lost_not_as_a_client_close()
    {
        var (_, cookie) = await app.Member();
        var capture = new Capture();
        using var factory = With(capture);
        var ws = await Connect(factory, cookie);
        await Task.Delay(1100);
        ws.Abort();
        var line = await FirstLine(capture, "Socket closed");
        Assert.NotNull(line);
        Assert.Contains("line lost", line);
        Assert.DoesNotContain("by the client", line);
        Assert.DoesNotContain("by the server", line);
        Assert.Contains("after 1 s", line);
    }
}
