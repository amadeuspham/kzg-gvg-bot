using Kzg.Core.Jungle;
using Kzg.Core.Testing;
using static Kzg.Core.Tests.Jungle.JungleTestHelpers;

namespace Kzg.Core.Tests.Jungle;

public sealed class JungleMatchTests
{
    private readonly ManualTimeSource _time = new();
    private readonly List<JungleClip> _played = [];
    private readonly List<string> _log = [];
    private readonly JungleMatch _match;
    private bool _accept = true;
    private int _reach = 3;

    public JungleMatchTests() => _match = new JungleMatch(
        _time,
        clip =>
        {
            if (_accept)
            {
                _played.Add(clip);
            }

            return _accept;
        },
        () => _reach,
        _log.Add);

    private string Say(JungleCommandKind kind) => _match.Handle(new JungleCommand(kind));

    private string Sync(int m, int s) => _match.Handle(new JungleCommand(JungleCommandKind.Sync, Mmss(m, s)));

    private void Run(double seconds)
    {
        for (var i = 0; i < (int)Math.Round(seconds * 10); i++)
        {
            _time.Advance(0.1);
            _match.Tick();
        }
    }

    [Fact]
    public void Status_when_idle_says_how_to_start() =>
        Assert.Equal("Stopped. Run /jungle start, or /jungle sync MM:SS to join a match in progress.", Say(JungleCommandKind.Status));

    [Fact]
    public void Start_runs_a_full_match_with_ten_calls_and_ends()
    {
        Assert.Equal("Clock started at 35:00. Next call at 25:40.", Say(JungleCommandKind.Start));
        Run((35 * 60) + 1);
        Assert.Equal(10, _played.Count);
        Assert.Equal(1, _log.Count(l => l == "Match over."));
        Assert.Equal("Match over.", Say(JungleCommandKind.Status));
    }

    [Fact]
    public void Calls_play_the_clip_of_their_time_and_are_logged()
    {
        Sync(20, 50);
        Run(31);
        Assert.Equal([JungleClip.JungleHalftime40, JungleClip.JungleHalftime20], _played);
        Assert.Contains("Call 20:40: JungleHalftime40.", _log);
    }

    [Fact]
    public void Sync_type_ahead_fires_the_next_call_on_time()
    {
        Assert.Equal("Synced to 25:45. Next call at 25:40.", Sync(25, 45));
        Run(4);
        Assert.Empty(_played);
        Run(2);
        Assert.Single(_played);
    }

    [Fact]
    public void Sync_without_a_time_gives_the_usage() =>
        Assert.Equal(JungleTime.SyncUsage, Say(JungleCommandKind.Sync));

    [Fact]
    public void Pause_holds_the_calls_and_resume_continues()
    {
        Sync(20, 50);
        Run(5);
        Assert.StartsWith("Paused at 20:45", Say(JungleCommandKind.Pause));
        Run(300);
        Assert.Empty(_played);
        Assert.StartsWith("Paused at 20:45", Say(JungleCommandKind.Status));
        Assert.StartsWith("Resumed at 20:45", Say(JungleCommandKind.Resume));
        Run(6);
        Assert.Single(_played);
    }

    [Fact]
    public void Sync_while_paused_says_so()
    {
        Say(JungleCommandKind.Start);
        Say(JungleCommandKind.Pause);
        Assert.Equal("Synced to 20:00 (paused). Next call at 15:40.", Sync(20, 0));
    }

    [Fact]
    public void Pause_and_resume_in_the_wrong_state_say_so()
    {
        Assert.Equal("The clock is not running.", Say(JungleCommandKind.Pause));
        Assert.Equal("The clock is not paused.", Say(JungleCommandKind.Resume));
        Say(JungleCommandKind.Start);
        Say(JungleCommandKind.Pause);
        Assert.Equal("Already paused.", Say(JungleCommandKind.Pause));
    }

    [Fact]
    public void Stop_stops_the_calls()
    {
        Sync(25, 45);
        Assert.Equal("Stopped.", Say(JungleCommandKind.Stop));
        Run(30);
        Assert.Empty(_played);
    }

    [Fact]
    public void Sync_past_a_call_skips_it()
    {
        Assert.Equal("Synced to 25:30. Next call at 25:20.", Sync(25, 30));
        Run(5);
        Assert.Empty(_played);
    }

    [Fact]
    public void Sync_at_the_end_reports_no_calls_left()
    {
        Assert.Equal("Synced to 04:00. No calls left.", Sync(4, 0));
    }

    [Fact]
    public void Match_over_is_logged_once_and_again_after_a_new_match()
    {
        Sync(0, 5);
        Run(7);
        Run(7);
        Assert.Equal(1, _log.Count(l => l == "Match over."));

        Sync(0, 3);
        Run(5);
        Assert.Equal(2, _log.Count(l => l == "Match over."));
    }

    [Fact]
    public void Sync_to_zero_does_not_log_over_on_its_own_tick()
    {
        Sync(0, 0);
        Run(1);
        Assert.DoesNotContain("Match over.", _log);
    }

    [Fact]
    public void A_call_nobody_can_hear_is_logged_not_played()
    {
        _reach = 0;
        Sync(25, 45);
        Run(6);
        Assert.Empty(_played);
        Assert.Contains(_log, l => l.StartsWith("Call 25:40 reached nobody", StringComparison.Ordinal));
    }

    [Fact]
    public void A_call_the_source_refuses_is_logged()
    {
        _accept = false;
        Sync(25, 45);
        Run(6);
        Assert.Contains(_log, l => l.StartsWith("Call 25:40 dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void Test_plays_now_and_counts_the_channels()
    {
        Assert.Equal("Test call sent to 3 team channels.", Say(JungleCommandKind.Test));
        Assert.Equal([JungleClip.Jungle20], _played);
    }

    [Fact]
    public void Test_with_one_channel_says_channel_not_channels()
    {
        _reach = 1;
        Assert.Equal("Test call sent to 1 team channel.", Say(JungleCommandKind.Test));
    }

    [Fact]
    public void Test_with_no_reach_plays_nothing()
    {
        _reach = 0;
        Assert.Equal("No team channel has a relay bot in it now, so nobody heard it.", Say(JungleCommandKind.Test));
        Assert.Empty(_played);
    }

    [Fact]
    public void Test_while_a_call_plays_asks_to_wait()
    {
        _accept = false;
        Assert.Equal("A call is playing. Try again in a few seconds.", Say(JungleCommandKind.Test));
    }

    [Fact]
    public void Test_does_not_touch_the_clock()
    {
        Say(JungleCommandKind.Test);
        Assert.StartsWith("Stopped.", Say(JungleCommandKind.Status));
    }

    [Fact]
    public void Unknown_command_kind_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _match.Handle(new JungleCommand((JungleCommandKind)99)));

    [Fact]
    public void Works_without_a_log_sink()
    {
        var quiet = new JungleMatch(_time, _ => true, () => 1);
        quiet.Handle(new JungleCommand(JungleCommandKind.Start));
        _time.Advance(TimeSpan.FromMinutes(36));
        quiet.Tick();
    }

    [Fact]
    public async Task Handle_and_Tick_may_run_on_different_threads()
    {
        var tick = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                _match.Tick();
            }
        });
        var handle = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                _match.Handle(new JungleCommand(JungleCommandKind.Status));
                _match.Handle(new JungleCommand(JungleCommandKind.Sync, Mmss(25, 45)));
            }
        });
        await Task.WhenAll(tick, handle);
    }
}
