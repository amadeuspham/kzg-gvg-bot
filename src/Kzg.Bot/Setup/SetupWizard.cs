using System.Collections;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Pairing;
using Kzg.Discord.Setup;
using Kzg.Protocol;

namespace Kzg.Bot.Setup;

public enum SetupStep
{
    /// <summary>Waiting for the bot tokens.</summary>
    Tokens,

    /// <summary>The tokens are good; waiting for every bot to be in the server.</summary>
    Invite,

    /// <summary>The files are written; the bot is starting.</summary>
    Done,
}

/// <summary>One bot as the page shows it: its name in Discord, never its token.</summary>
/// <param name="Slot">The token field it was pasted in, from 1, which names the channel it fills: see <see cref="RelayNames.ForSlot"/>.</param>
/// <param name="Servers">The servers it is in at the last look; null when Discord couldn't be asked.</param>
/// <param name="Primary">The first bot pasted, which runs the commands.</param>
public sealed record SetupBotView(int Slot, string Name, ulong ApplicationId, IReadOnlyList<ServerRef>? Servers, bool Primary);

/// <summary>What the page shows now.</summary>
/// <param name="FieldErrors">Per token field, from the last submit; null where the field was fine.</param>
public sealed record SetupView(
    SetupStep Step,
    IReadOnlyList<SetupBotView> Bots,
    IReadOnlyList<ServerRef> Shared,
    string? PublicHost,
    string? Problem,
    IReadOnlyList<string?> FieldErrors);

/// <summary>How a setup link's key fared.</summary>
public enum KeyUse
{
    Accepted,

    /// <summary>The key was good once and is spent; a new one has been printed.</summary>
    Spent,

    Wrong,
}

/// <summary>
/// The first-run setup, free of the web: the one-time key, the browser session it opens, the pasted tokens
/// checked with Discord, the wait for every bot to be in one server, and the files written at the end. The
/// tokens live in memory until <see cref="FinishAsync"/> writes them to <c>secrets.json</c>, owner-only, and
/// nothing here returns, prints or logs one.
/// </summary>
public sealed class SetupWizard
{
    /// <summary>One relay bot per team voice channel: three teams in each of the two big teams.</summary>
    public const int MaxBots = RelayNames.Slots;

    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(3);

    private readonly IBotAccounts _accounts;
    private readonly DataPaths _paths;
    private readonly BotConfig? _existing;
    private readonly ITimeSource _time;
    private readonly IDictionary _environment;
    private readonly Action<string> _printKey;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _working = new(1, 1);
    private readonly TaskCompletionSource<BotConfig> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string? _keyHash;
    private string? _spentKeyHash;
    private string? _sessionHash;
    private SetupStep _step = SetupStep.Tokens;
    private List<(string Token, BotAccount Account, int Slot)> _bots = [];
    private List<IReadOnlyList<ServerRef>?> _servers = [];
    private TimeSpan? _lookedAt;
    private string? _publicHost;
    private string? _problem;
    private IReadOnlyList<string?> _fieldErrors = [];

    /// <param name="existing">The config of an earlier setup, when running it again: its other settings stay.</param>
    /// <param name="publicHost">The players' address to offer, from the earlier config or the environment.</param>
    /// <param name="printKey">Shows a new setup key to whoever runs the bot: the console, never the log.</param>
    public SetupWizard(IBotAccounts accounts, DataPaths paths, BotConfig? existing, string? publicHost, ITimeSource time, Action<string> printKey, IDictionary? environment = null)
    {
        (_accounts, _paths, _existing, _publicHost, _time, _printKey) = (accounts, paths, existing, publicHost, time, printKey);
        _environment = environment ?? Environment.GetEnvironmentVariables();
    }

    /// <summary>Completes with the config written once setup is done.</summary>
    public Task<BotConfig> Completed => _completed.Task;

    /// <summary>Makes the key for the setup link and shows it; the one before stops working.</summary>
    public void IssueKey()
    {
        var key = WireSecret.Generate();
        lock (_gate)
        {
            _keyHash = SecretHash.OfWire(key);
        }
        _printKey(key);
    }

