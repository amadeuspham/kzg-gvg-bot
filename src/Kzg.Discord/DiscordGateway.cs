using System.Collections.Concurrent;
using System.Net;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Pairing;
using Kzg.Discord.Commands;
using Kzg.Discord.Dave;
using Kzg.Discord.Identity;
using Kzg.Discord.Presence;
using Kzg.Discord.Voice;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;

namespace Kzg.Discord;

/// <summary>The bot can't start for a reason an admin can fix; the message is safe to print.</summary>
public sealed class DiscordStartException(string message) : Exception(message);

/// <summary>What one relay bot is doing, for <c>/gvg status</c>.</summary>
/// <param name="NicknameRefused">Discord refused its team-name nickname for a missing Change Nickname permission.</param>
public sealed record RelayBotState(string TokenRef, bool Primary, ChannelId? Channel, BotState State, long FramesSent, long SendErrors, long DecryptFailures = 0, bool NicknameRefused = false);

/// <summary>
/// Discord for the engine: a bot account per relay channel, each its own gateway connection, and the primary
/// bot also watching who is where, naming channels and answering slash commands. It is the one
/// <see cref="IVoiceGateway"/> the engine is built on; which bots sit in which channels follows
/// <see cref="ApplyAsync"/>, so links come and go while it runs.
/// </summary>
public sealed class DiscordGateway : IDiscordRuntime
{
    private static readonly TimeSpan GuildTimeout = TimeSpan.FromSeconds(30);

    private readonly Secrets _secrets;
    private readonly ITimeSource _time;
    private readonly OwnBots _ownBots = new();
    private readonly PresenceTracker _presence;
    private readonly Dictionary<string, RelayBot> _bots = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly InteractionBridge _bridge;
    private readonly object _linksGate = new();
    private readonly TaskCompletionSource _guildSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile IReadOnlyList<IVoiceChannelLink> _links = [];
    private volatile BotConfig _config;
    private volatile GvgCommands? _commands;
    private readonly LateLogger _log = new();
    private ILoggerFactory? _loggers;
    private RelayBot? _primary;
    private ulong _guildId;
    private ulong _applicationId;
    private string _registeredFor = "";
    private bool _running = true;
    private bool _disposed;

