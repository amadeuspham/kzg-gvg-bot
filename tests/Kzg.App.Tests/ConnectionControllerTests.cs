using Kzg.App.Link;
using Kzg.Protocol;

namespace Kzg.App.Tests;

public class ConnectionControllerTests
{
    private const string Host = "gvg.example.com";

    private static readonly Guid InstallId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly FakeTimeSource _time = new();
    private readonly FakeTransportFactory _transports = new();
    private readonly ConnectionController _controller;
    private readonly List<ConnectionSnapshot> _snapshots = [];
    private readonly List<string> _issuedTokens = [];
    private int _rejected;

    public ConnectionControllerTests()
    {
        _controller = new ConnectionController(_transports, _time, InstallId, "1.2.3");
        _controller.Changed += _snapshots.Add;
        _controller.DeviceTokenIssued += _issuedTokens.Add;
        _controller.TokenRejected += () => _rejected++;
    }

    private static string NewSecret() => WireSecret.Generate();

    private string StartWithToken(string? token = null)
    {
        token ??= NewSecret();
        _controller.Start(new LinkCredential.DeviceToken(token), Host);
        return token;
    }

    private FakeTransport Online()
    {
        StartWithToken();
        var transport = _transports.Latest;
        transport.Receive(Messages.Welcome());
        return transport;
    }

