using Kzg.Core.Config;
using NetCord;
using NetCord.Rest;

namespace Kzg.Discord.Commands;

/// <summary>
/// The slash commands as Discord is told about them: <c>/gvg</c>, <c>/connect</c> and <c>/jungle</c>, in the one guild. The
/// shape matches what <see cref="GvgCommands"/> reads, and a test holds the two together.
/// </summary>
public static class CommandSpec
{
    public const string Gvg = "gvg";
    public const string Connect = "connect";
    public const string Jungle = "jungle";
    public const string JungleTimeOption = "time";

    /// <summary>How many voice channels <c>/gvg setup</c> takes per team: the guild's three small teams.</summary>
    public const int SetupChannelsPerTeam = RelayNames.ChannelsPerTeam;

    public const string AlertsOptionName = "channel";

    /// <summary>The <c>/gvg setup</c> option for a team's n-th channel, from 1, such as <c>attack-2</c>.</summary>
    public static string TeamOption(string team, int number) => $"{team}-{number}";

    private static string Title(string team) => team == "attack" ? "Attack" : "Defense";

    /// <summary>The commands to register. <c>/gvg</c> is hidden from players without Manage Server, unless an admin role is set: then everyone sees it and the handler decides.</summary>
    public static IReadOnlyList<ApplicationCommandProperties> Build(BotConfig config)
    {
        var groupChoices = new[] { new ApplicationCommandOptionChoiceProperties("Attack", "attack"), new ApplicationCommandOptionChoiceProperties("Defense", "defense") };
        var voice = new[] { ChannelType.VoiceGuildChannel, ChannelType.StageGuildChannel };
        var botChoices = config.Bots.Take(25).Select(b => new ApplicationCommandOptionChoiceProperties(RelayNames.Of(config, b), b.TokenRef)).ToArray();
        var keyChoices = ConfigKeys.All.Take(25).Select(k => new ApplicationCommandOptionChoiceProperties(k.Name, k.Name)).ToArray();

        ApplicationCommandOptionProperties Sub(string name, string description, params ApplicationCommandOptionProperties[] options) =>
            new(ApplicationCommandOptionType.SubCommand, name, description) { Options = options };

        ApplicationCommandOptionProperties Group(string name, string description, params ApplicationCommandOptionProperties[] subs) =>
            new(ApplicationCommandOptionType.SubCommandGroup, name, description) { Options = subs };

        ApplicationCommandOptionProperties GroupOption() =>
            new(ApplicationCommandOptionType.String, "group", "Attack or Defense") { Required = true, Choices = groupChoices };

        ApplicationCommandOptionProperties ChannelOption(string description, bool required = true) =>
            new(ApplicationCommandOptionType.Channel, "channel", description) { Required = required, ChannelTypes = voice };

        var botOption = new ApplicationCommandOptionProperties(ApplicationCommandOptionType.String, "bot", "The relay bot, by the team channel it is in now")
        {
            Required = true,
            Choices = botChoices.Length > 0 ? botChoices : null,
        };

        ApplicationCommandOptionProperties UserOption() => new(ApplicationCommandOptionType.User, "user", "The player") { Required = true };

        var text = new[] { ChannelType.TextGuildChannel, ChannelType.AnnouncementGuildChannel };
        ApplicationCommandOptionProperties AlertsOption(string description) =>
            new(ApplicationCommandOptionType.Channel, AlertsOptionName, description) { Required = false, ChannelTypes = text };

        // Discord lists the required options first, so each team's first channel comes before the rest.
        List<ApplicationCommandOptionProperties> setup =
        [
            new(ApplicationCommandOptionType.Channel, TeamOption("attack", 1), "An Attack voice channel") { Required = true, ChannelTypes = voice },
            new(ApplicationCommandOptionType.Channel, TeamOption("defense", 1), "A Defense voice channel") { Required = true, ChannelTypes = voice },
        ];
        foreach (var team in new[] { "attack", "defense" })
        {
            for (var n = 2; n <= SetupChannelsPerTeam; n++)
            {
                setup.Add(new(ApplicationCommandOptionType.Channel, TeamOption(team, n), $"Another {Title(team)} voice channel") { Required = false, ChannelTypes = voice });
            }
        }
        setup.Add(AlertsOption("A text channel where the bot posts problems, such as a relay bot going offline"));

        var gvg = new SlashCommandProperties(Gvg, "Run the guild-war relay")
        {
            Options =
            [
                Sub("setup", "Pick the team voice channels; a relay bot joins each channel", [.. setup]),
                Sub("alerts", "Pick the text channel for problem reports, or leave it out to stop them", AlertsOption("Where problems are posted")),
                Sub("bind", "Bind a voice channel to a team; a free relay bot joins it", GroupOption(), ChannelOption("The team's voice channel")),
                Sub("unbind", "Unbind a voice channel; its relay bot leaves", ChannelOption("The voice channel")),
                Group("bot", "Relay bots",
                    Sub("assign", "Put a relay bot into a voice channel, or take it out", botOption, ChannelOption("Where it goes; leave out to take it out of voice", required: false))),
                Group("config", "The bot's settings",
                    Sub("show", "Show the settings"),
                    Sub("set", "Change a setting",
                        new ApplicationCommandOptionProperties(ApplicationCommandOptionType.String, "key", "The setting") { Required = true, Choices = keyChoices },
                        new ApplicationCommandOptionProperties(ApplicationCommandOptionType.String, "value", "The new value, or none to clear it") { Required = true, MaxLength = 100 })),
                Sub("start", "Put the relay bots into their channels"),
                Sub("stop", "Take the relay bots out of voice"),
                Sub("status", "Show the bots and channels"),
                Sub("revoke", "Unlink a player's apps", UserOption()),
            ],
        }
            .WithContexts([InteractionContextType.Guild])
            .WithDefaultGuildPermissions(config.AdminRoleId is null ? Permissions.ManageGuild : null);

        var connect = new SlashCommandProperties(Connect, "Get a code that links the Kzg app to your Discord account")
            .WithContexts([InteractionContextType.Guild]);

        // Any member may run it: the calls help the whole team, and the clock changes nothing else.
        var jungle = new SlashCommandProperties(Jungle, "Jungle spawn calls on a match clock")
        {
            Options =
            [
                Sub("start", "Start the clock at 35:00"),
                Sub("sync", "Set the clock to the in-game time",
                    new ApplicationCommandOptionProperties(ApplicationCommandOptionType.String, JungleTimeOption, "The in-game time as MM:SS, such as 24:30")
                    {
                        Required = true,
                        MinLength = 4,
                        MaxLength = 5,
                    }),
                Sub("pause", "Pause the clock"),
                Sub("resume", "Resume the clock"),
                Sub("stop", "Stop the clock"),
                Sub("status", "Show the clock and the next call"),
                Sub("test", "Play a test call in the team channels"),
            ],
        }
            .WithContexts([InteractionContextType.Guild]);

        return [gvg, connect, jungle];
    }
}
