using System.Net.WebSockets;
using System.Text;
using Kzg.Bot.Control;
using Kzg.Core.Abstractions;
using Kzg.Core.Pairing;
using Kzg.Protocol;
using static Kzg.Bot.Tests.Control.Harness;

namespace Kzg.Bot.Tests.Control;

// Real WebSockets over real loopback sockets, against real pairing on a temp folder.
public sealed class ControlServerTests : IAsyncDisposable
{
    private readonly Harness _bot = new();

    public ValueTask DisposeAsync() => _bot.DisposeAsync();

    [Fact]
    public async Task A_connect_code_signs_in_once_and_issues_a_token_that_survives_a_restart()
    {
        var (_, welcome) = await _bot.SignInAsync(Alice);

        Assert.Equal("1", welcome.UserId);
        Assert.Equal("Alice", welcome.DisplayName);
        Assert.Equal((2000, 6000, "0.1.0"), (welcome.HeartbeatMs, welcome.TimeoutMs, welcome.ServerVersion));
        Assert.True(WireSecret.IsValid(welcome.DeviceToken));
        await _bot.WaitForEventAsync("connected 1");

        // A restart: a new server over the same devices.json.
        await using var restarted = new Harness(folder: _bot.Folder);
        var app = await restarted.ConnectAsync();
        await app.SendAsync(Hello(token: welcome.DeviceToken));
        var again = await app.ExpectAsync<WelcomeMessage>();

        Assert.Equal("1", again.UserId);
        Assert.Null(again.DeviceToken);
    }

    [Fact]
    public async Task A_player_is_welcomed_with_their_name_as_discord_has_it()
    {
        var (_, welcome) = await _bot.SignInAsync(Bob);

        Assert.Equal("Боб", welcome.DisplayName);
    }

    [Fact]
    public async Task A_used_expired_or_unknown_code_is_refused_with_its_reason()
    {
        var code = _bot.Codes.Issue(Alice, Host);
        var first = await _bot.ConnectAsync();
        await first.SendAsync(Hello(code: code));
        // The code is spent only once the server has answered; without this the second hello can win the race.
        await first.ExpectAsync<WelcomeMessage>();

        await AssertRefusedAsync(Hello(code: code), ErrorCode.Used);
        await AssertRefusedAsync(Hello(code: ConnectCode.Generate(Host).Format()), ErrorCode.BadCode);

        var stale = _bot.Codes.Issue(Alice, Host);
        _bot.Time.Now += ConnectCodes.Lifetime;
        await AssertRefusedAsync(Hello(code: stale), ErrorCode.Expired);
    }

    [Fact]
    public async Task A_player_not_in_the_guild_is_refused()
    {
        await AssertRefusedAsync(Hello(code: _bot.Codes.Issue(Stranger, Host)), ErrorCode.NotMember);

        var (_, welcome) = await _bot.SignInAsync(Bob);
        _bot.Discord.Members.TryRemove(Bob.V, out _);
        await AssertRefusedAsync(Hello(token: welcome.DeviceToken), ErrorCode.NotMember);
    }

    [Fact]
    public async Task An_unknown_token_is_revoked_and_one_idle_for_ninety_days_has_expired()
    {
        await AssertRefusedAsync(Hello(token: WireSecret.Generate()), ErrorCode.Revoked);

        var (_, welcome) = await _bot.SignInAsync(Bob);
        _bot.Wall.Now += TimeSpan.FromDays(90);
        await AssertRefusedAsync(Hello(token: welcome.DeviceToken), ErrorCode.Expired);
    }

    [Fact]
    public async Task Revoking_a_player_closes_their_live_app_and_their_token_stops_working()
    {
        var (app, welcome) = await _bot.SignInAsync(Bob);
        await _bot.WaitForEventAsync("connected 2");

        Assert.Equal(1, _bot.Server.RevokeUser(Bob));

        var (status, _, before) = await app.ExpectCloseAsync();
        Assert.Equal(CloseCodes.Policy, status);
        Assert.Equal(ErrorCode.Revoked, Assert.IsType<ErrorMessage>(Assert.Single(before)).Code);
        Assert.Contains("disconnected 2", _bot.Events.Log);
        await AssertRefusedAsync(Hello(token: welcome.DeviceToken), ErrorCode.Revoked);
    }

