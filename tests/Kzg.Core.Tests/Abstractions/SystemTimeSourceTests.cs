using Kzg.Core.Abstractions;

namespace Kzg.Core.Tests.Abstractions;

public class SystemTimeSourceTests
{
    [Fact]
    public void Real_time_starts_near_zero_and_never_goes_back()
    {
        var time = new SystemTimeSource();

        var first = time.Now;
        var second = time.Now;

        Assert.InRange(first, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.True(second >= first);
    }

    [Fact]
    public void The_fake_starts_away_from_zero_and_moves_only_when_advanced()
    {
        var time = new FakeTimeSource();
        ITimeSource source = time;

        Assert.Equal(TimeSpan.FromHours(5), source.Now);
        time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(TimeSpan.FromHours(5) + TimeSpan.FromMilliseconds(20), source.Now);
    }
}
