using Kzg.App.Input;

namespace Kzg.App.Input.Tests;

public class ElevationWatchTests
{
    private const int Own = 100;

    private readonly FakeTimeSource _time = new();
    private readonly FakeProbe _probe = new();
    private readonly ElevationWatch _watch;

    public ElevationWatchTests() => _watch = new ElevationWatch(_probe, _time, Own);

    private sealed class FakeProbe : IForegroundProbe
    {
        public ForegroundApp? Foreground { get; set; }

        public int Reads { get; private set; }

        public ForegroundApp? Read()
        {
            Reads++;
            return Foreground;
        }
    }

    [Fact]
    public void An_elevated_window_in_front_is_noticed_at_the_first_look_and_reported_as_a_change()
    {
        _probe.Foreground = new ForegroundApp(7, IsElevated: true);

        Assert.True(_watch.Poll());
        Assert.True(_watch.ElevatedWindowInFront);
        Assert.False(_watch.Poll());
    }

    [Fact]
    public void It_looks_at_most_once_a_second()
    {
        _probe.Foreground = new ForegroundApp(7, IsElevated: false);
        _watch.Poll();

        _time.Advance(TimeSpan.FromMilliseconds(900));
        _watch.Poll();
        Assert.Equal(1, _probe.Reads);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        _watch.Poll();
        Assert.Equal(2, _probe.Reads);
    }

    [Fact]
    public void A_normal_window_in_front_clears_it()
    {
        _probe.Foreground = new ForegroundApp(7, IsElevated: true);
        _watch.Poll();
        _time.Advance(TimeSpan.FromSeconds(1));
        _probe.Foreground = new ForegroundApp(8, IsElevated: false);

        Assert.True(_watch.Poll());
        Assert.False(_watch.ElevatedWindowInFront);
    }

    [Fact]
    public void This_apps_own_window_and_an_unreadable_foreground_change_nothing()
    {
        _probe.Foreground = new ForegroundApp(7, IsElevated: true);
        _watch.Poll();

        _time.Advance(TimeSpan.FromSeconds(1));
        _probe.Foreground = new ForegroundApp(Own, IsElevated: false);
        Assert.False(_watch.Poll());
        Assert.True(_watch.ElevatedWindowInFront);

        _time.Advance(TimeSpan.FromSeconds(1));
        _probe.Foreground = null;
        Assert.False(_watch.Poll());
        Assert.True(_watch.ElevatedWindowInFront);
    }

    [Fact]
    public void The_system_probe_sees_nothing_off_Windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Null(new SystemForegroundProbe().Read());
    }
}
