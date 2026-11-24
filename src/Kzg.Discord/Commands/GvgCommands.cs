using System.Globalization;
using System.Text;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Jungle;
using NetCord;

namespace Kzg.Discord.Commands;

/// <summary>
/// What each slash command does. It sees a command as a <see cref="CommandInvocation"/> and answers with
/// a <see cref="CommandReply"/>, so none of it needs Discord. Rights are checked here, on the server, for
/// every call: the permission Discord attaches to a command only hides it, and a guild's admins can lift that.
/// </summary>
public sealed class GvgCommands(IGvgControl control)
{
    private const ulong AdminBits = (ulong)(Permissions.Administrator | Permissions.ManageGuild);

    public async Task<CommandReply> HandleAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        var config = control.Config;
        if (call.GuildId is not { } guild || config.GuildId is { } own && own != guild)
        {
            return CommandReply.Of("This bot only takes commands in its own server.");
        }
        if (call.Caller.IsBot)
        {
            return CommandReply.Of("Bots can't run these commands.");
        }
        if (call.Command == "connect")
        {
            return Connect(call, config);
        }
        if (call.Command == CommandSpec.Jungle)
        {
            return Jungle(call);
        }
        if (call.Command != "gvg")
        {
            return CommandReply.Of("That command isn't known.");
        }
        if (!IsAdmin(call.Caller, config))
        {
            return CommandReply.Of("You need the Manage Server permission, or the bot's admin role, to use /gvg.");
        }
        return call.Path switch
        {
            ["setup"] => await SetupAsync(call, cancellationToken).ConfigureAwait(false),
            ["alerts"] => await AlertsAsync(call, cancellationToken).ConfigureAwait(false),
            ["bind"] => await BindAsync(call, cancellationToken).ConfigureAwait(false),
            ["unbind"] => await UnbindAsync(call, cancellationToken).ConfigureAwait(false),
            ["bot", "assign"] => await AssignAsync(call, cancellationToken).ConfigureAwait(false),
            ["config", "show"] => ShowConfig(config),
            ["config", "set"] => await SetConfigAsync(call, cancellationToken).ConfigureAwait(false),
            ["start"] => await SetRunningAsync(true, cancellationToken).ConfigureAwait(false),
            ["stop"] => await SetRunningAsync(false, cancellationToken).ConfigureAwait(false),
            ["status"] => ShowStatus(),
            ["revoke", "user"] or ["revoke"] => Revoke(call),
            _ => CommandReply.Of("That command isn't known."),
        };
    }

    /// <summary>Manage Server or Administrator, or the configured admin role.</summary>
    public static bool IsAdmin(Caller caller, BotConfig config) =>
        (caller.Permissions & AdminBits) != 0 || config.AdminRoleId is { } role && caller.Roles.Contains(role);

    // ---- /connect

    private CommandReply Connect(CommandInvocation call, BotConfig config)
    {
        if (config.PublicHost is not { } host)
        {
            return CommandReply.Of("The bot has no public address yet. Ask an admin to set one with /gvg config set publicHost.");
        }
        var code = control.IssueConnectCode(call.Caller.User, host);
        return CommandReply.Of($"Paste this code into the app. It works once, for 10 minutes. Keep it to yourself.\n`{code}`");
    }

    // ---- /jungle

    /// <summary>Open to every member, unlike /gvg: the clock holds no setting, and a wrong sync is fixed by another.</summary>
    private CommandReply Jungle(CommandInvocation call)
    {
        JungleCommand command;
        switch (call.Path)
        {
            case ["sync"]:
                if (!JungleTime.TryParse(call.Option(CommandSpec.JungleTimeOption), out var time, out var error))
                {
                    return CommandReply.Of(error);
                }
                command = new JungleCommand(JungleCommandKind.Sync, time);
                break;
            case [var name] when Enum.TryParse<JungleCommandKind>(name, ignoreCase: true, out var kind) && kind is not JungleCommandKind.Sync:
                command = new JungleCommand(kind);
                break;
            default:
                return CommandReply.Of("That command isn't known.");
        }
        return CommandReply.Of(control.Jungle(call.Caller.User, command));
    }

    // ---- setup, alerts

    /// <summary>The teams in one go: these channels and no others, and a relay bot in each.</summary>
    private async Task<CommandReply> SetupAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        var picked = new Dictionary<Group, List<ChannelId>> { [Group.Attack] = [], [Group.Defense] = [] };
        foreach (var (group, team) in new[] { (Group.Attack, "attack"), (Group.Defense, "defense") })
        {
            for (var n = 1; n <= CommandSpec.SetupChannelsPerTeam; n++)
            {
                if (call.Option(CommandSpec.TeamOption(team, n)) is not { } text)
                {
                    continue;
                }
                if (!ChannelId.TryParse(text, out var channel))
                {
                    return CommandReply.Of("Pick voice channels for the teams.");
                }
                picked[group].Add(channel);
            }
        }
        if (picked[Group.Attack].Count == 0 || picked[Group.Defense].Count == 0)
        {
            return CommandReply.Of("Pick at least one voice channel for Attack and one for Defense.");
        }
        var all = picked[Group.Attack].Concat(picked[Group.Defense]).ToList();
        if (all.GroupBy(c => c).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return CommandReply.Of($"{Name(twice.Key)} is picked twice. Each team channel belongs to one team, once.");
        }
        ChannelId? alerts = null;
        if (call.Option(CommandSpec.AlertsOptionName) is { } alertText)
        {
            if (!ChannelId.TryParse(alertText, out var alertChannel))
            {
                return CommandReply.Of("Pick a text channel for the alerts.");
            }
            alerts = alertChannel;
        }
        var outcome = await control.EditAsync(config =>
        {
            var groups = new GroupsConfig
            {
                Attack = new GroupConfig { Channels = picked[Group.Attack] },
                Defense = new GroupConfig { Channels = picked[Group.Defense] },
            };
            if (SetupPlans.AssignChannels(config.Bots, groups) is not { } bots)
            {
                return new ConfigEdit.Refuse(
                    $"You picked {all.Count} channels, but there are {config.Bots.Count} relay bots: one bot sits in each channel. "
                    + "Pick fewer channels, or add relay bots on the bot's setup page (start it with --setup).");
            }
            var changed = config with { Groups = groups, Bots = bots, AlertsChannelId = alerts ?? config.AlertsChannelId };
            return new ConfigEdit.Change(changed, SetupSummary(changed));
        }, cancellationToken).ConfigureAwait(false);
        if (!outcome.Done)
        {
            return CommandReply.Of(outcome.Message);
        }
        var reply = new StringBuilder(outcome.Message);
        if (alerts is { } posted)
        {
            reply.Append('\n').Append(await TestAlertsAsync(posted, cancellationToken).ConfigureAwait(false));
        }
        return CommandReply.Of(reply.ToString());
    }

    private string SetupSummary(BotConfig config)
    {
        var text = new StringBuilder("Setup saved.\n");
        foreach (var group in new[] { Group.Attack, Group.Defense })
        {
            var g = config.Groups[group];
            text.Append("**").Append(Capital(group)).Append("**: ")
                .Append(string.Join(", ", g.Channels.Select(c => $"{Name(c)} ({RelayNames.Of(config, config.Bots.First(b => b.ChannelId == c))})")))
                .Append(".\n");
        }
        if (config.Bots.Where(b => b.ChannelId is null).Select(b => RelayNames.Of(config, b)).ToArray() is { Length: > 0 } spare)
        {
            text.Append("Not needed for now: ").Append(string.Join(", ", spare)).Append(".\n");
        }
        if (!control.Running)
        {
            text.Append("The relay is stopped: /gvg start puts the bots in their channels.\n");
        }
        if (config.AlertsChannelId is null)
        {
            text.Append("Tip: /gvg alerts picks a text channel where the bot reports problems, such as a relay bot going offline.\n");
        }
        text.Append(config.PublicHost is null
            ? "Before players can link their app, set the bot's address: /gvg config set publicHost gvg.example.com (your own)."
            : "Players: run /connect here in Discord and paste the code into the Kzg app. Discord sign-in is optional; codes always work.");
        return text.ToString();
    }

    private async Task<CommandReply> AlertsAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        ChannelId? channel = null;
        if (call.Option(CommandSpec.AlertsOptionName) is { } text)
        {
            if (!ChannelId.TryParse(text, out var parsed))
            {
                return CommandReply.Of("Pick a text channel.");
            }
            channel = parsed;
        }
        var outcome = await control.EditAsync(config => new ConfigEdit.Change(
            config with { AlertsChannelId = channel },
            channel is { } c ? $"Problems are posted in <#{c}> from now on." : "The bot no longer posts problems to a channel."), cancellationToken).ConfigureAwait(false);
        if (!outcome.Done || channel is not { } target)
        {
            return CommandReply.Of(outcome.Message);
        }
        return CommandReply.Of(outcome.Message + "\n" + await TestAlertsAsync(target, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Posts a first message to the alerts channel, so a missing permission shows now rather than in a war.</summary>
    private async Task<string> TestAlertsAsync(ChannelId channel, CancellationToken cancellationToken) =>
        await control.PostAsync(channel, "The Kzg bot will post problems here, such as a relay bot going offline.", cancellationToken).ConfigureAwait(false)
            ? $"A test message is in <#{channel}>."
            : $"The bot couldn't post in <#{channel}>. Give the first relay bot the View Channel and Send Messages permissions there.";

    // ---- bind, unbind, bot assign

    private Task<CommandReply> BindAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        if (!TryGroup(call.Option("group"), out var group) || !ChannelId.TryParse(call.Option("channel"), out var channel))
        {
            return Task.FromResult(CommandReply.Of("Give a group (attack or defense) and a voice channel."));
        }
        return EditAsync(config =>
        {
            if (config.Groups[group].Channels.Contains(channel))
            {
                return new ConfigEdit.Refuse($"{Name(channel)} is bound to {group} already.");
            }
            foreach (var other in new[] { Group.Attack, Group.Defense })
            {
                if (config.Groups[other].Channels.Contains(channel))
                {
                    return new ConfigEdit.Refuse($"{Name(channel)} is bound to {other}. Unbind it first.");
                }
            }
            var groups = config.Groups.With(group, config.Groups[group] with { Channels = [.. config.Groups[group].Channels, channel] });
            var bots = config.Bots;
            string bot;
            if (bots.FirstOrDefault(b => b.ChannelId == channel) is { } there)
            {
                bot = $"{RelayNames.Of(config with { Groups = groups }, there)} is there already.";
            }
            else if (bots.FirstOrDefault(b => b.ChannelId is null) is { } free)
            {
                var joined = free with { ChannelId = channel };
                bots = [.. bots.Select(b => b == free ? joined : b)];
                bot = $"{RelayNames.Of(config with { Groups = groups }, joined)} will join it.";
            }
            else
            {
                bot = "No relay bot is free: free one with /gvg bot assign, then assign it here.";
            }
            return new ConfigEdit.Change(config with { Groups = groups, Bots = bots }, $"Bound {Name(channel)} to {group}. {bot}");
        }, cancellationToken);
    }

    private Task<CommandReply> UnbindAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        if (!ChannelId.TryParse(call.Option("channel"), out var channel))
        {
            return Task.FromResult(CommandReply.Of("Give a voice channel."));
        }
        return EditAsync(config =>
        {
            var group = new[] { Group.Attack, Group.Defense }.Cast<Group?>().FirstOrDefault(g => config.Groups[g!.Value].Channels.Contains(channel));
            var bot = config.Bots.FirstOrDefault(b => b.ChannelId == channel);
            if (group is null && bot is null)
            {
                return new ConfigEdit.Refuse($"{Name(channel)} isn't bound and has no relay bot.");
            }
            var groups = config.Groups;
            if (group is { } owner)
            {
                groups = groups.With(owner, groups[owner] with { Channels = [.. groups[owner].Channels.Where(c => c != channel)] });
            }
            var bots = bot is null ? config.Bots : [.. config.Bots.Select(b => b == bot ? b with { ChannelId = null } : b)];
            var said = group is { } was ? $"Unbound {Name(channel)} from {was}." : $"{Name(channel)} wasn't bound.";
            var changed = config with { Groups = groups, Bots = bots };
            return new ConfigEdit.Change(changed, said + (bot is null ? "" : $" {RelayNames.Of(config, bot)} left it.") + Renamed(config, changed));
        }, cancellationToken);
    }

    private Task<CommandReply> AssignAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        var name = call.Option("bot");
        var hasChannel = call.Option("channel") is not null;
        if (!ChannelId.TryParse(call.Option("channel"), out var channel) && hasChannel)
        {
            return Task.FromResult(CommandReply.Of("That isn't a voice channel."));
        }
        return EditAsync(config =>
        {
            var bot = config.Bots.FirstOrDefault(b => string.Equals(b.TokenRef, name, StringComparison.Ordinal));
            if (bot is null)
            {
                return new ConfigEdit.Refuse($"No relay bot is called '{name}'. They are: {string.Join(", ", config.Bots.Select(b => RelayNames.Team(config, b) is { } team ? $"{team} ({b.TokenRef})" : RelayNames.Of(config, b)))}.");
            }
            if (!hasChannel)
            {
                return new ConfigEdit.Change(
                    config with { Bots = [.. config.Bots.Select(b => b == bot ? b with { ChannelId = null } : b)] },
                    $"{RelayNames.Of(config, bot)} leaves its channel.");
            }
            if (config.Bots.FirstOrDefault(b => b.ChannelId == channel && b != bot) is { } there)
            {
                return new ConfigEdit.Refuse($"{RelayNames.Of(config, there)} is in {Name(channel)} already. A channel has one bot.");
            }
            var moved = bot with { ChannelId = channel };
            var changed = config with { Bots = [.. config.Bots.Select(b => b == bot ? moved : b)] };
            var was = RelayNames.Of(config, bot);
            var said = $"{was} joins {Name(channel)}";
            return new ConfigEdit.Change(changed, RelayNames.Team(changed, moved) is not { } now
                ? $"{said}. That channel isn't bound to a group yet: use /gvg bind."
                : now == was ? $"{said}." : $"{said} as {now}.");
        }, cancellationToken);
    }

    // ---- config

    private CommandReply ShowConfig(BotConfig config)
    {
        var text = new StringBuilder("```\n");
        foreach (var key in ConfigKeys.All)
        {
            text.Append(key.Name).Append(" = ").Append(key.Get(config)).Append('\n');
        }
        text.Append("guildId = ").Append(config.GuildId?.ToString(CultureInfo.InvariantCulture) ?? "(not set)").Append('\n');
        text.Append("listen = ").Append(config.Listen).Append('\n');
        text.Append("alerts = ").Append(config.AlertsChannelId is { } alerts ? "#" + Name(alerts) : "(not set)").Append('\n');
        foreach (var group in new[] { Group.Attack, Group.Defense })
        {
            var g = config.Groups[group];
            text.Append(group.ToString().ToLowerInvariant()).Append(": ")
                .Append(g.Channels.Count == 0 ? "no channels" : string.Join(", ", g.Channels.Select(Name)))
                .Append('\n');
        }
        text.Append("bots: ").Append(string.Join(", ", config.Bots.Select(b =>
            $"{b.TokenRef}{(b.Primary ? "*" : "")}{(RelayNames.Team(config, b) is { } team ? $" ({team})" : "")}@{(b.ChannelId is { } c ? Name(c) : "-")}"))).Append("\n```");
        return CommandReply.Of(control.Redact(text.ToString()));
    }

    private Task<CommandReply> SetConfigAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        var key = ConfigKeys.Find(call.Option("key"));
        if (key is null)
        {
            return Task.FromResult(CommandReply.Of("That setting can't be changed here. They are: " + string.Join(", ", ConfigKeys.All.Select(k => k.Name)) + "."));
        }
        var value = call.Option("value") ?? "";
        return EditAsync(config =>
        {
            var (changed, problem) = key.Set(config, value);
            return changed is null
                ? new ConfigEdit.Refuse(problem ?? "That value isn't valid.")
                : new ConfigEdit.Change(changed, $"{key.Name} is now {key.Get(changed)}." + (key.NeedsRestart ? " It applies after the bot restarts." : ""));
        }, cancellationToken);
    }

    // ---- start, stop, status, revoke

    private async Task<CommandReply> SetRunningAsync(bool running, CancellationToken cancellationToken)
    {
        if (control.Running == running)
        {
            return CommandReply.Of(running ? "The relay is running already." : "The relay is stopped already.");
        }
        await control.SetRunningAsync(running, cancellationToken).ConfigureAwait(false);
        return CommandReply.Of(running ? "The relay bots are joining their channels." : "The relay bots left their channels. /gvg start brings them back.");
    }

    private CommandReply ShowStatus()
    {
        var status = control.Status();
        var text = new StringBuilder(status.Running ? "Relay: running\n" : "Relay: stopped\n");
        foreach (var group in status.Groups)
        {
            text.Append(Capital(group.Group)).Append('\n');
            if (group.Channels.Count == 0)
            {
                text.Append("  no channels bound\n");
            }
            foreach (var channel in group.Channels)
            {
                text.Append("  ").Append(channel.Name).Append(": ").Append(channel.Bot is null ? "no bot" : $"{channel.Bot} {Describe(channel.State)}")
                    .Append(channel.NicknameRefused ? " (it can't set its nickname: give it the Change Nickname permission)" : "").Append('\n');
            }
        }
        var spare = status.Bots.Where(b => b.ChannelName is null).Select(b => b.Name).ToArray();
        if (spare.Length > 0)
        {
            text.Append("Free bots: ").Append(string.Join(", ", spare)).Append('\n');
        }
        text.Append(CultureInfo.InvariantCulture, $"Apps connected: {status.AppsConnected}; linked devices: {status.Devices}\n");
        text.Append(CultureInfo.InvariantCulture, $"Frames relayed: {status.FramesSent}; packets dropped: {status.PacketsDropped}");
        return CommandReply.Of(text.ToString());
    }

    private CommandReply Revoke(CommandInvocation call)
    {
        if (!UserId.TryParse(call.Option("user"), out var user))
        {
            return CommandReply.Of("Give the player to unlink.");
        }
        var revoked = control.Revoke(user);
        return CommandReply.Of(revoked == 0
            ? $"<@{user}> has no linked app."
            : $"Revoked {revoked} linked app(s) for <@{user}>. Any open session is closed.");
    }

    // ---- helpers

    private async Task<CommandReply> EditAsync(Func<BotConfig, ConfigEdit> edit, CancellationToken cancellationToken)
    {
        var outcome = await control.EditAsync(edit, cancellationToken).ConfigureAwait(false);
        return CommandReply.Of(outcome.Message);
    }

    private string Name(ChannelId channel) => control.ChannelName(channel);

    /// <summary>The bots whose team name an edit changed without moving them, as an unbind shifts the channels after it.</summary>
    private static string Renamed(BotConfig before, BotConfig after)
    {
        var renamed = after.Bots
            .Select(b => (Bot: b, Was: before.Bots.FirstOrDefault(o => o.TokenRef == b.TokenRef)))
            .Where(p => p.Was is not null && p.Was.ChannelId == p.Bot.ChannelId && p.Bot.ChannelId is not null)
            .Select(p => (Was: RelayNames.Of(before, p.Was!), Now: RelayNames.Of(after, p.Bot)))
            .Where(p => p.Was != p.Now)
            .Select(p => $"{p.Was} is {p.Now} now")
            .ToArray();
        return renamed.Length == 0 ? "" : $" {string.Join(", ", renamed)}.";
    }

    private static string Describe(BotState state) => state switch
    {
        BotState.Ready => "ready",
        BotState.Joining => "not in yet",
        _ => "idle",
    };

    private static bool TryGroup(string? text, out Group group)
    {
        group = default;
        switch (text)
        {
            case "attack":
                group = Group.Attack;
                return true;
            case "defense":
                group = Group.Defense;
                return true;
            default:
                return false;
        }
    }

    private static string Capital(Group group) => group == Group.Attack ? "Attack" : "Defense";
}
