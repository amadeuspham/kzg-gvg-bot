using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Jungle;
using Kzg.Discord.Commands;
using NetCord;

namespace Kzg.Discord.Tests;

public sealed class GvgCommandsTests
{
    private static readonly UserId Admin = new(1), Player = new(2), Roled = new(3);
    private const ulong AdminRole = 777;

    private readonly FakeControl _control = new();
    private readonly GvgCommands _commands;

    public GvgCommandsTests() => _commands = new GvgCommands(_control);

    private static Caller Manager(UserId? who = null) => new(who ?? Admin, (ulong)Permissions.ManageGuild, [], false);

    private static Caller Plain(UserId? who = null) => new(who ?? Player, 0, [], false);

    private Task<CommandReply> Run(string command, string[] path, Caller caller, params (string Name, string Value)[] options) =>
        _commands.HandleAsync(
            new CommandInvocation(command, path, options.ToDictionary(o => o.Name, o => o.Value), caller, FakeControl.Guild),
            CancellationToken.None);

    private Task<CommandReply> Gvg(string[] path, params (string Name, string Value)[] options) => Run("gvg", path, Manager(), options);

    // ---- /jungle

    [Theory]
    [InlineData("start", JungleCommandKind.Start)]
    [InlineData("pause", JungleCommandKind.Pause)]
    [InlineData("resume", JungleCommandKind.Resume)]
    [InlineData("stop", JungleCommandKind.Stop)]
    [InlineData("status", JungleCommandKind.Status)]
    [InlineData("test", JungleCommandKind.Test)]
    public async Task A_player_without_rights_may_run_every_jungle_sub_command_and_each_maps_to_its_command(string sub, JungleCommandKind kind)
    {
        _control.JungleReply = "the clock says";

        var reply = await Run("jungle", [sub], Plain());

        Assert.Equal("the clock says", reply.Text);
        var (caller, command) = Assert.Single(_control.JungleCalls);
        Assert.Equal(Player, caller);
        Assert.Equal(new JungleCommand(kind), command);
        Assert.Equal(0, _control.Edits);
    }

    [Fact]
    public async Task Jungle_sync_passes_the_parsed_time()
    {
        await Run("jungle", ["sync"], Plain(), ("time", "24:30"));

        Assert.Equal(new JungleCommand(JungleCommandKind.Sync, new TimeSpan(0, 24, 30)), Assert.Single(_control.JungleCalls).Command);
    }

    [Theory]
    [InlineData("36:00")]
    [InlineData("2430")]
    [InlineData("24:6")]
    [InlineData("ab:cd")]
    public async Task A_bad_jungle_sync_time_is_refused_with_the_reason_and_control_is_not_called(string time)
    {
        var reply = await Run("jungle", ["sync"], Plain(), ("time", time));

        Assert.Contains("MM:SS", reply.Text);
        Assert.Empty(_control.JungleCalls);
    }

    [Fact]
    public async Task A_missing_jungle_sync_time_is_refused_and_an_unknown_sub_command_is_not_known()
    {
        var missing = await Run("jungle", ["sync"], Plain());
        var unknown = await Run("jungle", ["dance"], Plain());

        Assert.Contains("MM:SS", missing.Text);
        Assert.Contains("isn't known", unknown.Text);
        Assert.Empty(_control.JungleCalls);
    }

    [Fact]
    public async Task Jungle_still_refuses_another_server_and_bots()
    {
        var elsewhere = await _commands.HandleAsync(new CommandInvocation("jungle", ["start"], new Dictionary<string, string>(), Plain(), 999), default);
        var bot = await _commands.HandleAsync(new CommandInvocation("jungle", ["start"], new Dictionary<string, string>(), new Caller(Player, 0, [], true), FakeControl.Guild), default);

        Assert.Contains("own server", elsewhere.Text);
        Assert.Contains("Bots", bot.Text);
        Assert.Empty(_control.JungleCalls);
    }

    // ---- who may

    [Fact]
    public async Task A_player_without_rights_cannot_run_gvg_and_nothing_changes()
    {
        var reply = await Run("gvg", ["bind"], Plain(), ("group", "attack"), ("channel", "102"));

        Assert.Contains("Manage Server", reply.Text);
        Assert.Equal(0, _control.Edits);
    }

