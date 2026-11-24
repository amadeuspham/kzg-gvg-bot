using Kzg.Core.Abstractions;

namespace Kzg.Core.Tests.Abstractions;

public class OpusPacketTests
{
    [Fact]
    public void A_frame_is_20_ms_of_48_kHz_stereo()
    {
        Assert.Equal(960, OpusFormat.FrameSamples);
        Assert.Equal(1920, OpusFormat.FrameShorts);
    }

    [Fact]
    public void A_packet_keeps_what_the_library_handed_over()
    {
        var time = new FakeTimeSource();
        byte[] data = [0xF8, 0xFF, 0xFE];

        var packet = new OpusPacket(new UserId(5), ushort.MaxValue, uint.MaxValue, data, time.Now);

        Assert.Equal(new UserId(5), packet.User);
        Assert.Equal(ushort.MaxValue, packet.Seq);
        Assert.Equal(uint.MaxValue, packet.RtpTs);
        Assert.Equal(data, packet.Data.ToArray());
        Assert.Equal(TimeSpan.FromHours(5), packet.Arrival);
    }

    [Fact]
    public void An_edge_records_who_what_and_when()
    {
        var edge = new BroadcastEdge(new UserId(5), BroadcastAction.OpenMic, true, TriggerKind.AppKey, TimeSpan.FromSeconds(3));

        Assert.Equal(edge with { }, edge);
        Assert.NotEqual(edge with { On = false }, edge);
    }
}
