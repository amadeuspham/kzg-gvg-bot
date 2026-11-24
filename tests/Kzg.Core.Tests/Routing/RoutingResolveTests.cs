using Kzg.Core.Abstractions;
using Kzg.Core.Routing;
using Kzg.Protocol;

namespace Kzg.Core.Tests.Routing;

// Where a source that is not a player is heard: bound VCs with a ready bot, nothing else.
public class RoutingResolveTests
{
    private static readonly ChannelId A1 = new(11), A2 = new(12), A3 = new(13);
    private static readonly ChannelId D1 = new(21), D2 = new(22);
    private static readonly ChannelId Lobby = new(99);

    private static readonly UserId Alice = new(1), Bob = new(2), Carol = new(3), Dave = new(4);

    private readonly RoutingEngine _engine = new(new RoutingConfig(
        new Dictionary<Group, IReadOnlyList<ChannelId>> { [Group.Attack] = [A1, A2, A3], [Group.Defense] = [D1, D2] },
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromSeconds(120)));

    public RoutingResolveTests()
    {
        foreach (var channel in new[] { A1, A2, D1, D2, Lobby })
        {
            _engine.OnLinkReady(channel, true);
        }
        // A3's bot is down.
    }

    [Fact]
    public void Groups_resolve_to_their_bound_VCs_with_a_ready_bot_in_group_order()
    {
        Assert.Equal([A1, A2, D1, D2], _engine.Resolve(new TargetSpec.Groups([Group.Attack, Group.Defense])));
        Assert.Equal([D1, D2], _engine.Resolve(new TargetSpec.Groups([Group.Defense])));
    }

    [Fact]
    public void A_group_named_twice_gives_each_VC_once()
    {
        Assert.Equal([D1, D2], _engine.Resolve(new TargetSpec.Groups([Group.Defense, Group.Defense])));
    }

    [Fact]
    public void Channels_keep_only_the_bound_ones_with_a_ready_bot()
    {
        // The lobby has a ready bot but is bound to no group; A3 is bound but its bot is down.
        Assert.Equal([D2, A1], _engine.Resolve(new TargetSpec.Channels([D2, Lobby, A3, A1, D2])));
    }

    [Fact]
    public void Users_resolve_to_the_bound_VC_each_sits_in_now()
    {
        _engine.OnPresence(Alice, A1);
        _engine.OnPresence(Bob, Lobby);
        _engine.OnPresence(Carol, A3);
        _engine.OnPresence(Dave, A1);

        Assert.Equal([A1], _engine.Resolve(new TargetSpec.Users([Alice, Bob, Carol, Dave, new UserId(77)])));

        _engine.OnPresence(Alice, D1);
        Assert.Equal([D1], _engine.Resolve(new TargetSpec.Users([Alice])));

        _engine.OnPresence(Alice, null);
        Assert.Empty(_engine.Resolve(new TargetSpec.Users([Alice])));
    }

    [Fact]
    public void A_bot_going_down_or_a_rebind_applies_at_once()
    {
        _engine.OnLinkReady(A1, false);
        Assert.Equal([A2], _engine.Resolve(new TargetSpec.Groups([Group.Attack])));

        _engine.Configure(new RoutingConfig(
            new Dictionary<Group, IReadOnlyList<ChannelId>> { [Group.Attack] = [A2, Lobby], [Group.Defense] = [] },
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromSeconds(120)));
        Assert.Equal([A2, Lobby], _engine.Resolve(new TargetSpec.Groups([Group.Attack])));
        Assert.Empty(_engine.Resolve(new TargetSpec.Channels([D1])));
    }

    [Fact]
    public void A_source_reaches_the_VC_a_player_sits_in_where_the_player_is_never_sent_back()
    {
        _engine.OnPresence(Alice, A1);
        _engine.OnAppConnected(Alice, TimeSpan.Zero);
        _engine.OnEdge(new BroadcastEdge(Alice, BroadcastAction.Group, true, TriggerKind.AppKey, TimeSpan.Zero));
        var plan = _engine.Tick(TimeSpan.FromMilliseconds(20));

        Assert.DoesNotContain(A1, plan.Destinations);
        Assert.Contains(A1, _engine.Resolve(new TargetSpec.Groups([Group.Attack])));
    }
}
