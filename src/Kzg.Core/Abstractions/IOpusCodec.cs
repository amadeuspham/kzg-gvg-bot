namespace Kzg.Core.Abstractions;

/// <summary>Makes the codecs the engine needs: a decoder per source, an encoder per destination VC.</summary>
public interface IOpusCodecFactory
{
    IOpusDecoder Decoder();

    IOpusEncoder Encoder(int bitrate);
}

/// <summary>Decodes one source's Opus stream, frame by frame, at <see cref="OpusFormat"/>.</summary>
public interface IOpusDecoder
{
    /// <summary>Decodes one packet into interleaved PCM; returns samples per channel written.</summary>
    int Decode(ReadOnlySpan<byte> opus, Span<short> pcm);

    /// <summary>Fills one frame by packet-loss concealment; returns samples per channel written.</summary>
    int Conceal(Span<short> pcm);
}

/// <summary>Encodes one destination's mixed PCM, frame by frame, at <see cref="OpusFormat"/>.</summary>
public interface IOpusEncoder
{
    /// <summary>Encodes one frame of interleaved PCM; returns the packet's length in bytes.</summary>
    int Encode(ReadOnlySpan<short> pcm, Span<byte> opus);
}
