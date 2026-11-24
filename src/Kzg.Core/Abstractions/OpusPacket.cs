namespace Kzg.Core.Abstractions;

/// <summary>
/// One received Opus packet, as a voice library thread hands it over. That thread only enqueues it;
/// the engine thread does everything else. <paramref name="Arrival"/> is <see cref="ITimeSource"/> time.
/// <paramref name="Ssrc"/> names the stream the packet belongs to, or is 0 when the source doesn't say: a
/// player who moves to another voice channel, or leaves and comes back, starts a stream of new numbers, and
/// the engine must not read it as a late continuation of the old one.
/// </summary>
public sealed record OpusPacket(UserId User, ushort Seq, uint RtpTs, ReadOnlyMemory<byte> Data, TimeSpan Arrival, uint Ssrc = 0);

/// <summary>The audio format of every frame: 48 kHz stereo, 20 ms, as Discord and Gengfu use.</summary>
public static class OpusFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int FrameMs = 20;

    /// <summary>Samples per channel in one frame.</summary>
    public const int FrameSamples = SampleRate / 1000 * FrameMs;

    /// <summary>Interleaved 16-bit samples in one frame, both channels.</summary>
    public const int FrameShorts = FrameSamples * Channels;
}
