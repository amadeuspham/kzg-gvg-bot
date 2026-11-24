namespace Kzg.Core.Abstractions;

/// <summary>
/// The Discord side as the engine sees it: one link per relay bot, and where users are. The adapter
/// raises events on its own threads; handlers only enqueue.
/// </summary>
public interface IVoiceGateway
{
    IReadOnlyList<IVoiceChannelLink> Links { get; }

    /// <summary>A user joined, moved or left voice; the channel is null when they left.</summary>
    event Action<UserId, ChannelId?> PresenceChanged;

    /// <summary>
    /// A relay bot was put into a VC after the engine was built, as <c>/gvg bot assign</c> and
    /// <c>/gvg bind</c> do. The link may not be <see cref="IVoiceChannelLink.Ready"/> yet; the engine
    /// polls that. Raised on any thread. A gateway whose links never change keeps this no-op default.
    /// </summary>
    event Action<IVoiceChannelLink> LinkAdded
    {
        add { }
        remove { }
    }

    /// <summary>
    /// A link was taken out for good, as <c>/gvg unbind</c> and <c>/gvg bot assign</c> do. The link sends
    /// and receives nothing after this. Raised on any thread.
    /// </summary>
    event Action<IVoiceChannelLink> LinkRemoved
    {
        add { }
        remove { }
    }
}

/// <summary>One relay bot sitting in one voice channel.</summary>
public interface IVoiceChannelLink
{
    ulong BotUserId { get; }

    ChannelId Channel { get; }

    /// <summary>Connected and able to send; a link that is not ready raises the botDown warning.</summary>
    bool Ready { get; }

    /// <summary>Every packet from a user in this channel; the adapter drops our own bots' packets.</summary>
    event Action<OpusPacket> PacketReceived;

    /// <summary>Sends one 20 ms Opus frame into the channel.</summary>
    void Send(ReadOnlyMemory<byte> opus);

    void SetSpeaking(bool on);
}
