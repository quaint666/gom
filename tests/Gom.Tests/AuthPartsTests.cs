using Gom.Web.Auth;

namespace Gom.Tests;

public class DeviceLabelTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36", "Chrome on Android")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1", "Safari on iPhone")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0", "Firefox on Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15", "Safari on Mac")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36", "Chrome on Linux")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/126.0.0.0 Mobile/15E148 Safari/604.1", "Chrome on iPad")]
    [InlineData("curl/8.5.0", "Unknown device")]
    [InlineData("", "Unknown device")]
    public void Gives_a_short_label_and_nothing_else(string userAgent, string expected) =>
        Assert.Equal(expected, DeviceLabel.From(userAgent));

    [Fact]
    public void A_missing_header_is_unknown() => Assert.Equal("Unknown device", DeviceLabel.From(null));
}

public class SignInThrottleTests
{
    private readonly FakeClock _clock = new();
    private SignInThrottle New(int maxEntries = 10_000) => new(_clock, maxEntries);

    /// <summary>One wrong password, as the app makes it: wait out any wait, then begin (which counts it).</summary>
    private void Fail(SignInThrottle t, string ip, string gomId)
    {
        _clock.Advance(t.Wait(ip, gomId));
        Assert.Equal(TimeSpan.Zero, t.Begin(ip, gomId));
    }

    [Fact]
    public void Attempts_sent_together_meet_the_wait_too_because_each_is_counted_before_it_is_checked()
    {
        var t = New();
        var let = Enumerable.Range(0, 30).Count(_ => t.Begin("1.1.1.1", "me") == TimeSpan.Zero);
        Assert.Equal(3, let);
    }

    [Fact]
    public void An_attempt_that_was_never_checked_does_not_count()
    {
        var t = New();
        for (var i = 0; i < 3; i++) { t.Begin("1.1.1.1", "me"); t.Undo("1.1.1.1", "me"); }
        Assert.Equal(TimeSpan.Zero, t.Wait("1.1.1.1", "me"));
    }

    [Fact]
    public void Right_passwords_from_one_address_never_slow_it()
    {
        // A family behind one address, each signing in.
        var t = New();
        for (var i = 0; i < 10; i++) { Assert.Equal(TimeSpan.Zero, t.Begin("1.1.1.1", "person" + i)); t.Succeeded("1.1.1.1", "person" + i); }
        Assert.Equal(TimeSpan.Zero, t.Wait("1.1.1.1", "someone"));
    }

    [Fact]
    public void The_first_failures_are_free()
    {
        var t = New();
        for (var i = 0; i < 2; i++) Fail(t, "1.1.1.1", "me");
        Assert.Equal(TimeSpan.Zero, t.Wait("1.1.1.1", "me"));
    }

    [Fact]
    public void After_three_failures_the_wait_doubles_each_time()
    {
        var t = New();
        for (var i = 0; i < 3; i++) Fail(t, "1.1.1.1", "me");
        Assert.Equal(TimeSpan.FromSeconds(1), t.Wait("1.1.1.1", "me"));
        Fail(t, "1.1.1.1", "me");
        Assert.Equal(TimeSpan.FromSeconds(2), t.Wait("1.1.1.1", "me"));
        Fail(t, "1.1.1.1", "me");
        Assert.Equal(TimeSpan.FromSeconds(4), t.Wait("1.1.1.1", "me"));
    }

    [Fact]
    public void The_wait_counts_down_and_is_never_longer_than_a_minute()
    {
        var t = New();
        for (var i = 0; i < 30; i++) Fail(t, "1.1.1.1", "me");
        Assert.Equal(TimeSpan.FromSeconds(60), t.Wait("1.1.1.1", "me"));
        _clock.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.FromSeconds(15), t.Wait("1.1.1.1", "me"));
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.Zero, t.Wait("1.1.1.1", "me"));
    }

    [Fact]
    public void One_address_trying_many_IDs_is_slowed_whatever_the_ID()
    {
        var t = New();
        for (var i = 0; i < 3; i++) Fail(t, "1.1.1.1", "id" + i);
        Assert.True(t.Wait("1.1.1.1", "a_new_id") > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, t.Wait("2.2.2.2", "a_new_id"));
    }

    [Fact]
    public void Many_addresses_trying_one_ID_are_slowed_for_that_ID_whatever_the_address()
    {
        var t = New();
        for (var i = 0; i < 3; i++) Fail(t, "9.9.9." + i, "victim");
        Assert.True(t.Wait("5.5.5.5", "victim") > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, t.Wait("5.5.5.5", "someone_else"));
    }

    [Fact]
    public void A_success_clears_that_ID_but_not_the_address()
    {
        // Otherwise an attacker could reset their own address by signing in to an account of their own.
        var t = New();
        for (var i = 0; i < 4; i++) Fail(t, "1.1.1.1", "me");
        t.Succeeded("1.1.1.1", "me");
        Assert.Equal(TimeSpan.Zero, t.Wait("2.2.2.2", "me"));
        Assert.True(t.Wait("1.1.1.1", "me") > TimeSpan.Zero);
    }

    [Fact]
    public void Failures_are_forgotten_after_15_quiet_minutes()
    {
        var t = New();
        for (var i = 0; i < 6; i++) Fail(t, "1.1.1.1", "me");
        _clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(TimeSpan.Zero, t.Wait("1.1.1.1", "me"));
        Fail(t, "1.1.1.1", "me");
        Assert.Equal(TimeSpan.Zero, t.Wait("1.1.1.1", "me")); // counting starts again from one
    }

    [Fact]
    public void Memory_stays_bounded_when_an_attacker_invents_IDs()
    {
        var t = New(maxEntries: 100);
        for (var i = 0; i < 1_000; i++) Fail(t, "10.0." + i, "made_up_" + i);
        Assert.True(t.EntryCount <= 200, $"{t.EntryCount} entries");
    }
}

public class HashLimiterTests
{
    [Fact]
    public async Task Runs_the_work_and_returns_its_result()
    {
        var limiter = new HashLimiter(2, TimeSpan.FromSeconds(1));
        Assert.Equal(7, await limiter.RunAsync(() => 7));
    }

    [Fact]
    public async Task When_every_slot_is_busy_a_new_caller_gives_up_after_the_timeout()
    {
        var limiter = new HashLimiter(1, TimeSpan.FromMilliseconds(50));
        var release = new ManualResetEventSlim();
        var busy = limiter.RunAsync(() => { release.Wait(); return 1; });
        await Task.Delay(100); // let it take the slot

        var second = await limiter.RunAsync(() => 2);

        release.Set();
        Assert.Equal(1, await busy);
        Assert.Null(second);
    }

    [Fact]
    public async Task A_slot_is_given_back_even_when_the_work_throws()
    {
        var limiter = new HashLimiter(1, TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<InvalidOperationException>(() => limiter.RunAsync<int>(() => throw new InvalidOperationException()));
        Assert.Equal(3, await limiter.RunAsync(() => 3));
    }
}
