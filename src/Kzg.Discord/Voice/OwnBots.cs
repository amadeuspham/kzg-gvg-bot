using System.Collections.Concurrent;

namespace Kzg.Discord.Voice;

/// <summary>
/// The Discord users that are our own relay bots. A relay bot's voice is what the others in its channel
/// hear, so it must never come back in as a player's audio. Read on voice threads, written as bots sign in.
/// </summary>
public sealed class OwnBots
{
    private readonly ConcurrentDictionary<ulong, byte> _ids = new();

    public void Add(ulong id)
    {
        if (id != 0)
        {
            _ids[id] = 0;
        }
    }

    public bool Contains(ulong id) => _ids.ContainsKey(id);
}
