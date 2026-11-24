using Kzg.Bot.Engine;
using Kzg.Core.Abstractions;
using Kzg.Core.Testing;
using static Kzg.Bot.Tests.Engine.EngineRig;

namespace Kzg.Bot.Tests.Engine;

// Links come and go while the engine runs, as /gvg bind, unbind and bot assign put relay bots in and out of VCs.
public sealed class EngineLinksTests : IDisposable
{
    private sealed class MovingGateway : IVoiceGateway
    {
        private readonly List<IVoiceChannelLink> _links = [];

        public IReadOnlyList<IVoiceChannelLink> Links => _links;

        public event Action<UserId, ChannelId?> PresenceChanged = delegate { };

        public event Action<IVoiceChannelLink> LinkAdded = delegate { };

        public event Action<IVoiceChannelLink> LinkRemoved = delegate { };

        public int Subscribers => LinkAdded.GetInvocationList().Length - 1 + LinkRemoved.GetInvocationList().Length - 1;

        public void Add(IVoiceChannelLink link)
        {
            _links.Add(link);
            LinkAdded(link);
        }

        public void Remove(IVoiceChannelLink link)
        {
            _links.Remove(link);
            LinkRemoved(link);
        }

        public void Seat(UserId user, ChannelId? channel) => PresenceChanged(user, channel);
    }

    private readonly ManualTimeSource _time = new();
    private readonly MovingGateway _gateway = new();
    private readonly FakeVoiceChannelLink _l1, _l2;
    private readonly AudioEngine _engine;

    public EngineLinksTests()
    {
        (_l1, _l2) = (new FakeVoiceChannelLink(A1, 91, _time), new FakeVoiceChannelLink(A2, 92, _time));
        _gateway.Add(_l1);
        _gateway.Add(_l2);
        _engine = new AudioEngine(Config(), _gateway, new FakeOpusCodecFactory(), _time);
        _gateway.Seat(U1, A1);
        _gateway.Seat(U2, A2);
        _gateway.Seat(U3, A3);
        _engine.Connected(U1, _time.Now);
        Step();
    }

    public void Dispose() => _engine.Dispose();

    private void Step(params (FakeVoiceChannelLink Link, UserId User, byte[] Packet)[] talkers)
    {
        foreach (var (link, user, packet) in talkers)
        {
            link.Deliver(user, packet);
        }
        _engine.Tick(_time.Now);
        _time.Advance(Frame);
    }

    private void Speak(int frames, int from = 0)
    {
        for (var i = from; i < from + frames; i++)
        {
            Step((_l1, U1, Packet(1, i)));
        }
    }

    [Fact]
    public void A_link_added_while_running_receives_the_relay_once_it_is_ready()
    {
        var l3 = new FakeVoiceChannelLink(A3, 93, _time);
        _gateway.Add(l3);
        _engine.OnEdge(new BroadcastEdge(U1, BroadcastAction.Group, true, TriggerKind.AppKey, _time.Now));

        Speak(20);

        Assert.NotEmpty(l3.Sent);
        Assert.Contains(A3, _engine.ReadyChannels);
        Assert.Equal(0, l3.SentWhileNotReady);
    }

    [Fact]
    public void A_link_that_is_not_ready_yet_is_not_sent_to_and_the_game_goes_on_without_it()
    {
        var l3 = new FakeVoiceChannelLink(A3, 93, _time) { Ready = false };
        _gateway.Add(l3);
        _engine.OnEdge(new BroadcastEdge(U1, BroadcastAction.Group, true, TriggerKind.AppKey, _time.Now));

        Speak(20);

        Assert.Empty(l3.Sent);
        Assert.NotEmpty(_l2.Sent);
        Assert.DoesNotContain(A3, _engine.ReadyChannels);
    }

    [Fact]
    public void A_link_removed_while_running_gets_nothing_more_and_its_VC_is_no_longer_ready()
    {
        _engine.OnEdge(new BroadcastEdge(U1, BroadcastAction.Group, true, TriggerKind.AppKey, _time.Now));
        Speak(10);
        var before = _l2.Sent.Count;
        var changes = 0;
        _engine.LinksChanged += () => changes++;

        _gateway.Remove(_l2);
        Speak(10, from: 10);

        Assert.True(before > 0);
        Assert.Equal(before, _l2.Sent.Count);
        Assert.DoesNotContain(A2, _engine.ReadyChannels);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_removed_link_no_longer_feeds_the_engine()
    {
        _gateway.Remove(_l1);
        Step();
        _engine.OnEdge(new BroadcastEdge(U1, BroadcastAction.Group, true, TriggerKind.AppKey, _time.Now));

        Speak(10);

        // The speaker's packets came in on a link nobody listens to.
        Assert.Empty(_l2.Sent);
    }

    [Fact]
    public void A_link_that_replaces_another_in_the_same_VC_takes_its_place()
    {
        var replacement = new FakeVoiceChannelLink(A2, 94, _time);
        _gateway.Add(replacement);
        _engine.OnEdge(new BroadcastEdge(U1, BroadcastAction.Group, true, TriggerKind.AppKey, _time.Now));

        Speak(10);
        // The old link, removed afterwards, must not take the new one out.
        _gateway.Remove(_l2);
        Speak(10, from: 10);

        Assert.NotEmpty(replacement.Sent);
        Assert.Contains(A2, _engine.ReadyChannels);
    }

    [Fact]
    public void A_link_added_twice_is_kept_once()
    {
        _gateway.Add(_l1);
        _engine.OnEdge(new BroadcastEdge(U1, BroadcastAction.Group, true, TriggerKind.AppKey, _time.Now));

        Speak(20);
        Step();
        Step();
        Step();

        // One frame each, not two: a second copy of the link would double the relay.
        Assert.Equal(_l2.Sent.Select(f => f.Opus[1]).Distinct().Count(), _l2.Sent.Count);
        Assert.NotEmpty(_l2.Sent);
    }

    [Fact]
    public void A_link_in_the_gateway_before_the_engine_was_built_works_as_it_always_did()
    {
        // _l1 and _l2 were there at construction.
        _engine.OnEdge(new BroadcastEdge(U1, BroadcastAction.Group, true, TriggerKind.AppKey, _time.Now));

        Speak(20);

        Assert.NotEmpty(_l2.Sent);
    }

    [Fact]
    public void Disposing_stops_listening_for_links()
    {
        _engine.Dispose();

        Assert.Equal(0, _gateway.Subscribers);
    }
}
