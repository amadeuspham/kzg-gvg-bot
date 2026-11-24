using Kzg.Bot.Engine;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Testing;
using static Kzg.Bot.Tests.Engine.EngineRig;

namespace Kzg.Bot.Tests.Engine;

// The glue between the gateway, the buffers, routing, the mixer and the links, on a clock the test moves.
public sealed class EngineRelayTests
{
    private static readonly int[] First50 = [.. Enumerable.Range(0, 50)];

    private static EngineRig Team()
    {
        var rig = new EngineRig();
        rig.Seat(U1, A1);
        rig.Seat(U2, A2);
        rig.Seat(U3, A3);
        rig.Seat(Enemy, D1);
        rig.SignIn(U1);
        rig.Idle(1);
        return rig;
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void A_broadcast_whose_packets_come_in_late_bursts_reaches_the_other_VCs_whole(int holdTicks)
    {
        // Three times, every packet sent over 200 to 240 ms is held back on the way and then let through at once.
        using var rig = Team();
        rig.Press(U1, true);
        Queue<byte[]> held = [];
        for (var i = 0; i < 260; i++)
        {
            if (i < 240)
            {
                held.Enqueue(Packet(1, i));
            }
            var holding = i is >= 40 and < 240 && (i - 40) % 70 < holdTicks;
            rig.Step(holding ? [] : [.. held.Select(p => (rig.L1, U1, p))]);
            if (!holding)
            {
                held.Clear();
            }
        }

        Assert.Equal([.. Enumerable.Range(0, 240)], Indexes(rig.L2));
        Assert.Equal([.. Enumerable.Range(0, 240)], Indexes(rig.L3));
    }

    [Fact]
    public void A_held_key_relays_the_speaker_to_the_other_team_VCs_and_never_back_or_to_the_other_team()
    {
        using var rig = Team();

        rig.Press(U1, true);
        for (var i = 0; i < 50; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(10);

        Assert.Equal(First50, Indexes(rig.L2));
        Assert.Equal(First50, Indexes(rig.L3));
        Assert.Empty(rig.L1.Sent);
        Assert.Empty(rig.LD.Sent);
        // One source goes on as it came: no decode, no re-encode.
        Assert.Equal(Packet(1, 7), rig.L2.Sent[7].Opus);
        Assert.All([rig.L1, rig.L2, rig.L3, rig.LD], link => Assert.Equal(0, link.SentWhileNotReady));
    }

    [Fact]
    public void Nothing_is_relayed_without_a_key_held()
    {
        using var rig = Team();

        for (var i = 0; i < 30; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(10);

        Assert.Empty(rig.L2.Sent);
        Assert.Empty(rig.L3.Sent);
    }

    [Fact]
    public void A_press_a_little_after_speech_began_still_gets_the_first_frames_through_the_preroll()
    {
        using var rig = Team();

        for (var i = 0; i < 10; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Press(U1, true);
        for (var i = 10; i < 40; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(10);

        Assert.Equal(Enumerable.Range(0, 40), Indexes(rig.L2));
        Assert.Equal(Enumerable.Range(0, 40), Indexes(rig.L3));
    }

    [Fact]
    public void Without_a_preroll_the_same_press_loses_the_start_of_the_word()
    {
        using var rig = new EngineRig(Config(prerollMs: 0));
        rig.Seat(U1, A1);
        rig.Seat(U2, A2);
        rig.SignIn(U1);
        rig.Idle(1);

        for (var i = 0; i < 10; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Press(U1, true);
        for (var i = 10; i < 40; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(10);

        var sent = Indexes(rig.L2);
        Assert.NotEmpty(sent);
        Assert.True(sent[0] >= 5, $"the first relayed frame was {sent[0]}");
    }

    [Fact]
    public void Two_speakers_at_once_are_mixed_for_a_VC_that_hears_both_and_passed_through_where_one_is_heard()
    {
        using var rig = Team();
        rig.SignIn(U2);
        rig.Idle(1);

        rig.Press(U1, true);
        rig.Press(U2, true);
        for (var i = 0; i < 40; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)), (rig.L2, U2, Packet(2, i)));
        }
        rig.Idle(10);

        // A3 hears both: 100 + 200 = 300, encoded by A3's own encoder. When both end, the buffers conceal
        // up to three frames each, and those are mixed too: -7 + -7.
        Assert.All(rig.L3.Sent.Take(40), frame => Assert.Equal(FakeOpusCodecFactory.Encoded(300), frame.Opus));
        Assert.All(rig.L3.Sent.Skip(40), frame => Assert.Equal(FakeOpusCodecFactory.Encoded(-14), frame.Opus));
        Assert.InRange(rig.L3.Sent.Count, 40, 43);
        // A1 hears only U2, A2 only U1: each as it came.
        Assert.Equal(Enumerable.Range(0, 40), Indexes(rig.L1));
        Assert.All(rig.L1.Sent, frame => Assert.Equal(2, frame.Opus[0]));
        Assert.Equal(Enumerable.Range(0, 40), Indexes(rig.L2));
        Assert.All(rig.L2.Sent, frame => Assert.Equal(1, frame.Opus[0]));
    }

    [Fact]
    public void A_VC_whose_bot_is_down_is_not_sent_to_and_the_speaker_is_warned_until_it_is_back()
    {
        using var rig = Team();
        rig.L3.Ready = false;

        rig.Press(U1, true);
        for (var i = 0; i < 20; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.L3.Ready = true;
        for (var i = 20; i < 40; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(10);

        Assert.Equal(0, rig.L3.SentWhileNotReady);
        Assert.Equal(Enumerable.Range(0, 40), Indexes(rig.L2));
        Assert.True(rig.L3.Sent.Count is > 0 and < 40);
        Assert.Contains(rig.Statuses, s => s.User == U1 && s.Warnings.Contains(Kzg.Protocol.StatusWarning.BotDown));
        Assert.Contains(rig.Statuses, s => s.User == U1 && s.Active.Contains(BroadcastAction.Group) && s.Targets.SequenceEqual([A2, A3]));
    }

    [Fact]
    public void The_speaking_flag_goes_on_with_the_first_frame_and_off_a_hangover_after_the_last()
    {
        using var rig = Team();

        rig.Press(U1, true);
        for (var i = 0; i < 20; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(15);

        var changes = rig.L2.SpeakingChanges;
        Assert.Equal([true, false], changes.Select(c => c.On));
        var lastFrame = rig.L2.Sent[^1].At;
        Assert.True(changes[1].At - lastFrame >= Frame * 4, "the flag held for the hangover");
        Assert.Empty(rig.L1.SpeakingChanges);
    }

    [Fact]
    public void A_silent_app_ends_the_relay_after_the_heartbeat_timeout_and_a_beating_one_goes_on()
    {
        using var rig = Team();
        rig.Press(U1, true);

        // 8 s of talk, with no heartbeat at all.
        for (var i = 0; i < 400; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }

        var lastRelayed = rig.L2.Sent[^1].At;
        Assert.InRange(lastRelayed - rig.L2.Sent[0].At, TimeSpan.FromSeconds(5.5), TimeSpan.FromSeconds(6.5));
        Assert.Contains(rig.Statuses, s => s.User == U1 && s.Active.Count == 0);
    }

    [Fact]
    public void Heartbeats_keep_a_held_key_relaying_and_one_that_says_nothing_is_held_ends_it()
    {
        using var rig = Team();
        rig.Press(U1, true);

        for (var i = 0; i < 400; i++)
        {
            if (i % 100 == 0)
            {
                rig.Beat(U1, held: true);
            }
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(5);
        var whileHeld = rig.L2.Sent.Count;
        Assert.True(whileHeld >= 395, $"relayed {whileHeld} of 400");

        rig.Beat(U1, held: false);
        for (var i = 0; i < 50; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }

        Assert.Equal(whileHeld, rig.L2.Sent.Count);
    }

    [Fact]
    public void A_new_config_unbinding_a_VC_stops_sending_to_it_from_the_next_tick()
    {
        using var rig = Team();
        rig.Press(U1, true);
        for (var i = 0; i < 20; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        var before = rig.L3.Sent.Count;

        rig.Engine.Configure(Config() with
        {
            Groups = new GroupsConfig { Attack = new GroupConfig { Channels = [A1, A2] } },
        });
        for (var i = 20; i < 40; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }

        Assert.Equal(before, rig.L3.Sent.Count);
        Assert.True(rig.L2.Sent.Count > before);
    }

    [Fact]
    public void A_player_who_leaves_voice_mid_broadcast_stops_being_heard()
    {
        using var rig = Team();
        rig.Press(U1, true);
        for (var i = 0; i < 20; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }

        rig.Seat(U1, null);
        var before = rig.L2.Sent.Count;
        for (var i = 20; i < 40; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }

        Assert.True(rig.L2.Sent.Count <= before + 1);
    }

    [Fact]
    public void A_packet_that_will_not_decode_is_dropped_and_counted_and_the_rest_go_through()
    {
        using var rig = new EngineRig(codecs: new Picky());
        rig.Seat(U1, A1);
        rig.Seat(U2, A2);
        rig.SignIn(U1);
        rig.Idle(1);

        rig.Press(U1, true);
        for (var i = 0; i < 30; i++)
        {
            rig.Step((rig.L1, U1, i == 10 ? [99, 10] : Packet(1, i)));
        }
        rig.Idle(10);

        Assert.Equal(Enumerable.Range(0, 30).Where(i => i != 10), Indexes(rig.L2));
        Assert.Equal(1, rig.Engine.Stats.DecodeErrors);
        Assert.Equal(0, rig.Engine.Stats.TickFaults);
    }

    [Fact]
    public void A_link_that_throws_on_send_does_not_stop_the_others()
    {
        var time = new ManualTimeSource();
        var gateway = new ThrowingGateway(time);
        using var engine = new AudioEngine(Config(), gateway, new FakeOpusCodecFactory(), time);
        gateway.Join(U1, A1);
        engine.Connected(U1, time.Now);
        engine.Tick(time.Now);
        engine.OnEdge(new BroadcastEdge(U1, BroadcastAction.Group, true, TriggerKind.AppKey, time.Now));
        for (var i = 0; i < 20; i++)
        {
            gateway.Source.Deliver(U1, Packet(1, i));
            engine.Tick(time.Now);
            time.Advance(Frame);
        }

        Assert.True(gateway.Healthy.Sent.Count >= 15);
        Assert.True(engine.Stats.SendErrors >= 15);
    }

    [Fact]
    public void Packets_past_the_queue_limit_are_dropped_and_counted_instead_of_piling_up()
    {
        using var rig = new EngineRig(options: new EngineOptions { MaxQueuedPackets = 5 });

        for (var i = 0; i < 20; i++)
        {
            rig.L1.Deliver(U1, Packet(1, i));
        }

        Assert.Equal(15, rig.Engine.Stats.PacketsDropped);
        rig.Engine.Tick(rig.Time.Now);
        // The queue has room again once the engine has drained it.
        rig.L1.Deliver(U1, Packet(1, 20));
        Assert.Equal(15, rig.Engine.Stats.PacketsDropped);
    }

    [Fact]
    public void A_packets_bytes_are_copied_when_it_arrives_because_the_library_reuses_its_buffer()
    {
        using var rig = Team();
        rig.Press(U1, true);
        var shared = new byte[] { 1, 0 };

        for (var i = 0; i < 20; i++)
        {
            shared[1] = (byte)i;
            rig.L1.Deliver(U1, shared);
            shared[1] = 255;
            rig.Engine.Tick(rig.Time.Now);
            rig.Time.Advance(Frame);
        }
        rig.Idle(10);

        Assert.Equal(Enumerable.Range(0, 20), Indexes(rig.L2));
    }

    [Fact]
    public void A_player_gone_quiet_is_forgotten_after_the_idle_time_unless_relaying()
    {
        using var rig = new EngineRig();
        rig.Seat(U1, A1);
        rig.Seat(U2, A2);
        rig.SignIn(U2);
        rig.Idle(1);
        rig.Step((rig.L1, U1, Packet(1, 0)));
        rig.Press(U2, true);
        rig.Step((rig.L2, U2, Packet(2, 0)));

        // U2 is still holding the key and the app is beating: they are relaying, so they stay.
        for (var i = 0; i < 700; i++)
        {
            if (i % 100 == 0)
            {
                rig.Beat(U2, held: true);
            }
            rig.Step();
        }

        // U1 is gone from the engine's buffers; U2, still relaying, is kept.
        Assert.Equal(1, rig.Engine.PlayerBuffers);
    }

    [Fact]
    public void The_engine_thread_ticks_on_its_own_and_stops_when_asked()
    {
        var time = new SystemTimeSource();
        var gateway = new FakeVoiceGateway(time);
        using var engine = new AudioEngine(Config(), gateway, new FakeOpusCodecFactory(), time);

        engine.Start();
        Assert.Throws<InvalidOperationException>(engine.Start);
        SpinWait.SpinUntil(() => engine.Stats.Ticks >= 10, TimeSpan.FromSeconds(5));
        engine.Stop();

        var ticks = engine.Stats.Ticks;
        Assert.InRange(ticks, 10, 200);
        Thread.Sleep(100);
        Assert.Equal(ticks, engine.Stats.Ticks);
    }

    [Fact]
    public void After_disposing_the_engine_hears_nothing_more()
    {
        var rig = Team();
        rig.Engine.Dispose();

        rig.L1.Deliver(U1, Packet(1, 0));
        rig.Seat(U1, null);

        Assert.Equal(0, rig.Engine.Stats.PacketsDropped);
    }

    private sealed class Picky : IOpusCodecFactory
    {
        private readonly FakeOpusCodecFactory _inner = new();

        public IOpusDecoder Decoder() => new PickyDecoder(_inner.Decoder());

        public IOpusEncoder Encoder(int bitrate) => _inner.Encoder(bitrate);

        private sealed class PickyDecoder(IOpusDecoder inner) : IOpusDecoder
        {
            public int Decode(ReadOnlySpan<byte> opus, Span<short> pcm) =>
                opus[0] == 99 ? throw new InvalidOperationException("corrupted packet") : inner.Decode(opus, pcm);

            public int Conceal(Span<short> pcm) => inner.Conceal(pcm);
        }
    }

    private sealed class ThrowingGateway : IVoiceGateway
    {
        public ThrowingGateway(ManualTimeSource time)
        {
            Source = new FakeVoiceChannelLink(A1, 1, time);
            Healthy = new FakeVoiceChannelLink(A2, 2, time);
            Links = [Source, new ThrowingLink(A3), Healthy];
        }

        public FakeVoiceChannelLink Source { get; }

        public FakeVoiceChannelLink Healthy { get; }

        public IReadOnlyList<IVoiceChannelLink> Links { get; }

        public event Action<UserId, ChannelId?>? PresenceChanged;

        public void Join(UserId user, ChannelId channel) => PresenceChanged?.Invoke(user, channel);
    }

    private sealed class ThrowingLink(ChannelId channel) : IVoiceChannelLink
    {
        public ulong BotUserId => 3;

        public ChannelId Channel { get; } = channel;

        public bool Ready => true;

        public event Action<OpusPacket>? PacketReceived
        {
            add { }
            remove { }
        }

        public void Send(ReadOnlyMemory<byte> opus) => throw new InvalidOperationException("the socket is gone");

        public void SetSpeaking(bool on) => throw new InvalidOperationException("the socket is gone");
    }
}
