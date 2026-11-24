using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Discord;
using Kzg.Discord.Commands;

namespace Kzg.Bot.Discord;

/// <summary>
/// Decides what to tell the admins' alerts channel about the relay bots: one message when bots have been out
/// of their channels for <see cref="Grace"/>, and one when they are back. A bot that drops and rejoins within
/// the grace, as a moved bot or a short network blip does, says nothing. Pure: the host calls
/// <see cref="Check"/> on a timer and posts what it returns.
/// </summary>
public sealed class AlertMonitor(ITimeSource time)
{
    /// <summary>Long enough for a rejoin after a move or a voice server change, short enough to fix before a war call.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, (TimeSpan Since, bool Told)> _down = new(StringComparer.Ordinal);

    /// <summary>The messages to post now, if any, given each bot's state.</summary>
    public IReadOnlyList<string> Check(BotConfig config, bool running, IReadOnlyList<RelayBotState> states, Func<ChannelId, string> name)
    {
        var now = time.Now;
        var byRef = states.ToDictionary(s => s.TokenRef, StringComparer.Ordinal);
        List<string> gone = [], back = [];
        HashSet<string> watched = new(StringComparer.Ordinal);
        foreach (var bot in config.Bots)
        {
            if (!running || bot.ChannelId is not { } channel)
            {
                continue;
            }
            watched.Add(bot.TokenRef);
            var ready = byRef.TryGetValue(bot.TokenRef, out var state) && state.State == BotState.Ready && state.Channel == channel;
            var label = $"{RelayNames.Of(config, bot)} ({name(channel)})";
            if (ready)
            {
                if (_down.Remove(bot.TokenRef, out var was) && was.Told)
                {
                    back.Add(label);
                }
                continue;
            }
            if (!_down.TryGetValue(bot.TokenRef, out var down))
            {
                _down[bot.TokenRef] = (now, false);
            }
            else if (!down.Told && now - down.Since >= Grace)
            {
                _down[bot.TokenRef] = (down.Since, true);
                gone.Add(label);
            }
        }
        // A bot taken out of voice on purpose, or a stopped relay, is no longer watched and says nothing.
        foreach (var stale in _down.Keys.Where(k => !watched.Contains(k)).ToArray())
        {
            _down.Remove(stale);
        }
        List<string> messages = [];
        if (gone.Count > 0)
        {
            messages.Add($"Relay bot offline: {string.Join(", ", gone)}. Players there don't hear big-team calls and can't send them. "
                + "The bot keeps trying to rejoin; if it doesn't come back, check the bot's computer and its internet, and that the bot may still see and join that channel.");
        }
        if (back.Count > 0)
        {
            messages.Add($"Back online: {string.Join(", ", back)}.");
        }
        return messages;
    }
}
