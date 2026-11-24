using Kzg.Core.Abstractions;

namespace Kzg.Discord.Voice;

/// <summary>One voice packet as the library hands it over; <paramref name="frame"/> is only valid during the call.</summary>
internal delegate void FrameReceived(uint ssrc, ushort sequence, uint timestamp, ReadOnlySpan<byte> frame);

/// <summary>
/// A bot's connection into one voice channel, as <see cref="RelayLink"/> needs it. NetCord's voice client
/// implements it for real; tests implement it to feed packets and read what was sent.
/// </summary>
internal interface IVoiceConnection : IDisposable
{
    /// <summary>A packet arrived, on the library's UDP read thread.</summary>
    event FrameReceived Frame;

    /// <summary>The media path is up (the first time, or after the library reconnected from scratch).</summary>
    event Action Ready;

    /// <summary>A player came into the channel. They have not seen the speaking flag of anyone already there.</summary>
    event Action UserJoined;

    /// <summary>The library resumed after a drop, with nothing lost.</summary>
    event Action Resumed;

    /// <summary>The connection dropped and the library is trying to get it back.</summary>
    event Action Interrupted;

    /// <summary>
    /// Discord says the bot's own voice state is now this channel, or none: someone moved or disconnected
    /// it. Raised on the gateway's thread; also with the channel it joined, which is not a move.
    /// </summary>
    event Action<ChannelId?> ChannelChanged;

    /// <summary>True from <see cref="Ready"/> or <see cref="Resumed"/> until <see cref="Interrupted"/>.</summary>
    bool IsReady { get; }

    /// <summary>Completes when the connection is gone for good and a new one is needed.</summary>
    Task Lost { get; }

    /// <summary>The player a stream's SSRC belongs to; false until Discord has said.</summary>
    bool TryGetUser(uint ssrc, out ulong user);

    /// <summary>Encrypts and sends one packet on the caller's thread; the caller may reuse its buffer at once.</summary>
    void Send(ushort sequence, uint timestamp, ReadOnlySpan<byte> opus);

    Task SetSpeakingAsync(bool on);

    Task CloseAsync();
}
