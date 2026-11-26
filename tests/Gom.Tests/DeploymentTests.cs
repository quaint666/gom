using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gom.Tests;

public class DeploymentTests(AppFixture app) : IClassFixture<AppFixture>
{
    [Theory]
    [InlineData("/healthz")]
    [InlineData("/")]
    [InlineData("/sign-in")]
    [InlineData("/friends")]
    public async Task A_monitor_may_ask_with_HEAD(string path)
    {
        using var response = await app.Call(HttpMethod.Head, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The page policy is <c>style-src 'self'</c>: a style attribute is refused, also the one a template writes on its first draw.
    /// Styles that depend on a value go through the style object after drawing (see placeMenu in chat.ts).
    /// </summary>
    [Fact]
    public void No_script_writes_an_inline_style_attribute()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gom.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var scripts = Path.Combine(dir.FullName, "src", "Gom.Web", "Scripts");
        Assert.True(Directory.Exists(scripts));
        var offenders = Directory.EnumerateFiles(scripts, "*.ts", SearchOption.AllDirectories)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"\sstyle="))
            .Select(f => Path.GetRelativePath(dir.FullName, f)).ToList();
        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gom.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    /// <summary>
    /// The hidden time text in a message is positioned absolutely. Without a positioned scroller around it the page itself grows and scrolls
    /// off the chat. The scroller must be positioned; this guards the rule in the stylesheet.
    /// </summary>
    [Fact]
    public void The_message_scroller_is_positioned_so_hidden_text_cannot_stretch_the_page()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Gom.Web", "wwwroot", "css", "gom.css"));
        var rule = System.Text.RegularExpressions.Regex.Match(css, @"(?m)^\.msgs \{([^}]*)\}");
        Assert.True(rule.Success);
        Assert.Contains("position: relative", rule.Groups[1].Value);
    }

    /// <summary>A bubble is as wide as its picture. A percentage in the picture grid's maximum width is ignored when the bubble works out its own width, so the bubble then stretches to its limit and leaves an uneven strip beside the picture.</summary>
    [Fact]
    public void The_picture_grid_has_a_fixed_maximum_width_so_the_bubble_fits_the_picture()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Gom.Web", "wwwroot", "css", "gom.css"));
        var rule = System.Text.RegularExpressions.Regex.Match(css, @"(?m)^\.pics \{([^}]*)\}");
        Assert.True(rule.Success);
        var max = System.Text.RegularExpressions.Regex.Match(rule.Groups[1].Value, @"max-inline-size:\s*([^;]+);");
        Assert.True(max.Success);
        Assert.DoesNotContain("%", max.Groups[1].Value);
    }

    /// <summary>The chat page must never scroll as a page: on a phone a page that can scroll a little ends up with its header off the top and a blank strip under the composer.</summary>
    [Fact]
    public void The_chat_page_itself_cannot_scroll()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Gom.Web", "wwwroot", "css", "gom.css"));
        var rule = System.Text.RegularExpressions.Regex.Match(css, @"(?m)^html:has\(> body\[data-page=""home""\]\), body\[data-page=""home""\] \{([^}]*)\}");
        Assert.True(rule.Success);
        Assert.Contains("overflow: hidden", rule.Groups[1].Value);
        Assert.Contains(Gom.Web.Pages.All, p => p.Route == "/" && p.Name == "home");
    }

    /// <summary>On a phone the keyboard must shrink the page, not cover the composer or leave a gap.</summary>
    [Fact]
    public void Every_page_lets_the_keyboard_resize_the_content()
    {
        var pages = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Gom.Web", "wwwroot", "pages"), "*.html").ToList();
        Assert.NotEmpty(pages);
        Assert.All(pages, p => Assert.Contains("interactive-widget=resizes-content", File.ReadAllText(p)));
    }

    /// <summary>Motion is for people who have not asked for less: every animation and transition sits inside the no-preference media query.</summary>
    [Fact]
    public void Every_animation_and_transition_is_inside_the_reduced_motion_gate()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Gom.Web", "wwwroot", "css", "gom.css"));
        var stack = new Stack<bool>();   // for each open block: is it (inside) the gate
        var header = new System.Text.StringBuilder();
        var offenders = new List<string>();
        var declaration = new System.Text.StringBuilder();
        foreach (var ch in css)
        {
            if (ch == '{')
            {
                var gate = (stack.Count > 0 && stack.Peek()) || header.ToString().Contains("prefers-reduced-motion: no-preference", StringComparison.Ordinal);
                stack.Push(gate);
                header.Clear(); declaration.Clear();
            }
            else if (ch == '}') { if (stack.Count > 0) stack.Pop(); header.Clear(); declaration.Clear(); }
            else if (ch == ';')
            {
                var d = declaration.ToString().Trim();
                if ((d.StartsWith("animation", StringComparison.Ordinal) || d.StartsWith("transition", StringComparison.Ordinal)) && !(stack.Count > 0 && stack.Peek()))
                    offenders.Add(d);
                declaration.Clear(); header.Append(ch);
            }
            else { header.Append(ch); declaration.Append(ch); }
        }
        Assert.Empty(offenders);
    }

    /// <summary>
    /// The image's migration-bundle stage copies the source folders one by one (so it stays cached when only scripts change). A new
    /// folder of C# that is not in that list breaks the image build, and nothing else notices until CI builds the image. This does.
    /// </summary>
    [Fact]
    public void Every_folder_of_csharp_is_copied_into_the_migration_bundle_stage_of_the_image()
    {
        var root = RepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var web = Path.Combine(root, "src", "Gom.Web");
        var skip = new[] { "bin", "obj", "node_modules", "Scripts", "ScriptTests", "wwwroot", "Properties" };
        var folders = Directory.EnumerateDirectories(web).Select(Path.GetFileName).Where(n => n is not null && !skip.Contains(n))
            .Where(n => Directory.EnumerateFiles(Path.Combine(web, n!), "*.cs", SearchOption.AllDirectories).Any()).ToList();
        Assert.NotEmpty(folders);
        var missing = folders.Where(n => !dockerfile.Contains($"COPY src/Gom.Web/{n} ./src/Gom.Web/{n}", StringComparison.Ordinal)).ToList();
        Assert.Empty(missing);
    }

    /// <summary>The same for the C# files beside Program.cs, which that stage names one by one (Pages.cs once broke the image this way).</summary>
    [Fact]
    public void Every_csharp_file_beside_Program_is_copied_into_the_migration_bundle_stage_of_the_image()
    {
        var root = RepoRoot();
        var line = File.ReadAllLines(Path.Combine(root, "Dockerfile")).Single(l => l.StartsWith("COPY src/Gom.Web/Program.cs ", StringComparison.Ordinal));
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Gom.Web"), "*.cs").Select(Path.GetFileName).ToList();
        Assert.Contains("Program.cs", files);
        Assert.DoesNotContain(files, f => !line.Contains($"src/Gom.Web/{f} ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_method_an_address_does_not_take_is_a_405()
    {
        using var response = await app.Call(HttpMethod.Put, "/healthz");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Every_answer_says_which_features_of_the_device_the_page_may_not_use()
    {
        using var response = await app.Call(HttpMethod.Get, "/sign-in");
        Assert.Equal("camera=(), microphone=(self), geolocation=()", response.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact]
    public async Task A_script_is_checked_with_the_server_each_time_and_a_page_is_never_stored()
    {
        using var script = await app.Call(HttpMethod.Get, "/js/main.js");
        using var page = await app.Call(HttpMethod.Get, "/sign-in");
        Assert.Equal("no-cache", script.Headers.CacheControl?.ToString());
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_script_answers_a_HEAD_with_an_etag_that_stays_the_same_until_the_file_changes()
    {
        // The page tells a new version is out by asking this about its own script.
        using var first = await app.Call(HttpMethod.Head, "/js/main.js");
        using var second = await app.Call(HttpMethod.Head, "/js/main.js");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(string.IsNullOrEmpty(first.Headers.ETag?.Tag));
        Assert.Equal(first.Headers.ETag?.Tag, second.Headers.ETag?.Tag);
        Assert.Empty(await first.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_font_may_be_kept_for_a_week()
    {
        using var font = await app.Call(HttpMethod.Get, "/fonts/BeVietnamPro-Regular.woff2");
        Assert.Equal(HttpStatusCode.OK, font.StatusCode);
        Assert.Equal("public, max-age=604800", font.Headers.CacheControl?.ToString());
    }

    [Fact]
    public void The_database_commands_are_not_written_to_the_log_in_production()
    {
        // At Information, EF prints every statement; the level is set in appsettings.json for every environment.
        var level = app.Factory.Services.GetRequiredService<IConfiguration>()["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"];
        Assert.Equal("Warning", level);
    }
}

/// <summary>What the app believes about the visitor behind a proxy chain.</summary>
public class ProxyTests(AppFixture app) : IClassFixture<AppFixture>
{
    /// <summary>Shows the address the app ends up with, after the forwarded-headers step, in a response header.</summary>
    private sealed class ShowAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, run) =>
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["X-Seen"] = $"{context.Connection.RemoteIpAddress}|{context.Request.Scheme}";
                    return Task.CompletedTask;
                });
                await run();
            });
            next(builder);
        };
    }

    private async Task<string> Seen(string? hops)
    {
        var factory = app.Factory.WithWebHostBuilder(b =>
        {
            if (hops is not null) b.UseSetting("ForwardedHeaders:Hops", hops);
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter, ShowAddress>());
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("X-Test-Ip", "10.9.9.9");
        request.Headers.Add("X-Forwarded-For", "1.2.3.4, 5.6.7.8");
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await client.SendAsync(request);
        return response.Headers.GetValues("X-Seen").Single();
    }

    [Fact]
    public async Task With_no_proxy_the_forwarded_headers_are_not_believed() =>
        Assert.Equal("10.9.9.9|http", await Seen(null));

    [Fact]
    public async Task With_two_proxies_the_visitor_is_the_second_address_from_the_right() =>
        Assert.Equal("1.2.3.4|https", await Seen("2"));

    [Fact]
    public async Task With_one_proxy_the_visitor_is_the_last_address() =>
        Assert.Equal("5.6.7.8|https", await Seen("1"));

    [Fact]
    public void A_negative_number_of_proxies_stops_the_app_at_start()
    {
        var factory = app.Factory.WithWebHostBuilder(b => b.UseSetting("ForwardedHeaders:Hops", "-1"));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
}
