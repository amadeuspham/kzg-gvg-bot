using Kzg.Core.Abstractions;
using Kzg.Core.Audio;

namespace Kzg.Core.Tests.Audio;

// Slot i is the i-th 20 ms frame of a player's stream, sent at t0 + 20 ms × i. Its packet carries
// i as its one byte, so a read shows which frame came out. At the 60 ms starting delay, slot i is
// due three ticks after it was sent. A relay started before any audio plays each frame as it is due,
// at live.
public class JitterBufferTests
{
    private static readonly TimeSpan F = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Preroll = TimeSpan.FromMilliseconds(300);
    private static readonly UserId Bob = new(2);

    private readonly FakeTimeSource _time = new();
    private readonly TimeSpan _t0;
    private readonly JitterBuffer _buffer = new();
    private ushort _seq0 = 100;
    private uint _ts0 = 48_000;

    public JitterBufferTests() => _t0 = _time.Now;

    private OpusPacket Packet(int slot, int? seq = null, TimeSpan? late = null) =>
        new(Bob, (ushort)(_seq0 + (seq ?? slot)), unchecked(_ts0 + ((uint)slot * 960)), new byte[] { (byte)slot },
            _t0 + (slot * F) + (late ?? TimeSpan.Zero));

    /// <summary>Moves to slot <paramref name="slot"/>'s send time, sending it unless told not to, then reads.</summary>
    private (int? Relay, FrameKind Kind) Step(int slot, bool send = true)
    {
        _time.Advance(_t0 + (slot * F) - _time.Now);
        if (send)
        {
            _buffer.Add(Packet(slot));
        }
        var relay = _buffer.Read(_time.Now);
        return (Id(relay), relay.Kind);
    }

    /// <summary>Starts the relay before any audio, so it plays at live.</summary>
    private void AtLive() => _buffer.Start(_t0 - Preroll);

    /// <summary>The ids of <paramref name="count"/> slots from <paramref name="first"/>: a packet's one byte wraps at 256.</summary>
    private static IEnumerable<int> Slots(int first, int count) => Enumerable.Range(first, count).Select(s => s % 256);

    private static int? Id(FrameRead read) => read.Kind == FrameKind.Frame ? read.Opus.Span[0] : null;

    [Fact]
    public void Frames_play_in_order_three_ticks_behind_arrival()
    {
        AtLive();
        List<int?> heard = [];
        for (var slot = 0; slot < 10; slot++)
        {
            heard.Add(Step(slot).Relay);
        }

        Assert.Equal([null, null, null, 0, 1, 2, 3, 4, 5, 6], heard);
        Assert.Equal(JitterBuffer.MinDelay, _buffer.Delay);
    }