    [Fact]
    public async Task Administrator_and_Manage_Server_may_and_so_may_the_admin_role()
    {
        Assert.Contains("Relay:", (await Run("gvg", ["status"], new Caller(Admin, (ulong)Permissions.Administrator, [], false))).Text);
        Assert.Contains("Relay:", (await Run("gvg", ["status"], Manager())).Text);

        await _control.EditAsync(c => new ConfigEdit.Change(c with { AdminRoleId = AdminRole }, ""), default);
        var viaRole = await Run("gvg", ["status"], new Caller(Roled, 0, [AdminRole], false));
        var otherRole = await Run("gvg", ["status"], new Caller(Roled, 0, [AdminRole + 1], false));

        Assert.Contains("Relay:", viaRole.Text);
        Assert.Contains("Manage Server", otherRole.Text);
    }

    [Fact]
    public async Task A_command_from_another_server_or_from_a_bot_is_refused()
    {
        var elsewhere = await _commands.HandleAsync(new CommandInvocation("gvg", ["status"], new Dictionary<string, string>(), Manager(), 999), default);
        var direct = await _commands.HandleAsync(new CommandInvocation("gvg", ["status"], new Dictionary<string, string>(), Manager(), null), default);
        var bot = await _commands.HandleAsync(new CommandInvocation("gvg", ["status"], new Dictionary<string, string>(), new Caller(Admin, ulong.MaxValue, [], true), FakeControl.Guild), default);

        Assert.Contains("own server", elsewhere.Text);
        Assert.Contains("own server", direct.Text);
        Assert.Contains("Bots", bot.Text);
    }

    [Fact]
    public async Task An_unknown_command_or_sub_command_is_refused()
    {
        Assert.Contains("isn't known", (await Gvg(["frobnicate"])).Text);
        Assert.Contains("isn't known", (await Run("nope", [], Manager())).Text);
    }

    // ---- bind

    [Fact]
    public async Task Bind_adds_the_channel_to_the_group_and_a_free_bot_joins_it()
    {
        var reply = await Gvg(["bind"], ("group", "attack"), ("channel", "102"));

        Assert.Contains("Bound Attack 2 to Attack", reply.Text);
        Assert.Contains("A2 Broadcaster will join it", reply.Text);
        Assert.Equal([FakeControl.A1, FakeControl.A2], _control.Config.Groups.Attack.Channels);
        Assert.Equal(FakeControl.A2, _control.Config.Bots.Single(b => b.TokenRef == "relay2").ChannelId);
    }

    [Fact]
    public async Task Bind_with_no_free_bot_says_so_and_still_binds()
    {
        await Gvg(["bind"], ("group", "attack"), ("channel", "102"));
        await Gvg(["bind"], ("group", "attack"), ("channel", "103"));

        var reply = await Gvg(["bind"], ("group", "defense"), ("channel", "201"));

        Assert.Contains("No relay bot is free", reply.Text);
        Assert.Equal([FakeControl.D1], _control.Config.Groups.Defense.Channels);
    }

    [Fact]
    public async Task Bind_does_not_move_a_channel_between_groups_or_bind_it_twice()
    {
        var other = await Gvg(["bind"], ("group", "defense"), ("channel", "101"));
        var twice = await Gvg(["bind"], ("group", "attack"), ("channel", "101"));

        Assert.Contains("bound to Attack", other.Text);
        Assert.Contains("Unbind it first", other.Text);
        Assert.Contains("already", twice.Text);
        Assert.Empty(_control.Config.Groups.Defense.Channels);
    }

    [Fact]
    public async Task Bind_leaves_a_bot_that_is_in_the_channel_already()
    {
        await _control.EditAsync(c => new ConfigEdit.Change(c with { Bots = [.. c.Bots.Select(b => b.TokenRef == "relay3" ? b with { ChannelId = FakeControl.A3 } : b)] }, ""), default);

        var reply = await Gvg(["bind"], ("group", "attack"), ("channel", "103"));

        Assert.Contains("A2 Broadcaster is there already", reply.Text);
    }

    [Theory]
    [InlineData("middle", "101")]
    [InlineData("attack", "not-an-id")]
    [InlineData("attack", "0")]
    public async Task Bind_with_a_bad_group_or_channel_is_refused(string group, string channel)
    {
        var reply = await Gvg(["bind"], ("group", group), ("channel", channel));

        Assert.Contains("Give a group", reply.Text);
        Assert.Equal(0, _control.Edits);
    }

