using System.Collections.Concurrent;
using Kzg.Bot.Logging;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Jungle;
using Kzg.Discord;
using Kzg.Discord.Commands;
using Microsoft.Extensions.Logging;

namespace Kzg.Bot.Discord;

/// <summary>
/// What the slash commands act on: the config on disk, the running bot and the Discord bots. Every change
/// is checked against the config's rules, written, handed to routing and to the bots that sit in voice, in
/// that order and one at a time, so a command can't leave the file and the running bot disagreeing.
/// </summary>
public sealed class GvgHost : IGvgControl
{
    private readonly BotApp _bot;
    private readonly ConfigStore _store;
    private readonly Secrets _secrets;
    private readonly IDiscordRuntime _discord;
    private readonly Redactor _redactor;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _editing = new(1, 1);
    private readonly ConcurrentDictionary<string, ulong> _learnedBots = new(StringComparer.Ordinal);
    private ulong _learnedGuild;
    private volatile bool _live;

    public GvgHost(BotApp bot, ConfigStore store, Secrets secrets, IDiscordRuntime discord, Redactor redactor)
    {
        (_bot, _store, _secrets, _discord, _redactor) = (bot, store, secrets, discord, redactor);
        _log = bot.Loggers.CreateLogger("Kzg.Bot.Commands");
        discord.GuildResolved += guild =>
        {
            Interlocked.Exchange(ref _learnedGuild, guild);
            FlushIfLive();
        };
        discord.BotIdLearned += (tokenRef, id) =>
        {
            _learnedBots[tokenRef] = id;
            FlushIfLive();
        };
    }

    public BotConfig Config => _bot.Config;

    public bool Running => _discord.Running;

    /// <summary>
    /// The startup is over: what the bots learned while signing in (the server, their own IDs) is written to
    /// the config, and later discoveries are written as they come.
    /// </summary>
    public async Task GoLiveAsync(CancellationToken cancellationToken)
    {
        _live = true;
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EditOutcome> EditAsync(Func<BotConfig, ConfigEdit> edit, CancellationToken cancellationToken)
    {
        await _editing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _bot.Config;
            switch (edit(current))
            {
                case ConfigEdit.Refuse refuse:
                    return new EditOutcome(false, refuse.Reason);
                case ConfigEdit.Change change:
                    switch (_store.Save(change.Config, _secrets))
                    {
                        case FileSave.Invalid invalid:
                            return new EditOutcome(false, "That would break the config: " + string.Join("; ", invalid.Errors.Select(e => e.ToString())));
                        case FileSave.Unwritable:
                            return new EditOutcome(false, "The config file couldn't be written, so nothing changed. Check the bot's data folder.");
                    }
                    _bot.Reconfigure(change.Config);
                    await _discord.ApplyAsync(change.Config, _discord.Running, cancellationToken).ConfigureAwait(false);
                    return new EditOutcome(true, change.Message);
                default:
                    return new EditOutcome(false, "Nothing to do.");
            }
        }
        finally
        {
            _editing.Release();
        }
    }

    public async Task SetRunningAsync(bool running, CancellationToken cancellationToken)
    {
        await _editing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Saved first so the stop survives a restart; an unwritable file leaves everything as it was.
            var config = _bot.Config with { Stopped = !running };
            switch (_store.Save(config, _secrets))
            {
                case FileSave.Invalid invalid:
                    throw new InvalidOperationException("That would break the config: " + string.Join("; ", invalid.Errors.Select(e => e.ToString())));
                case FileSave.Unwritable:
                    throw new InvalidOperationException("The config file couldn't be written, so nothing changed. Check the bot's data folder.");
            }
            _bot.Reconfigure(config);
            await _discord.ApplyAsync(config, running, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _editing.Release();
        }
    }

