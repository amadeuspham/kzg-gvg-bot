using Kzg.Core.Abstractions;
using Kzg.Core.Testing;
using static Kzg.Bot.Tests.Engine.EngineRig;

namespace Kzg.Bot.Tests.Engine;

// Audio that is not a player's, such as jungle calls: read once a tick, heard in every VC it targets.
public sealed class EngineSourceTests
{
    private const byte Call = 9;

    private static readonly TargetSpec Teams = new TargetSpec.Groups([Group.Attack, Group.Defense]);

    private static EngineRig Team(params IAudioSource[] sources)
    {
        var rig = new EngineRig(sources: sources);
        rig.Seat(U1, A1);
        rig.Seat(U2, A2);
        rig.Seat(Enemy, D1);
        rig.SignIn(U1);
        rig.Idle(1);
        return rig;
    }

    [Fact]
    public void A_source_alone_passes_through_unchanged_to_every_ready_bound_VC_a_player_sits_in_or_not()
    {
        var source = new StubSource(Teams);
        using var rig = Team(source);

        source.Play(Call, 10);
        rig.Idle(15);

        int[] all = [.. Enumerable.Range(0, 10)];
        Assert.All([rig.L1, rig.L2, rig.L3, rig.LD], link =>
        {
            Assert.Equal(all, Indexes(link));
            Assert.Equal(Packet(Call, 3), link.Sent[3].Opus);
        });
    }

    [Fact]
    public void A_source_is_sent_only_where_it_targets()
    {
        var source = new StubSource(new TargetSpec.Groups([Group.Defense]));
        using var rig = Team(source);

        source.Play(Call, 5);
        rig.Idle(10);

        Assert.Equal(5, rig.LD.Sent.Count);
        Assert.All([rig.L1, rig.L2, rig.L3], link => Assert.Empty(link.Sent));
    }