    /// <param name="http">Talks to Discord's HTTP API for sign-in; the gateway owns one when none is given.</param>
    public DiscordGateway(BotConfig config, Secrets secrets, ITimeSource time, HttpClient? http = null)
    {
        (_config, _secrets, _time) = (config, secrets, time);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 64 * 1024 };
        _presence = new PresenceTracker((user, channel) => PresenceChanged(user, channel), _ownBots.Contains);
        Names = new ChannelNames();
        Identity = new DiscordIdentity(_http, OAuthFor, () => GuildId, new NetCordGuildDirectory(() => _primary?.Gateway?.Rest), _log);
        _bridge = new InteractionBridge(() => _commands, _log);
        foreach (var bot in config.Bots.Where(b => b.BotUserId is not null))
        {
            _ownBots.Add(bot.BotUserId!.Value.V);
        }
    }

    public IReadOnlyList<IVoiceChannelLink> Links => _links;

    public event Action<UserId, ChannelId?> PresenceChanged = delegate { };

    public event Action<IVoiceChannelLink> LinkAdded = delegate { };

    public event Action<IVoiceChannelLink> LinkRemoved = delegate { };

    public event Action<ulong>? GuildResolved;

    public event Action<string, ulong>? BotIdLearned;

    public ChannelNames Names { get; }

    public IDiscordIdentity Identity { get; }

    /// <summary>The server's ID once the primary bot has connected; null before.</summary>
    public ulong? GuildId => _guildId == 0 ? null : _guildId;

    public IReadOnlyList<RelayBotState> Bots
    {
        get
        {
            lock (_linksGate)
            {
                return [.. _bots.Values.Select(bot => bot.State())];
            }
        }
    }

    public bool Running => _running;

    /// <summary>
    /// Signs the primary bot in, learns the server, takes the first look at who is in voice (which raises
    /// presence), and tells Discord the slash commands. Throws <see cref="DiscordStartException"/> when
    /// that can't be done. Call it after the engine is subscribed, or the first presence is missed.
    /// </summary>
    public async Task StartAsync(ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        _loggers = loggers;
        _log.Set(loggers.CreateLogger("Kzg.Discord"));
        var check = DaveCheck.Run();
        if (!check.Ok)
        {
            throw new DiscordStartException(check.Message);
        }
        _log.LogInformation("{Dave}", check.Message);
        var config = _config;
        var primaryEntry = config.Bots.FirstOrDefault(b => b.Primary) ?? throw new DiscordStartException("config has no primary bot");
        var primary = BotFor(primaryEntry);
        _primary = primary;
        ReadyEventArgs ready;
        try
        {
            ready = await primary.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or DiscordStartException or OutOfMemoryException))
        {
            throw new DiscordStartException($"the primary bot, {RelayNames.Of(config, primaryEntry)}, couldn't sign in to Discord ({exception.GetType().Name}); check its token '{primary.TokenRef}' in secrets.json");
        }
        _guildId = ChooseGuild(config, ready);
        _applicationId = ready.ApplicationId;
        if (config.GuildId is null)
        {
            _log.LogInformation("the bot serves guild {Guild}, the only server it is in", _guildId);
            GuildResolved?.Invoke(_guildId);
        }
        // The snapshot may have arrived before the guild was known; it is read again from the cache if so.
        primary.GuildId = _guildId;
        primary.ReplayGuild();
        try
        {
            await _guildSeen.Task.WaitAsync(GuildTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new DiscordStartException($"Discord didn't send the server's details within {GuildTimeout.TotalSeconds:0} s");
        }
        await RegisterCommandsAsync(config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>From now on slash commands are answered by <paramref name="control"/>.</summary>
    public void AttachControl(IGvgControl control) => _commands = new GvgCommands(control);

    /// <summary>Puts the relay bots where the config says; with <paramref name="running"/> false, nowhere.</summary>
    public async Task ApplyAsync(BotConfig config, bool running, CancellationToken cancellationToken)
    {
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _config = config;
            _running = running;
            foreach (var entry in config.Bots)
            {
                var bot = BotFor(entry);
                bot.Nickname.Wanted = RelayNames.Team(config, entry);
                if (bot.Gateway is not null)
                {
                    // Not awaited: a slow or refused rename must not hold up the bots' moves.
                    _ = bot.Nickname.SyncAsync(CancellationToken.None);
                }
                var wanted = running ? entry.ChannelId : null;
                if (bot.Link is { } link && (wanted is null || link.Channel != wanted))
                {
                    await RemoveLinkAsync(bot, link).ConfigureAwait(false);
                }
                if (bot.Link is null && wanted is { } channel)
                {
                    AddLink(bot, channel);
                }
            }
            if (_guildId != 0)
            {
                await RegisterCommandsAsync(config, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async Task<bool> PostAsync(ChannelId channel, string text, CancellationToken cancellationToken)
    {
        if (_primary?.Gateway is not { } gateway)
        {
            return false;
        }
        try
        {
            await gateway.Rest.SendMessageAsync(channel.V, new MessageProperties
            {
                Content = CommandReply.Of(text).Text,
                AllowedMentions = AllowedMentionsProperties.None,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            _log.LogWarning("a message to channel {Channel} couldn't be posted ({Error}); can the primary bot see the channel and send messages there?", channel.V, exception.GetType().Name);
            return false;
        }
    }

    /// <summary>Takes every bot out of voice and signs them all out of Discord.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _commands = null;
            RelayBot[] bots;
            lock (_linksGate)
            {
                bots = [.. _bots.Values];
            }
            foreach (var bot in bots)
            {
                if (bot.Link is { } link)
                {
                    await RemoveLinkAsync(bot, link).ConfigureAwait(false);
                }
            }
            await Task.WhenAll(bots.Select(bot => bot.DisconnectAsync())).ConfigureAwait(false);
        }
        finally
        {
            _applyGate.Release();
        }
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    // ---- Links

    private void AddLink(RelayBot bot, ChannelId channel)
    {
        var link = new RelayLink(channel, () => bot.UserId, _time, _ownBots, _loggers?.CreateLogger($"Kzg.Discord.Link.{bot.TokenRef}"));
        var joiner = new LazyJoiner(bot, () => _guildId, _loggers);
        var supervisor = new LinkSupervisor(link, joiner, _loggers?.CreateLogger($"Kzg.Discord.Join.{bot.TokenRef}"), name: () => RelayNames.Of(_config, bot.Entry));
        bot.Link = link;
        bot.Supervisor = supervisor;
        lock (_linksGate)
        {
            _links = [.. _links, link];
        }
        LinkAdded(link);
        supervisor.Start();
    }

    private async Task RemoveLinkAsync(RelayBot bot, RelayLink link)
    {
        lock (_linksGate)
        {
            _links = [.. _links.Where(l => !ReferenceEquals(l, link))];
        }
        // The engine stops sending first; the bot leaves once nothing is on its way.
        LinkRemoved(link);
        var supervisor = bot.Supervisor;
        (bot.Link, bot.Supervisor) = (null, null);
        if (supervisor is not null)
        {
            await supervisor.StopAsync().ConfigureAwait(false);
        }
        link.Dispose();
    }

    private RelayBot BotFor(BotEntry entry)
    {
        lock (_linksGate)
        {
            if (_bots.TryGetValue(entry.TokenRef, out var existing))
            {
                existing.Entry = entry;
                return existing;
            }
            var bot = new RelayBot(entry, this);
            _bots[entry.TokenRef] = bot;
            return bot;
        }
    }

    // ---- Commands

    private async Task RegisterCommandsAsync(BotConfig config, CancellationToken cancellationToken)
    {
        // The bot choices of /gvg bot assign show each bot's team name, so a rename registers them again.
        var fingerprint = $"{config.AdminRoleId is null}|{string.Join(',', config.Bots.Select(b => $"{b.TokenRef}={RelayNames.Of(config, b)}"))}";
        if (fingerprint == _registeredFor || _primary?.Gateway is not { } gateway)
        {
            return;
        }
        try
        {
            var registered = await gateway.Rest.BulkOverwriteGuildApplicationCommandsAsync(
                _applicationId, _guildId, CommandSpec.Build(config), cancellationToken: cancellationToken).ConfigureAwait(false);
            _registeredFor = fingerprint;
            _log.LogInformation("registered {Count} slash command(s): {Names}", registered.Count, string.Join(", ", registered.Select(c => "/" + c.Name)));
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            _log.LogWarning("the slash commands couldn't be registered ({Error}); is the primary bot invited with the applications.commands scope?", exception.GetType().Name);
        }
    }

    private OAuthCredentials? OAuthFor()
    {
        var oauth = _config.OAuth;
        return oauth is { Enabled: true, ClientId: { } id } && _secrets.TryGet(Secrets.OAuthClientSecretName, out var secret)
            ? new OAuthCredentials(id, secret) : null;
    }

    private ulong ChooseGuild(BotConfig config, ReadyEventArgs ready)
    {
        if (config.GuildId is { } wanted)
        {
            return ready.GuildIds.Contains(wanted)
                ? wanted
                : throw new DiscordStartException($"the primary bot isn't in server {wanted}; invite it with its OAuth2 link");
        }
        return ready.GuildIds.Count switch
        {
            1 => ready.GuildIds[0],
            0 => throw new DiscordStartException("the primary bot isn't in any server; invite it with its OAuth2 link"),
            _ => throw new DiscordStartException("the primary bot is in several servers: set guildId in config.json"),
        };
    }

    // ---- One relay bot

    /// <summary>One bot account: its gateway connection (made when first needed) and its link, if it has a channel.</summary>
    private sealed class RelayBot
    {
        private readonly DiscordGateway _owner;
        private readonly SemaphoreSlim _connecting = new(1, 1);
        private ReadyEventArgs? _ready;
        private readonly ConcurrentDictionary<ulong, Guild> _guilds = new();
        private long _userId;

        public RelayBot(BotEntry entry, DiscordGateway owner)
        {
            (Entry, _owner) = (entry, owner);
            Nickname = new BotNickname(SetNicknameAsync, owner._loggers?.CreateLogger($"Kzg.Discord.{entry.TokenRef}") ?? owner._log);
        }

        public BotEntry Entry { get; set; }

        public BotNickname Nickname { get; }

        public LogStats Stats { get; } = new();

        public string TokenRef => Entry.TokenRef;

        public GatewayClient? Gateway { get; private set; }

        public RelayLink? Link { get; set; }

        public LinkSupervisor? Supervisor { get; set; }

        public ulong GuildId { get; set; }

        public ulong UserId => (ulong)Interlocked.Read(ref _userId) is var id and not 0 ? id : Entry.BotUserId?.V ?? 0;

        public RelayBotState State() => new(
            TokenRef,
            Entry.Primary,
            Link?.Channel,
            Link is null ? BotState.Idle : Link.Ready ? BotState.Ready : BotState.Joining,
            Link?.FramesSent ?? 0,
            Link?.SendErrors ?? 0,
            Stats.DecryptFailures,
            Nickname.Refused);

        private async Task<NicknameResult> SetNicknameAsync(string? name, CancellationToken cancellationToken)
        {
            if (Gateway is not { } gateway || _owner._guildId == 0)
            {
                return NicknameResult.Failed;
            }
            try
            {
                // An empty nickname clears it, back to the account's own name.
                await gateway.Rest.ModifyCurrentGuildUserAsync(_owner._guildId, o => o.Nickname = name ?? "", cancellationToken: cancellationToken).ConfigureAwait(false);
                return NicknameResult.Set;
            }
            catch (RestException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
            {
                return NicknameResult.Refused;
            }
        }

        /// <summary>Signs the bot in if it isn't; every caller gets the same connection.</summary>
        public async Task<ReadyEventArgs> ConnectAsync(CancellationToken cancellationToken)
        {
            await _connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Gateway is not null && _ready is { } done)
                {
                    return done;
                }
                if (!_owner._secrets.TryGet(TokenRef, out var token))
                {
                    throw new DiscordStartException($"no secret called '{TokenRef}'");
                }
                var log = _owner._loggers?.CreateLogger($"Kzg.Discord.{TokenRef}") ?? _owner._log;
                var ready = new TaskCompletionSource<ReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                var client = new GatewayClient(new BotToken(token), new GatewayClientConfiguration
                {
                    Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
                    Logger = new NetCordLog(log, TokenRef),
                });
                client.Ready += args =>
                {
                    Interlocked.Exchange(ref _userId, (long)args.User.Id);
                    _owner.OnBotIdentified(this, args.User.Id);
                    ready.TrySetResult(args);
                    return default;
                };
                if (Entry.Primary)
                {
                    _owner.Watch(client, this);
                }
                Gateway = client;
                try
                {
                    await client.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    _ready = await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    Gateway = null;
                    client.Dispose();
                    throw;
                }
                return _ready;
            }
            finally
            {
                _connecting.Release();
            }
        }

        public void Remember(Guild guild) => _guilds[guild.Id] = guild;

        public void ReplayGuild()
        {
            if (_guilds.TryGetValue(GuildId, out var guild))
            {
                _owner.OnGuild(guild);
            }
        }

        public async Task DisconnectAsync()
        {
            var client = Gateway;
            Gateway = null;
            _ready = null;
            if (client is null)
            {
                return;
            }
            try
            {
                await client.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _owner._log.LogDebug("closing {Bot}'s gateway failed: {Error}", RelayNames.Of(_owner._config, Entry), exception.GetType().Name);
            }
            client.Dispose();
        }
    }

    /// <summary>Signs a bot in on its first join, so a bot with no channel never connects.</summary>
    private sealed class LazyJoiner(RelayBot bot, Func<ulong> guildId, ILoggerFactory? loggers) : IVoiceJoiner
    {
        public async Task<IVoiceConnection> JoinAsync(ChannelId channel, CancellationToken cancellationToken)
        {
            await bot.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _ = bot.Nickname.SyncAsync(CancellationToken.None);
            var gateway = bot.Gateway ?? throw new InvalidOperationException("the bot isn't signed in");
            var log = loggers?.CreateLogger($"Kzg.Discord.Voice.{bot.TokenRef}");
            var joiner = new NetCordJoiner(gateway, guildId(), () => new VoiceClientConfiguration
            {
                Logger = log is null ? null : new NetCordLog(log, bot.TokenRef, bot.Stats),
                ExternalSocketAddressDiscoveryTimeout = TimeSpan.FromSeconds(10),
            }, bot.UserId);
            return await joiner.JoinAsync(channel, cancellationToken).ConfigureAwait(false);
        }

        public async Task LeaveAsync(CancellationToken cancellationToken)
        {
            if (bot.Gateway is { } gateway && guildId() != 0)
            {
                await new NetCordJoiner(gateway, guildId(), () => new VoiceClientConfiguration()).LeaveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // ---- What the primary bot watches

    private void OnBotIdentified(RelayBot bot, ulong id)
    {
        _ownBots.Add(id);
        // A relay bot left in voice by an earlier run must not count as a player.
        _presence.Drop(id);
        if (bot.Entry.BotUserId?.V != id)
        {
            BotIdLearned?.Invoke(bot.TokenRef, id);
        }
    }

    private void Watch(GatewayClient client, RelayBot primary)
    {
        client.GuildCreate += args =>
        {
            if (args.Guild is { } guild)
            {
                primary.Remember(guild);
                if (guild.Id == _guildId)
                {
                    OnGuild(guild);
                }
            }
            return default;
        };
        client.VoiceStateUpdate += state =>
        {
            if (state.GuildId == _guildId)
            {
                _presence.Update(state.UserId, state.ChannelId);
            }
            return default;
        };
        client.GuildChannelCreate += channel => NameChannel(channel);
        client.GuildChannelUpdate += channel => NameChannel(channel);
        client.GuildChannelDelete += channel =>
        {
            if (channel.GuildId == _guildId)
            {
                Names.Remove(channel.Id);
            }
            return default;
        };
        client.InteractionCreate += _bridge.OnInteraction;
    }

    /// <summary>The server's snapshot, on first connect and again after a full re-identify: names and presence are set from it.</summary>
    private void OnGuild(Guild guild)
    {
        Names.Reset(guild.Channels.Values.OfType<INamedChannel>().Select(c => (((IEntity)c).Id, c.Name)));
        _presence.Snapshot(guild.VoiceStates.Values.Where(v => v.ChannelId is not null).Select(v => (v.UserId, v.ChannelId!.Value)));
        _guildSeen.TrySetResult();
    }

    private ValueTask NameChannel(IGuildChannel channel)
    {
        if (channel.GuildId == _guildId && channel is INamedChannel named)
        {
            Names.Set(channel.Id, named.Name);
        }
        return default;
    }
}