    // ---- unbind

    [Fact]
    public async Task Unbind_removes_the_channel_and_its_bot_leaves()
    {
        var reply = await Gvg(["unbind"], ("channel", "101"));

        Assert.Contains("Unbound Attack 1 from Attack", reply.Text);
        Assert.Contains("A1 Broadcaster left it", reply.Text);
        Assert.Empty(_control.Config.Groups.Attack.Channels);
        Assert.Null(_control.Config.Bots.Single(b => b.TokenRef == "relay1").ChannelId);
    }

    [Fact]
    public async Task Unbind_says_which_bots_the_channels_after_it_rename()
    {
        await Gvg(["bind"], ("group", "attack"), ("channel", "102"));

        var reply = await Gvg(["unbind"], ("channel", "101"));

        Assert.Contains("A1 Broadcaster left it. A2 Broadcaster is A1 Broadcaster now.", reply.Text);
    }

    [Fact]
    public async Task Unbind_of_a_channel_nobody_uses_is_refused_and_one_with_only_a_bot_frees_the_bot()
    {
        var nothing = await Gvg(["unbind"], ("channel", "202"));
        await Gvg(["bot", "assign"], ("bot", "relay2"), ("channel", "202"));
        var onlyBot = await Gvg(["unbind"], ("channel", "202"));

        Assert.Contains("isn't bound and has no relay bot", nothing.Text);
        Assert.Contains("wasn't bound", onlyBot.Text);
        Assert.Null(_control.Config.Bots.Single(b => b.TokenRef == "relay2").ChannelId);
    }

    [Fact]
    public async Task Unbind_without_a_channel_is_refused()
    {
        Assert.Contains("Give a voice channel", (await Gvg(["unbind"])).Text);
    }

    // ---- bot assign

    [Fact]
    public async Task Assign_puts_a_bot_in_a_channel_and_warns_when_the_channel_is_not_bound()
    {
        var reply = await Gvg(["bot", "assign"], ("bot", "relay2"), ("channel", "202"));

        Assert.Contains("relay2 (spare) joins Defense 2", reply.Text);
        Assert.Contains("isn't bound", reply.Text);
        Assert.Equal(new ChannelId(202), _control.Config.Bots.Single(b => b.TokenRef == "relay2").ChannelId);
    }

    [Fact]
    public async Task Assign_without_a_channel_takes_the_bot_out()
    {
        var reply = await Gvg(["bot", "assign"], ("bot", "relay1"));

        Assert.Contains("A1 Broadcaster leaves its channel", reply.Text);
        Assert.Null(_control.Config.Bots.Single(b => b.TokenRef == "relay1").ChannelId);
    }

    [Fact]
    public async Task Assign_into_a_team_channel_says_the_name_the_bot_takes()
    {
        await Gvg(["bot", "assign"], ("bot", "relay1"));

        var reply = await Gvg(["bot", "assign"], ("bot", "relay3"), ("channel", "101"));

        Assert.Contains("relay3 (spare) joins Attack 1 as A1 Broadcaster.", reply.Text);
    }

    [Fact]
    public async Task Assign_refuses_an_unknown_bot_and_a_channel_that_has_a_bot()
    {
        var unknown = await Gvg(["bot", "assign"], ("bot", "relay9"), ("channel", "202"));
        var taken = await Gvg(["bot", "assign"], ("bot", "relay2"), ("channel", "101"));
        var moveSelf = await Gvg(["bot", "assign"], ("bot", "relay1"), ("channel", "101"));

        Assert.Contains("No relay bot is called 'relay9'. They are: A1 Broadcaster (relay1), relay2 (spare), relay3 (spare).", unknown.Text);
        Assert.Contains("A1 Broadcaster is in Attack 1 already", taken.Text);
        Assert.Contains("A1 Broadcaster joins Attack 1.", moveSelf.Text);
    }

    [Fact]
    public async Task Assign_with_a_channel_that_is_not_an_id_is_refused()
    {
        var reply = await Gvg(["bot", "assign"], ("bot", "relay2"), ("channel", "x"));

        Assert.Contains("isn't a voice channel", reply.Text);
    }