    [Fact]
    public void A_source_and_a_broadcasting_player_are_mixed_where_both_are_heard()
    {
        var source = new StubSource(Teams);
        using var rig = Team(source);
        rig.Press(U1, true);
        var i = 0;
        for (; i < 40; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        Assert.NotEmpty(rig.L2.Sent);
        foreach (var link in new[] { rig.L1, rig.L2, rig.L3, rig.LD })
        {
            link.ClearSent();
        }

        source.Play(2, 10);
        for (var end = i + 10; i < end; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }

        // The player's team VCs hear both, summed (100 + 200) and encoded once.
        var mixed = FakeOpusCodecFactory.Encoded(300);
        Assert.All([rig.L2, rig.L3], link =>
        {
            Assert.Equal(10, link.Sent.Count);
            Assert.All(link.Sent, frame => Assert.Equal(mixed, frame.Opus));
        });
        // The player's own VC and the other team hear the call alone, passed through.
        Assert.All([rig.L1, rig.LD], link => Assert.Equal([.. Enumerable.Range(0, 10)], Indexes(link)));
        Assert.All(rig.L1.Sent, frame => Assert.Equal(2, frame.Opus[0]));
    }

    [Fact]
    public void Without_a_ready_link_nothing_is_sent()
    {
        var source = new StubSource(Teams);
        using var rig = Team(source);
        foreach (var link in new[] { rig.L1, rig.L2, rig.L3, rig.LD })
        {
            link.Ready = false;
        }
        rig.Idle(1);

        source.Play(Call, 10);
        rig.Idle(15);

        Assert.All([rig.L1, rig.L2, rig.L3, rig.LD], link =>
        {
            Assert.Empty(link.Sent);
            Assert.Equal(0, link.SentWhileNotReady);
        });
    }

    [Fact]
    public void A_VC_whose_bot_is_down_misses_the_call_and_the_others_hear_it()
    {
        var source = new StubSource(Teams);
        using var rig = Team(source);
        rig.L2.Ready = false;
        rig.Idle(1);

        source.Play(Call, 5);
        rig.Idle(10);

        Assert.Empty(rig.L2.Sent);
        Assert.All([rig.L1, rig.L3, rig.LD], link => Assert.Equal(5, link.Sent.Count));
    }

    [Fact]
    public void A_throwing_source_does_not_stop_the_player_relay()
    {
        var source = new StubSource(Teams) { Throws = true };
        using var rig = Team(source);

        rig.Press(U1, true);
        for (var i = 0; i < 50; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(10);

        int[] first50 = [.. Enumerable.Range(0, 50)];
        Assert.Equal(first50, Indexes(rig.L2));
        Assert.Equal(first50, Indexes(rig.L3));
        Assert.Empty(rig.LD.Sent);
        var stats = rig.Engine.Stats;
        Assert.True(stats.SourceErrors >= 50, $"{stats.SourceErrors} source errors");
        Assert.Equal(0, stats.TickFaults);
    }

    [Fact]
    public void A_source_that_recovers_plays_again()
    {
        var source = new StubSource(Teams) { Throws = true };
        using var rig = Team(source);
        rig.Idle(5);

        source.Throws = false;
        source.Play(Call, 5);
        rig.Idle(10);

        Assert.Equal(5, rig.L1.Sent.Count);
    }

    [Fact]
    public void A_source_frame_that_will_not_decode_is_dropped_and_the_next_plays()
    {
        var source = new StubSource(Teams);
        using var rig = new EngineRig(codecs: new ThrowingOnTagCodecs(0xFF), sources: [source]);
        rig.Idle(1);

        source.Enqueue(FrameRead.Frame(new byte[] { 0xFF, 0 }));
        source.Play(Call, 3);
        rig.Idle(10);

        Assert.Equal([0, 1, 2], Indexes(rig.L1));
        Assert.Equal(1, rig.Engine.Stats.DecodeErrors);
    }

    [Fact]
    public void The_speaking_flag_goes_up_with_a_call_and_down_a_hangover_after_it()
    {
        var source = new StubSource(Teams);
        using var rig = Team(source);

        source.Play(Call, 20);
        rig.Idle(30);

        Assert.All([rig.L1, rig.L2, rig.L3, rig.LD], link =>
        {
            var changes = link.SpeakingChanges;
            Assert.Equal([true, false], changes.Select(c => c.On));
            Assert.Equal(link.Sent[0].At, changes[0].At);
            Assert.True(changes[1].At - link.Sent[^1].At >= Frame * 4, "the flag held for the hangover");
        });
    }

    [Fact]
    public void Each_clip_after_a_silence_starts_on_a_fresh_decoder_and_a_running_clip_keeps_its_own()
    {
        var codecs = new CountingCodecs();
        var source = new StubSource(Teams);
        using var rig = new EngineRig(codecs: codecs, sources: [source]);
        rig.Idle(1);

        source.Play(Call, 3);
        source.Play(Call, 3);
        rig.Idle(10);
        Assert.Equal(1, codecs.Decoders);

        source.Play(Call, 3);
        rig.Idle(10);
        Assert.Equal(2, codecs.Decoders);
    }

    /// <summary>A source that plays what a test queues, one frame a tick, and may throw on every read.</summary>
    private sealed class StubSource(TargetSpec targets) : IAudioSource
    {
        private readonly Queue<FrameRead> _frames = new();

        public bool Throws { get; set; }

        public SourceId Id { get; } = SourceId.ForTts("stub");

        public TargetSpec Targets { get; } = targets;

        public void Enqueue(FrameRead frame) => _frames.Enqueue(frame);

        /// <summary>A clip of <paramref name="frames"/> packets tagged <paramref name="tag"/> and numbered from 0.</summary>
        public void Play(byte tag, int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                Enqueue(FrameRead.Frame(Packet(tag, i)));
            }
        }

        public FrameRead Read(long tick) =>
            Throws ? throw new InvalidOperationException("the source broke")
            : _frames.TryDequeue(out var frame) ? frame : FrameRead.Silent;
    }

    private sealed class CountingCodecs : IOpusCodecFactory
    {
        private readonly FakeOpusCodecFactory _inner = new();

        public int Decoders { get; private set; }

        public IOpusDecoder Decoder()
        {
            Decoders++;
            return _inner.Decoder();
        }

        public IOpusEncoder Encoder(int bitrate) => _inner.Encoder(bitrate);
    }

    private sealed class ThrowingOnTagCodecs(byte bad) : IOpusCodecFactory
    {
        private readonly FakeOpusCodecFactory _inner = new();

        public IOpusDecoder Decoder() => new BadTagDecoder(_inner.Decoder(), bad);

        public IOpusEncoder Encoder(int bitrate) => _inner.Encoder(bitrate);

        private sealed class BadTagDecoder(IOpusDecoder inner, byte bad) : IOpusDecoder
        {
            public int Decode(ReadOnlySpan<byte> opus, Span<short> pcm) =>
                opus[0] == bad ? throw new InvalidDataException("corrupt") : inner.Decode(opus, pcm);

            public int Conceal(Span<short> pcm) => inner.Conceal(pcm);
        }
    }
}
