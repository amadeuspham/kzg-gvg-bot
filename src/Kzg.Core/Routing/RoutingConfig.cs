using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Protocol;

namespace Kzg.Core.Routing;

/// <summary>The part of the config routing reads: which VC is in which group, and the timings.</summary>
public sealed record RoutingConfig
{
    private readonly IReadOnlyDictionary<ChannelId, Group> _groupOf;
    private readonly IReadOnlyDictionary<Group, IReadOnlyList<ChannelId>> _channels;

    public RoutingConfig(
        IReadOnlyDictionary<Group, IReadOnlyList<ChannelId>> channels,
        TimeSpan preroll,
        TimeSpan maxHold)
    {
        Dictionary<ChannelId, Group> groupOf = [];
        foreach (var (group, list) in channels)
        {
            foreach (var channel in list)
            {
                // A VC in two groups would leak one group's calls into the other; validation refuses it.
                if (!groupOf.TryAdd(channel, group))
                {
                    throw new ArgumentException("a channel is bound to one group at most", nameof(channels));
                }
            }
        }
        (_groupOf, _channels) = (groupOf, channels);
        (Preroll, MaxHold) = (preroll, maxHold);
    }

    /// <summary>How far before a press its relay starts reading.</summary>
    public TimeSpan Preroll { get; }

    /// <summary>The longest a key hold relays before it is taken for stuck and ended.</summary>
    public TimeSpan MaxHold { get; }

    /// <summary>How long an app may go without a heartbeat before its sessions end.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromMilliseconds(WireProtocol.TimeoutMs);

    public static RoutingConfig From(BotConfig config) => new(
        new Dictionary<Group, IReadOnlyList<ChannelId>>
        {
            [Group.Attack] = config.Groups.Attack.Channels,
            [Group.Defense] = config.Groups.Defense.Channels,
        },
        config.Preroll,
        config.MaxHold);

    /// <summary>The group a VC is bound to; null for an unbound VC or none.</summary>
    public Group? GroupOf(ChannelId? channel) => channel is { } c && _groupOf.TryGetValue(c, out var group) ? group : null;

    public IReadOnlyList<ChannelId> ChannelsOf(Group group) => _channels.TryGetValue(group, out var list) ? list : [];
}
