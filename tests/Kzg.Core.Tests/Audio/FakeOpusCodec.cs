using Kzg.Core.Abstractions;

namespace Kzg.Core.Tests.Audio;

/// <summary>
/// A codec that makes its work visible: a "packet" decodes to every sample being its first byte
/// times 100, concealment gives every sample -7, and encoding keeps the PCM it was handed.
/// </summary>
internal sealed class FakeOpusCodec : IOpusCodecFactory
{
    public List<int> Bitrates { get; } = [];

    public IOpusDecoder Decoder() => new FakeDecoder();

    public IOpusEncoder Encoder(int bitrate)
    {
        Bitrates.Add(bitrate);
        return new FakeEncoder();
    }
}

internal sealed class FakeDecoder : IOpusDecoder
{
    public const short Concealed = -7;

    public int Decodes { get; private set; }

    public int Conceals { get; private set; }

    /// <summary>What each call claims it wrote, per channel; a real decoder may write less for a bad packet.</summary>
    public int SamplesPerChannel { get; set; } = OpusFormat.FrameSamples;

    public int Decode(ReadOnlySpan<byte> opus, Span<short> pcm)
    {
        Decodes++;
        pcm[..(SamplesPerChannel * OpusFormat.Channels)].Fill((short)(opus[0] * 100));
        return SamplesPerChannel;
    }

    public int Conceal(Span<short> pcm)
    {
        Conceals++;
        pcm[..(SamplesPerChannel * OpusFormat.Channels)].Fill(Concealed);
        return SamplesPerChannel;
    }
}

internal sealed class FakeEncoder : IOpusEncoder
{
    public const byte Marker = 0xEC;

    public int Encodes { get; private set; }

    public short[]? LastPcm { get; private set; }

    public int Encode(ReadOnlySpan<short> pcm, Span<byte> opus)
    {
        Encodes++;
        LastPcm = pcm.ToArray();
        opus[0] = Marker;
        opus[1] = (byte)Encodes;
        return 2;
    }
}