    [Fact]
    public async Task Unlinking_forgets_this_apps_token_and_closes_normally()
    {
        var (app, welcome) = await _bot.SignInAsync(Bob);

        await app.SendAsync(new UnlinkMessage());

        Assert.Equal(CloseCodes.Normal, (await app.ExpectCloseAsync()).Status);
        await AssertRefusedAsync(Hello(token: welcome.DeviceToken), ErrorCode.Revoked);
    }

    [Fact]
    public async Task Signing_in_from_a_second_app_closes_the_first_which_is_told_not_to_return()
    {
        var (first, _) = await _bot.SignInAsync(Bob);
        var (second, _) = await _bot.SignInAsync(Bob, Guid.Parse("6f1c2a4e-0000-4000-8000-000000000002"));

        var (status, _, _) = await first.ExpectCloseAsync();

        Assert.Equal(CloseCodes.Replaced, status);
        Assert.Equal(4000, (int)CloseCodes.Replaced);
        Assert.Equal(["connected 2", "connected 2"], _bot.Events.Log);
        Assert.Equal([Bob], _bot.Server.SignedIn);

        await second.SendAsync(new PressMessage(WireAction.Group, true, 1));
        await _bot.WaitForEventAsync("edge 2 Group True AppKey");
    }

    [Fact]
    public async Task Presses_toggles_and_heartbeats_reach_the_engine_and_heartbeats_are_echoed()
    {
        var (app, _) = await _bot.SignInAsync(Alice);

        await app.SendAsync(new PressMessage(WireAction.Group, true, 1));
        await app.SendAsync(new PressMessage(WireAction.Group, false, 1));
        await app.SendAsync(new OpenMicMessage(true, 1));
        await app.SendAsync(new HeartbeatMessage(7, [WireAction.Group], true, 123_456));
        var ack = await app.ExpectAsync<HeartbeatAckMessage>();
        await app.SendAsync(new PressMessage(WireAction.Group, false, 2));
        await _bot.WaitForEventAsync("edge 1 Group False AppKey");

        Assert.Equal((7, 123_456), (ack.Seq, ack.SentAtMs));
        // The repeated press seq 1 is the app resending; it is dropped.
        Assert.Equal(
            ["connected 1", "edge 1 Group True AppKey", "edge 1 OpenMic True AppKey", "heartbeat 1 [Group] True", "edge 1 Group False AppKey"],
            _bot.Events.Log);
    }

    [Fact]
    public async Task The_all_teams_key_reaches_the_engine_as_its_own_action_sharing_the_press_sequence()
    {
        var (app, _) = await _bot.SignInAsync(Bob);

        await app.SendAsync(new PressMessage(WireAction.AllTeams, true, 1));
        await app.SendAsync(new PressMessage(WireAction.Group, true, 2));
        await app.SendAsync(new HeartbeatMessage(1, [WireAction.Group, WireAction.AllTeams], false, 0));
        await app.ExpectAsync<HeartbeatAckMessage>();
        // One press sequence for both keys, as the app numbers them: an older one is a resend.
        await app.SendAsync(new PressMessage(WireAction.AllTeams, false, 2));
        await app.SendAsync(new PressMessage(WireAction.AllTeams, false, 3));
        await _bot.WaitForEventAsync("edge 2 AllTeams False AppKey");

        Assert.Equal(
            ["connected 2", "edge 2 AllTeams True AppKey", "edge 2 Group True AppKey", "heartbeat 2 [Group,AllTeams] False", "edge 2 AllTeams False AppKey"],
            _bot.Events.Log);
    }

