using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Gom.Web.Chat;

namespace Gom.Tests;

/// <summary>Each person's colour: kept with their account, one list of codes everywhere, and every colour readable.</summary>
public partial class AppearanceTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gom.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, "src", "Gom.Web", .. parts]));
    }

    private async Task<string?> PaletteOf(string cookie)
    {
        using var r = await app.Call(HttpMethod.Get, "/api/me", cookie: cookie);
        return (await AppFixture.Json(r)).GetProperty("palette").GetString();
    }

    [Fact]
    public async Task Everyone_starts_in_sage_and_a_colour_they_choose_follows_them_to_every_device()
    {
        var (user, cookie) = await app.Member();
        Assert.Equal("sage", await PaletteOf(cookie));
        using (var r = await app.Call(HttpMethod.Put, "/api/me/palette", new { palette = "lotus" }, cookie)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("lotus", await PaletteOf(await app.SignIn(user.GomId)));
    }

    [Theory]
    [InlineData("pink")]
    [InlineData("Sea")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Only_a_colour_Gom_has_is_accepted(string? palette)
    {
        var (_, cookie) = await app.Member();
        using var r = await app.Call(HttpMethod.Put, "/api/me/palette", new { palette }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_palette", (await AppFixture.Json(r)).GetProperty("error").GetString());
        Assert.Equal("sage", await PaletteOf(cookie));
    }

    [Fact]
    public void The_server_the_page_the_stylesheet_and_both_languages_know_the_same_colours_in_the_same_order()
    {
        var page = PageList().Match(Source("Scripts", "theme.ts"));
        Assert.True(page.Success, "PALETTES not found in theme.ts");
        Assert.Equal(Appearance.Palettes, Quoted().Matches(page.Groups[1].Value).Select(m => m.Groups[1].Value));

        var css = Source("wwwroot", "css", "gom.css");
        var blocks = CssBlock().Matches(css).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(Appearance.Palettes.Skip(1), blocks);   // sage is the default, in its own block
        Assert.Contains("[data-swatch=\"sage\"]", css);

        foreach (var table in new[] { "vi.ts", "en.ts" })
        {
            var names = Names().Match(Source("Scripts", "strings", table));
            Assert.True(names.Success, $"palettes not found in {table}");
            Assert.Equal(Appearance.Palettes, NameKey().Matches(names.Groups[1].Value).Select(m => m.Groups[1].Value));
        }
    }

    [Fact]
    public async Task Everyone_starts_with_no_background_and_one_they_choose_follows_them_while_an_unknown_one_is_refused()
    {
        var (user, cookie) = await app.Member();
        async Task<string?> PatternOf(string c)
        {
            using var r = await app.Call(HttpMethod.Get, "/api/me", cookie: c);
            return (await AppFixture.Json(r)).GetProperty("pattern").GetString();
        }
        Assert.Equal("plain", await PatternOf(cookie));
        using (var r = await app.Call(HttpMethod.Put, "/api/me/pattern", new { pattern = "tiles" }, cookie)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("tiles", await PatternOf(await app.SignIn(user.GomId)));
        foreach (var bad in new[] { "stars", "Tiles", "", null })
        {
            using var r = await app.Call(HttpMethod.Put, "/api/me/pattern", new { pattern = bad }, cookie);
            Assert.Equal("invalid_pattern", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        Assert.Equal("tiles", await PatternOf(cookie));
    }

    [Fact]
    public async Task The_server_the_page_the_stylesheet_the_files_and_both_languages_know_the_same_backgrounds()
    {
        var page = PatternList().Match(Source("Scripts", "theme.ts"));
        Assert.True(page.Success, "PATTERNS not found in theme.ts");
        Assert.Equal(Appearance.Patterns, Quoted().Matches(page.Groups[1].Value).Select(m => m.Groups[1].Value));

        var css = Source("wwwroot", "css", "gom.css");
        Assert.Equal(Appearance.Patterns.Skip(1), PatternRule().Matches(css).Select(m => m.Groups[1].Value));   // plain has none
        foreach (var code in Appearance.Patterns.Skip(1))
        {
            Assert.Contains($"url(\"/patterns/{code}.svg\")", css);
            var svg = System.Xml.Linq.XElement.Parse(Source("wwwroot", "patterns", code + ".svg"));
            Assert.NotNull(svg.Attribute("width"));   // the tile's size comes from the file
            Assert.NotNull(svg.Attribute("height"));
            using var r = await app.Call(HttpMethod.Get, $"/patterns/{code}.svg");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("image/svg+xml", r.Content.Headers.ContentType!.MediaType);
            Assert.Equal("public, max-age=604800", r.Headers.CacheControl!.ToString());
        }

        foreach (var table in new[] { "vi.ts", "en.ts" })
        {
            var names = PatternNames().Match(Source("Scripts", "strings", table));
            Assert.True(names.Success, $"patterns not found in {table}");
            Assert.Equal(Appearance.Patterns, NameKey().Matches(names.Groups[1].Value).Select(m => m.Groups[1].Value));
        }
    }

    /// <summary>
    /// Every colour, light and dark: white on the button, links on the page, the tint's text on the tint, the words and times on your
    /// own bubble, and grey text (a day, a notice) where it crosses the background's pattern, all at least 4.5 to 1 (WCAG AA), worked
    /// out from the stylesheet itself.
    /// </summary>
    [Fact]
    public void Every_colour_is_readable_in_light_and_in_dark()
    {
        var css = Source("wwwroot", "css", "gom.css");
        var neutrals = Tokens(css[css.IndexOf(":root {", StringComparison.Ordinal)..css.IndexOf("\n}\n", StringComparison.Ordinal)]);
        var sageAt = css.IndexOf(":root, [data-swatch=\"sage\"] {", StringComparison.Ordinal);
        var palettes = new Dictionary<string, Dictionary<string, (string Light, string Dark)>>
        {
            ["sage"] = Tokens(css[sageAt..css.IndexOf('}', sageAt)]),
        };
        foreach (Match m in CssBlock().Matches(css)) palettes[m.Groups[1].Value] = Tokens(css[m.Index..css.IndexOf('}', m.Index)]);
        Assert.Equal(Appearance.Palettes.Count, palettes.Count);

        var low = new List<string>();
        foreach (var (code, t) in palettes)
            foreach (var dark in new[] { false, true })
            {
                string V(Dictionary<string, (string Light, string Dark)> from, string name) => dark ? from[name].Dark : from[name].Light;
                var pairs = new (string What, string Fore, string Back)[]
                {
                    ("white on the button", V(neutrals, "--on-btn"), V(t, "--btn")),
                    ("link on the page", V(t, "--link"), V(neutrals, "--bg")),
                    ("tint text on the tint", V(t, "--tint-text"), V(t, "--tint")),
                    ("words on your bubble", V(neutrals, "--text"), V(t, "--out")),
                    ("times on your bubble", V(neutrals, "--muted"), V(t, "--out")),
                    ("grey text on the background's pattern", V(neutrals, "--muted"), V(t, "--pat")),
                };
                foreach (var (what, fore, back) in pairs)
                {
                    var ratio = Contrast(fore, back);
                    if (ratio < 4.5) low.Add($"{code} {(dark ? "dark" : "light")}: {what} {ratio:0.00}");
                }
            }
        Assert.Empty(low);
    }

    private static Dictionary<string, (string Light, string Dark)> Tokens(string block)
    {
        var tokens = new Dictionary<string, (string, string)>();
        foreach (Match m in Token().Matches(block))
        {
            var value = m.Groups[2].Value.Trim();
            var pair = LightDark().Match(value);
            tokens[m.Groups[1].Value] = pair.Success ? (pair.Groups[1].Value, pair.Groups[2].Value) : (value, value);
        }
        return tokens;
    }

    private static double Contrast(string a, string b)
    {
        static double L(string hex)
        {
            double C(int i)
            {
                var c = int.Parse(hex.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * C(0) + 0.7152 * C(1) + 0.0722 * C(2);
        }
        var (x, y) = (L(a), L(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [GeneratedRegex(@"export const PALETTES = \[([^\]]*)\]")]
    private static partial Regex PageList();
    [GeneratedRegex(@"export const PATTERNS = \[([^\]]*)\]")]
    private static partial Regex PatternList();
    [GeneratedRegex(@"\.backdrop\[data-pattern=""([a-z]+)""\] \{ --pattern")]
    private static partial Regex PatternRule();
    [GeneratedRegex(@"patterns: \{([^}]*)\}")]
    private static partial Regex PatternNames();
    [GeneratedRegex(@"'([a-z]+)'")]
    private static partial Regex Quoted();
    [GeneratedRegex(@":root\[data-palette=""([a-z]+)""\]")]
    private static partial Regex CssBlock();
    [GeneratedRegex(@"palettes: \{([^}]*)\}")]
    private static partial Regex Names();
    [GeneratedRegex(@"([a-z]+): '")]
    private static partial Regex NameKey();
    [GeneratedRegex(@"(--[a-z-]+):\s*([^;]+);")]
    private static partial Regex Token();
    [GeneratedRegex(@"light-dark\((#[0-9A-Fa-f]{6}),\s*(#[0-9A-Fa-f]{6})\)")]
    private static partial Regex LightDark();
}
