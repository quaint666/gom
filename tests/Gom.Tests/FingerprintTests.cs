using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Gom.Web;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gom.Tests;

/// <summary>Fingerprinted file names: what the list is checked against, how a page is rewritten, and what the server says about the files.</summary>
public sealed class FingerprintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gom-assets-" + Guid.NewGuid().ToString("N"));

    public FingerprintTests() => Directory.CreateDirectory(Path.Combine(_root, "js"));
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..Assets.HashLength];

    /// <summary>A plain file and its copy under the right name, and the list naming them.</summary>
    private string Made(string content = "console.log(1)", string name = "main")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var copy = $"js/{name}.{Hash(bytes)}.js";
        File.WriteAllBytes(Path.Combine(_root, "js", $"{name}.js"), bytes);
        File.WriteAllBytes(Path.Combine(_root, copy), bytes);
        File.WriteAllText(Path.Combine(_root, "assets.json"), $$"""{ "/js/{{name}}.js": "/{{copy}}" }""");
        return "/" + copy;
    }

    private IReadOnlyDictionary<string, string> Load() => Assets.Load(new PhysicalFileProvider(_root), NullLogger.Instance);

    [Fact]
    public void A_copy_that_is_what_its_name_says_is_used()
    {
        var copy = Made();
        Assert.Equal(copy, Load()["/js/main.js"]);
    }

    [Fact]
    public void With_no_list_nothing_is_renamed()
    {
        Assert.Empty(Load());
    }

    [Fact]
    public void A_list_that_cannot_be_read_renames_nothing()
    {
        File.WriteAllText(Path.Combine(_root, "assets.json"), "not json");
        Assert.Empty(Load());
        File.WriteAllText(Path.Combine(_root, "assets.json"), "null");
        Assert.Empty(Load());
    }

    [Fact]
    public void A_list_left_over_from_an_older_build_is_not_believed()
    {
        Made();
        File.WriteAllText(Path.Combine(_root, "js", "main.js"), "console.log(2)");   // the plain file changed; the copy did not
        Assert.Empty(Load());
    }

    [Fact]
    public void A_copy_that_is_missing_or_not_the_same_as_the_plain_file_is_not_used()
    {
        var copy = Made();
        File.WriteAllText(Path.Combine(_root, copy.TrimStart('/')), "something else");   // named right, but not the same bytes
        Assert.Empty(Load());
        File.Delete(Path.Combine(_root, copy.TrimStart('/')));
        Assert.Empty(Load());
    }

    [Fact]
    public void A_copy_named_after_some_other_hash_is_not_used()
    {
        Made();
        File.WriteAllText(Path.Combine(_root, "assets.json"), """{ "/js/main.js": "/js/main.0000000000.js" }""");
        File.Copy(Path.Combine(_root, "js", "main.js"), Path.Combine(_root, "js", "main.0000000000.js"));
        Assert.Empty(Load());
    }

    [Fact]
    public void A_page_names_the_copies_and_changes_nothing_else()
    {
        var names = new Dictionary<string, string> { ["/js/main.js"] = "/js/main.abcdef0123.js" };
        var html = """<script type="module" src="/js/main.js"></script> <a href="/js/main.js.map">/js/main.js in words</a>""";
        Assert.Equal("""<script type="module" src="/js/main.abcdef0123.js"></script> <a href="/js/main.js.map">/js/main.js in words</a>""", Assets.Apply(html, names));
        Assert.Equal(html, Assets.Apply(html, new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("main.0123456789.js", true)]
    [InlineData("gom.abcdef0123.css", true)]
    [InlineData("main.js", false)]
    [InlineData("main.012345678.js", false)]      // a short hash
    [InlineData("main.0123456789.js.map", false)]
    [InlineData("main.ABCDEF0123.js", false)]
    [InlineData("BeVietnamPro-Regular.woff2", false)]
    public void Only_the_names_this_scheme_makes_count_as_fingerprinted(string name, bool expected) =>
        Assert.Equal(expected, Assets.IsFingerprinted(name));
}

/// <summary>On the built site (npm run build): the pages name the copies, and the copies may be kept for a year.</summary>
public class BuiltSiteTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly string[] Plain = ["/js/main.js", "/css/gom.css", "/theme-init.js"];

    [Fact]
    public async Task A_page_loads_fingerprinted_copies_that_are_kept_for_a_year_while_the_plain_names_still_work()
    {
        var html = await (await app.Call(HttpMethod.Get, "/sign-in")).Content.ReadAsStringAsync();
        foreach (var plain in Plain)
        {
            var stem = Regex.Escape(Path.ChangeExtension(plain, null));
            var copy = Regex.Match(html, $"\"({stem}\\.[0-9a-f]{{10}}{Regex.Escape(Path.GetExtension(plain))})\"");
            Assert.True(copy.Success, $"the page names no fingerprinted copy of {plain} (was npm run build run?)");
            Assert.DoesNotContain($"\"{plain}\"", html);

            using var kept = await app.Call(HttpMethod.Get, copy.Groups[1].Value);
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
            Assert.Equal("public, max-age=31536000, immutable", kept.Headers.CacheControl?.ToString());
            // The name is the content's hash, and the plain file is the same bytes.
            var bytes = await kept.Content.ReadAsByteArrayAsync();
            Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(bytes))[..10], copy.Groups[1].Value);
            using var plainResponse = await app.Call(HttpMethod.Get, plain);
            Assert.Equal(bytes, await plainResponse.Content.ReadAsByteArrayAsync());
            Assert.Equal("no-cache", plainResponse.Headers.CacheControl?.ToString());
        }
    }

    [Fact]
    public async Task The_page_itself_is_still_never_stored()
    {
        using var page = await app.Call(HttpMethod.Get, "/sign-in");
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
    }
}
