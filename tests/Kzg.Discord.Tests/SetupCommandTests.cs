using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Discord.Commands;
using NetCord;

namespace Kzg.Discord.Tests;

/// <summary><c>/gvg setup</c> and <c>/gvg alerts</c>, offline: Discord can't run a slash command for a bot, so the handler is driven directly.</summary>
public sealed class SetupCommandTests
{
    private static readonly UserId Admin = new(1);
    private static readonly ChannelId Alerts = new(900);

    private readonly FakeControl _control = new();
    private readonly GvgCommands _commands;

    public SetupCommandTests() => _commands = new GvgCommands(_control);

    private Task<CommandReply> Gvg(string[] path, params (string Name, string Value)[] options) =>
        _commands.HandleAsync(
            new CommandInvocation("gvg", path, options.ToDictionary(o => o.Name, o => o.Value), new Caller(Admin, (ulong)Permissions.ManageGuild, [], false), FakeControl.Guild),
            CancellationToken.None);

    [Fact]
    public async Task Setup_binds_both_teams_and_puts_a_bot_in_each_channel()
    {
        var reply = await Gvg(["setup"], ("attack-1", "101"), ("attack-2", "102"), ("defense-1", "201"));

        var config = _control.Config;
        Assert.Equal([FakeControl.A1, FakeControl.A2], config.Groups.Attack.Channels);
        Assert.Equal([FakeControl.D1], config.Groups.Defense.Channels);
        Assert.DoesNotContain("caller", reply.Text, StringComparison.OrdinalIgnoreCase);
        // relay1 was in Attack 1 already and stays; the free bots fill the rest in order.
        Assert.Equal(FakeControl.A1, config.Bots.Single(b => b.TokenRef == "relay1").ChannelId);
        Assert.Equal(FakeControl.A2, config.Bots.Single(b => b.TokenRef == "relay2").ChannelId);
        Assert.Equal(FakeControl.D1, config.Bots.Single(b => b.TokenRef == "relay3").ChannelId);
        Assert.Contains("Setup saved", reply.Text);
        Assert.Contains("Attack 2 (A2 Broadcaster)", reply.Text);
        Assert.Contains("Defense 1 (D1 Broadcaster)", reply.Text);
        Assert.Contains("/connect", reply.Text);
        Assert.Contains("optional", reply.Text);
        Assert.Contains("/gvg alerts", reply.Text);
    }

    [Fact]
    public async Task Setup_puts_each_bot_in_the_channel_its_setup_field_named_whatever_the_config_order()
    {
        var control = new FakeControl(new BotConfig
        {
            GuildId = FakeControl.Guild,
            Bots = [new BotEntry { TokenRef = "relay2", Primary = true }, new BotEntry { TokenRef = "relay1" }, new BotEntry { TokenRef = "relay3" }],
        });

        var reply = await new GvgCommands(control).HandleAsync(
            new CommandInvocation("gvg", ["setup"], new Dictionary<string, string> { ["attack-1"] = "101", ["attack-2"] = "102", ["defense-1"] = "201" }, new Caller(Admin, (ulong)Permissions.ManageGuild, [], false), FakeControl.Guild),
            CancellationToken.None);

        Assert.Equal([FakeControl.A2, FakeControl.A1, FakeControl.D1], control.Config.Bots.Select(b => b.ChannelId!.Value));
        Assert.Contains("Attack 1 (A1 Broadcaster), Attack 2 (A2 Broadcaster)", reply.Text);
    }

    [Fact]
    public async Task Setup_again_replaces_the_channels_and_takes_spare_bots_out()
    {
        await Gvg(["setup"], ("attack-1", "101"), ("attack-2", "102"), ("defense-1", "201"));

        var reply = await Gvg(["setup"], ("attack-1", "103"), ("defense-1", "201"));

        var config = _control.Config;
        Assert.Equal([FakeControl.A3], config.Groups.Attack.Channels);
        Assert.Equal(2, config.Bots.Count(b => b.ChannelId is not null));
        Assert.Equal(FakeControl.D1, config.Bots.Single(b => b.TokenRef == "relay3").ChannelId);
        Assert.Contains(config.Bots, b => b.ChannelId == FakeControl.A3);
        Assert.Contains("Not needed for now", reply.Text);
    }

    [Theory]
    [InlineData("101", "101", "is picked twice")]
    [InlineData("101", "", "at least one voice channel")]
    public async Task Setup_refuses_a_channel_twice_or_a_team_with_none(string attack, string defense, string expected)
    {
        var options = new List<(string, string)> { ("attack-1", attack) };
        if (defense.Length > 0)
        {
            options.Add(("defense-1", defense));
        }

        var reply = await Gvg(["setup"], [.. options]);

        Assert.Contains(expected, reply.Text);
        Assert.Equal(0, _control.Edits);
    }

    [Fact]
    public async Task Setup_refuses_more_channels_than_bots_and_says_how_to_add_bots()
    {
        var reply = await Gvg(["setup"], ("attack-1", "101"), ("attack-2", "102"), ("attack-3", "103"), ("defense-1", "201"));

        Assert.Contains("3 relay bots", reply.Text);
        Assert.Contains("--setup", reply.Text);
        Assert.Equal(0, _control.Edits);
    }

    [Fact]
    public async Task Setup_with_an_alerts_channel_saves_it_and_posts_a_test_message()
    {
        var reply = await Gvg(["setup"], ("attack-1", "101"), ("defense-1", "201"), ("channel", "900"));

        Assert.Equal(Alerts, _control.Config.AlertsChannelId);
        Assert.Equal(Alerts, Assert.Single(_control.Posts).Channel);
        Assert.Contains("test message", reply.Text);
        Assert.DoesNotContain("/gvg alerts picks", reply.Text);
    }

    [Fact]
    public async Task Setup_without_a_public_address_says_how_to_set_one()
    {
        await _control.EditAsync(c => new ConfigEdit.Change(c with { PublicHost = null }, ""), default);

        var reply = await Gvg(["setup"], ("attack-1", "101"), ("defense-1", "201"));

        Assert.Contains("/gvg config set publicHost", reply.Text);
    }

    [Fact]
    public async Task Setup_while_stopped_says_how_to_start()
    {
        await _control.SetRunningAsync(false, default);

        var reply = await Gvg(["setup"], ("attack-1", "101"), ("defense-1", "201"));

        Assert.Contains("/gvg start", reply.Text);
    }

    [Fact]
    public async Task Alerts_sets_and_clears_the_channel_and_says_when_it_cannot_post()
    {
        var set = await Gvg(["alerts"], ("channel", "900"));
        Assert.Equal(Alerts, _control.Config.AlertsChannelId);
        Assert.Contains("test message", set.Text);

        _control.RefusePosts = true;
        var refused = await Gvg(["alerts"], ("channel", "900"));
        Assert.Contains("Send Messages", refused.Text);

        var cleared = await Gvg(["alerts"]);
        Assert.Null(_control.Config.AlertsChannelId);
        Assert.Contains("no longer posts", cleared.Text);
        Assert.Contains("alerts = (not set)", (await Gvg(["config", "show"])).Text);
    }

    [Fact]
    public async Task Config_show_names_the_alerts_channel()
    {
        await Gvg(["alerts"], ("channel", "201"));

        Assert.Contains("alerts = #Defense 1", (await Gvg(["config", "show"])).Text);
    }
}