    public GvgStatus Status()
    {
        var config = _bot.Config;
        var states = _discord.Bots.ToDictionary(b => b.TokenRef, StringComparer.Ordinal);
        ChannelStatus Channel(ChannelId channel)
        {
            var bot = config.Bots.FirstOrDefault(b => b.ChannelId == channel);
            var known = bot is not null && states.TryGetValue(bot.TokenRef, out var s) ? s : null;
            return new ChannelStatus(channel, ChannelName(channel), bot is null ? null : RelayNames.Of(config, bot), known?.State ?? BotState.Idle, known?.NicknameRefused ?? false);
        }
        GroupStatus Group(Group group) => new(group, [.. config.Groups[group].Channels.Select(Channel)]);
        return new GvgStatus(
            _discord.Running,
            [Group(Core.Abstractions.Group.Attack), Group(Core.Abstractions.Group.Defense)],
            [.. config.Bots.Select(b => new BotStatus(
                b.TokenRef,
                RelayNames.Of(config, b),
                b.ChannelId is { } c ? ChannelName(c) : null,
                states.TryGetValue(b.TokenRef, out var s) ? s.State : BotState.Idle))],
            _bot.Control.SignedIn.Count,
            _bot.Devices.Count,
            _bot.Engine.Stats.FramesSent,
            _bot.Engine.Stats.PacketsDropped);
    }

    public string ChannelName(ChannelId channel) => _discord.Names.NameOf(channel);

    public string IssueConnectCode(UserId user, string host) => _bot.Codes.Issue(user, host);

    public int Revoke(UserId user) => _bot.Control.RevokeUser(user);

    public string Jungle(UserId caller, JungleCommand command)
    {
        _log.LogInformation("jungle {Command} by {User}", command, caller);
        return _bot.Jungle.Handle(command);
    }

    public string Redact(string text) => _redactor.Redact(text);

    public Task<bool> PostAsync(ChannelId channel, string text, CancellationToken cancellationToken) =>
        _discord.PostAsync(channel, _redactor.Redact(text), cancellationToken);

    /// <summary>Posts to the alerts channel, if the config names one; a post that fails is logged and dropped.</summary>
    public async Task AlertAsync(string text, CancellationToken cancellationToken)
    {
        if (_bot.Config.AlertsChannelId is { } channel && !await PostAsync(channel, text, cancellationToken).ConfigureAwait(false))
        {
            _log.LogWarning("an alert couldn't be posted to the alerts channel");
        }
    }

    /// <summary>
    /// Watches the relay bots until <paramref name="cancellationToken"/> stops it, posting to the alerts channel
    /// when one stays out of its channel and when it is back. <paramref name="every"/> is how often it looks.
    /// </summary>
    public async Task WatchAsync(AlertMonitor monitor, TimeSpan every, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await CheckAsync(monitor, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>One look by <see cref="WatchAsync"/>: what the monitor finds is posted.</summary>
    public async Task CheckAsync(AlertMonitor monitor, CancellationToken cancellationToken)
    {
        foreach (var message in monitor.Check(_bot.Config, _discord.Running, _discord.Bots, ChannelName))
        {
            _log.LogWarning("{Alert}", message);
            await AlertAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private void FlushIfLive()
    {
        if (_live)
        {
            _ = FlushAsync(CancellationToken.None);
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var guild = Interlocked.Exchange(ref _learnedGuild, 0);
        var bots = _learnedBots.ToArray();
        _learnedBots.Clear();
        if (guild == 0 && bots.Length == 0)
        {
            return;
        }
        try
        {
            var outcome = await EditAsync(config =>
            {
                var adopt = guild != 0 && config.GuildId is null;
                var learn = config.Bots.Where(b => bots.Any(l => l.Key == b.TokenRef && l.Value != b.BotUserId?.V)).ToHashSet();
                if (!adopt && learn.Count == 0)
                {
                    return new ConfigEdit.Refuse("");
                }
                return new ConfigEdit.Change(
                    config with
                    {
                        GuildId = adopt ? guild : config.GuildId,
                        Bots = [.. config.Bots.Select(b => learn.Contains(b) ? b with { BotUserId = new UserId(bots.First(l => l.Key == b.TokenRef).Value) } : b)],
                    },
                    "");
            }, cancellationToken).ConfigureAwait(false);
            if (!outcome.Done && outcome.Message.Length > 0)
            {
                _log.LogWarning("what the bots learned couldn't be saved: {Reason}", outcome.Message);
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            _log.LogWarning("what the bots learned couldn't be saved: {Error}", exception.GetType().Name);
        }
    }
}