    private void Advance(double seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    private void Tick(double seconds)
    {
        Advance(seconds);
        _controller.Tick();
    }

    // ----- hello and welcome -----

    [Fact]
    public void Starting_with_a_token_connects_over_wss_and_says_hello_with_only_the_token()
    {
        var token = StartWithToken();

        var hello = Assert.Single(_transports.Latest.SentOf<HelloMessage>());
        Assert.Equal(new Uri($"wss://{Host}/ws"), _transports.Latest.ConnectedTo);
        Assert.Equal(token, hello.DeviceToken);
        Assert.Null(hello.Code);
        Assert.Null(hello.LoginId);
        Assert.Equal("1.2.3", hello.AppVersion);
        Assert.Equal(InstallId, hello.InstallId);
        Assert.Equal(LinkPhase.Connecting, _controller.Snapshot.Phase);
    }

    [Fact]
    public void A_connect_code_travels_whole_in_hello()
    {
        var code = ConnectCode.Generate(Host).Format();

        _controller.Start(new LinkCredential.Code(code), Host);

        Assert.Equal(code, Assert.Single(_transports.Latest.SentOf<HelloMessage>()).Code);
    }

    [Fact]
    public void A_login_id_waits_for_the_browser()
    {
        _controller.Start(new LinkCredential.LoginId(NewSecret()), Host);

        Assert.Equal(LinkPhase.SigningIn, _controller.Snapshot.Phase);
        Assert.NotNull(Assert.Single(_transports.Latest.SentOf<HelloMessage>()).LoginId);
    }

    [Fact]
    public void Welcome_makes_the_link_online_and_the_badge_Connected()
    {
        var transport = Online();

        Assert.Equal(LinkPhase.Online, _controller.Snapshot.Phase);
        Assert.Equal(BadgeState.Connected, _controller.Snapshot.Badge);
        Assert.Equal("Alex", _controller.Snapshot.DisplayName);
        Assert.Null(_controller.Snapshot.OfflineSince);
        Assert.Single(transport.SentOf<HeartbeatMessage>());
    }

    [Fact]
    public void A_new_device_token_is_handed_over_and_used_for_every_later_hello()
    {
        var code = ConnectCode.Generate(Host).Format();
        _controller.Start(new LinkCredential.Code(code), Host);
        var issued = NewSecret();

        _transports.Latest.Receive(Messages.Welcome(token: issued));
        _transports.Latest.Close();
        Tick(1);

        Assert.Equal([issued], _issuedTokens);
        var hello = Assert.Single(_transports.Latest.SentOf<HelloMessage>());
        Assert.Equal(issued, hello.DeviceToken);
        Assert.Null(hello.Code);
    }

    [Fact]
    public void A_welcome_that_leaves_a_code_link_without_a_token_is_a_protocol_error()
    {
        _controller.Start(new LinkCredential.Code(ConnectCode.Generate(Host).Format()), Host);

        _transports.Latest.Receive(Messages.Welcome(token: null));

        Assert.Equal(LinkPhase.Failed, _controller.Snapshot.Phase);
        Assert.Equal(LinkFailure.ProtocolError, _controller.Snapshot.Failure);
    }

    [Fact]
    public void Nothing_is_logged_or_printed_with_a_secret_in_it()
    {
        var token = NewSecret();

        Assert.DoesNotContain(token, new LinkCredential.DeviceToken(token).ToString());
        Assert.DoesNotContain(token, new LinkCredential.LoginId(token).ToString());
        Assert.DoesNotContain(token, new LinkCredential.Code(token).ToString());
    }

    [Fact]
    public void The_dev_override_replaces_the_address_but_nothing_else()
    {
        var controller = new ConnectionController(
            _transports, _time, InstallId, "1.2.3", new ConnectionOptions { ServerOverride = new Uri("ws://127.0.0.1:7878/ws") });

        controller.Start(new LinkCredential.DeviceToken(NewSecret()), Host);

        Assert.Equal(new Uri("ws://127.0.0.1:7878/ws"), _transports.Latest.ConnectedTo);
        Assert.True(controller.HasServerOverride);
        Assert.False(_controller.HasServerOverride);
        Assert.Single(_transports.Latest.SentOf<HelloMessage>());
    }

    // ----- heartbeat, keys, round trip -----

    [Fact]
    public void A_heartbeat_goes_out_every_two_seconds_with_a_rising_seq_and_the_clock()
    {
        var transport = Online();

        Tick(1.9);
        Assert.Single(transport.SentOf<HeartbeatMessage>());
        Tick(0.1);
        Tick(2);

        var beats = transport.SentOf<HeartbeatMessage>().ToList();
        Assert.Equal(3, beats.Count);
        Assert.Equal([0L, 1L, 2L], beats.Select(b => b.Seq));
        Assert.Equal((long)_time.Now.TotalMilliseconds, beats[^1].SentAtMs);
    }

    [Fact]
    public void A_held_key_is_sent_as_a_press_and_restated_in_every_heartbeat()
    {
        var transport = Online();

        _controller.SetGroupKey(true);
        Tick(2);
        _controller.SetGroupKey(false);
        Tick(2);

        var presses = transport.SentOf<PressMessage>().ToList();
        Assert.Equal([true, false], presses.Select(p => p.Down));
        Assert.All(presses, p => Assert.Equal(WireAction.Group, p.Action));
        Assert.True(presses[1].Seq > presses[0].Seq);
        var beats = transport.SentOf<HeartbeatMessage>().ToList();
        Assert.Empty(beats[0].Held);
        Assert.Equal([WireAction.Group], beats[1].Held);
        Assert.Empty(beats[2].Held);
    }

    [Fact]
    public void The_all_teams_key_is_its_own_press_and_rides_in_every_heartbeat_beside_the_team_key()
    {
        var transport = Online();

        _controller.SetAllTeamsKey(true);
        Tick(2);
        _controller.SetGroupKey(true);
        Tick(2);
        _controller.SetAllTeamsKey(false);

        var presses = transport.SentOf<PressMessage>().ToList();
        Assert.Equal([(WireAction.AllTeams, true), (WireAction.Group, true), (WireAction.AllTeams, false)], presses.Select(p => (p.Action, p.Down)));
        Assert.True(presses[2].Seq > presses[1].Seq && presses[1].Seq > presses[0].Seq);
        var beats = transport.SentOf<HeartbeatMessage>().ToList();
        Assert.Equal([WireAction.AllTeams], beats[1].Held);
        Assert.Equal([WireAction.Group, WireAction.AllTeams], beats[2].Held);
        Assert.True(_controller.Snapshot.GroupHeld);
        Assert.False(_controller.Snapshot.AllTeamsHeld);
    }

    [Fact]
    public void Holding_the_all_teams_key_shows_as_broadcasting_and_a_reconnect_restates_it()
    {
        var transport = Online();

        _controller.SetAllTeamsKey(true);
        Assert.Equal(BadgeState.Broadcasting, _controller.Snapshot.Badge);
        Assert.True(_controller.Snapshot.AllTeamsHeld);

        transport.Close();
        Tick(1);
        _transports.Latest.Receive(Messages.Welcome());

        Assert.Equal([WireAction.AllTeams], _transports.Latest.SentOf<HeartbeatMessage>().First().Held);
    }

    [Fact]
    public void A_repeated_key_state_sends_nothing_new()
    {
        var transport = Online();

        _controller.SetGroupKey(true);
        _controller.SetGroupKey(true);

        Assert.Single(transport.SentOf<PressMessage>());
    }

    [Fact]
    public void An_ack_gives_the_round_trip_time()
    {
        var transport = Online();
        Tick(2);
        var beat = transport.SentOf<HeartbeatMessage>().Last();

        Advance(0.042);
        transport.Receive(new HeartbeatAckMessage(beat.Seq, beat.SentAtMs));

        Assert.Equal(42, _controller.Snapshot.RoundTripMs);
    }

    // ----- status and badge -----

    [Fact]
    public void Status_shows_the_channel_team_targets_and_warnings()
    {
        var transport = Online();

        transport.Receive(Messages.Status(warnings: [StatusWarning.BotDown]));

        var snapshot = _controller.Snapshot;
        Assert.Equal("Attack 1", snapshot.ChannelName);
        Assert.Equal(WireGroup.Attack, snapshot.Group);
        Assert.Equal(["Attack 2", "Attack 3"], snapshot.Targets);
        Assert.Equal([StatusWarning.BotDown], snapshot.Warnings);
    }

    [Fact]
    public void Holding_the_key_while_online_makes_the_badge_Broadcasting_until_released()
    {
        Online();

        _controller.SetGroupKey(true);
        Assert.Equal(BadgeState.Broadcasting, _controller.Snapshot.Badge);

        _controller.SetGroupKey(false);
        Assert.Equal(BadgeState.Connected, _controller.Snapshot.Badge);
    }

    [Fact]
    public void An_active_relay_from_the_bot_also_shows_Broadcasting()
    {
        var transport = Online();

        transport.Receive(Messages.Status(active: [WireAction.OpenMic], openMic: true));

        Assert.Equal(BadgeState.Broadcasting, _controller.Snapshot.Badge);
    }

    // ----- drops and reconnects -----

    [Fact]
    public void A_dropped_connection_shows_Reconnecting_with_the_time_it_was_lost()
    {
        var transport = Online();
        Advance(30);
        var lostAt = _time.Now;

        transport.Close();

        Assert.Equal(LinkPhase.Offline, _controller.Snapshot.Phase);
        Assert.Equal(BadgeState.Reconnecting, _controller.Snapshot.Badge);
        Assert.Equal(lostAt, _controller.Snapshot.OfflineSince);
        Assert.Equal(LinkFailure.None, _controller.Snapshot.Failure);
    }

    [Fact]
    public void The_badge_turns_Disconnected_after_fifteen_seconds_without_a_connection_but_keeps_retrying()
    {
        var transport = Online();
        transport.Close();
        _transports.Configure = t => t.ConnectFailure = new HttpRequestException("down");

        Tick(14);
        Assert.Equal(BadgeState.Reconnecting, _controller.Snapshot.Badge);
        Tick(1);

        Assert.Equal(BadgeState.Disconnected, _controller.Snapshot.Badge);
        Assert.Equal(LinkFailure.ServerUnreachable, _controller.Snapshot.Failure);
        var attempts = _transports.Created.Count;
        Tick(10);
        Assert.True(_transports.Created.Count > attempts);
    }

    [Fact]
    public void Retries_wait_one_two_five_then_ten_seconds()
    {
        _transports.Configure = t => t.ConnectFailure = new HttpRequestException("down");
        StartWithToken();
        var times = new List<double> { 0 };
        var start = _time.Now;

        for (var step = 0; step < 90 * 10 && times.Count < 6; step++)
        {
            var before = _transports.Created.Count;
            Tick(0.1);
            if (_transports.Created.Count > before)
            {
                times.Add(Math.Round((_time.Now - start).TotalSeconds, 1));
            }
        }

        Assert.Equal([0, 1.0, 3.0, 8.0, 18.0, 28.0], times);
    }

    [Fact]
    public void Retry_now_skips_the_pause_and_connects_at_once()
    {
        _transports.Configure = t => t.ConnectFailure = new HttpRequestException("down");
        StartWithToken();
        Tick(1);
        Tick(3);
        var attempts = _transports.Created.Count;
        var next = _controller.Snapshot.NextAttemptAt;
        Assert.NotNull(next);
        Assert.True(next > _time.Now);

        _controller.RetryNow();

        Assert.Equal(attempts + 1, _transports.Created.Count);
    }

    [Fact]
    public void Retry_now_while_online_or_already_connecting_starts_nothing()
    {
        Online();
        var attempts = _transports.Created.Count;

        _controller.RetryNow();

        Assert.Equal(attempts, _transports.Created.Count);
        Assert.Equal(LinkPhase.Online, _controller.Snapshot.Phase);
    }

    [Fact]
    public void A_connect_that_succeeds_after_Retry_now_brings_the_link_back()
    {
        var transport = Online();
        transport.Close();
        Assert.Equal(LinkPhase.Offline, _controller.Snapshot.Phase);

        _controller.RetryNow();
        _transports.Latest.Receive(Messages.Welcome());

        Assert.Equal(LinkPhase.Online, _controller.Snapshot.Phase);
        Assert.Equal(BadgeState.Connected, _controller.Snapshot.Badge);
    }

    [Fact]
    public void A_reconnect_signs_in_again_with_the_token_and_restores_the_badge()
    {
        var token = StartWithToken();
        _transports.Latest.Receive(Messages.Welcome());
        _transports.Latest.Close();
        Tick(1);

        _transports.Latest.Receive(Messages.Welcome());

        Assert.Equal(2, _transports.Created.Count);
        Assert.Equal(token, Assert.Single(_transports.Latest.SentOf<HelloMessage>()).DeviceToken);
        Assert.Equal(BadgeState.Connected, _controller.Snapshot.Badge);
        Assert.Null(_controller.Snapshot.OfflineSince);
    }

    [Fact]
    public void A_key_held_through_an_outage_is_restated_by_the_first_heartbeat_after_the_reconnect()
    {
        Online().Close();
        _controller.SetGroupKey(true);
        Tick(1);

        _transports.Latest.Receive(Messages.Welcome());

        var beat = Assert.Single(_transports.Latest.SentOf<HeartbeatMessage>());
        Assert.Equal([WireAction.Group], beat.Held);
        Assert.Empty(_transports.Latest.SentOf<PressMessage>());
    }

    [Fact]
    public void A_key_released_during_an_outage_is_not_restated()
    {
        var transport = Online();
        _controller.SetGroupKey(true);
        transport.Close();
        _controller.SetGroupKey(false);
        Tick(1);

        _transports.Latest.Receive(Messages.Welcome());

        Assert.Empty(Assert.Single(_transports.Latest.SentOf<HeartbeatMessage>()).Held);
    }

    [Fact]
    public void A_connection_that_goes_quiet_for_the_timeout_is_dropped_and_retried()
    {
        var transport = Online();

        Tick(5.9);
        Assert.Equal(LinkPhase.Online, _controller.Snapshot.Phase);
        Tick(0.1);

        Assert.Equal(LinkPhase.Offline, _controller.Snapshot.Phase);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public void Anything_from_the_bot_counts_as_life()
    {
        var transport = Online();

        for (var i = 0; i < 5; i++)
        {
            Tick(4);
            transport.Receive(new HeartbeatAckMessage(i, 0));
        }

        Assert.Equal(LinkPhase.Online, _controller.Snapshot.Phase);
    }

    [Fact]
    public void A_connection_that_opens_but_never_welcomes_is_dropped_after_the_deadline()
    {
        StartWithToken();

        Tick(14.9);
        Assert.Equal(LinkPhase.Connecting, _controller.Snapshot.Phase);
        Tick(0.2);

        Assert.Equal(LinkPhase.Offline, _controller.Snapshot.Phase);
        Assert.True(_transports.Created[0].Disposed);
    }

    [Fact]
    public void A_send_that_fails_drops_the_connection()
    {
        var transport = Online();
        transport.SendFailure = new IOException("broken pipe");

        Tick(2);

        Assert.Equal(LinkPhase.Offline, _controller.Snapshot.Phase);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public void Events_from_a_connection_that_was_replaced_are_ignored()
    {
        var old = Online();
        old.Close();
        Tick(1);
        _transports.Latest.Receive(Messages.Welcome());

        old.Receive(Messages.Error(ErrorCode.Revoked));
        old.Close();

        Assert.Equal(LinkPhase.Online, _controller.Snapshot.Phase);
    }

    [Fact]
    public void A_message_the_app_cannot_read_drops_the_connection_without_failing_the_link()
    {
        var transport = Online();

        transport.ReceiveRaw("{\"v\":2,\"type\":\"nonsense\"}");

        Assert.Equal(LinkPhase.Offline, _controller.Snapshot.Phase);
    }

    [Fact]
    public void A_newer_protocol_version_asks_the_player_to_update()
    {
        var transport = Online();

        transport.ReceiveRaw("{\"v\":3,\"type\":\"welcome\"}");

        Assert.Equal(LinkFailure.VersionMismatch, _controller.Snapshot.Failure);
        Assert.Equal(LinkPhase.Failed, _controller.Snapshot.Phase);
    }

    [Fact]
    public void A_bot_from_before_all_teams_asks_the_player_to_update_and_stops_reconnecting()
    {
        StartWithToken();

        _transports.Latest.ReceiveRaw("{\"v\":1,\"type\":\"welcome\",\"userId\":\"42\",\"displayName\":\"Ann\",\"callerOf\":null,\"heartbeatMs\":2000,\"timeoutMs\":6000,\"serverVersion\":\"0.1.0\"}");
        var created = _transports.Created.Count;
        Tick(120);

        Assert.Equal(LinkPhase.Failed, _controller.Snapshot.Phase);
        Assert.Equal(LinkFailure.VersionMismatch, _controller.Snapshot.Failure);
        Assert.Equal(created, _transports.Created.Count);
    }

    [Fact]
    public void A_status_before_welcome_is_a_protocol_error()
    {
        StartWithToken();

        _transports.Latest.Receive(Messages.Status());

        Assert.Equal(LinkFailure.ProtocolError, _controller.Snapshot.Failure);
    }

    // ----- refusals -----

    [Theory]
    [InlineData(ErrorCode.Revoked, LinkFailure.Revoked, true)]
    [InlineData(ErrorCode.Expired, LinkFailure.TokenExpired, true)]
    [InlineData(ErrorCode.NotMember, LinkFailure.NotMember, false)]
    [InlineData(ErrorCode.VersionMismatch, LinkFailure.VersionMismatch, false)]
    public void A_refused_device_token_stops_everything_and_only_a_dead_token_is_forgotten(ErrorCode code, LinkFailure failure, bool forgotten)
    {
        StartWithToken();

        _transports.Latest.Receive(Messages.Error(code));
        var created = _transports.Created.Count;
        Tick(120);

        Assert.Equal(LinkPhase.Failed, _controller.Snapshot.Phase);
        Assert.Equal(failure, _controller.Snapshot.Failure);
        Assert.Equal(BadgeState.Disconnected, _controller.Snapshot.Badge);
        Assert.Equal(forgotten ? 1 : 0, _rejected);
        Assert.Equal(created, _transports.Created.Count);
    }

    [Theory]
    [InlineData(ErrorCode.BadCode, LinkFailure.BadCode)]
    [InlineData(ErrorCode.Used, LinkFailure.CodeUsed)]
    [InlineData(ErrorCode.Expired, LinkFailure.CodeExpired)]
    public void A_refused_connect_code_fails_without_touching_any_saved_token(ErrorCode code, LinkFailure failure)
    {
        _controller.Start(new LinkCredential.Code(ConnectCode.Generate(Host).Format()), Host);

        _transports.Latest.Receive(Messages.Error(code));
        Tick(120);

        Assert.Equal(failure, _controller.Snapshot.Failure);
        Assert.Equal(0, _rejected);
        Assert.Single(_transports.Created);
    }

    [Fact]
    public void Being_replaced_by_a_newer_app_is_final_so_two_apps_never_evict_each_other()
    {
        var transport = Online();

        transport.Close(4000);
        Tick(300);

        Assert.Equal(LinkPhase.Failed, _controller.Snapshot.Phase);
        Assert.Equal(LinkFailure.ReplacedByNewerApp, _controller.Snapshot.Failure);
        Assert.Equal(BadgeState.Disconnected, _controller.Snapshot.Badge);
        Assert.Single(_transports.Created);
        Assert.Equal(0, _rejected);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(1011)]
    [InlineData(1008)]
    [InlineData(null)]
    public void Any_other_close_is_a_drop_and_the_app_reconnects(int? code)
    {
        var transport = Online();

        transport.Close(code);
        Tick(1);

        Assert.Equal(2, _transports.Created.Count);
    }

    // ----- linking by code and by sign-in -----

    [Fact]
    public void A_code_is_never_tried_again_once_hello_was_sent()
    {
        _controller.Start(new LinkCredential.Code(ConnectCode.Generate(Host).Format()), Host);

        _transports.Latest.Close();
        Tick(60);

        Assert.Equal(LinkFailure.Interrupted, _controller.Snapshot.Failure);
        Assert.Single(_transports.Created);
    }

    [Fact]
    public void A_code_is_tried_again_when_the_server_could_not_be_reached_at_all()
    {
        _transports.Configure = t => t.ConnectFailure = new HttpRequestException("down");
        _controller.Start(new LinkCredential.Code(ConnectCode.Generate(Host).Format()), Host);

        Tick(1);

        Assert.Equal(2, _transports.Created.Count);
        Assert.NotEqual(LinkPhase.Failed, _controller.Snapshot.Phase);
    }

    [Fact]
    public void A_sign_in_that_drops_is_tried_again_until_it_runs_out()
    {
        _controller.Start(new LinkCredential.LoginId(NewSecret()), Host);

        _transports.Latest.Close();
        Tick(1);
        Assert.Equal(2, _transports.Created.Count);
        Assert.Equal(LinkPhase.SigningIn, _controller.Snapshot.Phase);

        Tick(600);

        Assert.Equal(LinkFailure.SignInTimedOut, _controller.Snapshot.Failure);
        Assert.Equal(LinkPhase.Failed, _controller.Snapshot.Phase);
    }

    [Fact]
    public void A_sign_in_waits_in_silence_without_the_welcome_deadline()
    {
        _controller.Start(new LinkCredential.LoginId(NewSecret()), Host);

        Tick(120);

        Assert.Equal(LinkPhase.SigningIn, _controller.Snapshot.Phase);
        Assert.Single(_transports.Created);
    }

    // ----- open mic -----

    [Fact]
    public void Open_mic_is_not_sent_while_offline()
    {
        StartWithToken();

        _controller.SetOpenMic(true);

        Assert.Empty(_transports.Latest.SentOf<OpenMicMessage>());
        Assert.False(_controller.Snapshot.OpenMic);
    }

    [Fact]
    public void Any_player_latches_open_mic_and_it_rides_in_every_heartbeat()
    {
        var transport = Online();

        _controller.SetOpenMic(true);
        Tick(2);

        var message = Assert.Single(transport.SentOf<OpenMicMessage>());
        Assert.True(message.On);
        Assert.True(_controller.Snapshot.OpenMic);
        Assert.True(transport.SentOf<HeartbeatMessage>().Last().OpenMic);

        _controller.SetOpenMic(false);
        Assert.False(_controller.Snapshot.OpenMic);
    }

    [Fact]
    public void The_open_mic_latch_does_not_survive_a_dropped_connection()
    {
        var transport = Online();
        _controller.SetOpenMic(true);

        transport.Close();
        Tick(1);
        _transports.Latest.Receive(Messages.Welcome());

        Assert.False(_controller.Snapshot.OpenMic);
        Assert.False(_transports.Latest.SentOf<HeartbeatMessage>().Single().OpenMic);
        Assert.Equal(LinkNotice.OpenMicOffAfterDrop, _controller.Snapshot.Notice);
    }

    [Fact]
    public void When_the_bot_ends_open_mic_the_latch_follows_so_no_heartbeat_turns_it_back_on()
    {
        var transport = Online();
        _controller.SetOpenMic(true);
        Advance(1.5);

        transport.Receive(Messages.Status(openMic: false));

        Assert.False(_controller.Snapshot.OpenMic);
    }

    [Fact]
    public void A_status_sent_before_the_bot_saw_the_latch_does_not_undo_it()
    {
        var transport = Online();
        _controller.SetOpenMic(true);
        Advance(0.2);

        transport.Receive(Messages.Status(openMic: false));

        Assert.True(_controller.Snapshot.OpenMic);
    }


    // ----- unlink and stop -----

    [Fact]
    public async Task Unlinking_tells_the_bot_and_forgets_the_credential()
    {
        var transport = Online();

        await _controller.UnlinkAsync();

        Assert.Single(transport.SentOf<UnlinkMessage>());
        Assert.Equal(LinkPhase.NotLinked, _controller.Snapshot.Phase);
        Assert.Equal(BadgeState.Disconnected, _controller.Snapshot.Badge);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task Unlinking_while_offline_just_stops()
    {
        var transport = Online();
        transport.Close();

        await _controller.UnlinkAsync();

        Assert.Empty(transport.SentOf<UnlinkMessage>());
        Assert.Equal(LinkPhase.NotLinked, _controller.Snapshot.Phase);
        Tick(100);
        Assert.Single(_transports.Created);
    }

    [Fact]
    public void Stopping_cancels_a_connect_in_progress_and_nothing_afterwards_counts()
    {
        _transports.Configure = t => t.ConnectGate = new TaskCompletionSource();
        StartWithToken();
        var transport = _transports.Latest;

        _controller.Stop();
        transport.ConnectGate!.SetResult();
        Tick(60);

        Assert.Equal(LinkPhase.NotLinked, _controller.Snapshot.Phase);
        Assert.Empty(transport.SentOf<HelloMessage>());
        Assert.Single(_transports.Created);
    }

    [Fact]
    public void Changed_fires_only_when_the_snapshot_differs()
    {
        Online();
        var count = _snapshots.Count;

        Tick(0.1);
        Tick(0.1);

        Assert.Equal(count, _snapshots.Count);
    }
}
