using System.Text.Json.Serialization;

namespace Kzg.Protocol;

/// <summary>
/// One <c>/ws</c> message: a JSON object with <c>v</c> and <c>type</c> beside its own fields. The
/// hierarchy is closed: only this assembly can add a message, because each must say what makes it
/// invalid. <see cref="MessageCodec"/> reads and writes them.
/// </summary>
public abstract record KzgMessage
{
    private protected KzgMessage(string type)
    {
        Type = type;
    }

    [JsonPropertyName("v")]
    [JsonPropertyOrder(-2)]
    public int V => WireProtocol.Version;

    [JsonPropertyName("type")]
    [JsonPropertyOrder(-1)]
    public string Type { get; }

    /// <summary>Why the message breaks a rule JSON types can't express, or null when it is valid.</summary>
    internal abstract string? Problem();
}

/// <summary>A message the app sends the bot.</summary>
public abstract record ClientMessage : KzgMessage
{
    private protected ClientMessage(string type)
        : base(type)
    {
    }
}

/// <summary>A message the bot sends the app.</summary>
public abstract record ServerMessage : KzgMessage
{
    private protected ServerMessage(string type)
        : base(type)
    {
    }
}

/// <summary>
/// The app's first message: exactly one credential. <paramref name="Code"/> is the whole pasted
/// connect code; <paramref name="DeviceToken"/> and <paramref name="LoginId"/> are base64url secrets.
/// </summary>
public sealed record HelloMessage(
    string AppVersion,
    Guid InstallId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeviceToken = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LoginId = null)
    : ClientMessage(TypeName)
{
    public const string TypeName = "hello";

    /// <summary>Which credential is set, never its value.</summary>
    public override string ToString() =>
        $"hello {{ appVersion = {AppVersion}, installId = {InstallId}, credential = {Credential} }}";

    private string Credential => (Code, DeviceToken, LoginId) switch
    {
        (not null, null, null) => "code",
        (null, not null, null) => "deviceToken",
        (null, null, not null) => "loginId",
        _ => "invalid",
    };

    internal override string? Problem()
    {
        var credentials = (Code is null ? 0 : 1) + (DeviceToken is null ? 0 : 1) + (LoginId is null ? 0 : 1);
        if (credentials != 1)
        {
            return "hello needs exactly one of code, deviceToken and loginId";
        }
        if (Code is not null && !ConnectCode.TryParse(Code, out _))
        {
            return "hello.code is not a connect code";
        }
        if (DeviceToken is not null && !WireSecret.IsValid(DeviceToken))
        {
            return "hello.deviceToken is not a 32-byte base64url secret";
        }
        if (LoginId is not null && !WireSecret.IsValid(LoginId))
        {
            return "hello.loginId is not a 32-byte base64url secret";
        }
        if (!Text.IsShortLine(AppVersion, 64))
        {
            return "hello.appVersion must be 1 to 64 printable characters";
        }
        return InstallId == Guid.Empty ? "hello.installId must not be the empty GUID" : null;
    }
}

/// <summary>A broadcast key went down or up: <see cref="WireAction.Group"/> or <see cref="WireAction.AllTeams"/>.</summary>
public sealed record PressMessage(WireAction Action, bool Down, long Seq)
    : ClientMessage(TypeName)
{
    public const string TypeName = "press";

    internal override string? Problem() =>
        !WireProtocol.IsKey(Action) ? "press.action must be group or allTeams; open mic has its own message"
        : Seq < 0 ? "press.seq must not be negative"
        : null;
}

/// <summary>The player turned the open-mic latch on or off.</summary>
public sealed record OpenMicMessage(bool On, long Seq)
    : ClientMessage(TypeName)
{
    public const string TypeName = "openMic";

    internal override string? Problem() => Seq < 0 ? "openMic.seq must not be negative" : null;
}

/// <summary>
/// Sent every <see cref="WireProtocol.HeartbeatMs"/>. It restates the held keys and the latch, so a
/// lost key-up heals on the next one. <paramref name="Held"/> lists keys only, so never openMic.
/// </summary>
public sealed record HeartbeatMessage(long Seq, IReadOnlyList<WireAction> Held, bool OpenMic, long SentAtMs)
    : ClientMessage(TypeName)
{
    public const string TypeName = "heartbeat";

    internal override string? Problem() =>
        Seq < 0 ? "heartbeat.seq must not be negative"
        : SentAtMs < 0 ? "heartbeat.sentAtMs must not be negative"
        : Held.Any(a => !WireProtocol.IsKey(a)) ? "heartbeat.held lists keys only; open mic is its own field"
        : Held.Distinct().Count() != Held.Count ? "heartbeat.held lists an action twice"
        : null;
}