    /// <summary>
    /// Trades the link's key for a browser session, once. The spent key, shown again, prints a fresh one, so
    /// a second browser can be let in from the console; a key never issued gets nothing.
    /// </summary>
    public KeyUse UseKey(string? key, out string? session)
    {
        session = null;
        var hash = SecretHash.OfWire(key);
        lock (_gate)
        {
            if (hash is not null && _keyHash is { } good && SecretHash.Same(hash, good))
            {
                session = WireSecret.Generate();
                _sessionHash = SecretHash.OfWire(session);
                (_spentKeyHash, _keyHash) = (good, null);
                return KeyUse.Accepted;
            }
            if (hash is null || _spentKeyHash is not { } spent || !SecretHash.Same(hash, spent))
            {
                return KeyUse.Wrong;
            }
        }
        IssueKey();
        return KeyUse.Spent;
    }

    /// <summary>Whether the cookie is the session the key opened; only the latest one counts.</summary>
    public bool IsSession(string? cookie)
    {
        var hash = SecretHash.OfWire(cookie);
        lock (_gate)
        {
            return hash is not null && _sessionHash is { } session && SecretHash.Same(hash, session);
        }
    }

    public SetupView View()
    {
        lock (_gate)
        {
            var bots = Views();
            return new SetupView(_step, bots, Shared(bots), _publicHost, _problem, _fieldErrors);
        }
    }

