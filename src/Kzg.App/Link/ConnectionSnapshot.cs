using Kzg.Protocol;

namespace Kzg.App.Link;

/// <summary>Where the link to the bot stands.</summary>
public enum LinkPhase
{
    /// <summary>No credential: the player has not linked this PC, or unlinked it.</summary>
    NotLinked,

    /// <summary>Connecting and waiting for <c>welcome</c>.</summary>
    Connecting,

    /// <summary>Connected with a login id, waiting for the player to finish signing in in the browser.</summary>
    SigningIn,

    Online,

    /// <summary>Dropped or unreachable; trying again after a pause.</summary>
    Offline,

    /// <summary>Refused for a reason retrying can't fix. Nothing more is tried.</summary>
    Failed,
}

/// <summary>Why the link isn't up, when it isn't, in the app's own terms.</summary>
public enum LinkFailure
{
    None,
    NotLinked,
    ServerUnreachable,
    BadCode,
    CodeUsed,
    CodeExpired,

    /// <summary>The device token ran out after 90 idle days.</summary>
    TokenExpired,

    /// <summary>An admin revoked the device token, or the player unlinked from another PC.</summary>
    Revoked,
    NotMember,
    VersionMismatch,
    SignInTimedOut,

    /// <summary>The connection dropped after the one-use code was sent, so the player must ask for a new one.</summary>
    Interrupted,

    /// <summary>The same player signed in from another app; the bot closed this one and it must not take the link back by itself.</summary>
    ReplacedByNewerApp,

    /// <summary>The bot said something the protocol doesn't allow.</summary>
    ProtocolError,
}

/// <summary>What the status badge shows. Each has its own text and icon shape as well as a color.</summary>
public enum BadgeState
{
    Connected,
    Reconnecting,
    Disconnected,
    Broadcasting,
}

/// <summary>A one-off message about something the app did on its own.</summary>
public enum LinkNotice
{
    None,

    /// <summary>The connection dropped, so the open-mic latch was turned off.</summary>
    OpenMicOffAfterDrop,
}

/// <summary>Everything the window shows about the connection, as one immutable value.</summary>
public sealed record ConnectionSnapshot
{
    public static ConnectionSnapshot NotLinked { get; } = new();

    public LinkPhase Phase { get; init; } = LinkPhase.NotLinked;

    public LinkFailure Failure { get; init; } = LinkFailure.NotLinked;

    public BadgeState Badge { get; init; } = BadgeState.Disconnected;

    public LinkNotice Notice { get; init; }

    public string? DisplayName { get; init; }

    public string? ChannelName { get; init; }

    public WireGroup? Group { get; init; }

    /// <summary>The names of the VCs a broadcast reaches.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    public IReadOnlyList<StatusWarning> Warnings { get; init; } = [];

    /// <summary>The names of the VCs whose relay bot is offline; empty when none, or when the bot doesn't say.</summary>
    public IReadOnlyList<string> BotDownIn { get; init; } = [];

    /// <summary>The team key is down on this PC.</summary>
    public bool GroupHeld { get; init; }

    /// <summary>The all-teams key is down on this PC.</summary>
    public bool AllTeamsHeld { get; init; }

    /// <summary>The open-mic latch this app has asked for.</summary>
    public bool OpenMic { get; init; }

    /// <summary>The bot is relaying this player's open mic.</summary>
    public bool OpenMicActive { get; init; }

    public int? RoundTripMs { get; init; }

    /// <summary>When the connection was last up (or the app started trying), on the app's clock; null while online.</summary>
    public TimeSpan? OfflineSince { get; init; }

    /// <summary>When the next attempt starts, on the app's clock, while waiting between attempts; otherwise null.</summary>
    public TimeSpan? NextAttemptAt { get; init; }

    public bool IsOnline => Phase == LinkPhase.Online;

    // Lists compare by value, so an unchanged status doesn't wake the window.
    public bool Equals(ConnectionSnapshot? other) =>
        other is not null && Phase == other.Phase && Failure == other.Failure && Badge == other.Badge && Notice == other.Notice
        && DisplayName == other.DisplayName && ChannelName == other.ChannelName && Group == other.Group
        && Targets.SequenceEqual(other.Targets) && Warnings.SequenceEqual(other.Warnings) && BotDownIn.SequenceEqual(other.BotDownIn) && GroupHeld == other.GroupHeld && AllTeamsHeld == other.AllTeamsHeld
        && OpenMic == other.OpenMic && OpenMicActive == other.OpenMicActive && RoundTripMs == other.RoundTripMs
        && OfflineSince == other.OfflineSince && NextAttemptAt == other.NextAttemptAt;

    public override int GetHashCode() => HashCode.Combine(Phase, Failure, Badge, DisplayName, ChannelName, GroupHeld, OpenMic, RoundTripMs);
}
