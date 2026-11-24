using Kzg.Discord.Voice;

namespace Kzg.Discord.Tests;

public sealed class RtpClockTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromHours(5);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Frames_every_20_ms_are_960_samples_apart_and_one_sequence_number_apart()
    {
        var clock = new RtpClock(new Random(1));
        var first = clock.Next(T0);

        for (var i = 1; i <= 50; i++)
        {
            var (sequence, timestamp) = clock.Next(T0 + Ms(20 * i));
            Assert.Equal((ushort)(first.Sequence + i), sequence);
            Assert.Equal(first.Timestamp + (960u * (uint)i), timestamp);
        }
    }

    [Fact]
    public void A_tick_that_runs_a_little_early_or_late_does_not_make_the_timestamp_drift()
    {
        var clock = new RtpClock(new Random(2));
        var first = clock.Next(T0);
        var jitter = new[] { 3.0, -4.0, 7.0, -6.0, 5.0, -2.0 };

        for (var i = 1; i <= 300; i++)
        {
            var (_, timestamp) = clock.Next(T0 + Ms((20 * i) + jitter[i % jitter.Length]));
            Assert.Equal(first.Timestamp + (960u * (uint)i), timestamp);
        }
    }

    [Fact]
    public void A_pause_shows_in_the_timestamp_but_not_the_sequence()
    {
        var clock = new RtpClock(new Random(3));
        var before = clock.Next(T0);

        // 1 s of nothing, then a new burst.
        var after = clock.Next(T0 + Ms(1000));

        Assert.Equal((ushort)(before.Sequence + 1), after.Sequence);
        Assert.Equal(before.Timestamp + (960u * 50), after.Timestamp);
    }

    [Fact]
    public void A_burst_after_a_pause_keeps_the_frame_rate()
    {
        var clock = new RtpClock(new Random(4));
        clock.Next(T0);
        var start = clock.Next(T0 + Ms(2000));

        var next = clock.Next(T0 + Ms(2020));

        Assert.Equal(start.Timestamp + 960, next.Timestamp);
    }

    [Fact]
    public void Two_packets_never_share_a_timestamp_even_when_the_engine_catches_up_in_a_hurry()
    {
        var clock = new RtpClock(new Random(5));
        var seen = new HashSet<uint> { clock.Next(T0).Timestamp };

        // Five frames in the same millisecond after a stall.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(seen.Add(clock.Next(T0 + Ms(20)).Timestamp));
        }
    }

    [Fact]
    public void The_numbers_wrap_without_a_fault()
    {
        // A start near the top of both ranges: the first Random values are what the clock starts from.
        var clock = new RtpClock(new MaxRandom());
        var first = clock.Next(T0);

        var second = clock.Next(T0 + Ms(20));

        Assert.Equal((ushort)(first.Sequence + 1), second.Sequence);
        Assert.Equal(unchecked(first.Timestamp + 960u), second.Timestamp);
        // The sequence started at 65535 and the first packet is the next one on, 0.
        Assert.Equal((ushort)0, first.Sequence);
        Assert.True(second.Timestamp < first.Timestamp);
    }

    private sealed class MaxRandom : Random
    {
        public override int Next(int maxValue) => maxValue - 1;

        public override long NextInt64(long maxValue) => maxValue - 1;
    }
}
