using System.Diagnostics;
using System.Net;
using Gom.Web.Previews;
using Microsoft.Extensions.Options;

namespace Gom.Tests;

/// <summary>The part of link previews that touches the internet for strangers: what it will and will not connect to.</summary>
public class PreviewFetchTests
{
    public static TheoryData<string, bool> Addresses => new()
    {
        // Public IPv4 stays public.
        { "8.8.8.8", true }, { "1.1.1.1", true }, { "93.184.216.34", true }, { "172.15.255.255", true }, { "172.32.0.1", true }, { "100.63.255.255", true }, { "100.128.0.1", true },
        // Everything that is this machine or a network of its own.
        { "0.0.0.0", false }, { "10.0.0.1", false }, { "10.255.255.255", false }, { "127.0.0.1", false }, { "127.255.255.254", false },
        { "169.254.169.254", false }, { "169.254.0.1", false }, { "172.16.0.1", false }, { "172.31.255.255", false }, { "172.17.0.1", false },
        { "192.168.0.1", false }, { "192.168.255.255", false }, { "100.64.0.1", false }, { "100.127.255.255", false },
        { "192.0.0.1", false }, { "192.0.2.1", false }, { "198.18.0.1", false }, { "198.19.255.255", false }, { "198.51.100.7", false }, { "203.0.113.7", false },
        { "224.0.0.1", false }, { "239.255.255.255", false }, { "240.0.0.1", false }, { "255.255.255.255", false }, { "192.88.99.1", false },
        // IPv6: only global unicast, and not the special parts of it.
        { "2606:4700:4700::1111", true }, { "2001:4860:4860::8888", true },
        { "::1", false }, { "::", false }, { "fe80::1", false }, { "fc00::1", false }, { "fd00:ec2::254", false }, { "ff02::1", false },
        { "2001:db8::1", false }, { "2001:0000:4136:e378:8000:63bf:3fff:fdd2", false }, { "2002:7f00:1::", false }, { "64:ff9b::7f00:1", false },
        // IPv4 written inside IPv6 is judged as the IPv4 it is.
        { "::ffff:127.0.0.1", false }, { "::ffff:10.0.0.1", false }, { "::ffff:169.254.169.254", false }, { "::ffff:8.8.8.8", true },
    };

    [Theory]
    [MemberData(nameof(Addresses))]
    public void Only_public_addresses_may_be_connected_to(string address, bool allowed) =>
        Assert.Equal(allowed, AddressGuard.IsPublic(IPAddress.Parse(address)));

    private static IOptions<PreviewOptions> Options(int port, Action<PreviewOptions>? tweak = null)
    {
        var o = new PreviewOptions { AllowedPorts = [port], TimeoutSeconds = 3 };
        tweak?.Invoke(o);
        return Microsoft.Extensions.Options.Options.Create(o);
    }

    private static readonly Func<string, bool> Html = t => t == "text/html";

    /// <summary>A fetcher that may use 127.0.0.1 only, so 127.0.0.2 stands in for "an address that must not be reached".</summary>
    private static SafeFetcher Lenient(int port, Action<PreviewOptions>? tweak = null) => new(Options(port, tweak), ip => ip.Equals(IPAddress.Loopback));

    [Fact]
    public async Task It_fetches_a_page_it_is_allowed_to_reach()
    {
        await using var site = new LocalSite();
        using var fetcher = Lenient(site.Port);
        var got = await fetcher.GetAsync(new Uri(site.Url("/page")), Html, 512 * 1024, "text/html", default);
        Assert.NotNull(got);
        Assert.Equal("text/html", got.ContentType);
        Assert.False(got.Cut);
        Assert.Contains("Chuyến đi Đà Lạt", System.Text.Encoding.UTF8.GetString(got.Body));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("[::1]")]
    [InlineData("2130706433")]     // 127.0.0.1 as one number
    [InlineData("0x7f.0.0.1")]
    [InlineData("127.1")]
    [InlineData("0")]
    public async Task With_the_real_rules_nothing_on_this_machine_can_be_reached_however_it_is_written(string host)
    {
        await using var site = new LocalSite();
        using var fetcher = new SafeFetcher(Options(site.Port));
        var got = await fetcher.GetAsync(new Uri($"http://{host}:{site.Port}/page"), Html, 512 * 1024, "text/html", default);
        Assert.Null(got);
        Assert.Equal(0, site.Total);   // not a single request arrived
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[fd00:ec2::254]/latest/meta-data/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.17.0.1/")]
    [InlineData("http://100.64.0.1/")]
    public async Task The_cloud_metadata_address_and_private_networks_are_refused_before_any_connection(string address)
    {
        using var fetcher = new SafeFetcher(Options(80, o => o.AllowedPorts = [80, 443]));
        var watch = Stopwatch.StartNew();
        Assert.Null(await fetcher.GetAsync(new Uri(address), Html, 1000, "text/html", default));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "it was refused, not left to time out");
    }

