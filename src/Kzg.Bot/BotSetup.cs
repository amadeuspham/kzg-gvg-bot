using System.Net;
using Kzg.Bot.Control;
using Kzg.Bot.Engine;
using Kzg.Bot.Http;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Pairing;
using Microsoft.Extensions.Logging;

namespace Kzg.Bot;

/// <summary>
/// Everything the host is made of, so a real bot, the offline fake and a test differ only in what they
/// pass here. The Discord adapter supplies <see cref="Gateway"/> and <see cref="Discord"/>.
/// </summary>
public sealed record BotSetup
{
    public required BotConfig Config { get; init; }

    public required Secrets Secrets { get; init; }

    /// <summary>The folder <c>devices.json</c> lives in.</summary>
    public required string DataFolder { get; init; }

    public required IVoiceGateway Gateway { get; init; }

    public required IDiscordIdentity Discord { get; init; }

    public required IOpusCodecFactory Codecs { get; init; }

    public required IChannelNames ChannelNames { get; init; }

    public ITimeSource Time { get; init; } = new SystemTimeSource();

    /// <summary>Wall-clock time, for device tokens' lifetimes.</summary>
    public TimeProvider Wall { get; init; } = TimeProvider.System;

    /// <summary>Where Kestrel listens, instead of the config's <c>listen</c>; port 0 takes any free port.</summary>
    public IPEndPoint? Listen { get; init; }

    public TextWriter Log { get; init; } = Console.Out;

    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    /// <summary>Keeps ASP.NET's own log lines to warnings and up; tests turn it off to check that what Kestrel logs is redacted too.</summary>
    public bool QuietFramework { get; init; } = true;

    public EngineOptions Engine { get; init; } = new();

    public ControlOptions Control { get; init; } = new();

    public HttpLimits Limits { get; init; } = new();
}

/// <summary>The bot couldn't start for a reason an admin can fix; the message is safe to print.</summary>
public sealed class StartupException(string message) : Exception(message);
