using System.Collections.Concurrent;
using Kzg.Core.Abstractions;

namespace Kzg.Discord.Presence;

/// <summary>
/// The voice channels' names as Discord shows them, for the app's <c>status.channelName</c> and
/// <c>botDownIn</c>. The gateway fills it from the guild snapshot and keeps it with channel events; it is
/// read from the engine and control threads.
/// </summary>
public sealed class ChannelNames
{
    private readonly ConcurrentDictionary<ulong, string> _names = new();

    /// <summary>Replaces every name: the guild snapshot is the whole truth.</summary>
    public void Reset(IEnumerable<(ulong Channel, string Name)> channels)
    {
        var fresh = channels.ToDictionary(c => c.Channel, c => c.Name);
        foreach (var gone in _names.Keys.Where(id => !fresh.ContainsKey(id)).ToArray())
        {
            _names.TryRemove(gone, out _);
        }
        foreach (var (channel, name) in fresh)
        {
            _names[channel] = name;
        }
    }

    public void Set(ulong channel, string name) => _names[channel] = name;

    public void Remove(ulong channel) => _names.TryRemove(channel, out _);

    /// <summary>The channel's name, or null when it isn't known.</summary>
    public string? Find(ChannelId channel) => _names.TryGetValue(channel.V, out var name) ? name : null;

    /// <summary>The channel's name, or a stand-in the app can still show.</summary>
    public string NameOf(ChannelId channel) => Find(channel) ?? $"Channel {channel}";

    public IReadOnlyDictionary<ChannelId, string> All => _names.ToDictionary(p => new ChannelId(p.Key), p => p.Value);
}
