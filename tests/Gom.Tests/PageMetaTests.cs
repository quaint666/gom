using System.Text;
using Gom.Web.Previews;

namespace Gom.Tests;

/// <summary>Reading what a page says about itself, and finding the address in a message. Both take text from strangers.</summary>
public class PageMetaTests
{
    private static readonly Uri Page = new("https://example.com/a/b");
    private static PageMeta Read(string html) => PageMetaReader.Read(Encoding.UTF8.GetBytes(html), Page);

    [Fact]
    public void The_open_graph_tags_come_first_and_the_title_tag_is_the_fallback()
    {
        var full = Read("<head><title>Cũ</title><meta property='og:title' content='Mới'><meta name=\"description\" content=\"mô tả thường\"><meta property=\"og:description\" content=\"mô tả og\"><meta property=og:site_name content=Gom></head>");
        Assert.Equal(new PageMeta("Mới", "mô tả og", "Gom", null), full);
        var plain = Read("<head><title>  Chỉ có\n  tiêu đề </title><meta name=description content='và mô tả'></head>");
        Assert.Equal("Chỉ có tiêu đề", plain.Title);
        Assert.Equal("và mô tả", plain.Description);
        Assert.Equal("example.com", plain.Site);   // no site name given: the host
    }

    [Fact]
    public void Attributes_may_come_in_any_order_with_any_quotes_and_entities_are_decoded()
    {
        var m = Read("<meta content=\"Tom &amp; Jerry &#8211; &quot;hay&quot;\" property='og:title'>");
        Assert.Equal("Tom & Jerry – \"hay\"", m.Title);
    }

    [Fact]
    public void Text_is_made_plain_and_short()
    {
        var m = Read($"<meta property='og:title' content='a\u0000b\u0007c   d'><meta property='og:description' content='{new string('ạ', 500)}'>");
        Assert.Equal("a b c d", m.Title);   // a control character separates like a space
        Assert.Equal(301, m.Description!.Length);
        Assert.EndsWith("…", m.Description);
        Assert.Null(Read("<meta property='og:title' content='   '>").Title);
    }

    [Theory]
    [InlineData("/pic.png", "https://example.com/pic.png")]
    [InlineData("pic.png", "https://example.com/a/pic.png")]
    [InlineData("//cdn.example.org/x.jpg", "https://cdn.example.org/x.jpg")]
    [InlineData("http://other.example/x.jpg", "http://other.example/x.jpg")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:image/png;base64,AAAA", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("ftp://example.com/x.png", null)]
    [InlineData("https://user:pw@example.com/x.png", null)]
    public void The_picture_address_is_made_whole_and_only_http_and_https_are_kept(string given, string? expected) =>
        Assert.Equal(expected, Read($"<meta property='og:image' content='{given}'>").Image);

    [Fact]
    public void Only_the_head_is_read_and_a_page_of_junk_does_not_hang_the_reader()
    {
        Assert.Null(Read("<head></head><body><meta property='og:title' content='in the body'></body>").Title);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Read("<meta " + string.Concat(Enumerable.Repeat("a=\"", 20000)));
        Read(string.Concat(Enumerable.Repeat("<meta ", 50000)));
        Read("<title>" + new string('x', 400_000));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("xem https://example.com/a?b=1, nhé", "https://example.com/a?b=1")]
    [InlineData("vào www.example.com.", "https://www.example.com/")]
    [InlineData("(https://en.wikipedia.org/wiki/Gom_(chat))", "https://en.wikipedia.org/wiki/Gom_(chat)")]
    [InlineData("https://a.example/1 và https://b.example/2", "https://a.example/1")]
    [InlineData("javascript:alert(1) https://c.example", "https://c.example/")]
    [InlineData("https://google.com@evil.example/x https://d.example", "https://d.example/")]
    [InlineData("thuwww.example.com", null)]
    [InlineData("http://localhost:5080 và https://intranet", null)]
    [InlineData("không có gì", null)]
    public void The_first_address_is_found_the_way_the_page_draws_links(string body, string? expected) =>
        Assert.Equal(expected, LinkFinder.First(body)?.AbsoluteUri);

    [Fact]
    public void A_message_of_nothing_but_candidates_is_answered_quickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        LinkFinder.First(string.Concat(Enumerable.Repeat("http://", 5000)));
        LinkFinder.First("https://a." + new string('b', 15000));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }
}
