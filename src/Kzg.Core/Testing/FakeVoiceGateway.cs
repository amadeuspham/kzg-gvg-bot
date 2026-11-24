using Kzg.Core.Abstractions;

namespace Kzg.Core.Testing;

/// <summary>
/// The Discord side with no Discord: links a test feeds and reads, and presence it moves by hand. It runs
/// the whole engine offline. Links are added before the engine is built, as a real adapter has them at start.
/// </summary>
public sealed class FakeVoiceGateway(ITimeSource time) : IVoiceGateway
{
    private readonly List<FakeVoiceChannelLink> _links = [];

    public IReadOnlyList<IVoiceChannelLink> Links => _links;

    public event Action<UserId, ChannelId?> PresenceChanged = delegate { };

    /// <summary>A relay bot sitting in <paramref name="channel"/>; its user ID is made up unless given.</summary>
    public FakeVoiceChannelLink AddLink(ChannelId channel, ulong? botUserId = null)
    {
        if (_links.Any(l => l.Channel == channel))
        {
            throw new ArgumentException("a channel has one relay bot", nameof(channel));
        }
        var link = new FakeVoiceChannelLink(channel, botUserId ?? 9_000_000_000UL + channel.V, time);
        _links.Add(link);
        return link;
    }

    public FakeVoiceChannelLink Link(ChannelId channel) =>
        _links.FirstOrDefault(l => l.Channel == channel) ?? throw new KeyNotFoundException($"no relay bot in channel {channel}");

    /// <summary>A user joins, moves to or (with null) leaves a voice channel.</summary>
    public void Join(UserId user, ChannelId? channel) => PresenceChanged(user, channel);
}
