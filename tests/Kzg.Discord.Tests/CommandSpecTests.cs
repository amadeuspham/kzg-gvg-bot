using Kzg.Core.Config;
using Kzg.Discord.Commands;
using NetCord;
using NetCord.Rest;

namespace Kzg.Discord.Tests;

public sealed class CommandSpecTests
{
    private static BotConfig Config(ulong? adminRole = null) => new()
    {
        AdminRoleId = adminRole,
        Bots = [new BotEntry { TokenRef = "relay1", Primary = true }, new BotEntry { TokenRef = "relay2" }],
    };

    private static SlashCommandProperties Command(IReadOnlyList<ApplicationCommandProperties> all, string name) =>
        Assert.IsType<SlashCommandProperties>(all.Single(c => c.Name == name));

    /// <summary>Every path of sub-command names, with the names of the value options at its end.</summary>
    private static IEnumerable<(string Path, string[] Options)> Leaves(IEnumerable<ApplicationCommandOptionProperties>? options, string prefix = "")
    {
        foreach (var option in options ?? [])
        {
            if (option.Type is ApplicationCommandOptionType.SubCommand)
            {
                yield return ((prefix + option.Name).Trim(), [.. (option.Options ?? []).Select(o => o.Name)]);
            }
            else if (option.Type is ApplicationCommandOptionType.SubCommandGroup)
            {
                foreach (var leaf in Leaves(option.Options, prefix + option.Name + " "))
                {
                    yield return leaf;
                }
            }
        }
    }

    [Fact]
    public void There_are_three_commands_gvg_connect_and_jungle()
    {
        var all = CommandSpec.Build(Config());

        Assert.Equal(["connect", "gvg", "jungle"], all.Select(c => c.Name).Order());
    }

    [Fact]
    public void Jungle_has_the_clock_sub_commands_and_sync_takes_a_required_time_of_four_or_five_characters()
    {
        var jungle = Command(CommandSpec.Build(Config()), "jungle");
        var leaves = Leaves(jungle.Options).ToDictionary(l => l.Path, l => l.Options);

        Assert.Equal(["pause", "resume", "start", "status", "stop", "sync", "test"], leaves.Keys.Order());
        Assert.Equal(["time"], leaves["sync"]);
        Assert.All(leaves.Where(l => l.Key != "sync"), l => Assert.Empty(l.Value));
        var time = jungle.Options!.Single(o => o.Name == "sync").Options!.Single();
        Assert.Equal(ApplicationCommandOptionType.String, time.Type);
        Assert.True(time.Required);
        Assert.Equal(4, time.MinLength);
        Assert.Equal(5, time.MaxLength);
        Assert.InRange(time.Description.Length, 1, 100);
    }

    [Fact]
    public void Jungle_is_for_every_member_even_with_no_admin_role()
    {
        Assert.Null(Command(CommandSpec.Build(Config()), "jungle").DefaultGuildPermissions);
        Assert.Null(Command(CommandSpec.Build(Config(adminRole: 777)), "jungle").DefaultGuildPermissions);
    }

    [Fact]
    public void The_gvg_sub_commands_are_the_plan_s_with_the_options_the_handler_reads()
    {
        var leaves = Leaves(Command(CommandSpec.Build(Config()), "gvg").Options).ToDictionary(l => l.Path, l => l.Options);

        Assert.Equal(
            ["alerts", "bind", "bot assign", "config set", "config show", "revoke", "setup", "start", "status", "stop", "unbind"],
            leaves.Keys.Order());
        Assert.Equal(["group", "channel"], leaves["bind"]);
        Assert.Equal(["channel"], leaves["unbind"]);
        Assert.Equal(["bot", "channel"], leaves["bot assign"]);
        Assert.Equal(["key", "value"], leaves["config set"]);
        Assert.Equal(["user"], leaves["revoke"]);
        Assert.Equal(["attack-1", "defense-1", "attack-2", "attack-3", "defense-2", "defense-3", "channel"], leaves["setup"]);
        Assert.Equal(["channel"], leaves["alerts"]);
    }

