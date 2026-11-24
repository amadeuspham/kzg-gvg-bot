using Kzg.Core.Abstractions;

namespace Kzg.Core.Config;

/// <summary>
/// What admins and players call a relay bot: the team channel it sits in, such as "A1 Broadcaster" for the first
/// Attack channel. The name follows the bot's channel, so it changes with <c>/gvg setup</c>, bind, unbind and
/// bot assign; the tokenRef stays the bot's name in the files and is shown only for a bot with no team channel.
/// </summary>
public static class RelayNames
{
    /// <summary>The team channels the setup page and <c>/gvg setup</c> name: the guild's three small teams per side.</summary>
    public const int ChannelsPerTeam = 3;

    /// <summary>Every setup slot: A1 to A3, then D1 to D3.</summary>
    public const int Slots = 2 * ChannelsPerTeam;

    /// <summary>The setup page's n-th bot, from 1: the channel it fills at <c>/gvg setup</c>, such as "D2 Broadcaster".</summary>
    public static string ForSlot(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, Slots);
        return slot <= ChannelsPerTeam ? $"A{slot} Broadcaster" : $"D{slot - ChannelsPerTeam} Broadcaster";
    }

    /// <summary>A bound channel's short name, "A1" for the first Attack channel; null for a channel bound to neither team.</summary>
    public static string? TeamChannel(GroupsConfig groups, ChannelId channel)
    {
        var attack = IndexOf(groups.Attack.Channels, channel);
        if (attack >= 0)
        {
            return $"A{attack + 1}";
        }
        var defense = IndexOf(groups.Defense.Channels, channel);
        return defense >= 0 ? $"D{defense + 1}" : null;
    }

    /// <summary>"A1 Broadcaster" for a bot in a team channel; null for a bot with no channel or one bound to neither team.</summary>
    public static string? Team(BotConfig config, BotEntry bot) =>
        bot.ChannelId is { } channel && TeamChannel(config.Groups, channel) is { } team ? $"{team} Broadcaster" : null;

    /// <summary>How a reply, status line, alert or log names the bot: its team name, else its tokenRef and why it has none.</summary>
    public static string Of(BotConfig config, BotEntry bot) =>
        Team(config, bot) ?? (bot.ChannelId is null ? $"{bot.TokenRef} (spare)" : $"{bot.TokenRef} (no team)");

    private static int IndexOf(IReadOnlyList<ChannelId> channels, ChannelId channel)
    {
        for (var i = 0; i < channels.Count; i++)
        {
            if (channels[i] == channel)
            {
                return i;
            }
        }
        return -1;
    }
}