    [Fact]
    public async Task A_burst_of_statuses_is_coalesced_to_the_newest_at_ten_a_second_at_most()
    {
        // The server's timers move by hand: what goes out at each instant, however fast the machine is.
        var timers = new ManualTimers();
        await using var bot = new Harness(timers: timers);
        var (app, _) = await bot.SignInAsync(Bob);
        await bot.WaitForEventAsync("connected 2");
        void Send(int i) => bot.Server.SendStatus(Bob, new StatusMessage([], false, [$"Attack {i}"], []));
        async Task<string> Next() => (await app.ExpectAsync<StatusMessage>()).Targets[0];
        var interval = new ControlOptions().StatusInterval;
        // The connection's own timers, such as its heartbeat deadline, wait too; only the status timer is counted.
        var others = timers.Waiting;
        Assert.Equal(TimeSpan.FromMilliseconds(100), interval);

        // A burst: the first goes at once, the rest wait for the interval and only the newest of them goes.
        for (var i = 0; i < 30; i++)
        {
            Send(i);
        }
        Assert.Equal("Attack 0", await Next());
        Assert.Equal(others + 1, timers.Waiting);
        timers.Advance(interval - TimeSpan.FromTicks(1));
        Assert.Equal(others + 1, timers.Waiting);
        timers.Advance(TimeSpan.FromTicks(1));
        Assert.Equal("Attack 29", await Next());

        // Right after a send, the next waits a whole interval from that send, not from when it was queued.
        Send(30);
        timers.Advance(interval / 2);
        Send(31);
        Assert.Equal(others + 1, timers.Waiting);
        timers.Advance(interval / 2 - TimeSpan.FromTicks(1));
        Assert.Equal(others + 1, timers.Waiting);
        timers.Advance(TimeSpan.FromTicks(1));
        Assert.Equal("Attack 31", await Next());

        // After a quiet interval a status goes at once again: at most one per interval, ten a second.
        timers.Advance(interval);
        Send(32);
        Assert.Equal(others, timers.Waiting);
        Assert.Equal("Attack 32", await Next());
        Assert.Throws<ArgumentException>(() => bot.Server.SendStatus(Bob, new StatusMessage([], false, [""], [])));
        bot.Server.SendStatus(Alice, new StatusMessage([], false, [], []));
    }

    [Fact]
    public async Task A_message_over_4_kb_closes_with_1009_whole_or_in_fragments()
    {
        var app = await _bot.ConnectAsync();
        await app.SendRawAsync(new string('x', WireProtocol.MaxMessageBytes + 1));
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, (await app.ExpectCloseAsync()).Status);

