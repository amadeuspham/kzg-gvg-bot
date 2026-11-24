using Concentus;
using Concentus.Enums;
using Kzg.Core.Abstractions;
using CoreDecoder = Kzg.Core.Abstractions.IOpusDecoder;
using CoreEncoder = Kzg.Core.Abstractions.IOpusEncoder;
using InnerDecoder = Concentus.IOpusDecoder;
using InnerEncoder = Concentus.IOpusEncoder;

namespace Kzg.Audio;

/// <summary>Opus codecs from Concentus, the managed port: no native libopus to ship or load.</summary>
public sealed class ConcentusOpusCodecFactory : IOpusCodecFactory
{
    static ConcentusOpusCodecFactory()
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
    }

    public CoreDecoder Decoder() => new FrameDecoder();

    public CoreEncoder Encoder(int bitrate) => new FrameEncoder(bitrate);

    private sealed class FrameDecoder : CoreDecoder
    {
        private readonly InnerDecoder _inner =
            OpusCodecFactory.CreateDecoder(OpusFormat.SampleRate, OpusFormat.Channels);

        public int Decode(ReadOnlySpan<byte> opus, Span<short> pcm) =>
            _inner.Decode(opus, pcm, OpusFormat.FrameSamples);

        // An empty packet is how Opus is told to conceal a lost frame.
        public int Conceal(Span<short> pcm) =>
            _inner.Decode(ReadOnlySpan<byte>.Empty, pcm, OpusFormat.FrameSamples);
    }

    private sealed class FrameEncoder : CoreEncoder
    {
        private readonly InnerEncoder _inner;

        public FrameEncoder(int bitrate)
        {
            _inner = OpusCodecFactory.CreateEncoder(
                OpusFormat.SampleRate, OpusFormat.Channels, OpusApplication.OPUS_APPLICATION_VOIP);
            _inner.Bitrate = bitrate;
        }

        public int Encode(ReadOnlySpan<short> pcm, Span<byte> opus) =>
            _inner.Encode(pcm, OpusFormat.FrameSamples, opus, opus.Length);
    }
}
