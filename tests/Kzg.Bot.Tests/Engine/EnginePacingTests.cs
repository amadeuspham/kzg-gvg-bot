using Kzg.Bot.Engine;
using Kzg.Core.Abstractions;
using Kzg.Core.Testing;
using static Kzg.Bot.Tests.Engine.EngineRig;

namespace Kzg.Bot.Tests.Engine;

// The tick loop paced like Gengfu's VoiceSender: every 20 ms by an injected clock and sleep.
public sealed class EnginePacingTests
{
    private static AudioEngine Engine(ManualTimeSource time, Action<TimeSpan> sleep, IVoiceGateway? gateway = null) =>
        new(Config(), gateway ?? new FakeVoiceGateway(time), new FakeOpusCodecFactory(), time, sleep: sleep);

    /// <summary>A gateway whose one link breaks the engine's tick the first time it is asked if it is ready.</summary>
    private sealed class BreakingGateway : IVoiceGateway
    {
        private readonly BreakingLink _link = new();

        public IReadOnlyList<IVoiceChannelLink> Links => [_link];

        public event Action<UserId, ChannelId?> PresenceChanged = delegate { };

        private sealed class BreakingLink : IVoiceChannelLink
        {
            private bool _broken;

            public ulong BotUserId => 1;

            public ChannelId Channel => A1;

            public bool Ready
            {
                get
                {
                    if (!_broken)
                    {
                        _broken = true;
                        throw new InvalidOperationException("the link broke");
                    }
                    return true;
                }
            }

            public event Action<OpusPacket> PacketReceived = delegate { };

            public void Send(ReadOnlyMemory<byte> opus)
            {
            }

            public void SetSpeaking(bool on)
            {
            }
        }
    }

    [Fact]
    public void Ticks_come_every_20_ms_by_the_clock()
    {
        var time = new ManualTimeSource();
        var start = time.Now;
        using var stop = new CancellationTokenSource();
        using var engine = Engine(time, wait =>
        {
            time.Advance(wait);
            if (time.Now - start >= TimeSpan.FromSeconds(1))
            {
                stop.Cancel();
            }
        });

        engine.Run(stop.Token);

        Assert.Equal(50, engine.Stats.Ticks);
        Assert.Equal(0, engine.Stats.Resyncs);
    }

    [Fact]
    public void A_short_stall_is_caught_up_tick_by_tick_so_no_slot_is_lost()
    {
        var time = new ManualTimeSource();
        var start = time.Now;
        var sleeps = 0;
        using var stop = new CancellationTokenSource();
        using var engine = Engine(time, wait =>
        {
            time.Advance(wait);
            if (++sleeps == 10)
            {
                time.Advance(TimeSpan.FromMilliseconds(80));
            }
            if (time.Now - start >= TimeSpan.FromSeconds(1))
            {
                stop.Cancel();
            }
        });

        engine.Run(stop.Token);

        Assert.Equal(50, engine.Stats.Ticks);
        Assert.Equal(0, engine.Stats.Resyncs);
    }

    [Fact]
    public void A_long_stall_is_not_made_up_in_a_burst_the_slots_restart_from_now()
    {
        var time = new ManualTimeSource();
        var start = time.Now;
        var sleeps = 0;
        using var stop = new CancellationTokenSource();
        using var engine = Engine(time, wait =>
        {
            time.Advance(wait);
            if (++sleeps == 10)
            {
                time.Advance(TimeSpan.FromSeconds(1));
            }
            if (time.Now - start >= TimeSpan.FromSeconds(3))
            {
                stop.Cancel();
            }
        });

        engine.Run(stop.Token);

        Assert.Equal(1, engine.Stats.Resyncs);
        // 2 s of ticks, not 3: the stalled second's slots are skipped.
        Assert.InRange(engine.Stats.Ticks, 98, 102);
    }

    [Fact]
    public void A_tick_that_throws_is_counted_and_the_loop_goes_on()
    {
        var time = new ManualTimeSource();
        var start = time.Now;
        using var stop = new CancellationTokenSource();
        using var engine = Engine(time, wait =>
        {
            time.Advance(wait);
            if (time.Now - start >= TimeSpan.FromSeconds(1))
            {
                stop.Cancel();
            }
        }, new BreakingGateway());

        engine.Run(stop.Token);

        Assert.Equal(1, engine.Stats.TickFaults);
        Assert.True(engine.Stats.Ticks >= 45);
    }

    [Fact]
    public void Cancelling_before_the_first_tick_runs_nothing()
    {
        var time = new ManualTimeSource();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        using var engine = Engine(time, _ => { });

        engine.Run(stop.Token);

        Assert.Equal(0, engine.Stats.Ticks);
    }
}
