using System.Net;
using System.Net.WebSockets;
using Kzg.Bot.Control;
using Kzg.Bot.Fake;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Protocol;

namespace Kzg.Bot.Tests.Hosting;

// The real host: Kestrel on loopback, the real control server and pairing, over the demo guild.
public sealed class HostTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _cleanup = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _cleanup)
        {
            await item.DisposeAsync();
        }
    }

    private async Task<HostRig> RigAsync(Func<BotConfig, BotConfig>? config = null, string? folder = null)
    {
        var rig = new HostRig(config, folder: folder);
        _cleanup.Add(rig);
        return await rig.StartAsync();
    }

    private async Task<AppClient> Track(Task<AppClient> app)
    {
        var client = await app;
        _cleanup.Add(client);
        return client;
    }

    [Fact]
    public async Task Healthz_answers_ok_with_the_headers_every_reply_carries()
    {
        var rig = await RigAsync();

        using var response = await rig.Http.GetAsync(rig.Url("/healthz"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok\n", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Only_get_is_served_and_unknown_paths_are_not_found()
    {
        var rig = await RigAsync();

        using var post = await rig.Http.PostAsync(rig.Url("/healthz"), null);
        using var missing = await rig.Http.GetAsync(rig.Url("/admin"));
        using var plainWs = await rig.Http.GetAsync(rig.Url("/ws"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, plainWs.StatusCode);
    }

    [Fact]
    public async Task An_app_signs_in_over_ws_with_a_connect_code_and_its_heartbeat_is_acknowledged()
    {
        var rig = await RigAsync();

        var (app, welcome) = await rig.SignInAsync(DemoWorld.Alice);
        _cleanup.Add(app);
        await app.SendAsync(new HeartbeatMessage(1, [], false, 777));
        var ack = await app.UntilAsync<HeartbeatAckMessage>(_ => true);

        Assert.Equal("1001", welcome.UserId);
        Assert.NotNull(welcome.DeviceToken);
        Assert.Equal((1, 777), (ack.Seq, ack.SentAtMs));
    }

    [Fact]
    public async Task A_first_status_arrives_with_the_players_channel_and_team_and_the_bot_down_warning_is_absent()
    {
        var rig = await RigAsync();
        var (app, _) = await rig.SignInAsync(DemoWorld.Bram);
        _cleanup.Add(app);

        var status = await app.UntilAsync<StatusMessage>(_ => true);

        Assert.Equal("Attack 2", status.ChannelName);
        Assert.Equal(WireGroup.Attack, status.Group);
        Assert.Empty(status.Active);
        Assert.Empty(status.Warnings);
        Assert.Equal([], status.BotDownIn);
    }

    [Fact]
    public async Task A_status_names_the_VCs_whose_bot_is_down_and_follows_them_as_they_change()
    {
        var rig = await RigAsync();
        var (app, _) = await rig.SignInAsync(DemoWorld.Alice);
        _cleanup.Add(app);
        await app.UntilAsync<StatusMessage>(s => s.Warnings.Count == 0);

        rig.World.Gateway.Link(DemoWorld.Attack2).Ready = false;
        var one = await app.UntilAsync<StatusMessage>(s => s.Warnings.Contains(StatusWarning.BotDown));
        Assert.Equal(["Attack 2"], one.BotDownIn);

        // The warning stays on, so only the names say that another bot went.
        rig.World.Gateway.Link(DemoWorld.Attack3).Ready = false;
        var two = await app.UntilAsync<StatusMessage>(s => s.BotDownIn is { Count: 2 });
        Assert.Equal(["Attack 2", "Attack 3"], two.BotDownIn);
        Assert.Contains(StatusWarning.BotDown, two.Warnings);

        rig.World.Gateway.Link(DemoWorld.Attack2).Ready = true;
        var back = await app.UntilAsync<StatusMessage>(s => s.BotDownIn is { Count: 1 });
        Assert.Equal(["Attack 3"], back.BotDownIn);

        rig.World.Gateway.Link(DemoWorld.Attack3).Ready = true;
        var clear = await app.UntilAsync<StatusMessage>(s => s.Warnings.Count == 0);
        Assert.Equal([], clear.BotDownIn);
    }

    [Fact]
    public async Task A_player_outside_a_team_VC_is_told_no_bot_is_down()
    {
        var rig = await RigAsync();
        rig.World.Gateway.Link(DemoWorld.Attack2).Ready = false;
        var (app, _) = await rig.SignInAsync(DemoWorld.Dana);
        _cleanup.Add(app);

        var status = await app.UntilAsync<StatusMessage>(_ => true);

        Assert.Equal([], status.BotDownIn);
    }

    [Fact]
    public async Task A_player_in_no_voice_channel_is_told_so()
    {
        var rig = await RigAsync();
        var (app, _) = await rig.SignInAsync(DemoWorld.Dana);
        _cleanup.Add(app);

        var status = await app.UntilAsync<StatusMessage>(_ => true);

        Assert.Null(status.ChannelName);
        Assert.Equal([StatusWarning.NotInTeamVc], status.Warnings);
    }

    [Fact]
    public async Task Discord_sign_in_runs_through_start_and_callback_to_a_welcome_with_a_new_token()
    {
        var rig = await RigAsync();
        var login = WireSecret.Generate();
        var app = await Track(rig.ConnectAsync());
        await app.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), LoginId: login));

        // The app is waiting. The browser goes to /auth/start, then Discord sends it back to /auth/callback.
        using var start = await rig.Http.GetAsync(rig.Url($"/auth/start?login={login}"));
        using var callback = await rig.Http.GetAsync(rig.Url($"/auth/callback?code={SignInIdentity.GoodCode}&state={login}"));
        var welcome = await app.ExpectAsync<WelcomeMessage>();

        Assert.Equal(HttpStatusCode.Found, start.StatusCode);
        var location = start.Headers.Location!.AbsoluteUri;
        Assert.StartsWith("https://discord.com/oauth2/authorize?", location);
        Assert.Contains($"state={login}", location);
        Assert.Contains($"client_id={HostRig.ClientId}", location);
        Assert.Contains("scope=identify", location);
        Assert.Contains(Uri.EscapeDataString("https://gvg.example.com/auth/callback"), location);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Equal("1001", welcome.UserId);
        Assert.True(WireSecret.IsValid(welcome.DeviceToken));
    }

    [Fact]
    public async Task A_callback_for_nobody_or_with_a_code_discord_refuses_is_turned_away()
    {
        var rig = await RigAsync();
        var login = WireSecret.Generate();
        var app = await Track(rig.ConnectAsync());
        await app.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), LoginId: login));
        using var started = await rig.Http.GetAsync(rig.Url($"/auth/start?login={login}"));

        using var unknown = await rig.Http.GetAsync(rig.Url($"/auth/callback?code=x&state={WireSecret.Generate()}"));
        using var refused = await rig.Http.GetAsync(rig.Url($"/auth/callback?code=wrong&state={login}"));
        using var garbage = await rig.Http.GetAsync(rig.Url("/auth/callback?state=not-a-secret"));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [Fact]
    public async Task A_device_token_survives_a_restart_because_devices_json_is_read_at_start()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kzg-host-tests", Guid.NewGuid().ToString("N"));
        string token;
        await using (var first = await new HostRig(folder: folder).StartAsync())
        {
            var (app, welcome) = await first.SignInAsync(DemoWorld.Bram);
            await using (app)
            {
                token = welcome.DeviceToken!;
            }
        }

        await using var second = await new HostRig(folder: folder).StartAsync();
        await using var again = await second.ConnectAsync();
        await again.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), DeviceToken: token));
        var welcome2 = await again.ExpectAsync<WelcomeMessage>();

        Assert.Equal("1002", welcome2.UserId);
        Assert.Null(welcome2.DeviceToken);
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task Stopping_the_bot_closes_every_app_with_going_away()
    {
        var rig = await RigAsync();
        var (app, _) = await rig.SignInAsync(DemoWorld.Alice);
        _cleanup.Add(app);

        var closed = app.UntilClosedAsync();
        await rig.Bot.StopAsync();
        await closed;

        Assert.Equal(CloseCodes.GoingAway, app.CloseStatus);
    }

    [Fact]
    public async Task A_second_app_for_the_same_player_replaces_the_first_with_close_code_4000()
    {
        var rig = await RigAsync();
        var (first, _) = await rig.SignInAsync(DemoWorld.Alice);
        _cleanup.Add(first);

        var (second, _) = await rig.SignInAsync(DemoWorld.Alice);
        _cleanup.Add(second);
        await first.UntilClosedAsync();

        Assert.Equal(CloseCodes.Replaced, first.CloseStatus);
        Assert.Equal((WebSocketCloseStatus)4000, first.CloseStatus);
    }

    [Fact]
    public async Task Reconfiguring_changes_whether_discord_sign_in_is_on()
    {
        var rig = await RigAsync();
        var login = WireSecret.Generate();
        var app = await Track(rig.ConnectAsync());
        await app.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), LoginId: login));
        using var before = await rig.Http.GetAsync(rig.Url($"/auth/start?login={login}"));

        rig.Bot.Reconfigure(rig.Bot.Config with { OAuth = new OAuthConfig { Enabled = false } });
        using var after = await rig.Http.GetAsync(rig.Url($"/auth/start?login={login}"));

        Assert.Equal(HttpStatusCode.Found, before.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task A_broken_devices_json_stops_the_start_with_a_message_that_quotes_no_value()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kzg-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "devices.json"), "{ \"schemaVersion\": 1, \"devices\": [ {\"tokenHash\": \"hunter2-secret\"} ");

        try
        {
            var e = Assert.Throws<StartupException>(() => new HostRig(folder: folder));
            Assert.Contains("devices.json", e.Message);
            Assert.DoesNotContain("hunter2", e.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_listen_address_that_is_not_an_endpoint_stops_the_start()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kzg-host-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var world = DemoWorld.Create(new SystemTimeSource(), "h");
            var e = Assert.Throws<StartupException>(() => BotApp.Create(new BotSetup
            {
                Config = world.Config with { Listen = "everywhere" },
                Secrets = Secrets.Empty,
                DataFolder = folder,
                Gateway = world.Gateway,
                Discord = world.Identity,
                Codecs = new Kzg.Audio.ConcentusOpusCodecFactory(),
                ChannelNames = world.Names,
                Log = new StringWriter(),
            }));
            Assert.Contains("listen", e.Message);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Nothing_secret_reaches_the_log_even_with_the_frameworks_own_request_logging_on()
    {
        var rig = await RigAsync();
        var login = WireSecret.Generate();
        var loginApp = await Track(rig.ConnectAsync());
        await loginApp.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), LoginId: login));
        using var start = await rig.Http.GetAsync(rig.Url($"/auth/start?login={login}"));
        using var callback = await rig.Http.GetAsync(rig.Url($"/auth/callback?code={SignInIdentity.GoodCode}&state={login}"));
        var token = (await loginApp.ExpectAsync<WelcomeMessage>()).DeviceToken!;
        var code = rig.Bot.Codes.Issue(DemoWorld.Bram, $"127.0.0.1:{rig.Bot.Address.Port}");
        var codeApp = await Track(rig.ConnectAsync());
        await codeApp.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), Code: code));
        var codeToken = (await codeApp.ExpectAsync<WelcomeMessage>()).DeviceToken!;
        await rig.Bot.DisposeAsync();

        var log = rig.LogText;

        Assert.Contains("Request starting", log);
        Assert.Contains("/auth/callback", log);
        Assert.Contains("listening on", log);
        foreach (var secret in new[] { login, SignInIdentity.GoodCode, token, code, code.Split('.')[2], codeToken, HostRig.BotToken })
        {
            Assert.DoesNotContain(secret, log);
        }
    }
}