    [Fact]
    public void Setup_takes_voice_channels_for_the_teams_and_a_text_channel_for_alerts_with_the_required_ones_first()
    {
        var setup = Command(CommandSpec.Build(Config()), "gvg").Options!.Single(o => o.Name == "setup").Options!.ToList();

        Assert.True(setup.TakeWhile(o => o.Required == true).Count() == 2 && setup.Skip(2).All(o => o.Required != true));
        foreach (var team in setup.Where(o => o.Name.Contains('-')))
        {
            Assert.Equal([ChannelType.VoiceGuildChannel, ChannelType.StageGuildChannel], team.ChannelTypes);
        }
        Assert.Contains(ChannelType.TextGuildChannel, setup.Single(o => o.Name == "channel").ChannelTypes!);
        Assert.DoesNotContain(ChannelType.VoiceGuildChannel, setup.Single(o => o.Name == "channel").ChannelTypes!);
        Assert.DoesNotContain(setup, o => o.Type == ApplicationCommandOptionType.User);
        Assert.All(setup, o => Assert.InRange(o.Description.Length, 1, 100));
    }

    [Fact]
    public void Gvg_is_for_Manage_Server_unless_an_admin_role_is_set_and_connect_is_for_everyone()
    {
        var plain = CommandSpec.Build(Config());
        var roled = CommandSpec.Build(Config(adminRole: 777));

        Assert.Equal(Permissions.ManageGuild, Command(plain, "gvg").DefaultGuildPermissions);
        Assert.Null(Command(roled, "gvg").DefaultGuildPermissions);
        Assert.Null(Command(plain, "connect").DefaultGuildPermissions);
    }

    [Fact]
    public void Every_command_works_in_the_server_only()
    {
        foreach (var command in CommandSpec.Build(Config()))
        {
            Assert.Equal([InteractionContextType.Guild], command.Contexts);
        }
    }

    [Fact]
    public void Channel_options_take_voice_channels_only_and_the_bot_option_offers_the_bots()
    {
        var gvg = Command(CommandSpec.Build(Config()), "gvg");
        var bind = gvg.Options!.Single(o => o.Name == "bind");
        var assign = gvg.Options!.Single(o => o.Name == "bot").Options!.Single(o => o.Name == "assign");

        Assert.Contains(ChannelType.VoiceGuildChannel, bind.Options!.Single(o => o.Name == "channel").ChannelTypes!);
        Assert.DoesNotContain(ChannelType.TextGuildChannel, bind.Options!.Single(o => o.Name == "channel").ChannelTypes!);
        Assert.Equal(["relay1", "relay2"], assign.Options!.Single(o => o.Name == "bot").Choices!.Select(c => c.StringValue));
        Assert.Equal(["relay1 (spare)", "relay2 (spare)"], assign.Options!.Single(o => o.Name == "bot").Choices!.Select(c => c.Name));
        var placed = Config() with { Groups = new GroupsConfig { Defense = new GroupConfig { Channels = [new(201)] } }, Bots = [new BotEntry { TokenRef = "relay1", Primary = true, ChannelId = new(201) }] };
        var named = Command(CommandSpec.Build(placed), "gvg").Options!.Single(o => o.Name == "bot").Options!.Single(o => o.Name == "assign");
        Assert.Equal("D1 Broadcaster", named.Options!.Single(o => o.Name == "bot").Choices!.Single().Name);
        Assert.True(bind.Options!.Single(o => o.Name == "channel").Required);
        Assert.False(assign.Options!.Single(o => o.Name == "channel").Required);
    }

    [Fact]
    public void Every_setting_the_handler_can_change_is_offered_by_name()
    {
        var set = Command(CommandSpec.Build(Config()), "gvg").Options!.Single(o => o.Name == "config").Options!.Single(o => o.Name == "set");

        Assert.Equal(ConfigKeys.All.Select(k => k.Name), set.Options!.Single(o => o.Name == "key").Choices!.Select(c => c.StringValue));
    }

    [Fact]
    public void Names_fit_Discords_limits()
    {
        foreach (var command in CommandSpec.Build(Config()))
        {
            Assert.InRange(command.Name.Length, 1, 32);
            Assert.InRange(((SlashCommandProperties)command).Description.Length, 1, 100);
        }
    }
}