        var fragmented = await _bot.ConnectAsync();
        var chunk = Encoding.UTF8.GetBytes(new string('y', 1500));
        for (var i = 0; i < 3; i++)
        {
            await fragmented.Socket.SendAsync(chunk, WebSocketMessageType.Text, endOfMessage: false, CancellationToken.None);
        }
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, (await fragmented.ExpectCloseAsync()).Status);
    }

    [Fact]
    public async Task A_message_of_exactly_4_kb_is_read()
    {
        var app = await _bot.ConnectAsync();
        var hello = Encoding.UTF8.GetString(MessageCodec.Serialize(Hello(code: _bot.Codes.Issue(Bob, Host))));
        await app.SendRawAsync(hello.TrimEnd('}') + new string(' ', WireProtocol.MaxMessageBytes - hello.Length) + "}");

        await app.ExpectAsync<WelcomeMessage>();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"v":2,"type":"welcome","userId":"1"}""")]
    [InlineData("""{"v":2,"type":"hello","appVersion":"1.0.0","installId":"6f1c2a4e-0000-4000-8000-000000000001"}""")]
    public async Task A_malformed_message_closes_with_1007(string text)
    {
        var app = await _bot.ConnectAsync();

        await app.SendRawAsync(text);

        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, (await app.ExpectCloseAsync()).Status);
    }

    [Fact]
    public async Task Another_protocol_version_is_told_so_before_the_close()
    {
        var app = await _bot.ConnectAsync();

        await app.SendRawAsync("""{"v":3,"type":"hello"}""");

        var (status, _, before) = await app.ExpectCloseAsync();
        Assert.Equal(CloseCodes.Policy, status);
        Assert.Equal(ErrorCode.VersionMismatch, Assert.IsType<ErrorMessage>(Assert.Single(before)).Code);
    }

    [Fact]
    public async Task An_app_from_before_all_teams_is_told_to_update_instead_of_being_dropped_as_malformed()
    {
        var app = await _bot.ConnectAsync();

        await app.SendRawAsync($$"""{"v":1,"type":"hello","appVersion":"0.1.0","installId":"{{Guid.NewGuid()}}"}""");

        var (status, _, before) = await app.ExpectCloseAsync();
        Assert.Equal(CloseCodes.Policy, status);
        Assert.Equal(ErrorCode.VersionMismatch, Assert.IsType<ErrorMessage>(Assert.Single(before)).Code);
    }

    [Fact]
    public async Task A_binary_message_closes_with_1003()
    {
        var app = await _bot.ConnectAsync();

        await app.SendRawAsync([1, 2, 3], WebSocketMessageType.Binary);

        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, (await app.ExpectCloseAsync()).Status);
    }

    [Fact]
    public async Task A_second_hello_is_against_policy()
    {
        var (app, _) = await _bot.SignInAsync(Bob);

        await app.SendAsync(Hello(code: _bot.Codes.Issue(Bob, Host)));

        Assert.Equal(CloseCodes.Policy, (await app.ExpectCloseAsync()).Status);
    }

    [Fact]
    public async Task Without_a_hello_in_time_the_connection_is_closed()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), new ControlOptions().HelloDeadline);
        await using var bot = new Harness(new ControlOptions { HelloDeadline = TimeSpan.FromMilliseconds(300) });
        var app = await bot.ConnectAsync();
        var started = DateTime.UtcNow;

        // Anything before the hello counts for nothing, and doesn't buy time.
        await app.SendAsync(new HeartbeatMessage(1, [], false, 0));
        var (status, reason, before) = await app.ExpectCloseAsync();

        Assert.Equal(CloseCodes.Policy, status);
        Assert.Equal("no hello", reason);
        Assert.Empty(before);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_signed_in_app_that_goes_silent_is_closed_and_its_sessions_end()
    {
        Assert.Equal(TimeSpan.FromSeconds(6), new ControlOptions().IdleTimeout);
        await using var bot = new Harness(new ControlOptions { IdleTimeout = TimeSpan.FromMilliseconds(400) });
        var (app, _) = await bot.SignInAsync(Bob);

        var (status, reason, _) = await app.ExpectCloseAsync();

        Assert.Equal(CloseCodes.Policy, status);
        Assert.Equal("no heartbeat", reason);
        Assert.Equal(["connected 2", "disconnected 2"], bot.Events.Log);
    }

    [Fact]
    public async Task An_app_that_closes_ends_its_sessions()
    {
        var (app, _) = await _bot.SignInAsync(Bob);

        await app.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await app.ServerRun.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["connected 2", "disconnected 2"], _bot.Events.Log);
    }

    [Fact]
    public async Task An_app_that_vanishes_without_closing_ends_its_sessions()
    {
        var (app, _) = await _bot.SignInAsync(Bob);

        app.Socket.Abort();
        app.Socket.Dispose();
        await app.ServerRun.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["connected 2", "disconnected 2"], _bot.Events.Log);
    }

    [Fact]
    public async Task When_discord_cannot_be_reached_the_app_is_told_to_try_again()
    {
        _bot.Discord.Unreachable = true;
        var app = await _bot.ConnectAsync();

        await app.SendAsync(Hello(code: _bot.Codes.Issue(Bob, Host)));

        Assert.Equal(CloseCodes.ServerError, (await app.ExpectCloseAsync()).Status);
    }

    [Fact]
    public async Task Shutting_down_closes_every_app_as_going_away()
    {
        var (signedIn, _) = await _bot.SignInAsync(Bob);
        var waiting = await _bot.ConnectAsync();
        await Task.Delay(50);

        _bot.Server.CloseAll();

        Assert.Equal(CloseCodes.GoingAway, (await signedIn.ExpectCloseAsync()).Status);
        Assert.Equal(CloseCodes.GoingAway, (await waiting.ExpectCloseAsync()).Status);
    }

    [Fact]
    public async Task Cancelling_the_hosts_token_closes_the_connection_as_going_away()
    {
        using var stop = new CancellationTokenSource();
        var app = await _bot.ConnectAsync(stop.Token);
        await app.SendAsync(Hello(code: _bot.Codes.Issue(Bob, Host)));
        await app.ExpectAsync<WelcomeMessage>();

        await stop.CancelAsync();

        Assert.Equal(CloseCodes.GoingAway, (await app.ExpectCloseAsync()).Status);
    }

    private async Task AssertRefusedAsync(HelloMessage hello, ErrorCode code)
    {
        var app = await _bot.ConnectAsync();
        await app.SendAsync(hello);
        var (status, _, before) = await app.ExpectCloseAsync();
        Assert.Equal(CloseCodes.Policy, status);
        Assert.Equal(code, Assert.IsType<ErrorMessage>(Assert.Single(before)).Code);
    }
}
