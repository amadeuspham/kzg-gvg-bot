using Kzg.Bot.Engine;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Routing;
using Kzg.Core.Testing;

namespace Kzg.Bot.Tests.Engine;

/// <summary>
/// An engine over a fake gateway, driven tick by tick on a clock that moves only when the test says.
/// Four VCs: three in the attack team and one in the defense team.
/// </summary>
internal sealed class EngineRig : IDisposable
{
    public static readonly ChannelId A1 = new(101), A2 = new(102), A3 = new(103), D1 = new(201);
    public static readonly UserId U1 = new(1), U2 = new(2), U3 = new(3), Enemy = new(4);

    public static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(20);

    public EngineRig(
        BotConfig? config = null,
        EngineOptions? options = null,
        IOpusCodecFactory? codecs = null,
        IReadOnlyList<IAudioSource>? sources = null)
    {
        Gateway = new FakeVoiceGateway(Time);
        (L1, L2, L3, LD) = (Gateway.AddLink(A1), Gateway.AddLink(A2), Gateway.AddLink(A3), Gateway.AddLink(D1));
        Engine = new AudioEngine(config ?? Config(), Gateway, codecs ?? new FakeOpusCodecFactory(), Time, options, sources: sources);
        Engine.StatusChanged += Statuses.Add;
    }

    public ManualTimeSource Time { get; } = new();

    public FakeVoiceGateway Gateway { get; }

    public FakeVoiceChannelLink L1 { get; }

    public FakeVoiceChannelLink L2 { get; }

    public FakeVoiceChannelLink L3 { get; }

    public FakeVoiceChannelLink LD { get; }

    public AudioEngine Engine { get; }

    public List<UserStatus> Statuses { get; } = [];

    public static BotConfig Config(int prerollMs = 300) => new()
    {
        PrerollMs = prerollMs,
        Groups = new GroupsConfig
        {
            Attack = new GroupConfig { Channels = [A1, A2, A3] },
            Defense = new GroupConfig { Channels = [D1] },
        },
    };

    /// <summary>A packet whose first byte is the speaker's tag, which the fake codec decodes to tag * 100, and whose second is its index.</summary>
    public static byte[] Packet(byte tag, int index) => [tag, (byte)index];

    public void Seat(UserId user, ChannelId? channel) => Gateway.Join(user, channel);

    public void SignIn(UserId user) => Engine.Connected(user, Time.Now);

    public void Press(UserId user, bool down) =>
        Engine.OnEdge(new BroadcastEdge(user, BroadcastAction.Group, down, TriggerKind.AppKey, Time.Now));

    public void Beat(UserId user, bool held) =>
        Engine.Heartbeat(user, held ? [BroadcastAction.Group] : [], false, Time.Now);

    /// <summary>One 20 ms: each talker's next frame arrives, the engine ticks, the clock moves on.</summary>
    public void Step(params (FakeVoiceChannelLink Link, UserId User, byte[] Packet)[] talkers)
    {
        foreach (var (link, user, packet) in talkers)
        {
            link.Deliver(user, packet);
        }
        Engine.Tick(Time.Now);
        Time.Advance(Frame);
    }

    public void Idle(int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            Step();
        }
    }

    /// <summary>The index byte of every frame a link was sent, in order.</summary>
    public static int[] Indexes(FakeVoiceChannelLink link) => [.. link.Sent.Select(f => (int)f.Opus[1])];

    public void Dispose() => Engine.Dispose();
}
