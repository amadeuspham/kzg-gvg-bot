using Kzg.Bot.Fake;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Pairing;
using Kzg.Core.Testing;
using Kzg.Discord;
using Kzg.Discord.Commands;
using Kzg.Discord.Presence;
using Microsoft.Extensions.Logging;

namespace Kzg.Bot.Tests.Hosting;

/// <summary>
/// The Discord adapter with no Discord: bots become fake links as the config puts them in channels, and
/// the host's startup, commands and shutdown run over it exactly as over the real one.
/// </summary>
internal sealed class FakeDiscordRuntime(ITimeSource time) : IDiscordRuntime
{
    private readonly Dictionary<string, FakeVoiceChannelLink> _links = [];
    private readonly object _gate = new();

    public IVoiceChannelLink[] Current
    {
        get
        {
            lock (_gate)
            {
                return [.. _links.Values];
            }
        }
    }

    public IReadOnlyList<IVoiceChannelLink> Links => Current;

    public event Action<UserId, ChannelId?> PresenceChanged = delegate { };

    public event Action<IVoiceChannelLink> LinkAdded = delegate { };

    public event Action<IVoiceChannelLink> LinkRemoved = delegate { };

    public event Action<ulong>? GuildResolved;

    public event Action<string, ulong>? BotIdLearned;

    public ChannelNames Names { get; } = new();

    public IDiscordIdentity Identity { get; set; } = new TableDiscordIdentity(DemoWorld.Members);

    public bool Running { get; private set; } = true;

    public IReadOnlyList<RelayBotState> Bots { get; set; } = [];

    public IGvgControl? Control { get; private set; }

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    public Exception? FailStart { get; set; }

    public ulong? ResolveGuild { get; set; }

    public Dictionary<string, ulong> LearnBots { get; } = [];

    public int Applies { get; private set; }

    public List<UserId> JoinedAtStart { get; } = [];

    public Task StartAsync(ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (FailStart is { } failure)
        {
            throw failure;
        }
        Started = true;
        if (ResolveGuild is { } guild)
        {
            GuildResolved?.Invoke(guild);
        }
        foreach (var (name, id) in LearnBots)
        {
            BotIdLearned?.Invoke(name, id);
        }
        return Task.CompletedTask;
    }

    public void AttachControl(IGvgControl control) => Control = control;

    public Task ApplyAsync(BotConfig config, bool running, CancellationToken cancellationToken)
    {
        Applies++;
        Running = running;
        foreach (var bot in config.Bots)
        {
            var wanted = running ? bot.ChannelId : null;
            FakeVoiceChannelLink? existing;
            lock (_gate)
            {
                _links.TryGetValue(bot.TokenRef, out existing);
            }
            if (existing is not null && (wanted is null || existing.Channel != wanted))
            {
                lock (_gate)
                {
                    _links.Remove(bot.TokenRef);
                }
                LinkRemoved(existing);
                existing = null;
            }
            if (existing is null && wanted is { } channel)
            {
                var link = new FakeVoiceChannelLink(channel, 9_000_000_000UL + channel.V, time);
                lock (_gate)
                {
                    _links[bot.TokenRef] = link;
                }
                LinkAdded(link);
            }
        }
        return Task.CompletedTask;
    }

    public void Join(UserId user, ChannelId? channel) => PresenceChanged(user, channel);

    /// <summary>What the primary bot posted, in order.</summary>
    public List<(ChannelId Channel, string Text)> Posts { get; } = [];

    /// <summary>Discord refuses every post, as when the bot may not send in the channel.</summary>
    public bool RefusePosts { get; set; }

    public Task<bool> PostAsync(ChannelId channel, string text, CancellationToken cancellationToken)
    {
        lock (Posts)
        {
            if (RefusePosts)
            {
                return Task.FromResult(false);
            }
            Posts.Add((channel, text));
            return Task.FromResult(true);
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