    [Fact]
    public async Task There_is_no_caller_command_any_more()
    {
        Assert.Contains("isn't known", (await Gvg(["caller", "set"], ("group", "attack"), ("user", "42"))).Text);
        Assert.Contains("isn't known", (await Gvg(["caller", "clear"], ("group", "attack"))).Text);
    }

    // ---- config

    [Fact]
    public async Task Config_show_lists_the_settings_the_groups_and_the_bots_without_any_secret()
    {
        var reply = await Gvg(["config", "show"]);

        Assert.Contains("publicHost = gvg.example.com", reply.Text);
        Assert.Contains("prerollMs = 300", reply.Text);
        Assert.Contains("jungleVolume = 32", reply.Text);
        Assert.Contains("attack: Attack 1", reply.Text);
        Assert.Contains("relay1* (A1 Broadcaster)@Attack 1", reply.Text);
        Assert.Contains("relay2@-", reply.Text);
    }

    [Fact]
    public async Task Jungle_volume_is_set_in_range_and_refused_outside_it_or_as_text()
    {
        var set = await Gvg(["config", "set"], ("key", "jungleVolume"), ("value", "60"));
        Assert.Equal(60, _control.Config.JungleVolume);
        Assert.Contains("jungleVolume is now", set.Text);

        foreach (var bad in new[] { "0", "101", "loud" })
        {
            var refused = await Gvg(["config", "set"], ("key", "jungleVolume"), ("value", bad));
            Assert.Contains("jungleVolume", refused.Text);
            Assert.Equal(60, _control.Config.JungleVolume);
        }
    }

    [Fact]
    public async Task Config_show_is_redacted_on_its_way_out()
    {
        await _control.EditAsync(c => new ConfigEdit.Change(c with { PublicHost = "t1.example.com" }, ""), default);

        var reply = await Gvg(["config", "show"]);

        Assert.DoesNotContain("t1.example.com", reply.Text);
        Assert.Contains("[redacted]", reply.Text);
    }

    [Theory]
    [InlineData("prerollMs", "200", false)]
    [InlineData("maxHoldMs", "60000", false)]
    [InlineData("oauth.enabled", "off", false)]
    [InlineData("deviceTokenDays", "30", true)]
    [InlineData("jungleVolume", "60", false)]
    [InlineData("publicHost", "other.example.org", false)]
    [InlineData("adminRoleId", "123456789", false)]
    [InlineData("oauth.clientId", "98765", false)]
    public async Task Config_set_changes_a_setting_and_says_whether_a_restart_is_needed(string key, string value, bool restart)
    {
        var reply = await Gvg(["config", "set"], ("key", key), ("value", value));

        Assert.Contains($"{key} is now", reply.Text);
        Assert.Equal(restart, reply.Text.Contains("after the bot restarts", StringComparison.Ordinal));
        Assert.Equal(1, _control.Edits);
    }

    [Fact]
    public async Task Config_set_can_clear_an_optional_setting()
    {
        var reply = await Gvg(["config", "set"], ("key", "publicHost"), ("value", "none"));

        Assert.Contains("publicHost is now (not set)", reply.Text);
        Assert.Null(_control.Config.PublicHost);
    }

    [Theory]
    [InlineData("prerollMs", "fast")]
    [InlineData("prerollMs", "-5")]
    [InlineData("oauth.enabled", "maybe")]
    [InlineData("adminRoleId", "role")]
    [InlineData("publicHost", "   ")]
    public async Task Config_set_with_a_value_that_does_not_parse_changes_nothing(string key, string value)
    {
        var before = _control.Config;

        var reply = await Gvg(["config", "set"], ("key", key), ("value", value));

        Assert.Equal(0, _control.Edits);
        Assert.Same(before, _control.Config);
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
    }

    [Fact]
    public async Task Config_set_with_a_value_that_breaks_a_rule_is_refused_with_the_rule()
    {
        var reply = await Gvg(["config", "set"], ("key", "prerollMs"), ("value", "70"));

        Assert.Contains("break the config", reply.Text);
        Assert.Contains("prerollMs", reply.Text);
        Assert.Equal(300, _control.Config.PrerollMs);
    }

