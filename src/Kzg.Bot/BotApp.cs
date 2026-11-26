using System.Net;
using Kzg.Bot.Control;
using Kzg.Bot.Engine;
using Kzg.Bot.Http;
using Kzg.Bot.Jungle;
using Kzg.Bot.Logging;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Jungle;
using Kzg.Core.Pairing;
using Kzg.Core.Routing;
using Kzg.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kzg.Bot;

/// <summary>
/// The running bot: Kestrel with <c>/ws</c>, <c>/auth/*</c> and <c>/healthz</c>, the control server behind
/// them, and the audio engine thread, wired so presses and heartbeats reach routing and
/// routing's statuses reach the apps. It owns what it builds and none of what it is given.
/// </summary>
public sealed class BotApp : IAsyncDisposable
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromHours(1);

    private readonly WebApplication _web;
    private readonly BotSetup _setup;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stopSweeping = new();
    private readonly Lock _statusGate = new();
    private readonly Dictionary<UserId, (Core.Routing.UserStatus Status, IReadOnlyList<ChannelId> BotDownIn)> _lastStatus = [];
    private volatile BotConfig _config;
    private Task _sweeping = Task.CompletedTask;
    private Task _ticking = Task.CompletedTask;
    private readonly JungleSource _jungle;
    private int _jungleVolume;
    private int _jungleVersion;
    private Task _jungleReload = Task.CompletedTask;
    private bool _started;
    private bool _stopped;

    private BotApp(BotSetup setup, WebApplication web, DeviceRegistry devices, ConnectCodes codes, PendingLogins logins)
    {
        (_setup, _web, Devices, Codes, Logins, _config) = (setup, web, devices, codes, logins, setup.Config);
        var loggers = web.Services.GetRequiredService<ILoggerFactory>();
        Loggers = loggers;
        _log = loggers.CreateLogger("Kzg.Bot");
        var jungleLog = loggers.CreateLogger("Kzg.Bot.Jungle");
        _jungleVolume = setup.Config.JungleVolume;
        _jungle = new JungleSource(JungleClips.Load(setup.Codecs, _jungleVolume));
        Engine = new AudioEngine(setup.Config, setup.Gateway, setup.Codecs, setup.Time, setup.Engine,
            loggers.CreateLogger("Kzg.Bot.Engine"), sources: [_jungle]);
        Jungle = new JungleMatch(setup.Time, _jungle.Play, JungleReach, message => jungleLog.LogInformation("{Message}", message));
        Control = new ControlServer(setup.Control, setup.Time, TimeProvider.System, codes, logins, devices, setup.Discord, Engine);
        Control.Edge += Engine.OnEdge;
        Engine.StatusChanged += SendStatus;
        Engine.LinksChanged += ResendBotDownNames;
        var auth = new AuthEndpoints(logins, setup.Discord, () => SignInFor(_config));
        var proxies = setup.Config.TrustedProxies.Select(text => ConfigValidation.TryParseNetwork(text, out var network)
            ? network
            : throw new StartupException("config 'trustedProxies' holds an entry that is not an IP address or CIDR network")).ToList();
        Endpoints.Map(web, Control, auth, new RateLimits(setup.Limits, proxies, loggers.CreateLogger("Kzg.Bot.Http"), TimeProvider.System));
    }

    public AudioEngine Engine { get; }

    /// <summary>The host's loggers, which redact every secret; the Discord adapter logs through them.</summary>
    public ILoggerFactory Loggers { get; }

    public ControlServer Control { get; }

    /// <summary>Completes when the latest jungle clip re-encode, started by a volume change in <see cref="Reconfigure"/>, is done.</summary>
    public Task JungleClipsReady => Volatile.Read(ref _jungleReload);

    /// <summary>The jungle-call clock; <c>/jungle</c> drives it, and a timer started with the bot plays its calls.</summary>
    public JungleMatch Jungle { get; }

    public ConnectCodes Codes { get; }

    public PendingLogins Logins { get; }

    public DeviceRegistry Devices { get; }

    /// <summary>The config in force; <see cref="Reconfigure"/> replaces it.</summary>
    public BotConfig Config => _config;

    /// <summary>Where Kestrel is listening, with the real port when the setup asked for any.</summary>
    public Uri Address
    {
        get
        {
            var addresses = _web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            return new Uri(addresses?.FirstOrDefault() ?? throw new InvalidOperationException("the bot isn't listening yet"));
        }
    }

    /// <exception cref="StartupException">The listen address or <c>devices.json</c> is wrong.</exception>
    public static BotApp Create(BotSetup setup)
    {
        var endpoint = setup.Listen ?? ParseListen(setup.Config.Listen);
        var devices = LoadDevices(setup);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.AddRedactingLogger(setup.Log, Redactor.For(setup.Secrets), setup.LogLevel, setup.QuietFramework);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = setup.Limits.Connections;
            kestrel.Listen(endpoint);
        });
        builder.Services.AddRateLimiter(_ => { });
        var web = builder.Build();
        return new BotApp(setup, web, devices, new ConnectCodes(setup.Time), new PendingLogins(setup.Time));
    }

    /// <summary>A new config, such as after a <c>/gvg</c> command; routing and sign-in see it from now on.</summary>
    public void Reconfigure(BotConfig config)
    {
        _config = config;
        Engine.Configure(config);
        ReloadJungleClips(config.JungleVolume);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            throw new InvalidOperationException("the bot is already started");
        }
        _started = true;
        Engine.Start();
        await _web.StartAsync(cancellationToken).ConfigureAwait(false);
        _sweeping = SweepAsync(_stopSweeping.Token);
        _ticking = TickJungleAsync(_stopSweeping.Token);
        _log.LogInformation("listening on {Address}", Address);
    }

    /// <summary>Completes when the host is asked to shut down, by cancellation or a signal.</summary>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => _web.WaitForShutdownAsync(cancellationToken);

    /// <summary>Closes every app with "going away", stops Kestrel, then the engine.</summary>
    public async Task StopAsync()
    {
        if (!_started || _stopped)
        {
            return;
        }
        _stopped = true;
        Control.CloseAll();
        await _stopSweeping.CancelAsync().ConfigureAwait(false);
        await _web.StopAsync().ConfigureAwait(false);
        Engine.Stop();
        await _sweeping.ConfigureAwait(false);
        await _ticking.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Engine.Dispose();
        await _web.DisposeAsync().ConfigureAwait(false);
        _stopSweeping.Dispose();
    }

    private static SignInSettings? SignInFor(BotConfig config) =>
        config is { OAuth: { Enabled: true, ClientId: { } clientId }, PublicHost: { } host } ? new SignInSettings(clientId, host) : null;

    private void SendStatus(Core.Routing.UserStatus status)
    {
        var botDownIn = StatusMapper.BotDownIn(status.Group, RoutingConfig.From(_config), Engine.ReadyChannels);
        lock (_statusGate)
        {
            _lastStatus[status.User] = (status, botDownIn);
        }
        Send(status, botDownIn);
    }

    /// <summary>
    /// Routing resends a status when the <c>botDown</c> warning appears or goes, but not when a different
    /// bot goes down while it stays on. Players already warned are told the new names.
    /// </summary>
    private void ResendBotDownNames()
    {
        var config = RoutingConfig.From(_config);
        List<(Core.Routing.UserStatus Status, IReadOnlyList<ChannelId> Down)> changed = [];
        lock (_statusGate)
        {
            foreach (var (user, (status, sent)) in _lastStatus.ToArray())
            {
                if (!status.Warnings.Contains(StatusWarning.BotDown))
                {
                    continue;
                }
                var now = StatusMapper.BotDownIn(status.Group, config, Engine.ReadyChannels);
                if (!now.SequenceEqual(sent))
                {
                    _lastStatus[user] = (status, now);
                    changed.Add((status, now));
                }
            }
        }
        foreach (var (status, down) in changed)
        {
            Send(status, down);
        }
    }

    private void Send(Core.Routing.UserStatus status, IReadOnlyList<ChannelId> botDownIn)
    {
        try
        {
            Control.SendStatus(status.User, StatusMapper.ToMessage(status, _setup.ChannelNames, botDownIn));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.LogWarning("a status couldn't be sent: {Error}", exception.GetType().Name);
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SweepEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var forgotten = Devices.Sweep();
                if (forgotten > 0)
                {
                    _log.LogInformation("forgot {Count} device token(s) unused past their lifetime", forgotten);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>How many team channels a jungle call would reach now: the bound ones whose relay bot is in voice.</summary>
    private int JungleReach()
    {
        var config = RoutingConfig.From(_config);
        var ready = Engine.ReadyChannels;
        return new[] { Group.Attack, Group.Defense }.SelectMany(config.ChannelsOf).Distinct().Count(ready.Contains);
    }

    /// <summary>
    /// A new volume re-encodes the clips off the caller's thread and swaps them in. The latest volume wins when
    /// changes come quickly, and a failure is logged and leaves the clips as they were.
    /// </summary>
    private void ReloadJungleClips(int volume)
    {
        if (volume == _jungleVolume)
        {
            return;
        }
        _jungleVolume = volume;
        var version = Interlocked.Increment(ref _jungleVersion);
        var previous = _jungleReload;
        Volatile.Write(ref _jungleReload, Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                var clips = JungleClips.Load(_setup.Codecs, volume);
                if (version == Volatile.Read(ref _jungleVersion))
                {
                    _jungle.SetClips(clips);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _log.LogWarning("the jungle clips couldn't be re-encoded: {Error}", exception.GetType().Name);
            }
        }));
    }

    private async Task TickJungleAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(JungleMatch.TickEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Jungle.Tick();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // One bad tick must not end the clock for the rest of the match.
                    _log.LogWarning("a jungle tick failed: {Error}", exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private static IPEndPoint ParseListen(string listen) =>
        IPEndPoint.TryParse(listen, out var endpoint) ? endpoint : throw new StartupException("config 'listen' is not an IP address and port");

    private static DeviceRegistry LoadDevices(BotSetup setup)
    {
        Directory.CreateDirectory(setup.DataFolder);
        var store = new DevicesStore(Path.Combine(setup.DataFolder, "devices.json"));
        var records = store.Load() switch
        {
            FileLoad<IReadOnlyList<DeviceRecord>>.Loaded loaded => loaded.Value,
            FileLoad<IReadOnlyList<DeviceRecord>>.Broken broken => throw new StartupException("devices.json is broken: " + string.Join("; ", broken.Errors)),
            _ => throw new StartupException("devices.json couldn't be read"),
        };
        return DeviceRegistry.Over(store, records, setup.Wall, TimeSpan.FromDays(setup.Config.DeviceTokenDays));
    }
}