    [Fact]
    public void Packets_out_of_order_within_the_delay_come_out_in_order()
    {
        AtLive();
        List<int?> heard = [];
        for (var slot = 0; slot < 14; slot++)
        {
            _time.Advance(_t0 + (slot * F) - _time.Now);
            if (slot == 5)
            {
                _buffer.Add(Packet(6, late: TimeSpan.FromMilliseconds(-15)));
                _buffer.Add(Packet(5));
            }
            else if (slot != 6)
            {
                _buffer.Add(Packet(slot));
            }
            heard.Add(Id(_buffer.Read(_time.Now)));
        }

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10], heard.Skip(3));
        Assert.Equal(JitterBuffer.MinDelay, _buffer.Delay);
    }

    [Fact]
    public void A_lost_packet_is_concealed()
    {
        AtLive();
        List<FrameKind> kinds = [];
        for (var slot = 0; slot < 10; slot++)
        {
            kinds.Add(Step(slot, send: slot != 4).Kind);
        }

        Assert.Equal([FrameKind.Frame, FrameKind.Missing, FrameKind.Frame], kinds.Skip(6).Take(3));
    }

    [Fact]
    public void Concealment_covers_three_frames_and_a_longer_loss_plays_silence()
    {
        AtLive();
        List<(int?, FrameKind)> heard = [];
        for (var slot = 0; slot < 16; slot++)
        {
            var step = Step(slot, send: slot is < 4 or > 8);
            heard.Add((step.Relay, step.Kind));
        }

        Assert.Equal(
            [(3, FrameKind.Frame), (null, FrameKind.Missing), (null, FrameKind.Missing), (null, FrameKind.Missing),
             (null, FrameKind.Silent), (null, FrameKind.Silent), (9, FrameKind.Frame)],
            heard.Skip(6).Take(7));
    }

    [Fact]
    public void A_pause_in_speech_plays_silence_not_concealment()
    {
        AtLive();
        // The sender skips slots 5 to 7 with no gap in its sequence numbers: it had nothing to say.
        List<FrameKind> kinds = [];
        for (var slot = 0; slot < 14; slot++)
        {
            _time.Advance(_t0 + (slot * F) - _time.Now);
            if (slot < 5)
            {
                _buffer.Add(Packet(slot));
            }
            else if (slot >= 8)
            {
                _buffer.Add(Packet(slot, seq: slot - 3));
            }
            kinds.Add(_buffer.Read(_time.Now).Kind);
        }

        Assert.Equal([FrameKind.Frame, FrameKind.Silent, FrameKind.Silent, FrameKind.Silent, FrameKind.Frame], kinds.Skip(7).Take(5));
    }

    [Fact]
    public void After_speech_stops_at_most_three_frames_are_concealed()
    {
        AtLive();
        List<FrameKind> kinds = [];
        for (var slot = 0; slot < 15; slot++)
        {
            kinds.Add(Step(slot, send: slot < 5).Kind);
        }

        Assert.Equal([FrameKind.Frame, FrameKind.Missing, FrameKind.Missing, FrameKind.Missing, FrameKind.Silent, FrameKind.Silent], kinds.Skip(7).Take(6));
    }

    [Fact]
    public void A_late_packet_grows_the_delay_to_its_lateness_and_ten_calm_seconds_shrink_it()
    {
        for (var slot = 0; slot < 5; slot++)
        {
            Step(slot);
        }
        _time.Advance(_t0 + (9 * F) - _time.Now);
        _buffer.Add(Packet(5, late: 4 * F));
        var underrun = _time.Now;

        Assert.Equal(4 * F, _buffer.Delay);

        var slotAt = 9;
        while (_t0 + (slotAt * F) < underrun + JitterBuffer.ShrinkAfter - F)
        {
            Step(slotAt++);
        }
        Assert.Equal(4 * F, _buffer.Delay);

        _time.Advance(underrun + JitterBuffer.ShrinkAfter - _time.Now);
        _buffer.Read(_time.Now);
        Assert.Equal(JitterBuffer.MinDelay, _buffer.Delay);
    }

    [Fact]
    public void Under_a_longer_delay_frames_play_that_much_further_behind()
    {
        AtLive();
        Step(0);
        // 70 ms late: whole frames, so 80 ms.
        _buffer.Add(Packet(1, late: TimeSpan.FromMilliseconds(70)));
        List<int?> heard = [];
        for (var slot = 2; slot < 12; slot++)
        {
            heard.Add(Step(slot).Relay);
        }

        Assert.Equal(4 * F, _buffer.Delay);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], heard.SkipWhile(h => h is null));
        Assert.Equal(4, heard.IndexOf(0) + 2);
    }

    [Fact]
    public void No_delay_grows_past_240_ms()
    {
        AtLive();
        Step(0);
        _buffer.Add(Packet(1, late: TimeSpan.FromMilliseconds(400)));

        Assert.Equal(TimeSpan.FromMilliseconds(240), _buffer.Delay);
        Assert.Equal(JitterBuffer.MaxDelay, _buffer.Delay);
    }

    [Theory]
    [InlineData(190, 200)]
    [InlineData(200, 200)]
    [InlineData(215, 220)]
    [InlineData(240, 240)]
    public void A_burst_held_back_on_the_way_loses_no_frame_and_the_delay_grows_to_hold_it(int holdMs, int delayMs)
    {
        var hold = TimeSpan.FromMilliseconds(holdMs);
        AtLive();
        // Unbroken speech; from slot 50, every packet sent within the hold arrives at once at its end.
        var relayed = Play(200, slot => slot >= 50 && (slot - 50) * F < hold ? hold - ((slot - 50) * F) : TimeSpan.Zero);

        Assert.Equal(Enumerable.Range(0, 200), relayed);
        Assert.Equal(TimeSpan.FromMilliseconds(delayMs), _buffer.Delay);
    }

    [Fact]
    public void Bursts_of_190_to_240_ms_lose_no_frame_and_ten_calm_seconds_bring_the_delay_back_to_60_ms()
    {
        AtLive();
        // A burst every 2 s, each longer than the last, then calm.
        int[] holds = [190, 200, 210, 220, 230, 240];
        TimeSpan Late(int slot)
        {
            var burst = (slot / 100) - 1;
            var hold = burst >= 0 && burst < holds.Length ? TimeSpan.FromMilliseconds(holds[burst]) : TimeSpan.Zero;
            var into = (slot % 100) * F;
            return into < hold ? hold - into : TimeSpan.Zero;
        }
        List<TimeSpan> delays = [];
        var relayed = Play(700, Late, delays.Add);

        Assert.Equal(Slots(0, 700), relayed);
        Assert.Equal(TimeSpan.FromMilliseconds(200), delays[150]);
        Assert.Equal(JitterBuffer.MaxDelay, delays[699]);
        Assert.True(delays.Max() <= JitterBuffer.MaxDelay);

        // Ten calm seconds after the last burst, the delay sizes back down to what calm needs.
        var lastBurst = _t0 + (600 * F) + JitterBuffer.MaxDelay;
        for (var slot = 700; _t0 + (slot * F) < lastBurst + JitterBuffer.ShrinkAfter; slot++)
        {
            Step(slot);
        }
        Assert.Equal(JitterBuffer.MaxDelay, _buffer.Delay);
        _time.Advance(lastBurst + JitterBuffer.ShrinkAfter - _time.Now);
        _buffer.Read(_time.Now);
        Assert.Equal(JitterBuffer.MinDelay, _buffer.Delay);
    }

    [Fact]
    public void Bursts_that_keep_coming_keep_the_delay_that_holds_them()
    {
        AtLive();
        // A 200 ms burst every 2 s for 30 s: the delay grows at the second, as the first opens the stream,
        // and stays.
        var hold = TimeSpan.FromMilliseconds(200);
        List<TimeSpan> delays = [];
        var relayed = Play(1500, slot => (slot % 100) * F is var into && into < hold ? hold - into : TimeSpan.Zero, delays.Add);

        Assert.Equal(Slots(0, 1500), relayed);
        Assert.All(delays.Skip(120), d => Assert.Equal(hold, d));
    }

    [Fact]
    public void A_shrink_during_speech_drops_nothing_and_the_relay_catches_up_at_the_next_gap()
    {
        AtLive();
        Play(20, slot => slot == 10 ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMilliseconds(200), _buffer.Delay);

        // Speech goes on through the shrink, ten seconds on: every frame still plays, in order.
        List<int> played = [];
        for (var slot = 30; slot < 580; slot++)
        {
            if (Step(slot).Relay is { } id)
            {
                played.Add(id);
            }
        }
        Assert.Equal(JitterBuffer.MinDelay, _buffer.Delay);
        Assert.Equal(Slots(played[0], played.Count), played);
        // It plays on 140 ms behind live, the difference the shrink made, until the next gap.
        Assert.Equal(569 % 256, played[^1]);

        // A pause, then the next talkspurt plays at live.
        for (var slot = 580; slot < 640; slot++)
        {
            _time.Advance(_t0 + (slot * F) - _time.Now);
            if (slot >= 610)
            {
                _buffer.Add(Packet(slot, seq: slot - 30));
            }
            _buffer.Read(_time.Now);
        }
        Assert.Equal(TimeSpan.Zero, _buffer.RelayLag(_time.Now));
    }

    [Fact]
    public void A_restart_for_a_new_stream_keeps_the_delay_and_ends_the_relay()
    {
        AtLive();
        Play(20, slot => slot == 10 ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMilliseconds(160), _buffer.Delay);

        _buffer.Restart();

        Assert.Equal(TimeSpan.FromMilliseconds(160), _buffer.Delay);
        Assert.False(_buffer.Relaying);
        Assert.Equal(0, _buffer.Count);
        Assert.Equal(FrameKind.Silent, _buffer.Read(_time.Now).Kind);
        Assert.True(_buffer.RelayPassed(_time.Now));
    }

    /// <summary>
    /// Sends slots 0 to <paramref name="slots"/> − 1, each arriving <paramref name="late"/> after it was sent,
    /// and reads once a tick as the engine does: what arrived by the tick first, then the frame. The ticks go
    /// on until the last frame is due. Returns the frames the relay played, in order.
    /// </summary>
    private List<int> Play(int slots, Func<int, TimeSpan> late, Action<TimeSpan>? delay = null)
    {
        var arrivals = Enumerable.Range(0, slots).Select(s => (Slot: s, At: _t0 + (s * F) + late(s))).OrderBy(a => a.At).ThenBy(a => a.Slot).ToList();
        List<int> relayed = [];
        var next = 0;
        var ticks = slots + (int)(JitterBuffer.MaxDelay / F) + 1;
        for (var tick = 0; tick < ticks; tick++)
        {
            _time.Advance(_t0 + (tick * F) - _time.Now);
            for (; next < arrivals.Count && arrivals[next].At <= _time.Now; next++)
            {
                _buffer.Add(Packet(arrivals[next].Slot, late: late(arrivals[next].Slot)));
            }
            if (Id(_buffer.Read(_time.Now)) is { } id)
            {
                relayed.Add(id);
            }
            if (tick < slots)
            {
                delay?.Invoke(_buffer.Delay);
            }
        }
        return relayed;
    }

    [Fact]
    public void The_relay_starts_back_by_the_preroll_so_the_first_word_survives_a_late_press()
    {
        // Speech starts at slot 0; the key goes down 200 ms later, and routing reads back 300 ms.
        for (var slot = 0; slot < 10; slot++)
        {
            Step(slot);
        }
        var press = _t0 + (10 * F);
        _buffer.Start(press - Preroll);

        List<int?> relayed = [];
        for (var slot = 10; slot < 20; slot++)
        {
            relayed.Add(Step(slot).Relay);
        }

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], relayed);
        Assert.True(_buffer.Relaying);
    }

    [Fact]
    public void In_unbroken_speech_the_relay_plays_what_arrived_a_preroll_ago()
    {
        for (var slot = 0; slot < 30; slot++)
        {
            Step(slot);
        }
        _buffer.Start(_time.Now + F - Preroll);
        for (var slot = 30; slot < 60; slot++)
        {
            // Live plays what arrived 60 ms ago, the relay what arrived 300 ms ago.
            var step = Step(slot);
            Assert.Equal(slot - 15, step.Relay);
        }

        Assert.Equal(Preroll - JitterBuffer.MinDelay, _buffer.RelayLag(_time.Now));
    }

    [Fact]
    public void The_relay_catches_up_to_live_at_the_next_gap_in_speech()
    {
        for (var slot = 0; slot < 30; slot++)
        {
            Step(slot);
        }
        _buffer.Start(_time.Now + F - Preroll);
        for (var slot = 30; slot < 60; slot++)
        {
            Step(slot);
        }

        // A pause, then the next talkspurt, sequence numbers unbroken.
        List<(int? Live, int? Relay)> heard = [];
        for (var slot = 60; slot < 100; slot++)
        {
            _time.Advance(_t0 + (slot * F) - _time.Now);
            if (slot >= 80)
            {
                _buffer.Add(Packet(slot, seq: slot - 20));
            }
            heard.Add((slot - 3, Id(_buffer.Read(_time.Now))));
        }

        // Live is three ticks behind the send.
        var resumed = heard.Where(h => h.Live >= 80).ToList();
        Assert.NotEmpty(resumed);
        Assert.All(resumed, h => Assert.Equal(h.Live, h.Relay));
        Assert.Equal(80, resumed[0].Relay);
        Assert.Equal(TimeSpan.Zero, _buffer.RelayLag(_time.Now));
    }

    [Fact]
    public void A_relay_more_than_the_preroll_and_60_ms_behind_jumps_to_live()
    {
        for (var slot = 0; slot < 30; slot++)
        {
            Step(slot);
        }
        _buffer.Start(_time.Now + F - Preroll);
        Step(30);

        // The engine stalls for 200 ms while packets keep coming.
        for (var slot = 31; slot < 41; slot++)
        {
            _buffer.Add(Packet(slot));
        }
        // Live is three ticks behind the send.
        Assert.Equal(38, Step(41).Relay);
    }

    [Fact]
    public void The_buffer_holds_one_point_two_seconds_and_a_start_further_back_reads_what_is_held()
    {
        for (var slot = 0; slot < 150; slot++)
        {
            Step(slot);
        }

        // 1.2 s up to live, and the three frames not due yet.
        Assert.Equal(63, _buffer.Count);

        // A start further back than the buffer reaches begins at its oldest frame, and stays there.
        _buffer.Start(_t0);
        // Live is three slots behind the send.
        Assert.Equal(150 - 3 - 59, Step(150).Relay);
        Assert.Equal(151 - 3 - 59, Step(151).Relay);
    }

    [Fact]
    public void A_relay_started_before_any_audio_plays_the_first_frame_that_comes()
    {
        _buffer.Start(_t0 - Preroll);
        Assert.Equal(FrameKind.Silent, _buffer.Read(_t0).Kind);
        Assert.Null(_buffer.RelayLag(_t0));

        List<int?> relayed = [];
        for (var slot = 0; slot < 8; slot++)
        {
            relayed.Add(Step(slot).Relay);
        }

        Assert.Equal([null, null, null, 0, 1, 2, 3, 4], relayed);
    }

    [Fact]
    public void Stopping_the_relay_silences_its_head()
    {
        Step(0);
        _buffer.Start(_t0);
        _buffer.Stop();

        Assert.False(_buffer.Relaying);
        Assert.Equal(FrameKind.Silent, _buffer.Read(_time.Now).Kind);
        Assert.Null(_buffer.RelayLag(_time.Now));
        for (var slot = 1; slot < 5; slot++)
        {
            Assert.Null(Step(slot).Relay);
        }
    }

    [Fact]
    public void A_repeated_or_empty_packet_changes_nothing()
    {
        Step(0);
        _buffer.Add(Packet(0));
        _buffer.Add(new OpusPacket(Bob, 999, 1, ReadOnlyMemory<byte>.Empty, _time.Now));

        Assert.Equal(1, _buffer.Count);
    }

    [Fact]
    public void The_buffer_keeps_its_own_copy_of_each_packet()
    {
        AtLive();
        var data = new byte[] { 7 };
        _buffer.Add(new OpusPacket(Bob, 1, 1, data, _time.Now));
        data[0] = 99;

        _time.Advance(TimeSpan.FromMilliseconds(60));
        Assert.Equal(7, Id(_buffer.Read(_time.Now)));
    }

    [Fact]
    public void Sequence_and_timestamp_wrap_around_unnoticed()
    {
        AtLive();
        (_seq0, _ts0) = (65_530, uint.MaxValue - (960 * 5));
        List<int?> heard = [];
        for (var slot = 0; slot < 15; slot++)
        {
            heard.Add(Step(slot).Relay);
        }

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], heard.Skip(3));
    }

    [Fact]
    public void A_stream_that_restarts_its_timestamps_plays_from_its_new_start()
    {
        AtLive();
        for (var slot = 0; slot < 100; slot++)
        {
            Step(slot);
        }

        // Next in sequence, but stamped far back: the sender began a fresh stream.
        _ts0 -= 960 * 1000;
        var restart = _time.Now + F;
        List<int?> heard = [];
        for (var i = 0; i < 8; i++)
        {
            _time.Advance(restart + (i * F) - _time.Now);
            _buffer.Add(new OpusPacket(Bob, (ushort)(_seq0 + 100 + i), _ts0 + ((uint)i * 960), new byte[] { (byte)(200 + i) }, _time.Now));
            heard.Add(Id(_buffer.Read(_time.Now)));
        }

        Assert.Equal([null, null, null, 200, 201, 202, 203, 204], heard);
    }

    [Fact]
    public void A_packet_older_than_the_buffer_is_dropped()
    {
        for (var slot = 0; slot < 100; slot++)
        {
            Step(slot);
        }
        var count = _buffer.Count;

        _buffer.Add(Packet(10, late: TimeSpan.FromMilliseconds(1800)));

        Assert.Equal(count, _buffer.Count);
    }

    [Fact]
    public void A_long_silence_re_anchors_on_the_next_packet()
    {
        AtLive();
        Step(0);
        // Ten seconds later a packet comes, its timestamp only 1 s on: the sender's clock is not ours.
        _time.Advance(TimeSpan.FromSeconds(10));
        _buffer.Add(new OpusPacket(Bob, (ushort)(_seq0 + 1), _ts0 + (50 * 960), new byte[] { 50 }, _time.Now));

        _time.Advance(TimeSpan.FromMilliseconds(60));
        Assert.Equal(50, Id(_buffer.Read(_time.Now)));
    }

    [Fact]
    public void A_start_takes_nothing_that_arrived_before_it_even_when_every_packet_comes_late()
    {
        // Every packet is 30 ms late, so frame s arrives at t0 + 30 ms + 20 ms × s.
        var late = TimeSpan.FromMilliseconds(30);
        var press = _t0 + (40 * F);
        List<int?> relayed = [];
        for (var slot = 0; slot < 70; slot++)
        {
            _time.Advance(_t0 + (slot * F) + late - _time.Now);
            _buffer.Add(Packet(slot, late: late));
            if (_time.Now >= press && !_buffer.Relaying)
            {
                _buffer.Start(press - Preroll);
            }
            relayed.Add(Id(_buffer.Read(_time.Now)));
        }

        // Press − 300 ms is t0 + 500 ms: frame 23 arrived at 490 ms, frame 24 at 510 ms.
        var first = relayed.First(r => r is not null);
        Assert.Equal(24, first);
        Assert.Equal(Enumerable.Range(24, relayed.Count(r => r is not null)), relayed.Where(r => r is not null).Cast<int>());
    }

    [Fact]
    public void A_start_far_back_plays_on_unbroken_and_catches_up_at_the_next_pause()
    {
        for (var slot = 0; slot < 60; slot++)
        {
            Step(slot);
        }
        // 600 ms back: further than the pre-roll, and the relay must not jump.
        _buffer.Start(_time.Now + F - TimeSpan.FromMilliseconds(600));
        List<int?> relayed = [];
        for (var slot = 60; slot < 100; slot++)
        {
            relayed.Add(Step(slot).Relay);
        }
        Assert.Equal(Enumerable.Range(30, 40).Cast<int?>(), relayed);

        // A pause, then more speech: the relay comes back to live.
        for (var slot = 100; slot < 140; slot++)
        {
            _time.Advance(_t0 + (slot * F) - _time.Now);
            if (slot >= 120)
            {
                _buffer.Add(Packet(slot, seq: slot - 20));
            }
            _buffer.Read(_time.Now);
        }
        Assert.Equal(TimeSpan.Zero, _buffer.RelayLag(_time.Now));
    }

    [Fact]
    public void A_stall_on_top_of_a_far_start_still_jumps_to_live()
    {
        for (var slot = 0; slot < 60; slot++)
        {
            Step(slot);
        }
        _buffer.Start(_time.Now + F - TimeSpan.FromMilliseconds(600));
        Step(60);
        for (var slot = 61; slot < 66; slot++)
        {
            _buffer.Add(Packet(slot));
        }

        Assert.Equal(63, Step(66).Relay);
    }

    [Fact]
    public void A_drain_plays_up_to_the_last_frame_that_arrived_by_the_release()
    {
        for (var slot = 0; slot < 40; slot++)
        {
            Step(slot);
        }
        _buffer.Start(_time.Now + F - Preroll);
        for (var slot = 40; slot < 60; slot++)
        {
            Step(slot);
        }
        var release = _t0 + (60 * F);

        // Speech goes on after the release; only what came by it is the player's to send.
        List<int?> drained = [];
        for (var slot = 60; slot < 100 && !_buffer.RelayPassed(release); slot++)
        {
            drained.Add(Step(slot).Relay);
        }

        Assert.Equal(60, drained[^1]);
        Assert.Equal(Enumerable.Range(45, 16).Cast<int?>(), drained);
    }

    [Fact]
    public void Without_a_relay_or_any_audio_there_is_nothing_to_drain()
    {
        Assert.True(_buffer.RelayPassed(_time.Now));
        _buffer.Start(_t0);
        Assert.True(_buffer.RelayPassed(_time.Now));

        Step(0);
        Assert.False(_buffer.RelayPassed(_time.Now));
    }

    [Fact]
    public void Nothing_plays_before_any_audio()
    {
        AtLive();
        Assert.Equal(FrameKind.Silent, _buffer.Read(_time.Now).Kind);
    }
}
