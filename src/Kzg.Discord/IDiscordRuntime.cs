using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Pairing;
using Kzg.Discord.Commands;
using Kzg.Discord.Presence;
using Microsoft.Extensions.Logging;

namespace Kzg.Discord;

/// <summary>
/// Everything the host needs from Discord: the engine's gateway, who may sign in, channel names, and the
/// bots' lifecycle. <see cref="DiscordGateway"/> is the real one; tests of the host use a stand-in.
/// </summary>
public interface IDiscordRuntime : IVoiceGateway, IAsyncDisposable
{
    ChannelNames Names { get; }

    IDiscordIdentity Identity { get; }

    /// <summary>Whether the relay bots are meant to be in their channels.</summary>
    bool Running { get; }

    IReadOnlyList<RelayBotState> Bots { get; }

    /// <summary>The config named no server and the bot found the only one it is in. The host saves it.</summary>
    event Action<ulong>? GuildResolved;

    /// <summary>A relay bot signed in and its user ID is now known: (tokenRef, id). The host saves it.</summary>
    event Action<string, ulong>? BotIdLearned;

    /// <summary>Signs the primary bot in and learns the server; throws <see cref="DiscordStartException"/> when it can't.</summary>
    Task StartAsync(ILoggerFactory loggers, CancellationToken cancellationToken);

    /// <summary>From now on slash commands are answered by <paramref name="control"/>.</summary>
    void AttachControl(IGvgControl control);

    /// <summary>Puts the relay bots where the config says; with <paramref name="running"/> false, nowhere.</summary>
    Task ApplyAsync(BotConfig config, bool running, CancellationToken cancellationToken);

    /// <summary>
    /// The primary bot posts <paramref name="text"/> to a text channel, mentioning no one; false when Discord
    /// refused or couldn't be reached, as when the bot may not post there. It never throws for that.
    /// </summary>
    Task<bool> PostAsync(ChannelId channel, string text, CancellationToken cancellationToken);
}
