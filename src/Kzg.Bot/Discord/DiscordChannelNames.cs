using Kzg.Bot.Engine;
using Kzg.Core.Abstractions;
using Kzg.Discord.Presence;

namespace Kzg.Bot.Discord;

/// <summary>The engine's <see cref="IChannelNames"/> over the names the Discord adapter keeps.</summary>
public sealed class DiscordChannelNames(ChannelNames names) : IChannelNames
{
    public string NameOf(ChannelId channel) => names.NameOf(channel);
}
