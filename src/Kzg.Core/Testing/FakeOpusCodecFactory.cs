using Kzg.Core.Abstractions;

namespace Kzg.Core.Testing;

/// <summary>
/// A codec that makes its work visible. A packet decodes to every sample being its first byte times 100,
/// concealment gives every sample <see cref="Concealed"/>, and a frame encodes to
/// <see cref="Marker"/> then its first sample as two big-endian bytes, so a test can read a mix's sum.
/// </summary>
public sealed class FakeOpusCodecFactory : IOpusCodecFactory
{
    public const short Concealed = -7;
    public const byte Marker = 0xEC;

    public IOpusDecoder Decoder() => new FakeDecoder();

    public IOpusEncoder Encoder(int bitrate) => new FakeEncoder();

    /// <summary>The packet <see cref="IOpusEncoder.Encode"/> gives for a frame whose samples all equal <paramref name="sample"/>.</summary>
    public static byte[] Encoded(short sample) => [Marker, (byte)(sample >> 8), (byte)sample];

    private sealed class FakeDecoder : IOpusDecoder
    {
        public int Decode(ReadOnlySpan<byte> opus, Span<short> pcm)
        {
            pcm[..OpusFormat.FrameShorts].Fill((short)(opus[0] * 100));
            return OpusFormat.FrameSamples;
        }

        public int Conceal(Span<short> pcm)
        {
            pcm[..OpusFormat.FrameShorts].Fill(Concealed);
            return OpusFormat.FrameSamples;
        }
    }

    private sealed class FakeEncoder : IOpusEncoder
    {
        public int Encode(ReadOnlySpan<short> pcm, Span<byte> opus)
        {
            var packet = Encoded(pcm[0]);
            packet.CopyTo(opus);
            return packet.Length;
        }
    }
}
