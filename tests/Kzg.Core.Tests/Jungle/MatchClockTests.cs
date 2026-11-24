using Kzg.Core.Jungle;
using Kzg.Core.Testing;

using static Kzg.Core.Tests.Jungle.JungleTestHelpers;

namespace Kzg.Core.Tests.Jungle;

public class MatchClockTests
{
    private readonly ManualTimeSource _time = new();
    private readonly MatchClock _clock;

    public MatchClockTests() => _clock = new MatchClock(_time);

    [Fact]
    public void Idle_shows_full_match_and_does_not_move()
    {
        _time.Advance(100);
        Assert.Equal(ClockState.Idle, _clock.State);
        Assert.Equal(Mmss(35, 0), _clock.GameTime);
    }

    [Fact]
    public void Start_counts_down_from_35()
    {
        _clock.Start();
        _time.Advance(90);
        Assert.Equal(ClockState.Running, _clock.State);
        Assert.Equal(Mmss(33, 30), _clock.GameTime);
    }

    [Fact]
    public void Pause_freezes_and_resume_continues()
    {
        _clock.Start();
        _time.Advance(60);
        _clock.Pause();
        _time.Advance(300);
        Assert.Equal(ClockState.Paused, _clock.State);
        Assert.Equal(Mmss(34, 0), _clock.GameTime);

        _clock.Resume();
        _time.Advance(10);
        Assert.Equal(ClockState.Running, _clock.State);
        Assert.Equal(Mmss(33, 50), _clock.GameTime);
    }

    [Fact]
    public void Pause_and_resume_in_wrong_state_do_nothing()
    {
        _clock.Pause();
        _clock.Resume();
        Assert.Equal(ClockState.Idle, _clock.State);

        _clock.Start();
        _clock.Resume();
        _time.Advance(5);
        Assert.Equal(Mmss(34, 55), _clock.GameTime);
    }

    [Fact]
    public void Sync_moves_the_running_clock()
    {
        _clock.Start();
        _time.Advance(30);
        _clock.Sync(Mmss(24, 30));
        _time.Advance(5);
        Assert.Equal(Mmss(24, 25), _clock.GameTime);
    }

    [Fact]
    public void Sync_while_paused_sets_the_frozen_time()
    {
        _clock.Start();
        _clock.Pause();
        _clock.Sync(Mmss(19, 0));
        _time.Advance(50);
        Assert.Equal(ClockState.Paused, _clock.State);
        Assert.Equal(Mmss(19, 0), _clock.GameTime);

        _clock.Resume();
        _time.Advance(1);
        Assert.Equal(Mmss(18, 59), _clock.GameTime);
    }

    [Fact]
    public void Sync_from_idle_starts_the_clock()
    {
        _clock.Sync(Mmss(10, 0));
        _time.Advance(1);
        Assert.Equal(ClockState.Running, _clock.State);
        Assert.Equal(Mmss(9, 59), _clock.GameTime);
    }

    [Fact]
    public void Sync_above_35_is_clamped_and_negative_ends_the_match()
    {
        _clock.Sync(Mmss(40, 0));
        Assert.Equal(Mmss(35, 0), _clock.GameTime);
        _clock.Sync(TimeSpan.FromSeconds(-5));
        Assert.Equal(ClockState.Over, _clock.State);
    }

    [Fact]
    public void Clock_is_over_at_zero_and_stays_there()
    {
        _clock.Start();
        _time.Advance(35 * 60);
        Assert.Equal(ClockState.Over, _clock.State);
        Assert.Equal(TimeSpan.Zero, _clock.GameTime);
        _time.Advance(100);
        Assert.Equal(TimeSpan.Zero, _clock.GameTime);
    }

    [Fact]
    public void Stop_returns_to_idle()
    {
        _clock.Start();
        _time.Advance(10);
        _clock.Stop();
        Assert.Equal(ClockState.Idle, _clock.State);
        Assert.Equal(Mmss(35, 0), _clock.GameTime);
    }

    [Fact]
    public void Start_restarts_a_running_match()
    {
        _clock.Start();
        _time.Advance(600);
        _clock.Start();
        Assert.Equal(Mmss(35, 0), _clock.GameTime);
    }
}
