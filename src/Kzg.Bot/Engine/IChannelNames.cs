using Kzg.Core.Abstractions;

namespace Kzg.Bot.Engine;

/// <summary>The names the app shows for voice channels, which the engine's IDs don't carry.</summary>
public interface IChannelNames
{
    /// <summary>The channel's name as Discord shows it; any text if it isn't known.</summary>
    string NameOf(ChannelId channel);
}
