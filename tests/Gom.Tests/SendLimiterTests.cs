using Gom.Web.Chat;

namespace Gom.Tests;

public class SendLimiterTests
{
    private readonly FakeClock _clock = new();
    private SendLimiter Make(int burst = 3, double perSecond = 1, int maxEntries = 10_000) => new(_clock, burst, perSecond, maxEntries);

    [Fact]
    public void A_burst_is_allowed_then_the_wait_is_for_the_next_token()
    {
        var limiter = Make();
        var user = Guid.NewGuid();

        for (var i = 0; i < 3; i++) Assert.Null(limiter.TryTake(user));

        Assert.Equal(TimeSpan.FromSeconds(1), limiter.TryTake(user));
    }

    [Fact]
    public void Tokens_come_back_with_time_but_never_above_the_burst()
    {
        var limiter = Make();
        var user = Guid.NewGuid();
        for (var i = 0; i < 3; i++) limiter.TryTake(user);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(limiter.TryTake(user));
        Assert.Null(limiter.TryTake(user));
        Assert.NotNull(limiter.TryTake(user));

        _clock.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < 3; i++) Assert.Null(limiter.TryTake(user));
        Assert.NotNull(limiter.TryTake(user));
    }

    [Fact]
    public void A_partial_wait_asks_for_only_what_is_left()
    {
        var limiter = Make();
        var user = Guid.NewGuid();
        for (var i = 0; i < 3; i++) limiter.TryTake(user);

        _clock.Advance(TimeSpan.FromMilliseconds(400));

        Assert.Equal(TimeSpan.FromMilliseconds(600), limiter.TryTake(user));
    }

    [Fact]
    public void People_are_counted_separately()
    {
        var limiter = Make(burst: 1);
        Assert.Null(limiter.TryTake(Guid.NewGuid()));
        Assert.Null(limiter.TryTake(Guid.NewGuid()));
    }

    [Fact]
    public void Memory_stays_bounded_however_many_people_send()
    {
        var limiter = Make(burst: 2, maxEntries: 50);
        for (var i = 0; i < 500; i++) limiter.TryTake(Guid.NewGuid());
        Assert.True(limiter.Count <= 50, $"{limiter.Count} entries");
    }
}
