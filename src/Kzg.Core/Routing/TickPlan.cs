using Kzg.Core.Abstractions;
using Kzg.Protocol;

namespace Kzg.Core.Routing;

/// <summary>One player relaying this tick.</summary>
/// <param name="From">The VC the player is in, whose bot receives their audio; null outside voice.</param>
/// <param name="Targets">
/// The VCs their audio goes to: their group's other bound VCs with a ready bot, or every other bound VC
/// of both groups while the all-teams key is held. Empty outside a team VC.
/// </param>
/// <param name="Actions">The sessions keeping the relay on, in <see cref="BroadcastAction"/> order; empty while it drains.</param>
/// <param name="ReadFrom">
/// Set only on the tick the relay starts: the receive-clock time the player's buffer starts at. Each
/// session's trigger sets it: a press reads back by the pre-roll.
/// </param>
/// <param name="DrainUntil">
/// Set while the relay drains after the player let go: play on until the buffer has played what
/// arrived by this time, then say so with <see cref="RoutingEngine.OnRelayDrained"/>.
/// </param>
public sealed record SourceRoute(
    UserId User,
    ChannelId? From,
    IReadOnlyList<ChannelId> Targets,
    IReadOnlyList<BroadcastAction> Actions,
    TimeSpan? ReadFrom,
    TimeSpan? DrainUntil = null)
{
    public SourceId Source => SourceId.ForUser(User);
}

/// <summary>What one app's <c>status</c> message says, in engine types; the host turns IDs into names.</summary>
/// <param name="Channel">The VC the player is in; null outside voice.</param>
/// <param name="Group">The group of that VC; null outside a team VC.</param>
/// <param name="Active">The sessions actually relaying, which is none outside a team VC.</param>
/// <param name="OpenMic">The open-mic latch as the bot holds it: off once the bot ends or refuses it.</param>
/// <param name="Targets">The VCs reached while relaying.</param>
public sealed record UserStatus(
    UserId User,
    ChannelId? Channel,
    Group? Group,
    IReadOnlyList<BroadcastAction> Active,
    bool OpenMic,
    IReadOnlyList<ChannelId> Targets,
    IReadOnlyList<StatusWarning> Warnings)
{
    public bool SameAs(UserStatus other) =>
        User == other.User && Channel == other.Channel && Group == other.Group && OpenMic == other.OpenMic
        && Active.SequenceEqual(other.Active) && Targets.SequenceEqual(other.Targets) && Warnings.SequenceEqual(other.Warnings);
}

/// <summary>What one engine tick does: who relays where, whose relay ended, and which apps need a new status.</summary>
public sealed class TickPlan
{
    private readonly Dictionary<ChannelId, List<UserId>> _byDestination = [];

    internal TickPlan(TimeSpan now, IReadOnlyList<SourceRoute> routes, IReadOnlyList<UserId> ended, IReadOnlyList<UserStatus> statusChanges)
    {
        (Now, Routes, Ended, StatusChanges) = (now, routes, ended, statusChanges);
        foreach (var route in routes)
        {
            foreach (var target in route.Targets)
            {
                if (!_byDestination.TryGetValue(target, out var sources))
                {
                    _byDestination[target] = sources = [];
                }
                sources.Add(route.User);
            }
        }
    }

    public TimeSpan Now { get; }

    /// <summary>Every relaying player, in user ID order; one with no targets still reads, so its buffer stays live.</summary>
    public IReadOnlyList<SourceRoute> Routes { get; }

    /// <summary>Players whose relay stopped this tick.</summary>
    public IReadOnlyList<UserId> Ended { get; }

    /// <summary>Statuses that differ from what each app was last given; an app's first tick always has one.</summary>
    public IReadOnlyList<UserStatus> StatusChanges { get; }

    /// <summary>The VCs that receive anything this tick.</summary>
    public IEnumerable<ChannelId> Destinations => _byDestination.Keys;

    /// <summary>The players heard in a VC this tick, in user ID order; never a player sitting in it.</summary>
    public IReadOnlyList<UserId> SourcesFor(ChannelId destination) =>
        _byDestination.TryGetValue(destination, out var sources) ? sources : [];
}
