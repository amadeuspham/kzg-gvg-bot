using Kzg.Core.Abstractions;
using Kzg.Core.Jungle;

namespace Kzg.Core.Tests.Jungle;

public sealed class JungleAudioTests
{
    internal static byte[] Wav(int rate, int channels, int samplesPerChannel, bool silent = false)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        var dataBytes = samplesPerChannel * channels * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        for (var i = 0; i < samplesPerChannel; i++)
        {
            var value = silent ? (short)0 : (short)(Math.Sin(i * 0.05) * 8000);
            for (var c = 0; c < channels; c++)
            {
                w.Write(value);
            }
        }

        return stream.ToArray();
    }

    [Fact]
    public void Makes_whole_20ms_stereo_frames_with_lead_and_tail_silence()
    {
        var frames = JungleAudio.Frames(Wav(48000, 1, 48000));
        // 1 s of audio + 100 ms lead, rounded up to whole frames after at least one frame of tail.
        Assert.Equal(56, frames.Count);
        Assert.All(frames, f => Assert.Equal(OpusFormat.FrameShorts, f.Length));
        Assert.All(frames.Take(5), f => Assert.All(f, s => Assert.Equal(0, s)));
        Assert.All(frames[^1], s => Assert.Equal(0, s));
    }

    [Fact]
    public void Both_channels_carry_the_same_samples()
    {
        var frames = JungleAudio.Frames(Wav(48000, 1, 48000));
        Assert.All(frames, f =>
        {
            for (var i = 0; i < OpusFormat.FrameSamples; i++)
            {
                Assert.Equal(f[2 * i], f[(2 * i) + 1]);
            }
        });
    }

    [Fact]
    public void Scales_the_normalized_clip_by_OutputGain()
    {
        var frames = JungleAudio.Frames(Wav(48000, 1, 48000));
        var peak = frames.Max(f => f.Max(s => Math.Abs((int)s)));

        // Normalised to 0.891 of full scale, then halved.
        var expected = 0.891 * JungleAudio.OutputGain * short.MaxValue;
        Assert.InRange(peak, expected * 0.99, expected * 1.01);
    }

    [Theory]
    [InlineData(48000, 2)]
    [InlineData(22050, 1)]
    public void Other_rates_and_stereo_are_converted_to_the_same_length(int rate, int channels)
    {
        var frames = JungleAudio.Frames(Wav(rate, channels, rate));
        Assert.InRange(frames.Count, 55, 57);
    }

    [Fact]
    public void Silent_or_broken_wav_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => JungleAudio.Frames(Wav(48000, 1, 4800, silent: true)));
        Assert.Throws<InvalidDataException>(() => JungleAudio.Frames([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => JungleAudio.Frames("RIFF\0\0\0\0WAVE"u8));
    }

    [Fact]
    public void Rejects_non_16_bit()
    {
        var wav = Wav(48000, 1, 100);
        wav[34] = 8;
        Assert.Throws<InvalidDataException>(() => JungleAudio.Frames(wav));
    }

    [Fact]
    public void Skips_unknown_chunks_before_the_data()
    {
        var wav = Wav(48000, 1, 4800);
        // Insert a 4-byte "LIST" chunk after the 36-byte header, before "data".
        byte[] extra = [.. "LIST"u8, 4, 0, 0, 0, 1, 2, 3, 4];
        var patched = (byte[])[.. wav[..36], .. extra, .. wav[36..]];
        Assert.Equal(JungleAudio.Frames(wav).Count, JungleAudio.Frames(patched).Count);
    }

    [Fact]
    public void A_wav_with_no_data_chunk_is_rejected()
    {
        var wav = Wav(48000, 1, 100);
        Assert.Throws<InvalidDataException>(() => JungleAudio.Frames(wav.AsSpan(0, 36)));
    }

    [Fact]
    public void Volume_100_is_jungle_bots_gain_and_lower_volumes_scale_it_down_linearly()
    {
        Assert.Equal(JungleAudio.OutputGain, JungleAudio.GainFor(100));
        Assert.Equal(JungleAudio.OutputGain * 0.32f, JungleAudio.GainFor(32), 6);
        Assert.True(JungleAudio.GainFor(1) > 0);
        Assert.Equal(JungleAudio.GainFor(100), JungleAudio.GainFor(500));
        Assert.Equal(JungleAudio.GainFor(1), JungleAudio.GainFor(0));
    }

    [Fact]
    public void The_gain_scales_every_sample_and_the_default_is_the_full_level()
    {
        var wav = Wav(48000, 1, 48000);
        var full = JungleAudio.Frames(wav);
        var quiet = JungleAudio.Frames(wav, JungleAudio.GainFor(32));

        int Peak(IReadOnlyList<short[]> frames) => frames.Max(f => f.Max(s => Math.Abs((int)s)));

        Assert.Equal(full.Count, quiet.Count);
        Assert.Equal(Peak(full), Peak(JungleAudio.Frames(wav, JungleAudio.GainFor(100))));
        Assert.InRange(Peak(quiet), Peak(full) * 0.31, Peak(full) * 0.33);
    }
}
