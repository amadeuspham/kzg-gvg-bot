using Kzg.Core.Abstractions;

namespace Kzg.Core.Config;

/// <summary>A relay bot the setup page signed up: the name its token goes by and its Discord user, once known.</summary>
public sealed record SetupBot(string TokenRef, UserId? BotUserId);

/// <summary>
/// The config changes the two setup steps make, free of any page or command: the setup page's bot list and
/// server, and <c>/gvg setup</c>'s teams with a relay bot in each team channel.
/// </summary>
public static class SetupPlans
{
    /// <summary>The name the setup page gives the token pasted in its n-th field, from 1; <see cref="RelayNames.ForSlot"/> labels that field.</summary>
    public static string TokenRefFor(int number) => $"relay{number}";

    /// <summary>
    /// The config after the setup page: its bots, the first primary, in <paramref name="guildId"/>. Everything
    /// else an earlier config set stays, and a bot that was there before keeps its channel. A move to another
    /// server drops the teams and alerts channel, whose IDs belong to the old one.
    /// </summary>
    /// <param name="publicHost">The players' address; null keeps the one set before.</param>
    public static BotConfig WithBots(BotConfig? existing, IReadOnlyList<SetupBot> bots, ulong guildId, string? publicHost)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bots.Count);
        var config = existing ?? new BotConfig();
        if (config.GuildId is { } old && old != guildId)
        {
            config = config with { Groups = new GroupsConfig(), AlertsChannelId = null };
        }
        var keep = config.GuildId == guildId || config.GuildId is null;
        var entries = bots.Select((bot, i) =>
        {
            var before = keep && bot.BotUserId is { } user ? config.Bots.FirstOrDefault(b => b.BotUserId == user) : null;
            return new BotEntry { TokenRef = bot.TokenRef, BotUserId = bot.BotUserId, ChannelId = before?.ChannelId, Primary = i == 0 };
        }).ToList();
        // Two bots never share a channel: a kept channel goes to the first bot that had it.
        HashSet<ChannelId> taken = [];
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].ChannelId is { } channel && !taken.Add(channel))
            {
                entries[i] = entries[i] with { ChannelId = null };
            }
        }
        return config with { GuildId = guildId, PublicHost = publicHost ?? config.PublicHost, Bots = entries };
    }

    /// <summary>
    /// The setup slot a bot was pasted in, from 1, by the tokenRef the setup page gave it; null for a bot named
    /// some other way, as in a config written by hand.
    /// </summary>
    public static int? SlotOf(string tokenRef)
    {
        for (var slot = 1; slot <= RelayNames.Slots; slot++)
        {
            if (string.Equals(tokenRef, TokenRefFor(slot), StringComparison.Ordinal))
            {
                return slot;
            }
        }
        return null;
    }

    /// <summary>
    /// The bots with one in each of <paramref name="groups"/>' channels, so each bot lands where the setup page
    /// said: the n-th Attack channel takes the bot pasted as An Broadcaster, the n-th Defense channel the Dn Broadcaster. A
    /// channel whose own bot is missing keeps the bot already in it, else takes a left-over bot in the config's
    /// order; a bot left over after that leaves voice. Null when there are too few bots.
    /// </summary>
    public static IReadOnlyList<BotEntry>? AssignChannels(IReadOnlyList<BotEntry> bots, GroupsConfig groups)
    {
        var channels = groups.Attack.Channels.Concat(groups.Defense.Channels).ToList();
        if (channels.Count > bots.Count)
        {
            return null;
        }
        var result = bots.Select(b => b with { ChannelId = null }).ToArray();
        var placed = new bool[result.Length];
        HashSet<ChannelId> filled = [];
        void Place(int bot, ChannelId channel)
        {
            result[bot] = result[bot] with { ChannelId = channel };
            placed[bot] = true;
            filled.Add(channel);
        }
        foreach (var (channel, slot) in SlotsOf(groups))
        {
            var own = Array.FindIndex(result, b => SlotOf(b.TokenRef) == slot);
            if (own >= 0)
            {
                Place(own, channel);
            }
        }
        foreach (var channel in channels.Where(c => !filled.Contains(c)))
        {
            var there = Enumerable.Range(0, bots.Count).FirstOrDefault(i => !placed[i] && bots[i].ChannelId == channel, -1);
            if (there >= 0)
            {
                Place(there, channel);
            }
        }
        foreach (var channel in channels.Where(c => !filled.Contains(c)).ToList())
        {
            Place(Array.IndexOf(placed, false), channel);
        }
        return result;
    }

    private static IEnumerable<(ChannelId Channel, int Slot)> SlotsOf(GroupsConfig groups) =>
        groups.Attack.Channels.Take(RelayNames.ChannelsPerTeam).Select((c, i) => (c, i + 1))
            .Concat(groups.Defense.Channels.Take(RelayNames.ChannelsPerTeam).Select((c, i) => (c, RelayNames.ChannelsPerTeam + i + 1)));
}
