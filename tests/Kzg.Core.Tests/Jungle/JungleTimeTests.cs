using Kzg.Core.Jungle;
using static Kzg.Core.Tests.Jungle.JungleTestHelpers;

namespace Kzg.Core.Tests.Jungle;

public class JungleTimeTests
{
    [Theory]
    [InlineData("24:30", 24, 30)]
    [InlineData("4:05", 4, 5)]
    [InlineData("04:05", 4, 5)]
    [InlineData("35:00", 35, 0)]
    [InlineData("0:00", 0, 0)]
    [InlineData("  20:00  ", 20, 0)]
    [InlineData("34:59", 34, 59)]
    public void Parses_sync_times(string text, int minutes, int seconds)
    {
        Assert.True(JungleTime.TryParse(text, out var time, out var error));
        Assert.Equal(Mmss(minutes, seconds), time);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("24")]
    [InlineData("24:5")]
    [InlineData("24:60")]
    [InlineData("24:30:10")]
    [InlineData(":30")]
    [InlineData("-1:30")]
    [InlineData("24:30 extra")]
    [InlineData("100:00")]
    public void Rejects_bad_times_with_the_usage(string? text)
    {
        Assert.False(JungleTime.TryParse(text, out _, out var error));
        Assert.Equal(JungleTime.SyncUsage, error);
    }

    [Theory]
    [InlineData("35:01")]
    [InlineData("36:00")]
    public void Rejects_times_above_the_match_start(string text)
    {
        Assert.False(JungleTime.TryParse(text, out _, out var error));
        Assert.Contains("35:00", error);
        Assert.EndsWith(JungleTime.SyncUsage, error);
    }

    [Theory]
    [InlineData(0, 0, "00:00")]
    [InlineData(24, 30, "24:30")]
    [InlineData(35, 0, "35:00")]
    [InlineData(4, 5, "04:05")]
    public void Formats_game_time(int m, int s, string expected) =>
        Assert.Equal(expected, JungleTime.Format(Mmss(m, s)));

    [Fact]
    public void Format_rounds_down_and_never_goes_negative()
    {
        Assert.Equal("24:29", JungleTime.Format(TimeSpan.FromSeconds((24 * 60) + 29.9)));
        Assert.Equal("00:00", JungleTime.Format(TimeSpan.FromSeconds(-3)));
    }
}
