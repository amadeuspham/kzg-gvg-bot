using Kzg.Bot.Discord;
using Kzg.Bot.Logging;
using Kzg.Bot.Tests.Hosting;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Testing;
using Kzg.Discord;
using Kzg.Discord.Commands;

namespace Kzg.Bot.Tests.Setup;

public sealed class AlertMonitorTests
{
    private static readonly ChannelId A1 = new(101), A2 = new(102);

    private readonly ManualTimeSource _time = new();

    private static BotConfig Config(bool second = true) => new()
    {
        AlertsChannelId = new ChannelId(900),
        Groups = new GroupsConfig { Attack = new GroupConfig { Channels = [A1, A2] } },
        Bots =
        [
            new BotEntry { TokenRef = "relay1", Primary = true, ChannelId = A1 },
            new BotEntry { TokenRef = "relay2", ChannelId = second ? A2 : null },
        ],
    };

    private static RelayBotState State(string bot, ChannelId? channel, BotState state) => new(bot, bot == "relay1", channel, state, 0, 0);

    private static string Name(ChannelId channel) => channel == A1 ? "Attack 1" : "Attack 2";

    [Fact]
    public void A_bot_out_for_the_grace_is_reported_once_and_again_when_it_is_back()
    {
        var monitor = new AlertMonitor(_time);
        RelayBotState[] down = [State("relay1", A1, BotState.Ready), State("relay2", A2, BotState.Joining)];

        Assert.Empty(monitor.Check(Config(), true, down, Name));
        _time.Advance(AlertMonitor.Grace - TimeSpan.FromSeconds(1));
        Assert.Empty(monitor.Check(Config(), true, down, Name));
        _time.Advance(TimeSpan.FromSeconds(1));
        var gone = Assert.Single(monitor.Check(Config(), true, down, Name));
        Assert.StartsWith("Relay bot offline: A2 Broadcaster (Attack 2)", gone);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(monitor.Check(Config(), true, down, Name));

        var back = monitor.Check(Config(), true, [State("relay1", A1, BotState.Ready), State("relay2", A2, BotState.Ready)], Name);
        Assert.Equal(["Back online: A2 Broadcaster (Attack 2)."], back);
    }

    [Fact]
    public void A_short_drop_says_nothing_and_neither_does_a_stopped_relay_or_a_bot_taken_out()
    {
        var monitor = new AlertMonitor(_time);
        RelayBotState[] down = [State("relay1", A1, BotState.Joining), State("relay2", null, BotState.Idle)];

        monitor.Check(Config(), true, down, Name);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(monitor.Check(Config(), true, [State("relay1", A1, BotState.Ready), State("relay2", A2, BotState.Ready)], Name));

        monitor.Check(Config(), false, down, Name);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(monitor.Check(Config(), false, down, Name));

        monitor.Check(Config(second: false), true, [State("relay1", A1, BotState.Ready)], Name);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(monitor.Check(Config(second: false), true, [State("relay1", A1, BotState.Ready)], Name));
    }

    [Fact]
    public void Bots_that_go_down_together_are_one_message_and_a_bot_in_the_wrong_channel_counts_as_down()
    {
        var monitor = new AlertMonitor(_time);
        RelayBotState[] down = [State("relay1", A2, BotState.Ready), State("relay2", A2, BotState.Joining)];

        monitor.Check(Config(), true, down, Name);
        _time.Advance(AlertMonitor.Grace);

        var message = Assert.Single(monitor.Check(Config(), true, down, Name));
        Assert.Contains("A1 Broadcaster (Attack 1), A2 Broadcaster (Attack 2)", message);
    }

    [Fact]
    public async Task The_host_posts_what_the_monitor_finds_to_the_alerts_channel_redacted()
    {
        await using var rig = await new HostRig(c => c with { AlertsChannelId = new ChannelId(900), Groups = new GroupsConfig { Defense = new GroupConfig { Channels = [new ChannelId(1)] } }, Bots = [new BotEntry { TokenRef = "bot1", Primary = true, ChannelId = new ChannelId(1) }] }).StartAsync();
        var discord = new FakeDiscordRuntime(_time)
        {
            Bots = [new RelayBotState("bot1", true, new ChannelId(1), BotState.Joining, 0, 0)],
        };
        discord.Names.Reset([(1, "Attack 1")]);
        var host = new GvgHost(rig.Bot, new ConfigStore(Path.Combine(rig.Folder, "config.json")), Secrets.Empty, discord, new Redactor([HostRig.BotToken]));
        var monitor = new AlertMonitor(_time);

        await host.CheckAsync(monitor, default);
        _time.Advance(AlertMonitor.Grace);
        await host.CheckAsync(monitor, default);
        await host.PostAsync(new ChannelId(900), "leak " + HostRig.BotToken, default);
        discord.RefusePosts = true;
        await host.AlertAsync("dropped", default);

        Assert.Equal(2, discord.Posts.Count);
        Assert.All(discord.Posts, p => Assert.Equal(new ChannelId(900), p.Channel));
        Assert.StartsWith("Relay bot offline: D1 Broadcaster (Attack 1)", discord.Posts[0].Text);
        Assert.DoesNotContain(HostRig.BotToken, discord.Posts[1].Text);
        Assert.Contains("couldn't be posted", rig.LogText);
    }
}
