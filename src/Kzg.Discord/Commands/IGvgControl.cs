using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Jungle;

namespace Kzg.Discord.Commands;

/// <summary>What an edit of the config decided: the new config and the words for the admin, or a refusal.</summary>
public abstract record ConfigEdit
{
    private ConfigEdit()
    {
    }

    /// <summary>The config to save and apply, and what to tell the admin once it is.</summary>
    public sealed record Change(BotConfig Config, string Message) : ConfigEdit;

    /// <summary>Nothing changes; the reason is what the admin reads.</summary>
    public sealed record Refuse(string Reason) : ConfigEdit;
}

/// <summary>What became of an edit: done, or why not. Either way the message is safe to show.</summary>
public sealed record EditOutcome(bool Done, string Message);

public enum BotState
{
    /// <summary>In its voice channel and able to send.</summary>
    Ready,

    /// <summary>Assigned a channel but not in it yet, or dropped and rejoining.</summary>
    Joining,

    /// <summary>Assigned no channel, or the relay is stopped.</summary>
    Idle,
}

/// <param name="Bot">The relay bot's name, such as "A1 Broadcaster"; null when the channel has none.</param>
/// <param name="NicknameRefused">Discord refused the bot's nickname: it relays, but shows under its account name.</param>
public sealed record ChannelStatus(ChannelId Channel, string Name, string? Bot, BotState State, bool NicknameRefused = false);

public sealed record GroupStatus(Group Group, IReadOnlyList<ChannelStatus> Channels);

/// <param name="Name">How replies name it: its team name, or its tokenRef while it has no team channel.</param>
public sealed record BotStatus(string TokenRef, string Name, string? ChannelName, BotState State);

/// <summary>What <c>/gvg status</c> shows.</summary>
public sealed record GvgStatus(
    bool Running,
    IReadOnlyList<GroupStatus> Groups,
    IReadOnlyList<BotStatus> Bots,
    int AppsConnected,
    int Devices,
    long FramesSent,
    long PacketsDropped);

/// <summary>
/// What the slash commands need from the bot around them. The host implements it over the config store, the
/// engine, the control server and the Discord gateway; tests implement it with a table. Every change goes
/// through <see cref="EditAsync"/>, so none can save a config that breaks a rule.
/// </summary>
public interface IGvgControl
{
    /// <summary>The config in force.</summary>
    BotConfig Config { get; }

    /// <summary>Whether the relay bots are in their channels; <c>/gvg stop</c> takes them out.</summary>
    bool Running { get; }

    /// <summary>
    /// Runs the edit on the config in force, one at a time. A change is validated, saved, applied to routing
    /// and to which bots sit where, and only then reported done.
    /// </summary>
    Task<EditOutcome> EditAsync(Func<BotConfig, ConfigEdit> edit, CancellationToken cancellationToken);

    Task SetRunningAsync(bool running, CancellationToken cancellationToken);

    GvgStatus Status();

    /// <summary>A channel's name as Discord shows it.</summary>
    string ChannelName(ChannelId channel);

    /// <summary>A connect code for the user, good once for 10 minutes. It is a secret: never log it.</summary>
    string IssueConnectCode(UserId user, string host);

    /// <summary>Revokes the user's device tokens and closes their live app sessions; how many tokens were revoked.</summary>
    int Revoke(UserId user);

    /// <summary>Runs a jungle-clock command for the caller and returns the reply text.</summary>
    string Jungle(UserId caller, JungleCommand command);

    /// <summary>Masks anything secret in text bound for Discord.</summary>
    string Redact(string text);

    /// <summary>Posts to a text channel as the primary bot; false when it couldn't, such as for a missing permission.</summary>
    Task<bool> PostAsync(ChannelId channel, string text, CancellationToken cancellationToken);
}