    /// <summary>Checks each pasted token with Discord; on to the invite step when every one is a bot's.</summary>
    /// <param name="pasted">The token fields in order; blanks are skipped, so "1, 3" are two bots, the A1 and A3 Broadcasters.</param>
    public async Task SubmitAsync(IReadOnlyList<string?> pasted, string? publicHost, CancellationToken cancellationToken)
    {
        await _working.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var errors = new string?[pasted.Count];
            var host = string.IsNullOrWhiteSpace(publicHost) ? null : publicHost.Trim();
            string? problem = null;
            if (host is not null && !ConnectCode.IsValidHost(host))
            {
                problem = "The public address isn't a web address such as gvg.example.com. Leave it empty if you don't have one yet.";
            }
            List<(string Token, BotAccount Account, int Field)> accepted = [];
            for (var i = 0; i < pasted.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(pasted[i]))
                {
                    continue;
                }
                if (DiscordBotAccounts.Normalize(pasted[i]) is not { } token)
                {
                    errors[i] = "This isn't a bot token. Copy it from the Bot tab with Reset Token, not the client secret or the application ID.";
                    continue;
                }
                switch (await _accounts.CheckAsync(token, cancellationToken).ConfigureAwait(false))
                {
                    case TokenCheck.Accepted ok when accepted.Any(a => a.Account.ApplicationId == ok.Account.ApplicationId):
                        errors[i] = $"This is {ok.Account.Name} again. Each relay bot is a bot account of its own.";
                        break;
                    case TokenCheck.Accepted ok:
                        accepted.Add((token, ok.Account, i));
                        break;
                    case TokenCheck.Refused:
                        errors[i] = "Discord doesn't accept this token. Reset it in the Bot tab and paste the new one.";
                        break;
                    default:
                        errors[i] = "Discord couldn't be reached to check this token. Check the internet connection and try again.";
                        break;
                }
            }
            if (accepted.Count == 0 && errors.All(e => e is null))
            {
                problem ??= "Paste at least one bot token.";
            }
            lock (_gate)
            {
                _fieldErrors = errors;
                _problem = problem;
                if (problem is not null || errors.Any(e => e is not null))
                {
                    return;
                }
                _publicHost = host ?? _publicHost;
                _bots = [.. accepted.Select(a => (a.Token, a.Account, a.Field + 1))];
                _servers = [.. accepted.Select(_ => (IReadOnlyList<ServerRef>?)null)];
                _lookedAt = null;
                _step = SetupStep.Invite;
            }
        }
        finally
        {
            _working.Release();
        }
    }

    /// <summary>Back to the tokens, forgetting the ones pasted.</summary>
    public void Back()
    {
        lock (_gate)
        {
            if (_step != SetupStep.Invite)
            {
                return;
            }
            (_step, _bots, _servers, _problem, _fieldErrors) = (SetupStep.Tokens, [], [], null, []);
        }
    }

    /// <summary>Asks Discord which servers each bot is in, at most every few seconds however often the page reloads.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        await _working.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<string> tokens;
            lock (_gate)
            {
                if (_step != SetupStep.Invite || _lookedAt is { } at && _time.Now - at < LookEvery)
                {
                    return;
                }
                tokens = [.. _bots.Select(b => b.Token)];
            }
            var servers = await Task.WhenAll(tokens.Select(t => _accounts.ServersAsync(t, cancellationToken))).ConfigureAwait(false);
            lock (_gate)
            {
                _servers = [.. servers];
                _lookedAt = _time.Now;
                _problem = null;
            }
        }
        finally
        {
            _working.Release();
        }
    }

    /// <summary>Writes <c>secrets.json</c> and <c>config.json</c> for the bots in <paramref name="server"/>, or the one server they share.</summary>
    /// <returns>Whether it is done; when not, the view's problem says why.</returns>
    public async Task<bool> FinishAsync(ulong? server, CancellationToken cancellationToken)
    {
        await _working.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<(string Token, BotAccount Account, int Slot)> bots;
            ulong guild;
            lock (_gate)
            {
                if (_step != SetupStep.Invite)
                {
                    return _step == SetupStep.Done;
                }
                var shared = Shared(Views());
                ulong? chosen = server is { } wanted ? shared.FirstOrDefault(s => s.Id == wanted)?.Id : shared.Count == 1 ? shared[0].Id : null;
                if (chosen is not { } id)
                {
                    _problem = shared.Count == 0
                        ? "Not every bot is in the same server yet. Invite each one with its button, then try again."
                        : "Pick the server the bots are for.";
                    return false;
                }
                (bots, guild) = (_bots, id);
            }
            var problem = Write(bots, guild);
            lock (_gate)
            {
                _problem = problem;
                if (problem is not null)
                {
                    return false;
                }
                _step = SetupStep.Done;
                _bots = [.. _bots.Select(b => ("", b.Account, b.Slot))];
            }
            return true;
        }
        finally
        {
            _working.Release();
        }
    }

    /// <summary>Lets the bot go on to run; called once the page that says so has been sent.</summary>
    public void Release(BotConfig config) => _completed.TrySetResult(config);

    /// <summary>The config just written, once done.</summary>
    public BotConfig? Written { get; private set; }

    private string? Write(IReadOnlyList<(string Token, BotAccount Account, int Slot)> bots, ulong guild)
    {
        // The field, not the count, names the token, so /gvg setup can put each bot in the channel its field showed.
        var names = bots.Select(b => SetupPlans.TokenRefFor(b.Slot)).ToList();
        var config = SetupPlans.WithBots(
            _existing,
            [.. bots.Select((b, i) => new SetupBot(names[i], b.Account.BotUserId is { } id ? new UserId(id) : null))],
            guild,
            _publicHost);
        // Tokens of bots the earlier setup had and this one doesn't are taken off the disk.
        var stale = (_existing?.Bots.Select(b => b.TokenRef) ?? []).Except(names, StringComparer.Ordinal);
        var store = new SecretsStore(_paths.SecretsFile);
        switch (store.Save(names.Select((n, i) => (n, bots[i].Token)).ToDictionary(p => p.n, p => p.Item2, StringComparer.Ordinal), stale))
        {
            case FileSave.Invalid invalid:
                return string.Join("; ", invalid.Errors.Select(e => e.Problem));
            case FileSave.Unwritable:
                return $"secrets.json couldn't be written in {_paths.Folder}. Check that the bot may write there.";
        }
        if (store.Load(_environment) is not FileLoad<Secrets>.Loaded { Value: var secrets })
        {
            return "secrets.json couldn't be read back.";
        }
        // Discord sign-in is optional: an earlier config that had it on without its client secret turns it off.
        if (config.OAuth.Enabled && !secrets.Has(Secrets.OAuthClientSecretName))
        {
            config = config with { OAuth = config.OAuth with { Enabled = false } };
        }
        switch (new ConfigStore(_paths.ConfigFile).Save(config, secrets))
        {
            case FileSave.Invalid invalid:
                return "The settings would be invalid: " + string.Join("; ", invalid.Errors);
            case FileSave.Unwritable:
                return $"config.json couldn't be written in {_paths.Folder}. Check that the bot may write there.";
        }
        Written = config;
        return null;
    }

    private List<SetupBotView> Views() =>
        [.. _bots.Select((b, i) => new SetupBotView(b.Slot, b.Account.Name, b.Account.ApplicationId, _servers.ElementAtOrDefault(i), i == 0))];

    /// <summary>The servers every bot is in, in the first bot's order.</summary>
    private static List<ServerRef> Shared(IReadOnlyList<SetupBotView> bots)
    {
        if (bots.Count == 0 || bots.Any(b => b.Servers is null))
        {
            return [];
        }
        return [.. bots[0].Servers!.Where(s => bots.All(b => b.Servers!.Any(o => o.Id == s.Id)))];
    }
}
