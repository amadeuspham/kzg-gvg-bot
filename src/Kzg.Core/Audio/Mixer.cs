using Kzg.Core.Abstractions;

namespace Kzg.Core.Audio;

/// <summary>One source's part in a destination's tick: what its buffer gave, and that frame decoded.</summary>
/// <param name="Pcm">The decoded or concealed frame, <see cref="OpusFormat.FrameShorts"/> interleaved samples; ignored when silent.</param>
public readonly record struct MixInput(FrameRead Frame, ReadOnlyMemory<short> Pcm);

public enum MixKind
{
    /// <summary>Nothing to send this tick.</summary>
    Nothing,

    /// <summary>One source's packet, sent on unchanged: no decode, no re-encode, no quality lost.</summary>
    Passthrough,

    /// <summary>Two or more sources summed, limited and encoded with this destination's encoder.</summary>
    Mixed,
}

/// <param name="Opus">The packet to send; a mixed one lives in the mixer's buffer until its next <see cref="Mixer.Mix"/>.</param>
public readonly record struct MixOutput(MixKind Kind, ReadOnlyMemory<byte> Opus);

/// <summary>
/// One destination VC's mixer, run once a tick. A lone source passes through untouched; several are
/// summed, softly limited so loud talk together never clips, and encoded by this VC's own encoder.
/// </summary>
public sealed class Mixer(IOpusEncoder encoder)
{
    /// <summary>The mixed stream's bitrate, as the plan sets it for voice.</summary>
    public const int Bitrate = 64_000;

    // Opus's largest packet for one frame.
    private const int MaxPacketBytes = 1275;

    private readonly int[] _sum = new int[OpusFormat.FrameShorts];
    private readonly short[] _pcm = new short[OpusFormat.FrameShorts];
    private readonly byte[] _packet = new byte[MaxPacketBytes];

    public static Mixer Create(IOpusCodecFactory codecs) => new(codecs.Encoder(Bitrate));

    public MixOutput Mix(ReadOnlySpan<MixInput> inputs)
    {
        var audible = 0;
        MixInput only = default;
        foreach (var input in inputs)
        {
            if (input.Frame.Kind != FrameKind.Silent)
            {
                (audible, only) = (audible + 1, input);
            }
        }
        if (audible == 0)
        {
            return default;
        }
        if (audible == 1)
        {
            // A lone lost frame is left to the listener's decoder, which conceals it as ours would.
            return only.Frame.Kind == FrameKind.Frame ? new MixOutput(MixKind.Passthrough, only.Frame.Opus) : default;
        }
        Array.Clear(_sum);
        foreach (var input in inputs)
        {
            if (input.Frame.Kind == FrameKind.Silent)
            {
                continue;
            }
            if (input.Pcm.Length != OpusFormat.FrameShorts)
            {
                throw new ArgumentException($"a frame is {OpusFormat.FrameShorts} samples", nameof(inputs));
            }
            var pcm = input.Pcm.Span;
            for (var i = 0; i < pcm.Length; i++)
            {
                _sum[i] += pcm[i];
            }
        }
        SoftLimiter.Apply(_sum, _pcm);
        var length = encoder.Encode(_pcm, _packet);
        return new MixOutput(MixKind.Mixed, _packet.AsMemory(0, length));
    }
}

/// <summary>
/// Brings a sum of voices back into 16 bits without clipping: untouched below the knee, then bent
/// smoothly toward full scale, so two loud callers at once sound loud, not broken.
/// </summary>
public static class SoftLimiter
{
    /// <summary>Where limiting begins: about -2 dB of full scale.</summary>
    public const int Knee = 26_000;

    private const double Headroom = short.MaxValue - Knee;

    public static void Apply(ReadOnlySpan<int> sum, Span<short> output)
    {
        if (output.Length < sum.Length)
        {
            throw new ArgumentException("the output holds every sample", nameof(output));
        }
        for (var i = 0; i < sum.Length; i++)
        {
            output[i] = Limit(sum[i]);
        }
    }

    public static short Limit(int sample)
    {
        var magnitude = Math.Abs((long)sample);
        if (magnitude <= Knee)
        {
            return (short)sample;
        }
        // tanh leaves the knee at slope 1, so the bend has no corner, and never reaches full scale.
        var limited = Knee + (Headroom * Math.Tanh((magnitude - Knee) / Headroom));
        var rounded = (short)Math.Min(short.MaxValue, Math.Round(limited));
        return sample < 0 ? (short)-rounded : rounded;
    }
}

/// <summary>Turns what a buffer gave into PCM with a source's decoder.</summary>
public static class FrameDecoding
{
    /// <summary>Decodes a frame or conceals a missing one into <paramref name="pcm"/>; false, writing nothing, for silence.</summary>
    public static bool Decode(IOpusDecoder decoder, FrameRead frame, Span<short> pcm)
    {
        if (pcm.Length < OpusFormat.FrameShorts)
        {
            throw new ArgumentException($"a frame is {OpusFormat.FrameShorts} samples", nameof(pcm));
        }
        switch (frame.Kind)
        {
            case FrameKind.Frame:
                Fill(decoder.Decode(frame.Opus.Span, pcm), pcm);
                return true;
            case FrameKind.Missing:
                Fill(decoder.Conceal(pcm), pcm);
                return true;
            default:
                return false;
        }
    }

    // A decoder that gives less than a whole frame, as a corrupt packet may, leaves silence after it.
    private static void Fill(int samplesPerChannel, Span<short> pcm)
    {
        var written = Math.Clamp(samplesPerChannel, 0, OpusFormat.FrameSamples) * OpusFormat.Channels;
        pcm[written..OpusFormat.FrameShorts].Clear();
    }
}