    [Fact]
    public async Task Config_set_for_a_setting_that_is_not_listed_names_the_ones_that_are()
    {
        var reply = await Gvg(["config", "set"], ("key", "listen"), ("value", "0.0.0.0:80"));

        Assert.Contains("can't be changed here", reply.Text);
        Assert.Contains("prerollMs", reply.Text);
        Assert.Equal("127.0.0.1:7878", _control.Config.Listen);
    }

    // ---- start, stop, status, revoke

    [Fact]
    public async Task Stop_and_start_flip_the_relay_and_say_when_it_is_so_already()
    {
        Assert.Contains("running already", (await Gvg(["start"])).Text);
        Assert.Contains("left their channels", (await Gvg(["stop"])).Text);
        Assert.False(_control.Running);
        Assert.Contains("stopped already", (await Gvg(["stop"])).Text);
        Assert.Contains("joining", (await Gvg(["start"])).Text);
        Assert.True(_control.Running);
    }

    [Fact]
    public async Task Status_shows_each_group_with_its_channels_and_bots()
    {
        _control.StatusToShow = new GvgStatus(
            true,
            [
                new GroupStatus(Group.Attack, [new ChannelStatus(FakeControl.A1, "Attack 1", "A1 Broadcaster", BotState.Ready), new ChannelStatus(FakeControl.A2, "Attack 2", "A2 Broadcaster", BotState.Joining, NicknameRefused: true), new ChannelStatus(FakeControl.A3, "Attack 3", null, BotState.Idle)]),
                new GroupStatus(Group.Defense, []),
            ],
            [new BotStatus("relay1", "A1 Broadcaster", "Attack 1", BotState.Ready), new BotStatus("relay9", "relay9 (spare)", null, BotState.Idle)],
            3,
            5,
            1234,
            7);

        var text = (await Gvg(["status"])).Text;

        Assert.Contains("Relay: running", text);
        Assert.Contains("Attack\n", text);
        Assert.DoesNotContain("caller", text, StringComparison.Ordinal);
        Assert.Contains("Attack 1: A1 Broadcaster ready\n", text);
        Assert.Contains("Attack 2: A2 Broadcaster not in yet (it can't set its nickname: give it the Change Nickname permission)", text);
        Assert.Contains("Attack 3: no bot", text);
        Assert.Contains("Defense\n", text);
        Assert.Contains("no channels bound", text);
        Assert.Contains("Free bots: relay9 (spare)", text);
        Assert.Contains("Apps connected: 3; linked devices: 5", text);
        Assert.Contains("Frames relayed: 1234; packets dropped: 7", text);
    }

    [Fact]
    public async Task Revoke_unlinks_the_player_and_says_how_many()
    {
        var reply = await Gvg(["revoke"], ("user", "42"));

        Assert.Equal([new UserId(42)], _control.Revoked);
        Assert.Contains("Revoked 2 linked app(s) for <@42>", reply.Text);
    }

    [Fact]
    public async Task Revoke_for_a_player_with_no_app_or_no_player_says_so()
    {
        _control.RevokeCount = 0;

        Assert.Contains("no linked app", (await Gvg(["revoke"], ("user", "42"))).Text);
        Assert.Contains("Give the player", (await Gvg(["revoke"], ("user", "x"))).Text);
    }

    // ---- connect

    [Fact]
    public async Task Connect_gives_any_member_a_code_for_the_public_host()
    {
        var reply = await Run("connect", [], Plain(new UserId(42)));

        Assert.Contains("KZG1.fake.code", reply.Text);
        Assert.Equal([(new UserId(42), "gvg.example.com")], _control.Issued);
    }

    [Fact]
    public async Task Connect_without_a_public_host_says_how_to_set_one_and_issues_nothing()
    {
        await _control.EditAsync(c => new ConfigEdit.Change(c with { PublicHost = null }, ""), default);

        var reply = await Run("connect", [], Plain());

        Assert.Contains("/gvg config set publicHost", reply.Text);
        Assert.Empty(_control.Issued);
    }

    // ---- reply

    [Fact]
    public void A_reply_never_goes_past_Discords_limit()
    {
        var reply = CommandReply.Of(new string('x', 5000));

        Assert.Equal(CommandReply.MaxLength, reply.Text.Length);
        Assert.EndsWith("…", reply.Text);
    }
}
