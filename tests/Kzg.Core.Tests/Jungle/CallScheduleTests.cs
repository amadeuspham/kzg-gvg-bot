using Kzg.Core.Jungle;
using Kzg.Core.Testing;

using static Kzg.Core.Tests.Jungle.JungleTestHelpers;

namespace Kzg.Core.Tests.Jungle;

public class CallScheduleTests
{
    private readonly ManualTimeSource _time = new();
    private readonly MatchClock _clock;
    private readonly CallSchedule _schedule;

    public CallScheduleTests()
    {
        _clock = new MatchClock(_time);
        _schedule = new CallSchedule(_clock);
    }

    /// <summary>Advances in 100 ms ticks like the bot does and collects every call that fires.</summary>
    private List<JungleCall> Run(double seconds)
    {
        var fired = new List<JungleCall>();
        for (var i = 0; i < (int)Math.Round(seconds * 10); i++)
        {
            _time.Advance(0.1);
            fired.AddRange(_schedule.TakeDue());
        }

        return fired;
    }

    [Fact]
    public void Schedule_has_ten_calls_in_game_order()
    {
        var expected = new[]
        {
            Mmss(25, 40), Mmss(25, 20), Mmss(20, 40), Mmss(20, 20), Mmss(15, 40),
            Mmss(15, 20), Mmss(10, 40), Mmss(10, 20), Mmss(5, 40), Mmss(5, 20),
        };
        Assert.Equal(expected, _schedule.Calls.Select(c => c.At));
    }

    [Fact]
    public void Calls_carry_spawn_lead_and_clip()
    {
        var first = _schedule.Calls[0];
        Assert.Equal(new JungleCall(Mmss(25, 0), TimeSpan.FromSeconds(40), Mmss(25, 40), JungleClip.Jungle40), first);
        Assert.Equal(JungleClip.Jungle20, _schedule.Calls[1].Clip);
        Assert.Equal(JungleClip.JungleHalftime40, _schedule.Calls[2].Clip);
        Assert.Equal(JungleClip.JungleHalftime20, _schedule.Calls[3].Clip);
        Assert.All(_schedule.Calls.Skip(4), c => Assert.True(c.Clip is JungleClip.Jungle40 or JungleClip.Jungle20));
    }

    [Fact]
    public void Full_match_fires_ten_calls_once_each_in_time()
    {
        _clock.Start();
        var fired = Run(35 * 60);
        Assert.Equal(_schedule.Calls, fired);
    }

    [Fact]
    public void Call_fires_at_its_time_not_before()
    {
        _clock.Start();
        Assert.Empty(Run(9 * 60 + 19)); // game 25:41
        Assert.Single(Run(1.5));        // passes 25:40
    }

    [Fact]
    public void Nothing_is_due_while_idle_or_paused()
    {
        _clock.Sync(Mmss(25, 41));
        _clock.Pause();
        _time.Advance(100);
        Assert.Empty(_schedule.TakeDue());
        _clock.Stop();
        Assert.Empty(_schedule.TakeDue());
    }

    [Fact]
    public void Pause_shifts_every_later_call()
    {
        _clock.Start();
        var before = Run(15 * 60 - 5); // game 20:05: both 20:xx calls already fired
        Assert.Equal(4, before.Count);

        _clock.Pause();
        _time.Advance(120);
        Assert.Empty(_schedule.TakeDue());
        _clock.Resume();

        var after = Run(5 * 60 + 1); // game 15:04
        Assert.Equal([Mmss(15, 40), Mmss(15, 20)], after.Select(c => c.At));
    }

    [Fact]
    public void Pause_just_before_a_call_delays_it_until_resume_time_passes()
    {
        _clock.Sync(Mmss(20, 45));
        Run(3); // 20:42
        _clock.Pause();
        _time.Advance(500);
        _clock.Resume();
        var fired = Run(3); // passes 20:40
        Assert.Equal([Mmss(20, 40)], fired.Select(c => c.At));
    }

    [Fact]
    public void Sync_inside_the_grace_window_fires_the_call()
    {
        _clock.Start();
        _clock.Sync(Mmss(25, 39)); // 1 s past 25:40
        var due = _schedule.TakeDue();
        Assert.Equal([Mmss(25, 40)], due.Select(c => c.At));
        Assert.Empty(_schedule.TakeDue());
    }

    [Fact]
    public void Sync_exactly_on_a_call_time_fires_it()
    {
        _clock.Start();
        _clock.Sync(Mmss(25, 40));
        Assert.Single(_schedule.TakeDue());
    }

    [Fact]
    public void Sync_past_calls_skips_them_silently()
    {
        _clock.Start();
        _clock.Sync(Mmss(24, 30)); // jumps past 25:40 and 25:20
        Assert.Empty(_schedule.TakeDue());
        Assert.Empty(Run(10)); // still nothing: skipped for good
    }

    [Fact]
    public void Grace_boundary_two_seconds_late_is_skipped()
    {
        _clock.Start();
        _clock.Sync(Mmss(25, 38)); // exactly 2 s late
        Assert.Empty(_schedule.TakeDue());
    }

    [Fact]
    public void Sync_back_rearms_calls()
    {
        _clock.Start();
        _clock.Sync(Mmss(24, 30));
        Assert.Empty(_schedule.TakeDue());

        _clock.Sync(Mmss(25, 45));
        Assert.Empty(_schedule.TakeDue());
        var fired = Run(10); // 25:35
        Assert.Equal([Mmss(25, 40)], fired.Select(c => c.At));
    }

    [Fact]
    public void Sync_back_after_a_fired_call_fires_it_again()
    {
        _clock.Start();
        _clock.Sync(Mmss(25, 41));
        Assert.Single(Run(2));
        _clock.Sync(Mmss(25, 41));
        Assert.Single(Run(2));
    }

    [Fact]
    public void Type_ahead_sync_lines_up_the_next_call()
    {
        _clock.Start();
        _clock.Sync(Mmss(20, 45));
        Assert.Equal(Mmss(20, 40), _schedule.Next!.At);
        var fired = Run(30);
        Assert.Equal([Mmss(20, 40), Mmss(20, 20)], fired.Select(c => c.At));
    }

    [Fact]
    public void Next_tracks_progress_and_ends_with_the_last_call()
    {
        Assert.Equal(Mmss(25, 40), _schedule.Next!.At);
        _clock.Start();
        Run(10 * 60); // game 25:00
        Assert.Equal(Mmss(20, 40), _schedule.Next!.At);
        Run(30 * 60);
        Assert.Null(_schedule.Next);
    }

    [Fact]
    public void Next_is_null_after_the_last_call_even_while_running()
    {
        _clock.Sync(Mmss(5, 0));
        Assert.Null(_schedule.Next);
    }

    [Fact]
    public void Nothing_fires_after_the_match_is_over()
    {
        _clock.Sync(Mmss(0, 1));
        _time.Advance(5);
        Assert.Empty(_schedule.TakeDue());
        Assert.Null(_schedule.Next);
    }
}
