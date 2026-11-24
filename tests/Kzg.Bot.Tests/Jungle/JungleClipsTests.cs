using Kzg.Audio;
using Kzg.Bot.Fake;
using Kzg.Bot.Jungle;
using Kzg.Bot.Tests.Hosting;
using Kzg.Core.Abstractions;
using Kzg.Core.Jungle;

namespace Kzg.Bot.Tests.Jungle;

[Collection("real time")]
public sealed class JungleClipsTests
{
    private static readonly ConcentusOpusCodecFactory Codecs = new();

    [RealTimeFact]
    public void Every_clip_loads_with_the_real_codec_and_has_at_least_a_second_of_packets()
    {
        var clips = JungleClips.Load(Codecs, 100);

        Assert.Equal(Enum.GetValues<JungleClip>().Order(), clips.Keys.Order());
        foreach (var (clip, packets) in clips)
        {
            Assert.True(packets.Count >= 50, $"{clip} has only {packets.Count} packets");
            Assert.All(packets, p => Assert.False(p.IsEmpty));
        }
    }

    [RealTimeFact]
    public void Every_clip_decodes_and_is_audible()
    {
        foreach (var (clip, packets) in JungleClips.Load(Codecs, 100))
        {
            var decoder = Codecs.Decoder();
            var pcm = new short[OpusFormat.FrameShorts];
            var peak = 0;
            foreach (var packet in packets)
            {
                Assert.Equal(OpusFormat.FrameSamples, decoder.Decode(packet.Span, pcm));
                peak = Math.Max(peak, pcm.Max(s => Math.Abs((int)s)));
            }

            Assert.True(peak > 1000, $"{clip} is nearly silent (peak {peak})");
        }
    }

    [RealTimeFact]
    public void The_clips_feed_a_source_that_plays_them_whole()
    {
        var clips = JungleClips.Load(Codecs, 100);
        var source = new JungleSource(clips);

        Assert.True(source.Play(JungleClip.Jungle40));
        var frames = 0;
        while (source.Read(frames).Kind == FrameKind.Frame)
        {
            frames++;
        }

        Assert.Equal(clips[JungleClip.Jungle40].Count, frames);
    }

    private static int Peak(IReadOnlyList<ReadOnlyMemory<byte>> packets)
    {
        var decoder = Codecs.Decoder();
        var pcm = new short[OpusFormat.FrameShorts];
        var peak = 0;
        foreach (var packet in packets)
        {
            decoder.Decode(packet.Span, pcm);
            peak = Math.Max(peak, pcm.Max(s => Math.Abs((int)s)));
        }

        return peak;
    }

    [RealTimeFact]
    public void A_lower_volume_gives_quieter_packets_in_proportion()
    {
        var loud = Peak(JungleClips.Load(Codecs, 100)[JungleClip.Jungle40]);
        var quiet = Peak(JungleClips.Load(Codecs, 32)[JungleClip.Jungle40]);

        Assert.InRange(quiet / (double)loud, 0.25, 0.40);
    }

    [RealTimeFact]
    public async Task A_volume_change_re_encodes_the_clips_the_host_plays_and_the_next_call_is_quieter()
    {
        await using var rig = await new HostRig(c => c with { GuildId = 5000, JungleVolume = 100 }).StartAsync();
        await rig.Bot.JungleClipsReady;
        var loud = PlayedPeak(rig);

        rig.Bot.Reconfigure(rig.Bot.Config with { JungleVolume = 20 });
        await rig.Bot.JungleClipsReady;
        var quiet = PlayedPeak(rig);

        Assert.InRange(quiet / (double)loud, 0.12, 0.30);
    }

    // One test call to the Attack 1 link, read back once the call has ended (the link stops getting frames).
    private static int PlayedPeak(HostRig rig)
    {
        var link = rig.World.Gateway.Link(DemoWorld.Attack1);
        link.ClearSent();
        Assert.True(SpinWait.SpinUntil(() => rig.Bot.Jungle.Handle(new JungleCommand(JungleCommandKind.Test)).StartsWith("Test call", StringComparison.Ordinal), TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => link.Sent.Count > 0, TimeSpan.FromSeconds(10)));
        var count = -1;
        while (count != link.Sent.Count)
        {
            count = link.Sent.Count;
            Thread.Sleep(600);
        }

        var decoder = Codecs.Decoder();
        var pcm = new short[OpusFormat.FrameShorts];
        var peak = 0;
        foreach (var frame in link.Sent)
        {
            decoder.Decode(frame.Opus, pcm);
            peak = Math.Max(peak, pcm.Max(s => Math.Abs((int)s)));
        }

        return peak;
    }
}
