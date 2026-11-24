using System.Net;
using System.Runtime.InteropServices;
using Kzg.Audio;
using Kzg.Bot.Discord;
using Kzg.Bot.Fake;
using Kzg.Bot.Logging;
using Kzg.Bot.Setup;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Discord;
using Kzg.Discord.Setup;
using Microsoft.Extensions.Logging;

namespace Kzg.Bot;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        using var file = OpenLogFile(args);
        if (file is null)
        {
            return await RunAsync(args, Console.Out, Console.Error, stop.Token).ConfigureAwait(false);
        }
        var log = TextWriter.Synchronized(file);
        // Whatever else writes to the console, and a crash's last words, land in the same bounded file.
        Console.SetOut(log);
        Console.SetError(log);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.WriteLine("the bot crashed: " + new Redactor([]).Redact(e.ExceptionObject.ToString()));
            log.Flush();
        };
        return await RunAsync(args, log, log, stop.Token).ConfigureAwait(false);
    }

    /// <summary>The <c>--log-file</c> writer, or null for the console: no option, a bad command line (said on the console), or a file that can't be opened.</summary>
    public static RotatingLogWriter? OpenLogFile(string[] args, TextWriter? console = null)
    {
        string? path;
        try
        {
            path = LaunchOptions.Parse(args).LogFile;
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (path is null)
        {
            return null;
        }
        try
        {
            return new RotatingLogWriter(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            (console ?? Console.Error).WriteLine($"the log file '{path}' couldn't be opened ({exception.GetType().Name}); logging to the console");
            return null;
        }
    }

    /// <summary>Builds the Discord side from the config and secrets; tests replace it so no run needs Discord.</summary>
    public delegate IDiscordRuntime DiscordFactory(BotConfig config, Secrets secrets, ITimeSource time);

    /// <summary>The environment variable the Docker bundle sets to the players' address; the setup page offers it.</summary>
    public const string PublicHostVariable = "KZG_PUBLIC_HOST";

    /// <summary>How often the relay bots are looked at for the alerts channel.</summary>
    public static readonly TimeSpan AlertEvery = TimeSpan.FromSeconds(5);

    /// <summary>Runs the bot until <paramref name="stop"/> is cancelled; the exit code. <paramref name="started"/> is told once it is fully up: listening, and in Discord.</summary>
    /// <param name="discord">Makes the Discord side for a real run; the default signs the bots in to Discord.</param>
    /// <param name="setup">What the setup page talks to, for tests; the default asks Discord.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop, Action<BotApp>? started = null, DiscordFactory? discord = null, SetupHooks? setup = null)
    {
        LaunchOptions options;
        try
        {
            options = LaunchOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            await error.WriteLineAsync(LaunchOptions.Usage).ConfigureAwait(false);
            return 64;
        }
        if (options.Help)
        {
            await output.WriteLineAsync(LaunchOptions.Usage).ConfigureAwait(false);
            return 0;
        }
        try
        {
            return options.Fake
                ? await RunFakeAsync(options, output, stop, started).ConfigureAwait(false)
                : await RunRealAsync(options, output, stop, started, discord ?? RealDiscord, setup ?? new SetupHooks()).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is StartupException or DiscordStartException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
    }

    /// <summary>
    /// The offline bot: the real host, engine and control server over a made-up guild. It exists for the
    /// app's developers and for tests; the fake voice links hear nothing and are fed by whoever drives them.
    /// </summary>
    private static async Task<int> RunFakeAsync(LaunchOptions options, TextWriter output, CancellationToken stop, Action<BotApp>? started)
    {
        var time = new SystemTimeSource();
        var temp = options.DataDir is null ? Directory.CreateTempSubdirectory("kzg-fake-") : null;
        var world = DemoWorld.Create(time, "localhost");
        try
        {
            var bot = BotApp.Create(new BotSetup
            {
                Config = world.Config,
                Secrets = Secrets.Empty,
                DataFolder = options.DataDir ?? temp!.FullName,
                Gateway = world.Gateway,
                Discord = world.Identity,
                Codecs = new ConcentusOpusCodecFactory(),
                ChannelNames = world.Names,
                Time = time,
                Listen = options.Listen ?? new IPEndPoint(IPAddress.Loopback, 7878),
                Log = output,
            });
            await using (bot.ConfigureAwait(false))
            {
                await bot.StartAsync(stop).ConfigureAwait(false);
                world.SeatPlayers();
                await Describe(output, bot).ConfigureAwait(false);
                started?.Invoke(bot);
                await bot.WaitForShutdownAsync(stop).ConfigureAwait(false);
                await bot.StopAsync().ConfigureAwait(false);
            }
            return 0;
        }
        finally
        {
            temp?.Delete(recursive: true);
        }
    }

    private static IDiscordRuntime RealDiscord(BotConfig config, Secrets secrets, ITimeSource time) => new DiscordGateway(config, secrets, time);

    /// <summary>
    /// The real bot: the config and secrets in the data folder, every relay bot signed in to Discord, the
    /// slash commands on the primary one. Every bot is signed out again when it stops.
    /// </summary>
    private static async Task<int> RunRealAsync(LaunchOptions options, TextWriter output, CancellationToken stop, Action<BotApp>? started, DiscordFactory makeDiscord, SetupHooks hooks)
    {
        var paths = DataPaths.Resolve(options.DataDir);
        using var awake = hooks.KeepAwake ? KeepAwake.Start(output) : null;
        if ((options.Setup || !File.Exists(paths.ConfigFile))
            && !await RunSetupAsync(paths, options, output, hooks, stop).ConfigureAwait(false))
        {
            return 0;
        }
        var secrets = new SecretsStore(paths.SecretsFile).Load(Environment.GetEnvironmentVariables()) switch
        {
            FileLoad<Secrets>.Loaded loaded => loaded.Value,
            FileLoad<Secrets>.Broken broken => throw new StartupException("secrets.json is broken: " + string.Join("; ", broken.Errors)),
            _ => throw new StartupException($"secrets.json couldn't be read in '{paths.Folder}'"),
        };
        var store = new ConfigStore(paths.ConfigFile);
        var config = store.Load(secrets) switch
        {
            FileLoad<BotConfig>.Loaded loaded => loaded.Value,
            FileLoad<BotConfig>.Missing => throw new StartupException($"no config.json in '{paths.Folder}'"),
            FileLoad<BotConfig>.Broken broken => throw new StartupException("config.json is broken: " + string.Join("; ", broken.Errors)
                + (broken.Errors.Any(e => e.Setting?.EndsWith(".tokenRef", StringComparison.Ordinal) == true) ? ". Run the bot with --setup to paste the bot tokens again" : "")),
            _ => throw new StartupException($"config.json couldn't be read in '{paths.Folder}'"),
        };
        var time = new SystemTimeSource();
        var discord = makeDiscord(config, secrets, time);
        try
        {
            var bot = BotApp.Create(new BotSetup
            {
                Config = config,
                Secrets = secrets,
                DataFolder = paths.Folder,
                Gateway = discord,
                Discord = discord.Identity,
                Codecs = new ConcentusOpusCodecFactory(),
                ChannelNames = new DiscordChannelNames(discord.Names),
                Time = time,
                Listen = options.Listen,
                Log = output,
            });
            await using (bot.ConfigureAwait(false))
            {
                var host = new GvgHost(bot, store, secrets, discord, Redactor.For(secrets));
                await bot.StartAsync(stop).ConfigureAwait(false);
                await discord.StartAsync(bot.Loggers, stop).ConfigureAwait(false);
                await discord.ApplyAsync(bot.Config, running: true, stop).ConfigureAwait(false);
                discord.AttachControl(host);
                await host.GoLiveAsync(stop).ConfigureAwait(false);
                var marker = new RunMarker(paths.Folder);
                if (marker.Begin())
                {
                    await host.AlertAsync("The Kzg bot started again after it stopped unexpectedly, such as by a crash or a power cut. It is running now.", stop).ConfigureAwait(false);
                }
                using var watch = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var watching = host.WatchAsync(new AlertMonitor(time), hooks.AlertEvery ?? AlertEvery, watch.Token);
                started?.Invoke(bot);
                await bot.WaitForShutdownAsync(stop).ConfigureAwait(false);
                await watch.CancelAsync().ConfigureAwait(false);
                await watching.ConfigureAwait(false);
                await bot.StopAsync().ConfigureAwait(false);
                marker.End();
            }
            return 0;
        }
        finally
        {
            // Every bot leaves voice and signs out, whether the run ended well or not.
            await discord.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Serves the setup page until it is done (true) or the bot is stopped (false). It keeps the settings of
    /// an earlier config, read without its tokens, so a second setup only replaces the bots.
    /// </summary>
    private static async Task<bool> RunSetupAsync(DataPaths paths, LaunchOptions options, TextWriter output, SetupHooks hooks, CancellationToken stop)
    {
        var environment = hooks.Environment ?? Environment.GetEnvironmentVariables();
        BotConfig? existing = null;
        if (File.Exists(paths.ConfigFile))
        {
            existing = new ConfigStore(paths.ConfigFile).Load(Secrets.Empty, checkSecrets: false) switch
            {
                FileLoad<BotConfig>.Loaded loaded => loaded.Value,
                FileLoad<BotConfig>.Broken broken => throw new StartupException(
                    "config.json is broken, so setup can't keep its settings: " + string.Join("; ", broken.Errors) + ". Fix it, or move it out of the data folder to start afresh"),
                _ => throw new StartupException($"config.json couldn't be read in '{paths.Folder}'"),
            };
        }
        var publicHost = existing?.PublicHost
            ?? (environment[PublicHostVariable] is string { Length: > 0 } host && Protocol.ConnectCode.IsValidHost(host.Trim()) ? host.Trim() : null);
        var listen = options.Listen ?? (IPEndPoint.TryParse(existing?.Listen ?? new BotConfig().Listen, out var configured)
            ? configured : throw new StartupException("config 'listen' is not an IP address and port"));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 256 * 1024 };
        SetupServer? server = null;
        var wizard = new SetupWizard(hooks.Accounts ?? new DiscordBotAccounts(http), paths, existing, publicHost, new SystemTimeSource(),
            key => PrintSetupLinks(output, server!.Address.Port, listen, publicHost, key), environment);
        server = SetupServer.Create(wizard, listen, publicHost, output, Redactor.For(Secrets.Empty));
        await using (server.ConfigureAwait(false))
        {
            try
            {
                await server.StartAsync(stop).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new StartupException($"the setup page couldn't listen on {listen}: {exception.Message}");
            }
            wizard.IssueKey();
            hooks.Started?.Invoke(server);
            try
            {
                await wizard.Completed.WaitAsync(stop).ConfigureAwait(false);
                await output.WriteLineAsync("setup is done; starting the bot").ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                await server.StopAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>The setup links, on the console only: the key in them opens the page that takes the bot tokens.</summary>
    private static void PrintSetupLinks(TextWriter output, int port, IPEndPoint listen, string? publicHost, string key)
    {
        var local = listen.Address.Equals(IPAddress.IPv6Loopback) ? "[::1]" : "localhost";
        output.WriteLine();
        output.WriteLine("== Kzg bot setup ==");
        output.WriteLine("Open this link in a web browser on this computer to set the bot up:");
        output.WriteLine($"  http://{local}:{port}/setup?key={key}");
        if (publicHost is not null)
        {
            output.WriteLine("Or from any computer, once your HTTPS address reaches this bot:");
            output.WriteLine($"  https://{publicHost}/setup?key={key}");
        }
        output.WriteLine("The link opens the page once. Keep it to yourself: it lets someone set up your bot.");
        output.WriteLine();
        output.Flush();
    }

    private static async Task Describe(TextWriter output, BotApp bot)
    {
        var host = $"127.0.0.1:{bot.Address.Port}";
        await output.WriteLineAsync($"offline bot: {bot.Address} (/ws, /auth/start, /auth/callback, /healthz); no TLS, so an app needs a ws:// override").ConfigureAwait(false);
        await output.WriteLineAsync("demo connect codes, good once for 10 minutes each (printed here only, never logged):").ConfigureAwait(false);
        foreach (var user in new[] { DemoWorld.Alice, DemoWorld.Bram, DemoWorld.Chen, DemoWorld.Dana })
        {
            await output.WriteLineAsync($"  {DemoWorld.Members[user]}: {bot.Codes.Issue(user, host)}").ConfigureAwait(false);
        }
    }
}

/// <summary>What a real run's setup page and watchers use; tests replace the parts that reach Discord or the Mac.</summary>
public sealed record SetupHooks
{
    /// <summary>Checks the pasted tokens; null asks Discord.</summary>
    public IBotAccounts? Accounts { get; init; }

    /// <summary>Told once the setup page listens, with the server (its wizard holds the key).</summary>
    public Action<SetupServer>? Started { get; init; }

    /// <summary>Where the setup page reads <c>KZG_PUBLIC_HOST</c> and <c>KZG_SECRET_*</c>; null for the process environment.</summary>
    public System.Collections.IDictionary? Environment { get; init; }

    /// <summary>Keeps a Mac awake while the bot runs.</summary>
    public bool KeepAwake { get; init; } = true;

    /// <summary>How often the alerts watcher looks; null for <see cref="Program.AlertEvery"/>.</summary>
    public TimeSpan? AlertEvery { get; init; }
}
