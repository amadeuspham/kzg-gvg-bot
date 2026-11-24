using Kzg.Core.Abstractions;

namespace Kzg.Bot.Tests.Hosting;

/// <summary>Audio made at test time: tones and click trains. Nothing is checked in or left on disk.</summary>
internal static class SpeechAudio
{
    /// <summary>
    /// Opus frames of a tone that changes pitch every frame, so no two packets share bytes and a relayed
    /// frame can be traced to its index.
    /// </summary>
    public static IReadOnlyList<byte[]> Tone(IOpusCodecFactory codecs, int frames, int baseHz = 300)
    {
        var encoder = codecs.Encoder(64_000);
        var pcm = new short[OpusFormat.FrameShorts];
        var packet = new byte[1275];
        List<byte[]> result = [];
        for (var i = 0; i < frames; i++)
        {
            var frequency = baseHz + (17 * i);
            for (var n = 0; n < OpusFormat.FrameSamples; n++)
            {
                var sample = (short)(9000 * Math.Sin(2 * Math.PI * frequency * (((i * OpusFormat.FrameSamples) + n) / (double)OpusFormat.SampleRate)));
                pcm[2 * n] = pcm[(2 * n) + 1] = sample;
            }
            result.Add(packet.AsSpan(0, encoder.Encode(pcm, packet)).ToArray());
        }
        return result;
    }

    /// <summary>
    /// A click every 250 ms over a little noise of its own in each frame, so no two packets share bytes: the
    /// click train latency is measured on, in Opus frames.
    /// </summary>
    public static IReadOnlyList<byte[]> ClickTrain(IOpusCodecFactory codecs, int frames, int seed = 5)
    {
        var encoder = codecs.Encoder(64_000);
        var random = new Random(seed);
        var pcm = new short[OpusFormat.FrameShorts];
        var packet = new byte[1275];
        List<byte[]> result = [];
        for (var i = 0; i < frames; i++)
        {
            for (var n = 0; n < OpusFormat.FrameSamples; n++)
            {
                var click = i % 12 == 0 && n < 96 ? (int)(22000 * Math.Exp(-n / 24.0) * Math.Cos(n * 0.9)) : 0;
                pcm[2 * n] = pcm[(2 * n) + 1] = (short)Math.Clamp(click + random.Next(-40, 41), short.MinValue, short.MaxValue);
            }
            result.Add(packet.AsSpan(0, encoder.Encode(pcm, packet)).ToArray());
        }
        return result;
    }

    /// <summary>Marks a packet that several frames share, as the encoder writes for silence: its index can't be known.</summary>
    public const int Ambiguous = -2;

    /// <summary>The frame index of each packet; packets more than one frame made map to <see cref="Ambiguous"/>.</summary>
    public static Dictionary<string, int> Index(IReadOnlyList<byte[]> frames)
    {
        Dictionary<string, int> index = [];
        for (var i = 0; i < frames.Count; i++)
        {
            var key = Convert.ToBase64String(frames[i]);
            index[key] = index.ContainsKey(key) ? Ambiguous : i;
        }
        return index;
    }

    /// <summary>The frame a packet is, <see cref="Ambiguous"/>, or -1 when no frame made it.</summary>
    public static int IndexOf(Dictionary<string, int> index, byte[] packet) =>
        index.TryGetValue(Convert.ToBase64String(packet), out var i) ? i : -1;
}
