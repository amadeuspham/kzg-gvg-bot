using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Routing;
using Kzg.Protocol;

namespace Kzg.Core.Tests.Routing;

public class RoutingEngineTests
{
    private static readonly ChannelId A1 = new(11), A2 = new(12), A3 = new(13);
    private static readonly ChannelId D1 = new(21), D2 = new(22), D3 = new(23);
    private static readonly ChannelId Lobby = new(99);
    private static readonly ChannelId[] Bound = [A1, A2, A3, D1, D2, D3];

    private static readonly UserId Alice = new(1), Bob = new(2), Carol = new(3);

    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Preroll = TimeSpan.FromMilliseconds(300);

    private readonly FakeTimeSource _time = new();
    private readonly RoutingEngine _engine;

    public RoutingEngineTests()
    {
        _engine = new RoutingEngine(Config());
        foreach (var channel in Bound)
        {
            _engine.OnLinkReady(channel, true);
        }
    }

    private static RoutingConfig Config() => new(
        new Dictionary<Group, IReadOnlyList<ChannelId>> { [Group.Attack] = [A1, A2, A3], [Group.Defense] = [D1, D2, D3] },
        Preroll,
        TimeSpan.FromSeconds(120));

    private TickPlan Tick(TimeSpan? after = null)
    {
        _time.Advance(after ?? Frame);
        return _engine.Tick(_time.Now);
    }

    private void Join(UserId user, ChannelId channel, bool app = true)
    {
        _engine.OnPresence(user, channel);
        if (app)
        {
            _engine.OnAppConnected(user, _time.Now);
        }
    }

    private void Press(UserId user, bool down = true) =>
        _engine.OnEdge(new BroadcastEdge(user, BroadcastAction.Group, down, TriggerKind.AppKey, _time.Now));

    private void PressAllTeams(UserId user, bool down = true) =>
        _engine.OnEdge(new BroadcastEdge(user, BroadcastAction.AllTeams, down, TriggerKind.AppKey, _time.Now));

    private void OpenMic(UserId user, bool on = true) =>
        _engine.OnEdge(new BroadcastEdge(user, BroadcastAction.OpenMic, on, TriggerKind.AppKey, _time.Now));

    private void Heartbeat(UserId user, bool held = false, bool openMic = false, bool allTeams = false) =>
        _engine.OnHeartbeat(user, [.. held ? [BroadcastAction.Group] : Array.Empty<BroadcastAction>(), .. allTeams ? [BroadcastAction.AllTeams] : Array.Empty<BroadcastAction>()], openMic, _time.Now);

    private static SourceRoute Route(TickPlan plan, UserId user) => Assert.Single(plan.Routes, r => r.User == user);

    private static UserStatus Status(TickPlan plan, UserId user) => Assert.Single(plan.StatusChanges, s => s.User == user);

    [Fact]
    public void A_held_key_reaches_the_other_vcs_of_the_players_group_only()
    {
        Join(Bob, A1);
        Press(Bob);

        var plan = Tick();

        var route = Route(plan, Bob);
        Assert.Equal([A2, A3], route.Targets);
        Assert.Equal(A1, route.From);
        Assert.Equal([BroadcastAction.Group], route.Actions);
        Assert.Equal(SourceId.ForUser(Bob), route.Source);
        Assert.Equal([A2, A3], plan.Destinations.OrderBy(c => c.V));
        Assert.Equal([Bob], plan.SourcesFor(A2));
        Assert.Empty(plan.SourcesFor(A1));
        Assert.All([D1, D2, D3], d => Assert.Empty(plan.SourcesFor(d)));
    }

    [Fact]
    public void A_relay_reads_back_by_the_preroll_from_the_press_on_its_first_tick_only()
    {
        Join(Bob, A1);
        Tick();
        var pressedAt = _time.Now;
        Press(Bob);

        Assert.Equal(pressedAt - Preroll, Route(Tick(), Bob).ReadFrom);
        Assert.Null(Route(Tick(), Bob).ReadFrom);
    }

    [Fact]
    public void Releasing_the_key_drains_what_arrived_by_the_release_then_ends_the_relay_once()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();
        var released = _time.Now;
        Press(Bob, down: false);

