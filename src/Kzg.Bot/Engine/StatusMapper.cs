using Kzg.Core.Abstractions;
using Kzg.Core.Routing;
using Kzg.Protocol;

namespace Kzg.Bot.Engine;

/// <summary>Turns what routing knows of a player into the <c>status</c> their app is sent.</summary>
public static class StatusMapper
{
    /// <param name="botDownIn">The VCs of the player's group whose relay bot isn't ready; empty when none.</param>
    public static StatusMessage ToMessage(UserStatus status, IChannelNames names, IReadOnlyList<ChannelId>? botDownIn = null) => new(
        [.. status.Active.Select(action => action.ToWire())],
        status.OpenMic,
        [.. status.Targets.Select(channel => NameOf(names, channel))],
        status.Warnings,
        status.Channel is { } channel ? NameOf(names, channel) : null,
        status.Channel is not null ? status.Group?.ToWire() : null,
        [.. (botDownIn ?? []).Select(channel => NameOf(names, channel)).Distinct()]);

    /// <summary>The VCs of <paramref name="group"/>, in config order, whose relay bot isn't in <paramref name="ready"/>. This is the rule behind routing's <c>botDown</c> warning.</summary>
    public static IReadOnlyList<ChannelId> BotDownIn(Group? group, RoutingConfig config, IReadOnlySet<ChannelId> ready) =>
        group is { } own ? [.. config.ChannelsOf(own).Where(channel => !ready.Contains(channel))] : [];

    /// <summary>A name the wire accepts: Discord's, tidied, or a stand-in when it is empty.</summary>
    private static string NameOf(IChannelNames names, ChannelId channel)
    {
        var name = new string([.. (names.NameOf(channel) ?? "").Trim().Where(c => !char.IsControl(c))]);
        if (name.Length > 100)
        {
            name = name[..100];
        }
        return name.Length == 0 ? $"Channel {channel}" : name;
    }
}
