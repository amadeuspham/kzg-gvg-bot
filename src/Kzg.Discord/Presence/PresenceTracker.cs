using Kzg.Core.Abstractions;

namespace Kzg.Discord.Presence;

/// <summary>
/// Who is in which voice channel, as one bot's gateway told it. A guild snapshot sets the whole picture and
/// voice-state updates follow it; a gateway resume sends no new snapshot but a full re-identify does, so a
/// snapshot is diffed against what is known and only the differences are raised. Changes of mute or
/// deafen, which move nobody, raise nothing. Handlers run under a lock, in order: they must only enqueue.
/// </summary>
public sealed class PresenceTracker(Action<UserId, ChannelId?> raise, Func<ulong, bool> isIgnored)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, ulong> _channelOf = [];

    /// <summary>The whole picture: everyone in voice now. Anyone known but missing here has left.</summary>
    public void Snapshot(IEnumerable<(ulong User, ulong Channel)> inVoice)
    {
        var now = inVoice.Where(p => !isIgnored(p.User)).GroupBy(p => p.User).ToDictionary(g => g.Key, g => g.Last().Channel);
        lock (_gate)
        {
            foreach (var user in _channelOf.Keys.Where(user => !now.ContainsKey(user)).ToArray())
            {
                _channelOf.Remove(user);
                raise(new UserId(user), null);
            }
            foreach (var (user, channel) in now)
            {
                if (!_channelOf.TryGetValue(user, out var known) || known != channel)
                {
                    _channelOf[user] = channel;
                    raise(new UserId(user), new ChannelId(channel));
                }
            }
        }
    }

    /// <summary>A voice-state update: the user is now in <paramref name="channel"/>, or in none.</summary>
    public void Update(ulong user, ulong? channel)
    {
        if (isIgnored(user))
        {
            return;
        }
        lock (_gate)
        {
            if (channel is { } to)
            {
                if (_channelOf.TryGetValue(user, out var known) && known == to)
                {
                    return;
                }
                _channelOf[user] = to;
                raise(new UserId(user), new ChannelId(to));
            }
            else if (_channelOf.Remove(user))
            {
                raise(new UserId(user), null);
            }
        }
    }

    /// <summary>The user turned out to be ignorable after all (one of our bots, learned late): they are gone as far as anyone knows.</summary>
    public void Drop(ulong user)
    {
        lock (_gate)
        {
            if (_channelOf.Remove(user))
            {
                raise(new UserId(user), null);
            }
        }
    }

    /// <summary>Where the user is, as far as is known.</summary>
    public ChannelId? ChannelOf(ulong user)
    {
        lock (_gate)
        {
            return _channelOf.TryGetValue(user, out var channel) ? new ChannelId(channel) : null;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _channelOf.Count;
            }
        }
    }
}
