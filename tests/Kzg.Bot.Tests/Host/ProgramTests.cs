using System.Net;
using Kzg.Bot.Fake;
using Kzg.Protocol;

namespace Kzg.Bot.Tests.Hosting;

public sealed class ProgramTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "kzg-program-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_fake_bot_starts_answers_healthz_prints_demo_codes_and_stops_on_cancel()
    {
        var written = new StringWriter();
        var output = TextWriter.Synchronized(written);
        var error = new StringWriter();
        using var stop = new CancellationTokenSource();
        var listening = new TaskCompletionSource<BotApp>();

        var run = Program.RunAsync(["--fake", "--listen", "127.0.0.1:0"], output, error, stop.Token, listening.SetResult);
        var bot = await listening.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var http = new HttpClient();
        var health = await http.GetStringAsync(new Uri(bot.Address, "/healthz"));
        var codeLine = written.ToString().Split('\n').First(line => line.Contains("Alice"));
        var code = codeLine[(codeLine.IndexOf("KZG1.", StringComparison.Ordinal))..].Trim();
        using var app = new System.Net.WebSockets.ClientWebSocket();
        await app.ConnectAsync(new UriBuilder(bot.Address) { Scheme = "ws", Path = "/ws" }.Uri, CancellationToken.None);
        await app.SendAsync(MessageCodec.Serialize(new HelloMessage("1.0.0", Guid.NewGuid(), Code: code)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[4096];
        var received = await app.ReceiveAsync(buffer, CancellationToken.None);
        var welcome = Assert.IsType<MessageParse<ServerMessage>.Parsed>(MessageCodec.ParseServer(buffer.AsSpan(0, received.Count)));
        await stop.CancelAsync();
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("ok\n", health);
        Assert.Equal(0, exit);
        Assert.Equal("1001", Assert.IsType<WelcomeMessage>(welcome.Message).UserId);
        Assert.Equal("", error.ToString());
        // The demo codes are printed for the developer, on their own lines, and never go through the logger.
        Assert.DoesNotContain(written.ToString().Split('\n'), line => line.Contains("info ") && line.Contains("KZG1."));
    }

    [Fact]
    public async Task Help_prints_the_usage_and_exits_zero()
    {
        var output = new StringWriter();

        var exit = await Program.RunAsync(["--help"], output, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("--fake", output.ToString());
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("--listen")]
    [InlineData("--listen", "somewhere")]
    [InlineData("--data-dir")]
    public async Task A_bad_command_line_exits_64_with_the_reason_and_the_usage(params string[] args)
    {
        var error = new StringWriter();

        var exit = await Program.RunAsync(args, new StringWriter(), error, CancellationToken.None);

        Assert.Equal(64, exit);
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public async Task Without_fake_a_Discord_that_will_not_start_is_reported_without_the_token()
    {
        var folder = Folder();
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "secrets.json"), "{ \"bot1\": \"a-bot-token-value\" }");
            await File.WriteAllTextAsync(Path.Combine(folder, "config.json"), "{ \"schemaVersion\": 1 }");
            var error2 = new StringWriter();
            var refusing = new FakeDiscordRuntime(new Kzg.Core.Testing.ManualTimeSource())
            {
                FailStart = new Kzg.Discord.DiscordStartException("the primary bot 'bot1' couldn't sign in to Discord (HttpRequestException); check its token in secrets.json"),
            };
            var valid = await Program.RunAsync(["--data-dir", folder, "--listen", "127.0.0.1:0"], new StringWriter(), error2, CancellationToken.None, discord: (_, _, _) => refusing, setup: new SetupHooks { KeepAwake = false });
            Assert.Equal(1, valid);
            Assert.Contains("couldn't sign in", error2.ToString());
            Assert.DoesNotContain("a-bot-token-value", error2.ToString());
            Assert.True(refusing.Disposed);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_broken_config_is_reported_by_line_without_quoting_a_value()
    {
        var folder = Folder();
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "config.json"), "{ \"schemaVersion\": 1, \"prerollMs\": \"not-a-number-secret\" }");
            var error = new StringWriter();

            var exit = await Program.RunAsync(["--data-dir", folder], new StringWriter(), error, CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Contains("config.json is broken", error.ToString());
            Assert.DoesNotContain("not-a-number-secret", error.ToString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_command_line_reads_every_option()
    {
        var options = LaunchOptions.Parse(["--fake", "--data-dir", "d", "--listen", "127.0.0.1:9"]);

        Assert.Equal(new LaunchOptions(true, "d", IPEndPoint.Parse("127.0.0.1:9"), false), options);
        Assert.True(LaunchOptions.Parse(["-h"]).Help);
        Assert.True(LaunchOptions.Parse(["--setup"]).Setup);
        Assert.False(options.Setup);
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--fake", "--setup"]));
    }

    [Fact]
    public void The_demo_world_seats_five_players_and_leaves_Dana_out_of_voice()
    {
        var world = DemoWorld.Create(new Kzg.Core.Testing.ManualTimeSource(), "h");
        List<(Kzg.Core.Abstractions.UserId, Kzg.Core.Abstractions.ChannelId?)> seated = [];
        world.Gateway.PresenceChanged += (u, c) => seated.Add((u, c));

        world.SeatPlayers();

        Assert.Equal(5, seated.Count);
        Assert.DoesNotContain(seated, s => s.Item1 == DemoWorld.Dana);
        Assert.Equal("Attack 2", world.Names.NameOf(DemoWorld.Attack2));
        Assert.Equal("Channel 5", world.Names.NameOf(new Kzg.Core.Abstractions.ChannelId(5)));
        Assert.Equal(6, world.Gateway.Links.Count);
    }
}
