using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gom.Web;

namespace Gom.Tests;

/// <summary>A page older than the server's <see cref="Pages.PageVersion"/> is told to reload, on its requests and on the socket.</summary>
public class PageVersionTests(AppFixture app) : IClassFixture<AppFixture>
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("-1", false)]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("2", false)]
    public void Only_a_page_that_says_a_lower_number_is_too_old(string? sent, bool expected) =>
        Assert.Equal(expected, Pages.TooOld(sent));

    private async Task<HttpResponseMessage> Me(string cookie, string? version)
    {
        using var client = app.Client();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("Cookie", $"__Host-gom={cookie}");
        if (version is not null) request.Headers.Add(Pages.PageVersionHeader, version);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task An_older_page_is_told_to_reload_and_a_current_one_or_one_that_says_nothing_is_answered()
    {
        var (_, cookie) = await app.Member();
        using var old = await Me(cookie, (Pages.PageVersion - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        Assert.Equal("update_required", (await old.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        using var current = await Me(cookie, Pages.PageVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var silent = await Me(cookie, null);
        Assert.Equal(HttpStatusCode.OK, silent.StatusCode);
    }

    [Fact]
    public async Task An_older_page_on_the_socket_is_closed_with_4426_and_a_current_one_is_ready()
    {
        var (_, cookie) = await app.Member();
        async Task<System.Net.WebSockets.WebSocket> Connect(int version)
        {
            var client = app.Factory.Server.CreateWebSocketClient();
            client.ConfigureRequest = r => { r.Headers["Cookie"] = $"__Host-gom={cookie}"; r.Headers["Origin"] = AppFixture.Origin; };
            return await client.ConnectAsync(new Uri($"ws://localhost/ws?page={version}"), CancellationToken.None);
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];

        using var old = await Connect(Pages.PageVersion - 1);
        var closed = await old.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Close, closed.MessageType);
        Assert.Equal(4426, (int)old.CloseStatus!.Value);

        using var current = await Connect(Pages.PageVersion);
        var first = await current.ReceiveAsync(buffer, cts.Token);
        Assert.Equal("ready", JsonDocument.Parse(buffer.AsMemory(0, first.Count)).RootElement.GetProperty("t").GetString());
    }

    [Fact]
    public void The_page_sends_the_header_the_server_reads()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gom.slnx"))) dir = dir.Parent;
        var scripts = Path.Combine(dir!.FullName, "src", "Gom.Web", "Scripts");
        Assert.Contains($"'{Pages.PageVersionHeader}'", File.ReadAllText(Path.Combine(scripts, "api.ts")), StringComparison.Ordinal);
        Assert.Contains("page=", File.ReadAllText(Path.Combine(scripts, "main.ts")), StringComparison.Ordinal);
    }
}