/// <summary>Revoke the device token this connection signed in with.</summary>
public sealed record UnlinkMessage() : ClientMessage(TypeName)
{
    public const string TypeName = "unlink";

    internal override string? Problem() => null;
}

/// <summary>The bot accepted the hello. <paramref name="DeviceToken"/> is set only when this hello issued a new one.</summary>
public sealed record WelcomeMessage(
    string UserId,
    string DisplayName,
    int HeartbeatMs,
    int TimeoutMs,
    string ServerVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeviceToken = null)
    : ServerMessage(TypeName)
{
    public const string TypeName = "welcome";

    /// <summary>Never the device token, so a logged welcome reveals nothing.</summary>
    public override string ToString() =>
        $"welcome {{ userId = {UserId}, newDeviceToken = {DeviceToken is not null} }}";

    internal override string? Problem() =>
        !Snowflake.IsValid(UserId) ? "welcome.userId is not a snowflake"
        : !Text.IsShortLine(DisplayName, 100) ? "welcome.displayName must be 1 to 100 printable characters"
        : HeartbeatMs <= 0 ? "welcome.heartbeatMs must be positive"
        : TimeoutMs <= HeartbeatMs ? "welcome.timeoutMs must be longer than heartbeatMs"
        : !Text.IsShortLine(ServerVersion, 64) ? "welcome.serverVersion must be 1 to 64 printable characters"
        : DeviceToken is not null && !WireSecret.IsValid(DeviceToken) ? "welcome.deviceToken is not a 32-byte base64url secret"
        : null;
}

/// <summary>
/// What the bot sees of this player, sent on change. <paramref name="ChannelName"/> is absent outside
/// voice; <paramref name="Group"/> is absent outside a team VC. <paramref name="OpenMic"/> is the latch
/// as requested; <paramref name="Active"/> holds openMic only while it actually relays.
/// <paramref name="Targets"/> are the names of the VCs reached, for the app to show.
/// <paramref name="BotDownIn"/> names the VCs of the player's group whose relay bot isn't ready, so the app can say
/// which ones can't hear a broadcast; empty when none, and absent from an older bot (the <c>botDown</c> warning still says it).
/// </summary>
public sealed record StatusMessage(
    IReadOnlyList<WireAction> Active,
    bool OpenMic,
    IReadOnlyList<string> Targets,
    IReadOnlyList<StatusWarning> Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChannelName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WireGroup? Group = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? BotDownIn = null)
    : ServerMessage(TypeName)
{
    public const string TypeName = "status";

    internal override string? Problem() =>
        ChannelName is not null && !Text.IsShortLine(ChannelName, 100) ? "status.channelName must be 1 to 100 printable characters"
        : Group is not null && ChannelName is null ? "status.group needs a channelName"
        : Active.Distinct().Count() != Active.Count ? "status.active lists an action twice"
        : Targets.Any(t => !Text.IsShortLine(t, 100)) ? "status.targets must be names of 1 to 100 printable characters"
        : Warnings.Distinct().Count() != Warnings.Count ? "status.warnings lists a warning twice"
        : BotDownIn is not null && BotDownIn.Any(name => !Text.IsShortLine(name, 100)) ? "status.botDownIn must be names of 1 to 100 printable characters"
        : BotDownIn is not null && BotDownIn.Distinct().Count() != BotDownIn.Count ? "status.botDownIn lists a name twice"
        : null;
}

/// <summary>Echoes a heartbeat, so the app can show its round-trip time.</summary>
public sealed record HeartbeatAckMessage(long Seq, long SentAtMs)
    : ServerMessage(TypeName)
{
    public const string TypeName = "heartbeatAck";

    internal override string? Problem() =>
        Seq < 0 ? "heartbeatAck.seq must not be negative"
        : SentAtMs < 0 ? "heartbeatAck.sentAtMs must not be negative"
        : null;
}

/// <summary>The bot refused the app. <paramref name="Message"/> is for a person; act on the code.</summary>
public sealed record ErrorMessage(ErrorCode Code, string Message)
    : ServerMessage(TypeName)
{
    public const string TypeName = "error";

    internal override string? Problem() =>
        Message.Length > 500 ? "error.message must be at most 500 characters" : null;
}

internal static class Text
{
    /// <summary>Non-empty, at most <paramref name="max"/> UTF-16 units, no control characters.</summary>
    public static bool IsShortLine(string? text, int max) =>
        !string.IsNullOrEmpty(text) && text.Length <= max && !text.Any(char.IsControl);
}