        // The relay plays behind live: it goes on, to the same VCs, until the buffer catches the release.
        var plan = Tick();
        var drain = Route(plan, Bob);
        Assert.Equal(released, drain.DrainUntil);
        Assert.Empty(drain.Actions);
        Assert.Equal([A2, A3], drain.Targets);
        Assert.Null(drain.ReadFrom);
        Assert.Empty(plan.Ended);
        Assert.Empty(Status(plan, Bob).Active);
        Assert.Equal(released, Route(Tick(), Bob).DrainUntil);

        _engine.OnRelayDrained(Bob);
        plan = Tick();

        Assert.Empty(plan.Routes);
        Assert.Equal([Bob], plan.Ended);
        Assert.Empty(Tick().Ended);
    }

    [Fact]
    public void A_drain_the_host_never_finishes_ends_after_the_whole_buffer()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();
        Press(Bob, down: false);
        var started = Tick();

        _time.Advance(started.Now + RoutingEngine.MaxDrain - Frame - _time.Now);
        Assert.NotEmpty(_engine.Tick(_time.Now).Routes);

        Assert.Equal([Bob], Tick().Ended);
    }

    [Fact]
    public void Pressing_again_during_a_drain_carries_on_the_same_relay()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();
        Press(Bob, down: false);
        Tick();

        Press(Bob);
        var route = Route(Tick(), Bob);

        Assert.Equal([BroadcastAction.Group], route.Actions);
        Assert.Null(route.ReadFrom);
        Assert.Null(route.DrainUntil);
        _engine.OnRelayDrained(Bob);
        Assert.Null(Route(Tick(), Bob).DrainUntil);
    }

    [Fact]
    public void A_drain_never_follows_the_player_into_the_other_group()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();
        Press(Bob, down: false);
        Tick();

        _engine.OnPresence(Bob, D1);
        var plan = Tick();

        Assert.Empty(plan.Routes);
        Assert.Equal([Bob], plan.Ended);
    }

    [Fact]
    public void A_drain_moves_with_the_player_inside_their_group()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();
        Press(Bob, down: false);
        Tick();

        _engine.OnPresence(Bob, A2);

        Assert.Equal([A1, A3], Route(Tick(), Bob).Targets);
    }

    [Fact]
    public void An_end_the_bot_makes_or_can_only_guess_the_time_of_stops_at_once()
    {
        // A key-up only a heartbeat reports, a lost app, a stuck key: none drains.
        Join(Bob, A1);
        Press(Bob);
        Tick();
        Heartbeat(Bob, held: false);
        Assert.Equal([Bob], Tick().Ended);

        Press(Bob);
        Tick();
        _engine.OnAppDisconnected(Bob);
        Assert.Equal([Bob], Tick().Ended);

        // A forced end in the same tick as a release, of another session: the forced one wins.
        Join(Alice, A1);
        Press(Alice);
        OpenMic(Alice);
        Tick();
        Press(Alice, down: false);
        _engine.OnAppDisconnected(Alice);
        Assert.Equal([Alice], Tick().Ended);

        // A release that came before the app went still drains: the key was let go, not lost.
        Join(Bob, A1);
        Press(Bob);
        Tick();
        Press(Bob, down: false);
        _engine.OnAppDisconnected(Bob);
        Assert.NotNull(Route(Tick(), Bob).DrainUntil);
        _engine.OnRelayDrained(Bob);
        Tick();

        // Releasing a key the bot already ended doesn't drain either.
        Join(Bob, A1);
        Press(Bob);
        for (var i = 0; i < 61; i++)
        {
            Tick(TimeSpan.FromSeconds(2));
            Heartbeat(Bob, held: true);
        }
        Press(Bob, down: false);
        Assert.Empty(Tick().Routes);
    }

    [Fact]
    public void A_drain_needs_a_group_to_finish_to()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();
        _engine.OnPresence(Bob, Lobby);
        Press(Bob, down: false);

        Assert.Equal([Bob], Tick().Ended);
    }

    [Fact]
    public void A_move_mid_broadcast_retargets_on_the_next_tick_without_restarting()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();

        _engine.OnPresence(Bob, A2);
        var route = Route(Tick(), Bob);

        Assert.Equal([A1, A3], route.Targets);
        Assert.Null(route.ReadFrom);
    }

    [Fact]
    public void A_players_team_is_the_vc_they_sit_in()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();

        _engine.OnPresence(Bob, D2);

        Assert.Equal([D1, D3], Route(Tick(), Bob).Targets);
    }

    [Fact]
    public void An_unbound_vc_gets_no_targets_and_a_warning_and_coming_back_resumes()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();

        _engine.OnPresence(Bob, Lobby);
        var plan = Tick();

        var route = Route(plan, Bob);
        Assert.Empty(route.Targets);
        Assert.Empty(plan.Destinations);
        var status = Status(plan, Bob);
        Assert.Equal([StatusWarning.NotInTeamVc], status.Warnings);
        Assert.Null(status.Group);
        Assert.Equal(Lobby, status.Channel);
        Assert.Empty(status.Active);

        _engine.OnPresence(Bob, A3);
        Assert.Equal([A1, A2], Route(Tick(), Bob).Targets);
    }

    [Fact]
    public void Leaving_voice_reaches_no_one()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();

        _engine.OnPresence(Bob, null);
        var plan = Tick();

        Assert.Empty(Route(plan, Bob).Targets);
        Assert.Null(Status(plan, Bob).Channel);
        Assert.Equal([StatusWarning.NotInTeamVc], Status(plan, Bob).Warnings);
    }

    [Fact]
    public void No_one_is_ever_routed_into_a_vc_they_sit_in()
    {
        // Everyone broadcasts from every pair of seats; nobody's audio lands where they sit.
        UserId[] users = [Alice, Bob, Carol];
        foreach (var user in users)
        {
            Join(user, A1);
            Press(user);
            OpenMic(user);
        }
        foreach (var a in Bound)
        {
            foreach (var b in Bound.Append(Lobby))
            {
                _engine.OnPresence(Alice, a);
                _engine.OnPresence(Bob, b);
                _engine.OnPresence(Carol, a == D1 ? D2 : D1);
                var plan = Tick();
                foreach (var route in plan.Routes)
                {
                    Assert.DoesNotContain(route.From, route.Targets.Cast<ChannelId?>());
                    Assert.DoesNotContain(route.User, route.From is { } from ? plan.SourcesFor(from) : []);
                }
            }
        }
    }

    [Fact]
    public void Two_broadcasters_are_heard_together_where_neither_sits()
    {
        Join(Alice, A1);
        Join(Bob, A2);
        Press(Alice);
        Press(Bob);

        var plan = Tick();

        Assert.Equal([Bob], plan.SourcesFor(A1));
        Assert.Equal([Alice], plan.SourcesFor(A2));
        Assert.Equal([Alice, Bob], plan.SourcesFor(A3));
    }

    [Fact]
    public void A_vc_whose_bot_is_down_is_skipped_and_its_group_warned()
    {
        Join(Bob, A1);
        Press(Bob);
        _engine.OnLinkReady(A3, false);

        var plan = Tick();

        Assert.Equal([A2], Route(plan, Bob).Targets);
        Assert.Equal([StatusWarning.BotDown], Status(plan, Bob).Warnings);
        Assert.Equal([A2], Status(plan, Bob).Targets);

        _engine.OnLinkReady(A3, true);
        Assert.Empty(Status(Tick(), Bob).Warnings);
    }

    [Fact]
    public void App_edges_count_only_while_the_app_is_connected()
    {
        Join(Bob, A1, app: false);
        Press(Bob);
        _engine.OnHeartbeat(Bob, [BroadcastAction.Group], false, _time.Now);

        var plan = Tick();

        Assert.Empty(plan.Routes);
        Assert.Empty(plan.StatusChanges);
    }

    [Fact]
    public void Without_a_heartbeat_for_six_seconds_the_apps_sessions_end()
    {
        Join(Alice, A1);
        Press(Alice);
        OpenMic(Alice);
        Tick();
        Heartbeat(Alice, held: true, openMic: true);
        var beat = _time.Now;

        Assert.NotEmpty(Tick(TimeSpan.FromMilliseconds(5980)).Routes);
        _time.Advance(beat + TimeSpan.FromSeconds(6) - _time.Now);
        var plan = _engine.Tick(_time.Now);

        Assert.Empty(plan.Routes);
        Assert.Equal([Alice], plan.Ended);

        // Edges from an app that timed out are ignored until it signs in again.
        Press(Alice, down: false);
        Press(Alice);
        Assert.Empty(Tick().Routes);
    }

    [Fact]
    public void Heartbeats_keep_the_app_alive()
    {
        Join(Bob, A1);
        Press(Bob);
        for (var i = 0; i < 10; i++)
        {
            Tick(TimeSpan.FromSeconds(2));
            Heartbeat(Bob, held: true);
        }

        Assert.NotEmpty(Tick().Routes);
    }

    [Fact]
    public void Disconnecting_ends_the_key_and_open_mic()
    {
        Join(Alice, A1);
        Press(Alice);
        OpenMic(Alice);
        Tick();

        _engine.OnAppDisconnected(Alice);
        var plan = Tick();

        Assert.Empty(plan.Routes);
        Assert.Empty(plan.StatusChanges);
    }

    [Fact]
    public void A_heartbeat_heals_a_lost_key_up()
    {
        Join(Bob, A1);
        Press(Bob);
        Tick();

        Heartbeat(Bob, held: false);

        Assert.Equal([Bob], Tick().Ended);
    }

    [Fact]
    public void A_heartbeat_heals_a_lost_key_down_reading_back_from_the_heartbeat()
    {
        Join(Bob, A1);
        Tick();
        var beat = _time.Now;

        Heartbeat(Bob, held: true);

        Assert.Equal(beat - Preroll, Route(Tick(), Bob).ReadFrom);
    }

    [Fact]
    public void A_stuck_key_ends_after_two_minutes_and_stays_ended_until_it_is_let_go()
    {
        Join(Bob, A1);
        Press(Bob);
        var pressed = _time.Now;
        Tick();

        _time.Advance(pressed + TimeSpan.FromSeconds(120) - Frame - _time.Now);
        Heartbeat(Bob, held: true);
        Assert.NotEmpty(_engine.Tick(_time.Now).Routes);

        var plan = Tick();
        Assert.Equal([Bob], plan.Ended);

        // The app still claims the key; a heartbeat saying so doesn't restart it.
        Heartbeat(Bob, held: true);
        Assert.Empty(Tick().Routes);

        Heartbeat(Bob, held: false);
        Heartbeat(Bob, held: true);
        Assert.NotEmpty(Tick().Routes);
    }

    [Fact]
    public void A_fresh_press_after_a_stuck_key_ended_starts_again()
    {
        Join(Bob, A1);
        Press(Bob);
        for (var i = 0; i < 61; i++)
        {
            Tick(TimeSpan.FromSeconds(2));
            Heartbeat(Bob, held: true);
        }
        Assert.Empty(Tick().Routes);

        Press(Bob);
        Press(Bob);

        Assert.NotNull(Route(Tick(), Bob).ReadFrom);
    }

    [Fact]
    public void Any_player_latches_open_mic_in_their_own_groups_vcs()
    {
        Join(Bob, A2);
        OpenMic(Bob);

        var plan = Tick();

        var route = Route(plan, Bob);
        Assert.Equal([BroadcastAction.OpenMic], route.Actions);
        Assert.Equal([A1, A3], route.Targets);
        Assert.Equal(_time.Now - Frame - Preroll, route.ReadFrom);
        var status = Status(plan, Bob);
        Assert.True(status.OpenMic);
        Assert.Equal([BroadcastAction.OpenMic], status.Active);
        Assert.Equal(Group.Attack, status.Group);
        Assert.Empty(status.Warnings);

        // A latch holds without a heartbeat claiming a key.
        Heartbeat(Bob, openMic: true);
        Assert.NotEmpty(Tick(TimeSpan.FromSeconds(1)).Routes);
        var off = _time.Now;
        OpenMic(Bob, on: false);
        Assert.Equal(off, Route(Tick(), Bob).DrainUntil);
        _engine.OnRelayDrained(Bob);
        Assert.Equal([Bob], Tick().Ended);
    }

    [Fact]
    public void Two_players_on_open_mic_in_one_group_are_heard_together()
    {
        Join(Alice, A1);
        Join(Bob, A2);
        OpenMic(Alice);
        OpenMic(Bob);

        var plan = Tick();

        Assert.Equal([A2, A3], Route(plan, Alice).Targets);
        Assert.Equal([A1, A3], Route(plan, Bob).Targets);
        Assert.Equal([Alice, Bob], plan.SourcesFor(A3));
        Assert.Equal([Bob], plan.SourcesFor(A1));
    }

    [Fact]
    public void Open_mic_reaches_the_group_of_the_vc_the_player_sits_in_never_the_other()
    {
        Join(Alice, D1);
        OpenMic(Alice);

        var route = Route(Tick(), Alice);

        Assert.Equal([D2, D3], route.Targets);
    }

    [Fact]
    public void Open_mic_outside_a_team_vc_is_refused_and_a_heartbeat_does_not_latch_it()
    {
        Join(Bob, Lobby);
        OpenMic(Bob);

        var plan = Tick();

        Assert.Empty(plan.Routes);
        var status = Status(plan, Bob);
        Assert.False(status.OpenMic);
        Assert.Empty(status.Active);
        Assert.Equal([StatusWarning.NotInTeamVc], status.Warnings);

        Heartbeat(Bob, openMic: true);
        Assert.Empty(Tick().Routes);
    }

    [Fact]
    public void Leaving_the_group_ends_open_mic_and_coming_back_does_not_resume_it()
    {
        Join(Alice, A1);
        OpenMic(Alice);
        Tick();

        _engine.OnPresence(Alice, Lobby);
        var plan = Tick();
        Assert.Equal([Alice], plan.Ended);
        Assert.Equal([StatusWarning.NotInTeamVc], Status(plan, Alice).Warnings);

        _engine.OnPresence(Alice, A1);
        Heartbeat(Alice, openMic: true);
        Assert.Empty(Tick().Routes);
    }

    [Fact]
    public void Open_mic_and_the_key_together_relay_once_and_releasing_one_keeps_the_other()
    {
        Join(Alice, A1);
        OpenMic(Alice);
        Press(Alice);

        Assert.Equal([BroadcastAction.Group, BroadcastAction.OpenMic], Route(Tick(), Alice).Actions);

        Press(Alice, down: false);
        var plan = Tick();
        Assert.Equal([BroadcastAction.OpenMic], Route(plan, Alice).Actions);
        Assert.Null(Route(plan, Alice).ReadFrom);
    }

    [Fact]
    public void Synthetic_edges_do_nothing()
    {
        Join(Alice, A1);

        _engine.OnEdge(new BroadcastEdge(Alice, BroadcastAction.Group, true, TriggerKind.Synthetic, _time.Now));
        _engine.OnEdge(new BroadcastEdge(Alice, BroadcastAction.OpenMic, true, TriggerKind.Synthetic, _time.Now));

        Assert.Empty(Tick().Routes);
    }

    [Fact]
    public void A_status_goes_out_on_the_first_tick_and_then_only_on_change()
    {
        Join(Bob, A1);

        var first = Status(Tick(), Bob);
        Assert.Equal(A1, first.Channel);
        Assert.Equal(Group.Attack, first.Group);
        Assert.Empty(first.Active);
        Assert.Empty(first.Targets);
        Assert.Empty(first.Warnings);
        Assert.Empty(Tick().StatusChanges);

        Press(Bob);
        var pressed = Status(Tick(), Bob);
        Assert.Equal([BroadcastAction.Group], pressed.Active);
        Assert.Equal([A2, A3], pressed.Targets);
        Assert.Empty(Tick().StatusChanges);

        // A reconnecting app is told again, even though nothing changed.
        _engine.OnAppConnected(Bob, _time.Now);
        Assert.Single(Tick().StatusChanges);
    }

    [Fact]
    public void A_player_the_engine_does_not_know_changes_nothing()
    {
        _engine.OnAppDisconnected(Bob);
        _engine.OnHeartbeat(Bob, [BroadcastAction.Group], true, _time.Now);

        Assert.Empty(Tick().Routes);
    }

    [Fact]
    public void Routing_reads_the_config_file_s_groups_and_timings()
    {
        var config = RoutingConfig.From(new BotConfig
        {
            PrerollMs = 200,
            MaxHoldMs = 5000,
            Groups = new GroupsConfig
            {
                Attack = new GroupConfig { Channels = [A1] },
                Defense = new GroupConfig { Channels = [D1, D2] },
            },
        });

        Assert.Equal(Group.Attack, config.GroupOf(A1));
        Assert.Equal(Group.Defense, config.GroupOf(D2));
        Assert.Null(config.GroupOf(A2));
        Assert.Null(config.GroupOf(null));
        Assert.Equal([D1, D2], config.ChannelsOf(Group.Defense));
        Assert.Equal(TimeSpan.FromMilliseconds(200), config.Preroll);
        Assert.Equal(TimeSpan.FromSeconds(5), config.MaxHold);
        Assert.Equal(TimeSpan.FromMilliseconds(WireProtocol.TimeoutMs), config.HeartbeatTimeout);
    }

    [Fact]
    public void A_channel_in_two_groups_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new RoutingConfig(
            new Dictionary<Group, IReadOnlyList<ChannelId>> { [Group.Attack] = [A1], [Group.Defense] = [A1] },
            Preroll, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_group_with_nothing_configured_has_no_vcs()
    {
        var config = new RoutingConfig(new Dictionary<Group, IReadOnlyList<ChannelId>>(), Preroll, TimeSpan.FromSeconds(1));

        Assert.Empty(config.ChannelsOf(Group.Attack));
    }

    // ----- The all-teams key -----

    [Fact]
    public void A_held_all_teams_key_reaches_every_bound_vc_of_both_groups_but_the_players_own()
    {
        Join(Bob, A1);
        PressAllTeams(Bob);

        var plan = Tick();

        var route = Route(plan, Bob);
        Assert.Equal([A2, A3, D1, D2, D3], route.Targets);
        Assert.Equal([BroadcastAction.AllTeams], route.Actions);
        Assert.Equal(_time.Now - Frame - Preroll, route.ReadFrom);
        Assert.Empty(plan.SourcesFor(A1));
        Assert.All([A2, A3, D1, D2, D3], d => Assert.Equal([Bob], plan.SourcesFor(d)));
        var status = Status(plan, Bob);
        Assert.Equal([BroadcastAction.AllTeams], status.Active);
        Assert.Equal([A2, A3, D1, D2, D3], status.Targets);
    }

    [Fact]
    public void The_all_teams_key_from_a_defense_vc_reaches_attack_too()
    {
        Join(Carol, D2);
        PressAllTeams(Carol);

        Assert.Equal([A1, A2, A3, D1, D3], Route(Tick(), Carol).Targets);
    }

    [Fact]
    public void The_all_teams_key_skips_vcs_whose_bot_is_down()
    {
        _engine.OnLinkReady(D2, false);
        Join(Bob, A1);
        PressAllTeams(Bob);

        Assert.Equal([A2, A3, D1, D3], Route(Tick(), Bob).Targets);
    }

    [Fact]
    public void The_all_teams_key_outside_a_team_vc_reaches_no_one()
    {
        Join(Bob, Lobby);
        PressAllTeams(Bob);

        var plan = Tick();

        Assert.Empty(Route(plan, Bob).Targets);
        Assert.Empty(Status(plan, Bob).Active);
        Assert.Equal([StatusWarning.NotInTeamVc], Status(plan, Bob).Warnings);
    }

    [Fact]
    public void Releasing_the_all_teams_key_drains_to_both_groups()
    {
        Join(Bob, A1);
        PressAllTeams(Bob);
        Tick();
        var released = _time.Now;
        PressAllTeams(Bob, down: false);

        var drain = Route(Tick(), Bob);

        Assert.Equal(released, drain.DrainUntil);
        Assert.Equal([A2, A3, D1, D2, D3], drain.Targets);
        _engine.OnRelayDrained(Bob);
        Assert.Equal([Bob], Tick().Ended);
    }

    [Fact]
    public void A_team_key_drain_after_an_all_teams_hold_reaches_only_the_team()
    {
        Join(Bob, A1);
        PressAllTeams(Bob);
        Tick();
        PressAllTeams(Bob, down: false);
        _engine.OnRelayDrained(Bob);
        Tick();
        Tick();
        Press(Bob);
        Tick();
        Press(Bob, down: false);

        Assert.Equal([A2, A3], Route(Tick(), Bob).Targets);
    }

    [Fact]
    public void Both_keys_together_relay_once_to_every_team_and_letting_go_of_all_teams_narrows_to_the_team()
    {
        Join(Bob, A1);
        Press(Bob);
        PressAllTeams(Bob);

        var both = Route(Tick(), Bob);
        Assert.Equal([BroadcastAction.Group, BroadcastAction.AllTeams], both.Actions);
        Assert.Equal([A2, A3, D1, D2, D3], both.Targets);

        PressAllTeams(Bob, down: false);
        var team = Route(Tick(), Bob);
        Assert.Equal([BroadcastAction.Group], team.Actions);
        Assert.Equal([A2, A3], team.Targets);
        Assert.Null(team.ReadFrom);

        PressAllTeams(Bob);
        Press(Bob, down: false);
        Assert.Equal([A2, A3, D1, D2, D3], Route(Tick(), Bob).Targets);
    }

    [Fact]
    public void Open_mic_with_the_all_teams_key_held_reaches_every_team_until_the_key_is_let_go()
    {
        Join(Bob, A1);
        OpenMic(Bob);
        PressAllTeams(Bob);
        Assert.Equal([A2, A3, D1, D2, D3], Route(Tick(), Bob).Targets);

        PressAllTeams(Bob, down: false);
        var plan = Tick();
        Assert.Equal([BroadcastAction.OpenMic], Route(plan, Bob).Actions);
        Assert.Equal([A2, A3], Route(plan, Bob).Targets);
    }

    [Fact]
    public void An_all_teams_broadcaster_and_a_team_broadcaster_mix_where_neither_sits()
    {
        Join(Alice, A1);
        Join(Carol, D1);
        PressAllTeams(Alice);
        Press(Carol);

        var plan = Tick();

        Assert.Equal([Alice, Carol], plan.SourcesFor(D2));
        Assert.Equal([Alice], plan.SourcesFor(D1));
        Assert.Equal([Alice], plan.SourcesFor(A2));
        Assert.Empty(plan.SourcesFor(A1));
    }

    [Fact]
    public void A_heartbeat_heals_a_lost_all_teams_key_down_and_up()
    {
        Join(Bob, A1);
        Heartbeat(Bob, allTeams: true);
        Assert.Equal([BroadcastAction.AllTeams], Route(Tick(), Bob).Actions);

        // The key-up came at some unknown time since the last heartbeat: stop, don't drain.
        Heartbeat(Bob);
        Assert.Equal([Bob], Tick().Ended);
    }

    [Fact]
    public void A_stuck_all_teams_key_ends_after_two_minutes()
    {
        Join(Bob, A1);
        PressAllTeams(Bob);
        Tick();

        for (var i = 0; i < 61; i++)
        {
            Heartbeat(Bob, allTeams: true);
            Tick(TimeSpan.FromSeconds(2));
        }

        Assert.DoesNotContain(_engine.Tick(_time.Now).Routes, r => r.User == Bob);
    }

    [Fact]
    public void Disconnecting_ends_the_all_teams_key_at_once()
    {
        Join(Bob, A1);
        PressAllTeams(Bob);
        Tick();

        _engine.OnAppDisconnected(Bob);

        Assert.Equal([Bob], Tick().Ended);
    }

    [Fact]
    public void A_synthetic_all_teams_edge_does_nothing()
    {
        Join(Bob, A1);
        _engine.OnEdge(new BroadcastEdge(Bob, BroadcastAction.AllTeams, true, TriggerKind.Synthetic, _time.Now));

        Assert.Empty(Tick().Routes);
    }
}