    [Fact]
    public async Task A_port_that_is_not_allowed_is_not_tried()
    {
        await using var site = new LocalSite();
        using var fetcher = Lenient(site.Port + 1);
        Assert.Null(await fetcher.GetAsync(new Uri(site.Url("/page")), Html, 1000, "text/html", default));
        Assert.Equal(0, site.Total);
    }

    [Fact]
    public async Task Anything_but_http_and_https_and_any_address_with_a_name_in_front_is_refused()
    {
        await using var site = new LocalSite();
        using var fetcher = Lenient(site.Port);
        Assert.Null(await fetcher.GetAsync(new Uri("file:///etc/passwd"), Html, 1000, "text/html", default));
        Assert.Null(await fetcher.GetAsync(new Uri("ftp://127.0.0.1/x"), Html, 1000, "text/html", default));
        Assert.Null(await fetcher.GetAsync(new Uri($"http://user:pw@127.0.0.1:{site.Port}/page"), Html, 1000, "text/html", default));
        Assert.Equal(0, site.Total);
    }

    [Fact]
    public async Task Redirects_are_followed_up_to_three_and_then_given_up_on()
    {
        await using var site = new LocalSite();
        using var fetcher = Lenient(site.Port);
        Assert.NotNull(await fetcher.GetAsync(new Uri(site.Url("/hop/2")), Html, 512 * 1024, "text/html", default));   // /hop/2 -> /hop/1 -> /hop/0 -> /page: three redirects
        Assert.Null(await fetcher.GetAsync(new Uri(site.Url("/hop/3")), Html, 512 * 1024, "text/html", default));
    }

    [Fact]
    public async Task A_redirect_is_checked_like_a_first_address()
    {
        await using var site = new LocalSite();
        using var fetcher = Lenient(site.Port);
        Assert.Null(await fetcher.GetAsync(new Uri(site.Url("/to-127-0-0-2")), Html, 512 * 1024, "text/html", default));
        Assert.Equal(0, site.HitsFor("127.0.0.2"));    // the second address was never connected to
        Assert.Equal(1, site.HitsFor("127.0.0.1"));

        Assert.Null(await fetcher.GetAsync(new Uri(site.Url("/to-file")), Html, 512 * 1024, "text/html", default));
        using var strict = new SafeFetcher(Options(80, o => o.AllowedPorts = [80, site.Port]));
        Assert.Null(await strict.GetAsync(new Uri(site.Url("/to-metadata")), Html, 512 * 1024, "text/html", default));
    }

    [Fact]
    public async Task A_page_that_never_answers_is_given_up_on_at_the_time_limit()
    {
        await using var site = new LocalSite();
        using var fetcher = Lenient(site.Port, o => o.TimeoutSeconds = 1);
        var watch = Stopwatch.StartNew();
        Assert.Null(await fetcher.GetAsync(new Uri(site.Url("/slow")), Html, 512 * 1024, "text/html", default));
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.8, 4);
    }

    [Fact]
    public async Task A_long_page_is_cut_at_the_limit_and_a_packed_one_cannot_swell_past_it()
    {
        await using var site = new LocalSite();
        using var fetcher = Lenient(site.Port);
        var big = await fetcher.GetAsync(new Uri(site.Url("/big")), Html, 100_000, "text/html", default);
        Assert.NotNull(big);
        Assert.True(big.Cut);
        Assert.Equal(100_000, big.Body.Length);

        var bomb = await fetcher.GetAsync(new Uri(site.Url("/gzip-bomb")), Html, 100_000, "text/html", default);
        Assert.NotNull(bomb);
        Assert.True(bomb.Cut);
        Assert.Equal(100_000, bomb.Body.Length);   // 200 MB packed in a few hundred KB, and only 100 KB was ever read
    }

    [Fact]
    public async Task Content_of_the_wrong_kind_gives_nothing()
    {
        await using var site = new LocalSite();
        using var fetcher = Lenient(site.Port);
        Assert.Null(await fetcher.GetAsync(new Uri(site.Url("/text")), Html, 1000, "text/html", default));
        Assert.Null(await fetcher.GetAsync(new Uri(site.Url("/missing")), Html, 1000, "text/html", default));
    }
}
