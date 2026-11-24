using Kzg.Core.Abstractions;

namespace Kzg.Audio.Tests;

public class CodecTests
{
    private static short[] Sine(double hz, int frames, double amplitude = 12000)
    {
        var pcm = new short[frames * OpusFormat.FrameShorts];
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var v = (short)(amplitude * Math.Sin(2 * Math.PI * hz * i / OpusFormat.SampleRate));
            pcm[2 * i] = v;
            pcm[(2 * i) + 1] = v;
        }
        return pcm;
    }

    private static (short[] Decoded, int Packets) RoundTrip(short[] input)
    {
        var factory = new ConcentusOpusCodecFactory();
        var encoder = factory.Encoder(64_000);
        var decoder = factory.Decoder();
        var output = new List<short>();
        var packet = new byte[1275];
        var frame = new short[OpusFormat.FrameShorts];
        var packets = 0;
        for (var offset = 0; offset + OpusFormat.FrameShorts <= input.Length; offset += OpusFormat.FrameShorts)
        {
            var length = encoder.Encode(input.AsSpan(offset, OpusFormat.FrameShorts), packet);
            Assert.InRange(length, 1, packet.Length);
            var samples = decoder.Decode(packet.AsSpan(0, length), frame);
            Assert.Equal(OpusFormat.FrameSamples, samples);
            output.AddRange(frame);
            packets++;
        }
        return (output.ToArray(), packets);
    }

    [Fact]
    public void Sine_survives_a_round_trip_with_usable_snr()
    {
        var input = Sine(1000, 50);
        var (decoded, packets) = RoundTrip(input);
        Assert.Equal(50, packets);

        // Opus adds codec delay; find the lag that best aligns, then measure SNR over the settled tail.
        var best = double.NegativeInfinity;
        for (var lag = 0; lag < 1200; lag++)
        {
            best = Math.Max(best, Snr(input, decoded, lag));
        }
        Assert.True(best > 15, $"SNR {best:F1} dB");
    }

    private static double Snr(short[] reference, short[] decoded, int lag)
    {
        double signal = 0, noise = 0;
        for (var i = 20 * OpusFormat.FrameShorts; i < reference.Length - 2400; i += 2)
        {
            double r = reference[i], d = decoded[i + (lag * 2)];
            signal += r * r;
            noise += (r - d) * (r - d);
        }
        return 10 * Math.Log10(signal / Math.Max(noise, 1));
    }

    [Fact]
    public void Conceal_fills_a_frame_with_continuing_sound_after_a_lost_packet()
    {
        var factory = new ConcentusOpusCodecFactory();
        var encoder = factory.Encoder(64_000);
        var decoder = factory.Decoder();
        var input = Sine(440, 20);
        var packet = new byte[1275];
        var frame = new short[OpusFormat.FrameShorts];
        for (var f = 0; f < 10; f++)
        {
            var length = encoder.Encode(input.AsSpan(f * OpusFormat.FrameShorts, OpusFormat.FrameShorts), packet);
            decoder.Decode(packet.AsSpan(0, length), frame);
        }

        var concealed = new short[OpusFormat.FrameShorts];
        Assert.Equal(OpusFormat.FrameSamples, decoder.Conceal(concealed));
        Assert.Contains(concealed, s => Math.Abs(s) > 500);

        // Decoding resumes normally after the gap.
        var next = encoder.Encode(input.AsSpan(11 * OpusFormat.FrameShorts, OpusFormat.FrameShorts), packet);
        Assert.Equal(OpusFormat.FrameSamples, decoder.Decode(packet.AsSpan(0, next), frame));
    }

    [Fact]
    public void Packets_fit_the_bitrate()
    {
        var factory = new ConcentusOpusCodecFactory();
        var encoder = factory.Encoder(64_000);
        var packet = new byte[1275];
        var input = Sine(1000, 30);
        var total = 0;
        for (var f = 0; f < 30; f++)
        {
            total += encoder.Encode(input.AsSpan(f * OpusFormat.FrameShorts, OpusFormat.FrameShorts), packet);
        }
        // 64 kbps is 160 bytes per 20 ms; allow VBR slack.
        Assert.InRange(total / 30.0, 10, 400);
    }
}
