using Kzg.Core.Abstractions;
using Kzg.Core.Testing;

namespace Kzg.Core.Tests.Testing;

public sealed class FakeVoiceGatewayTests
{
    private static readonly ChannelId A1 = new(101), A2 = new(102);
    private static readonly UserId Alice = new(1), Bob = new(2);

    private readonly ManualTimeSource _time = new();

    [Fact]
    public void Links_are_one_per_channel_and_found_by_channel()
    {
        var gateway = new FakeVoiceGateway(_time);
        var first = gateway.AddLink(A1);
        var second = gateway.AddLink(A2, botUserId: 77);

        Assert.Equal([first, second], gateway.Links);
        Assert.Same(second, gateway.Link(A2));
        Assert.Equal(77UL, second.BotUserId);
        Assert.NotEqual(first.BotUserId, second.BotUserId);
        Assert.Throws<ArgumentException>(() => gateway.AddLink(A1));
        Assert.Throws<KeyNotFoundException>(() => gateway.Link(new ChannelId(5)));
    }

    [Fact]
    public void Presence_changes_are_raised_to_whoever_listens()
    {
        var gateway = new FakeVoiceGateway(_time);
        List<(UserId, ChannelId?)> seen = [];
        gateway.PresenceChanged += (user, channel) => seen.Add((user, channel));

        gateway.Join(Alice, A1);
        gateway.Join(Alice, null);

        Assert.Equal([(Alice, (ChannelId?)A1), (Alice, null)], seen);
    }

    [Fact]
    public void A_delivered_frame_is_a_packet_that_carries_the_sequence_timestamp_and_arrival_a_sender_writes()
    {
        var link = new FakeVoiceGateway(_time).AddLink(A1);
        List<OpusPacket> received = [];
        link.PacketReceived += received.Add;

        link.Deliver(Alice, new byte[] { 1 });
        _time.Advance(TimeSpan.FromMilliseconds(20));
        link.Deliver(Alice, new byte[] { 2 });
        link.Deliver(Bob, new byte[] { 3 });

        Assert.Equal([Alice, Alice, Bob], received.Select(p => p.User));
        Assert.Equal((ushort)(received[0].Seq + 1), received[1].Seq);
        Assert.Equal(received[0].RtpTs + (uint)OpusFormat.FrameSamples, received[1].RtpTs);
        Assert.Equal(_time.Now - TimeSpan.FromMilliseconds(20), received[0].Arrival);
        Assert.Equal(_time.Now, received[1].Arrival);
        // Each user counts on their own.
        Assert.Equal(received[0].Seq, received[2].Seq);
    }

    [Fact]
    public void A_pause_jumps_the_timestamp_and_not_the_sequence()
    {
        var link = new FakeVoiceGateway(_time).AddLink(A1);
        List<OpusPacket> received = [];
        link.PacketReceived += received.Add;

        link.Deliver(Alice, new byte[] { 1 });
        link.Pause(Alice, 10);
        link.Deliver(Alice, new byte[] { 2 });

        Assert.Equal((ushort)(received[0].Seq + 1), received[1].Seq);
        Assert.Equal(received[0].RtpTs + (11u * OpusFormat.FrameSamples), received[1].RtpTs);
    }

    [Fact]
    public void A_sequence_number_wraps_as_a_real_one_does()
    {
        var link = new FakeVoiceGateway(_time).AddLink(A1);
        List<OpusPacket> received = [];
        link.PacketReceived += received.Add;

        for (var i = 0; i < 70_000; i++)
        {
            link.Deliver(Alice, new byte[] { 1 });
        }

        Assert.Equal(received[0].Seq, received[65_536].Seq);
    }

    [Fact]
    public void What_the_engine_sends_is_recorded_with_its_time_and_copied()
    {
        var link = new FakeVoiceGateway(_time).AddLink(A1);
        var buffer = new byte[] { 7, 8 };

        link.Send(buffer);
        buffer[0] = 0;
        _time.Advance(TimeSpan.FromMilliseconds(20));
        link.SetSpeaking(true);
        link.Send(new byte[] { 9 });

        var sent = link.Sent;
        Assert.Equal([new byte[] { 7, 8 }, new byte[] { 9 }], sent.Select(f => f.Opus));
        Assert.Equal(TimeSpan.FromMilliseconds(20), sent[1].At - sent[0].At);
        var speaking = Assert.Single(link.SpeakingChanges);
        Assert.True(speaking.On);

        link.ClearSent();
        Assert.Empty(link.Sent);
        Assert.Empty(link.SpeakingChanges);
    }

    [Fact]
    public void A_frame_sent_to_a_link_that_is_not_ready_is_counted()
    {
        var link = new FakeVoiceGateway(_time).AddLink(A1);

        link.Send(new byte[] { 1 });
        link.Ready = false;
        link.Send(new byte[] { 1 });

        Assert.Equal(1, link.SentWhileNotReady);
    }

    [Fact]
    public async Task Play_delivers_every_frame_in_order_at_the_pace_of_the_clock()
    {
        var real = new SystemTimeSource();
        var link = new FakeVoiceGateway(real).AddLink(A1);
        List<OpusPacket> received = [];
        link.PacketReceived += p =>
        {
            lock (received)
            {
                received.Add(p);
            }
        };
        byte[][] frames = [[1], [2], [3], [4], [5]];

        await link.PlayAsync(Alice, frames);

        Assert.Equal(frames, received.Select(p => p.Data.ToArray()));
        var span = received[^1].Arrival - received[0].Arrival;
        Assert.InRange(span, TimeSpan.FromMilliseconds(70), TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task Play_stops_when_cancelled()
    {
        var link = new FakeVoiceGateway(new SystemTimeSource()).AddLink(A1);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => link.PlayAsync(Alice, [.. Enumerable.Repeat(new byte[] { 1 }, 500)], cancel.Token));
    }

    [Fact]
    public void The_manual_clock_moves_only_when_advanced()
    {
        var start = _time.Now;

        Assert.Equal(start, _time.Now);
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(start + TimeSpan.FromSeconds(2), _time.Now);
        Assert.True(start > TimeSpan.Zero);
    }

    [Fact]
    public void The_fake_codec_decodes_to_the_first_byte_conceals_and_encodes_the_first_sample()
    {
        var codecs = new FakeOpusCodecFactory();
        var pcm = new short[OpusFormat.FrameShorts];

        Assert.Equal(OpusFormat.FrameSamples, codecs.Decoder().Decode(new byte[] { 3, 9 }, pcm));
        Assert.All(pcm, s => Assert.Equal(300, s));
        Assert.Equal(OpusFormat.FrameSamples, codecs.Decoder().Conceal(pcm));
        Assert.All(pcm, s => Assert.Equal(FakeOpusCodecFactory.Concealed, s));

        Array.Fill(pcm, (short)-1234);
        var packet = new byte[8];
        var length = codecs.Encoder(64_000).Encode(pcm, packet);
        Assert.Equal(FakeOpusCodecFactory.Encoded(-1234), packet[..length]);
    }
}
