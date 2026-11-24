using System.Buffers.Binary;
using Kzg.Core.Abstractions;

namespace Kzg.Core.Jungle;

/// <summary>
/// Turns a clip's WAV into 20 ms PCM frames in the engine's format. The clips are recorded once, so the
/// bot speaks the same on every OS and needs no speech engine at run time.
/// </summary>
public static class JungleAudio
{
    // Peak-normalised clips were too loud next to voice chat; 0.5 is -6 dB. It is the gain at volume 100.
    public const float OutputGain = 0.5f;

    private const float PeakTarget = 0.891f;

    // Silence before the clip keeps its start from being clipped; after it, the encoder's lookahead keeps its end.
    private const int LeadSilenceSamples = OpusFormat.SampleRate / 10;

    /// <summary>The gain for a volume of 1 to 100 (<c>jungleVolume</c>): 100 is <see cref="OutputGain"/>, the rest scale it linearly.</summary>
    public static float GainFor(int volume) => OutputGain * Math.Clamp(volume, 1, 100) / 100f;

    /// <summary>
    /// The clip, at <paramref name="gain"/> (<see cref="OutputGain"/> unless given), as whole frames of stereo interleaved PCM, with silence in front and at least one frame of
    /// silence after. Throws <see cref="InvalidDataException"/> for a WAV it cannot read or a silent clip.
    /// </summary>
    public static IReadOnlyList<short[]> Frames(ReadOnlySpan<byte> wav, float gain = OutputGain)
    {
        var (mono, rate) = ReadWav(wav);
        var samples = Pad(Normalize(rate == OpusFormat.SampleRate ? mono : Resample(mono, rate)));

        var frames = new List<short[]>(samples.Length / OpusFormat.FrameSamples);
        for (var offset = 0; offset + OpusFormat.FrameSamples <= samples.Length; offset += OpusFormat.FrameSamples)
        {
            var pcm = new short[OpusFormat.FrameShorts];
            for (var i = 0; i < OpusFormat.FrameSamples; i++)
            {
                var value = (short)Math.Round(samples[offset + i] * gain * short.MaxValue);
                pcm[2 * i] = value;
                pcm[(2 * i) + 1] = value;
            }

            frames.Add(pcm);
        }

        return frames;
    }

    /// <summary>16-bit PCM WAV, mono or stereo, as mono samples in -1..1 plus the sample rate.</summary>
    private static (float[] Samples, int Rate) ReadWav(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a WAV file.");
        }

        int format = 0, channels = 0, rate = 0, bits = 0;
        for (var position = 12; position + 8 <= wav.Length;)
        {
            var id = wav.Slice(position, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav[(position + 4)..]);
            var body = position + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && body + 16 <= wav.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav[body..]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav[(body + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(wav[(body + 4)..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav[(body + 14)..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format is not (1 or 0xFFFE) || bits != 16 || channels is < 1 or > 2 || rate <= 0)
                {
                    throw new InvalidDataException(
                        $"WAV must be 16-bit PCM, mono or stereo. Got format {format}, {bits} bits, {channels} channels, {rate} Hz.");
                }

                var length = size < 0 || size > wav.Length - body ? wav.Length - body : size;
                return (ToMono(wav.Slice(body, length), channels), rate);
            }

            if (size < 0)
            {
                break;
            }

            position = body + size + (size & 1);
        }

        throw new InvalidDataException("The WAV has no audio.");
    }

    private static float[] ToMono(ReadOnlySpan<byte> data, int channels)
    {
        var frameBytes = 2 * channels;
        var samples = new float[data.Length / frameBytes];
        for (var i = 0; i < samples.Length; i++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++)
            {
                sum += BinaryPrimitives.ReadInt16LittleEndian(data[((i * frameBytes) + (2 * c))..]);
            }

            samples[i] = sum / (channels * 32768f);
        }

        return samples;
    }

    // Linear interpolation: plenty for a short spoken clip, and no extra dependency.
    private static float[] Resample(float[] samples, int rate)
    {
        var count = (int)((long)samples.Length * OpusFormat.SampleRate / rate);
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            var position = (double)i * rate / OpusFormat.SampleRate;
            var index = (int)position;
            var next = Math.Min(index + 1, samples.Length - 1);
            var fraction = (float)(position - index);
            result[i] = (samples[index] * (1 - fraction)) + (samples[next] * fraction);
        }

        return result;
    }

    private static float[] Normalize(float[] samples)
    {
        var peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
        if (peak < 0.001f)
        {
            throw new InvalidDataException("The clip is silent.");
        }

        var gain = PeakTarget / peak;
        return [.. samples.Select(s => s * gain)];
    }

    // Whole frames: silence in front, then the clip, then at least one frame of silence.
    private static float[] Pad(float[] samples)
    {
        var length = LeadSilenceSamples + samples.Length + OpusFormat.FrameSamples;
        var padded = new float[(length + OpusFormat.FrameSamples - 1) / OpusFormat.FrameSamples * OpusFormat.FrameSamples];
        samples.CopyTo(padded, LeadSilenceSamples);
        return padded;
    }
}
