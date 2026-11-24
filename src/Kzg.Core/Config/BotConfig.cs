using System.Text.Json;
using System.Text.Json.Serialization;
using Kzg.Core.Abstractions;

namespace Kzg.Core.Config;

/// <summary>
/// What an admin sets for the bot, in <c>config.json</c> or through <c>/gvg</c> commands. Secrets are
/// kept apart in <c>secrets.json</c>: a bot names its token by <see cref="BotEntry.TokenRef"/>. A
/// setting left out of the file takes the default shown here; an ID that isn't set yet is null.
/// </summary>
public sealed record BotConfig
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Always written; a file from another schema is refused rather than guessed at.</summary>
    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The one Discord server; null until the bot settles on the server it was added to.</summary>
    [JsonConverter(typeof(SnowflakeConverter))]
    public ulong? GuildId { get; init; }

    /// <summary>A role whose members may run <c>/gvg</c> beside those with Manage Server; null for none.</summary>
    [JsonConverter(typeof(SnowflakeConverter))]
    public ulong? AdminRoleId { get; init; }

    /// <summary>The address players' apps connect to, as a connect code carries it; null until set.</summary>
    public string? PublicHost { get; init; }

    /// <summary>The text channel the primary bot posts problems to, such as a relay bot gone offline; null for none.</summary>
    public ChannelId? AlertsChannelId { get; init; }

    /// <summary>The address Kestrel listens on, behind the reverse proxy: an IP and a port.</summary>
    public string Listen { get; init; } = "127.0.0.1:7878";

    /// <summary>How far back a pressed key's relay starts, so the first word survives the press's trip.</summary>
    public int PrerollMs { get; init; } = 300;

    /// <summary>The longest a key hold relays before the bot ends it as stuck.</summary>
    public int MaxHoldMs { get; init; } = 120_000;

    /// <summary>How long an unused device token lasts.</summary>
    public int DeviceTokenDays { get; init; } = 90;

    /// <summary>
    /// How loud the jungle calls are, 1 to 100. 100 is the level jungle-bot used (gain 0.5), which was too
    /// loud next to voice chat in real games; the default is about 10 dB under it.
    /// </summary>
    public int JungleVolume { get; init; } = 32;

    // "oauth", not the naming policy's "oAuth".
    [JsonPropertyName("oauth")]
    public OAuthConfig OAuth { get; init; } = new();

    public GroupsConfig Groups { get; init; } = new();

    /// <summary>The relay bots, one per voice channel; exactly one is primary and runs the slash commands.</summary>
    public IReadOnlyList<BotEntry> Bots { get; init; } = [new BotEntry { TokenRef = "bot1", Primary = true }];

    public TimeSpan Preroll => TimeSpan.FromMilliseconds(PrerollMs);

    public TimeSpan MaxHold => TimeSpan.FromMilliseconds(MaxHoldMs);
}

/// <summary>
/// "Sign in with Discord". Off by default: it needs the Discord application's client ID here and its
/// client secret in <c>secrets.json</c> as <see cref="Secrets.OAuthClientSecretName"/>.
/// </summary>
public sealed record OAuthConfig
{
    public bool Enabled { get; init; }

    [JsonConverter(typeof(SnowflakeConverter))]
    public ulong? ClientId { get; init; }
}

public sealed record GroupsConfig
{
    public GroupConfig Attack { get; init; } = new();

    public GroupConfig Defense { get; init; } = new();

    public GroupConfig this[Group group] => group switch
    {
        Group.Attack => Attack,
        Group.Defense => Defense,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    public GroupsConfig With(Group group, GroupConfig config) => group switch
    {
        Group.Attack => this with { Attack = config },
        Group.Defense => this with { Defense = config },
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };
}

/// <summary>One big team: its bound small-team VCs.</summary>
public sealed record GroupConfig
{
    public IReadOnlyList<ChannelId> Channels { get; init; } = [];

    /// <summary>
    /// Shot callers are gone, but configs written before still name one here. It is read and dropped,
    /// whatever its value, so such a config loads; it is never written.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("shotCallerUserId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private JsonElement? RetiredShotCallerUserId
    {
        get => null;
        init { }
    }
}

/// <summary>One relay bot account.</summary>
public sealed record BotEntry
{
    /// <summary>The name its token has in <c>secrets.json</c>, never the token itself.</summary>
    public required string TokenRef { get; init; }

    /// <summary>The bot's own Discord user; null until it first signs in and the bot learns it.</summary>
    public UserId? BotUserId { get; init; }

    /// <summary>The voice channel it sits in; null while it is assigned none.</summary>
    public ChannelId? ChannelId { get; init; }

    /// <summary>Runs the slash commands and reads presence; exactly one bot is.</summary>
    public bool Primary { get; init; }
}
