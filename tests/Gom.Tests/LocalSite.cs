using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gom.Tests;

/// <summary>
/// A small web site on this machine for the link-preview tests to fetch from. It listens on every loopback address, and notes which
/// local address each request came in on, so a test can tell "127.0.0.1" from "127.0.0.2" and whether anything arrived at all.
/// </summary>
public sealed class LocalSite : IAsyncDisposable
{
    private readonly WebApplication _app;
    public int Port { get; }
    public List<string> Hits { get; } = [];
    public string Html { get; set; } = Page("Chuyến đi Đà Lạt", "Ảnh và kế hoạch cho cả nhà", "Gom Blog", "/pic.png");
    public byte[] Picture { get; set; } = TestImages.Png(300, 200);

    public static string Page(string title, string description, string site, string? image) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>ignored {title}</title><meta property=\"og:title\" content=\"{title}\"><meta property=\"og:description\" content=\"{description}\">" +
        $"<meta property=\"og:site_name\" content=\"{site}\">{(image is null ? "" : $"<meta property=\"og:image\" content=\"{image}\">")}</head><body>hello</body></html>";

    public LocalSite()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, 0));
        _app = builder.Build();
        _app.Use(async (ctx, next) => { lock (Hits) Hits.Add($"{ctx.Connection.LocalIpAddress}{ctx.Request.Path}"); await next(); });
        _app.MapGet("/page", (HttpContext c) => Results.Content(Html, "text/html; charset=utf-8"));
        _app.MapGet("/pic.png", () => Results.Bytes(Picture, "image/png"));
        _app.MapGet("/svg", () => Results.Content("<svg xmlns='http://www.w3.org/2000/svg'/>", "image/svg+xml"));
        _app.MapGet("/text", () => Results.Text("just words", "text/plain"));
        _app.MapGet("/empty", () => Results.Content("<html><head></head><body>x</body></html>", "text/html"));
        _app.MapGet("/big", () => Results.Content("<html><head><title>Lớn</title></head><body>" + new string('x', 2_000_000) + "</body></html>", "text/html"));
        _app.MapGet("/slow", async (HttpContext c) => { await Task.Delay(TimeSpan.FromSeconds(30), c.RequestAborted); return Results.Content(Html, "text/html"); });
        _app.MapGet("/hop/{n:int}", (int n, HttpContext c) => Results.Redirect(n <= 0 ? "/page" : $"/hop/{n - 1}", permanent: false));
        _app.MapGet("/to-127-0-0-2", (HttpContext c) => Results.Redirect($"http://127.0.0.2:{c.Connection.LocalPort}/page"));
        _app.MapGet("/to-metadata", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
        _app.MapGet("/to-file", () => Results.Redirect("file:///etc/passwd"));
        _app.MapGet("/gzip-bomb", async (HttpContext c) =>
        {
            c.Response.Headers.ContentEncoding = "gzip";
            c.Response.ContentType = "text/html";
            await using var gz = new GZipStream(c.Response.Body, CompressionLevel.SmallestSize, leaveOpen: true);
            var zeros = new byte[1 << 20];
            for (var i = 0; i < 200; i++) await gz.WriteAsync(zeros);   // 200 MB when unpacked, a few hundred KB packed
        });
        _app.StartAsync().GetAwaiter().GetResult();
        Port = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    public string Url(string path, string host = "127.0.0.1") => $"http://{host}:{Port}{path}";
    public int HitsFor(string localAddress) { lock (Hits) return Hits.Count(h => h.StartsWith(localAddress + "/", StringComparison.Ordinal) || h.StartsWith(localAddress.Replace("::ffff:", "") + "/", StringComparison.Ordinal)); }
    public int Total { get { lock (Hits) return Hits.Count; } }
    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
